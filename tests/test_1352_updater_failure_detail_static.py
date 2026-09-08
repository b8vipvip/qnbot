from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def test_updater_persists_stack_and_position_for_field_failures():
    text = UPDATER.read_text(encoding="utf-8-sig")
    assert "function Get-FailureDetail" in text
    assert "ScriptStackTrace" in text
    assert "InvocationInfo.PositionMessage" in text
    assert "$failureDetail = Get-FailureDetail $failure" in text
    assert "Write-UpdateResult $resultPath 'failed' $ExpectedVersion $stage $failureDetail" in text


def test_updater_has_one_mutation_commit_state_not_multiple_condition_owners():
    text = UPDATER.read_text(encoding="utf-8-sig")
    waiter_start = text.index("function Wait-BotWebSocketPortRelease")
    waiter_end = text.index("function Clear-DirectoryContentsWithRetry")
    waiter = text[waiter_start:waiter_end]
    assert "$handoff = Get-BotWebSocketHandoffState $TargetInstallDir $Port" in waiter
    assert "if ([bool]$handoff.Ready)" in waiter
    assert "Get-NetTCPConnection" not in waiter
    assert "Test-LoopbackPortBindable" not in waiter
    assert "Get-InstallProcessState" not in waiter
