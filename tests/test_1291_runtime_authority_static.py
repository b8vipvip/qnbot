from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_streaming_generation_uses_session_agent_token_directly():
    s = source("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "CreateLinkedTokenSource(lease.CancellationToken)" in s
    assert "MonitorLeaseAsync(" not in s
    assert "monitorCts" not in s
    assert "await response.Content.ReadAsStringAsync();" not in s
    assert "ReadResponseBodyAsync(response.Content, linked.Token)" in s


def test_buyer_alias_canonicalizes_qianniu_transport_prefix_in_one_place():
    s = source("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    assert 'CnTaobaoTransportPrefix = "cntaobao"' in s
    assert "CanonicalIdentity(left)" in s
    assert "CanonicalIdentity(right)" in s
    assert "StartsWith(CnTaobaoTransportPrefix" in s


def test_recovery_paths_share_one_source_replay_ledger():
    bulk = source("src/Bot/ChromeNs/BulkListManagementUiBridge.cs")
    active = source("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    assert "internal static class ConversationIngressRecoveryLedger" in bulk
    assert "ClaimRecoveryMessages(qn, unreadMessages)" in bulk
    assert "ClaimRecoveryMessages(qn, recent)" in bulk
    assert "ConversationIngressRecoveryLedger.TryClaim(seller, message" in active
    assert "return processed;" in active
    assert "已回灌新增候选消息" in bulk
