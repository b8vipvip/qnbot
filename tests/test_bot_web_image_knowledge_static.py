from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_web_image_knowledge_api_is_authenticated_shop_client_scoped_and_uses_server_ocr():
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    assert '@router.post("/api/bot-web/image-knowledge/recognize")' in api
    assert '@router.get("/api/bot-web/image-knowledge")' in api
    assert '@router.post("/api/bot-web/image-knowledge")' in api
    assert '@router.delete("/api/bot-web/image-knowledge/{image_id}")' in api
    assert 'Depends(core._web_client)' in api
    assert 'runtime_ocr._run_ocr' in api
    assert 'hashlib.sha256(raw).hexdigest()' in api
    assert 'UNIQUE(client_id, shop_key, image_sha256)' in api
    assert '8 * 1024 * 1024' in api


def test_web_image_knowledge_ui_supports_long_press_import_auto_ocr_and_editing():
    html = read("services/api-control-plane/static/bot-web.html")
    script = read("services/api-control-plane/static/bot-web-v2.js")
    assert 'id="importImageKnowledgeAction"' in html
    assert 'id="imageKnowledgeFile"' in html
    assert 'id="imageKnowledgeDialog"' in html
    assert 'id="imageKnowledgeOcr"' in html
    assert 'id="imageKnowledgeDescription"' in html
    assert 'id="addImageKnowledgeBtn"' in html
    assert '/api/bot-web/image-knowledge/recognize' in script
    assert '/api/bot-web/image-knowledge' in script
    assert 'prepareImageKnowledgeFile' in script
    assert 'loadImageKnowledge' in script
    assert 'importImageKnowledgeAction' in script
    assert 'async function normalizeImageKnowledgeFile(file)' in script
    assert 'createImageBitmap(file)' in script
    assert 'canvas.toBlob(resolve,"image/png")' in script
    assert 'await normalizeImageKnowledgeFile(file)' in script
    assert '25000000' in script
    assert 'image/bmp,image/heic,image/heif,.bmp,.heic,.heif' in html
