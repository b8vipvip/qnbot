from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_windows_image_knowledge_has_per_shop_delta_state_and_conflict_safe_apply():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    assert "SyncDirty" in service
    assert "CloudRevision" in service
    assert "VisualImageKnowledgeSyncTombstone" in service
    assert "VisualImageKnowledgeSyncState" in service
    assert "BuildCloudSyncRequest" in service
    assert "ApplyCloudSyncResponse" in service
    assert "response[\"shop_key\"]" in service
    assert "TryDeleteManagedImage" in service
    assert "since_revision" in service
    assert "base_revision" in service
    assert "SyncImageKnowledgeAsync" in sync
    assert '"/api/runtime/v1/bot-web/image-knowledge/sync"' in sync
    assert '"X-Shop-Key"' in sync
    assert "state.Shop.ShopKey" in sync


def test_cloud_image_knowledge_is_bound_to_client_shop_and_uses_revisioned_tombstones():
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    binding = read("services/api-control-plane/bot_client_shop_binding.py")
    assert "shop_key TEXT NOT NULL DEFAULT ''" in api
    assert "seller TEXT NOT NULL DEFAULT ''" in api
    assert "bot_web_image_knowledge_sync_state" in api
    assert "bot_web_image_knowledge_tombstones" in api
    assert "UNIQUE(client_id,shop_key,image_sha256)" in api
    assert "legacy_unbound_count" in api
    assert "quarantined" in api
    assert "_require_image_shop_scope" in api
    assert "shop_scope_mismatch" in api
    assert "seller_scope_mismatch" in api
    assert '@router.post("/api/runtime/v1/bot-web/image-knowledge/sync")' in api
    assert "base_revision" in api
    assert "conflicts" in api
    assert "revision" in api
    assert "bot_web_image_knowledge" in binding
    assert "bot_web_image_knowledge_tombstones" in binding
    assert "bot_web_image_knowledge_sync_state" in binding


def test_web_image_knowledge_uses_the_same_shop_scoped_cloud_records_as_windows_sync():
    script = read("services/api-control-plane/static/bot-web-v2.js")
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    assert "imageKnowledgeScope()" in script
    assert "shop_key" in script
    assert "base_revision" in script
    assert "imageKnowledgeScopeQuery()" in script
    assert "shop_key: str = Query" in api
    assert "seller: str = Query" in api
    assert "def web_image_knowledge_list(" in api
    assert "def web_image_knowledge_save(" in api
    assert "def web_image_knowledge_delete(" in api


def test_original_buyer_images_are_captured_from_shop_cache_sent_and_rendered_in_bot_web():
    turns = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    console = read("services/api-control-plane/bot_web_console.py")
    conversation = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    script = read("services/api-control-plane/static/bot-web-v2.js")
    assert "public bool HasImage" in turns
    assert "TryGetCompletedCachedImagePath" in cache
    assert "messageKey.EndsWith" in cache
    assert "AttachmentBytes" in sync
    assert '["data_base64"]' in sync
    assert '["attachment"]' in sync
    assert "attachment_bytes BLOB" in console
    assert "_decode_message_attachment" in console
    assert "bot_web_conversation_message_attachment" in console
    assert "attachment_bytes" not in conversation.split("def web_conversation_messages", 1)[1].split(
        "def web_mark_conversation_read", 1
    )[0]
    assert "loadMessageAttachments" in script
    assert "importImageKnowledgeFromMessage" in script
    assert "/attachment?" in script
    assert "new File([bytes]" in script


def test_message_attachment_storage_is_cleared_on_withdrawal_and_never_returned_inline():
    console = read("services/api-control-plane/bot_web_console.py")
    snapshot = console.split("def bot_web_snapshot", 1)[1].split("@router.get", 1)[0]
    save = console.split("def _save_message", 1)[1].split("def _apply_command_results", 1)[0]
    assert "attachment_bytes" not in snapshot
    assert "CASE WHEN excluded.withdrawn=1 THEN NULL" in save
    assert "attachment_bytes=COALESCE" not in save
    assert "image_base64" in console
