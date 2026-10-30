"""Regressions for one physical Qianniu message in mixed live/history mirrors."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_live_seller_and_remote_history_share_one_canonical_timeline():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "RecordMessage(state, seller, buyer, message, messageText, false);" in store
    assert "RecordMessage(state, seller, buyer, item, ExtractMessageText(item), true);" in store
    record = store.split("private static void RecordMessage(", 1)[1].split(
        "private static void MarkLastSellerTurnWithdrawn(", 1
    )[0]
    assert "GetNativeMessageAliases(message)" in record
    assert "GetPreciseNativeTimeTicks(message)" in record
    assert "HasSharedNativeAlias(t.NativeAliases, aliases)" in record
    assert "t.PreciseTimeTicks == preciseTime" in record
    assert "precisePeers.Count == 1" in record
    assert "peers.Count == 1 && opposite.Count == 1" in record
    assert "MergeObservedIdentity(exact, aliases, preciseTime, fromHistory)" in record
    assert "MergeObservedIdentity(opposite[0], aliases, preciseTime, fromHistory)" in record


def test_true_same_text_double_sends_are_not_collapsed_by_text_alone():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    record = store.split("private static void RecordMessage(", 1)[1].split(
        "private static void MarkLastSellerTurnWithdrawn(", 1
    )[0]
    assert "t.Role == role" in record
    assert "t.SeenLive && !t.SeenHistory" in record
    assert "t.SeenHistory && !t.SeenLive" in record
    assert "peers.Count == 1 && opposite.Count == 1" in record
    assert "precisePeers.Count == 1" in record
    assert "SeenLive = !fromHistory" in record
    assert "SeenHistory = fromHistory" in record
    assert "state.Turns.Add(new ConversationContextTurn" in record
    # Same-feed text equality must not be used as an independent dedup reason.
    assert "state.Turns.Any(t => NormalizeText(t.Text) == normalizedText)" not in record


def test_echo_source_identity_stays_stable_after_reconciliation():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "turn.NativeAliases = (turn.NativeAliases ?? new string[0])" in store
    assert "turn.MessageKey =" not in store.split("private static void MergeObservedIdentity(", 1)[1].split(
        "private static void MarkLastSellerTurnWithdrawn(", 1
    )[0]
    assert "NativeAliases = source.NativeAliases == null" in store
    assert "SeenLive = source.SeenLive" in store
    assert "SeenHistory = source.SeenHistory" in store
    assert "turn.Timestamp, turn.MessageKey" in mirror
    assert "BuildBotEchoEntityId(seller, buyer, messageKey, sentAt, answer)" in history


def test_native_dedup_diagnostics_do_not_log_buyer_or_reply_text():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "idHash=" in store
    assert "StableHash(messageKey).ToString(CultureInfo.InvariantCulture)" in store
    assert "role=" in store
    assert "clientId alone may identify a transport client" in store
