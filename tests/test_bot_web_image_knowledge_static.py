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
    assert 'async function sniffImageKnowledgeMime(file)' in script
    assert 'await sniffImageKnowledgeMime(file)' in script
    assert 'bytes[0]===0x42&&bytes[1]===0x4d' in script
    assert 'async function normalizeImageKnowledgeFile(file)' in script
    assert 'createImageBitmap(file)' in script
    assert 'canvas.toBlob(resolve,"image/png")' in script
    assert 'await normalizeImageKnowledgeFile(file)' in script
    prepare = script.split('async function prepareImageKnowledgeFile(file)', 1)[1].split('async function loadImageKnowledge', 1)[0]
    assert prepare.index('state.imageKnowledgeBase64=""') < prepare.index('if(file.size<1')
    assert prepare.index('$("saveImageKnowledgeBtn").disabled=true') < prepare.index('if(file.size<1')
    assert '25000000' in script
    assert 'image/bmp,image/heic,image/heif,.bmp,.heic,.heif' in html


def test_web_image_import_action_uses_attachment_flag_or_exact_placeholder_only():
    script = read("services/api-control-plane/static/bot-web-v2.js")
    action = script.split("function openActionSheet()", 1)[1].split(
        "function closeActionSheet", 1
    )[0]
    assert 'm.message_type==="image"' in action
    assert '!!m.attachment_sha256' in action
    assert '["[图片]","图片消息","[image]"].includes(String(m.text||"").trim())' in action
    assert r'/\[图片\]|图片消息/.test' not in action
