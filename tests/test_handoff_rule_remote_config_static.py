from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_account_purchase_exception_does_not_create_handoff_ticket():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    notification = read("src/Bot/ChromeNs/HandoffNotificationService.cs")

    assert "另一个账号" in service
    assert "给朋友" in service
    assert "再拍" in service
    assert "月卡" in service
    assert "可以的，月卡可以给朋友或其他账号充值" in service
    assert "TryApplySafeAutoReply" in notification
    assert notification.index("TryApplySafeAutoReply") < notification.index("EnableHandoffNotification")
    assert "return;" in notification[notification.index("TryApplySafeAutoReply"):notification.index("var cfg")]


def test_account_security_terms_still_take_priority_over_purchase_exceptions():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")

    for term in ["密码", "验证码", "找回", "被盗", "绑定", "实名"]:
        assert term in service
    risk = service.index("if (contextual && hasRisk)")
    exception = service.index("if (hasException)", risk)
    assert risk < exception
    assert "UpdateReason(decision, rule)" in service[risk:exception]


def test_local_rules_replace_server_polling_sync_keywords_and_use_shop_path_cache():
    service = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")

    assert "handoff-policy.json" in service
    assert "GetState(true)" in service
    assert "EnsurePolicyFile(path)" in service
    assert "SyncKeywordsToLocalConfig" in service
    assert "cfg.ManualKeywords = manual" in service
    assert "cfg.NoAutoReplyKeywords = confirm" in service
    assert "BotFeatureStore.SaveAutoReplyRules(cfg)" in service
    assert "Paths.GetRulesRoot(shop)" in service
    assert "ConcurrentDictionary<string, RuleState>" in service
    assert "CanAutoAdoptLegacy" in service
    assert "/api/runtime/v1/handoff/rules" not in service
    assert "HandoffRemoteRulesJson" not in service
    assert "PollLoopAsync" not in service
    assert "HttpClient" not in service


def test_local_rule_service_is_initialized_and_compiled():
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")
    props = read("src/Bot/Directory.Build.props")

    assert "HandoffRuleRemoteConfigService.Initialize();" in app
    assert "ChromeNs\\HandoffRuleRemoteConfigService.cs" in targets
    assert "Options\\HandoffPolicyUi.cs" in props
    assert "Knowledge\\KnowledgeCenterV2RecordsPage.cs" in props
    assert "ChromeNs\\BulkListManagementUiBridge.cs" in props
    assert "ChromeNs\\HandoffPolicyLegacyMigrationService.cs" in props


def test_wecom_page_policy_is_retired_and_migrated_to_shop_scoped_windows_client():
    page = read("services/api-control-plane/static/wecom.html")
    migration = read("services/api-control-plane/wecom_policy_migration.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    client_migration = read("src/Bot/ChromeNs/HandoffPolicyLegacyMigrationService.cs")

    assert "AI 转人工策略" in page
    assert ".panel:has(#policyText)" in migration
    assert "handoffPolicyMigrationNotice" in migration
    assert "功能设置 → 消息通知 → 转人工通知 → 通知策略" in migration
    assert 'path == "/api/runtime/v1/handoff/rules" and method == "GET"' in migration
    assert "return await call_next(request)" in migration
    assert "status_code=410" in migration
    assert "/api/runtime/v1/handoff/rules" in client_migration
    assert "handoff-policy-server-migration.json" in client_migration
    assert "ShopControlPlaneConnectionStore" in client_migration
    assert '"X-Shop-Key"' in client_migration
    assert "wecom_policy_migration.install(control_plane)" in bootstrap
    assert "include_router(wecom_handoff_policy.router)" not in bootstrap
    assert "init_handoff_policy_db" not in bootstrap
    assert "wecom_policy_migration.py" in dockerfile
    assert "wecom_handoff_policy.py" not in dockerfile
