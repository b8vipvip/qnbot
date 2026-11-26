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
    assert "pendingImageBytes" in service
    assert "actions.OrderBy(x => x.Item1)" in service
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
    assert '@router.post("/api/bot-web/image-knowledge/migrate-legacy")' in api
    assert "data.confirmed" in api
    assert "_require_image_shop_scope" in api
    assert "shop_scope_mismatch" in api
    assert "seller_scope_mismatch" in api
    assert '@router.post("/api/runtime/v1/bot-web/image-knowledge/sync")' in api
    assert "base_revision" in api
    assert "conflicts" in api
    assert "revision" in api
    assert "upserts: List[Dict[str, Any]] = Field(default_factory=list, max_length=5)" in api
    scoped_cleanup = binding.split("TOKEN_SCOPED_STATE_TABLES", 1)[1].split(")", 1)[0]
    assert "bot_web_image_knowledge" not in scoped_cleanup
    assert "bot_web_image_knowledge_tombstones" not in scoped_cleanup
    assert "bot_web_image_knowledge_sync_state" not in scoped_cleanup
    # The records and cursors remain partitioned by ShopKey, so token rebinds
    # hide the old shop rather than destroying its image knowledge.


def test_web_image_knowledge_uses_the_same_shop_scoped_cloud_records_as_windows_sync():
    script = read("services/api-control-plane/static/bot-web-v2.js")
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    assert "imageKnowledgeScope()" in script
    assert "shop_key" in script
    assert "base_revision" in script
    assert "imageKnowledgeScopeQuery()" in script
    assert "migrateLegacyImageKnowledgeBtn" in script
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
    decoder = console.split("def _decode_message_attachment", 1)[1].split("def _save_message", 1)[0]
    assert "raise HTTPException(status_code=409" in decoder
    assert "raise HTTPException(status_code=422" in decoder
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



def test_sync_preserves_local_edits_and_deletes_that_race_with_cloud_responses():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    upsert = service.split("private static void ApplyCloudUpsert", 1)[1].split(
        "private static void ApplyCloudDelete", 1
    )[0]
    delete = service.split("private static void ApplyCloudDelete", 1)[1].split(
        "private static VisualImageKnowledgeSyncState SelectSyncState", 1
    )[0]
    assert "local.SyncDirty" in upsert
    assert "local.UpdatedAtTicks > cloudUpdatedAt || localContentChanged" in upsert
    assert "localDelete.SyncDirty && localDelete.DeletedAtTicks > cloudUpdatedAt" in upsert
    assert "Save(shop.ShopKey, seller, entry, temp)" in upsert
    assert upsert.index("lock (Sync)") < upsert.index("Save(shop.ShopKey, seller, entry, temp)")
    assert "set Enabled=?,SyncDirty=0,CloudRevision=?" in upsert
    assert "cloudEnabled ? 1 : 0" in upsert
    assert "old.SyncDirty && old.UpdatedAtTicks > deletedAt" in delete
    assert "localDelete.SyncDirty && localDelete.DeletedAtTicks > deletedAt" in delete
    assert "CloudRevision=?" in delete


def test_cloud_cursor_rollback_triggers_full_shop_reconciliation():
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    assert "initial_revision" in api
    assert "def _current_image_revision(conn, client_id: int, shop_key: str)" in api
    assert "legacy_events" in api
    assert "revision = _current_image_revision(conn, client_id, shop_key) + 1" in api
    assert "data.since_revision > initial_revision" in api
    assert '"reset_cursor": True' in api
    assert 'response.Value<bool?>("reset_cursor") == true' in service
    assert "set SyncDirty=1,CloudRevision=0 where ShopKey=? and Seller=?" in service
    assert "LastCloudRevision,UpdatedAtTicks) values(?,?,0,?)" in service


def test_image_knowledge_sync_is_not_blocked_by_message_mirror_failures():
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    once = sync.split("private static async Task SyncOnceAsync", 1)[1].split(
        "private static async Task SyncImageKnowledgeAsync", 1
    )[0]
    assert "本店 Bot Web消息镜像同步失败；图片知识同步仍将独立执行" in once
    assert once.index("catch (Exception ex)") < once.index("await SyncImageKnowledgeAsync")
    assert "await SyncImageKnowledgeAsync(state, serverUrl, token)" in once


def test_generic_file_ids_are_not_misclassified_as_original_buyer_images():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    turns = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    helper = turns.split("private static bool IsImageMessage", 1)[1].split(
        "private static string[] GetNativeMessageAliases", 1
    )[0]
    assert "internal static bool IsImageMessage(QNChatMessage message, string messageText)" in safety
    assert "IncomingMessageSafety.IsImageMessage(message, messageText)" in helper
    assert "VisionImageResolver.ExtractUrl(message)" not in helper
    assert "original.header.summary" in safety
    assert "original.header.title" in safety



