from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
HTML=ROOT/"services/api-control-plane/static/bot-web.html"
JS=ROOT/"services/api-control-plane/static/bot-web-bot-enabled.js"
SERVER=ROOT/"services/api-control-plane/wecom_settings.py"
def read(p): return p.read_text(encoding="utf-8-sig")
def test_mobile_wecom_panel_masks_secrets_and_requires_confirmations():
    h=read(HTML); s=read(JS)
    assert 'id="wecomEnabled"' in h and 'id="wecomStatusBadge"' in h
    assert 'type="password"' in h
    assert 'id="wecomCallbackUrl" readonly' in h
    assert 'src="/static/bot-web-bot-enabled.js?v=5"' in h
    assert 'api("/api/bot-web/wecom/settings"' in s
    assert 'api("/api/bot-web/wecom/test"' in s
    assert s.count("confirm(")>=3
    assert "app_secret_configured" in s and "callback_token_configured" in s
def test_client_test_endpoint_uses_scoped_settings():
    s=read(SERVER)
    assert '@router.post("/api/bot-web/wecom/test")' in s
    assert "load_client_settings(int(client[\"id\"]))" in s
    assert "send_app_text_for(settings" in s
