from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_v2_optimization_replaces_legacy_v1_optimizer_and_preserves_revision_workflow():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    revision = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    targets = read("src/Directory.Build.targets")
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")

    assert not (ROOT / "src/Bot/Knowledge/KnowledgeOptimizationService.cs").exists()
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeOptimizationUi.cs").exists()
    assert 'Nav("智能导入", () => new KnowledgeV2SmartImportPage' in shell
    assert 'Nav("优化记录", () => new KnowledgeV2AiOptimizationHistoryPage' in shell
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in service
    assert "KnowledgeEngineV2GovernanceAuditService.TryAppendAction" in service
    assert "GenerateRevisionCandidates" in revision
    assert "KnowledgeEngineV2GovernanceAuditService.GetEntries" in ui
    assert "KnowledgeOptimizationService.cs" not in targets
    assert "KnowledgeOptimizationUi.cs" not in targets


def test_order_auto_reply_uses_mandatory_segment_sender_even_after_manual_takeover():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"); app = read("src/Bot/App.xaml.cs"); targets = read("src/Directory.Build.targets")
    assert "presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);" in order
    assert "sendOk = await SendMandatoryOrderTextAsync(plan, answer);" in order
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);" in order
    process = order[order.index("private async Task ProcessOrderPlacedReplyAsync"):]
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process
    assert "InstallOrderAutoReplyGuard" not in app; assert "CtlConversation.OrderAutoReplyGuard.cs" not in targets

def test_order_execution_rebinds_to_hub_authoritative_buyer_before_any_side_effect():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ResolveAuthoritativeBuyerFromHub" in order
    assert "OrderEventType.Created" in order and "OrderEventType.Paid" in order
    assert "OrderEventType.Closed" in order and "OrderEventType.RefundRequested" in order
    assert "order_buyer_authority_conflict" in order
    assert "plan.Buyer = authoritativeBuyer;" in order
    assert "plan.Snapshot.Buyer = authoritativeBuyer;" in order
    begin = order[order.index("internal static bool TryBeginExecution"):order.index("internal static void MarkDeliveryUncertain")]
    assert begin.index("ApplyAuthoritativeBuyerBeforeExecution(plan, out reason)") < begin.index("lock (ActionSync)")
    assert "OrderEventHub.RefreshFromCanonical(probe)" in order
    assert "ReferenceEquals(canonical, probe)" in order

def test_streaming_pipeline_hard_cancels_only_invalid_work_and_allows_relevant_parallel_completion():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"); formatter = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs"); app = read("src/Bot/App.xaml.cs"); targets = read("src/Directory.Build.targets")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs"); bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    assert '["stream"] = true' in pipeline; assert "HttpCompletionOption.ResponseHeadersRead" in pipeline; assert "if (!lease.IsCurrent)" in pipeline
    assert "CreateLinkedTokenSource(lease.CancellationToken)" in pipeline; assert "MonitorLeaseAsync(" not in pipeline; assert "monitorCts" not in pipeline
    assert "普通后续消息和人工回复不取消已派发AI" in pipeline; assert "ParallelReplyRelevanceGate.ShouldSend" in pipeline; assert "买家后续消息明确纠正/取消了前一问题" in pipeline
    assert "internal const int TotalAiBudgetSeconds = 120" in pipeline; assert "CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds))" in pipeline
    assert "StreamPhaseBudgetSeconds" not in pipeline; assert "StreamAttemptDefaultSeconds" not in pipeline; assert "timeoutCts" not in pipeline
    assert "AbsoluteGenerationAgeSeconds = BuyerStreamingReplyPipeline.TotalAiBudgetSeconds + 20" in agent
    assert "+ BuyerSessionAgent.AbsoluteGenerationAgeSeconds" in bridge
    assert "await lease.ConfirmStableAsync(180)" in pipeline; assert "await qn.SendTextWithRetryAsync" in pipeline; assert "正在流式生成答案" in pipeline
    assert "BuyerStreamingReplyPipeline.TotalAiBudgetSeconds" in pipeline
    fallback_start = pipeline.index("MyOpenAI.CallStructuredChat(")
    fallback_end = pipeline.index("),", fallback_start)
    fallback_call = pipeline[fallback_start:fallback_end]
    assert "token" in fallback_call
    assert "true" in fallback_call
    assert "BuyerStreamingReplyPipeline.Initialize();" in app; assert "ChromeNs\\BuyerStreamingReplyPipeline.cs" in targets
    assert 'StreamAbortMarker = "[[QN_STREAM_ABORTED]]"' in formatter; assert "value.IndexOf(StreamAbortMarker" in formatter; assert "已阻止发送半截答案" in formatter; assert "return \"错误：AI流式输出中断" in formatter
