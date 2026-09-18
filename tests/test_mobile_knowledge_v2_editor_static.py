from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
HTML=ROOT/"services/api-control-plane/static/bot-web.html"
JS=ROOT/"services/api-control-plane/static/bot-web-v2.js"

def read(p): return p.read_text(encoding="utf-8-sig")

def test_mobile_exposes_native_knowledge_v2_editor():
    h=read(HTML); s=read(JS)
    assert "Knowledge Center V2" in h
    assert 'id="addKnowledgeV2Btn"' in h
    assert 'id="knowledgeV2List"' in h
    assert 'id="knowledgeV2Dialog"' in h
    for field in ("knowledgeV2Type","knowledgeV2Intent","knowledgeV2Subject","knowledgeV2Predicate","knowledgeV2Entities","knowledgeV2Aliases","knowledgeV2Conditions","knowledgeV2Exclusions","knowledgeV2RequiredContext","knowledgeV2ProductIds","knowledgeV2Risk"):
        assert f'id="{field}"' in h
    assert 'src="/static/bot-web-v2.js?v=10"' in h
    assert 'api("/api/bot-web/knowledge-v2")' in s
    assert 'method:"PUT"' in s
    assert "SourceType:old.SourceType||\"web_manual\"" in s
    assert "CreatedAt:old.CreatedAt||now" in s
    assert "UpdatedAt:now" in s
    assert "confirm(\"确定删除这条 Knowledge V2 知识吗？" in s

def test_mobile_v2_save_preserves_governance_counters_and_structured_policy_fields():
    s=read(JS)
    for field in ("Authority","Confidence","UseCount","AcceptedCount","CorrectionCount","WithdrawCount","Conditions","Exclusions","RequiredContext","ProductIds","RiskLevel","Enabled","Status"):
        assert field+":" in s
    assert "state.knowledgeV2.filter(x=>x.Id!==id)" in s
    assert "等待 Windows 同步" in s
