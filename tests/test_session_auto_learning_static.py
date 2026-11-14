from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_session_learning_waits_five_minutes_and_keeps_audit_records():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "public const int InactivityMinutes = 5" in source
    assert "public const int SellerQuietSeconds = 30" in source
    assert "ConversationSessionLearningReportEntity" in source
    assert "StoreReplyStyleProfileEntity" in source
    assert 'report.Status = EnableAutomaticWrites ? "学习完成" : "学习完成（dry-run）";' in source
    assert "ResumePending" in source


def test_session_learning_rejects_bot_only_and_high_risk_self_learning():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert 'suggestion.EvidenceType == "bot_only"' in source
    assert 'suggestion.EvidenceType == "insufficient"' in source
    assert 'suggestion.Confidence < 0.86' in source
    assert "ContainsHighRisk" in source
    assert "禁止Bot自我学习" in source
    assert "withdrawn_bot_then_manual" in source
    assert "manual_correction" in source


def test_reviewed_knowledge_prefers_strong_human_corrections():
    source = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")

    assert 'evidenceType == "manual_correction"' in source
    assert 'evidenceType == "withdrawn_bot_then_manual"' in source
    assert "existingLooksCurated" in source
    assert "confidence < 0.95" in source
    assert "existing.AiGenerated = false" in source
    assert 'SourceType = sourceType' in source


def test_runtime_bridge_uses_exact_seller_and_injects_store_style_as_system_context():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")

    assert "QN.FindExistingBySellerNick(seller)" in source
    assert "?? QN.CurQN" not in source
    assert "InjectLearnedStyle" in source
    assert 'Role = "system"' in source
    assert "BuildReplyStylePromptAddon(seller)" in source
    assert '.Where(x => x.Role == "user" || x.Role == "assistant")' in source


def test_bot_history_can_be_loaded_by_session_time_range():
    source = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")

    assert "LoadRange(" in source
    assert "CreatedAtTicks >= ? and CreatedAtTicks <= ?" in source


def test_session_learning_is_initialized_and_visible_in_logs_ui():
    app = read("src/Bot/App.xaml.cs")
    ui = read("src/Bot/Options/ConversationSessionLearningUi.cs")
    targets = read("src/Directory.Build.targets")

    assert "ConversationSessionLearningService.Initialize();" in app
    assert "ConversationSessionLearningUi.Initialize();" in app
    assert 'TabHeader = "自动学习记录"' in ui
    assert "ConversationSessionLearningService.ReportsChanged" in ui
    assert "ConversationSessionLearningService.cs" in targets
    assert "ConversationSessionLearningRuntimeBridge.cs" in targets
    assert "ReviewedKnowledgeLearningService.cs" in targets
    assert "ConversationSessionLearningUi.cs" in targets


def test_five_minute_session_and_shop_safe_learning():
    s = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    assert "Schedule(existing, TimeSpan.FromMinutes(InactivityMinutes));" in s
    assert "TimeSpan.FromMinutes(InactivityMinutes) - (DateTime.Now - session.LastActivityAt)" in s
    assert "TimeSpan.FromSeconds(SellerQuietSeconds) - (DateTime.Now - session.LastActivityAt)" not in s
    assert "ShopContextLocator.ResolveRuntimeByShopKey(session.ShopKey)" in s
    assert "Key(shop.ShopKey, seller, buyer)" in s
    assert "using (ShopSettingsScope.Enter(shop))" in s
    assert "CountHumanSellerTurns(turns, cards) == 0" in s


def test_ai_history_exposes_existing_session_learning_reports():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    assert "ConversationSessionLearningService.GetReports(1000)" in ui
    assert "string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)" in ui
    assert "string.Equals(x.Seller, _seller, StringComparison.Ordinal)" in ui
    assert '"会话自动学习"' in ui
    assert "x.AppliedCount" in ui and "x.SkippedCount" in ui


