"""Sandbox simulation cannot send, order, or auto-learn; human-confirmed saves are explicit."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_existing_test_bench_has_separate_conversation_sandbox_and_search_tab():
    page = source("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert 'Header = "客服对话沙盒"' in page
    assert "new KnowledgeV2SandboxConversation(_seller)" in page
    assert 'Header = "知识检索与性能"' in page
    assert "30次性能测试" in page
    assert 'Nav("测试台", () => new KnowledgeV2DebuggerPage' in source(
        "src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")


def test_sandbox_is_read_only_without_outbound_order_or_ai_learning():
    simulator = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    forbidden = [
        "SendTextWithRetryAsync(", "SendText(", "OrderEventHub.Publish(",
        "TryCreatePlan(", "OrderPlacedAutoReplyService.ResolveAsync(",
        "FirstInquiryFixedReplyService.TryResolve(", "MarkDelivered(",
        "KnowledgeLearningService.LearnAsync(", "QueueLearn(",
        "ResponseProgressTracker.SetAnswerReady(", "MyOpenAI.GetAnswer(",
        "BotFlowTestService.RunAsync(", "BotFeatureStore.Save",
    ]
    for token in forbidden:
        assert token not in simulator, token
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(_seller)" in simulator
    assert "using (ShopSettingsScope.Enter(shop))" in simulator
    assert '"__knowledge_v2_sandbox__"' in simulator
    assert "KnowledgeEngineV2Service.Resolve(_seller, _syntheticBuyer, message)" in simulator
    assert "MyOpenAI.CallStructuredChat(" in simulator
    assert "OrderEventHub.ReadSandboxSamples(_seller, 150)" in simulator
    assert "KnowledgeEngineV2Repository.Save(_seller, knowledge)" in simulator
    # Save is only called from the explicit operator-confirmed knowledge action.
    save_section = simulator.split("private void SaveDirectAnswer()", 1)[1].split(
        "private sealed class KnowledgePreview", 1)[0]
    assert "MessageBoxResult.Yes" in save_section
    assert "KnowledgeEngineV2Semantics.IsHighRisk(" in save_section
    assert "KnowledgeEngineV2Repository.LoadAll(_seller)" in save_section
    assert "duplicate != null" in save_section


def test_all_scenario_inputs_use_configured_policies_and_are_labeled_preview():
    simulator = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    for scene in ("首条咨询", "商品链接", "本地短消息", "特定固定规则",
                  "下班回复", "模拟下单", "模拟付款", "普通咨询 / 知识V2"):
        assert scene in simulator
    assert "FirstInquiryFixedReplyService.Load(_seller)" in simulator
    assert "GetEnabledPresetsForPreview(_seller)" in simulator
    assert "LocalShortReplyService.TryResolve(_seller, message" in simulator
    assert "BotFeatureStore.EvaluateAutoReplyRule(message)" in simulator
    assert 'GetSetting(cfg, "EnableOrderPlacedReply", false)' in simulator
    assert 'GetSetting(cfg, "OrderPlacedReplyText", "")' in simulator
    assert 'orderId.StartsWith("SIM-", StringComparison.Ordinal)' in simulator
    assert "IsOnlyProductLink(scenes, message)" in simulator
    assert "_orderPreviewed.Contains(key)" in simulator


def test_preset_preview_does_not_initialize_default_config_as_a_write():
    presets = source("src/Bot/ChromeNs/BotReplyPresetService.cs")
    method = presets.split("GetEnabledPresetsForPreview(string seller)", 1)[1].split(
        "public static List<BotReplyPreset> GetEnabledPresets(", 1)[0]
    assert "TryGetString(SettingsKey, out json)" in method
    assert "CreateDefaults()" in method
    assert "SetString(" not in method
    assert "SavePresets(" not in method


def test_sandbox_is_compiled_with_wpf_temporary_project():
    targets = source("src/Directory.Build.targets")
    assert "Knowledge\\KnowledgeV2SandboxConversation.cs" in targets
    assert "<Compile Include=" in targets


def test_multi_checked_scenarios_and_explicit_order_event_buttons():
    ui = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    assert "Dictionary<string, CheckBox> _scenarios" in ui
    assert "IsChecked = true" in ui
    assert '"模拟发货"' in ui
    assert 'foreach (var scene in new[] { "模拟下单", "模拟付款", "模拟发货" })' in ui
    assert "if (isOrder && !scenes.Contains(forcedEvent))" in ui
    assert "if (!string.IsNullOrWhiteSpace(eventScene))" in ui
    assert "isShipped ? \"已发货\" : isPaid ? \"已付款\" : \"待付款\"" in ui


def test_historical_order_samples_only_read_shop_data_not_real_order_ids():
    hub = source("src/Bot/ChromeNs/OrderEventHub.cs")
    method = hub.split("internal static List<OrderSnapshot> ReadSandboxSamples(", 1)[1].split(
        "public static OrderSnapshot RefreshFromCanonical", 1
    )[0]
    assert "TryReadState(GetPath())" in method
    assert "string.Equals((x.Snapshot.Seller ?? \"\").Trim(), seller" in method
    assert 'OrderId = "SIM-REC-" + Guid.NewGuid()' in method
    assert 'Buyer = "模拟买家"' in method
    assert "Publish(" not in method
    assert "SaveInternal(" not in method
    assert "x.Snapshot.Buyer" not in method
    assert "x.Snapshot.OrderId" not in method


def test_ai_fallback_uses_sandbox_history_and_never_calls_real_getanswer():
    ui = source("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    assert "if (result.NeedsAi && enableAi && !isOrder)" in ui
    assert "MyOpenAI.CallStructuredChat(" in ui
    assert "ShopSettingsScope.Enter(shop)" in ui
    assert '"__knowledge_v2_sandbox__"' in ui
    assert "MyOpenAI.GetAnswer(" not in ui
    assert "OrderPlacedAutoReplyService.ResolveAsync(" not in ui
