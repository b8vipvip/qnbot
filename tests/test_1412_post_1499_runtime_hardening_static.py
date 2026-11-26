from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shared_desk_rpa_does_not_reattach_same_seller_pid_hwnd_on_force_refresh():
    src = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    assert "private string _sellerDeskBoundSeller = string.Empty;" in src
    assert "string.Equals(_sellerDeskBoundSeller, seller, StringComparison.Ordinal)" in src
    assert "automationApplication != null" in src
    assert "_sellerDeskProcessId == desk.ProcessId" in src
    assert "_sellerDeskHwnd == desk.Hwnd.Handle" in src
    assert "_sellerDeskBoundSeller = seller;" in src
    assert "_sellerDeskBoundSeller = string.Empty;" in src
    # The cached binding check must no longer be bypassed by force=true. Production 1.1.1499
    # repeatedly called EnsureSellerDeskBinding(true) while switching shared-window seller tabs.
    assert "if (!force && automationApplication != null" not in src


def test_platform_send_block_cancels_started_delivery_watchdog_but_never_clicks_continue():
    src = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    assert "SendDeliveryWatchdog.CancelConversation(" in src
    assert '"platform_send_blocked:" + (stage ?? string.Empty)' in src
    assert "平台发送拦截已同步取消发送回显监控" in src
    assert "不会自动点击“继续发送”" in src
    assert "ReturnModifyButton.AsButton().Invoke()" in src
    assert "ContinueButton.AsButton().Invoke()" not in src
    assert 'SetSendCancellation("警告撤回"' in src
    # Preserve the fail-closed policy from the 2026-09-09 field incident: the rejected
    # message may be returned for cleanup, but the Bot must never force it through.
    assert "result.Continued = false;" in src


def test_multi_seller_active_shop_fix_remains_present_after_runtime_hardening():
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    registry = read("src/Bot/Automation/ChatDeskNs/DeskSellerBindingRegistry.cs")
    assert "ActiveShopSessionRegistry.ActivateFromVerifiedConversationChange(" in bridge
    assert 'ActiveShopSessionRegistry.ReassertCurrent("verified-onConversationChange")' in bridge
    assert "真实onConversationChange已晋升活动客服" in bridge
    assert "活动店铺会话已切换" in coordinator
    assert "ActiveSellerByHwnd" in registry
    assert "同一Desk不能绑定两个seller" not in registry


def test_shop_identity_cache_isolates_bad_profiles_and_preserves_last_good_mappings():
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    refresh = bridge.split("private static void RefreshProfileCacheIfNeeded()", 1)[1].split(
        "private static string StableSellerRef", 1
    )[0]
    assert "IList<ShopProfile> profiles;" in refresh
    assert "profiles = Profiles.GetAll();" in refresh
    assert "Keep the last good identity cache" in refresh
    assert "foreach (var profile in profiles.Where(x => x != null))" in refresh
    assert "catch (Exception ex)" in refresh
    assert "failedShopKeys.Add(failedKey)" in refresh
    assert "failedSellerRefs.Add(failedRef)" in refresh
    assert "ShopByKey.TryGetValue(failedKey, out previous)" in refresh
    assert "ShopBySellerRef.TryGetValue(failedRef, out previous)" in refresh
    assert "ShopByKey.Clear();" not in refresh
    assert "ShopBySellerRef.Clear();" not in refresh
    assert "nextByKey" in refresh and "nextBySellerRef" in refresh


def test_shop_log_seller_ref_aliases_fail_closed_when_nickname_maps_to_multiple_shops():
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    refresh = bridge.split("private static void RefreshProfileCacheIfNeeded()", 1)[1].split(
        "private static string StableSellerRef", 1
    )[0]
    assert "var ambiguousSellerRefs = new HashSet<string>(StringComparer.Ordinal);" in refresh
    assert "void AddSellerRefAlias(string sellerRef, ShopContext shop)" in refresh
    assert "existing.ShopKey, shop.ShopKey" in refresh
    assert "nextBySellerRef.Remove(sellerRef);" in refresh
    assert "ambiguousSellerRefs.Add(sellerRef);" in refresh
    assert "AddSellerRefAlias(sellerRef, shop);" in refresh
    assert "if (!ambiguousSellerRefs.Contains(failedRef)" in refresh
    assert "if (!ambiguousSellerRefs.Contains(pair.Key)" in refresh
