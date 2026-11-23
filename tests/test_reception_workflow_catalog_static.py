"""Reception Agent autonomous publishing must remain shop-scoped and reversible."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_catalog_is_v2_shop_scoped_and_requires_stable_shop_identity():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    scope = service[
        service.index("private static ShopContext ResolveShopForSeller"):
        service.index("private static string PathForShop")
    ]

    assert 'qnbot.reception-workflows.v2' in service
    assert 'reception-workflow-catalog.json' in service
    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in scope
    assert "string.IsNullOrWhiteSpace(shop.ShopKey)" in scope
    assert "!shop.HasStableSellerId" in scope
    assert "ShopSettingsScope.Enter(shop)" in scope


def test_legacy_drafts_migrate_to_active_catalog_without_admin_approval():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    migration = service[
        service.index("private static void EnsureLegacyDraftMigrated"):
        service.index("public static string GetActiveJson")
    ]

    assert 'qnbot.reception-workflow-drafts.v1' in service
    assert 'reception-workflow-drafts.json' in service
    assert "Validate(legacyJson)" in migration
    assert 'migrated["publication_mode"] = "autonomous-migration"' in migration
    assert 'migrated["published_revision"] = newRevision' in migration
    assert "WriteCatalog(path, migrated)" in migration


def test_validated_agent_publish_is_immediate_and_has_no_admin_approval_gate():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    publish = service[
        service.index("public static string Publish"):
        service.index("public static string RollbackToPreviousPublished")
    ]

    assert "public static string Publish(string seller, string json)" in publish
    assert "var candidate = Validate(json)" in publish
    assert 'candidate["publication_mode"] = "autonomous"' in publish
    assert 'candidate["published_revision"] = nextRevision' in publish
    assert 'candidate["published_at_utc"]' in publish
    assert "WriteCatalog(path, candidate)" in publish
    assert "无需管理员审批" in service
    assert "approval_token" not in service.lower()
    assert "trusted_approver" not in service.lower()


def test_published_workflows_keep_high_risk_business_actions_out_of_catalog_authority():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    actions = service[
        service.index("private static readonly HashSet<string> AllowedActions"):
        service.index("private static readonly HashSet<string> AllowedEvidence")
    ]

    for allowed in ('"WAIT"', '"ASK_EVIDENCE"', '"HANDOFF"', '"READ_ONLY_TOOL"', '"REPLY"'):
        assert allowed in actions
    for forbidden in ('"PAYMENT"', '"RECHARGE"', '"REFUND"', '"FULFILL"', '"CHANGE_ACCOUNT"'):
        assert forbidden not in actions
    assert "付款、充值、退款、账号修改和实际履约动作不允许由流程目录授权" in service


def test_publish_uses_shop_scoped_cas_and_monotonic_flow_versions():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    publish = service[
        service.index("public static string Publish"):
        service.index("public static string RollbackToPreviousPublished")
    ]

    assert 'CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowPublish", 3000)' in publish
    assert "expectedRevision != currentRevision" in publish
    assert "currentRevision == long.MaxValue" in publish
    assert "SaveHistorySnapshot(shop, current)" in publish
    assert "BumpFlowVersions(candidate, current)" in publish
    assert 'candidate["catalog_revision"] = nextRevision' in publish
    assert "WriteCatalog(path, candidate)" in publish


def test_rollback_restores_previous_valid_published_snapshot_as_new_revision():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    rollback = service[
        service.index("public static string RollbackToPreviousPublished"):
        service.rindex("\n    }\n}")
    ]

    assert "public static string RollbackToPreviousPublished(string seller)" in rollback
    assert '"revision-*.json"' in rollback
    assert "OrderByDescending(item => item.Revision)" in rollback
    assert "Validate(contents)" in rollback
    assert "SaveHistorySnapshot(shop, current)" in rollback
    assert "BumpFlowVersions(restored, current)" in rollback
    assert 'restored["catalog_revision"] = restoredRevision' in rollback
    assert 'restored["publication_mode"] = "autonomous-rollback"' in rollback
    assert "WriteCatalog(path, restored)" in rollback
    assert "当前生效版本未修改" in rollback


def test_history_snapshots_are_immutable_and_keep_revision_identity():
    service = source("src/Bot/ChromeNs/ReceptionWorkflowCatalogService.cs")
    history_path = service[
        service.index("private static string HistoryPath"):
        service.index("private static void SaveHistorySnapshot")
    ]
    history = service[
        service.index("private static void SaveHistorySnapshot"):
        service.index("private static void WriteCatalog")
    ]

    assert '"revision-" + revision.ToString("D10"' in history_path
    assert "HistoryDirectoryForShop(shop)" in history
    assert "if (!IsMissing(path)) return;" in history
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in history


def test_reception_agent_ui_exposes_direct_publish_and_rollback():
    page = source("src/Bot/Knowledge/KnowledgeV2WorkflowDraftPage.cs")
    navigation = source("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    build = source("src/Directory.Build.targets")

    assert 'Text = "Reception Agent 接待流程"' in page
    assert 'Content = "校验并立即发布"' in page
    assert 'Content = "回滚上一发布版本"' in page
    assert "ReceptionWorkflowCatalogService.GetActiveJson(_seller)" in page
    assert "ReceptionWorkflowCatalogService.Publish(_seller, _editor.Text)" in page
    assert "ReceptionWorkflowCatalogService.RollbackToPreviousPublished(_seller)" in page
    assert 'Nav("Reception Agent 接待流程"' in navigation
    assert "ReceptionWorkflowCatalogService.cs" in build
    assert "KnowledgeV2WorkflowDraftPage.cs" in build


def test_status_document_distinguishes_autonomous_publication_from_business_authority():
    status = source("docs/RECEPTION_WORKFLOW_STATUS.md")

    assert "autonomous publication" in status.lower()
    assert "No administrator approval" in status
    assert "active-version pointer" in status
    assert "atomic rollback" in status
    assert "high-risk business actions" in status
    assert "runtime execution adapter" in status
