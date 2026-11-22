from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane" / "bot_web_console.py"
ADMIN = ROOT / "services" / "api-control-plane" / "bot_web_admin.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
INDEX = ROOT / "services" / "api-control-plane" / "static" / "index.html"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "bot-web-v2.js"
ADMIN_SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "client-token-copy.js"
WINDOWS = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebConsoleSyncService.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_server_packages_and_registers_mobile_console():
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    assert "import bot_web_console" in bootstrap
    assert "import bot_web_admin" in bootstrap
    assert "bot_web_console.install(control_plane)" in bootstrap
    assert "bot_web_admin.install(control_plane)" in bootstrap
    assert "bot_web_console.init_bot_web_db()" in bootstrap
    assert "bot_web_console.py" in dockerfile
    assert "bot_web_admin.py" in dockerfile


def test_web_login_is_bound_to_client_token_and_session():
    source = read(SERVICE)
    assert '@router.post("/api/bot-web/login")' in source
    assert "_client_by_token(token, capture_cipher=True, request_path=request.url.path)" in source
    assert 'request.session["bot_web_client_id"]' in source
    assert '@router.get("/api/bot-web/snapshot")' in source
    assert "Depends(_web_client)" in source
    assert '@router.post("/api/runtime/v1/bot-web/sync")' in source
    assert "Depends(_runtime_client)" in source


def test_tokens_are_encrypted_for_admin_copy_and_can_be_rotated():
    service = read(SERVICE)
    admin = read(ADMIN)
    script = read(ADMIN_SCRIPT)
    assert "token_cipher" in service
    assert "_cp.encrypt_secret(token)" in admin
    assert "_cp.decrypt_secret(row[\"token_cipher\"])" in admin
    assert '"/api/admin/mobile-bot/clients"' in script
    assert "copyClientToken" in script
    assert "rotateClientToken" in script
    assert "旧令牌会立即失效" in script


def test_mobile_page_has_status_messages_settings_and_manual_reply():
    page = read(PAGE)
    script = read(SCRIPT)
    assert "实时运行状态" in page
    assert 'id="messageList"' in page
    assert 'id="autoReplyEnabled"' in page
    assert 'id="replyForm"' in page
    assert "/api/bot-web/snapshot" in script
    assert "/api/bot-web/settings" in script
    assert "/api/bot-web/messages/send" in script
    assert "setInterval" in script


def test_windows_client_syncs_per_shop_state_and_recent_conversation_without_logging_token():
    source = read(WINDOWS)
    props = read(PROPS)
    assert "BotWebConsoleSyncService.cs" in props
    assert "InitializeForApp" in source
    assert '"/api/runtime/v1/bot-web/sync"' in source
    assert "ConversationContextStore.GetMirrorTurns" in source
    assert "ConversationContextStore.GetMirrorConversationSnapshots" in source
    assert "CapturedMessageSignatures" in source
    assert "effective_auto_reply_enabled" in source
    assert "ApplyDesiredSettings" in source
    assert "ExecuteSendTextAsync" in source
    assert "SendTextWithRetryAsync" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert "ConcurrentDictionary<string, ShopWebState>" in source
    assert '"X-Shop-Key"' in source
    assert "ControlPlaneClientToken" not in source
    assert "token=" not in source.lower()


def test_sensitive_command_state_is_per_shop_idempotent_and_not_persisted_in_server_logs():
    source = read(WINDOWS)
    server = read(SERVICE)
    assert "ProcessedCommandsKey" in source
    assert "WasProcessed(state, id)" in source
    assert "MarkProcessed(state, id)" in source
    assert "state.ProcessedCommands" in source
    assert "ShopSettingsScope.Enter(state.Shop)" in source
    assert "UNIQUE(client_id, message_key)" in server
    assert "ON CONFLICT(client_id,message_key) DO UPDATE SET" in server
    assert "payload_json" in server
    assert "print(" not in server


