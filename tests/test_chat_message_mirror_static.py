from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_bot_panel_renders_qianniu_style_message_timeline_instead_of_qa_cards():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert "StartMessageMirror();" in source
    assert "RefreshMessageMirror(true);" in source
    assert "stkDialog.Children.Add(ctlConversation)" not in source

    assert 'var sender = isBot ? "Bot" : (isSeller ? seller : buyer);' in mirror
    assert 'turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")' in mirror
    assert "HorizontalAlignment.Right" in mirror
    assert "HorizontalAlignment.Left" in mirror
    assert "BuildMessageMirrorDateSeparator" in mirror
    assert "BuildMessageMirrorBotContextMenu" in mirror
    assert "BuildCopyOnlyContextMenu" in mirror


def test_mirror_uses_real_buyer_and_seller_chat_events_and_remote_history():
    qn = read("src/Bot/ChromeNs/QN.cs")
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")

    seller_marker = """if (IsSellerMessage(message))
            {"""
    buyer_marker = """if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))
            {"""

    assert seller_marker in qn
    assert buyer_marker in qn
    assert qn.count("ConversationContextStore.RefreshAndRecord(message, messageText);") >= 2

    assert "GetMirrorTurns(" in context
    assert "Math.Min(100, maxTurns)" in context
    assert "RequestMirrorRemoteRefresh(" in context
    assert "requestedCount = 40" in context
    assert "Math.Min(100, requestedCount)" in context
    assert "RefreshRemoteHistory(state, shop, seller, buyer, ccode, 100);" in context
    assert ".ThenBy(t => t.MessageKey" not in context


def test_a_suffix_is_transport_only_and_bot_is_rendered_on_right_with_native_actions():
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert 'const string marker = "[A]"' in mirror
    assert "TryExtractBotEchoAnswer" in mirror
    assert "var usedNativeBotConversations = new HashSet<CtlConversation>();" in mirror
    assert "if (nativeBotConversation == null)" in mirror
    assert "usedNativeBotConversations.Add(nativeBotConversation);" in mirror
    assert "BuildMessageMirrorBubble(" in mirror
    assert "turn, seller, buyerDisplay, botAnswer, nativeBotConversation" in mirror
    assert "messageElement = nativeBotConversation;" not in mirror
    assert "BuildMessageMirrorBotContextMenu(botAction, turn, seller, buyerIdentity, buyer," in mirror

    # Bot bubbles retain their original actions even though the old Q/A card
    # is no longer the rendered element.
    assert "CreateAnswerContextMenu" in conversation
    assert "MirrorRequestResend" in mirror
    assert "MirrorRequestEdit" in mirror
    assert "KnowledgeCenterV2UiBridge.ShowBotReplySearch" in mirror
    assert "MirrorAnswerSource" in mirror
    for label in ("查看", "复制", "重发", "修改", "来源"):
        assert f'Header = "{label}"' in mirror

    # Human seller and buyer messages follow the same bubble layout.
    assert "messageElement = BuildMessageMirrorBubble(\n                        turn, seller, buyerDisplay, null, null, null, null, buyerIdentity);" in mirror


def test_mirror_renderer_is_wired_into_windows_build():
    targets = read("src/Directory.Build.targets")
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml")

    assert "CtlRobot.MessageMirror.cs" in targets
    assert 'Background="#F4F6F8"' in xaml
    assert 'Text="暂无聊天消息"' in xaml
    assert 'Text="正在同步当前买家的千牛聊天记录"' in xaml

def test_message_mirror_identity_and_reads_include_shop_key():
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "BuildConversationKey(shopKey, seller, buyer)" in robot
    assert "_messageMirrorShopKey" in mirror
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in mirror
    assert "LoadMessageMirrorTurns(QN qn, string seller, string buyer)" in mirror
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in mirror
    assert "using (ShopSettingsScope.Enter(shop))" in mirror

def test_message_mirror_resolves_live_ccode_and_canonical_buyer_identity():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    request = mirror.split(
        "private async void RequestMessageMirrorHistory(", 1
    )[1].split("private void RefreshMessageMirror(", 1)[0]

    # qnbotStatus creates a lightweight Conversation snapshot without CCode.
    # The mirror must query the live current conversation before fetching history.
    assert "await qn.CDP.GetCurrentConversationIDForHistory()" in request
    assert "var current = qn.Buyer;" in request
    assert "current.Ccode" in request
    assert "BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer)" in request
    assert "ConversationContextStore.RequestMirrorRemoteRefresh(seller, internalBuyer, ccode)" in request
    assert "_messageMirrorHistoryRequests.TryAdd(requestKey, 0)" in request

    # Raw chat events are keyed by the internal buyer nick; the panel may show
    # the display alias, so all timeline reads must use the canonical identity.
    assert "ResolveInternalNick(seller, buyerDisplay)" in mirror
    assert "GetMirrorTurns(seller, internalBuyer, 100)" in mirror

def test_message_mirror_keeps_display_alias_separate_from_canonical_action_identity():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "RenderMessageMirror(seller, buyerLabel, buyer, turns, keepBottom);" in mirror
    assert "string buyerDisplay,\n            string buyerIdentity," in mirror
    assert "FindMessageMirrorActionConversation(\n                        seller,\n                        buyerIdentity," in mirror
    assert "BotConversationHistoryStore.RecordBotEcho(\n                        seller, buyerIdentity," in mirror
    assert "BuildMessageMirrorBubble(\n                        turn, seller, buyerDisplay, botAnswer" in mirror
    assert "BuildMessageMirrorBotContextMenu(botAction, turn, seller, buyerIdentity, buyer," in mirror
    assert "LoadRecent(shopKey, seller, buyerIdentity, 200)" in mirror
    assert "ResolveShopKeyForConversation(seller, buyerIdentity), seller, botEchoRecordId" in mirror
    assert "ResolveShopKeyForConversation(seller, buyer), seller, botEchoRecordId" not in mirror
    assert '"\\n买家：" + buyerDisplay' in mirror



