from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_streaming_text_path_uses_v2_before_ai_generation():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = runtime[
        runtime.index("internal static async Task<bool> TryHandleAsync"):
        runtime.index("internal static bool IsSelectedKnowledgeCurrent")
    ]
    entry = pipeline[pipeline.index("private static async Task ProcessTextBurstStreamingAsync"):]

    assert "KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)" in entry
    assert entry.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)") < entry.index(
        'lease.MarkGenerating("streaming_answer_started")'
    ) < entry.index("StreamingBuyerAnswerService.GetAnswerAsync(")
    assert "KnowledgeEngineV2Service.Resolve(" in method
    assert "KnowledgeV2RuntimeResult.Continue(contextPromptAddon)" in method
    assert "KnowledgeV2RuntimeResult.HandledResult()" in method
    assert "KnowledgeContextualReplyService" not in pipeline


def test_contextual_ai_prompt_uses_v2_evidence_and_conversation_state_only():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "ConversationStateService.BuildPromptAddon(plan.ConversationState)" in router
    assert "knowledgeV2Result.ContextPromptAddon" in pipeline
    assert "CanUseOfflineKnowledgeFallback" in router
    fallback = router.split("public static bool CanUseOfflineKnowledgeFallback", 1)[1].split(
        "public static string BuildPromptAddon", 1
    )[0]
    assert "return false;" in fallback
    assert "KnowledgeLearningService.TryFindLocalAnswer" not in router
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeContextualReplyService.cs").exists()


def test_contextual_or_cancelled_drafts_do_not_enter_legacy_learning_queue():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    my_openai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "KnowledgeLearningService.QueueLearn" not in pipeline
    assert "KnowledgeLearningService.QueueLearn" not in my_openai
    assert "if (!lease.IsCurrent)" in pipeline
    assert "QueueManualAnswerComparison" in learning
    assert "buyerMessageId" in learning and "humanMessageId" in learning
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in learning
