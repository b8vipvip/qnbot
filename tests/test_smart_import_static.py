from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT / "src/Bot/Knowledge/KnowledgeAiService.cs").read_text(encoding="utf-8")
V2 = (ROOT / "src/Bot/Knowledge/KnowledgeV2SmartImportService.cs").read_text(encoding="utf-8")
UI = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs").read_text(encoding="utf-8")
OPENAI = (ROOT / "src/Bot/ChromeNs/MyOpenAI.cs").read_text(encoding="utf-8")
OPTS = (ROOT / "src/Bot/Options/CtlRobotOptions.xaml.cs").read_text(encoding="utf-8")
WF = (ROOT / ".github/workflows/windows-build.yml").read_text(encoding="utf-8")


def test_smart_import_timeout_config_defaults_and_v2_ui():
    assert "SmartImportTimeoutSeconds" in OPTS
    assert "GetSmartImportTimeoutSeconds" in OPTS
    assert "SaveSmartImportTimeoutSeconds" in OPTS
    assert "ClampTimeout" in SERVICE
    assert "AI分析超时：" in UI
    assert "timeoutSeconds" in V2


def test_structured_chat_has_request_level_timeout_and_token():
    assert "CallStructuredChat(messages, maxTokens, temperature, 0, CancellationToken.None)" in OPENAI
    assert "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)" in OPENAI
    assert "deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout))" in OPENAI
    assert "HttpCompletionOption.ResponseHeadersRead" in OPENAI
    assert "ReadResponseBodyWithCancellationAsync" in OPENAI
    assert "http.Timeout = Timeout.InfiniteTimeSpan" in OPENAI


def test_batching_rules_are_encoded_and_reused_by_v2_import():
    assert "TargetMinChars = 3000" in SERVICE
    assert "TargetMaxChars = 5000" in SERVICE
    assert "MaxBatchChars = 6000" in SERVICE
    assert "SplitLongParagraph" in SERVICE
    assert "KnowledgeAiService.SplitTextBatches" in V2


def test_incremental_idempotent_save_and_hashes_use_v2_repository_only():
    assert "KnowledgeAiService.ContentHash" in V2
    assert "var existing = KnowledgeEngineV2Repository.LoadAll(seller)" in V2
    assert "var seen = new HashSet<string>" in V2
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in V2
    assert "KnowledgeEngineV2Service.Warm(seller)" in V2
    assert "BotFeatureStore.SaveKnowledgeBase" not in V2
    assert "SaveDeduped(parsed.Items)" not in V2


def test_error_classification_and_cancellation_sources_are_owned_by_v2_import_services():
    service = (ROOT / "src/Bot/Knowledge/KnowledgeV2SmartImportService.cs").read_text(encoding="utf-8")
    ai = (ROOT / "src/Bot/Knowledge/KnowledgeAiService.cs").read_text(encoding="utf-8")
    ui = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs").read_text(encoding="utf-8")
    for term in ["UserCancel", "Timeout", "WindowClosed", "ReplacedByNewTask"]:
        assert term in service and term in ui
    assert "OperationCanceledException" in service
    assert "SmartImportException" in service
    assert "HttpRequestException" in service or "HttpRequestException" in ai
    assert "智能导入超时" in service
    assert "V2 JSON解析失败" in service

def test_json_fence_and_truncated_json_handling_are_shared_by_v2_ai_import():
    ai = (ROOT / "src/Bot/Knowledge/KnowledgeAiService.cs").read_text(encoding="utf-8")
    smart = (ROOT / "src/Bot/Knowledge/KnowledgeV2SmartImportService.cs").read_text(encoding="utf-8")

    assert "StripFence" in ai
    assert "StripFence" in smart or "KnowledgeAiService.ParseAiKnowledgeResult" in smart
    assert "可能是响应被截断" in ai or "可能是响应被截断" in smart

def test_progress_fields_are_reported_from_v2_import_batches():
    for term in [
        "正在分析第 {0}/{1} 批",
        "当前批次字符数",
        "已写入 Knowledge V2",
        "已跳过重复",
        "当前耗时",
        "当前接口",
    ]:
        assert term in V2


def test_windows_release_artifact_excludes_params_db():
    assert "runs-on: windows-2022" in WF
    assert "/p:Configuration=Release" in WF and "/p:Platform=x64" in WF
    assert "Where-Object { $_.Name -ne 'params.db' }" in WF
    assert "package" in WF and "Bot.exe" in WF and "README-PARAMS.txt" in WF