def test_bot_web_inbox_uses_the_desktop_message_mirror():
    page = read(PAGE)
    script = read(SCRIPT)
    sync = read(WINDOWS)
    context = read(ROOT / "src" / "Bot" / "ChromeNs" / "ConversationContextStore.cs")
    api = read(ROOT / "services" / "api-control-plane" / "bot_web_bot_qa.py")
    server = read(SERVICE)

    assert "is_bot INTEGER NOT NULL DEFAULT 0" in server
    assert "withdrawn INTEGER NOT NULL DEFAULT 0" in server
    assert "ALTER TABLE bot_messages ADD COLUMN is_bot" in server
    assert "ALTER TABLE bot_messages ADD COLUMN withdrawn" in server
    assert '<button data-page="messages"><span>▤</span>消息</button>' in page
    assert "仅显示买家问题与 Bot 自动回答" not in page
    assert "同步 Windows Bot 已采集的买家与客服消息" in page
    assert "ConversationContextStore.GetMirrorTurns" in sync
    assert "ConversationContextStore.GetMirrorConversationSnapshots" in sync
    assert "GetMirrorConversationSnapshots" in context
    assert '["is_bot"]' in sync
    assert '["withdrawn"]' in sync
    assert "project_conversation_message(row)" in api
    assert "build_bot_qa_messages" not in api
    assert 'record_scope": "conversation"' in api
    assert "已撤回一条消息" in script


def test_admin_console_links_mobile_page_and_copy_extension():
    index = read(INDEX)
    assert "Bot Web端" not in index
    assert 'href="/bot/"' not in index
    assert "手机 Web 端" in index
    assert "/bot/" in index
    assert 'src="/static/client-token-copy.js"' in index
    assert "管理员可复制对应令牌" in index


def test_bot_web_mirror_exports_all_buyers_and_matches_desktop_100_turn_window():
    context = read(ROOT / "src" / "Bot" / "ChromeNs" / "ConversationContextStore.cs")
    sync = read(WINDOWS)

    assert "var conversationLimit = maxConversations <= 0 ? int.MaxValue : maxConversations;" in context
    assert "var turnLimit = Math.Max(10, Math.Min(100, maxTurns));" in context
    assert "var take = Math.Max(10, Math.Min(100, maxTurns));" in context
    assert "GetMirrorTurns(seller, buyer, 100)" in sync
    assert "GetMirrorConversationSnapshots(" in sync
    assert "state.Shop.ShopKey, int.MaxValue, 100" in sync
    assert "Math.Min(256, maxConversations)" not in context


def test_bot_web_sync_queue_uses_backpressure_instead_of_silently_evicting_unsent_messages():
    sync = read(WINDOWS)
    enqueue = sync[sync.index("private static bool EnqueueMessage"):sync.index("private static void QueueDueSyncs")]

    assert "MaxPendingMessagesPerShop = 20000" in sync
    assert "PendingMessagesSync" in enqueue
    assert "nextCount > MaxPendingMessagesPerShop" in enqueue
    assert "return false;" in enqueue
    assert "等待后续同步" in enqueue
    assert "PendingMessages.TryRemove(old.MessageKey" not in enqueue

    capture = sync[sync.index("private static string CaptureTurn"):sync.index("private static bool EnqueueMessage")]
    assert "if (EnqueueMessage(" in capture
    assert "state.CapturedMessageSignatures[messageKey] = signature;" in capture


def test_bot_web_sync_queue_enforces_per_shop_payload_byte_budget_and_exact_accounting():
    sync = read(WINDOWS)
    enqueue = sync[sync.index("private static bool EnqueueMessage"):sync.index("private static void QueueDueSyncs")]
    flush = sync[sync.index("private static async Task SyncOnceAsync"):sync.index("private static JObject BuildStatus")]
    settings = sync[sync.index("private static void ApplyDesiredSettings"):sync.index("private static async Task ApplyCommandsAsync")]

    assert "MaxPendingMessageBytesPerShop = 64L * 1024 * 1024" in sync
    assert "public long PendingMessageBytes;" in sync
    assert "PendingMessageSizeBytes(item)" in enqueue
    assert "nextBytes > MaxPendingMessageBytesPerShop" in enqueue
    assert "state.PendingMessageBytes = nextBytes;" in enqueue
    assert "state.PendingMessageBytes - PendingMessageSizeBytes(item)" in flush
    assert "state.PendingMessageBytes = 0;" in settings
    assert "pending_message_bytes" in sync
    assert "PendingMessages.TryRemove(old.MessageKey" not in enqueue