def test_learning_timeline_uses_same_shop_scoped_key_as_context_store():
    ctx=read("src/Bot/ChromeNs/ConversationContextStore.cs")
    bridge=read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    assert 'return ScopeKey(seller) + "#" + (seller ?? string.Empty).Trim()' in ctx
    assert 'States.TryGetValue(Key(seller, buyer), out state)' in ctx
    assert 'ConversationContextStore.GetExistingLearningState(seller, buyer)' in bridge
    assert '(seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim()' not in bridge


def test_learning_history_refresh_is_typed_and_shop_scoped():
    ctx=read("src/Bot/ChromeNs/ConversationContextStore.cs")
    bridge=read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    assert 'ConversationContextStore.RefreshLearningHistoryCompleteAsync(' in ctx
    assert 'RefreshRemoteHistoryCompleteAsync(' in bridge
    assert 'method.Invoke(null, new[] { state, seller, buyer, ccode })' not in bridge
    assert 'ShopContextLocator.ResolveRuntimeByShopKey(shopKey)' in ctx
    assert 'LearningHistoryPageSize = 100' in ctx
    assert 'LearningHistoryMaxPages = 200' in ctx


def test_idle_learning_resolves_shop_before_structured_ai_call():
    source=read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analysis=source[source.index("private static async Task FinalizeAsync"):source.index("private static async Task<JObject> AnalyzeAsync")]
    assert "ShopContextLocator.ResolveRuntimeByShopKey(session.ShopKey)" in analysis
    assert "var shop = ShopContextLocator.ResolveBySellerNick(session.Seller);" not in analysis
    assert "using (ShopSettingsScope.Enter(shop))" in analysis
    assert "analysis = await AnalyzeAsync(session, turns, cards, styleBefore, localFirst);" in analysis
    assert analysis.index("using (ShopSettingsScope.Enter(shop))") < analysis.index("analysis = await AnalyzeAsync")
    assert analysis.index("analysis = await AnalyzeAsync") < analysis.index("ReviewedKnowledgeLearningService.ApplyReviewedKnowledge")


def test_session_learning_pages_complete_remote_history_and_recreates_state_after_restart():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "RefreshLearningHistoryCompleteAsync" in context
    assert "LearningHistoryPageSize = 100" in context
    assert "LearningHistoryMaxPages = 200" in context
    assert 'msgid = cursorId' in context
    assert 'msgtime = cursorTime' in context
    assert 'resultObject["hasMore"]' in context
    assert "history_cursor_stalled" in context
    assert "history_page_limit_reached" in context
    assert "var state = GetState(seller, buyer);" in context
    assert "RefreshRemoteHistoryCompleteAsync" in bridge
    assert "RefreshRemoteHistoryCompleteAsync(" in service
    assert '["historyComplete"]' in service
    assert '["historyPages"]' in service
    assert '["historyMessagesFetched"]' in service
    assert '["timelineTurns"]' in service
    assert '["botCards"]' in service


def test_session_learning_does_not_silently_truncate_turns_or_bot_cards():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")

    cleanup = context[context.index("private static void Cleanup(TimelineState state"):
                      context.index("// Typed access point for session learning")]
    turns = bridge[bridge.index("public static List<ConversationContextTurn> GetTurnsBetween"):
                   bridge.index("public static Task<ConversationHistoryRefreshResult>")]
    cards = service[service.index("private static string BuildCards"):
                    service.index("private static int CountHumanSellerTurns")]
    assert "Turns.Count > 100" not in cleanup
    assert "RemoveRange(0" not in cleanup
    assert ".Take(take)" not in turns
    assert "Math.Min(200" not in turns
    assert "Timestamp != DateTime.MinValue" in turns
    assert ".Take(80)" not in cards
    assert "LoadRangeComplete(" in service
    assert "public static List<BotConversationHistoryEntity> LoadRangeComplete(" in history
    assert "TrimConversationUnsafe" not in history