def test_message_mirror_history_reads_survive_cdp_contention_and_are_coalesced():
    cdp = read("src/Bot/ChromeNs/CDPClient.cs")
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert "HistoryGateWaitTimeoutMs = 10000" in cdp
    assert "InvokeForHistory<T>" in cdp
    assert "await _executeGate.WaitAsync(effectiveGateWaitTimeoutMs).ConfigureAwait(false)" in cdp
    assert "GetCurrentConversationIDForHistory" in cdp
    assert "RemoteHistoryInFlight" in context
    assert "RemoteHistoryLoaded" in context
    assert "TimeSpan.FromSeconds(5)" in context
    assert "TimeSpan.FromSeconds(30)" in context
    assert 'InvokeForHistory<JObject>("im.singlemsg.GetRemoteHisMsg"' in context
    assert "千牛镜像历史同步完成" in context
    assert "qn.Buyer.Ccode" in mirror


def test_message_mirror_preserves_last_good_timeline_on_transient_read_failure():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    loader = mirror.split(
        "private List<ConversationContextTurn> LoadMessageMirrorTurns(", 1
    )[1].split("private async void RequestMessageMirrorHistory(", 1)[0]
    refresh = mirror.split(
        "private void RefreshMessageMirror(bool force)", 1
    )[1].split("private static bool SameDisplayedTurn(", 1)[0]
    identity = refresh.split("if (identityChanged)", 1)[1].split(
        "var turns = LoadMessageMirrorTurns", 1
    )[0]
    outer_catch = refresh.split("catch (Exception ex)", 1)[1]

    # A transient read/scope exception must propagate to the outer refresh catch;
    # it must not be converted into an empty timeline that erases visible messages.
    assert "catch (Exception ex)" not in loader
    assert "return new List<ConversationContextTurn>()" not in loader
    assert "Log.ErrorWithMaxCount(\"刷新千牛镜像聊天区失败：" in outer_catch
    assert "RenderEmptyMessageMirror()" not in outer_catch

    # When the shop/buyer identity really changes, old messages are cleared before
    # attempting the new read, so preserving a last-good view cannot leak identities.
    assert "stkDialog.Children.Clear();" in identity
    assert "grdTipNoConv.Visibility = Visibility.Visible;" in identity
    assert "stkDialog.Children.Add(grdTipNoConv);" in identity


def test_message_mirror_recovers_using_exact_active_shop_and_cdp_session():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    registry = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")

    assert "internal static string GetActiveSessionId()" in registry
    assert "var activeSessionId = ActiveShopSessionRegistry.GetActiveSessionId();" in mirror
    assert "ShopContextLocator.FindConnectedQnByShopKey(\n                        activeShopKey, activeSessionId)" in mirror
    assert "ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(current)" in mirror

def test_read_only_bot_panel_identity_checks_do_not_require_qianniu_foreground():
    registry = read("src/Bot/Automation/ChatDeskNs/DeskSellerBindingRegistry.cs")
    native_binding = registry.split(
        "internal static bool IsSellerForDesk(Desk desk, string seller, string shopKey)", 1
    )[1].split("internal static bool IsBoundToDeskForReadOnlyUi", 1)[0]
    read_only_binding = registry.split(
        "internal static bool IsBoundToDeskForReadOnlyUi", 1
    )[1].split("internal static Desk BindResolvedSeller", 1)[0]

    # Keep strict foreground proof for side-effecting/native automation, but use
    # stable HWND + ShopKey + seller ownership for read-only assistant rendering.
    assert "desk.IsForeground" in native_binding
    assert "desk.IsForeground" not in read_only_binding
    assert "ShopToHwnd.TryGetValue(shopKey" in read_only_binding
    assert "ActiveShopByHwnd.TryGetValue(hwnd" in read_only_binding
    assert "ActiveSellerByHwnd.TryGetValue(hwnd" in read_only_binding

    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    resolver = mirror.split(
        "private QN ResolveMessageMirrorQn()", 1
    )[1].split("private List<ConversationContextTurn> LoadMessageMirrorTurns", 1)[0]
    assert "IsBoundToDeskForReadOnlyUi(" in resolver
    assert "_desk, seller, shop.ShopKey)" in resolver
    assert "IsSellerForDesk(_desk, seller)" not in resolver

    session_ui = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MultiShopSession.cs")
    assert "IsCurrentSessionForReadOnlyUi(qn)" in session_ui
    assert "IsBoundToDeskForReadOnlyUi(" in session_ui
    assert "_desk, seller, shopKey)" in session_ui

    robot_ui = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    reshow = robot_ui.split("public void ReShowAfterQNChange()", 1)[1].split(
        "private void RefreshConversations()", 1
    )[0]
    assert "IsCurrentSessionForReadOnlyUi(current)" in reshow
    assert "IsBoundToDeskForReadOnlyUi(_desk, seller, shopKey)" in reshow
    assert "IsActive(current)" not in reshow

    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    sync_ui = coordinator.split("private static void SyncAttachedUi(QN qn)", 1)[1].split(
        "\n        private static ", 1
    )[0]
    assert "ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(qn)" in sync_ui
    assert "IsBoundToDeskForReadOnlyUi(desk, seller, shopKey)" in sync_ui
    assert "ActiveShopSessionRegistry.IsActive(qn)" not in sync_ui