def test_bot_web_message_sync_disable_preserves_retry_for_unacknowledged_messages():
    sync = read(WINDOWS)
    capture = sync[sync.index("private static string CaptureTurn"):sync.index("private static long PendingMessageSizeBytes")]
    enqueue = sync[sync.index("private static bool EnqueueMessage"):sync.index("private static void QueueDueSyncs")]
    flush = sync[sync.index("private static async Task SyncOnceAsync"):sync.index("private static JObject BuildStatus")]
    settings = sync[sync.index("private static void ApplyDesiredSettings"):sync.index("private static async Task ApplyCommandsAsync")]

    assert "lock (state.PendingMessagesSync)" in capture
    assert "if (!state.MessageSyncEnabled) return string.Empty;" in capture
    assert "if (!state.MessageSyncEnabled) return false;" in enqueue
    assert "state.CapturedMessageSignatures.TryRemove(key, out ignored);" in settings
    assert "state.PendingMessages.Clear();" in settings
    assert "state.PendingMessageBytes = 0;" in settings
    assert flush.index("PendingMessages).Remove(") < flush.index("ApplyDesiredSettings(state,")
    assert "pending_message_bytes" in sync


def test_bot_web_server_retention_covers_the_complete_visible_buyer_window():
    service = read(SERVICE)
    script = read(SCRIPT)

    # The inbox renders up to 600 buyers; each desktop mirror contributes 100 turns.
    # A 20,000-row server cap silently reduced the effective inbox to about 200 buyers.
    assert "MAX_MESSAGES_PER_CLIENT = 600 * 100" in service
    assert "if count > MAX_MESSAGES_PER_CLIENT:" in service
    assert "ORDER BY id DESC LIMIT ?" in service
    assert "(client_id, client_id, MAX_MESSAGES_PER_CLIENT)" in service
    assert 'limit=600&query=' in script
    assert "limit:\"120\"" in script


def test_bot_web_message_history_survives_polling_and_buyer_switches():
    script = read(SCRIPT)
    assert "historyLoaded:false,messageLoadToken:0" in script
    assert "function mergeMessageHistory(existing,incoming)" in script
    assert "state.messages=mergeMessageHistory(state.messages,incoming)" in script
    assert "state.historyLoaded=true" in script
    assert "state.historyLoaded=false;state.messageLoadToken++" in script
    assert "requestToken!==state.messageLoadToken" in script
    assert "conversationKey(state.active)!==activeKey" in script
    assert "list.scrollTop=previousTop+list.scrollHeight-previousHeight" in script
    assert "renderConversations();renderMessages(false);await loadActiveMessages(true)" in script
    assert "await loadActiveMessages(true);await markRead()" not in script

def test_rejected_runtime_tokens_are_diagnosed_without_logging_the_secret():
    source = read(SERVICE)
    assert "reason=unknown_token" in source
    assert "reason=disabled_token" in source
    assert "digest[:12]" in source
    assert "request_path=request.url.path" in source
    rejected = source[source.index("def _client_by_token"):source.index("def _runtime_client")]
    assert "logger.warning" in rejected
    assert "token_hash_prefix" in rejected
    assert "logger.warning(.*token)" not in rejected
    assert "logger.warning(.*Authorization)" not in rejected



def test_bot_web_sync_acknowledges_only_the_exact_command_result_snapshot():
    sync = read(WINDOWS)
    state = sync[sync.index("private sealed class ShopWebState"):sync.index("// The sync timer runs every second")]
    flush = sync[
        sync.index("private static async Task SyncOnceAsync"):
        sync.index("private static JObject BuildStatus")
    ]
    commands = sync[
        sync.index("private static async Task ApplyCommandsAsync"):
        sync.index("private static JObject Result")
    ]
    ack = sync[
        sync.index("private static void AcknowledgeCommandResults"):
        sync.index("private static bool WasProcessed")
    ]

    assert "PendingCommandResultsSync" in state
    assert "lock (state.PendingCommandResultsSync)" in flush
    assert "AcknowledgeCommandResults(state, resultSnapshot);" in flush
    assert "SetPendingCommandResult(state, id," in commands
    assert "state.PendingCommandResults[id] = Result(" not in commands
    assert "ReferenceEquals(current, pair.Value)" in ack
    assert "state.PendingCommandResults.TryRemove(pair.Key, out ignored);" in ack