def test_incomplete_session_history_is_fail_closed_and_learning_defaults_to_dry_run():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[service.index("private static async Task FinalizeAsync"):
                      service.index("private static async Task<JObject> AnalyzeAsync")]

    assert "if (historyCoverage == null || !historyCoverage.IsComplete)" in finalize
    assert "remote_history_turn_count_mismatch" in finalize
    assert "未达到完整历史覆盖门槛，未调用AI，也未写入生产知识库" in finalize
    assert "private const bool EnableAutomaticWrites = false;" in service
    assert "knowledgeCorpusComplete" in finalize
    assert '"dry_run"' in finalize
    assert "默认dry-run" in finalize
    assert finalize.index("if (!EnableAutomaticWrites)") < finalize.index(
        "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge"
    )


def test_myopenai_endpoint_discovery_is_scoped_in_learning():
    myopenai=read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "var endpoints = AiEndpointStore.GetEnabledEndpoints();" in myopenai
    assert "Task.Run(() => MyOpenAI.CallStructuredChat(" in read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    source=read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "localFirst = ReplyModeService.IsLocalFirst(session.Seller);" in source

def test_learning_runtime_is_scoped_by_exact_qn_identity_and_shop_key():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in bridge
    assert "using (ShopSettingsScope.Enter(shop))" in bridge
    assert 'var scopedMessageKey = shop.ShopKey + "|" + messageKey' in bridge
    assert "Key(shop.ShopKey, seller, buyer)" in service
    assert "Key(session.ShopKey, session.Seller, session.Buyer)" in service
    assert "ShopContextLocator.ResolveRuntimeByShopKey(session.ShopKey)" in service
    assert "ProfileKey(string seller)" in service
    assert "return shop.ShopKey + \"#\" + seller;" in service
    assert "ResolveRuntimeByShopKey(string shopKey)" in locator
    assert "ShopKey = session.ShopKey" in service
    assert "ShopKey text" in history
    assert "LoadRangeComplete(session.ShopKey" in service
    assert "LoadRecentBySeller(shopKey, seller, 5000)" in read(
        "src/Bot/Knowledge/ChatHistoryScanService.cs"
    )


def test_learning_retries_never_overwrite_a_newer_same_buyer_session():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    requeue = service[
        service.index("private static void Requeue"):
        service.index("private static void CompleteSkipped")
    ]
    assert "Active.TryAdd(key, session)" in requeue
    assert "requeue_conflict_new_session_active" in requeue
    assert "Active[Key(" not in requeue


def test_remote_history_coverage_counts_only_unique_accepted_messages():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("private static string RecordMessage"):
        context.index("private static string[] GetNativeMessageAliases")
    ]
    refresh = context[
        context.index("RefreshLearningHistoryCompleteAsync"):
        context.index("private static int ReadInt")
    ]
    assert "private static string RecordMessage" in method
    assert "return exact.MessageKey ?? string.Empty;" in method
    assert "return precisePeers[0].MessageKey ?? string.Empty;" in method
    assert "return opposite[0].MessageKey ?? string.Empty;" in method
    assert "countedTimelineKeys.Add(canonicalTurnKey)" in refresh
    assert "result.MessagesInWindow++;" in refresh
    assert "var canonicalTurnKey = RecordMessage(state, seller, buyer, item, text, true);" in refresh


def test_legacy_shop_scoped_history_backfill_is_fail_closed():
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "Profiles.GetAll()" in locator
    assert "BackfillLegacyShopRows(string shopKey, string seller)" in history
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in history
    assert "catch (AmbiguousShopIdentityException)" in history
    assert "update BotConversationHistoryEntity set ShopKey = ?" in history
    assert "update BotReplySourceStampEntity set ShopKey = ?" in history
    assert "update BotReplyEchoLinkEntity set ShopKey = ?" in history


def test_manual_bot_source_corrections_are_shop_scoped():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "public string ShopKey { get; set; }" in history[
        history.index("internal sealed class BotReplyManualSourceEntity"):
        history.index("internal sealed class BotReplyEchoLinkEntity")
    ]
    assert "UpdateAnswerSource(\n            string shopKey, string seller, string entityId, string source)" in history
    assert "x.EntityId == entityId && x.ShopKey == shopKey" in history
    assert "GetManualSource(string shopKey, string seller, string entityId)" in history
    assert "GetOriginalSourceBeforeCorrection(\n            string shopKey, string seller, string entityId)" in history


