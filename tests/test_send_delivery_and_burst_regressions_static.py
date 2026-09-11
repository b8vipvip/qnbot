from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_send_watchdog_requires_real_shop_seller_echo_and_queues_ai_report():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    assert "HasRecentSellerEcho" in source
    assert "SendFailureAnomalyService.Queue" in source
    assert "答案已经生成并进入自动发送流程" in source
    assert "VerifyDelayMilliseconds = 9000" in source
    assert "Pending[pending.Id] = pending;" in source
    assert "public static void OnBuyerMessageObserved" in source
    observe_start = source.index("public static void OnBuyerMessageObserved")
    observe_end = source.index("public static void ExpectDelivery", observe_start)
    assert "Pending.TryRemove" not in source[observe_start:observe_end]
    assert "pending.Shop.ShopKey" in source
    assert "FindQn(pending.Shop, pending.Seller)" in source
    assert "Pending.TryRemove(pending.Id" in source


def test_unknown_qianniu_version_cannot_fall_into_smart_tip_false_success_path():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert 'qn.QnVersion = "999.999.999N"' in monitor
    assert "禁止误走SendSmartTipMsg" in monitor
    assert "Version.TryParse" in monitor


def test_same_buyer_worker_retains_ownership_through_ai_dispatch():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    clear_index = source.index("state.Items.Clear();")
    dispatch_index = source.index("await DispatchScopedAsync(burst, lease).ConfigureAwait(false);")
    assert clear_index < dispatch_index
    lane = source[source.index("private async Task RunAsync"):source.index("private async Task ProcessPreMergeAsync")]
    assert "state.WorkerRunning = false;" in lane
    assert lane.index("state.WorkerRunning = false;") < lane.index("return;")
    assert "var dispatchedItems = state.Items.ToList();" in source
    assert "return state.HardCancelVersion == capturedHardCancelVersion;" in source
    assert "state.PendingRules.Enqueue(item)" in source


def test_human_seller_reply_is_observed_without_invalidating_bot_generation():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")

    assert "ResponseProgressTracker.MarkManualIntervention(seller, buyer, text);" in monitor
    assert "qn.CancelActiveBuyerGeneration" not in monitor
    assert "kind != BuyerSessionEventKind.SellerHumanReply" in agent
    assert "人工客服回复但不取消Bot任务" in progress
    manual_start = progress.index("public static void MarkManualIntervention")
    manual_end = progress.index("public static void ObserveNewBuyerTurn", manual_start)
    assert "SendDeliveryWatchdog.CancelConversation" not in progress[manual_start:manual_end]
    assert "Entries.TryRemove" not in progress[manual_start:manual_end]
    assert "QueueManualAnswerComparison" in learning
    assert "return false;" in learning[learning.index("public static bool TryBlockForManualReply"):]


def test_buyer_session_runtime_bridge_uses_send_ledger_as_only_echo_authorship_source():
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    assert "SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text)" in bridge
    assert "IsRecentBotEcho" not in bridge
    assert "LastSetPlainText" not in bridge
    assert "SellerBotEcho" in bridge and "SellerHumanReply" in bridge
    assert "KnownBotAnswers" in watchdog
    assert "ConfirmDelivery" in watchdog
    assert "IsKnownBotAnswer" in watchdog


def test_progress_cards_are_isolated_per_turn_without_cancelling_previous_generation():
    source = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "ConcurrentDictionary<string, string> CurrentTurns" in source
    assert "AsyncLocal<string> OperationTurnKey" in source
    assert "#turn:" in source
    assert "PromoteCurrentTurn" in source
    assert "ConsolidatePendingBurstEntries" in source
    assert "上一条Bot任务继续独立处理，发送前会再次检查相关性" in source
    assert "该条消息已合并到同一轮连续消息中" in source
    assert "ResolveTerminalTurnKey" in source
    assert "ScopeKey(seller)" in source
    manual_start = source.index("public static void MarkManualIntervention")
    manual_end = source.index("public static void ObserveNewBuyerTurn", manual_start)
    assert "TryRemoveTurn" not in source[manual_start:manual_end]
    assert "RecordCancellation" not in source