def test_plain_text_image_mentions_are_not_treated_as_media_attachments():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    detect = safety.split("private static string DetectUnsupportedType", 1)[1].split(
        "private static bool IsExplicitImagePlaceholder", 1
    )[0]
    placeholder = safety.split("private static bool IsExplicitImagePlaceholder", 1)[1].split(
        "private static string DetectUnsupportedType", 1
    )[0]
    assert "ContainsMarker(imageMetadata, \"图片\", \"image\", \"photo\")" in detect
    assert "IsExplicitImagePlaceholder(messageText)" in detect
    assert "IsExplicitImagePlaceholder(message.summary)" in detect
    assert "ContainsMarker(combined, \"图片\", \"image\", \"photo\")" not in detect
    assert 'value == "[图片]"' in placeholder
    assert 'value == "图片消息"' in placeholder
    assert 'value == "图片"' not in placeholder
    assert 'value == "image"' not in placeholder



def test_windows_sqlite_enforces_shopkey_image_knowledge_id_and_hash_boundaries():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    save = service.split("public static VisualImageKnowledgeEntry Save", 1)[1].split(
        "public static bool Delete", 1
    )[0]
    assert 'Select("where EntityId = ? limit 1", entry.EntityId)' in save
    assert "globalId.ShopKey" in save
    assert "图片知识ID已属于其他ShopKey" in save
    assert 'where ShopKey = ? and ImageSha256 = ? and EntityId <> ? limit 1' in save
    assert "当前ShopKey已存在这张图片" in save
    final_write = save.split("            try\n            {", 1)[1].split("            catch", 1)[0]
    assert "currentGlobalId" in final_write
    assert "currentDuplicate" in final_write
    assert final_write.index("currentDuplicate") < final_write.index("SaveRecordsInTransaction")


def test_captioned_buyer_images_keep_their_question_and_offer_import_action():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    display = safety.split("public static string GetDisplayText", 1)[1].split(
        "public static bool IsMediaPlaceholder", 1
    )[0]
    preserve = safety.split("private static string PreserveImageCaption", 1)[1].split(
        "public static bool IsMediaPlaceholder", 1
    )[0]
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    menu = mirror.split("private ContextMenu BuildMessageMirrorBuyerContextMenu", 1)[1].split(
        "private async Task AddBuyerDirectAnswerFromMirror", 1
    )[0]
    assert "PreserveImageCaption(messageText)" in display
    assert 'caption + " [图片]"' in preserve
    assert "turn.HasImage" in menu
    assert 'Header = "导入图片知识"' in menu


def test_web_mirror_only_treats_real_image_flags_or_exact_media_placeholders_as_attachments():
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    capture = sync.split("var normalizedPlaceholder =", 1)[1].split("if (turn.Withdrawn", 1)[0]
    assert "var isImage = turn.HasImage" in capture
    assert 'string.Equals(normalizedPlaceholder, "[图片]"' in capture
    assert 'string.Equals(normalizedPlaceholder, "图片消息"' in capture
    assert 'string.Equals(normalizedPlaceholder, "[image]"' in capture
    assert 'text.IndexOf("[图片]"' not in capture
    assert 'text.IndexOf("图片消息"' not in capture


def test_windows_buyer_image_import_menu_does_not_match_image_mentions_in_plain_text():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    menu = mirror.split("private ContextMenu BuildMessageMirrorBuyerContextMenu", 1)[1].split(
        "private async Task AddBuyerDirectAnswerFromMirror", 1
    )[0]
    assert "turn.HasImage" in menu
    assert 'string.Equals(normalizedQuestion, "[图片]"' in menu
    assert 'string.Equals(normalizedQuestion, "图片消息"' in menu
    assert 'question.IndexOf("[图片]"' not in menu
    assert 'question.IndexOf("图片消息"' not in menu


def test_missing_image_queue_head_cannot_starve_later_valid_records():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    request = service.split("public static JObject BuildCloudSyncRequest", 1)[1].split("public static void ApplyCloudSyncResponse", 1)[0]
    assert "order by UpdatedAtTicks asc, EntityId asc" in request
    assert "order by UpdatedAtTicks asc limit 50" not in request
    assert "if (upserts.Count >= 5 || pendingImageBytes >= 8L * 1024L * 1024L) break;" in request
    assert "pendingImageBytes + bytes.Length > 8L * 1024L * 1024L" in request
    assert request.index("if (upserts.Count >= 5") < request.index("File.ReadAllBytes")


