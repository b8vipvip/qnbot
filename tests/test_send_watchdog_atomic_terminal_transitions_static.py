from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_each_pending_delivery_has_one_terminalization_lock():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    pending = source.split("private sealed class PendingDelivery", 1)[1].split(
        "public static void OnBuyerMessageObserved", 1
    )[0]
    assert "public readonly object Sync = new object();" in pending
    assert "public int Terminal;" in pending


def test_submission_evidence_and_timeout_share_the_same_terminal_lock():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    submission = source.split("public static bool MarkSubmissionAccepted", 1)[1].split(
        "public static int CancelPending", 1
    )[0]
    timeout = source.split("await Task.Delay(VerifyDelayMilliseconds);", 1)[1].split(
        "public static bool ConfirmDelivery", 1
    )[0]
    assert "lock (pending.Sync)" in submission
    assert "pending.Terminal != 0" in submission
    assert "Pending.TryGetValue(pending.Id, out current)" in submission
    assert "acceptedCount == 0" in submission
    assert "lock (pending.Sync)" in timeout
    assert "Pending.TryRemove(pending.Id, out removed)" in timeout
    assert "pending.Terminal = 1;" in timeout
    assert "submissionTicks = Interlocked.Read(ref pending.SubmissionAcceptedTicks);" in timeout
    assert "supersededByManual =" in timeout


def test_echo_manual_takeover_and_cancel_cannot_mutate_a_terminal_delivery():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    for start, end in (
        ("public static int CancelPending", "public static int MarkManualTakeover"),
        ("public static int MarkManualTakeover", "public static int CancelConversation"),
        ("public static int CancelConversation", "private static PendingDelivery FindPending"),
        ("public static bool ConfirmDelivery", "public static bool IsKnownBotAnswer"),
    ):
        method = source.split(start, 1)[1].split(end, 1)[0]
        assert "lock (pending.Sync)" in method
        assert "pending.Terminal != 0" in method
        assert "Pending.TryGetValue(" in method
        assert "pending.Terminal = 1;" in method or "SupersededByManual" in method
