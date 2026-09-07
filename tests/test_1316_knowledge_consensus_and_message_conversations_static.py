from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_knowledge_v2_safe_consensus_only_relaxes_duplicate_margin():
    source = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs").read_text(encoding="utf-8-sig")

    assert "HasSafeDirectConsensus" in source
    assert "consensus=safe" in source
    assert "best.Score >= threshold" in source
    assert "best.ConfidenceScore >= minConfidence" in source
    assert "!highRisk" in source
    assert "!decision.HasConflict" in source
    assert "KnowledgeEngineV2Semantics.IsHighRisk(candidate.Record.Answer)" in source
    assert "HasAnswerPolarityConflict" in source
    assert "query.ContextDependent" in source
    assert "Math.Max(margin, 0.12)" in source


def test_message_trace_console_uses_grouped_conversations_and_detail_export():
    api = (ROOT / "services/api-control-plane/message_processing_traces.py").read_text(encoding="utf-8-sig")
    ui = (ROOT / "services/api-control-plane/static/message-traces.js").read_text(encoding="utf-8-sig")

    assert '/api/admin/message-processing-conversations"' in api
    assert '/api/admin/message-processing-conversations/detail"' in api
    assert '/api/admin/message-processing-conversations/export"' in api
    assert "GROUP BY t.client_id, t.shop_key, t.seller, t.buyer, conversation_date" in api
    assert '"2h": timedelta(hours=2)' in api
    assert '"6h": timedelta(hours=6)' in api
    assert '"14d": timedelta(days=14)' in api
    assert "text/csv; charset=utf-8" in api

    assert "/api/admin/message-processing-conversations?" in ui
    assert "viewMessageConversation" in ui
    assert ">查看</button>" in ui
    assert "导出所有买家" in ui
    assert 'option value="2h"' in ui
    assert 'option value="6h"' in ui
    assert 'option value="1d" selected' in ui
