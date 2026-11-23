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


def test_same_buyer_lane_keeps_one_queue_owner_but_does_not_block_new_turns_on_slow_ai():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    lane = source[source.index("private async Task RunAsync"):source.index("private async Task ProcessPreMergeAsync")]

    # One actor still owns PendingRules/Items and is the only place that creates a detached burst.
    assert "state.PendingRules.Enqueue(item)" in source
    assert "var dispatchedItems = state.Items.ToList();" in lane
    assert "state.Items.Clear();" in lane
    assert "StartOwnedDispatch(key, state, burst, lease);" in lane
    assert "await DispatchScopedAsync(burst, lease)" not in lane

    # In-flight generations are tracked by that same state instead of creating a second buyer owner.
    assert "HashSet<Task> InFlightDispatches" in source
    assert "state.InFlightDispatches.Add(task);" in source
    assert "state.InFlightDispatches.Remove(task);" in source
    assert "state.InFlightDispatches.Count == 0" in source
    assert "RetireStateLocked" in source
    assert "state.Retired" in source

    # Generation cancellation releases coordinator ownership even when an upstream task ignores cancellation.
    dispatch = source[source.index("private async Task DispatchOwnedAsync"):source.index("private void OnOwnedDispatchCompleted")]
    assert "Task.WhenAny(dispatchTask, cancelledTask)" in dispatch
    assert "底层非协作任务如继续运行也不再拥有发送/终态资格" in dispatch
    assert "FinalizeReplyOutcome(burst, lease);" in dispatch
    assert "return !state.Retired" in source
    assert "state.HardCancelVersion == capturedHardCancelVersion" in source


def test_human_seller_reply_is_observed_without_invalidating_bot_generation():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")

    assert "ResponseProgressTracker.MarkManualIntervention(seller, buyer, text, shopKey);" in monitor
    assert "qn.CancelActiveBuyerGeneration" not in monitor
    assert "kind != BuyerSessionEventKind.SellerHumanReply" in agent
    assert "人工客服回复但不取消Bot任务" in progress
    manual_start = progress.index("public static void MarkManualIntervention")
    manual_end = progress.index("public static void ObserveNewBuyerTurn", manual_start)
    assert "SendDeliveryWatchdog.CancelConversation" not in progress[manual_start:manual_end]
    assert "Entries.TryRemove" not in progress[manual_start:manual_end]
    manual = learning[
        learning.index("public static bool TryBlockForManualReply"):
        learning.index("public static bool TryTakeSendBlock")
    ]
    assert "QueueManualAnswerComparison" in manual
    assert "SendDeliveryWatchdog.IsKnownBotAnswer" in manual
    assert "ParallelReplyRelevanceGate.IsGreetingOnly(sellerEcho)" in manual
    assert "return true;" in manual
    assert 'SetSendCancellation("人工接管"' in read("src/Bot/ChromeNs/QNRpa.cs")


def test_buyer_session_runtime_bridge_uses_send_ledger_as_only_echo_authorship_source():
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    assert "SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text, shopKey)" in bridge
    assert "IsRecentBotEcho" not in bridge
    assert "qn.Rpa == null || string.IsNullOrWhiteSpace(qn.Rpa.LastSetPlainText)" not in bridge
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
    assert "ScopeKey(seller, shopKey)" in source
    manual_start = source.index("public static void MarkManualIntervention")
    manual_end = source.index("public static void ObserveNewBuyerTurn", manual_start)
    assert "TryRemoveTurn" not in source[manual_start:manual_end]
    assert "RecordCancellation" not in source


def test_text_ai_pipeline_has_one_total_budget_and_terminal_trace_paths():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    trace = read("src/Bot/ChromeNs/MessageProcessingTraceService.cs")
    assert "internal const int TotalAiBudgetSeconds = 120;" in source
    assert "generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));" in source
    assert "StreamPhaseBudgetSeconds" not in source
    assert "StructuredFallbackSeconds" not in source
    assert "timeoutCts" not in source
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
    assert "confidence < 0.95" in source
    assert "ContainsUnsafeManualLearning" in source
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in source
    assert "TryResolveManualEvidenceMessageIds(" in source
    assert "QueueManualAnswerComparison(" in source
    assert "buyerMessageId, humanMessageId" in source
    assert "seller, question, manualAnswer" in source
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
    assert "_sessionAgent.CancelAll(seller, buyer, reason, shopKey)" in burst


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

def test_manual_takeover_blocks_current_send_but_does_not_cancel_other_buyer_generations():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    manual = learning[
        learning.index("public static bool TryBlockForManualReply"):
        learning.index("public static bool TryTakeSendBlock")
    ]

    bot_guard = manual.index("SendDeliveryWatchdog.IsKnownBotAnswer")
    same_answer = manual.index("var sameAsCandidate =")
    block = manual.index("return true;", same_answer)
    greeting = manual.index("ParallelReplyRelevanceGate.IsGreetingOnly(sellerEcho)")
    assert bot_guard < same_answer < block
    assert "!ParallelReplyRelevanceGate.IsGreetingOnly(sellerEcho)" in manual
    assert greeting < block
    assert 'SetSendCancellation("人工接管"' in qnrpa
    assert "自动发送因人工接管取消，禁止重试" in read("src/Bot/ChromeNs/QN.cs")

