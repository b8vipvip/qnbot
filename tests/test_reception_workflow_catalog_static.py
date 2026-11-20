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
    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in s
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in s
    assert "RestoreLatestDraft(" in s
    assert "SendTextWithRetryAsync" not in s
    assert "SubmitRecharge" not in s

def test_workflow_draft_path_respects_exact_current_shop_scope():
    s = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    resolver = s[s.index("private static string PathForShop"):s.index("private static JObject Default")]
    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in resolver
    assert "ShopSettingsScope.Enter(shop)" in resolver


def test_generic_workflow_editor_exposes_draft_only_navigation():
    ui = source("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    page = source("src/Bot/Knowledge/KnowledgeV2WorkflowDraftPage.cs")
    build = source("src/Directory.Build.targets")
    assert 'Nav("通用流程草稿", () => new KnowledgeV2WorkflowDraftPage(_seller))' in ui
    assert "ReceptionWorkflowCatalogService.SaveDraft(" in page
    assert "ReceptionWorkflowCatalogService.RestoreLatestDraft(" in page
    assert "ReceptionWorkflowCatalogService.cs" in build
    assert "KnowledgeV2WorkflowDraftPage.cs" in build

def test_workflow_restore_skips_corrupt_newest_backup_and_keeps_draft_fail_closed():
    s = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    restore = s[
        s.index("public static string RestoreLatestDraft"):
        s.index("\n    }\n}")
    ]
    assert "OrderByDescending(x => x, StringComparer.Ordinal).ToList()" in restore
    assert "foreach (var candidate in backups)" in restore
    assert "Validate(File.ReadAllText(candidate, Encoding.UTF8))" in restore
    assert "没有找到有效的流程草稿备份，当前草稿未修改" in restore
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(path, validated, 3, 50, out error)" in restore
    assert "仍需单独人工审核和灰度激活" in restore

def test_workflow_draft_backup_names_are_unique_under_rapid_repeated_saves():
    s = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    save = s[
        s.index("public static string SaveDraft"):
        s.index("public static string RestoreLatestDraft")
    ]
    assert 'path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")' in save
    assert 'Guid.NewGuid().ToString("N")' in save
    assert 'File.Copy(path, path + ".backup."' in save





def test_workflow_draft_storage_requires_stable_shop_identity_and_shopkey():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    path = service[
        service.index("private static string PathForShop"):
        service.index("private static JObject Default")
    ]

    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in path
    assert "string.IsNullOrWhiteSpace(shop.ShopKey)" in path
    assert "!shop.HasStableSellerId" in path
    assert "不允许保存流程草稿" in path


def test_workflow_steps_require_nonempty_unique_evidence_contracts():
    s = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    validation = s[
        s.index("private static JObject Validate"):
        s.index("private static bool ValidKey")
    ]
    assert "required.Count < 1 || required.Count > 10" in validation
    assert "new HashSet<string>(StringComparer.Ordinal)" in validation
    assert "evidenceKinds.Add(Convert.ToString(kind))" in validation
    assert "同一步骤不允许重复声明相同证据类型" in validation



def test_status_document_distinguishes_draft_catalog_from_missing_approval_publish_and_rollback():
    status = source("docs/RECEPTION_WORKFLOW_STATUS.md")

    assert "privacy-minimized, cross-restart evidence ledger" in status
    assert "versioned workflow draft catalog" in status
    assert "Every saved flow remains `draft`" in status
    assert "trusted operator identity and authorization source" in status
    assert "active-version pointer" in status
    assert "atomic rollback" in status
    assert "must remain draft-only" in status
