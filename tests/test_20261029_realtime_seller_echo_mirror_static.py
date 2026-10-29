"""A confirmed seller message must not depend on remote history to appear in Bot panel."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text(encoding="utf-8-sig")


def test_seller_echo_enters_canonical_mirror_before_agent_event():
    bridge = source("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    segment = bridge.split(
        "var botEcho = SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text);", 1
    )[1].split("var result = Agent.RecordEvent(", 1)[0]
    assert "ConversationContextStore.RecordObservedSellerMessage(" in segment
    assert "seller, buyer, message, text" in segment
    assert "RefreshRemoteHistory" not in segment


def test_seller_echo_is_strictly_shop_scoped_and_id_deduplicated():
    store = source("src/Bot/ChromeNs/ConversationContextStore.cs")
    segment = store.split(
        "internal static bool RecordObservedSellerMessage(", 1
    )[1].split("public static bool IsPlatformSystemTip(", 1)[0]
    assert "string.Equals(from, seller, StringComparison.Ordinal)" in segment
    assert "string.Equals(to, buyer, StringComparison.Ordinal)" in segment
    assert "var shop = ResolveShop(seller);" in segment
    assert "if (shop == null) return false;" in segment
    assert "ShopSettingsScope.Enter(shop)" in segment
    assert "RecordMessage(state, seller, buyer, message, messageText);" in segment
    assert "RequestMirrorRemoteRefresh" not in segment
    record = store.split("private static void RecordMessage(", 1)[1].split(
        "private static void MarkLastSellerTurnWithdrawn(", 1
    )[0]
    assert "IncomingMessageSafety.BuildMessageKey(message, cleanText)" in record
    assert "state.Turns.Any(t => t.MessageKey == messageKey)" in record


def test_bot_echo_renders_native_card_even_without_local_reply_card():
    mirror = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "TryExtractBotEchoAnswer(turn, out botAnswer)" in mirror
    assert "nativeBotConversation = CtlConversation.Create(" in mirror
    assert "nativeBotConversation.SetSendResult(true," in mirror
    assert 'const string marker = "[A]";' in mirror


def test_mirror_never_shows_internal_system_style_prompts():
    store = source("src/Bot/ChromeNs/ConversationContextStore.cs")
    mirror = store.split("public static List<ConversationContextTurn> GetMirrorTurns(", 1)[1].split(
        "public static void RequestMirrorRemoteRefresh(", 1
    )[0]
    assert '(t.Role == "user" || t.Role == "assistant")' in mirror
    assert "var eligible = state.Turns" in mirror
