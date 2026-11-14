from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v12_injection_payload_is_the_committed_source_of_truth():
    marker = "20261114-ws-recoverable-reconnect-v12"
    inject = read("src/Bin/inject.js")
    qn = read("src/Bot/Common/QNInject.cs")
    language = read("src/Bot/Common/LanguageRepairService.cs")
    props = read("src/Directory.Build.props")

    assert 'window.__qnbotInjectVersion = "' + marker + '";' in inject
    assert 'private const string injectVersionMarker = "' + marker + '";' in qn
    assert 'private const string InjectVersionMarker = "' + marker + '";' in language
    assert "ApplyRecoverableQianniuInjectV11" not in props
    assert "apply-recoverable-inject-v11.ps1" not in props
    assert not (ROOT / "scripts/apply-recoverable-inject-v11.ps1").exists()


def test_qninject_defers_while_qianniu_runs_and_retries_without_killing_it():
    qn = read("src/Bot/Common/QNInject.cs")
    start = qn[qn.index("public static async Task StartInject()"):qn.index("private static string FindInstallPath()")]

    assert "TryRepairWhenQianniuClosedAsync" in start
    assert "IsWorkbenchRunning()" in qn
    assert "resourceOwnerProcessNames" in qn
    assert "KillWorkbenchProcesses" not in qn
    assert "Process.Kill()" not in qn
    assert "MessageBox.Show" not in start
    assert "repairGate.WaitAsync(0)" in qn
    assert "sign.json" in qn


def test_language_repair_preserves_live_qianniu_and_persistent_login_storage():
    language = read("src/Bot/Common/LanguageRepairService.cs")
    cache = language[language.index("private static void ClearQianniuLanguageCaches()"):language.index("private static List<string> CollectQianniuProfileRoots()")]

    assert "KillWorkbenchProcesses" not in language
    assert "Process.Kill()" not in language
    assert "ResourceOwnerProcessNames" in language
    assert "if (IsWorkbenchRunning())" in language
    assert 'new[] { "Cache", "Code Cache", "GPUCache" }' in cache
    assert 'cacheNames.Add("Local Storage")' not in cache
    assert "preserved=LocalStorage/Cookies/IndexedDB/Login" in cache
