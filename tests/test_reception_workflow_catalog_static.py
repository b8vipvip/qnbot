"""Generic reception workflow draft schema must be safe and shop-scoped."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_generic_flow_catalog_does_not_publish_or_execute_operations():
    s = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    assert 'qnbot.reception-workflow-drafts.v1' in s
    assert 'Convert.ToString(flow["status"]) != "draft"' in s
    assert '"WAIT", "ASK_EVIDENCE", "HANDOFF", "READ_ONLY_TOOL", "REPLY_REVIEWED"' in s
    assert 'order_snapshot' in s and 'image_message' in s and 'manual_correction' in s
    assert 'next_state' in s and 'timeout_minutes' in s
    assert 'Path.Combine(dir, "reception-workflow-drafts.json")' in s
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in s
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in s
    assert "RestoreLatestDraft(" in s
    assert "SendTextWithRetryAsync" not in s
    assert "SubmitRecharge" not in s

def test_generic_workflow_editor_exposes_draft_only_navigation():
    ui = source("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    page = source("src/Bot/Knowledge/KnowledgeV2WorkflowDraftPage.cs")
    build = source("src/Directory.Build.targets")
    assert 'Nav("通用流程草稿", () => new KnowledgeV2WorkflowDraftPage(_seller))' in ui
    assert "ReceptionWorkflowCatalogService.SaveDraft(" in page
    assert "ReceptionWorkflowCatalogService.RestoreLatestDraft(" in page
    assert "ReceptionWorkflowCatalogService.cs" in build
    assert "KnowledgeV2WorkflowDraftPage.cs" in build
