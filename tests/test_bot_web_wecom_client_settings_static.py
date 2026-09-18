from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/"services/api-control-plane/wecom_settings.py"

def text(): return SRC.read_text(encoding="utf-8-sig")

def test_bot_web_wecom_settings_are_client_scoped_and_secrets_masked():
    s=text()
    assert "CREATE TABLE IF NOT EXISTS wecom_client_settings" in s
    assert "client_id INTEGER PRIMARY KEY" in s
    assert 'WHERE client_id=?' in s
    assert '@router.get("/api/bot-web/wecom/settings")' in s
    assert '@router.put("/api/bot-web/wecom/settings")' in s
    assert "public_client_settings" in s
    assert '"app_secret_configured"' in s
    assert '"callback_token_configured"' in s
    assert '"callback_aes_key_configured"' in s

def test_each_client_gets_unique_callback_path_and_encrypted_secrets():
    s=text()
    assert '"/api/wecom/callback/"+str(int(client_id))' in s
    assert "encrypt_secret(app_secret)" in s
    assert "encrypt_secret(callback_token)" in s
    assert "encrypt_secret(callback_aes_key)" in s
    assert '@router.post("/api/bot-web/wecom/generate-callback")' in s