def test_pending_session_resume_does_not_leave_ambiguous_or_duplicate_rows_pending():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    resume = service[
        service.index("private static void ResumePending"):
        service.index("private static void CleanupReports")
    ]
    assert 'report.Error = "legacy_report_shop_scope_unavailable";' in resume
    assert 'report.Error = "superseded_pending_session_duplicate";' in resume
    assert 'report.Error = "superseded_by_active_session";' in resume
    assert "report.CompletedAtTicks = DateTime.Now.Ticks;" in resume


def test_bot_answer_mirror_refresh_stays_on_owning_qn_shop():
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    assert "var activeQn = _preQN ?? QN.CurQN;" in robot
    assert "if (string.Equals(activeShopKey, shopKey, StringComparison.Ordinal))" in robot
    assert "catch { activeShopKey = string.Empty; }" in robot


def test_nickname_fallback_identity_cannot_merge_multiple_unstable_qn_instances():
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    context = read("src/Bot/ShopScope/ShopContext.cs")
    profile = read("src/Bot/ShopScope/ShopProfile.cs")
    assert "EnsureUnstableIdentityIsUnique(qn, context);" in locator
    assert "qnMatches.Count > 1 && qnMatches.Any(x => !x.Context.HasStableSellerId)" in locator
    assert "FindConnectedQnByShopKey" in locator
    assert "if (matches.Count > 1 && matches.Any(x => !x.Context.HasStableSellerId))" in locator
    assert "SellerNick" in context
    assert 'JsonProperty("seller_nick")' in profile
    assert "ShopIdentityResolver.Resolve(x.Seller)" in locator


def test_bot_resend_edit_and_knowledge_navigation_preserve_exact_shopkey():
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    card = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    manager = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert "FindConnectedQnByShopKey(e.ShopKey)" in robot
    assert "ResolveRuntimeByShopKey(e.ShopKey)" in robot
    assert "using (ShopSettingsScope.Enter(shop))" in robot
    assert "public string ShopKey { get; private set; }" in card
    assert "_answer, _shopKey" in card
    assert "LoadRecentBySeller(_shopKey, _seller, 2000)" in manager
    assert "UpdateAnswerSource(\n                _shopKey, _seller, _selectedSource.EntityId, source)" in manager
    assert "RequestedShopKey" in ui
    assert "new KnowledgeV2BotReplyManagementPage(_owner, _seller, _shopKey)" in ui


def test_bot_echo_attribution_uses_native_marker_and_time_not_text_alone():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    classifier = service[
        service.index("private static bool IsBotTurn"):
        service.index("private static List<Suggestion> ParseSuggestions")
    ]
    assert 'value.EndsWith("[A]"' in classifier
    assert 'value.EndsWith("[AI]"' in classifier
    assert "AnswerReadyAtTicks" in classifier
    assert "TimeSpan.FromMinutes(30).Ticks" in classifier


def test_ambiguous_seller_nick_never_falls_back_to_shared_legacy_timeline():
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "AmbiguousShopIdentityException" in locator
    assert "catch (AmbiguousShopIdentityException) { throw; }" in context

def test_repeated_history_refresh_counts_canonical_turns_not_only_new_merges():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    refresh = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult>"):
        context.index("private static int ReadInt")
    ]
    record = context[
        context.index("private static string RecordMessage"):
        context.index("private static string[] GetNativeMessageAliases")
    ]
    assert "private static string RecordMessage" in record
    assert "return exact.MessageKey ?? string.Empty;" in record
    assert "return precisePeers[0].MessageKey ?? string.Empty;" in record
    assert "return opposite[0].MessageKey ?? string.Empty;" in record
    assert "countedTimelineKeys.Add(canonicalTurnKey)" in refresh
    assert "historyCoverage.MessagesInWindow != turns.Count" in read(
        "src/Bot/ChromeNs/ConversationSessionLearningService.cs"
    )