def test_manual_takeover_is_rechecked_at_each_last_mile_send_path():
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    open_send = qnrpa[
        qnrpa.index("private async Task<bool> OpenAndSendText"):
    ]

    assert "Func<bool> manualTakeoverCheck = () =>" in open_send
    assert "if (manualTakeoverCheck())" in open_send
    assert "shopKey, manualTakeoverCheck" in open_send
    first_check = native.index("manualTakeoverCheck()")
    dom = native.index("TryTriggerSendViaCdpDomAsync")
    hwnd_check = native.index("manualTakeoverCheck()", dom)
    hwnd = native.index("TryPostSafeMainSendMouseMessage", hwnd_check)
    uia_check = native.index("manualTakeoverCheck()", hwnd)
    safe_uia = native.index("TryInvokeCachedSendButtonNow", uia_check)
    legacy_check = native.index("manualTakeoverCheck()", safe_uia)
    legacy = native.index("TrySendTextViaUiaAsync(", legacy_check)
    assert first_check < dom < hwnd_check < hwnd < uia_check < safe_uia < legacy_check < legacy

    uia = qnrpa[
        qnrpa.index("private async Task<bool> TrySendTextViaUiaAsync"):
        qnrpa.index("public async Task SendImageAsync")
    ]
    assert uia.count("manualTakeoverCheck()") >= 2
    assert "ClearExpectedDraftIfSafeAsync(buyer, text, \"人工接管\")" in uia


def test_manual_takeover_marks_pending_delivery_and_suppresses_only_unsubmitted_failure_alerts():
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    method = watchdog[
        watchdog.index("public static int MarkManualTakeover"):
        watchdog.index("public static int CancelConversation")
    ]
    activation = watchdog[
        watchdog.index("private static void Activate"):
        watchdog.index("public static bool ConfirmDelivery")
    ]

    assert "public int SupersededByManual;" in watchdog
    assert "Interlocked.Exchange(ref pending.SupersededByManual, 1);" in method
    suppressed = activation.index("submissionTicks <= 0")
    assert "pending.SupersededByManual" in activation[suppressed:suppressed + 500]
    assert suppressed < activation.index("if (!delivered)")
    assert "if (delivered || submissionTicks > 0)" not in activation


def test_manual_takeover_bypass_is_consumed_once_and_shared_across_final_send_checks():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    method = learning[
        learning.index("public static bool TryBlockForManualReply"):
        learning.index("public static bool TryTakeSendBlock")
    ]
    helper = qnrpa[
        qnrpa.index("private bool TryCancelForManualTakeover"):
        qnrpa.index("private string SellerNick")
    ]
    open_send = qnrpa[
        qnrpa.index("private async Task<bool> OpenAndSendText"):
    ]

    assert "out bool bypassed" in method
    assert "bypassed = true;" in method
    assert "if (bypassed) manualBypassConsumed = true;" in open_send
    assert "if (manualBypassConsumed) return false;" in open_send
    assert "out bypassed" in helper


def test_final_reply_relevance_gate_fails_closed_when_timeline_or_event_read_fails():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = source[
        source.index("public static bool ShouldSend("):
        source.index("internal static class ShopScopedReplyCircuitBreaker")
    ]
    catch = method[method.index("catch (Exception)"):]

    assert "ConversationContextStore.GetRecentTurns" in method
    assert "new BuyerSessionAgent().GetSnapshot" in method
    assert "发送前无法确认会话相关性或人工接管状态，已阻止自动回复" in catch
    assert "Log.ErrorWithMaxCount" in catch
    assert "return false;" in catch
    assert "return true;" not in catch



def test_text_reply_rechecks_human_takeover_after_delay_immediately_before_send():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    start = pipeline.index("private static async Task ProcessTextBurstStreamingAsync")
    end = pipeline.index("private static bool IsLocalKnowledgeDirectAnswerSource", start)
    method = pipeline[start:end]
    final_gate = method.rindex("ParallelReplyRelevanceGate.ShouldSend(")
    assert method.index("await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)") < final_gate
    assert final_gate < method.index('lease.MarkSending("streaming_send_started")')
    assert method.index('lease.MarkSending("streaming_send_started")') < method.index("qn.SendTextWithRetryAsync(")
    assert "streaming_final_relevance_suppressed" in method


