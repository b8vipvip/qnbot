from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "src" / "Bot" / "ChromeNs" / "ManualRechargeAssistantService.cs"
VISUAL = ROOT / "src" / "Bot" / "ChromeNs" / "RecentVisualContextService.cs"
COORDINATOR = ROOT / "src" / "Bot" / "ChromeNs" / "BuyerMessageBurstCoordinator.cs"
TARGETS = ROOT / "src" / "Directory.Build.targets"
SERVER = ROOT / "services" / "api-control-plane" / "manual_recharge.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
INDEX = ROOT / "services" / "api-control-plane" / "static" / "index.html"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "manual-recharge.html"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_manual_recharge_is_consumed_before_normal_ai_only_when_server_enabled():
    service = read(SERVICE)
    coordinator = read(COORDINATOR)
    assert '"/api/runtime/v1/manual-recharge/config"' in service
    assert "if (!await IsServerEnabledAsync" in service
    call = coordinator.index("ManualRechargeAssistantService.TryHandleAsync")
    off_hours = coordinator.index("TryResolveOffHours", call)
    local_reply = coordinator.index("LocalShortReplyService.TryResolve", call)
    assert call < off_hours < local_reply
    assert "return CanonicalPreMergeOutcome.Consumed" in coordinator[call:off_hours]


def test_start_requires_same_conversation_redeem_code_consent_and_recent_visual_account():
    service = read(SERVICE)
    visual = read(VISUAL)
    assert "ConversationContextStore.GetRecentTurns(seller, buyer" in service
    assert 'x.Role == "assistant"' in service
    assert '@"(?:会员)?兑换码\\s*[:：]' in service
    assert "HasExplicitConsent(text)" in service
    assert "RecentVisualContextService.TryGetRecentKugouAccountEvidence" in service
    assert "HasKugouOfficialAppEvidence(combined)" in visual
    assert "KugouNicknameRegex" in visual
    assert "KugouUserIdRegex" in visual


def test_sensitive_phone_and_code_are_memory_only_and_expire():
    service = read(SERVICE)
    assert "ConcurrentDictionary<string, Session> Sessions" in service
    assert "StateTtl = TimeSpan.FromMinutes(12)" in service
    assert "state.Phone = phone" in service
    assert "state.Code = code" in service
    assert "PersistentParams" in service
    assert 'PersistentParams.GetParam2Key(\n                "ControlPlaneUrl"' in service
    assert 'PersistentParams.GetParam2Key(\n                "ControlPlaneClientToken"' in service
    assert 'PersistentParams.Set' not in service
    assert "state.Phone = string.Empty" in service
    assert "state.Code = string.Empty" in service


def test_account_matching_is_exact_and_never_defaults_to_first_candidate():
    service = read(SERVICE)
    assert "FindExactMatches" in service
    assert "NormalizeAccount(x.UserId) == userId" in service
    assert "NormalizeAccount(x.Nickname) == nickname" in service
    assert "if (matches.Count == 1)" in service
    assert "state.Stage = Stage.AwaitAccountSelection" in service
    assert "accounts[0]" not in service
    assert "1确认充值" in service
    assert "IsFinalConfirmation" in service


def test_final_submission_has_local_two_minute_gate_and_server_idempotency():
    service = read(SERVICE)
    server = read(SERVER)
    assert "RecentSubmissions" in service
    assert "DateTime.UtcNow.AddMinutes(2)" in service
    assert '"/api/runtime/v1/manual-recharge/submit"' in service
    assert '["operation_id"] = state.OperationId' in service
    assert 'operation_id + "-duplicate-final"' in server
    assert 'operation_id + "-recharge"' in server
    submit = server.index("def runtime_manual_recharge_submit")
    duplicate = server.index('operation_id + "-duplicate-final"', submit)
    recharge = server.index('operation_id + "-recharge"', submit)
    assert submit < duplicate < recharge


def test_failures_stop_and_handoff_without_sensitive_values():
    service = read(SERVICE)
    assert "StopAndHandoffAsync" in service
    assert "RedactSensitive" in service
    assert "orderSuffix=" in service
    assert "OrderSuffix" in service
    assert "人工代充安全转人工" in service
    assert "WeComAppBridgeClient.SendNotificationAsync" in service
    notify = service.index("WeComAppBridgeClient.SendNotificationAsync")
    block = service[notify - 1000 : notify + 700]
    assert "state.Phone" not in block
    assert "state.Code" not in block
    assert '"；兑换码尾号=" + OrderSuffix' in block


def test_control_plane_is_fixed_path_https_whitelist_and_client_cannot_supply_url():
    server = read(SERVER)
    assert 'parsed.scheme.lower() != "https"' in server
    assert "ALLOWED_PATHS" in server
    assert '"check_order": "/check_order_validity"' in server
    assert '"send_interval": "/check_send_interval"' in server
    assert '"send_code": "/sfyzm"' in server
    assert '"accounts": "/submit"' in server
    assert '"duplicate": "/check_recharge_duplicate"' in server
    assert '"recharge": "/api"' in server
    assert "base_url: str" in server
    assert "class ManualRechargeSettingsInput" in server
    runtime_models = server[server.index("class OrderInput") : server.index("def init_manual_recharge_db")]
    assert "base_url" not in runtime_models
    assert "url:" not in runtime_models


def test_module_is_packaged_and_admin_switch_is_visible():
    targets = read(TARGETS)
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    index = read(INDEX)
    page = read(PAGE)
    assert "ManualRechargeAssistantService.cs" in targets
    assert "import manual_recharge" in bootstrap
    assert "include_router(manual_recharge.router)" in bootstrap
    assert "init_manual_recharge_db()" in bootstrap
    assert "manual_recharge.py" in dockerfile
    assert "/static/manual-recharge.html" in index
    assert "该功能默认关闭" in page
    assert "固定 HTTPS 白名单根地址" in page
    assert "/api/admin/manual-recharge/settings" in page
