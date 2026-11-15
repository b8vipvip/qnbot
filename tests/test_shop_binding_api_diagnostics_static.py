from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_binding_exposes_api_and_ai_answer_chain_diagnostics():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert 'MakeButton("测试 API 连接"' in ui
    assert 'MakeButton("测试 AI 回答链路"' in ui
    assert "RunDiagnosticAsync(false)" in ui
    assert "RunDiagnosticAsync(true)" in ui
    assert "GetSavedTokenForDiagnostics" in ui
    assert "尚未保存的内容" in ui
    assert "当前千牛会话真实发送" in ui or "正式千牛发送链路" in ui
    assert "可手动撤回" in ui
    assert "ShopApiDiagnosticsService.TestConnectionAsync" in ui
    assert "ShopApiDiagnosticsService.TestAnswerChainAsync(_shop, _seller" in ui


def test_diagnostics_use_shop_auth_and_the_real_text_default_gateway():
    service = read("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
    assert '"/api/runtime/v1/config"' in service
    assert '"/v1/chat/completions"' in service
    assert 'new AuthenticationHeaderValue("Bearer", token.Trim())' in service
    assert '"X-Shop-Key", shop.ShopKey' in service
    assert '["model"] = "text-default"' in service
    assert 'root["qianniu_routing"]' in service
    assert 'root["choices"]' in service
    assert 'message["content"]' in service
    assert "供应商/模型/协议" in service
    assert "AI实际回复" in service


def test_ai_chain_diagnostic_sends_to_current_buyer_through_production_path():
    service = read("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
    assert "ShopContextLocator.FindConnectedQnByShopKey(shop.ShopKey)" in service
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in service
    assert "resolvedShopKey, shop.ShopKey" in service
    assert "qn.GetCurrentConversationID()" in service
    assert '"【Bot链路测试，可手动撤回】"' in service
    assert "KnowledgeLearningService.AllowNextManualSend" in service
    assert "ShopSettingsScope.Enter(shop)" in service
    assert "qn.SendTextWithRetryAsync(buyer, sendText, 1)" in service
    assert "qn.Rpa.GetSendFailureReason()" in service
    assert "阶段6/6 千牛真实发送：通过" in service
    assert "生产 SendTextWithRetryAsync 已确认成功" in service


def test_diagnostics_service_is_compiled_for_wpf_temp_projects():
    props = read("src/Bot/Directory.Build.props")
    assert "ShopScope\\ShopApiDiagnosticsService.cs" in props
