from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_image_knowledge_cloud_api_is_packaged_shop_scoped_and_ocr_backed():
    api = read("services/api-control-plane/bot_web_image_knowledge.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    docker = read("services/api-control-plane/Dockerfile")
    binding = read("services/api-control-plane/bot_client_shop_binding.py")

    assert "bot_web_image_knowledge.install(control_plane, bot_web_console)" in bootstrap
    assert "bot_web_image_knowledge.init_db()" in bootstrap
    assert "bot_web_image_knowledge.py" in docker
    for table in ("bot_message_images", "bot_image_knowledge_state", "bot_image_knowledge_uploads"):
        assert table in api
        assert f'"{table}"' in binding
    assert '@router.post("/api/bot-web/image-knowledge/upload")' in api
    assert '@router.post("/api/bot-web/image-knowledge/from-message/{message_id}")' in api
    assert '@router.post("/api/runtime/v1/bot-web/message-image")' in api
    assert '@router.post("/api/runtime/v1/bot-web/image-knowledge-sync")' in api
    assert "runtime_ocr._run_ocr" in api
    assert "_MAX_IMAGE_BYTES = 8 * 1024 * 1024" in api
    assert "_require_runtime_shop" in api
    assert "hashlib.sha256" in api
    assert "revision_conflict" in api
    assert "raise_on_conflict=True" in api


def test_web_conversation_images_are_available_only_when_synced_for_the_exact_message():
    messages = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    image_api = read("services/api-control-plane/bot_web_image_knowledge.py")
    windows = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")

    assert "LEFT JOIN bot_message_images" in messages
    assert "AS image_available" in messages
    assert "X-Message-Key" in windows
    assert "X-Image-Sha256" in windows
    assert "TryGetReferenceByMessageKey" in windows
    assert "ResolveAsync(" in windows
    assert "message-image" in windows
    assert "role='user'" in image_api
    assert "withdrawn=0" in image_api


def test_windows_buyer_image_context_menu_imports_the_original_shop_scoped_image():
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")

    assert "导入图片知识" in conversation
    assert "Stylus.SetIsPressAndHoldEnabled(txtQuestion, true)" in conversation
    assert "TryGetNearestReference" in conversation
    assert "VisionImageCacheService.ResolveAsync" in conversation
    assert "ShowImageKnowledgeImport" in conversation
    assert "ShopSettingsScope.Enter(shop)" in conversation
    assert "TryGetNearestReference" in cache
    assert "OpenImageKnowledgeImport" in shell
    assert 'SelectNav("图片知识")' in shell
    assert "ImportImageAndRecognizeAsync(imagePath, imageName)" in shell


def test_windows_and_web_share_image_knowledge_metadata_and_original_files():
    local = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    cloud = read("src/Bot/Knowledge/VisualImageKnowledgeCloudSyncService.cs")
    props = read("src/Directory.Build.props")
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")

    assert "ImageName" in local
    assert "Deleted" in local
    assert "CloudContentFingerprint" in local
    assert "LoadAllForCloudSync" in local
    assert "SetCloudContentFingerprint" in local
    assert "VisualImageKnowledgeCloudSyncService.cs" in props
    assert "/api/runtime/v1/bot-web/image-knowledge-sync" in cloud
    assert "/api/runtime/v1/bot-web/image-knowledge/" in cloud
    assert "SendWithUnauthorizedRecoveryAsync" in cloud
    assert "ImportImageAndRecognizeAsync" in page
    assert "自动调用服务端 OCR" in page
    assert "识别图片" not in page


def test_web_image_knowledge_editor_and_message_action_support_auto_ocr_and_context_import():
    html = read("services/api-control-plane/static/bot-web.html")
    js = read("services/api-control-plane/static/bot-web-v2.js")
    css = read("services/api-control-plane/static/bot-web-v2.css")

    for control in ("imageKnowledgePanel", "uploadImageKnowledgeBtn", "imageKnowledgePreview",
                    "imageKnowledgeName", "imageKnowledgeOcr", "imageKnowledgeDescription",
                    "saveImageKnowledgeBtn", "importImageKnowledgeAction"):
        assert control in html
    assert "Knowledge Center V2" not in html
    assert "视觉理解" not in html
    assert "知识库" in html and "图片知识" in html
    assert "image_available" in js
    assert "导入图片知识" in js
    assert "/api/bot-web/image-knowledge/from-message/" in js
    assert "/api/bot-web/image-knowledge/upload" in js
    assert "FormData" in js
    assert "imageKnowledgeRevision" in js
    assert "expected_revision" in js
    assert "image-knowledge-layout" in css
