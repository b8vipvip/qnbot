from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_policy_switch_is_shop_scoped_and_neutral_when_disabled():
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgePolicyProfileService.cs").exists()
    source = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "SettingsLocks.GetOrAdd(shop.ShopKey" in source
    assert "KnowledgeEngineV2Constants.ModeProduction" in source
    assert "DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, seller)" in index
    assert "ShopReceptionPolicy.IsQuarantined" in index


def test_policy_window_has_explicit_switch_and_full_import_export():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert "运行状态：始终启用（不可关闭）" in source
    assert "导入V2完整包" in source and "导出V2完整包" in source
    assert "合并V2知识包" in source and "追加V2知识包" in source
    assert "TryReplaceAllIfUnchanged" in source


def test_policy_full_export_contains_reliability_stats():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    model = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    for field in ("UseCount", "AcceptedCount", "CorrectionCount", "WithdrawCount"):
        assert field in model
    assert "FeedbackEvents = KnowledgeEngineV2FeedbackService.ExportEvents(_seller)" in source
    assert "KnowledgeEngineV2Repository.ResumePortableImport" in source


def test_knowledge_center_has_complete_package_import_export():
    center = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    io = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert 'new KnowledgeV2ImportExportPage(_owner, _seller, _shopKey)' in center
    assert '导入V2完整包' in io
    assert '导出V2完整包' in io
    assert "KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(_seller, expectedFingerprint, replacement, recovery)" in io
    assert "Settings = KnowledgeEngineV2Service.GetSettingsView(_seller)" in io
    assert "ParseValidatedPackage" in io
    assert "CreateAutomaticBackup()" in io
    assert "BotFeatureStore.SaveKnowledgeBase" not in io


def test_dispatched_buyer_work_is_not_cancelled_by_next_buyer_message():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    enqueue = coordinator[coordinator.index("public void Enqueue(BuyerMessageBurstItem item)"):coordinator.index("private bool HasPendingBuyerMessages")]
    assert "InvalidateDispatchedAnswerOnArrival" not in enqueue
    assert "HardCancelVersion" in coordinator
    assert "state.HardCancelVersion == capturedHardCancelVersion" in coordinator
    assert "ParallelReplyRelevanceGate.ShouldSend" in pipeline
    assert "允许作为补充答案发送" in pipeline


def test_order_empty_message_center_event_triggers_independent_probe():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert 'ObserveGenericPaymentSignal(qn, "messageCenterNotify空载荷")' in source
    assert '已转入独立订单补扫' in source


def test_demonstrative_question_can_still_use_high_confidence_local_direct():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "IsSelfContainedDemonstrativeQuestion" in source
    assert "!IsSelfContainedDemonstrativeQuestion(message)" in source
    assert 'StartsWith("product:", StringComparison.OrdinalIgnoreCase)' in source
    assert "query.ContextDependent || !string.IsNullOrWhiteSpace(query.WorkingMemoryReason)" in public

# CI trigger: final feature branch contains no temporary workflow or patch helper files.
