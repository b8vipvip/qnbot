from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _method_body(text: str, signature: str, next_signature: str) -> str:
    start = text.index(signature)
    end = text.index(next_signature, start)
    return text[start:end]


def test_v11_migration_defers_live_qianniu_resource_mutation():
    script = (ROOT / "scripts" / "apply-recoverable-inject-v11.ps1").read_text(encoding="utf-8-sig")

    replacement = script.split("$runningReplacement = @'", 1)[1].split("'@", 1)[0]
    assert "if (IsWorkbenchRunning())" in replacement
    assert "migration deferred: AliWorkbench is running" in replacement
    assert "return;" in replacement
    assert "webui.zip/sign/cache mutation" in replacement
    assert "live patch; no close/kill/restart" not in replacement

    # The migration validator must keep rejecting the old destructive restart path.
    assert "KillWorkbenchProcesses();" in script
    assert "Legacy destructive QNInject migration remains" in script
    assert "Unsafe live Qianniu resource patching remains" in script


def test_attached_window_never_repositions_or_resizes_qianniu_host():
    source = (ROOT / "src" / "Bot" / "AssistWindow" / "WndAssist.AttachedPerformance.cs").read_text(encoding="utf-8-sig")

    assert "Desk.EvMaximize -= Desk_EvMaximize;" in source
    assert "Desk.EvMaximize += SafeDesk_EvMaximize;" in source

    maximize = _method_body(
        source,
        "private void SafeDesk_EvMaximize",
        "private void SafeDesk_EvMoved",
    )
    assert "Desk.ShowNormal" not in maximize
    assert "Desk.SetRect" not in maximize
    assert "Desk.SetLocation" not in maximize
    assert "Desk.BringTop" not in maximize

    track = _method_body(
        source,
        "private void SafeTrackGeometry",
        "private void SetRightPanelPositionWithoutVisibilityToggle",
    )
    assert "SetDeskLocation();" not in track
    assert "Desk.SetRect" not in track
    assert "Desk.SetLocation" not in track
    assert "Desk.ShowNormal" not in track
    assert "Desk.BringTop" not in track
