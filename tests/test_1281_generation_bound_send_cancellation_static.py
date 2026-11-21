from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_reliable_send_has_backward_compatible_cancellation_aware_overload():
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "return SendTextWithRetryAsync(buyer, text, retryCount, CancellationToken.None);" in qn
    assert "CancellationToken cancellationToken" in qn
    assert "await _sendGate.WaitAsync(cancellationToken);" in qn
    assert "await Task.Delay(1800, cancellationToken);" in qn
    assert "EnsureActiveBuyerForSendAsync(buyer, cancellationToken)" in qn
    assert "Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs), cancellationToken)" in qn


def test_reliable_send_checks_cancellation_before_real_ui_side_effects():
    qn = read("src/Bot/ChromeNs/QN.cs")
    confirm = qn.split("private async Task<bool> EnsureActiveBuyerForSendAsync", 1)[1]
    open_positions = [i for i in range(len(confirm)) if confirm.startswith("OpenChat(buyer);", i)]
    assert len(open_positions) == 2
    for position in open_positions:
        branch = confirm[:position]
        branch_start = branch.rfind("if (attempt == 0)")
        assert branch_start >= 0
        assert branch.rfind("cancellationToken.ThrowIfCancellationRequested();") > branch_start
        assert branch.rfind("IsCurrentShopScopeValidForSend(out shopScopeReason)") > branch_start
    send = qn.split("public async Task<bool> SendTextWithRetryAsync(", 1)[1]
    assert send.count("cancellationToken.ThrowIfCancellationRequested();") >= 3
    assert "Task.Delay(120, cancellationToken)" in qn


def test_premerge_fixed_reply_passes_exact_generation_token_without_marking_sending():
    fixed = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    region = fixed.split("private static async Task<bool> SendFixedAsync", 1)[1].split("public static async Task<bool> TryHandleAsync", 1)[0]
    assert "GetCancellationToken(item.SellerNick, item.BuyerNick, item.SessionGeneration, item.ShopKey)" in region
    assert "SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)" in region
    assert "catch (OperationCanceledException)" in region
    assert "BuyerSessionAgentState.Sending" not in region


def test_streaming_send_is_cancelled_by_lease_generation_token():
    stream = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in stream
    assert "Smart Reply发送期间generation硬失效" in stream
    assert "catch (OperationCanceledException)" in stream
