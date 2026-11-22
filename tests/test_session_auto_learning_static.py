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
    assert 'report.Status = !corpusComplete' in source
    assert 'EnableVerifiedCorpusCorrectionWrites = true' in source
    assert '自动新增' in source
    assert '自动修订' in source
    assert '通用AI建议仍待人工审核' in source
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

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in source
    assert "QN.FindExistingBySellerNick(seller)" not in source
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
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert "ConversationSessionLearningService.GetReportsForShop(" in ui
    assert "_shopKey, _seller, 1000" in ui
    assert "ShopContextLocator.ResolveRuntimeByShopKey(_shopKey)" in ui
    assert "using (ShopSettingsScope.Enter(shop))" in ui
    assert "new KnowledgeV2AiOptimizationHistoryPage(_owner, _seller, _shopKey)" in shell
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    assert '"where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc limit "' in service
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
    assert 'internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync(' in ctx
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
    assert "private const bool EnableVerifiedCorpusCorrectionWrites = true;" in service
    assert "ApplyVerifiedCorpusCorrections(session, corpusAnalysis, expectedCurrentFingerprint, out corpusSkipped)" in finalize
    assert "ApplyVerifiedCorpusNewKnowledge(" in finalize
    assert "EnableVerifiedCorpusNewKnowledgeWrites = true" in service
    assert "ApplyVerifiedCorpusNewKnowledge(" in finalize
    assert "EnableVerifiedCorpusNewKnowledgeWrites = true" in service
    assert '"ai_auto_publish_new_knowledge_and_verified_updates"' in finalize
    assert "genericSuggestionWriteMode" in finalize
    assert "通用会话AI建议仍为dry-run" in finalize
    assert finalize.index("if (!EnableAutomaticWrites)") < finalize.index(
        "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge"
    )
    assert "corpusApplied = ApplyVerifiedCorpusCorrections" in finalize


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
    assert "BotConversationHistoryStore.LoadRangeComplete(" in service
    assert "session.ShopKey, session.Seller, session.Buyer, from, endAt" in service
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
        context.index("private static bool TryReadInt")
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
    assert "ShopIdentityResolver.Resolve(qn.Seller)" in locator
    assert "GetProfileSellerNick(x)" in locator
    assert "GetProfileSellerNick(ShopProfile profile)" in locator
    assert 'const string fallbackPrefix = "nick:";' in locator
    assert "GetProfileSellerNick(x)" in locator


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
        context.index("private static bool TryReadInt")
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
    parser = context[context.index("private static bool TryReadInt"):]
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
    assert "if (qn == null)" in reassert
    assert "QN.CurQN = null;" in reassert
    assert "!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)" in reassert
    assert "!ReferenceEquals(QN.CurQN, current)" in reassert
    assert "_activeSessionId.Length > 0" in is_active
    assert "sessionId.Length > 0" in is_active
    assert "string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)" in is_active
    activate = coordinator[
        coordinator.index("private static bool Activate"):
        coordinator.index("private static bool OwnsAuthoritativeSession")
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
    settings_call = settings[
        settings.index("var knowledge = KnowledgeCenterWindow.ShowForSeller"):
    ]
    send = diagnostic[
        diagnostic.index("private static async Task<ShopApiDiagnosticReport> SendDiagnosticAnswerAsync"):
        diagnostic.index("private static ShopApiDiagnosticReport Failure")
    ]
    assert "FindConnectedQnByShopKey(shop.ShopKey)" in constructor
    assert "liveSellerNick" in constructor
    assert "savedSellerNick" in constructor
    assert "string.Equals(savedSellerNick, shop.ShopKey, StringComparison.Ordinal)" in constructor
    assert "shop.DisplayName, seller" not in show
    assert "string.Equals(activeSeller, seller, StringComparison.Ordinal)" in show
    assert "FindConnectedQnByShopKey(shop.ShopKey)" in send
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in send
    assert send.index("using (ShopSettingsScope.Enter(shop))") < send.index(
        "KnowledgeLearningService.AllowNextManualSend"
    )
    knowledge_window = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
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
    assert "ActiveShopSessionRegistry.GetActiveSessionId()" in resolver
    assert "ShopContextLocator.FindConnectedQnByShopKey(" in resolver
    assert "activeShopKey, activeSessionId" in resolver
    assert "ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(qn)" in resolver
    assert "ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(current)" in resolver
    assert "var current = QN.CurQN;" not in resolver
    assert "var qn = ResolveMessageMirrorQn();" in restore
    assert "var current = ResolveMessageMirrorQn();" in restore
    assert "ActiveShopSessionRegistry.IsActive(current)" in reshow

def test_desk_lookup_never_falls_back_across_explicit_shop_scope():
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")
    inst = desk[
        desk.index("public static Desk Inst"):
        desk.index("public static IList<Desk> Snapshot")
    ]
    scoped = inst[
        inst.index("var scopedShop = ShopSettingsScope.Current;"):
        inst.index("var current = QN.CurQN")
    ]
    assert "scopedShop.SellerNick" in scoped
    assert "FindConnectedQnByShopKey(scopedShop.ShopKey)" in scoped
    assert "DeskSellerBindingRegistry.FindSellerDesk(sellerNick, scopedShop.ShopKey)" in scoped
    assert "scopedShop.DisplayName" not in scoped
    assert "return null;" in scoped

def test_manual_reply_ai_optimization_records_are_shop_scoped_end_to_end():
    window = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    entity = window[
        window.index("internal sealed class AiOptimizationRecordEntity"):
        window.index("internal sealed class AiOptimizationRecordView")
    ]
    service = window[
        window.index("internal static class AiManualReplyOptimizationService"):
        window.index("internal static class ManualReplyAiComparisonBridge")
    ]
    bridge = window[
        window.index("internal static class ManualReplyAiComparisonBridge"):
        window.index("private static List<string> ExtractTexts")
    ]
    history_ui = window[
        window.index("internal sealed class AiOptimizationHistoryControl"):
    ]
    assert "public string ShopKey { get; set; }" in entity
    assert "GetRecords(\n            string shopKey, string seller, int maxCount)" in service
    assert '"where ShopKey = ? and Seller = ?' in service
    assert "alter table AiOptimizationRecordEntity add column ShopKey text" in service
    assert "BuildManualTurnKey(\n            string shopKey, string seller, string buyer, string question)" in service
    assert "BuildManualTurnKey(shopKey, seller, buyer, question)" in service
    assert "ShopKey = shopKey" in service
    assert "ResolveRuntimeBySeller(qn)" in bridge
    assert "using (ShopSettingsScope.Enter(shop))" in bridge
    assert "MatchesRecentBotAnswer(shop.ShopKey, seller, buyer, variants)" in bridge
    assert "StartForManualReply(\n                            seller, buyer, variants[0], DateTime.Now, shop.ShopKey)" in bridge
    assert "GetRecords(shopKey, seller, 500)" in history_ui
    assert "string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)" in history_ui

def test_learning_cleanup_preserves_pending_reports_and_startup_can_retry_schema():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    cleanup = service[
        service.index("private static void CleanupReports()"):
        service.index("private static void EnsureSchema()", service.index("private static void CleanupReports()"))
    ]
    init = service[
        service.index("public static void Initialize()"):
        service.index("public static void ObserveLiveMessage")
    ]
    assert "where CompletedAtTicks > 0 and EntityId not in" in cleanup
    assert "select EntityId from ConversationSessionLearningReportEntity where CompletedAtTicks > 0" in cleanup
    assert "Interlocked.CompareExchange(ref _initialized, 1, 0)" in init
    assert "Interlocked.Exchange(ref _initialized, 0);" in init


def test_learning_scoped_timeline_uses_real_seller_nick_even_for_duplicate_names():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    state = context[
        context.index("internal static object GetExistingLearningState("):
        context.index("internal static async Task<ConversationHistoryRefreshResult>")
    ]
    seller = context[
        context.index("private static string GetSellerNick("):
        context.index("private static string GetBuyerNick(")
    ]
    assert "ShopSettingsScope.Current" in state
    assert "?? ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in state
    assert "current.SellerNick" in seller
    assert "current.DisplayName" not in seller


def test_failed_bot_history_flush_never_overwrites_newer_pending_update():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    flush = history[
        history.index("private static void FlushPendingCore()"):
        history.index("private static void EnsureSchema()", history.index("private static void FlushPendingCore()"))
    ]
    assert "Pending.TryAdd(entity.EntityId, entity);" in flush
    assert "Pending[entity.EntityId] = entity;" not in flush


def test_knowledge_v2_session_history_filters_by_shop_before_sql_limit():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    scoped = service[
        service.index("public static List<ConversationSessionLearningReportView> GetReportsForShop("):
        service.index("private static string BuildInputCoverageSummary")
    ]
    assert '"where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc limit "' in scoped
    assert "shopKey, seller" in scoped
    assert "ConversationSessionLearningService.GetReportsForShop(" in ui
    assert "_shopKey, _seller, 1000" in ui

def test_learning_qn_message_listener_registration_uses_weak_instance_identity():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    attach = bridge[
        bridge.index("private static void AttachExisting()"):
        bridge.index("private static void OnRawMessages(")
    ]
    assert "ConditionalWeakTable<QN, object>" in bridge
    assert "lock (AttachSync)" in attach
    assert "Attached.TryGetValue(qn, out marker)" in attach
    assert "Attached.Add(qn, new object());" in attach
    assert "RuntimeHelpers.GetHashCode(qn)" not in attach

def test_windows_reusable_and_standalone_builds_use_distinct_concurrency_groups():
    workflow = read(".github/workflows/windows-build.yml")
    assert "workflow_call:" in workflow
    assert "pull_request:" in workflow
    assert "github.workflow" in workflow
    assert 'group: windows-release-build-${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}' in workflow
    assert "cancel-in-progress: ${{ github.event_name == 'pull_request' }}" in workflow

def test_manual_source_correction_updates_audit_and_visible_history_atomically():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    correction = history[
        history.index("public static bool UpdateAnswerSource(\n            string shopKey"):
        history.index("public static string GetManualSource(", history.index("public static bool UpdateAnswerSource(\n            string shopKey"))
    ]
    assert "DbHelper.Db.SaveRecordsInTransaction(new List<object> { original, row });" in correction
    assert "DbHelper.Db.SaveOneRecord(original);" not in correction
    assert "DbHelper.Db.SaveOneRecord(row);" not in correction

def test_native_send_requires_exact_shop_identity_and_background_events_cannot_switch_shops():
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    active = coordinator[
        coordinator.index("internal static bool IsActive(QN qn)"):
        coordinator.index("internal static void ObserveChatDialogActive")
    ]
    chat_event = coordinator[
        coordinator.index("internal static void ObserveChatDialogActive"):
        coordinator.index("internal static bool ActivateFromFocusedWebView")
    ]
    native = coordinator[
        coordinator.index("internal static bool ValidateNativeSend"):
        coordinator.index("internal static void ReassertCurrent")
    ]
    start = coordinator.index("private static bool Activate(QN qn")
    activate = coordinator[start:coordinator.index("private static bool OwnsAuthoritativeSession", start)]
    authority = coordinator[
        coordinator.index("private static bool OwnsAuthoritativeSession", start):
        coordinator.index("private static string ShopKey(QN qn)", start)
    ]
    assert "_activeShopKey.Length > 0" in active
    assert "shopKey.Length > 0" in active
    assert "string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)" in active
    assert "Activate(qn," in chat_event
    assert "false, evidence" in chat_event
    activation_guard = activate[activate.index("lock (Sync)"):activate.index("_activeShopKey = shopKey;")]
    assert "!focusProof && _activeShopKey.Length > 0" in activation_guard
    assert "!string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)" in activation_guard
    assert "!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)" in activation_guard
    assert "DateTime.UtcNow - _lastFocusProofUtc" not in chat_event
    assert "_activeShopKey.Length == 0 || shopKey.Length == 0" in native
    assert 'reason = "ShopKey缺失或不一致"' in native
    assert "sessionId.Length > 0" not in native
    assert "if (shopKey.Length == 0) return false;" in activate
    for path in (active, native, activate):
        assert "OwnsAuthoritativeSession(qn, shopKey, sessionId)" in path
    assert "!cdp.IsInvalidated && !cdp.IsShopIdentityRetired" in authority
    assert "server.IsAuthoritativeShopSession(shopKey, sessionId)" in authority

def test_legacy_shop_backfill_is_once_per_verified_shop_not_on_every_history_read():
    history = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    backfill = history[
        history.index("private static void BackfillLegacyShopRows("):
        history.index("private static BotConversationHistoryEntity Clone(", history.index("private static void BackfillLegacyShopRows("))
    ]
    assert "ConcurrentDictionary<string, bool> CompletedLegacyShopBackfills" in history
    assert "CompletedLegacyShopBackfills.ContainsKey(backfillKey)" in backfill
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in backfill
    assert "CompletedLegacyShopBackfills.TryAdd(backfillKey, true);" in backfill
    assert backfill.index("CompletedLegacyShopBackfills.TryAdd(backfillKey, true);") > backfill.index(
        "update BotReplyManualSourceEntity set ShopKey = ?"
    )

def test_learning_history_uses_connected_cdp_wrapper_for_exact_shopkey():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    find_qn = context[
        context.index("private static QN FindQn("):
        context.index("private static string RecordMessage")
    ]
    assert "ShopContextLocator.FindConnectedQnByShopKey(shop.ShopKey)" in find_qn
    assert "string.Equals(resolved.ShopKey, shop.ShopKey, StringComparison.Ordinal)" in find_qn
    assert "foreach (var qn in qns)" not in find_qn


def test_pending_learning_resume_covers_full_retention_and_audits_expired_rows():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    resume = service[
        service.index("private static void ResumePending"):
        service.index("private static void CleanupReports")
    ]
    assert '"order by UpdatedAtTicks desc"' in resume
    assert "RestoreKnowledgeCorpusCircuitBreakers(allReports);" in resume
    assert "allReports.Where(x => x.CompletedAtTicks == 0)" in resume
    assert "LastBuyerAtTicks >= ?" not in resume
    assert "ReportRetentionDays" in resume
    assert '"pending_session_exceeded_retention"' in resume
    assert '"pending_session_timestamp_missing"' in resume
    assert "report.CompletedAtTicks = DateTime.Now.Ticks;" in resume


def test_conversation_shop_resolution_prefers_exact_scope_and_active_shop_over_stale_pre_qn():
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    resolver = robot[
        robot.index("private string ResolveShopKeyForConversation"):
        robot.index("private static string BuildConversationKey")
    ]
    assert "ShopSettingsScope.Current" in resolver
    assert "scopedShop.SellerNick" in resolver
    assert "ActiveShopSessionRegistry.GetActiveShopKey()" in resolver
    assert "ActiveShopSessionRegistry.IsActive(activeQn)" in resolver
    assert "FindConnectedQnByShopKey(activeShopKey)" in resolver
    assert "ShopContextLocator.ResolveRuntimeBySeller(activeQn)" in resolver
    assert resolver.index("if (activeShopKey.Length > 0)") < resolver.index(
        "ResolveRuntimeBySellerNick(seller)"
    )




def test_negative_or_non_finite_hasmore_values_are_unknown_not_explicit_server_end():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    parser = context[
        context.index("private static bool TryReadInt"):
        context.index("private static TimelineState GetState")
    ]
    refresh = context[
        context.index("RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert "if (value < 0)" in parser
    assert "value = 0;\n                    return false;" in parser
    assert "double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0" in parser
    assert "hasMoreKnown && hasMore == 0" in refresh
    assert "if (!hasMoreKnown)" in refresh
    assert "if (hasMore > 0)" in refresh


def test_read_only_message_mirror_does_not_require_focus_proof_but_native_actions_still_do():
    registry = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    read_only = registry.split(
        "internal static bool IsCurrentSessionForReadOnlyUi(QN qn)", 1
    )[1].split("internal static void ObserveChatDialogActive", 1)[0]
    active = registry.split(
        "internal static bool IsActive(QN qn)", 1
    )[1].split("internal static bool IsCurrentSessionForReadOnlyUi", 1)[0]
    resolver = mirror.split(
        "private QN ResolveMessageMirrorQn()", 1
    )[1].split("private List<ConversationContextTurn> LoadMessageMirrorTurns", 1)[0]

    assert "OwnsAuthoritativeSession(qn, shopKey, sessionId)" in read_only
    assert "_activeShopKey" in read_only
    assert "_activeSessionId" in read_only
    assert "HasFreshFocusProofLocked" not in read_only
    assert "HasFreshFocusProofLocked()" in active
    assert "IsCurrentSessionForReadOnlyUi(qn)" in resolver
    assert "IsCurrentSessionForReadOnlyUi(current)" in resolver




def test_learning_report_ui_is_bound_to_one_verified_shop():
    ui = read("src/Bot/Options/ConversationSessionLearningUi.cs")
    refresh = ui[
        ui.index("private void RefreshReports()"):
        ui.index("private void ShowSelected()")
    ]
    assert "ShopContextLocator.ResolveCurrentForUi()" in refresh
    assert "GetReportsForShop(" in refresh
    assert "shop.ShopKey, seller, 200" in refresh
    assert "ConversationSessionLearningService.GetReports(200)" not in refresh
    assert "_items.Clear();" in refresh
    assert "已隐藏学习记录" in refresh


def test_commerce_learning_bridge_resolves_report_by_shopkey_not_nickname():
    bridge = read("src/Bot/ChromeNs/CommerceKnowledgeLearningBridge.cs")
    process = bridge[
        bridge.index("private static void ProcessReport("):
        bridge.index("private static bool ApplyScenarioScope(")
    ]
    assert "ResolveRuntimeByShopKey(report.ShopKey)" in process
    assert "shop.SellerNick" in process
    assert "ResolveBySellerNick(report.Seller)" not in process
    assert "Seller与ShopKey不匹配" in process



def test_learning_ingress_dedup_claim_waits_for_verified_shop_seller_and_buyer():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    observe = bridge[
        bridge.index("private static void ObserveMessage("):
        bridge.index("private static void InjectLearnedStyle(")
    ]

    assert observe.index("if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;") < observe.index(
        "var seller = (shop.SellerNick ?? string.Empty).Trim();"
    )
    assert "if (seller.Length == 0" in observe
    assert "var shop = ShopSettingsScope.Current;" in observe
    assert "shop = ShopContextLocator.ResolveRuntimeBySeller(qn);" in observe
    assert observe.index("if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;") < observe.index(
        "var seller = (shop.SellerNick ?? string.Empty).Trim();"
    )
    assert "if (seller.Length == 0" in observe
    assert "if (fromIsSeller == toIsSeller) return;" in observe
    assert "var buyer = fromIsSeller ? to : from;" in observe
    assert observe.index("if (string.IsNullOrWhiteSpace(buyer)" ) < observe.index("Seen.TryAdd(")
    assert observe.index("Seen.TryAdd(") < observe.index("BuyerMessageArchiveService.Observe(")
    assert "standby CDP page may deliver the same payload" in observe


def test_learning_seen_cache_cleanup_removes_only_the_exact_stale_value():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    cleanup = bridge[
        bridge.index("private static void CleanupSeen()"):
        bridge.index("\n        }", bridge.index("private static void CleanupSeen()"))
    ]

    assert "ICollection<KeyValuePair<string, DateTime>>" in cleanup
    assert "new KeyValuePair<string, DateTime>(pair.Key, pair.Value)" in cleanup
    assert "Seen.TryRemove(key, out ignored)" not in cleanup


def test_acceptance_diagnostics_is_read_only_and_redacts_customer_and_account_data():
    script = read("scripts/Invoke-QnBotAcceptanceDiagnostics.ps1")

    assert "#requires -Version 5.1" in script
    assert r'(?i)(?:^|[\s"])--render_id(?:=|\s+)"?bench_im"?(?=$|[\s"])' in script
    assert r'(?i)(?:^|[\s"])--remote-debugging-port(?:=|\s+)"?(\d+)"?(?=$|[\s"])' in script
    assert "CommandLineAvailable = -not [string]::IsNullOrWhiteSpace" in script
    assert "function Get-BotReleaseIdentity" in script
    assert "release-info.json" in script
    assert "BotReleaseTag = $botReleaseTag" in script
    assert "BotReleaseCommit = $botReleaseCommit" in script
    assert "BotVersionSource = $botVersionSource" in script
    assert "VersionSource = $versionSource" in script
    assert "assembly_file_version" in script
    assert "release_manifest" in script
    assert "AliRenderCommandLineVisibleCount" in script
    assert "AliRenderCommandLineUnavailableCount" in script
    assert "QianniuLoopbackListenerCandidates" in script
    assert "$_.AddressKind -eq 'loopback'" in script
    assert "not probed because bench_im identity was not established" in script
    assert "41010" in script
    assert "Runtime.evaluate" in script
    assert "typeof window.imsdk" in script
    assert "window.imsdk && window.imsdk.invoke" in script
    assert "window.imsdk && window.imsdk.on" in script
    assert "Method = 'GET'" in script
    assert "127.0.0.1" in script
    assert "CommandLine" in script
    assert "CustomerMessagesIncluded = $false" in script
    assert "CredentialsIncluded = $false" in script
    assert "TargetUrlsIncluded = $false" in script
    assert "RawCommandLinesIncluded = $false" in script
    for forbidden in [
        "SendTimiMsg",
        "SendTextAsync",
        "SendSmartTipMsg",
        "/v1/chat/completions",
        "Authorization",
        "document.cookie",
        "CookieContainer",
        "Set-Cookie",
        "Get-Content",
        "Invoke-WebRequest",
        "Invoke-RestMethod",
    ]:
        assert forbidden not in script

def test_reply_style_profile_requires_exact_stable_shop_scope():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    profile = service[
        service.index("private static string ProfileKey"):
        service.index("private static string GetStyleProfile")
    ]

    assert "var current = ShopSettingsScope.Current;" in profile
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in profile
    assert "string.IsNullOrWhiteSpace(shop.ShopKey)" in profile
    assert "!shop.HasStableSellerId" in profile
    assert "shop.SellerNick, seller, StringComparison.Ordinal" in profile
    assert "current.ShopKey, shop.ShopKey, StringComparison.Ordinal" in profile
    assert "return string.Empty;" in profile



def test_read_only_acceptance_diagnostics_runtime_snapshot_is_privacy_minimized_and_process_bound():
    script = read("scripts/Invoke-QnBotAcceptanceDiagnostics.ps1")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    snapshot = server[
        server.index("private void WriteReadOnlyAcceptanceSnapshot"):
        server.index("private void TouchSession(WebSocketSession session)")
    ]

    assert "RuntimeSnapshotPath" in script
    assert "Read-RuntimeAcceptanceSnapshot" in script
    assert "ProcessStartTimeUtcTicks" in script
    assert "ageSeconds -gt 120" in script
    assert "process_mismatch" in script
    assert "RuntimeShopSessions = $runtimeSnapshot" in script
    assert "StandbyCapabilitySessions = [int]$rawCounts.StandbyCapabilitySessions" in script
    assert "standby-capable sessions=" in script
    assert "ShopRef" in script and "OwnerSessionRef" in script
    assert "Standbys = standbys" in snapshot
    assert "RawShopKeysIncluded = $false" in script
    assert "RawWebSocketSessionIdsIncluded = $false" in script
    assert "RuntimeAcceptanceDiagnosticRef" in snapshot
    assert "HMACSHA256" in server
    assert "RuntimeAcceptanceDiagnosticSalt" in server
    assert "runtime-snapshot.json" in server
    assert "WriteAllTextAtomic" in snapshot
    assert "ProcessId = processId" in snapshot
    assert "ProcessStartTimeUtcTicks = processStartTimeUtcTicks" in snapshot
    assert "RecoverableStandbySessions = _duplicateShopSessions.Count(x => _recoverableStandbySessions.ContainsKey(x.Key))" in snapshot
    assert "StandbyCapabilitySessions = _recoverableStandbySessions.Count" in snapshot
    for forbidden in ("Seller =", "Buyer =", "ShopKey ="):
        assert forbidden not in snapshot
    assert "SessionRef =" in snapshot


def test_read_only_acceptance_diagnostics_never_connects_to_non_loopback_cdp_targets():
    script = read("scripts/Invoke-QnBotAcceptanceDiagnostics.ps1")
    probe = script[
        script.index("function Invoke-CdpReadonlyProbe"):
        script.index("function Read-RuntimeAcceptanceSnapshot")
    ]

    assert "$uri.Scheme -ne 'ws' -or -not $uri.IsLoopback" in probe
    assert "Runtime.evaluate" in probe
    assert "returnByValue = $true" in probe
    assert "document.cookie" not in probe
    assert "window._vs && window._vs.loginID" in probe

def test_legacy_knowledge_center_history_filters_session_reports_before_limit():
    ui = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    refresh = ui[
        ui.index("public void RefreshData()"):
        ui.index("private void OnRecordsChanged()")
    ]

    assert "ConversationSessionLearningService.GetReportsForShop(shopKey, seller, 500)" in refresh
    assert "ConversationSessionLearningService.GetReports(500)" not in refresh


def test_runtime_observers_use_weak_qn_identity_and_do_not_retain_retired_sessions():
    for path in (
        "src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs",
        "src/Bot/ChromeNs/BuyerIdentityAliasRuntimeBridge.cs",
        "src/Bot/ChromeNs/DirectOrderEventBridge.cs",
        "src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs",
        "src/Bot/ChromeNs/ManualVisualReplyLearningService.cs",
        "src/Bot/ChromeNs/KnowledgeEngineV2FeedbackBridge.cs",
    ):
        source = read(path)
        assert "ConditionalWeakTable<QN, object> Attached" in source
        assert "Attached.TryGetValue(qn, out marker)" in source
        assert "Attached.Add(qn, new object())" in source
        assert "RuntimeHelpers.GetHashCode(qn)" not in source
        assert "ConcurrentDictionary<int, bool> Attached" not in source
        assert "HashSet<QN> Attached" not in source


def test_learning_timeline_archives_buyer_turns_with_canonical_shop_and_alias_identity():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    observe = bridge[
        bridge.index("private static void ObserveMessage"):
        bridge.index("private static void InjectLearnedStyle")
    ]

    assert "var seller = (shop.SellerNick ?? string.Empty).Trim();" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(qnSeller, seller)" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(loginNick, seller)" in observe
    assert "shop.HasStableSellerId" in observe
    assert "sellerEndpointTargetId" in observe
    assert "loginTargetId" in observe
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message)" in observe
    assert "BuyerIdentityAliasService.ResolveInternalNick(seller, buyer)" in observe
    assert "var fromIsBuyer = BuyerIdentityAliasService.AreEquivalent(seller, from, buyer);" in observe
    assert "var isBuyer = fromIsBuyer && toIsSeller;" in observe
    assert "if (isBuyer && !ConversationContextStore.IsPlatformSystemTip(message, text))" in observe


def test_long_lived_runtime_bridges_use_weak_qn_subscription_keys_and_sweep_retired_state():
    for path in (
        "src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs",
        "src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs",
        "src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs",
        "src/Bot/ChromeNs/OrderNotificationTraceBridge.cs",
        "src/Bot/ChromeNs/RechargeStatusAutoQueryService.cs",
        "src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs",
        "src/Bot/Options/LegacyAboutUpdateRedirect.cs",
        "src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Bootstrap.cs",
        "src/Bot/ShopScope/ShopScopedRuntimeBridge.cs",
        "src/Bot/Knowledge/KnowledgeCenterWindow.cs",
        "src/Bot/Options/OrderPlacedReplyDelaySettings.cs",
    ):
        source = read(path)
        assert "ConditionalWeakTable<QN, object>" in source
        assert "ConcurrentDictionary<QN, byte> Attached" not in source
        assert "ConcurrentDictionary<QN, byte> Subscribed" not in source
        assert "ConcurrentDictionary<QN, byte> Qns" not in source
        assert "ConcurrentDictionary<QN, byte> ForegroundSubscribed" not in source
        assert "TryGetValue(qn, out marker)" in source
        assert "Add(qn, new object())" in source

    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "ConditionalWeakTable<QN, object> Subscribed" in monitor
    assert "ConditionalWeakTable<QN, object> VersionGuardLogged" in monitor
    assert "ConcurrentDictionary<QN, byte> Subscribed" not in monitor
    assert "CleanupRetiredQnState(sessions)" in monitor
    assert "NextConversationProbeAt.TryRemove(qn, out ignored)" in monitor
    assert "ConsecutiveProbeFailures.TryRemove(qn, out ignored)" in monitor

    for path in (
        "src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs",
        "src/Bot/Knowledge/KnowledgeCenterWindow.cs",
    ):
        source = read(path)
        assert "CleanupRetiredQnState(sessions)" in source
    business = read("src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs")
    assert "NextHistoryProbeAt.TryRemove(qn, out ignored)" in business
    assert "NextHookRepairAt.TryRemove(qn, out ignored)" in business
    ingress = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    assert "NextProbeAt.TryRemove(qn, out ignored)" in ingress
    assert "NextPanelProbeAt.TryRemove(qn, out ignored)" in ingress


def test_knowledge_policy_fuzzy_search_uses_weak_manager_identity_not_hash_codes():
    source = read("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")
    assert "ConditionalWeakTable<KnowledgeManagerControl, object> FuzzySearchAttached" in source
    assert "FuzzySearchAttached.TryGetValue(manager, out marker)" in source
    assert "FuzzySearchAttached.Add(manager, new object())" in source
    assert "RuntimeHelpers.GetHashCode(manager)" not in source
    assert "FuzzySearchAttached.TryRemove(instanceKey" not in source
