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
        internal const int TotalAiBudgetSeconds = 40;
        private static readonly ConcurrentDictionary<int, bool> PatchedCoordinators =
            new ConcurrentDictionary<int, bool>();
        private static Timer _patchTimer;
        private static int _initialized;

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
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            if (!lease.MarkGenerating("streaming_answer_started"))
            {
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick,
                    "generation已失效，未进入答案生成");
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
                answer = await StreamingBuyerAnswerService.GetAnswerAsync(
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
                    });
            }
            catch (OperationCanceledException)
            {
                if (lease.IsCurrent)
                {
                    var timeout = "错误：AI接口在" + TotalAiBudgetSeconds + "秒总超时预算内未返回有效答案。";
                    if (conversationCtl != null)
                    {
                        conversationCtl.SetProcessing("AI总超时预算已耗尽");
                        conversationCtl.SetStatus(timeout, false);
                    }
                    lease.MarkFailed("streaming_ai_budget_exhausted");
                    ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, timeout);
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
                    ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelled);
                    Log.Info("文本AI流已取消: buyer=" + burst.BuyerNick + ", hardInvalidated=True");
                }
                return;
            }
            catch (Exception ex)
            {
                answer = "错误：流式AI调用失败：" + ex.Message;
                Log.Info("流式AI调用失败: buyer=" + burst.BuyerNick + ", error=" + ex.Message);
            }
            finally
            {
                generationCts.Dispose();
            }

            if (!lease.IsCurrent)
            {
                const string stale = "当前回复任务已被显式失效，AI结果已丢弃";
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing(stale);
                    conversationCtl.SetStatus(stale, false);
                }
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, stale);
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
                    failure);
                Log.Info("流式文本AI失败，保持失败态且不进入答案就绪/完成: buyer="
                    + burst.BuyerNick + ", reason=" + CompactPreview(failure, 180));
                return;
            }

            try
            {
                var deduplication = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    lease.CancellationToken,
                    false);
                answer = deduplication.Answer;
            }
            catch (OperationCanceledException)
            {
                const string cancelledDuringValidation = "generation在发送前答案校验/去重期间已失效";
                if (conversationCtl != null) conversationCtl.SetStatus(cancelledDuringValidation, false);
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, cancelledDuringValidation);
                return;
            }

            if (!await lease.ConfirmStableAsync(180))
            {
                const string unstable = "发送前任务发生显式失效，答案已取消";
                if (conversationCtl != null) conversationCtl.SetStatus(unstable, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, unstable);
                return;
            }

            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, out relevanceReason))
            {
                var suppressed = "并发旧答案已抑制：" + relevanceReason;
                lease.MarkCompleted("streaming_relevance_suppressed");
                if (conversationCtl != null) conversationCtl.SetStatus(suppressed, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, suppressed);
                Log.Info("并发旧答案已抑制: buyer=" + burst.BuyerNick + ", reason=" + relevanceReason);
                return;
            }

            if (!lease.MarkReady("streaming_answer_materialized"))
            {
                const string invalidAtReady = "generation在答案发布前已终止，迟到答案已丢弃";
                if (conversationCtl != null) conversationCtl.SetStatus(invalidAtReady, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidAtReady);
                return;
            }

            var answerReadyAt = DateTime.Now;
            var answerSource = KnowledgeLearningService.ResolveAnswerSource(
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
                answerReadyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("流式文本答案已生成: buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds)
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                lease.MarkCompleted("streaming_answer_generated_only");
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.MarkSending("streaming_send_started"))
            {
                const string invalidBeforeSend = "未发送：generation在发送前已失效";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, invalidBeforeSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidBeforeSend);
                return;
            }

            bool sendOk;
            try
            {
                sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lease.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                const string cancelledDuringSend = "未发送：generation在等待发送资源/会话确认期间已失效";
                if (conversationCtl != null) conversationCtl.SetSendResult(false, cancelledDuringSend);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, cancelledDuringSend);
                Log.Info("Smart Reply发送期间generation硬失效，已停止后续UI发送副作用: buyer="
                    + burst.BuyerNick);
                return;
            }
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))
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
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
        }

        private static string CompactPreview(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : "…" + value.Substring(value.Length - max);
        }
    }

    internal static class ParallelReplyRelevanceGate
    {
        private static readonly Regex SupersedeRegex = new Regex(
            @"(?:^|\s)(?:不是|不对|说错了|我说的是|改一下|改成|算了|不用了|不用|取消|撤回|别回|不要回复|前面错了)(?:$|[，。！？\s])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool ShouldSend(string seller, string buyer, string originalQuestion, DateTime dispatchedAt, out string reason)
        {
            reason = string.Empty;
            try
            {
                var newerBuyer = ConversationContextStore.GetRecentTurns(seller, buyer, originalQuestion, 20)
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
            catch
            {
                return true;
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
        // Keep the provider pipeline well inside BuyerSessionAgent's 55-second absolute-age watchdog.
        // The stream phase has its own budget so multiple endpoints can never consume the entire
        // generation lifetime and starve the structured fallback.
        private const int StreamPhaseBudgetSeconds = 20;
        private const int StreamAttemptDefaultSeconds = 15;
        private const int StreamAttemptMaxSeconds = 18;
        private const int StructuredFallbackSeconds = 15;

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
            Action<string> partial)
        {
            token.ThrowIfCancellationRequested();

            string presetReply;
            if (ConversationContextStore.TryTakeProductLinkReply(seller, buyer, question, out presetReply))
            {
                if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, presetReply))
                {
                    return "错误：该预设回复已被客服撤回，未再次发送。";
                }
                KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, presetReply, "本地");
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
                var handoff = await StreamMessagesAsync(handoffMessages, token, partial);
                if (!string.IsNullOrWhiteSpace(handoff))
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
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fallback, "智能路由-离线知识兜底");
                    MessageProcessingTraceService.RecordKnowledgeDecision(
                        seller, buyer, "AI不可用，使用安全离线知识兜底", "knowledgeId=" + best.Entry.Id, 0);
                    return fallback;
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

            var answer = await StreamMessagesAsync(messages, token, partial);
            if (string.IsNullOrWhiteSpace(answer))
            {
                if (SmartReplyRouterService.CanUseOfflineKnowledgeFallback(plan)
                    && best != null
                    && best.Entry != null)
                {
                    var safeFallback = BotFeatureStore.ApplyOutputPolicy(best.Entry.Answer);
                    KnowledgeLearningService.RegisterAnswerSource(
                        seller, buyer, question, safeFallback, "智能路由-AI失败离线安全兜底");
                    MessageProcessingTraceService.RecordKnowledgeDecision(
                        seller,
                        buyer,
                        "AI接口失败，使用安全离线知识兜底",
                        "knowledgeId=" + best.Entry.Id + "；route=" + plan.Route,
                        0);
                    Log.Info("Smart Reply AI失败后使用安全离线知识兜底: buyer=" + buyer
                        + ", knowledgeId=" + best.Entry.Id);
                    return safeFallback;
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

        private static async Task<string> StreamMessagesAsync(
            JArray messages,
            CancellationToken token,
            Action<string> partial)
        {
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            var errors = new List<string>();
            using (var streamPhaseCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                streamPhaseCts.CancelAfter(TimeSpan.FromSeconds(StreamPhaseBudgetSeconds));
                try
                {
                    foreach (var endpoint in endpoints)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = await StreamOneAsync(endpoint, messages, streamPhaseCts.Token, partial);
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
                            continue;
                        }
                        errors.Add((endpoint.Name ?? "接口") + "：" + result.Error);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw;
                    errors.Add("流式阶段达到" + StreamPhaseBudgetSeconds + "秒预算，提前进入非流式兜底");
                }
            }

            token.ThrowIfCancellationRequested();
            try
            {
                var fallback = await Task.Run(
                    () => MyOpenAI.CallStructuredChat(messages, 220, 0.15, StructuredFallbackSeconds, token),
                    token);
                if (fallback != null && fallback.Success && !string.IsNullOrWhiteSpace(fallback.Answer))
                {
                    var sanitized = ReplyTranscriptSanitizer.Sanitize(fallback.Answer);
                    if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
                    errors.Add("非流式兜底仅返回了内部时间线标签，已丢弃");
                }
                if (fallback != null && !string.IsNullOrWhiteSpace(fallback.Error)) errors.Add(fallback.Error);
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
            var timeoutSeconds = endpoint.TimeoutSeconds <= 0
                ? StreamAttemptDefaultSeconds
                : Math.Max(8, Math.Min(StreamAttemptMaxSeconds, endpoint.TimeoutSeconds));

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token))
                using (var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(endpoint.BaseUrl)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                    request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");

                    using (var response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linked.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var failedBody = await ReadResponseBodyAsync(response.Content, linked.Token);
                            return Fail(sw, payloadText, "HTTP " + (int)response.StatusCode + " " + Short(failedBody, 300));
                        }

                        var mediaType = response.Content.Headers.ContentType == null
                            ? string.Empty
                            : (response.Content.Headers.ContentType.MediaType ?? string.Empty);
                        if (mediaType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var body = await ReadResponseBodyAsync(response.Content, linked.Token);
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
                            var lastPreviewAt = DateTime.MinValue;
                            var lastPreviewLength = 0;

                            while (true)
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                if (pendingRead == null) pendingRead = reader.ReadLineAsync();
                                var completed = await Task.WhenAny(
                                    pendingRead,
                                    Task.Delay(120, linked.Token));
                                if (completed != pendingRead)
                                {
                                    linked.Token.ThrowIfCancellationRequested();
                                    continue;
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
                if (token.IsCancellationRequested) throw;
                return Fail(sw, payloadText, "流式请求超时（" + timeoutSeconds + "秒）");
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
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (pendingRead == null) pendingRead = reader.ReadAsync(buffer, 0, buffer.Length);
                    var completed = await Task.WhenAny(pendingRead, Task.Delay(120, token)).ConfigureAwait(false);
                    if (completed != pendingRead)
                    {
                        token.ThrowIfCancellationRequested();
                        continue;
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