def test_text_ai_pipeline_has_one_total_budget_and_terminal_trace_paths():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    trace = read("src/Bot/ChromeNs/MessageProcessingTraceService.cs")
    assert "internal const int TotalAiBudgetSeconds = 40;" in source
    assert "generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));" in source
    assert "StreamPhaseBudgetSeconds = 20" in source
    assert "StructuredFallbackSeconds = 15" in source
    assert "ResponseProgressTracker.Fail" in source
    assert "ResponseProgressTracker.Cancel" in source
    assert "RecordKnowledgeDecision" in source
    assert "RecordAiFallbackStarted" in source
    assert "knowledge_decision" in trace
    assert "ai_fallback_started" in trace
    assert "processing_cancelled" in trace


def test_manual_answer_comparison_only_upgrades_safe_high_confidence_knowledge():
    source = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "CompareManualAnswerAsync" in source
    assert "KnowledgeEngineV2Semantics.TextSimilarity" in source
    assert "similarity >= 0.92" in source
    assert "confidence < 0.90" in source
    assert "ContainsUnsafeManualLearning" in source
    assert '"人工对照学习"' in source
    assert "should_learn" in source
    assert "人工答案优先级高于Bot，但不能因为措辞不同就修改知识" in source


def test_buyer_session_agent_keeps_parallel_generations_alive_until_explicit_invalidation():
    source = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    observe_start = source.index("public BuyerSessionAgentObservation ObserveBuyerMessage")
    record_start = source.index("public BuyerSessionEventResult RecordEvent", observe_start)
    observe = source[observe_start:record_start]
    assert "ActiveGenerations" in observe
    assert "previous.Cancel()" not in observe
    assert "SupersededPreviousGeneration = false" in observe
    assert 'superseded=False' in observe
    assert "independentGeneration=True" in observe
    assert "state.ActiveGenerations.TryGetValue(generation" in source
    assert "kind != BuyerSessionEventKind.SellerHumanReply" in source
    assert "public void CancelAll" in source
    assert "CompleteMergedAwayGenerations" in burst
    assert "coalesced_into_generation_" in burst
    assert "_sessionAgent.CancelAll(seller, buyer, reason)" in burst


def test_answer_context_menu_has_copy_action():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'new MenuItem { Header = "复制" }' in source
    assert "Clipboard.SetText(_answer ?? string.Empty);" in source


def test_all_generated_bot_text_replies_get_idempotent_ai_marker():
    formatter = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    flow_test = read("src/Bot/ChromeNs/BotFlowTestService.cs")

    assert 'public const string AiMarker = "[AI]";' in formatter
    assert "BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer)" in formatter
    assert "value.EndsWith(AiMarker, StringComparison.OrdinalIgnoreCase)" in formatter
    assert "BotOutboundMessageFormatter.StripAiMarker(value)" in formatter
    assert qn.count("ReplyDeduplicationService.EnsureDistinct(") >= 2
    assert "BotOutboundMessageFormatter.EnsureAiMarker(" in order
    assert "BotOutboundMessageFormatter.EnsureAiMarker(" in flow_test


def test_control_plane_runtime_guard_is_installed_and_packaged():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    guard = read("services/api-control-plane/runtime_routing_guard.py")
    assert "runtime_routing_guard.install(control_plane)" in bootstrap
    assert "runtime_routing_guard.py" in dockerfile
    assert "RUNTIME_TOTAL_BUDGET_SECONDS" in guard
    assert "RUNTIME_ATTEMPT_TIMEOUT_SECONDS" in guard
    assert "Interleave providers, models and protocols" in guard
    assert "for provider, models, protocols_by_model in prepared" in guard