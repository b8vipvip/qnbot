from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_v11_build_migration_replaces_permanent_retire_with_recoverable_standby():
    script = (ROOT / "scripts/apply-recoverable-inject-v11.ps1").read_text(encoding="utf-8")
    assert "20260909-ws-recoverable-standby-v11" in script
    assert "websocketStandbyUntil" in script
    assert "scheduleReconnect(15000)" in script
    assert "recoverableStandby: true" in script
    assert "Permanent websocketRetired state remains after v11 patch" in script


def test_v11_qninject_migration_never_kills_running_qianniu():
    script = (ROOT / "scripts/apply-recoverable-inject-v11.ps1").read_text(encoding="utf-8")
    assert "不关闭、不Kill、不重启千牛" in script
    assert "局部页面重新加载" in script
    assert "Legacy destructive QNInject migration remains" in script
    assert "需要先退出千牛后注入插件" in script  # detection-only legacy signature
    assert "KillWorkbenchProcesses();" not in script


def test_windows_build_applies_v11_before_resources_and_compile_without_bot_targets_file():
    props = (ROOT / "src/Directory.Build.props").read_text(encoding="utf-8")
    assert 'BeforeTargets="PrepareResources;CoreCompile"' in props
    assert "apply-recoverable-inject-v11.ps1" in props
    assert "'$(OS)' == 'Windows_NT'" in props
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