def test_vision_reply_rechecks_human_takeover_and_records_send_terminal_state():
    qn = read("src/Bot/ChromeNs/QN.cs")
    start = qn.index("private async Task ProcessVisionBurstAsync")
    end = qn.index("private void AddSkippedConversation", start)
    method = qn[start:end]
    assert "ParallelReplyRelevanceGate.ShouldSend(" in method
    assert method.index("ParallelReplyRelevanceGate.ShouldSend(") < method.index(
        'lease.MarkSending("vision_send_started")'
    ) < method.index("SendTextWithRetryAsync(burst.BuyerNick, answer, 1)")
    assert "vision_final_relevance_suppressed" in method
    assert 'lease.MarkCompleted("vision_send_completed")' in method
    assert 'lease.MarkFailed("vision_send_failed")' in method


def test_qn_rechecks_business_guard_after_send_lock_and_before_each_physical_attempt():
    qn = read("src/Bot/ChromeNs/QN.cs")
    start = qn.index("public async Task<bool> SendTextWithRetryAsync(")
    end = qn.index("private bool IsPreSendGuardSatisfied", start)
    method = qn[start:end]

    assert "Func<bool> preSendGuard = null" in method
    assert "SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken, preSendGuard)" in method
    assert method.count("SendTextAsync(buyer, text, preSendGuard)") >= 2
    assert "await _sendGate.WaitAsync(cancellationToken)" in method
    assert method.index("EnsureActiveBuyerForSendAsync(buyer, cancellationToken)") < method.index(
        'IsPreSendGuardSatisfied(preSendGuard, "首次发送")'
    ) < method.index("var ok = await SendTextAsync(buyer, text, preSendGuard)")
    assert method.index("EnsureActiveBuyerForSendAsync(buyer, cancellationToken)", method.index("var retry =")) < method.index(
        'IsPreSendGuardSatisfied(preSendGuard, "重试发送")'
    ) < method.index("ok = await SendTextAsync(buyer, text, preSendGuard)")
    assert "复核发生异常，已按安全策略阻止发送" in qn


def test_text_and_vision_pipelines_pass_final_guards_into_the_serialized_send_path():
    text_pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    vision_pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    text_send = text_pipeline[
        text_pipeline.index("Func<bool> preSendGuard = () =>"):
        text_pipeline.index("bool sendOk;", text_pipeline.index("Func<bool> preSendGuard = () =>"))
    ]
    assert "lease.IsCurrent" in text_send
    assert "ParallelReplyRelevanceGate.ShouldSend(" in text_send
    assert "ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision)" in text_send
    assert "lease.CancellationToken, preSendGuard" in text_pipeline

    vision_send = vision_pipeline[
        vision_pipeline.index("Func<bool> preSendGuard = () =>"):
        vision_pipeline.index("bool sendOk;", vision_pipeline.index("Func<bool> preSendGuard = () =>"))
    ]
    assert "lifecycleLease.IsCurrent" in vision_send
    assert "VisionImageCacheService.IsWithdrawn(" in vision_send
    assert "latestWithdrawn && !hasFollowUpText" in vision_send
    assert "ParallelReplyRelevanceGate.ShouldSend(" in vision_send
    assert "ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision)" in vision_send
    assert "lifecycleLease.CancellationToken, preSendGuard" in vision_pipeline


def test_final_guard_is_carried_to_the_last_moment_of_each_qianniu_physical_send_path():
    qn = read("src/Bot/ChromeNs/QN.cs")
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")

    assert "SendTextAsync(string buyer, string text, Func<bool> preSendGuard = null)" in qn
    assert "return await rpa.SendTextAsync(buyer, text, preSendGuard)" in qn
    assert "IsPreSendGuardSatisfied(preSendGuard, \"旧版接口发送\")" in qn
    assert "SendTextAsync(buyer, text, preSendGuard)" in qn

    assert "SendTextAsync(string buyer, string text, Func<bool> preSendGuard = null)" in qnrpa
    assert "OpenAndSendText(buyer, text, shopKey, manualBypassConsumed, preSendGuard)" in qnrpa
    assert "manualTakeoverCheck, preSendGuard" in qnrpa
    assert "IsPreSendGuardSatisfied(preSendGuard, \"UIA坐标发送\")" in qnrpa
    assert "IsPreSendGuardSatisfied(preSendGuard, \"UIA主按钮回退\")" in qnrpa

    assert "Func<bool> manualTakeoverCheck, Func<bool> preSendGuard)" in native
    assert "IsPreSendGuardSatisfied(preSendGuard, \"CDP页面发送\")" in native
    assert "IsPreSendGuardSatisfied(preSendGuard, \"HWND安全发送\")" in native
    assert native.index("IsPreSendGuardSatisfied(preSendGuard, \"HWND安全发送\")") < native.index("var moved = PostMessage(target, WmMouseMove")
    assert "IsPreSendGuardSatisfied(preSendGuard, \"安全UIA发送\")" in native
    assert "manualTakeoverCheck, preSendGuard).ConfigureAwait(false)" in native
