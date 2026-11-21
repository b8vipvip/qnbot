from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_v2_rejects_terminal_generation_before_answer_ready():
    source = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = source[source.index("internal static async Task<bool> TryHandleAsync"):source.index(
        "private static bool IsApprovedMatchForLogging"
    )]

    direct = method.index("decision == null || !decision.CanDirectReply")
    first_current = method.index("!lease.IsCurrent", direct)
    first_cancel = method.index("lease.CancellationToken.IsCancellationRequested", first_current)
    stable = method.index("lease.ConfirmStableAsync(80)", first_cancel)
    answer_ready = method.index("ResponseProgressTracker.SetAnswerReady(", stable)

    # Production regression from 1.1.1173: generation=1 was hard-cancelled at ~55s but the same
    # turn was later published as 本地知识V2 AnswerReady at ~113s. Lease validity must therefore
    # be proven before any progress-card Ready state or send-watchdog preparation can be triggered.
    assert direct < first_current < first_cancel < stable < answer_ready
    assert "Knowledge Engine V2迟到结果已丢弃" in method
    assert "Knowledge Engine V2发送前稳定性确认失败，未发布迟到答案" in method


def test_knowledge_v2_keeps_second_generation_barrier_immediately_before_send():
    source = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = source[source.index("internal static async Task<bool> TryHandleAsync"):source.index(
        "private static bool IsApprovedMatchForLogging"
    )]

    answer_ready = method.index("ResponseProgressTracker.SetAnswerReady(")
    send = method.index("qn.SendTextWithRetryAsync", answer_ready)
    between = method[answer_ready:send]

    assert "!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested" in between
    assert "ParallelReplyRelevanceGate.ShouldSend" in between
    assert between.index("!lease.IsCurrent") < between.index("ParallelReplyRelevanceGate.ShouldSend")
