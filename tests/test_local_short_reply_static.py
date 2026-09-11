from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def canonical_block():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    start = coordinator.index("internal static class CanonicalPreMergeDecisionService")
    end = coordinator.index("internal sealed class BuyerMessageBurstCoordinator", start)
    return coordinator[start:end]


def test_local_short_reply_is_shop_scoped_exact_match_and_never_calls_ai():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert 'ConfigFileName = "local-short-replies.json"' in service
    assert "Paths.GetConfigPath(shop, ConfigFileName)" in service
    assert "ShopSettingsScope.Current" in service
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in service
    assert "NormalizePhrase(phrase), normalized, StringComparison.Ordinal" in service
    assert "TrailingSafePunctuation" in service
    assert "？" not in service[service.index("TrailingSafePunctuation"):service.index("private sealed class CacheState")]
    assert "Contains(normalized)" not in service

    local_start = service.index("internal static class LocalShortReplyService")
    ui_start = service.index("namespace Bot.Knowledge")
    local_block = service[local_start:ui_start]
    assert "AiEndpointStore" not in local_block
    assert "StreamMessagesAsync" not in local_block
    assert "SemanticEmbeddingService" not in local_block


def test_defaults_cover_common_acknowledgement_thanks_wait_solved_and_closing_phrases():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    for phrase in [
        "好的", "好", "OK", "嗯", "收到", "知道了", "明白了",
        "谢谢", "辛苦了", "已经好了", "解决了", "稍等一下",
        "我试试", "不用了", "不好意思", "再见", "晚安",
    ]:
        assert phrase in service

    assert '"好的。"' in service
    assert '"不客气。"' in service
    assert '"好的，您先操作，有问题再告诉我。"' in service
    assert '"好的，有需要再联系我们。"' in service


def test_knowledge_center_gets_editable_short_message_management_page():
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert 'Header = "短消息回复"' in service
    assert '"问答管理"' in service
    assert "managerIndex + 1" in service
    assert "LocalShortReplyManagerControl" in service
    assert '"新增"' in service
    assert '"编辑所选"' in service
    assert '"启用/停用"' in service
    assert '"删除所选"' in service
    assert '"恢复默认模板"' in service
    assert '"导入JSON"' in service
    assert '"导出JSON"' in service
    assert "LocalShortReplyEditWindow" in service
    assert "SaveForCurrentUi" in service


def test_canonical_short_reply_precedes_normal_merge_and_preserves_handoff_priority():
    canonical = canonical_block()

    local = canonical.index("LocalShortReplyService.TryResolve(")
    outcome = "return localOk ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;"
    local_end = canonical.index(outcome, local) + len(outcome)
    handoff_check = canonical.rfind("BotFeatureStore.EvaluateAutoReplyRule(question)", 0, local)
    local_block = canonical[local:local_end]
    assert handoff_check >= 0
    assert handoff_check < local < local_end
    assert '"本地短消息回复"' in local_block
    assert outcome in local_block
    assert "aiCalled=false" in local_block


def test_new_buyer_message_enters_one_premerge_queue_before_merge():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    enqueue_start = coordinator.index("public void Enqueue(BuyerMessageBurstItem item)")
    observe = coordinator.index("_sessionAgent.ObserveBuyerMessage(", enqueue_start)
    queue = coordinator.index("state.PendingRules.Enqueue(item)", observe)
    worker = coordinator.index("private async Task RunAsync", queue)
    decision = coordinator.index("CanonicalPreMergeDecisionService.HandleAsync(", worker)
    merge = coordinator.index("EnqueueForMerge(item)", decision)
    assert enqueue_start < observe < queue < worker < decision < merge
    assert "Task.Run(async () =>" not in coordinator[enqueue_start:worker]
    assert "item.SessionGeneration = observation.Generation;" in coordinator[observe:queue]

    premerge = coordinator.split("private async Task ProcessPreMergeAsync", 1)[1].split(
        "private bool HasPendingBuyerMessages", 1
    )[0]
    pending = coordinator.split("private bool HasPendingBuyerMessages", 1)[1].split(
        "private void EnqueueForMerge", 1
    )[0]
    assert "state.PendingRules.Count == 0" in premerge
    assert "state.Items.Count == 0" in premerge
    assert "state.InFlightDispatches.Count == 0" in premerge
    assert "state.PendingRules.Count > 0" in pending
    assert "state.Items.Count > 0" in pending
    assert "state.InFlightDispatches.Count > 0" in pending


def test_management_page_registration_is_explicit_and_idempotent():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    service = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "Bot.Knowledge.LocalShortReplyUi.Initialize();" in coordinator
    assert "Interlocked.Exchange(ref _initialized, 1)" in service
    assert "EventManager.RegisterClassHandler(" in service
    assert "ConditionalWeakTable<KnowledgeCenterWindow, object>" in service