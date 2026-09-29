from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CHROME = ROOT / "src" / "Bot" / "ChromeNs"
SHOP = ROOT / "src" / "Bot" / "ShopScope"


def text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def all_cs_under(path: Path) -> str:
    return "\n".join(p.read_text(encoding="utf-8-sig") for p in path.rglob("*.cs"))


def test_commerce_selects_question_relevant_order_not_blind_latest():
    commerce = text(CHROME / "CommerceContextService.cs")
    guard = text(CHROME / "OrderGuidanceDeliveryGuard.cs")

    assert "GetRecentOrderSnapshots" in guard, (
        "Commerce must be able to inspect the buyer's recent per-order snapshots; "
        "a single latest snapshot is insufficient when the buyer has multiple orders."
    )
    assert "SelectRelevantOrderSnapshot" in commerce
    build_start = commerce.index("public static CommerceContextSnapshot Build")
    build_body = commerce[build_start: commerce.index("public static void EnrichState", build_start)]
    assert "TryGetLatestOrderSnapshot" not in build_body, (
        "Build must not blindly choose the latest buyer order."
    )
    assert "selectionReason" in commerce or "SelectionReason" in commerce
    assert "Commerce订单选择" in commerce, (
        "Selected order diagnostics must expose the selection reason and structured evidence."
    )


def test_cdp_foreground_chat_can_promote_connected_standby_without_disabling_guard():
    server = text(CHROME / "MyWebSocketServer.cs")
    coordinator = text(CHROME / "MultiShopRuntimeSessionCoordinator.cs")

    assert "TryPromoteDuplicateSellerSession" in server
    active_index = server.index('wMsg.Type == "onChatDlgActive"')
    window = server[max(0, active_index - 3000): active_index + 5000]
    assert "TryPromoteDuplicateSellerSession" in window, (
        "A real foreground onChatDlgActive from a connected standby must atomically become authority."
    )
    assert "IsAuthoritativeSellerSession" in server, (
        "The WebSocket authority registry must remain fail-closed after foreground rebinding."
    )
    assert "ValidateNativeSend" in coordinator, (
        "Native send must still reject a seller/shop/CDP session that is not the active authority."
    )
    validate = coordinator[coordinator.index("internal static bool ValidateNativeSend"):coordinator.index("internal static void ReassertCurrent")]
    assert "activeSession=" in validate and "targetSession=" in validate
    assert "return false" in validate


def test_control_plane_credentials_are_resolved_from_current_shop_at_call_time():
    chrome = all_cs_under(CHROME)
    shop = all_cs_under(SHOP)

    assert "ResolveForCurrentShop" in chrome or "ResolveForCurrentShop" in shop, (
        "Control-plane endpoints must resolve credentials from the current ShopKey at request time, "
        "rather than retaining a process-global/stale ApiKey."
    )
    combined = chrome + "\n" + shop
    assert "ShopSettingsScope.Current" in combined
    assert "ShopControlPlaneConnectionStore" in combined
    assert "TryGetToken" in combined
    assert "客户端令牌无效" in combined or "Unauthorized" in combined, (
        "401 must have an explicit fail-closed recovery path."
    )
    assert "ClaimAsync" in combined, (
        "An unchanged token rejected with 401 must be validated against its own ShopKey binding."
    )


def test_401_recovery_never_falls_back_to_another_shop_or_legacy_global_token():
    source = all_cs_under(SHOP) + "\n" + all_cs_under(CHROME)

    marker = "401同店令牌恢复"
    assert marker in source, (
        "Recovery must be observable in logs without exposing the token."
    )
    # This exact legacy getter may remain for migration/UI compatibility, but the recovery path
    # must explicitly document and enforce that it cannot use it as a runtime fallback.
    recovery_files = [
        p for p in list(SHOP.rglob("*.cs")) + list(CHROME.rglob("*.cs"))
        if marker in p.read_text(encoding="utf-8-sig")
    ]
    assert recovery_files
    recovery = "\n".join(p.read_text(encoding="utf-8-sig") for p in recovery_files)
    assert "GetLegacyGlobalToken" not in recovery
    assert "same-shop" in recovery.lower() or "同店" in recovery
