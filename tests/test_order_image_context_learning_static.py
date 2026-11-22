from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_notification_diagnostics_expand_nested_json_and_explain_rejection():
    code = read("src/Bot/ChromeNs/OrderNotificationTraceBridge.cs")
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "ParseExpanded" in code
    assert "Walk(JToken token" in code
    assert "订单通知未形成自动回复计划" in code
    assert "缺少明确下单/付款状态" in code
    assert "缺少可验证订单号" in code
    assert "未猜测当前会话" in code
    assert "payloadHash" in code
    assert "OrderNotificationTraceBridge.Start();" in monitor


def test_buyer_internal_nick_and_display_alias_share_bot_panel_and_recovery():
    aliases = read("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    ui = read("src/Bot/ChromeNs/BuyerIdentityAliasUiBridge.cs")
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "ResolveConversationKey" in aliases
    assert "ResolveInternalNick" in aliases
    assert "AreEquivalent" in aliases
    assert "buyerConversations" in ui
    assert "internalKey" in ui and "displayKey" in ui
    assert "ctl.ReShowAfterQNChange();" in ui
    assert "BuyerIdentityAliasService.ObserveMessage" in monitor
    assert "BuyerIdentityAliasService.AreEquivalent" in recovery
    assert "equivalent=false" in recovery


def test_this_kind_question_rebinds_recent_image_with_wider_caption_window():
    code = read("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "CaptionWindowSeconds = 15" in code
    assert "FollowUpWindowSeconds = 45" in code
    for phrase in ["这种", "这类", "这种能使用吗", "设备", "软件"]:
        assert phrase in code
    assert "图片指代续问已重新绑定最近图片" in code
    assert "最近图片未与后续文字合并" in code


def test_visual_result_retries_once_when_semantic_signature_is_missing():
    code = read("src/Bot/ChromeNs/VisionRequestService.cs")
    assert "StrictSemanticRepairPrompt" in code
    assert "visual_summary都不能为空" in code
    assert "视觉接口未返回结构化语义，开始一次同图JSON修复" in code
    assert "视觉结构化语义修复成功" in code
    assert "本轮可回复但不会建立图片学习候选" in code
    repair_at = code.index("StrictSemanticRepairPrompt")
    record_at = code.index("VisualKnowledgeLearningService.RecordVisionAnalysis")
    assert repair_at < record_at


def test_new_runtime_files_are_included_in_old_style_project_build():
    targets = read("src/Directory.Build.targets")
    for path in [
        "ChromeNs\\BuyerIdentityAliasService.cs",
        "ChromeNs\\BuyerIdentityAliasUiBridge.cs",
        "ChromeNs\\OrderNotificationTraceBridge.cs",
    ]:
        assert path in targets



def test_buyer_alias_storage_is_scoped_to_a_verified_shopkey_and_rejects_ambiguous_nicknames():
    source = read("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    observe = source[
        source.index("public static void Observe(string seller"):
        source.index("public static string ResolveConversationKey")
    ]
    scope = source[
        source.index("private static string ScopeKey"):
        source.index("private static bool Same", source.index("private static string ScopeKey"))
    ]

    assert "var shopKey = ScopeKey(seller);" in observe
    assert "if (shopKey.Length == 0) return;" in observe
    assert "Key(shopKey, seller, internalNick)" in observe
    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in scope
    assert "catch (AmbiguousShopIdentityException) { return string.Empty; }" in scope
    assert 'return "legacy-" + seller.ToLowerInvariant();' in scope
    assert "private static string Key(string shopKey, string seller, string alias)" in source


def test_runtime_safety_monitor_tracks_buyer_and_seller_turns_by_canonical_identity():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    observe = monitor[
        monitor.index("private static void Qn_EvRecieveNewMessage"):
        monitor.index("private static void RecordBuyerSourceSort")
    ]

    assert "ResolveRuntimeBySeller(qn)" in observe
    assert "using (ShopSettingsScope.Enter(shop))" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(to, seller)" in observe
    assert "if (fromIsSeller == toIsSeller) continue;" in observe
    assert "BuyerIdentityAliasService.ResolveInternalNick(seller, from)" in observe
    assert "BuyerIdentityAliasService.ResolveInternalNick(seller, to)" in observe
