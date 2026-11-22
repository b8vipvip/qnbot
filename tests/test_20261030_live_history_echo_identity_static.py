"""Regressions for one physical Qianniu message in mixed live/history mirrors."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_live_seller_and_remote_history_share_one_canonical_timeline():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "RecordMessage(state, seller, buyer, message, messageText, false);" in store
    assert "RecordMessage(state, seller, buyer, item, ExtractMessageText(item), true);" in store
    record = store.split("private static string RecordMessage(", 1)[1].split(
        "private static void MarkLastSellerTurnWithdrawn(", 1
    )[0]
    assert "GetNativeMessageAliases(message)" in record
    assert "GetPreciseNativeTimeTicks(message)" in record
    assert "HasSharedNativeAlias(t.NativeAliases, aliases)" in record
    assert "t.PreciseTimeTicks == preciseTime" in record
    assert "precisePeers.Count == 1" in record
    assert "peers.Count == 1 && opposite.Count == 1" in record
    assert "MergeObservedIdentity(exact, aliases, preciseTime, timestamp, fromHistory)" in record
    assert "MergeObservedIdentity(opposite[0], aliases, preciseTime, timestamp, fromHistory)" in record


def test_live_and_history_messages_resolve_buyer_aliases_within_shop_scope():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    live = store.split("public static void RefreshAndRecord(", 1)[1].split(
        "/// <summary>", 1
    )[0]
    seller_echo = store.split("internal static bool RecordObservedSellerMessage(", 1)[1].split(
        "public static bool IsPlatformSystemTip(", 1
    )[0]
    record = store.split("private static string RecordMessage(", 1)[1].split(
        "private static string[] GetNativeMessageAliases(", 1
    )[0]

    # Live events establish the alias before selecting the canonical timeline key.
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message);" in live
    assert "BuyerIdentityAliasService.ResolveInternalNick(seller, buyer)" in live

    # Seller echoes remain bound to the exact shop and seller, while accepting only
    # a buyer alias already observed by the shop-scoped identity service.
    assert "var shop = ResolveShop(seller, shopKey);" in seller_echo
    assert "ShopSettingsScope.Enter(shop)" in seller_echo
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message);" in seller_echo
    assert "BuyerIdentityAliasService.AreEquivalent(seller, to, buyer)" in seller_echo
    assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in seller_echo

    # Remote history uses the same alias-aware role classification as live messages.
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message);" in record
    assert "BuyerIdentityAliasService.AreEquivalent(seller, from, buyer)" in record
    assert "BuyerIdentityAliasService.AreEquivalent(seller, to, buyer)" in record


def test_true_same_text_double_sends_are_not_collapsed_by_text_alone():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    record = store.split("private static string RecordMessage(", 1)[1].split(
        "private static string[] GetNativeMessageAliases", 1
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
    assert "BuildBotEchoEntityId(shopKey, seller, buyer, messageKey, sentAt, answer)" in history
    assert "BuildLegacyBotEchoEntityId" in history


def test_native_dedup_diagnostics_do_not_log_buyer_or_reply_text():
    store = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "idHash=" in store
    assert "StableHash(messageKey).ToString(CultureInfo.InvariantCulture)" in store
    assert "role=" in store
    assert "clientId alone may identify a transport client" in store


def test_timeline_and_image_cache_resolve_canonical_seller_endpoint_before_buyer():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    vision = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    for source in (context, vision):
        seller = source[
            source.index("private static string GetSellerNick"):
            source.index("private static string GetBuyerNick")
        ]
        buyer = source[
            source.index("private static string GetBuyerNick"):
            source.index("private static", source.index("private static string GetBuyerNick") + 1)
        ]
        assert "ResolveRuntimeByShopKey(shopKey)" in seller
        assert "DirectOrderIdentityResolver.IdentityEquals(loginNick, shop.SellerNick)" in seller
        assert "DirectOrderIdentityResolver.IdentityEquals(loginNick, current.SellerNick)" in seller
        assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in buyer
        assert "DirectOrderIdentityResolver.IdentityEquals(to, seller)" in buyer
        assert "if (fromIsSeller == toIsSeller) return string.Empty;" in buyer
    seller_echo = context[
        context.index("internal static bool RecordObservedSellerMessage"):
        context.index("public static bool IsPlatformSystemTip")
    ]
    assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in seller_echo
    assert "DirectOrderIdentityResolver.IdentityEquals(loginNick, seller)" in seller_echo


def test_shop_scoped_timeline_and_vision_require_matching_native_seller_target_id():
    resolver = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    helper = resolver[
        resolver.index("public static bool IsMessageFromShop"):
        resolver.index("private static bool Candidate")
    ]
    assert "IdentityEquals(message.fromid.nick, seller)" in helper
    assert "IdentityEquals(message.toid.nick, seller)" in helper
    assert "if (fromIsSeller == toIsSeller) return false;" in helper
    assert "IdentityEquals(loginNick, seller)" in helper
    assert "shop.HasStableSellerId" in helper
    assert "message.fromid.targetId" in helper
    assert "message.toid.targetId" in helper
    assert "message.loginid.targetId" in helper

    for path in (
        "src/Bot/ChromeNs/ConversationContextStore.cs",
        "src/Bot/ChromeNs/VisionImageCacheService.cs",
    ):
        source = read(path)
        seller = source[
            source.index("private static string GetSellerNick"):
            source.index("private static string GetBuyerNick")
        ]
        assert "DirectOrderIdentityResolver.IsMessageFromShop(message, shop)" in seller
        assert "fromIsSeller == toIsSeller ? string.Empty : loginNick" in seller
