from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _method_body(text: str, signature: str, next_signature: str) -> str:
    start = text.index(signature)
    end = text.index(next_signature, start)
    return text[start:end]


def _code_only(text: str) -> str:
    return "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("//"))


def test_v12_migration_defers_live_qianniu_resource_mutation():
    qn = (ROOT / "src" / "Bot" / "Common" / "QNInject.cs").read_text(encoding="utf-8-sig")
    language = (ROOT / "src" / "Bot" / "Common" / "LanguageRepairService.cs").read_text(encoding="utf-8-sig")

    assert "TryRepairWhenQianniuClosedAsync" in qn
    assert "repairGate.WaitAsync(0)" in qn
    assert "resourceOwnerProcessNames" in qn
    assert "if (IsWorkbenchRunning())" in qn
    assert "KillWorkbenchProcesses" not in qn
    assert "Process.Kill()" not in qn
    assert "File.WriteAllText(signPath, string.Empty" in qn
    assert "ResourceOwnerProcessNames" in language
    assert "KillWorkbenchProcesses" not in language
    assert "Process.Kill()" not in language
    assert "preserved=LocalStorage/Cookies/IndexedDB/Login" in language


def test_attached_window_never_repositions_or_resizes_qianniu_host():
    source = (ROOT / "src" / "Bot" / "AssistWindow" / "WndAssist.AttachedPerformance.cs").read_text(encoding="utf-8-sig")

    assert "Desk.EvMaximize -= Desk_EvMaximize;" in source
    assert "Desk.EvMaximize += SafeDesk_EvMaximize;" in source

    maximize = _code_only(_method_body(
        source,
        "private void SafeDesk_EvMaximize",
        "private void SafeDesk_EvMoved",
    ))
    assert "Desk.ShowNormal" not in maximize
    assert "Desk.SetRect" not in maximize
    assert "Desk.SetLocation" not in maximize
    assert "Desk.BringTop" not in maximize

    track = _code_only(_method_body(
        source,
        "private void SafeTrackGeometry",
        "private void SetRightPanelPositionWithoutVisibilityToggle",
    ))
    assert "SetDeskLocation();" not in track
    assert "Desk.SetRect" not in track
    assert "Desk.SetLocation" not in track
    assert "Desk.ShowNormal" not in track
    assert "Desk.BringTop" not in track
