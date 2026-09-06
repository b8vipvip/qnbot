from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_human_edit_is_authoritative_provenance_not_ai_classification():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    ui = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    assert "internal static class KnowledgeV2AuthorityPolicy" in bridge
    assert 'source.IndexOf("人工修改"' in bridge
    assert "ApplyHumanConfirmed(record)" in bridge
    assert 'record.Status = "active"' in bridge
    assert 'record.Type = "business_fact"' in bridge
    assert "ApplyImportedLegacyProvenance(record, entry)" in bridge
    bridge_importer = bridge.split("namespace Bot.Knowledge")[0]
    assert 'record.Type = "learning_candidate";' not in bridge_importer
    assert 'record.Status = "candidate";' not in bridge_importer
    assert 'LearnAsync(e.Question, wnd.EditedAnswer, "人工修改"' in ui
    assert "KnowledgeEngineV2LearningBridge.SynchronizeSeller(e.Seller)" in ui
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in service


def test_runtime_and_test_console_share_v2_authority_policy():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in service
    assert ".Where(KnowledgeV2AuthorityPolicy.IsProductionApproved)" in service
    assert ".Select(KnowledgeV2AuthorityPolicy.NormalizeForRead)" in service
    assert "LearningCandidates = all.Count(KnowledgeV2AuthorityPolicy.IsCandidate)" in service


def test_reply_progress_cannot_resurrect_a_cancelled_turn():
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "UI/metrics observer only. BuyerSessionAgent is the sole business lifecycle authority." in tracker
    ready = tracker[tracker.index("public static CtlConversation SetAnswerReady"):tracker.index("public static void MarkDeliveryConfirmed")]
    assert "ObserveQuestion(seller, buyer, question, detected);" not in ready
    assert "已丢弃失效turn的迟到答案就绪观察" in ready
    terminal = tracker[tracker.index("private static string ResolveTerminalTurnKey"):tracker.index("private static bool TryRemoveTurn")]
    assert "if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;" in terminal


def test_streaming_pipeline_uses_session_agent_as_terminal_owner():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert 'lease.MarkGenerating("streaming_answer_started")' in pipeline
    assert 'lease.MarkReady("streaming_answer_materialized")' in pipeline
    assert 'lease.MarkSending("streaming_send_started")' in pipeline
    assert 'lease.MarkCompleted("streaming_send_completed")' in pipeline
    assert 'lease.MarkFailed("streaming_send_failed")' in pipeline
    assert 'lease.MarkCompleted("streaming_answer_generated_only")' in pipeline
    stable = coordinator[coordinator.index("public async Task<bool> ConfirmStableAsync"):coordinator.index("public bool MarkProcessing")]
    assert 'MarkReady("send_barrier_stable")' not in stable
    assert "return IsCurrent && !CancellationToken.IsCancellationRequested;" in stable


def test_explicit_human_answer_is_not_rewritten_by_ai_organizer():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "KnowledgeV2AuthorityPolicy.IsExplicitHumanConfirmationSource(sourceType)" in learning
    assert "learnedAnswer = safeAnswer;" in learning


def test_v2_learning_bridge_skips_already_synchronized_unchanged_entries():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    assert "IsPersistedStateSynchronized(existing, entry)" in bridge
    assert "public static bool IsPersistedStateSynchronized" in bridge
    assert 'record.Authority >= 0.98' in bridge
    assert 'record.Confidence >= 0.94' in bridge
