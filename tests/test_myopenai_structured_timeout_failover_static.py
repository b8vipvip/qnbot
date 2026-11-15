from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_structured_chat_endpoint_timeout_fails_over_without_swallowing_caller_cancellation():
    source = read("src/Bot/ChromeNs/MyOpenAI.cs")
    method = source[
        source.index("public static StructuredChatResult CallStructuredChat("):
        source.index("private static StructuredChatResult CallRawChatCompletions")
    ]

    assert "foreach (var endpoint in endpoints)" in method
    assert "catch (OperationCanceledException ex)" in method
    assert "cancellationToken.IsCancellationRequested" in method
    assert "if (cancellationToken.IsCancellationRequested) throw;" in method
    assert 'Error = "AI接口请求超时（HTTP deadline取消）：" + SafeError(ex.Message)' in method
    assert "BotRuntimeStats.RecordAiCall(endpoint" in method
    assert "errors.Add(err);" in method
    assert "if (result.Success) return result;" in method


def test_session_learning_timeout_retry_can_reach_secondary_configured_ai_endpoint():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]
    my = read("src/Bot/ChromeNs/MyOpenAI.cs")

    assert "KnowledgeCorpusTimeoutSeconds" in batch
    assert "catch (OperationCanceledException ex)" in batch
    assert "foreach (var endpoint in endpoints)" in my
    assert "if (cancellationToken.IsCancellationRequested) throw;" in my
