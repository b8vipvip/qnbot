using Bot.Automation.ChatDeskNs;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class BuyerStreamingReplyPipeline
    {
        // This is the single client-side AI deadline for one buyer generation. The control plane's
        // chat2api browser bridge can legitimately need up to ~90 seconds; routing/knowledge planning
        // happens before the HTTP call, so leave enough room for both without a shorter nested timer.
        internal const int TotalAiBudgetSeconds = 120;
        private static readonly ConcurrentDictionary<int, bool> PatchedCoordinators =
            new ConcurrentDictionary<int, bool>();
        private static Timer _patchTimer;
        private static int _initialized;
        // A synchronous fallback can outlive cancellation even after the buyer
        // generation is finalized. Track such work rather than blocking reply
        // dispatch or pretending it was physically terminated.
        private static int _unfinishedAnswerTasksAfterCancel;
        private static long _answerTasksOutlivedCancellation;
        internal static int UnfinishedAnswerTasksAfterCancel
        {
            get { return Volatile.Read(ref _unfinishedAnswerTasksAfterCancel); }
        }
        internal static long AnswerTasksOutlivedCancellation
        {
            get { return Interlocked.Read(ref _answerTasksOutlivedCancellation); }
        }

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            PatchExisting();
            _patchTimer = new Timer(_ => PatchExisting(), null, 100, 300);
            Log.Info("买家文本回复流式管线已启动：回复模式支持AI优先/本地优先；本地优先只在高置信知识命中时免AI直答，新消息可并发处理；普通后续消息和人工回复不取消已派发AI，只有显式硬失效才取消。" );
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try
                {
                    qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                }
                catch
                {
                    return;
                }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    if (PatchedCoordinators.ContainsKey(key)) continue;

                    var original = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (original == null) continue;
                    Func<BuyerMessageBurstLease, Task> wrapped = lease => HandleAsync(qn, original, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    PatchedCoordinators[key] = true;
                    Log.Info("已为客服实例启用并发Smart Reply流式管线: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装Smart Reply流式管线失败，将继续使用原回复流程：" + ex.Message, 10);
            }
        }

        private static async Task HandleAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> original,
            BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null
                || burst == null
                || burst.Items.Count < 1
                || burst.InputOverflow
                || !burst.HasReplyableItem
                || burst.LatestVisionItem != null)
            {
                await original(lease);
                return;
            }

            await ProcessTextBurstStreamingAsync(qn, lease);
        }

        private static async Task ProcessTextBurstStreamingAsync(QN qn, BuyerMessageBurstLease lease)
        {
            var burst = lease.Burst;
            var workflowDecision = ReceptionWorkflowRuntimeService.EvaluateText(burst);
            if (workflowDecision != null && workflowDecision.SuppressReply)
            {
                var workflowDetectedAt = burst.Items.Min(x => x.ReceivedAt);
                var workflowCtl = ResponseProgressTracker.BeginAnswer(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion,
                    workflowDetectedAt, burst.ShopKey);
                lease.MarkCompleted("reception_workflow_" + (workflowDecision.Action ?? "wait").ToLowerInvariant());
                if (workflowCtl != null) workflowCtl.SetSkipped(workflowDecision.Reason);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                Log.Info("Reception Agent状态机抑制普通AI回复: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", flow=" + workflowDecision.FlowId
                    + ", action=" + workflowDecision.Action);
                return;
            }

            // Preserve approved V2 context matches when V2 cannot safely direct-reply.
            KnowledgeV2RuntimeResult knowledgeV2Result = null;
            if (workflowDecision == null)
            {
                knowledgeV2Result = await KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease);
                if (knowledgeV2Result != null && knowledgeV2Result.Handled) return;
            }
            var sourceClient = qn.CDP;
            var sourceSessionId = sourceClient == null ? string.Empty : sourceClient.SessionId;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt,
                burst.ShopKey);
            if (!lease.MarkGenerating("streaming_answer_started"))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick,
                    "generation已失效，未进入答案生成", burst.ShopKey);
                return;
            }
            var aiStartedAt = DateTime.Now;
            // BuyerSessionAgent owns generation lifetime. Link the AI budget directly to that
            // canonical token instead of polling lease.IsCurrent in a second lifecycle loop.
            var generationCts = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
            generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));

            string answer;
            try
            {
                if (workflowDecision != null && workflowDecision.RequiresSend)
                {
                    answer = workflowDecision.ReplyText;
                    if (conversationCtl != null) conversationCtl.SetProcessing("Reception Agent已匹配已发布流程");
                }
                else
                {
                    var answerTask = StreamingBuyerAnswerService.GetAnswerAsync(
                        burst.SellerNick,
                        burst.BuyerNick,
                        string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion,
                        generationCts.Token,
                        partial =>
                        {
                            if (conversationCtl == null || !lease.IsCurrent) return;
                            var preview = CompactPreview(partial, 110);
                            if (!string.IsNullOrWhiteSpace(preview))
                            {
                                conversationCtl.SetProcessing("正在流式生成答案：" + preview);
                            }
                        },
                        burst.ShopKey,
                        knowledgeV2Result == null ? null : knowledgeV2Result.ContextPromptAddon);
                    // Legacy synchronous fallbacks may run inside Task.Run and cannot always be force-stopped
                    // after they begin. Race the whole answer task only against the canonical generation
                    // budget. Provider/control-plane timeouts remain upstream concerns and must not be
                    // pre-empted by a second shorter Windows timer.
                    answer = await AwaitWithCancellationAsync(answerTask, generationCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                if (lease.IsCurrent)
                {
                    var timeout = "错误：AI接口在" + TotalAiBudgetSeconds + "秒总超时预算内未返回有效答案。";
                    if (conversationCtl != null)
                    {
                        conversationCtl.SetProcessing("AI总超时预算已耗尽");
                        conversationCtl.SetStatus(timeout, false);
                    }
                    lease.MarkFailed("streaming_ai_budget_exhausted");
                    ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, timeout, burst.ShopKey);
                    Log.Info("文本AI流总预算超时: buyer=" + burst.BuyerNick
                        + ", budgetSeconds=" + TotalAiBudgetSeconds);
                }
                else
                {
                    const string cancelled = "当前回复任务已被显式失效，答案不会发送";
                    if (conversationCtl != null)
                    {
                        conversationCtl.SetProcessing(cancelled);
                        conversationCtl.SetStatus(cancelled, false);
                    }
                    ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelled, burst.ShopKey);
                    Log.Info("文本AI流已取消: buyer=" + burst.BuyerNick + ", hardInvalidated=True");
                }
                return;
            }
            catch (Exception ex)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                answer = "错误：流式AI调用失败：" + ex.Message;
                Log.Info("流式AI调用失败: buyer=" + burst.BuyerNick + ", error=" + ex.Message);
            }
            finally
            {
                generationCts.Dispose();
            }

            if (!lease.IsCurrent)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string stale = "当前回复任务已被显式失效，AI结果已丢弃";
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing(stale);
                    conversationCtl.SetStatus(stale, false);
                }
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, stale, burst.ShopKey);
                return;
            }

            // A deliberate workflow wait is a successful no-send decision, not an AI failure.
            // Never expose the internal marker to buyers or to the knowledge-learning lane.
            if (!string.IsNullOrWhiteSpace(answer)
                && answer.StartsWith(ReceptionFlowPolicy.SilentMarker, StringComparison.Ordinal))
            {
                var why = answer.Substring(ReceptionFlowPolicy.SilentMarker.Length);
                lease.MarkCompleted("reception_wait_for_evidence");
                if (conversationCtl != null) conversationCtl.SetSkipped(why);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                Log.Info("接待流程决定暂不回复: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", reason=" + why);
                return;
            }

            var sanitizedAnswer = ReplyTranscriptSanitizer.Sanitize(answer);
            if (!string.Equals(sanitizedAnswer, answer, StringComparison.Ordinal))
            {
                Log.Info("已移除AI回复中的内部时间线前缀: buyer=" + burst.BuyerNick
                    + ", before=" + CompactPreview(answer, 120)
                    + ", after=" + CompactPreview(sanitizedAnswer, 120));
                answer = sanitizedAnswer;
            }

            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                var failure = string.IsNullOrWhiteSpace(answer)
                    ? "错误：AI未返回有效答案。"
                    : answer;
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing("AI未生成可用答案");
                    conversationCtl.SetStatus(failure, false);
                }
                lease.MarkFailed("streaming_answer_invalid");
                ResponseProgressTracker.Fail(
                    burst.SellerNick,
                    burst.BuyerNick,
                    failure,
                    burst.ShopKey);
                Log.Info("流式文本AI失败，保持失败态且不进入答案就绪/完成: buyer="
                    + burst.BuyerNick + ", reason=" + CompactPreview(failure, 180));
                return;
            }

            try
            {
                var deduplication = workflowDecision != null && workflowDecision.RequiresSend
                    ? ReplyDeduplicationService.EnsureWorkflowReplyDistinct(
                        burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion,
                        answer, burst.ShopKey, lease.CancellationToken)
                    : ReplyDeduplicationService.EnsureDistinct(
                        burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion,
                        answer, lease.CancellationToken, false);
                answer = deduplication.Answer;
            }
            catch (OperationCanceledException)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string cancelledDuringValidation = "generation在发送前答案校验/去重期间已失效";
                if (conversationCtl != null) conversationCtl.SetStatus(cancelledDuringValidation, false);
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, cancelledDuringValidation, burst.ShopKey);
                return;
            }

            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string invalidAfterValidation = "发送前答案校验未通过，已阻止自动发送";
                lease.MarkFailed("streaming_pre_send_validation_failed");
                if (conversationCtl != null) conversationCtl.SetStatus(invalidAfterValidation, false);
                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, invalidAfterValidation, burst.ShopKey);
                return;
            }

            if (!await lease.ConfirmStableAsync(180))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string unstable = "发送前任务发生显式失效，答案已取消";
                if (conversationCtl != null) conversationCtl.SetStatus(unstable, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, unstable, burst.ShopKey);
                return;
            }

            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, out relevanceReason,
                false, burst.ShopKey))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                var suppressed = "并发旧答案已抑制：" + relevanceReason;
                lease.MarkCompleted("streaming_relevance_suppressed");
                if (conversationCtl != null) conversationCtl.SetStatus(suppressed, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, suppressed, burst.ShopKey);
                Log.Info("并发旧答案已抑制: buyer=" + burst.BuyerNick + ", reason=" + relevanceReason);
                return;
            }

            string businessBlock;
            var businessBlocked = workflowDecision != null
                ? ShopReceptionPolicy.BlockOutgoing(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer, out businessBlock, burst.ShopKey)
                : ShopReceptionPolicy.BlockOutgoing(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer, out businessBlock);
            if (businessBlocked)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lease.MarkCompleted("reception_business_guard_suppressed");
                if (conversationCtl != null) conversationCtl.SetSkipped(businessBlock);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            if (!lease.MarkReady("streaming_answer_materialized"))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string invalidAtReady = "generation在答案发布前已终止，迟到答案已丢弃";
                if (conversationCtl != null) conversationCtl.SetStatus(invalidAtReady, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidAtReady, burst.ShopKey);
                return;
            }

            var answerReadyAt = DateTime.Now;
            var answerSource = workflowDecision != null && workflowDecision.RequiresSend
                ? "Reception Agent"
                : KnowledgeLearningService.ResolveAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer);
            conversationCtl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                answerSource,
                detectedAt,
                answerReadyAt,
                burst.ShopKey);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("流式文本答案已生成: buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds)
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lease.MarkCompleted("streaming_answer_generated_only");
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            if (IsLocalKnowledgeDirectAnswerSource(answerSource))
            {
                var replyRules = BotFeatureStore.GetAutoReplyRules();
                var directDelaySeconds = Math.Max(0, Math.Min(300,
                    replyRules == null ? 3 : replyRules.KnowledgeDirectReplyDelaySeconds));
                if (directDelaySeconds > 0)
                {
                    var sourceServer = MyWebSocketServer.WSocketSvrInst;
                    if (sourceClient == null || sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired
                        || !string.Equals(sourceClient.ShopKey, burst.ShopKey, StringComparison.Ordinal)
                        || sourceSessionId.Length == 0 || sourceServer == null
                        || !sourceServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId))
                    {
                        const string invalidSourceSession = "未发送：本地知识直答来源CDP不是当前店铺权威会话";
                        if (conversationCtl != null) conversationCtl.SetSendResult(false, invalidSourceSession);
                        ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidSourceSession, burst.ShopKey);
                        return;
                    }
                    if (conversationCtl != null)
                        conversationCtl.SetProcessing("本地知识直答已生成，等待" + directDelaySeconds + "秒发送");
                    Log.Info("Smart Reply本地知识直答等待配置延迟: seller=" + burst.SellerNick
                        + ", buyer=" + burst.BuyerNick + ", source=" + answerSource
                        + ", delaySeconds=" + directDelaySeconds);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds), lease.CancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        const string cancelledDuringDelay = "未发送：知识直答延迟期间本轮已失效";
                        if (conversationCtl != null) conversationCtl.SetSendResult(false, cancelledDuringDelay);
                        ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelledDuringDelay, burst.ShopKey);
                        return;
                    }
                    if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                    {
                        if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：延迟期间Bot或自动回复开关已关闭");
                        lease.MarkCompleted("streaming_knowledge_direct_disabled_during_delay");
                        ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                        return;
                    }
                    var currentClient = qn.CDP;
                    var currentServer = MyWebSocketServer.WSocketSvrInst;
                    if (currentClient == null || !ReferenceEquals(currentClient, sourceClient)
                        || currentClient.IsInvalidated || currentClient.IsShopIdentityRetired
                        || !string.Equals(currentClient.ShopKey, burst.ShopKey, StringComparison.Ordinal)
                        || !string.Equals(currentClient.SessionId, sourceSessionId, StringComparison.Ordinal)
                        || currentServer == null
                        || !currentServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId))
                    {
                        const string changedSourceSession = "未发送：延迟期间本地知识直答原CDP会话已失效或被替换";
                        if (conversationCtl != null) conversationCtl.SetSendResult(false, changedSourceSession);
                        ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, changedSourceSession, burst.ShopKey);
                        return;
                    }
                    if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                        || !await lease.ConfirmStableAsync(80).ConfigureAwait(false))
                    {
                        const string unstableAfterDelay = "未发送：延迟结束后generation或会话稳定性校验失败";
                        if (conversationCtl != null) conversationCtl.SetSendResult(false, unstableAfterDelay);
                        ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, unstableAfterDelay, burst.ShopKey);
                        return;
                    }
                }
            }

            // Human seller replies are observational and intentionally do not cancel the
            // generation. Re-read takeover/timeline evidence after any configured delay
            // and immediately before entering the irreversible send path.
            string finalRelevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                out finalRelevanceReason, false, burst.ShopKey))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                var finalSuppression = "发送前检测到人工接管或买家问题已变化，已阻止旧回复：" + finalRelevanceReason;
                lease.MarkCompleted("streaming_final_relevance_suppressed");
                if (conversationCtl != null) conversationCtl.SetSendResult(false, finalSuppression);
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, finalSuppression, burst.ShopKey);
                Log.Info("文本自动回复最终发送门禁阻止迟到回复: buyer=" + burst.BuyerNick
                    + ", reason=" + finalRelevanceReason);
                return;
            }

            if (workflowDecision != null && workflowDecision.RequiresSend
                && !ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lease.MarkCompleted("reception_workflow_revision_changed_before_send");
                const string staleWorkflow = "未发送：接待流程在发送前已更新或回滚，旧版本回复已取消";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, staleWorkflow);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, staleWorkflow, burst.ShopKey);
                return;
            }

            if (!lease.MarkSending("streaming_send_started"))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                const string invalidBeforeSend = "未发送：generation在发送前已失效";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, invalidBeforeSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidBeforeSend, burst.ShopKey);
                return;
            }

            Func<bool> preSendGuard = () =>
            {
                if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
                    return false;
                string latestRelevanceReason;
                if (!ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestRelevanceReason, false, burst.ShopKey))
                {
                    Log.Info("文本回复等待发送资源期间会话相关性发生变化，已阻止发送: reason="
                        + latestRelevanceReason);
                    return false;
                }
                return workflowDecision == null || !workflowDecision.RequiresSend
                    || ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision);
            };

            bool sendOk;
            try
            {
                sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard);
            }
            catch (OperationCanceledException)
            {
                ReceptionWorkflowRuntimeService.CompleteDecision(workflowDecision, false);
                const string cancelledDuringSend = "未发送：generation在等待发送资源/会话确认期间已失效";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, cancelledDuringSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelledDuringSend, burst.ShopKey);
                Log.Info("Smart Reply发送期间generation硬失效，已停止后续UI发送副作用: buyer="
                    + burst.BuyerNick);
                return;
            }
            var sendCancelled = !sendOk && qn.Rpa != null && qn.Rpa.LastSendWasCancelled;
            if (sendCancelled)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lease.MarkCompleted("streaming_send_cancelled");
                if (conversationCtl != null)
                    conversationCtl.SetSendResult(false, "未发送：人工接管或发送前会话门禁已取消旧回复");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Smart Reply发送前会话门禁取消", burst.ShopKey);
                return;
            }
            ReceptionWorkflowRuntimeService.CompleteDecision(workflowDecision, sendOk);
            if (sendOk)
            {
                if (workflowDecision != null && workflowDecision.RequiresSend)
                    ReplyDeduplicationService.RememberDelivered(
                        burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
                else
                    ReplyDeduplicationService.RememberDelivered(
                        burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
                if (workflowDecision == null && string.Equals(answerSource, "AI生成", StringComparison.Ordinal))
                {
                    KnowledgeLearningService.QueueLearn(
                        burst.CombinedQuestion,
                        answer,
                        "AI生成",
                        burst.SellerNick,
                        burst.BuyerNick);
                }
            }
            if (sendOk)
                lease.MarkCompleted("streaming_send_completed");
            else
                lease.MarkFailed("streaming_send_failed");
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（Smart Reply Router，合并本轮买家消息）"
                        : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
            }
            Log.Info("Smart Reply文本真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
        }

        private static bool IsLocalKnowledgeDirectAnswerSource(string source)
        {
            return string.Equals(source, "智能路由-本地直答", StringComparison.Ordinal)
                || string.Equals(source, "智能路由-离线知识兜底", StringComparison.Ordinal);
        }

        private static async Task<T> AwaitWithCancellationAsync<T>(Task<T> task, CancellationToken token)
        {
            if (task == null) throw new ArgumentNullException("task");
            if (task.IsCompleted) return await task.ConfigureAwait(false);

            var cancelled = Task.Delay(Timeout.Infinite, token);
            var completed = await Task.WhenAny(task, cancelled).ConfigureAwait(false);
            if (completed != task)
            {
                // Never block buyer cancellation on non-cooperative legacy work.
                // Count any unfinished provider task until its natural completion,
                // including faulted/cancelled outcomes, and observe late faults.
                Interlocked.Increment(ref _unfinishedAnswerTasksAfterCancel);
                Interlocked.Increment(ref _answerTasksOutlivedCancellation);
                task.ContinueWith(
                    t =>
                    {
                        try
                        {
                            var observed = t.Exception;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _unfinishedAnswerTasksAfterCancel);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
            }
            return await task.ConfigureAwait(false);
        }

        private static string CompactPreview(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : "…" + value.Substring(value.Length - max);
        }
    }


    /// <summary>
    /// Conservative reception workflow guard. It uses the actual ordered seller/buyer
    /// timeline, not a global keyword match: a phone number is meaningful only when
    /// the most recent seller instruction is waiting for a TV account screenshot.
    /// Device-specific tutorials and recharge eligibility must come from confirmed
    /// knowledge or image evidence, never from this rule.
    /// </summary>
    internal static class ReceptionFlowPolicy
    {
        internal const string SilentMarker = "__QNBOT_RECEPTION_WAIT__:";
        private static readonly Regex MobileOnly = new Regex(
            @"^(?:\+?86[- ]?)?1[3-9]\d{9}$", RegexOptions.Compiled);

        internal static bool TryChooseSilence(
            string seller, string buyer, string question, out string reason)
        {
            reason = string.Empty;
            if (ShopReceptionPolicy.TryWaitForEvidence(seller, buyer, question, out reason))
                return true;
            if (!ShopReceptionPolicy.Applies(seller)
                || !ShopReceptionConfigService.GetConfig(seller).EnablePhoneWait)
                return false;
            var input = (question ?? string.Empty).Trim();
            if (!MobileOnly.IsMatch(input)) return false;
            var recent = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
            var lastSeller = recent.LastOrDefault(x => x != null
                && x.Role == "assistant" && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text));
            if (lastSeller == null || lastSeller.Timestamp == DateTime.MinValue
                || lastSeller.Timestamp < DateTime.Now.AddHours(-2)) return false;
            var instruction = lastSeller.Text ?? string.Empty;
            var config = ShopReceptionConfigService.GetConfig(seller);
            if (!RecentVisualContextService.LooksLikeConfiguredPhotoGuidance(instruction, config))
                return false;
            // A later instruction to send a phone number is a different workflow step.
            if (instruction.IndexOf("手机号", StringComparison.Ordinal) >= 0) return false;
            reason = "等待买家发送目标应用账号界面图片；仅收到手机号，不重复提问或发送无关答案";
            return true;
        }

        internal static string BuildPromptAddon(string seller, string buyer, string question)
        {
            return "\n\n【接待步骤与证据优先】\n"
                + "先根据当前买家消息、最近客服指令、订单阶段、已核验的图片语义和店铺知识库判断本轮步骤。"
                + "如果前面要求拍目标应用账号界面，买家提供图片或追问‘是这个界面吗’，优先核对图片是否真正展示目标账号及绑定状态；"
                + "不能因为商品名称含会员就回答‘包含会员’，也不能把识别到电视品牌当作已经完成账号绑定。"
                + "无法读到可靠的图像证据时不得猜测已绑定或支持充值，应要求清晰的指定页面或保持人工核验。"
                + "有设备专属绑定教程时只能从本店知识库中选取与已确认设备型号一致的教程链接；不能编造教程链接。"
                + "人工客服已经回应或完成当前步骤时不抢答。仅在证据足以推进到下一步骤时回复，不重复上一条指令。\n";
        }
    }

    internal static class ParallelReplyRelevanceGate
    {
        private static readonly Regex SupersedeRegex = new Regex(
            @"(?:^|\s)(?:不是|不对|说错了|我说的是|改一下|改成|算了|不用了|不用|取消|撤回|别回|不要回复|前面错了)(?:$|[，。！？\s])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool IsGreetingOnly(string message)
        {
            var value = (message ?? string.Empty).Trim().Trim('。', '，', ',', '！', '!', '～', '~', ' ');
            return new[] { "在的", "在的亲", "在的亲亲", "您好", "你好", "亲", "在吗", "收到", "稍等", "在的，亲", "在的亲！" }
                .Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        public static bool ShouldSend(string seller, string buyer, string originalQuestion, DateTime dispatchedAt, out string reason, bool trustedReviewedImageAnswer = false, string shopKey = null)
        {
            reason = string.Empty;
            try
            {
                var recent = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24, shopKey);
                // Use the typed BuyerSessionAgent event stream, not every unmarked seller
                // bubble: Qianniu's automatic shop greetings can lack [A], and must not
                // be mistaken for an intentional human takeover.
                // Order-ledger-authorized contractual messages use a separate sender.
                var snapshot = new BuyerSessionAgent().GetSnapshot(seller, buyer, shopKey);
                var newerHuman = snapshot != null
                    && snapshot.RecentEvents != null
                    && snapshot.RecentEvents.Any(x => x != null
                        && x.Kind == BuyerSessionEventKind.SellerHumanReply
                        && x.SourceTimestamp != DateTime.MinValue
                        && x.SourceTimestamp > dispatchedAt.AddMilliseconds(120));
                if (newerHuman)
                {
                    // A short greeting is not an answer to a screenshot. Only an
                    // already-reviewed OCR+Knowledge V2 answer can continue; any
                    // substantive human reply retains the no-duplicate safeguard.
                    var humanEchoes = recent.Where(x => x != null
                        && x.Role == "assistant"
                        && x.Timestamp != DateTime.MinValue
                        && x.Timestamp > dispatchedAt.AddMilliseconds(120)
                        && !string.IsNullOrWhiteSpace(x.Text)
                        && !x.Text.TrimEnd().EndsWith("[A]", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (trustedReviewedImageAnswer && humanEchoes.Count > 0
                        && humanEchoes.All(x => IsGreetingOnly(x.Text)))
                    {
                        reason = "人工仅发送简短接待语，允许已审核OCR知识回答图片问题";
                    }
                    else
                    {
                        reason = "人工客服已在本轮作出实质回复或无法确认仅为接待语，禁止重复回复";
                        return false;
                    }
                }
                var newerBuyer = recent
                    .Where(x => x != null
                        && x.Role == "user"
                        && !x.Withdrawn
                        && x.Timestamp != DateTime.MinValue
                        && x.Timestamp > dispatchedAt.AddMilliseconds(120)
                        && !string.IsNullOrWhiteSpace(x.Text))
                    .OrderBy(x => x.Timestamp)
                    .ToList();
                if (newerBuyer.Count == 0) return true;
                var latest = newerBuyer[newerBuyer.Count - 1].Text ?? string.Empty;
                if (SupersedeRegex.IsMatch(latest))
                {
                    reason = "买家后续消息明确纠正/取消了前一问题";
                    return false;
                }
                reason = "买家有后续消息，但未明确否定前一问题，允许作为补充答案发送";
                return true;
            }
            catch (Exception)
            {
                // This gate protects against a newer buyer correction and a real
                // human takeover. If the timeline/event ledger cannot be read, sending
                // anyway would turn an evidence failure into a duplicate or stale reply.
                reason = "发送前无法确认会话相关性或人工接管状态，已阻止自动回复";
                Log.ErrorWithMaxCount("自动回复最后相关性校验失败；因无法确认最新会话状态而安全抑制发送。", 20);
                return false;
            }
        }
    }

    internal static class ShopScopedReplyCircuitBreaker
    {
        internal const int FailureThreshold = 3;
        internal const int OpenSeconds = 30;

        internal sealed class Permit
        {
            public string ShopKey;
            public long Epoch;
            public bool Allowed;
            public bool HalfOpenProbe;
        }

        private sealed class State
        {
            public readonly object SyncRoot = new object();
            public int ConsecutiveFailures;
            public DateTime OpenUntilUtc = DateTime.MinValue;
            public bool ProbeInFlight;
            public long Epoch;
        }

        private static readonly ConcurrentDictionary<string, State> States =
            new ConcurrentDictionary<string, State>(StringComparer.Ordinal);

        public static Permit TryEnter(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                return new Permit { Allowed = true, ShopKey = string.Empty };

            var state = States.GetOrAdd(shopKey, _ => new State());
            lock (state.SyncRoot)
            {
                var now = DateTime.UtcNow;
                if (state.OpenUntilUtc > now)
                    return new Permit { Allowed = false, ShopKey = shopKey, Epoch = state.Epoch };
                if (state.OpenUntilUtc != DateTime.MinValue)
                {
                    if (state.ProbeInFlight)
                        return new Permit { Allowed = false, ShopKey = shopKey, Epoch = state.Epoch };
                    state.ProbeInFlight = true;
                    return new Permit
                    {
                        Allowed = true,
                        ShopKey = shopKey,
                        Epoch = state.Epoch,
                        HalfOpenProbe = true
                    };
                }
                return new Permit { Allowed = true, ShopKey = shopKey, Epoch = state.Epoch };
            }
        }

        public static void RecordSuccess(Permit permit)
        {
            State state;
            if (permit == null || !permit.Allowed || string.IsNullOrWhiteSpace(permit.ShopKey)
                || !States.TryGetValue(permit.ShopKey, out state)) return;
            lock (state.SyncRoot)
            {
                if (permit.Epoch != state.Epoch) return;
                state.ConsecutiveFailures = 0;
                if (permit.HalfOpenProbe)
                {
                    state.OpenUntilUtc = DateTime.MinValue;
                    state.ProbeInFlight = false;
                    state.Epoch++;
                    Log.Info("Smart Reply店铺AI熔断探测成功，已关闭熔断: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", permit.ShopKey));
                }
            }
        }

        public static void RecordFailure(Permit permit)
        {
            State state;
            if (permit == null || !permit.Allowed || string.IsNullOrWhiteSpace(permit.ShopKey)
                || !States.TryGetValue(permit.ShopKey, out state)) return;
            lock (state.SyncRoot)
            {
                if (permit.Epoch != state.Epoch || state.OpenUntilUtc > DateTime.UtcNow) return;
                state.ConsecutiveFailures++;
                if (permit.HalfOpenProbe || state.ConsecutiveFailures >= FailureThreshold)
                {
                    state.ConsecutiveFailures = FailureThreshold;
                    state.OpenUntilUtc = DateTime.UtcNow.AddSeconds(OpenSeconds);
                    state.ProbeInFlight = false;
                    state.Epoch++;
                    Log.Info("Smart Reply店铺AI连续失败，已开启安全熔断: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", permit.ShopKey)
                        + ", cooldownSeconds=" + OpenSeconds);
                }
            }
        }

        public static void ReleaseProbe(Permit permit)
        {
            State state;
            if (permit == null || !permit.HalfOpenProbe || string.IsNullOrWhiteSpace(permit.ShopKey)
                || !States.TryGetValue(permit.ShopKey, out state)) return;
            lock (state.SyncRoot)
            {
                if (permit.Epoch == state.Epoch) state.ProbeInFlight = false;
            }
        }
    }

    internal static class ReplyTranscriptSanitizer
    {
        internal const string PromptGuard =
            "\n\n输出格式硬规则：历史消息中的 [yyyy-MM-dd HH:mm:ss 客服]、[yyyy-MM-dd HH:mm:ss 买家]、[当前消息 ...] 等内容只是内部时间线标签，不属于回复正文。最终只输出真正要发给买家的正文，禁止把内部日期、时间、客服/买家/assistant/user 说话人标签复制到回复开头，也不要输出‘[当前消息 ...]’。如果买家问题本身确实需要日期或时间，可以在正文中正常回答，但不要使用内部时间线格式。";

        private static readonly Regex BracketedTimelinePrefix = new Regex(
            @"^\s*[\[【［]\s*(?:当前消息\s*)?(?:(?:20\d{2}[-/]\d{1,2}[-/]\d{1,2}|时间未知)\s*)?(?:(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?\s*)?(?:客服|买家|assistant|user)\s*[\]】］]\s*[:：\-—]?\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PlainTimelinePrefix = new Regex(
            @"^\s*(?:(?:20\d{2}[-/]\d{1,2}[-/]\d{1,2}|时间未知)\s+)?(?:(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?\s+)(?:客服|买家|assistant|user)\s*[:：\-—]\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static string Sanitize(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return value;

            for (var i = 0; i < 3; i++)
            {
                var next = BracketedTimelinePrefix.Replace(value, string.Empty, 1).TrimStart();
                if (string.Equals(next, value, StringComparison.Ordinal))
                {
                    next = PlainTimelinePrefix.Replace(value, string.Empty, 1).TrimStart();
                }
                if (string.Equals(next, value, StringComparison.Ordinal)) break;
                value = next;
            }
            return value.Trim();
        }
    }

    internal static class StreamingBuyerAnswerService
    {
        // One buyer generation owns one client-side cancellation deadline. The upstream control plane
        // owns provider-specific attempt/failover timeouts. A shorter stream-phase or per-endpoint
        // timer here previously cut chat2api off before its legitimate browser-bridge budget elapsed.

        private sealed class StreamResult
        {
            public bool Success;
            public string Answer;
            public string Error;
            public long LatencyMs;
            public int InputTokens;
            public int OutputTokens;
        }

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly MethodInfo BuildSystemPromptMethod = typeof(MyOpenAI).GetMethod(
            "BuildSystemPrompt",
            BindingFlags.Static | BindingFlags.NonPublic);

        public static async Task<string> GetAnswerAsync(
            string seller,
            string buyer,
            string question,
            CancellationToken token,
            Action<string> partial,
            string shopKey = null,
            string knowledgeV2ContextAddon = null)
        {
            token.ThrowIfCancellationRequested();

            // A receptionist decides whether to reply BEFORE consulting a matching answer.
            // Silent/wait decisions must not create AI calls or send generic knowledge.
            string silentReason;
            if (ReceptionFlowPolicy.TryChooseSilence(seller, buyer, question, out silentReason))
            {
                return ReceptionFlowPolicy.SilentMarker + silentReason;
            }

            string presetReply;
            if (ConversationContextStore.TryTakeProductLinkReply(seller, buyer, question, out presetReply))
            {
                if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, presetReply))
                {
                    return "错误：该预设回复已被客服撤回，未再次发送。";
                }
                KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, presetReply,
                    BotReplyPresetService.ResolveSourceLabel(seller, presetReply, shopKey));
                MessageProcessingTraceService.RecordKnowledgeDecision(
                    seller, buyer, "命中本地预设回复", "来源=商品链接/预设回复", 0);
                return presetReply;
            }

            if (string.IsNullOrWhiteSpace(question)) return "错误：买家消息为空，未调用AI。";

            var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
            if (manualDecision.Matched)
            {
                HandoffNotificationService.QueueNotify(seller, buyer, question, manualDecision);
                if (!manualDecision.AllowAutoReply)
                {
                    return "错误：命中人工确认规则，未自动回复。" + manualDecision.ReplyText + " 原因：" + manualDecision.Reason;
                }
                if (!manualDecision.UseAiReply)
                {
                    var fixedReply = BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fixedReply, "转人工回复");
                    MessageProcessingTraceService.RecordKnowledgeDecision(
                        seller, buyer, "命中固定自动回复规则", manualDecision.Reason, 0);
                    return fixedReply;
                }

                MessageProcessingTraceService.RecordAiFallbackStarted(
                    seller, buyer, "命中规则但配置为AI生成；reason=" + manualDecision.Reason);
                var handoffMessages = new JArray
                {
                    Message("system", "你是电商店铺的下班转人工助手。当前人工客服已下班。只能礼貌告知人工客服不在线、工作时间，以及问题已记录或建议买家在上班时间联系；不得回答退款、投诉、赔偿、隐私、订单核验等具体高风险结论。回复一句到两句，禁止编造。" + ReplyTranscriptSanitizer.PromptGuard),
                    Message("user", "人工客服工作时间：" + manualDecision.WorkHoursText
                        + "\n触发原因：" + manualDecision.Reason
                        + "\n买家问题：" + question)
                };
                var handoffPermit = ShopScopedReplyCircuitBreaker.TryEnter(shopKey);
                if (!handoffPermit.Allowed)
                    return BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);

                string handoff = string.Empty;
                try
                {
                    handoff = await StreamMessagesAsync(handoffMessages, token, partial);
                    if (string.IsNullOrWhiteSpace(handoff) || handoff.StartsWith("错误：", StringComparison.Ordinal))
                        ShopScopedReplyCircuitBreaker.RecordFailure(handoffPermit);
                    else
                        ShopScopedReplyCircuitBreaker.RecordSuccess(handoffPermit);
                }
                catch (OperationCanceledException)
                {
                    ShopScopedReplyCircuitBreaker.ReleaseProbe(handoffPermit);
                    throw;
                }
                catch (Exception ex)
                {
                    ShopScopedReplyCircuitBreaker.RecordFailure(handoffPermit);
                    Log.Info("下班转人工AI生成失败，使用固定兜底话术：" + ex.GetType().Name);
                }
                if (!string.IsNullOrWhiteSpace(handoff)
                    && !handoff.StartsWith("错误：", StringComparison.Ordinal))
                {
                    handoff = BotFeatureStore.ApplyOutputPolicy(handoff);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, handoff, "转人工回复");
                    return handoff;
                }
                return BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
            }

            var routeStarted = Stopwatch.StartNew();
            var plan = await SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token);
            routeStarted.Stop();
            var best = plan.BestCandidate;
            var replyMode = ReplyModeService.GetMode(seller);
            if (replyMode == BotReplyMode.LocalFirst
                && plan.Route == SmartReplyRouteKind.DirectKnowledge
                && best != null
                && best.Entry != null)
            {
                var directAnswer = BotFeatureStore.ApplyOutputPolicy(best.Entry.Answer);
                if (string.IsNullOrWhiteSpace(directAnswer)
                    || ConversationContextStore.IsWithdrawnAnswer(seller, buyer, directAnswer))
                    return "错误：本地知识回复为空或已被客服撤回，已阻止再次发送。";
                KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, directAnswer, "智能路由-本地直答");
                MessageProcessingTraceService.RecordKnowledgeDecision(
                    seller,
                    buyer,
                    "知识库高置信直答",
                    "knowledgeId=" + best.Entry.Id
                        + "；score=" + best.FinalScore.ToString("0.00")
                        + "；reason=" + plan.Reason,
                    routeStarted.ElapsedMilliseconds);
                Log.Info("本地优先高置信知识直答: buyer=" + buyer
                    + ", knowledgeId=" + best.Entry.Id
                    + ", score=" + best.FinalScore.ToString("0.00")
                    + ", contextDependency=" + plan.ContextDependencyScore.ToString("0.00"));
                return directAnswer;
            }

            MessageProcessingTraceService.RecordKnowledgeDecision(
                seller,
                buyer,
                plan.Route == SmartReplyRouteKind.ContextualKnowledge
                    ? "知识库作为上下文，不直接回答"
                    : "知识库未满足直接回答条件",
                "route=" + plan.Route
                    + "；candidates=" + (plan.Candidates == null ? 0 : plan.Candidates.Count)
                    + "；contextDependency=" + plan.ContextDependencyScore.ToString("0.00")
                    + "；reason=" + plan.Reason,
                routeStarted.ElapsedMilliseconds);

            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints == null || endpoints.Count < 1)
            {
                if (SmartReplyRouterService.CanUseOfflineKnowledgeFallback(plan)
                    && best != null
                    && best.Entry != null)
                {
                    var fallback = BotFeatureStore.ApplyOutputPolicy(best.Entry.Answer);
                    if (!string.IsNullOrWhiteSpace(fallback)
                        && !ConversationContextStore.IsWithdrawnAnswer(seller, buyer, fallback))
                    {
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fallback, "智能路由-离线知识兜底");
                        MessageProcessingTraceService.RecordKnowledgeDecision(
                            seller, buyer, "AI不可用，使用安全离线知识兜底", "knowledgeId=" + best.Entry.Id, 0);
                        return fallback;
                    }
                }
                return "错误：没有可用的AI接口；当前问题需要结合上下文，已阻止直接套用可能不合适的本地固定答案。";
            }

            MessageProcessingTraceService.RecordAiFallbackStarted(
                seller,
                buyer,
                "replyMode=" + ReplyModeService.GetDisplayName(replyMode)
                    + "；route=" + plan.Route
                    + "；reason=" + plan.Reason
                    + "；总预算=" + BuyerStreamingReplyPipeline.TotalAiBudgetSeconds + "秒");

            var primary = endpoints.First();
            var configuredPrompt = string.IsNullOrWhiteSpace(primary.SystemPrompt)
                ? Params.Robot.GetSystemPrompt()
                : primary.SystemPrompt;
            var dynamicSystemPrompt = BuildSystemPrompt(configuredPrompt);
            dynamicSystemPrompt += StorePromptProfileService.BuildPromptAddon();
            dynamicSystemPrompt += ConversationSessionLearningService.BuildReplyStylePromptAddon(seller);

            var contextForRules = new StringBuilder(question);
            foreach (var turn in plan.RecentTurns ?? new List<ConversationContextTurn>())
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                if (contextForRules.Length > 2200) break;
                contextForRules.Append(' ').Append(turn.Text);
            }
            dynamicSystemPrompt += BotFeatureStore.BuildPromptAddon(contextForRules.ToString());
            dynamicSystemPrompt += SmartReplyRouterService.BuildPromptAddon(plan);
            dynamicSystemPrompt += knowledgeV2ContextAddon ?? string.Empty;
            dynamicSystemPrompt += ReceptionFlowPolicy.BuildPromptAddon(seller, buyer, question);
            dynamicSystemPrompt += ShopReceptionPolicy.BuildPromptAddon(seller, buyer);
            dynamicSystemPrompt += ReplyTranscriptSanitizer.PromptGuard;

            var messages = new JArray { Message("system", dynamicSystemPrompt) };
            if (!string.IsNullOrWhiteSpace(plan.ContextDigest))
            {
                messages.Add(Message(
                    "system",
                    "【较早会话压缩摘要】以下仅用于保持上下文连续，不代表新的店铺事实：" + plan.ContextDigest));
            }

            foreach (var turn in plan.RecentTurns ?? new List<ConversationContextTurn>())
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                if (turn.Role != "assistant" && turn.Role != "user") continue;
                var time = turn.Timestamp == DateTime.MinValue
                    ? "时间未知"
                    : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                var speaker = turn.Role == "assistant" ? "客服" : "买家";
                messages.Add(Message(turn.Role, "[" + time + " " + speaker + "] " + turn.Text));
            }
            messages.Add(Message("user", "[当前消息 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 买家] " + question));

            Log.Info("Smart Reply Router选择: buyer=" + buyer
                + ", replyMode=" + ReplyModeService.GetDisplayName(replyMode)
                + ", route=" + plan.Route
                + ", contextDependency=" + plan.ContextDependencyScore.ToString("0.00")
                + ", candidates=" + (plan.Candidates == null ? 0 : plan.Candidates.Count)
                + ", reason=" + plan.Reason);

            var permit = ShopScopedReplyCircuitBreaker.TryEnter(shopKey);
            if (!permit.Allowed)
            {
                if (SmartReplyRouterService.CanUseOfflineKnowledgeFallback(plan)
                    && best != null && best.Entry != null)
                {
                    var safeFallback = BotFeatureStore.ApplyOutputPolicy(best.Entry.Answer);
                    if (!string.IsNullOrWhiteSpace(safeFallback)
                        && !ConversationContextStore.IsWithdrawnAnswer(seller, buyer, safeFallback))
                    {
                        KnowledgeLearningService.RegisterAnswerSource(
                            seller, buyer, question, safeFallback, "智能路由-AI熔断离线安全兜底");
                        MessageProcessingTraceService.RecordKnowledgeDecision(
                            seller, buyer, "AI上游熔断，使用安全离线知识兜底",
                            "knowledgeId=" + best.Entry.Id + "；route=" + plan.Route, 0);
                        return safeFallback;
                    }
                }
                return "错误：当前店铺AI上游处于熔断保护期，且没有可用的安全离线知识，已阻止自动发送并建议转人工。";
            }

            string answer = string.Empty;
            try
            {
                answer = await StreamMessagesAsync(messages, token, partial);
                if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
                    ShopScopedReplyCircuitBreaker.RecordFailure(permit);
                else
                    ShopScopedReplyCircuitBreaker.RecordSuccess(permit);
            }
            catch (OperationCanceledException)
            {
                ShopScopedReplyCircuitBreaker.ReleaseProbe(permit);
                throw;
            }
            catch (Exception ex)
            {
                ShopScopedReplyCircuitBreaker.RecordFailure(permit);
                Log.Info("Smart Reply AI生成异常，进入安全兜底：" + ex.GetType().Name);
            }

            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                if (SmartReplyRouterService.CanUseOfflineKnowledgeFallback(plan)
                    && best != null && best.Entry != null)
                {
                    var safeFallback = BotFeatureStore.ApplyOutputPolicy(best.Entry.Answer);
                    if (!string.IsNullOrWhiteSpace(safeFallback)
                        && !ConversationContextStore.IsWithdrawnAnswer(seller, buyer, safeFallback))
                    {
                        KnowledgeLearningService.RegisterAnswerSource(
                            seller, buyer, question, safeFallback, "智能路由-AI失败离线安全兜底");
                        MessageProcessingTraceService.RecordKnowledgeDecision(
                            seller, buyer, "AI接口失败，使用安全离线知识兜底",
                            "knowledgeId=" + best.Entry.Id + "；route=" + plan.Route, 0);
                        Log.Info("Smart Reply AI失败后使用安全离线知识兜底: buyer=" + buyer
                            + ", knowledgeId=" + best.Entry.Id);
                        return safeFallback;
                    }
                }
                return "错误：所有AI接口均未返回有效答案。";
            }
            answer = ReplyTranscriptSanitizer.Sanitize(answer);
            answer = BotFeatureStore.ApplyOutputPolicy(answer);
            if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer))
            {
                return "错误：该回复已被客服撤回，已阻止再次发送。";
            }

            var source = plan.Route == SmartReplyRouteKind.ContextualKnowledge
                ? "智能路由-知识上下文"
                : "AI生成";
            KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, answer, source);
            if (plan.Route == SmartReplyRouteKind.ContextualKnowledge && best != null && best.Entry != null)
            {
                Log.Info("Smart Reply知识上下文回复生成成功: buyer=" + buyer
                    + ", knowledgeId=" + best.Entry.Id
                    + ", score=" + best.FinalScore.ToString("0.00"));
            }
            return answer;
        }

        private static bool IsRetryableStreamError(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("http 408") || error.Contains("http 425")
                || error.Contains("http 429") || error.Contains("http 500")
                || error.Contains("http 502") || error.Contains("http 503")
                || error.Contains("http 504") || error.Contains("timeout")
                || error.Contains("timed out") || error.Contains("network")
                || error.Contains("connection") || error.Contains("temporarily")
                || error.Contains("socket") || error.Contains("远程主机")
                || error.Contains("网络") || error.Contains("连接");
        }

        private static async Task<string> StreamMessagesAsync(
            JArray messages,
            CancellationToken token,
            Action<string> partial)
        {
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            var errors = new List<string>();
            foreach (var endpoint in endpoints ?? new List<AiEndpointConfig>())
            {
                if (endpoint == null) continue;
                token.ThrowIfCancellationRequested();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var result = await StreamOneAsync(endpoint, messages, token, partial);
                    BotRuntimeStats.RecordAiCall(
                        endpoint,
                        result.InputTokens,
                        result.OutputTokens,
                        result.Success,
                        result.LatencyMs,
                        result.Success ? "流式成功" : result.Error);
                    endpoint.LastLatencyMs = result.LatencyMs;
                    endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                    if (result.Success && !string.IsNullOrWhiteSpace(result.Answer))
                    {
                        var sanitized = ReplyTranscriptSanitizer.Sanitize(result.Answer);
                        if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
                        errors.Add((endpoint.Name ?? "接口") + "：模型仅返回了内部时间线标签，已丢弃");
                        break;
                    }

                    var error = result.Error ?? "未知流式错误";
                    if (attempt == 0 && IsRetryableStreamError(error))
                    {
                        Log.Info("Smart Reply上游瞬时错误，进行一次有界重试: endpoint="
                            + (endpoint.Name ?? "接口") + ", error=" + error);
                        await Task.Delay(TimeSpan.FromMilliseconds(450), token).ConfigureAwait(false);
                        continue;
                    }
                    errors.Add((endpoint.Name ?? "接口") + "：" + error);
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            try
            {
                StructuredChatResult fallback = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    // The linked generation token remains the real deadline. Passing the same total
                    // budget here prevents CallStructuredChat from creating a shorter second window;
                    // whatever time was already spent streaming is automatically subtracted by token.
                    fallback = await Task.Run(
                        () => MyOpenAI.CallStructuredChat(
                            messages,
                            220,
                            0.15,
                            BuyerStreamingReplyPipeline.TotalAiBudgetSeconds,
                            token,
                            true),
                        token);
                    if (fallback != null && fallback.Success && !string.IsNullOrWhiteSpace(fallback.Answer))
                    {
                        var sanitized = ReplyTranscriptSanitizer.Sanitize(fallback.Answer);
                        if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
                        errors.Add("非流式兜底仅返回了内部时间线标签，已丢弃");
                        break;
                    }
                    var fallbackError = fallback == null ? "非流式兜底无结果" : fallback.Error;
                    if (attempt == 0 && IsRetryableStreamError(fallbackError))
                    {
                        errors.Add("非流式兜底：" + fallbackError);
                        await Task.Delay(TimeSpan.FromMilliseconds(450), token).ConfigureAwait(false);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(fallbackError)) errors.Add(fallbackError);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }

            Log.Error("流式AI接口全部失败：" + string.Join("；", errors));
            return string.Empty;
        }

        private static async Task<StreamResult> StreamOneAsync(
            AiEndpointConfig endpoint,
            JArray messages,
            CancellationToken token,
            Action<string> partial)
        {
            var sw = Stopwatch.StartNew();
            var payload = new JObject
            {
                ["model"] = endpoint.TextModel,
                ["messages"] = messages,
                ["temperature"] = 0.15,
                ["max_tokens"] = 220,
                ["stream"] = true
            };
            var payloadText = payload.ToString(Newtonsoft.Json.Formatting.None);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(endpoint.BaseUrl)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                    request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");

                    using (var response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var failedBody = await ReadResponseBodyAsync(response.Content, token);
                            return Fail(sw, payloadText, "HTTP " + (int)response.StatusCode + " " + Short(failedBody, 300));
                        }

                        var mediaType = response.Content.Headers.ContentType == null
                            ? string.Empty
                            : (response.Content.Headers.ContentType.MediaType ?? string.Empty);
                        if (mediaType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var body = await ReadResponseBodyAsync(response.Content, token);
                            var answer = ExtractNormalAnswer(body);
                            if (string.IsNullOrWhiteSpace(answer))
                            {
                                return Fail(sw, payloadText, "接口未返回可解析的流式或普通答案");
                            }
                            return Ok(sw, payloadText, answer);
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var buffer = new StringBuilder();
                            Task<string> pendingRead = null;
                            // One cancellation waiter for the entire stream. The old
                            // 120ms polling allocated a new timer/task roughly 500x
                            // per minute per stalled stream, even with no SSE input.
                            var cancelled = Task.Delay(Timeout.Infinite, token);
                            var lastPreviewAt = DateTime.MinValue;
                            var lastPreviewLength = 0;

                            while (true)
                            {
                                token.ThrowIfCancellationRequested();
                                if (pendingRead == null) pendingRead = reader.ReadLineAsync();
                                var completed = await Task.WhenAny(pendingRead, cancelled);
                                if (completed != pendingRead)
                                {
                                    token.ThrowIfCancellationRequested();
                                    throw new OperationCanceledException(token);
                                }

                                var line = await pendingRead;
                                pendingRead = null;
                                if (line == null) break;
                                line = line.Trim();
                                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                                var data = line.Substring(5).Trim();
                                if (data == "[DONE]") break;

                                string delta;
                                if (!TryExtractDelta(data, out delta) || string.IsNullOrEmpty(delta)) continue;
                                buffer.Append(delta);
                                if (partial != null
                                    && (buffer.Length - lastPreviewLength >= 8
                                        || DateTime.Now - lastPreviewAt >= TimeSpan.FromMilliseconds(180)))
                                {
                                    lastPreviewAt = DateTime.Now;
                                    lastPreviewLength = buffer.Length;
                                    partial(buffer.ToString());
                                }
                            }

                            var answer = buffer.ToString().Trim();
                            if (string.IsNullOrWhiteSpace(answer))
                            {
                                return Fail(sw, payloadText, "流已结束但没有收到文本内容");
                            }
                            if (partial != null) partial(answer);
                            return Ok(sw, payloadText, answer);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The caller's generation token is the only client-side timeout owner.
                throw;
            }
            catch (Exception ex)
            {
                return Fail(sw, payloadText, ex.Message);
            }
        }

        private static async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken token)
        {
            if (content == null) return string.Empty;
            token.ThrowIfCancellationRequested();
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var result = new StringBuilder();
                var buffer = new char[4096];
                Task<int> pendingRead = null;
                var cancelled = Task.Delay(Timeout.Infinite, token);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (pendingRead == null) pendingRead = reader.ReadAsync(buffer, 0, buffer.Length);
                    var completed = await Task.WhenAny(pendingRead, cancelled).ConfigureAwait(false);
                    if (completed != pendingRead)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(token);
                    }

                    var read = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (read <= 0) break;
                    result.Append(buffer, 0, read);
                }
                return result.ToString();
            }
        }

        private static bool TryExtractDelta(string data, out string delta)
        {
            delta = string.Empty;
            try
            {
                var json = JObject.Parse(data);
                var token = json["choices"]?[0]?["delta"]?["content"]
                    ?? json["choices"]?[0]?["text"]
                    ?? json["choices"]?[0]?["message"]?["content"];
                if (token == null) return false;
                delta = token.Type == JTokenType.String
                    ? token.ToString()
                    : token.ToString(Newtonsoft.Json.Formatting.None);
                return !string.IsNullOrEmpty(delta);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractNormalAnswer(string body)
        {
            try
            {
                var json = JObject.Parse(body ?? string.Empty);
                var token = json["choices"]?[0]?["message"]?["content"]
                    ?? json["choices"]?[0]?["text"];
                return token == null ? string.Empty : token.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildSystemPrompt(string configured)
        {
            try
            {
                if (BuildSystemPromptMethod != null)
                {
                    var value = BuildSystemPromptMethod.Invoke(null, new object[] { configured });
                    if (value != null) return Convert.ToString(value);
                }
            }
            catch
            {
            }
            return string.IsNullOrWhiteSpace(configured)
                ? "你是淘宝店铺客服助手。只回复买家当前问题，语气简短自然，不得编造价格、库存、物流、订单状态。"
                : configured.Trim();
        }

        private static JObject Message(string role, string content)
        {
            return new JObject
            {
                ["role"] = role,
                ["content"] = content ?? string.Empty
            };
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }

        private static string NormalizeUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://api.openai.com/v1";
            baseUrl = baseUrl.TrimEnd('/');
            return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl + "/chat/completions";
        }

        private static StreamResult Ok(Stopwatch sw, string payload, string answer)
        {
            sw.Stop();
            return new StreamResult
            {
                Success = true,
                Answer = answer,
                LatencyMs = sw.ElapsedMilliseconds,
                InputTokens = EstimateTokens(payload),
                OutputTokens = EstimateTokens(answer)
            };
        }

        private static StreamResult Fail(Stopwatch sw, string payload, string error)
        {
            sw.Stop();
            return new StreamResult
            {
                Success = false,
                Error = Short(error, 500),
                LatencyMs = sw.ElapsedMilliseconds,
                InputTokens = EstimateTokens(payload)
            };
        }

        private static int EstimateTokens(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Math.Max(1, value.Length / 2);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
