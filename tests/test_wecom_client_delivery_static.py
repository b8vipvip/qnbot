from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/"services/api-control-plane/wecom_bridge.py"
def text(): return SRC.read_text(encoding="utf-8-sig")
def test_runtime_notifications_use_authenticated_client_wecom_settings():
    s=text()
    assert "client_wecom_settings(int(client[\"id\"]))" in s
    assert "client_bridge_configured(settings)" in s
    assert "send_app_text_for(settings, recipients" in s
    assert 'settings.get("ticket_hours")' in s
    assert 'settings.get("to_users")' in s
def test_runtime_capabilities_are_client_specific():
    s=text()
    assert "public_client_settings(int(client[\"id\"]),settings)" in s
    assert '"enabled": bool(public["outbound_configured"])' in s
    assert '"callback_enabled": bool(public["callback_configured"])' in s
    assert '"callback_url": public["callback_url"]' in s