def test_pagination_flag_accepts_boolean_and_numeric_json_values():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    parser = context[context.index("private static int ReadInt"):]
    assert "JTokenType.Boolean" in parser
    assert "bool.TryParse(text, out boolean)" in parser
    assert "double.TryParse(text, NumberStyles.Float" in parser

def test_active_shop_reassert_uses_exact_shopkey_and_cdp_session():
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    reassert = coordinator[
        coordinator.index("internal static void ReassertCurrent"):
        coordinator.index("private static bool Activate")
    ]
    is_active = coordinator[
        coordinator.index("internal static bool IsActive"):
        coordinator.index("internal static void ObserveChatDialogActive")
    ]
    connected = locator[
        locator.index("public static QN FindConnectedQnByShopKey"):
        locator.index("public static ShopContext ResolveCurrentForUi")
    ]
    assert "ShopContextLocator.FindConnectedQnByShopKey(shopKey, sessionId)" in reassert
    assert "QN.FindExistingBySellerNick(seller)" not in reassert
    assert "_activeSessionId.Length == 0" in is_active
    assert "string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)" in is_active
    activate = coordinator[
        coordinator.index("private static bool Activate"):
        coordinator.index("private static string ShopKey")
    ]
    assert "_activeSessionId = sessionId;" in activate
    assert "string.Equals((x.CDP.SessionId ?? string.Empty).Trim()," in connected
    assert "OrderByDescending(x => ActiveShopSessionRegistry.IsActive(x.Qn))" in connected

def test_settings_and_diagnostic_actions_never_resolve_shop_by_display_name():
    settings = read("src/Bot/Options/WndOption.xaml.cs")
    diagnostic = read("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
    constructor = settings[
        settings.index("private WndOption(ShopContext shop)"):
        settings.index("private WndOption(string seller)")
    ]
    show = settings[
        settings.index("public static void MyShow(string seller"):
        settings.index("private void ShowPage")
    ]
    send = diagnostic[
        diagnostic.index("private static async Task<ShopApiDiagnosticReport> SendDiagnosticAnswerAsync"):
        diagnostic.index("private static ShopApiDiagnosticReport Failure")
    ]
    assert "Seller = (shop.SellerNick ?? string.Empty).Trim();" in constructor
    assert "FindConnectedQnByShopKey(shop.ShopKey)" in constructor
    assert "shop.DisplayName, seller" not in show
    assert "string.Equals(activeSeller, seller, StringComparison.Ordinal)" in show
    assert "FindConnectedQnByShopKey(shop.ShopKey)" in send
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in send
    assert send.index("using (ShopSettingsScope.Enter(shop))") < send.index(
        "KnowledgeLearningService.AllowNextManualSend"
    )
    knowledge_window = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    settings_call = show[show.index("var knowledge = KnowledgeCenterWindow.ShowForSeller"):]
    assert "ShowForSeller(this, Seller, _shop.ShopKey)" in settings_call
    assert "Window owner, string seller, string shopKey =" in knowledge_window


def test_bot_mirror_and_history_restore_follow_active_shopkey_not_stale_qn():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    history = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.History.cs")
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    resolver = mirror[
        mirror.index("private QN ResolveMessageMirrorQn"):
        mirror.index("private List<ConversationContextTurn> LoadMessageMirrorTurns")
    ]
    restore = history[
        history.index("private void EnsureCurrentConversationHistoryLoaded"):
        history.index("    }\n}")
    ]
    reshow = robot[
        robot.index("public void ReShowAfterQNChange"):
        robot.index("private void RefreshConversations")
    ]
    assert "ActiveShopSessionRegistry.GetActiveShopKey()" in resolver
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in resolver
    assert "ShopIdentityResolver.Resolve(current.Seller).ShopKey" in resolver
    assert "var qn = ResolveMessageMirrorQn();" in restore
    assert "var current = ResolveMessageMirrorQn();" in restore
    assert "ActiveShopSessionRegistry.IsActive(current)" in reshow