def test_duplicate_image_conflict_rekeys_atomically_without_deleting_local_file():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    helper = read("src/BotLib/Db/Sqlite/SQLiteHelper.cs")
    apply = service.split("public static void ApplyCloudSyncResponse", 1)[1].split(
        "private static void RekeyLocalDuplicateForCloudUpsert", 1
    )[0]
    rekey = service.split("private static void RekeyLocalDuplicateForCloudUpsert", 1)[1].split(
        "private static void ApplyCloudUpsert", 1
    )[0]
    upsert = service.split("private static void ApplyCloudUpsert", 1)[1].split(
        "private static void ApplyCloudDelete", 1
    )[0]
    assert "RemoveLocalDuplicate" not in service
    assert "ApplyCloudUpsert(shop, seller, data, duplicateLocalId)" in apply
    assert "RekeyLocalDuplicateForCloudUpsert(" in upsert
    assert upsert.index("ComputeSha256(bytes)") < upsert.index("RekeyLocalDuplicateForCloudUpsert(")
    assert upsert.index("VisionImageResolver.DetectMime(bytes)") < upsert.index("RekeyLocalDuplicateForCloudUpsert(")
    assert "DbHelper.Db.RunInTransaction" in rekey
    assert "update VisualImageKnowledgeEntry set EntityId=?,CloudRevision=?" in rekey
    assert "delete from VisualImageKnowledgeSyncTombstone" in rekey
    assert '"update VisualImageKnowledgeEntry set EntityId=?,CloudRevision=?' in rekey
    assert "TryDeleteManagedImage" not in rekey
    assert "public void RunInTransaction(Action<SQLiteConnection> action)" in helper
    assert "conn.RunInTransaction(() => action(conn))" in helper
    assert "conn.Execute(" in rekey


def test_image_delete_tombstone_is_atomic_and_survives_duplicate_id_cloud_restore():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    delete = service.split("public static bool Delete", 1)[1].split("public static JObject BuildCloudSyncRequest", 1)[0]
    reconcile = service.split("private static bool ReconcileDeletedImageByHash", 1)[1].split(
        "private static void RekeyLocalDuplicateForCloudUpsert", 1
    )[0]
    tombstone = service.split("internal sealed class VisualImageKnowledgeSyncTombstone", 1)[1].split(
        "internal sealed class VisualImageKnowledgeSyncState", 1
    )[0]
    assert "ImageSha256" in tombstone
    assert "DbHelper.Db.RunInTransaction(conn =>" in delete
    assert "item.ImageSha256" in delete
    assert "delete from VisualImageKnowledgeEntry" in delete
    assert "where ShopKey=? and Seller=? and ImageSha256=?" in reconcile
    assert "latest.DeletedAtTicks <= cloudUpdatedAt" in reconcile
    assert "set EntityId=?,SyncDirty=1,CloudRevision=?,ImageSha256=?" in reconcile
    assert "delete from VisualImageKnowledgeEntry" in reconcile
    assert "ReconcileDeletedImageByHash(shop, seller, id, sha, revision, cloudUpdatedAt)" in service
    assert "add column ImageSha256 text not null default ''" in service


def test_image_save_and_remote_delete_commit_row_and_tombstone_as_single_sqlite_transactions():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    save = service.split("public static VisualImageKnowledgeEntry Save", 1)[1].split(
        "public static bool Delete", 1
    )[0]
    delete = service.split("public static bool Delete", 1)[1].split(
        "public static JObject BuildCloudSyncRequest", 1
    )[0]
    remote_delete = service.split("private static void ApplyCloudDelete", 1)[1].split(
        "private static VisualImageKnowledgeSyncState SelectSyncState", 1
    )[0]
    assert "DbHelper.Db.RunInTransaction(conn =>" in save
    assert "conn.InsertOrReplace(entry)" in save
    assert "delete from VisualImageKnowledgeSyncTombstone" in save
    assert "DbHelper.Db.RunInTransaction(conn =>" in delete
    assert "insert or replace into VisualImageKnowledgeSyncTombstone" in delete
    assert "delete from VisualImageKnowledgeEntry" in delete
    assert "DbHelper.Db.RunInTransaction(conn =>" in remote_delete
    assert "ImageSha256" in remote_delete
    assert "TryDeleteManagedImage(old.ImagePath" in remote_delete


def test_duplicate_id_conflict_mapping_is_applied_before_same_revision_cloud_delta_upsert():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    apply = service.split("public static void ApplyCloudSyncResponse", 1)[1].split(
        "private static bool ReconcileDeletedImageByHash", 1
    )[0]
    assert ".OrderBy(x => x.Item1)" in apply
    assert '.ThenBy(x => Clean((string)x.Item4["local_id"], 80).Length > 0 ? 0 : 1)' in apply
    assert "Re-key the local duplicate first" in apply
