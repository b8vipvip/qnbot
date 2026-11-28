from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_handoff_policy_is_local_and_no_longer_polls_server():
    source = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    assert "handoff-policy.json" in source
    assert "HandoffPolicyUiBridge.Initialize" in source
    assert "BulkListManagementUi.Initialize" in source
    assert "/api/runtime/v1/handoff/rules" not in source
    assert "PollLoopAsync" not in source
    assert "HttpClient" not in source
    assert "不再访问服务端规则接口" in source
    assert "Paths.GetRulesRoot(shop)" in source


def test_legacy_server_rules_are_migrated_once_per_shop_then_marked_complete():
    migration = read("src/Bot/ChromeNs/HandoffPolicyLegacyMigrationService.cs")
    bridge = read("src/Bot/ChromeNs/BulkListManagementUiBridge.cs")
    props = read("src/Bot/Directory.Build.props")
    assert "HandoffPolicyLegacyMigrationService.StartOnce" in bridge
    assert "/api/runtime/v1/handoff/rules" in migration
    assert "handoff-policy-server-migration.json" in migration
    assert "ShopControlPlaneConnectionStore" in migration
    assert '"X-Shop-Key"' in migration
    assert "Paths.GetStateRoot(shop)" in migration
    assert "HandoffRuleRemoteConfigService.SaveRules(rules)" in migration
    assert "Task.Run" in migration
    assert "while (true)" not in migration
    assert "PollLoopAsync" not in migration
    assert "ChromeNs\\HandoffPolicyLegacyMigrationService.cs" in props


def test_notification_tab_gets_local_policy_button_and_manager():
    source = read("src/Bot/Options/HandoffPolicyUi.cs")
    assert 'FindText(window, "转人工通知")' in source
    assert 'Content = "通知策略"' in source
    assert "AI转人工通知策略（本机）" in source
    assert "覆盖全部" in source
    assert "合并更新" in source
    assert "仅追加" in source
    for label in ("全选", "取消全选", "删除所选", "清空全部", "导入", "导出"):
        assert label in source


def test_all_knowledge_rule_lists_have_bulk_management_controls():
    records = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    io = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    handoff = read("src/Bot/Options/HandoffPolicyUi.cs")
    assert not (ROOT / "src/Bot/Knowledge/BulkListManagementUi.cs").exists()
    assert "全选当前" in records and "删除所选" in records
    assert "SelectionMode = DataGridSelectionMode.Extended" in records
    assert "TryDeleteEditorRecords" in records
    for mode in ("replace", "merge", "append"):
        assert f'ImportPackage("{mode}")' in io
    assert "CreateAutomaticBackup(before)" in io
    assert "TryReplaceAllIfUnchanged" in io
    assert "FeedbackEvents" in io
    for label in ("全选", "取消全选", "删除所选", "清空全部"):
        assert label in handoff


def test_new_windows_files_are_included_in_all_wpf_builds():
    props = read("src/Bot/Directory.Build.props")
    assert "Knowledge\\KnowledgeCenterV2RecordsPage.cs" in props
    assert "ChromeNs\\BulkListManagementUiBridge.cs" in props
    assert "Options\\HandoffPolicyUi.cs" in props
    assert "ChromeNs\\HandoffPolicyLegacyMigrationService.cs" in props


def test_handoff_notification_still_applies_local_safe_exceptions():
    source = read("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "HandoffRuleRemoteConfigService.TryApplySafeAutoReply" in source
    assert "ShopSettingsScope.Enter(shop)" in source


def test_server_no_longer_registers_or_packages_handoff_policy_engine():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    migration = read("services/api-control-plane/wecom_policy_migration.py")
    assert "wecom_policy_migration.install(control_plane)" in bootstrap
    assert "import wecom_handoff_policy" not in bootstrap
    assert "wecom_handoff_policy.router" not in bootstrap
    assert "init_handoff_policy_db" not in bootstrap
    assert "wecom_policy_migration.py" in dockerfile
    assert "wecom_handoff_policy.py" not in dockerfile
    assert 'path == "/api/runtime/v1/handoff/rules" and method == "GET"' in migration
    assert "return await call_next(request)" in migration
