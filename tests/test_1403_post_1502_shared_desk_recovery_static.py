from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.MultiShopDeskBinding.cs"


def text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_active_shop_can_recover_lost_shared_desk_mapping_without_guessing():
    src = text(RPA)
    assert "TryRecoverActiveSellerDesk" in src
    assert "ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)" in src
    assert "Desk.Snapshot().Where(x => x != null && x.IsAlive).ToList()" in src
    assert "if (desks.Count != 1) return null;" in src
    assert '"active-session-single-desk-recovery"' in src
    assert "DeskSellerBindingRegistry.BindResolvedSeller" in src
    assert "DeskSellerBindingRegistry.MarkActiveSeller" in src


def test_recovery_stays_fail_closed_for_background_or_ambiguous_windows():
    src = text(RPA)
    recovery = src[src.index("private Desk TryRecoverActiveSellerDesk"):src.index("internal bool EnsureSellerDeskBinding")]
    assert "if (RuntimeSellerCount() <= 1) return null;" in recovery
    assert "if (!ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)) return null;" in recovery
    assert "if (desks.Count != 1) return null;" in recovery
    assert "FirstOrDefault" not in recovery


def test_existing_idempotent_attach_guard_is_preserved():
    src = text(RPA)
    assert "automationApplication != null" in src
    assert "_sellerDeskProcessId == desk.ProcessId" in src
    assert "_sellerDeskHwnd == desk.Hwnd.Handle" in src
    assert "string.Equals(_sellerDeskBoundSeller, seller, StringComparison.Ordinal)" in src
