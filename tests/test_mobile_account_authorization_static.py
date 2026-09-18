from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_mobile_account_authorization_is_read_only_and_client_scoped():
    runtime = (ROOT / "src/Bot/ChromeNs/BotWebConsoleSyncService.cs").read_text(encoding="utf-8-sig")
    html = (ROOT / "services/api-control-plane/static/bot-web.html").read_text(encoding="utf-8")
    js = (ROOT / "services/api-control-plane/static/bot-web-v2.js").read_text(encoding="utf-8")

    assert '["authorization"] = authorization' in runtime
    assert '["control_scope"] = "current_shop_only"' in runtime
    assert '["client_token_bound"] = connection.TokenExists' in runtime
    assert '["credentials_location"] = "windows_local_only"' in runtime
    assert '["sensitive_credentials_exposed"] = false' in runtime
    assert '["seller_accounts"] = new JArray(sellers)' in runtime

    assert 'id="authorizationInfo"' in html
    assert 'id="authorizationBadge"' in html
    assert "只展示当前 Bot 客户端绑定店铺" in html
    assert "客户端令牌、Cookie、密码及其他凭据不会在手机端回显" in html

    assert "s.status.authorization||{}" in js
    assert "仅 Windows 本机保存，不回显" in js
    assert "auth.client_token_bound" in js
    assert "auth.control_plane_configured" in js


def test_authorization_snapshot_never_serializes_token_or_server_url():
    runtime = (ROOT / "src/Bot/ChromeNs/BotWebConsoleSyncService.cs").read_text(encoding="utf-8-sig")
    start = runtime.index("var authorization = new JObject")
    end = runtime.index("return new JObject", start)
    block = runtime[start:end]

    forbidden = [
        '["token"]',
        '["api_key"]',
        '["cookie"]',
        '["password"]',
        '["server_url"]',
        "GetToken(",
        "TryGetToken(",
    ]
    for marker in forbidden:
        assert marker not in block
