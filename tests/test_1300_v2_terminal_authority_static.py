from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_stability_barrier_is_read_only_and_ready_is_materialization_owned():
    burst = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    streaming = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    start = burst.index("public async Task<bool> ConfirmStableAsync")
    end = burst.index("public bool MarkProcessing", start)
    stable = burst[start:end]
    assert 'MarkReady("send_barrier_stable")' not in stable
    assert "return IsCurrent && !CancellationToken.IsCancellationRequested;" in stable
    assert 'lease.MarkReady("streaming_answer_materialized")' in streaming


def test_dedup_hidden_ai_respects_generation_cancellation():
    source = text("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    assert "CancellationToken cancellationToken" in source
    assert "bool preserveTrustedAnswer" in source
    assert "cancellationToken.ThrowIfCancellationRequested();" in source
    assert "messages, 220, 0.10, 45, cancellationToken" in source
    assert "messages, 180, 0.35, 90, cancellationToken" in source
    assert source.count("catch (OperationCanceledException)") >= 2


def test_v2_uses_session_agent_as_only_terminal_authority():
    source = text("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "MaxDirectReplyAgeSeconds" not in source
    assert 'lease.MarkReady("knowledge_v2_answer_materialized")' in source
    assert 'lease.MarkSending("knowledge_v2_send_started")' in source
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in source
    assert "true);" in source[source.index("ReplyDeduplicationService.EnsureDistinct"):source.index("ReplyDeduplicationService.EnsureDistinct") + 500]
    assert 'lease.MarkCompleted("knowledge_v2_send_completed")' in source
    assert 'lease.MarkFailed("knowledge_v2_send_failed")' in source


def test_recovery_reports_only_real_buyer_business_ingress():
    source = text("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    region = source[source.index("internal async Task<int> ReconcileActiveConversationIngressAsync"):]
    assert "if (IsBuyerMessage(message)) processed++;" in region
    assert "processed++;\n" not in region.replace("if (IsBuyerMessage(message)) processed++;\n", "")
