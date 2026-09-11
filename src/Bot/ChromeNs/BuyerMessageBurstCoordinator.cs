using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BuyerMessageBurstItem
    {
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public string MessageKey { get; set; }
        public string DisplayText { get; set; }
        public QNChatMessage Message { get; set; }
        public IncomingMessageDecision SafetyDecision { get; set; }
        public VisionMessageDecision VisionDecision { get; set; }
        public long SortValue { get; set; }
        public DateTime ReceivedAt { get; set; }
        public long SessionGeneration { get; set; }
        public string SemanticContinuationContext { get; set; }

        public BuyerMessageBurstItem()
        {
            ReceivedAt = DateTime.Now;
        }
    }

    internal sealed class BuyerMessageBurst
    {
        public string SellerNick { get; private set; }
        public string BuyerNick { get; private set; }
        public IList<BuyerMessageBurstItem> Items { get; private set; }
        public string CombinedQuestion { get; private set; }
        public string ModelQuestion { get; private set; }
        public int Version { get; private set; }
        public long SessionGeneration { get; private set; }

        public BuyerMessageBurstItem LatestVisionItem
        {
            get
            {
                return Items.LastOrDefault(
                    x => x != null
                        && x.VisionDecision != null
                        && x.VisionDecision.Kind == VisionDecisionKind.Vision);
            }
        }

        public bool HasReplyableItem
        {
            get
            {
                return Items.Any(
                    x => x != null
                        && x.VisionDecision != null
                        && x.VisionDecision.Kind != VisionDecisionKind.Skip);
            }
        }

        public BuyerMessageBurst(
            string sellerNick,
            string buyerNick,
            IEnumerable<BuyerMessageBurstItem> items,
            int version)
        {
            SellerNick = sellerNick ?? string.Empty;
            BuyerNick = buyerNick ?? string.Empty;
            Version = version;
            Items = (items ?? new BuyerMessageBurstItem[0])
                .Where(x => x != null)
                .OrderBy(x => x.SortValue <= 0 ? x.ReceivedAt.Ticks : x.SortValue)
                .ThenBy(x => x.ReceivedAt)
                .ToList();
            SessionGeneration = Items.Count < 1 ? 0 : Items.Max(x => x.SessionGeneration);
            CombinedQuestion = BuildCombinedQuestion(Items);
            var continuation = Items
                .Select(x => (x.SemanticContinuationContext ?? string.Empty).Trim())
                .LastOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(continuation)
                && NormalizeCompare(CombinedQuestion).IndexOf(NormalizeCompare(continuation), StringComparison.Ordinal) < 0)
            {
                ModelQuestion = "【买家当前消息是对上一条未解决问题的省略补充或催问。请把主问题、后续片段以及最近商品/图片/订单上下文作为同一个问题整体理解，只回答一次，不要把‘？’、‘可以吗’、‘能用吗’之类片段当成新主题。】\n主问题："
                    + continuation + "\n后续片段：" + CombinedQuestion;
            }
            else
            {
                ModelQuestion = Items.Count <= 1
                    ? CombinedQuestion
                    : "【买家本轮连续消息，以下按发送顺序】\n" + CombinedQuestion;
            }
        }

        public static string BuildCombinedQuestion(IEnumerable<BuyerMessageBurstItem> items)
        {
            var parts = new List<string>();
            foreach (var item in (items ?? new BuyerMessageBurstItem[0])
                .Where(x => x != null)
                .OrderBy(x => x.SortValue <= 0 ? x.ReceivedAt.Ticks : x.SortValue)
                .ThenBy(x => x.ReceivedAt))
            {
                var text = NormalizeDisplay(item.DisplayText);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var normalized = NormalizeCompare(text);
                if (parts.Count > 0)
                {
                    var previous = parts[parts.Count - 1];
                    var previousNormalized = NormalizeCompare(previous);
                    if (normalized == previousNormalized) continue;
                    if (previousNormalized.Length <= 16
                        && normalized.Length > previousNormalized.Length
                        && normalized.StartsWith(previousNormalized, StringComparison.Ordinal))
                    {
                        parts[parts.Count - 1] = text;
                        continue;
                    }
                    if (normalized.Length <= 8
                        && previousNormalized.Length > normalized.Length
                        && previousNormalized.EndsWith(normalized, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                parts.Add(text);
            }

            if (parts.Count > 10) parts = parts.Skip(parts.Count - 10).ToList();
            var combined = string.Join("\n", parts);
            return combined.Length <= 1600 ? combined : combined.Substring(combined.Length - 1600);
        }

        private static string NormalizeDisplay(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Trim();
            value = Regex.Replace(value, @"[ \t]+", " ");
            value = Regex.Replace(value, @"\n{3,}", "\n\n");
            return value;
        }

        private static string NormalizeCompare(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }
    }

    internal sealed class BuyerMessageBurstLease
    {
        private readonly Func<bool> _isCurrent;
        private readonly BuyerSessionAgent _sessionAgent;

        public BuyerMessageBurst Burst { get; private set; }

        public bool IsCurrent
        {
            get
            {
                return _isCurrent != null
                    && _isCurrent()
                    && (_sessionAgent == null
                        || _sessionAgent.IsCurrent(Burst.SellerNick, Burst.BuyerNick, Burst.SessionGeneration));
            }
        }

        public CancellationToken CancellationToken
        {
            get
            {
                return _sessionAgent == null
                    ? CancellationToken.None
                    : _sessionAgent.GetCancellationToken(Burst.SellerNick, Burst.BuyerNick, Burst.SessionGeneration);
            }
        }

        public BuyerMessageBurstLease(
            BuyerMessageBurst burst,
            Func<bool> isCurrent,
            BuyerSessionAgent sessionAgent = null)
        {
            Burst = burst;
            _isCurrent = isCurrent;
            _sessionAgent = sessionAgent;
        }

        public async Task<bool> ConfirmStableAsync(int milliseconds)
        {
            try
            {
                await Task.Delay(Math.Max(0, milliseconds), CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return IsCurrent && !CancellationToken.IsCancellationRequested;
        }

        public bool MarkProcessing(string reason) { return Transition(BuyerSessionAgentState.Processing, reason); }
        public bool MarkGenerating(string reason) { return Transition(BuyerSessionAgentState.Generating, reason); }
        public bool MarkReady(string reason) { return Transition(BuyerSessionAgentState.Ready, reason); }
        public bool MarkSending(string reason) { return Transition(BuyerSessionAgentState.Sending, reason); }
        public bool MarkWaiting(string reason) { return Transition(BuyerSessionAgentState.Waiting, reason); }
        public bool MarkCompleted(string reason) { return Transition(BuyerSessionAgentState.Completed, reason); }
        public bool MarkFailed(string reason) { return Transition(BuyerSessionAgentState.Failed, reason); }

        private bool Transition(BuyerSessionAgentState state, string reason)
        {
            return _sessionAgent != null
                && _sessionAgent.TryTransition(
                    Burst.SellerNick,
                    Burst.BuyerNick,
                    Burst.SessionGeneration,
                    state,
                    reason);
        }
    }

    internal enum CanonicalPreMergeOutcome
    {
        Continue = 0,
        Consumed = 1,
        Failed = 2,
        Cancelled = 3
    }

    /// <summary>
    /// Pure policy/transport helper. It deliberately owns no same-buyer semaphore, worker,
    /// BuyerSessionAgent terminal transition, or BotActivityLease. BuyerMessageBurstCoordinator is
    /// the only runtime owner of those concerns.
    /// </summary>
    internal static class CanonicalPreMergeDecisionService
    {
        private const string DefaultOffHoursReply =
            "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。";
        private const int OffHoursRepeatMinutes = 2;
        private static readonly ConcurrentDictionary<string, DateTime> OffHoursDeliveredUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public static async Task<CanonicalPreMergeOutcome> HandleAsync(
            BuyerMessageBurstItem item,
            bool allowLocalShortReply,
            CancellationToken cancellationToken)
        {
            try { Bot.Knowledge.LocalShortReplyUi.Initialize(); } catch { }
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick)
                || string.IsNullOrWhiteSpace(item.DisplayText)
                || !Params.Robot.CanUseRobotReal)
                return CanonicalPreMergeOutcome.Continue;
            if (item.SafetyDecision != null && !item.SafetyDecision.ShouldCallAi)
                return CanonicalPreMergeOutcome.Continue;
            if (item.VisionDecision != null && item.VisionDecision.Kind == VisionDecisionKind.Skip)
                return CanonicalPreMergeOutcome.Continue;

            cancellationToken.ThrowIfCancellationRequested();
            ShopContext shop = null;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(item.SellerNick); }
            catch { shop = null; }
            if (shop == null)
            {
                Log.ErrorWithMaxCount(
                    "统一买家生命周期缺少店铺作用域，固定规则跳过并继续普通消息链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick, 20);
                return CanonicalPreMergeOutcome.Continue;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await HandleScopedAsync(item, allowLocalShortReply, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<CanonicalPreMergeOutcome> HandleScopedAsync(
            BuyerMessageBurstItem item,
            bool allowLocalShortReply,
            CancellationToken cancellationToken)
        {
            if (!Params.Robot.GetIsAutoReply()) return CanonicalPreMergeOutcome.Continue;
            var qn = QN.FindExistingBySellerNick(item.SellerNick);
            if (qn == null)
            {
                Log.ErrorWithMaxCount(
                    "统一买家生命周期固定规则未发送：找不到客服运行实例。seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick, 20);
                return CanonicalPreMergeOutcome.Continue;
            }

            var question = (item.DisplayText ?? string.Empty).Trim();
            var buyerKey = Key(item.SellerNick, item.BuyerNick);
            string offHoursReply;
            if (TryResolveOffHours(out offHoursReply))
            {
                DateTime until;
                if (!OffHoursDeliveredUntil.TryGetValue(buyerKey, out until) || until <= DateTime.Now)
                {
                    var ok = await SendFixedAsync(
                        qn, item, offHoursReply, "下班自动回复", cancellationToken).ConfigureAwait(false);
                    OffHoursDeliveredUntil[buyerKey] = ok
                        ? DateTime.Now.AddMinutes(OffHoursRepeatMinutes)
                        : DateTime.Now.AddSeconds(15);
                    return ok ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;
                }
                return CanonicalPreMergeOutcome.Consumed;
            }

            // Product cards/URLs are non-substantive context and must never consume the first-inquiry
            // slot. IncomingMessageSafety registers the shop-scoped local preset specifically so the
            // canonical buyer owner can send it without entering burst/Knowledge/AI at all.
            string productLinkReply;
            if (ConversationContextStore.TryTakeProductLinkReply(
                item.SellerNick, item.BuyerNick, question, out productLinkReply))
            {
                var productOk = await SendFixedAsync(
                    qn, item, productLinkReply, "商品链接预设回复", cancellationToken).ConfigureAwait(false);
                Log.Info("商品链接预设回复由统一买家生命周期消费: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", success=" + productOk + ", aiCalled=false");
                return productOk ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;
            }

            string firstReply;
            var firstReserved = FirstInquiryFixedReplyService.TryResolve(
                item.SellerNick, item.BuyerNick, question, out firstReply);
            if (firstReserved)
            {
                var firstOk = await SendFixedAsync(
                    qn, item, firstReply, "首条咨询固定回复", cancellationToken).ConfigureAwait(false);
                if (firstOk)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(item.SellerNick, item.BuyerNick);
                }
                else
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        item.SellerNick,
                        item.BuyerNick,
                        qn.Rpa == null ? "首条咨询固定回复发送失败" : qn.Rpa.GetSendFailureReason());
                    return cancellationToken.IsCancellationRequested
                        ? CanonicalPreMergeOutcome.Cancelled
                        : CanonicalPreMergeOutcome.Failed;
                }
            }

            if (allowLocalShortReply)
            {
                var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
                if (manualDecision == null || !manualDecision.Matched)
                {
                    string localAnswer;
                    string matchedPhrase;
                    if (LocalShortReplyService.TryResolve(
                        item.SellerNick, question, out localAnswer, out matchedPhrase))
                    {
                        if (firstReserved)
                        {
                            Log.Info("本地短消息已由首条咨询固定回复覆盖，继续同一实际问题普通回复链路: seller="
                                + item.SellerNick + ", buyer=" + item.BuyerNick + ", phrase=" + matchedPhrase);
                            return CanonicalPreMergeOutcome.Continue;
                        }
                        var localOk = await SendFixedAsync(
                            qn, item, localAnswer, "本地短消息回复", cancellationToken).ConfigureAwait(false);
                        Log.Info("本地短消息精确命中: seller=" + item.SellerNick
                            + ", buyer=" + item.BuyerNick + ", phrase=" + matchedPhrase
                            + ", success=" + localOk + ", aiCalled=false");
                        return localOk ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;
                    }
                }
            }

            return CanonicalPreMergeOutcome.Continue;
        }

        private static async Task<bool> SendFixedAsync(
            QN qn,
            BuyerMessageBurstItem item,
            string answer,
            string source,
            CancellationToken cancellationToken)
        {
            answer = BotOutboundMessageFormatter.EnsureAiMarker((answer ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(answer)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            var detectedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt;
            var ctl = ResponseProgressTracker.BeginAnswer(
                item.SellerNick, item.BuyerNick, item.DisplayText, detectedAt);
            try
            {
                KnowledgeLearningService.RegisterAnswerSource(
                    item.SellerNick, item.BuyerNick, item.DisplayText, answer, source);
                ctl = ResponseProgressTracker.SetAnswerReady(
                    item.SellerNick, item.BuyerNick, item.DisplayText, answer, source, detectedAt, DateTime.Now);
                BotRuntimeStats.RecordDisplayedAnswer(true);
                Log.Info(source + "由统一买家生命周期在消息合并前命中，不等待合并窗口、不检查AI接口: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick);
                cancellationToken.ThrowIfCancellationRequested();
                var ok = await qn.SendTextWithRetryAsync(
                    item.BuyerNick, answer, 3, cancellationToken).ConfigureAwait(false);
                if (ok)
                    ReplyDeduplicationService.RememberDelivered(item.SellerNick, item.BuyerNick, answer);
                if (ctl != null)
                    ctl.SetSendResult(ok, ok
                        ? "已发送（" + source + "，由统一买家生命周期前置处理）"
                        : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
                Log.Info(source + "前置真实发送完成: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", success=" + ok);
                return ok;
            }
            catch (OperationCanceledException)
            {
                if (ctl != null) ctl.SetSendResult(false, "generation已失效，固定回复发送已取消");
                return false;
            }
            catch (Exception ex)
            {
                if (ctl != null) ctl.SetSendResult(false, "发送失败：" + ex.Message);
                Log.ErrorWithMaxCount(source + "前置发送异常: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", error=" + ex.Message, 20);
                return false;
            }
            finally
            {
                ResponseProgressTracker.Complete(item.SellerNick, item.BuyerNick);
            }
        }

        private static bool TryResolveOffHours(out string answer)
        {
            answer = string.Empty;
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableWorkHours) return false;
            TimeSpan start;
            TimeSpan end;
            if (!TryParseClock(cfg.WorkStartTime, out start)
                || !TryParseClock(cfg.WorkEndTime, out end))
            {
                Log.ErrorWithMaxCount(
                    "下班自动回复工作时间配置无效，已停止固定回复。 workStart="
                    + (cfg.WorkStartTime ?? string.Empty) + ", workEnd=" + (cfg.WorkEndTime ?? string.Empty), 20);
                return false;
            }
            if (IsInsideWorkHours(DateTime.Now.TimeOfDay, start, end)) return false;
            var template = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? DefaultOffHoursReply
                : cfg.OffHoursFixedText.Trim();
            answer = template.Replace("{工作时间}", FormatClock(start) + "-" + FormatClock(end));
            return !string.IsNullOrWhiteSpace(answer);
        }

        private static bool TryParseClock(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            DateTime parsed;
            if (!DateTime.TryParseExact(
                (value ?? string.Empty).Trim(),
                new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsed)) return false;
            time = parsed.TimeOfDay;
            return true;
        }

        private static bool IsInsideWorkHours(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            if (start == end) return true;
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;
        }

        private static string FormatClock(TimeSpan value)
        {
            return ((int)value.TotalHours).ToString("00") + ":" + value.Minutes.ToString("00");
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }
    }

    internal sealed class BuyerMessageBurstCoordinator
    {
        private sealed class BurstState
        {
            public readonly object Sync = new object();
            public readonly Queue<BuyerMessageBurstItem> PendingRules = new Queue<BuyerMessageBurstItem>();
            public readonly List<BuyerMessageBurstItem> Items = new List<BuyerMessageBurstItem>();
            public readonly HashSet<Task> InFlightDispatches = new HashSet<Task>();
            public CancellationTokenSource DelayCancellation = new CancellationTokenSource();
            public bool WorkerRunning;
            public bool Retired;
            public int Version;
            public int HardCancelVersion;
            public DateTime StartedAt = DateTime.MinValue;
            public BotActivityLease ActivityLease;
            public long LatestSessionGeneration;
        }

        private sealed class RecentBuyerText
        {
            public string AnchorText { get; set; }
            public DateTime AnchorReceivedAt { get; set; }
            public long AnchorGeneration { get; set; }
            public string LatestText { get; set; }
            public DateTime LatestReceivedAt { get; set; }
            public long LatestGeneration { get; set; }
        }

        private const int SemanticContinuationWindowSeconds = 180;
        private static readonly SemaphoreSlim LegacyAiConfigurationGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, BurstState> _states =
            new ConcurrentDictionary<string, BurstState>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RecentBuyerText> _recentBuyerTexts =
            new ConcurrentDictionary<string, RecentBuyerText>(StringComparer.Ordinal);
        private readonly Func<BuyerMessageBurstLease, Task> _handler;
        private readonly BuyerSessionAgent _sessionAgent = new BuyerSessionAgent();

        public BuyerMessageBurstCoordinator(Func<BuyerMessageBurstLease, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _handler = handler;
            try { Bot.Knowledge.LocalShortReplyUi.Initialize(); } catch { }
        }

        internal BuyerSessionAgent SessionAgent { get { return _sessionAgent; } }

        public void Enqueue(BuyerMessageBurstItem item)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick)) return;

            var observation = _sessionAgent.ObserveBuyerMessage(
                item.SellerNick,
                item.BuyerNick,
                item.MessageKey,
                item.SortValue,
                item.ReceivedAt);
            if (observation.Duplicate)
            {
                Log.Info("BuyerSessionAgent已跨入口去重，本条消息不再进入规则/合并/AI链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick + ", key=" + (item.MessageKey ?? string.Empty));
                return;
            }

            item.SessionGeneration = observation.Generation;
            AttachSemanticContinuation(item);
            RememberRecentBuyerText(item);
            _sessionAgent.TryTransition(
                item.SellerNick,
                item.BuyerNick,
                item.SessionGeneration,
                BuyerSessionAgentState.Coalescing,
                "single_owner_lane");

            var key = Key(item.SellerNick, item.BuyerNick);
            while (true)
            {
                var state = _states.GetOrAdd(key, _ => new BurstState());
                var startWorker = false;
                var accepted = false;
                lock (state.Sync)
                {
                    if (!state.Retired)
                    {
                        state.PendingRules.Enqueue(item);
                        state.LatestSessionGeneration = item.SessionGeneration;
                        accepted = true;
                        if (!state.WorkerRunning)
                        {
                            state.WorkerRunning = true;
                            startWorker = true;
                        }
                    }
                }
                if (!accepted) continue;
                if (startWorker) Task.Run(() => RunAsync(key, state));
                return;
            }
        }

        private async Task RunAsync(string key, BurstState state)
        {
            try
            {
                while (true)
                {
                    BuyerMessageBurstItem ruleItem = null;
                    lock (state.Sync)
                    {
                        if (state.PendingRules.Count > 0)
                            ruleItem = state.PendingRules.Dequeue();
                        else if (state.Items.Count < 1)
                        {
                            state.WorkerRunning = false;
                            if (state.InFlightDispatches.Count == 0)
                            {
                                RetireStateLocked(key, state);
                            }
                            return;
                        }
                    }

                    if (ruleItem != null)
                    {
                        await ProcessPreMergeAsync(key, state, ruleItem).ConfigureAwait(false);
                        continue;
                    }

                    CancellationToken delayToken;
                    int capturedVersion;
                    int capturedHardCancelVersion;
                    int delayMilliseconds;
                    lock (state.Sync)
                    {
                        if (state.PendingRules.Count > 0) continue;
                        if (state.Items.Count < 1) continue;
                        delayToken = state.DelayCancellation.Token;
                        capturedVersion = state.Version;
                        capturedHardCancelVersion = state.HardCancelVersion;
                        delayMilliseconds = QuietDelayMilliseconds(state.Items, state.StartedAt);
                    }

                    try
                    {
                        await Task.Delay(delayMilliseconds, delayToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }

                    BuyerMessageBurst burst;
                    lock (state.Sync)
                    {
                        if (state.PendingRules.Count > 0) continue;
                        if (state.Version != capturedVersion || state.Items.Count < 1) continue;
                        var dispatchedItems = state.Items.ToList();
                        state.Items.Clear();
                        state.StartedAt = DateTime.MinValue;
                        burst = new BuyerMessageBurst(
                            dispatchedItems[0].SellerNick,
                            dispatchedItems[0].BuyerNick,
                            dispatchedItems,
                            capturedVersion);
                    }

                    CompleteMergedAwayGenerations(burst);
                    if (!_sessionAgent.IsCurrent(burst.SellerNick, burst.BuyerNick, burst.SessionGeneration))
                    {
                        Log.Info("统一买家生命周期派发前最终generation已失效，跳过本轮回复: seller="
                            + burst.SellerNick + ", buyer=" + burst.BuyerNick
                            + ", generation=" + burst.SessionGeneration);
                        continue;
                    }

                    var lease = new BuyerMessageBurstLease(
                        burst,
                        () =>
                        {
                            lock (state.Sync)
                            {
                                return !state.Retired
                                    && state.HardCancelVersion == capturedHardCancelVersion;
                            }
                        },
                        _sessionAgent);
                    lease.MarkProcessing("single_owner_dispatch");
                    lease.MarkGenerating("reply_generation_started");
                    StartOwnedDispatch(key, state, burst, lease);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("统一买家生命周期工作器异常，已释放本买家队列运行权: error=" + Safe(ex.Message, 220), 50);
                lock (state.Sync)
                {
                    foreach (var pending in state.PendingRules.Concat(state.Items).Where(x => x != null).ToList())
                    {
                        if (pending.SessionGeneration > 0)
                            _sessionAgent.TryTransition(
                                pending.SellerNick,
                                pending.BuyerNick,
                                pending.SessionGeneration,
                                BuyerSessionAgentState.Failed,
                                "single_owner_worker_exception");
                    }
                    state.PendingRules.Clear();
                    state.Items.Clear();
                    state.WorkerRunning = false;
                    if (state.InFlightDispatches.Count == 0)
                    {
                        RetireStateLocked(key, state);
                    }
                }
            }
        }

        private void StartOwnedDispatch(
            string key,
            BurstState state,
            BuyerMessageBurst burst,
            BuyerMessageBurstLease lease)
        {
            var task = DispatchOwnedAsync(burst, lease);
            lock (state.Sync)
            {
                if (state.Retired) return;
                state.InFlightDispatches.Add(task);
            }
            task.ContinueWith(
                _ => OnOwnedDispatchCompleted(key, state, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task DispatchOwnedAsync(BuyerMessageBurst burst, BuyerMessageBurstLease lease)
        {
            try
            {
                var dispatchTask = DispatchScopedAsync(burst, lease);
                var cancelledTask = Task.Delay(Timeout.Infinite, lease.CancellationToken);
                var winner = await Task.WhenAny(dispatchTask, cancelledTask).ConfigureAwait(false);
                if (!ReferenceEquals(winner, dispatchTask))
                {
                    dispatchTask.ContinueWith(
                        t => Log.ErrorWithMaxCount(
                            "已取消generation的底层回复任务迟到异常已观察并隔离: "
                            + Safe(t.Exception == null ? string.Empty : t.Exception.GetBaseException().Message, 220),
                            20),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    Log.Info("统一买家生命周期已在generation取消时释放回复占用；底层非协作任务如继续运行也不再拥有发送/终态资格: seller="
                        + burst.SellerNick + ", buyer=" + burst.BuyerNick
                        + ", generation=" + burst.SessionGeneration);
                    return;
                }

                await dispatchTask.ConfigureAwait(false);
                FinalizeReplyOutcome(burst, lease);
            }
            catch (OperationCanceledException)
            {
                if (lease.IsCurrent) lease.MarkFailed("reply_pipeline_cancelled");
            }
            catch (Exception ex)
            {
                if (lease.IsCurrent) lease.MarkFailed("reply_pipeline_exception");
                Log.Exception(ex);
            }
            finally
            {
                _sessionAgent.Prune(TimeSpan.FromMinutes(30));
            }
        }

        private void OnOwnedDispatchCompleted(string key, BurstState state, Task task)
        {
            lock (state.Sync)
            {
                state.InFlightDispatches.Remove(task);
                if (!state.WorkerRunning
                    && state.PendingRules.Count == 0
                    && state.Items.Count == 0
                    && state.InFlightDispatches.Count == 0)
                {
                    RetireStateLocked(key, state);
                }
            }
        }

        private void RetireStateLocked(string key, BurstState state)
        {
            if (state == null || state.Retired) return;
            state.Retired = true;
            DisposeActivity(state);
            BurstState current;
            if (_states.TryGetValue(key, out current) && ReferenceEquals(current, state))
            {
                BurstState ignored;
                _states.TryRemove(key, out ignored);
            }
        }

        private async Task ProcessPreMergeAsync(string key, BurstState state, BuyerMessageBurstItem item)
        {
            if (item == null || item.SessionGeneration <= 0) return;
            if (!_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration)) return;

            var token = _sessionAgent.GetCancellationToken(
                item.SellerNick, item.BuyerNick, item.SessionGeneration);
            bool allowLocalShortReply;
            lock (state.Sync)
            {
                allowLocalShortReply = state.PendingRules.Count == 0
                    && state.Items.Count == 0
                    && state.InFlightDispatches.Count == 0;
            }

            CanonicalPreMergeOutcome outcome;
            try
            {
                outcome = await CanonicalPreMergeDecisionService.HandleAsync(
                    item, allowLocalShortReply, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = CanonicalPreMergeOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "统一买家生命周期固定规则异常，继续普通合并链路: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", error=" + Safe(ex.Message, 220), 20);
                outcome = CanonicalPreMergeOutcome.Continue;
            }

            if (outcome == CanonicalPreMergeOutcome.Continue)
            {
                if (_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
                    EnqueueForMerge(item);
                return;
            }

            if (outcome == CanonicalPreMergeOutcome.Failed)
            {
                _sessionAgent.TryTransition(
                    item.SellerNick, item.BuyerNick, item.SessionGeneration,
                    BuyerSessionAgentState.Failed, "canonical_pre_merge_failed");
                return;
            }
            if (outcome == CanonicalPreMergeOutcome.Consumed)
            {
                _sessionAgent.TryTransition(
                    item.SellerNick, item.BuyerNick, item.SessionGeneration,
                    BuyerSessionAgentState.Completed, "canonical_pre_merge_consumed");
            }
        }

        private bool HasPendingBuyerMessages(string seller, string buyer)
        {
            BurstState state;
            if (!_states.TryGetValue(Key(seller, buyer), out state) || state == null) return false;
            lock (state.Sync)
            {
                return state.PendingRules.Count > 0
                    || state.Items.Count > 0
                    || state.InFlightDispatches.Count > 0;
            }
        }

        private void EnqueueForMerge(BuyerMessageBurstItem item)
        {
            if (!_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration)) return;
            var key = Key(item.SellerNick, item.BuyerNick);
            BurstState state;
            if (!_states.TryGetValue(key, out state) || state == null) return;

            List<long> trimmedGenerations = null;
            lock (state.Sync)
            {
                if (state.Retired) return;
                if (!string.IsNullOrWhiteSpace(item.MessageKey)
                    && state.Items.Any(x => string.Equals(x.MessageKey, item.MessageKey, StringComparison.Ordinal)))
                    return;

                var previousReceivedAt = state.Items.Count == 0
                    ? DateTime.MinValue
                    : state.Items[state.Items.Count - 1].ReceivedAt;
                AdaptiveReplyTimingService.RecordInterval(
                    item.SellerNick, item.BuyerNick, previousReceivedAt, item.ReceivedAt);

                if (state.ActivityLease == null)
                    state.ActivityLease = BotActivityCoordinator.Begin("买家消息聚合/回复", item.SellerNick, item.BuyerNick);
                if (state.Items.Count == 0) state.StartedAt = DateTime.Now;
                state.Items.Add(item);
                if (state.Items.Count > 12)
                {
                    var removeCount = state.Items.Count - 12;
                    trimmedGenerations = state.Items.Take(removeCount)
                        .Where(x => x != null && x.SessionGeneration > 0)
                        .Select(x => x.SessionGeneration)
                        .Distinct()
                        .ToList();
                    state.Items.RemoveRange(0, removeCount);
                }
                state.Version++;
                state.LatestSessionGeneration = item.SessionGeneration;
                try { state.DelayCancellation.Cancel(); } catch { }
                state.DelayCancellation.Dispose();
                state.DelayCancellation = new CancellationTokenSource();
            }

            foreach (var generation in trimmedGenerations ?? new List<long>())
            {
                _sessionAgent.TryTransition(
                    item.SellerNick, item.BuyerNick, generation,
                    BuyerSessionAgentState.Completed, "coalescing_buffer_trimmed");
            }
        }

        public void CancelBuyer(string seller, string buyer, string reason)
        {
            var key = Key(seller, buyer);
            BurstState state;
            if (_states.TryGetValue(key, out state) && state != null)
            {
                lock (state.Sync)
                {
                    state.Version++;
                    state.HardCancelVersion++;
                    state.PendingRules.Clear();
                    state.Items.Clear();
                    state.StartedAt = DateTime.MinValue;
                    try { state.DelayCancellation.Cancel(); } catch { }
                    state.DelayCancellation.Dispose();
                    state.DelayCancellation = new CancellationTokenSource();
                    DisposeActivity(state);
                }
            }
            _sessionAgent.CancelAll(seller, buyer, reason);
            Log.Info("买家自动回复任务已因显式硬失效全部取消: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
        }

        private void FinalizeReplyOutcome(BuyerMessageBurst burst, BuyerMessageBurstLease lease)
        {
            if (burst == null || lease == null || !lease.IsCurrent) return;
            BuyerSessionAgentState generationState;
            var hasState = _sessionAgent.TryGetGenerationState(
                burst.SellerNick, burst.BuyerNick, burst.SessionGeneration, out generationState);
            var failed = hasState && generationState == BuyerSessionAgentState.Failed;
            var returnedWithoutReady = hasState && generationState == BuyerSessionAgentState.Generating;
            if (failed)
            {
                Log.Info("回复管线返回时会话已是Failed，保留失败终态且禁止升级Completed: seller="
                    + burst.SellerNick + ", buyer=" + burst.BuyerNick
                    + ", generation=" + burst.SessionGeneration);
            }
            else if (returnedWithoutReady && burst.HasReplyableItem)
            {
                lease.MarkFailed("reply_pipeline_returned_without_ready");
                Log.Info("回复管线在答案就绪前返回，保持失败态而非误记Completed: seller="
                    + burst.SellerNick + ", buyer=" + burst.BuyerNick
                    + ", generation=" + burst.SessionGeneration);
            }
            else
            {
                lease.MarkCompleted(returnedWithoutReady
                    ? "non_replyable_media_skipped"
                    : "reply_pipeline_completed");
            }
        }

        private void CompleteMergedAwayGenerations(BuyerMessageBurst burst)
        {
            if (burst == null || burst.Items == null || burst.Items.Count < 2) return;
            foreach (var generation in burst.Items
                .Where(x => x != null && x.SessionGeneration > 0 && x.SessionGeneration != burst.SessionGeneration)
                .Select(x => x.SessionGeneration)
                .Distinct())
            {
                _sessionAgent.TryTransition(
                    burst.SellerNick, burst.BuyerNick, generation,
                    BuyerSessionAgentState.Completed,
                    "coalesced_into_generation_" + burst.SessionGeneration);
            }
        }

        private async Task DispatchScopedAsync(BuyerMessageBurst burst, BuyerMessageBurstLease lease)
        {
            ShopContext shop = null;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySellerNick(
                    burst == null ? string.Empty : burst.SellerNick);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "买家回复未能解析店铺身份，使用旧全局 AI 配置兼容模式：" + Safe(ex.Message, 220), 20);
            }

            if (shop == null)
            {
                await LegacyAiConfigurationGate.WaitAsync(lease.CancellationToken).ConfigureAwait(false);
                try
                {
                    if (!lease.IsCurrent) return;
                    await _handler(lease).ConfigureAwait(false);
                }
                finally
                {
                    LegacyAiConfigurationGate.Release();
                }
                return;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                if (!lease.IsCurrent) return;
                await _handler(lease).ConfigureAwait(false);
            }
        }

        private void AttachSemanticContinuation(BuyerMessageBurstItem item)
        {
            if (item == null || !LooksLikeSemanticContinuation(item.DisplayText)) return;
            var key = Key(item.SellerNick, item.BuyerNick);
            RecentBuyerText previous;
            if (!_recentBuyerTexts.TryGetValue(key, out previous) || previous == null) return;
            var currentAt = item.ReceivedAt == default(DateTime) ? DateTime.Now : item.ReceivedAt;
            var anchorText = NormalizeSemanticText(previous.AnchorText);
            if (string.IsNullOrWhiteSpace(anchorText) || previous.AnchorReceivedAt == DateTime.MinValue) return;
            var age = currentAt - previous.AnchorReceivedAt;
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds)) return;
            var currentText = NormalizeSemanticText(item.DisplayText);
            if (string.IsNullOrWhiteSpace(currentText)
                || string.Equals(anchorText, currentText, StringComparison.OrdinalIgnoreCase)) return;

            item.SemanticContinuationContext = anchorText;
            var supersededGeneration = previous.LatestGeneration > 0
                ? previous.LatestGeneration
                : previous.AnchorGeneration;
            if (supersededGeneration > 0 && supersededGeneration != item.SessionGeneration)
            {
                _sessionAgent.Cancel(
                    item.SellerNick, item.BuyerNick, supersededGeneration,
                    "semantic_continuation_superseded");
            }
            if (previous.LatestReceivedAt != DateTime.MinValue)
            {
                ResponseProgressTracker.MarkContextualContinuationMerged(
                    item.SellerNick, item.BuyerNick, previous.LatestReceivedAt, currentText);
            }
            Log.Info("买家省略/催问续句已关联未解决主问题: seller=" + item.SellerNick
                + ", buyer=" + item.BuyerNick
                + ", previousGeneration=" + supersededGeneration
                + ", generation=" + item.SessionGeneration
                + ", anchorAgeMs=" + Math.Max(0, (long)age.TotalMilliseconds));
        }

        private void RememberRecentBuyerText(BuyerMessageBurstItem item)
        {
            if (item == null) return;
            var text = NormalizeSemanticText(item.DisplayText);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 240) return;
            var key = Key(item.SellerNick, item.BuyerNick);
            var receivedAt = item.ReceivedAt == default(DateTime) ? DateTime.Now : item.ReceivedAt;
            var dependent = LooksLikeSemanticContinuation(text);
            if (dependent)
            {
                RecentBuyerText existing;
                while (_recentBuyerTexts.TryGetValue(key, out existing) && existing != null)
                {
                    if (string.IsNullOrWhiteSpace(existing.AnchorText)
                        || existing.AnchorReceivedAt == DateTime.MinValue
                        || receivedAt - existing.AnchorReceivedAt > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds)) break;
                    var updated = new RecentBuyerText
                    {
                        AnchorText = existing.AnchorText,
                        AnchorReceivedAt = existing.AnchorReceivedAt,
                        AnchorGeneration = existing.AnchorGeneration,
                        LatestText = text,
                        LatestReceivedAt = receivedAt,
                        LatestGeneration = item.SessionGeneration
                    };
                    if (_recentBuyerTexts.TryUpdate(key, updated, existing)) return;
                }
                if (IsPunctuationOnlySemanticNudge(text)) return;
            }
            _recentBuyerTexts[key] = new RecentBuyerText
            {
                AnchorText = text,
                AnchorReceivedAt = receivedAt,
                AnchorGeneration = item.SessionGeneration,
                LatestText = text,
                LatestReceivedAt = receivedAt,
                LatestGeneration = item.SessionGeneration
            };
        }

        private static bool LooksLikeSemanticContinuation(string value)
        {
            var text = NormalizeSemanticText(value);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32) return false;
            if (IsPunctuationOnlySemanticNudge(text)) return true;
            var compact = Regex.Replace(text.ToLowerInvariant(), @"[\s，。！？!?、；;：:…~～]", string.Empty);
            if (string.IsNullOrWhiteSpace(compact)) return true;
            var prefixes = new[] { "这个", "这款", "这种", "这个版本", "这个型号", "那个", "那款", "那种", "它", "这", "那" };
            if (prefixes.Any(x => compact.StartsWith(x, StringComparison.Ordinal)))
            {
                if (compact == "这个" || compact == "这个呢" || compact == "那个" || compact == "那个呢" || compact == "它呢") return true;
                if (Regex.IsMatch(compact, @"支持|能用|可以|可用|适用|兼容|行吗|能不能|可不可以|怎么样|咋样|有吗|吗$|呢$")) return true;
            }
            return Regex.IsMatch(compact,
                @"^(?:可以|可以吗|可以不|行|行吗|行不行|能|能吗|能用|能用吗|能不能|支持|支持吗|可用|可用吗|适用|适用吗|兼容|兼容吗|有|有吗|是吗|对吗|确定吗|真的吗|真的|好了吗|好了没|怎么样|咋样|多久|什么时候|多少钱|在哪|哪里|怎么弄|怎么用|呢)$");
        }

        private static bool IsPunctuationOnlySemanticNudge(string value)
        {
            var compact = Regex.Replace(NormalizeSemanticText(value), @"[\s，。！？!?、；;：:…~～.\-—_]+", string.Empty);
            return compact.Length == 0;
        }

        private static string NormalizeSemanticText(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return Regex.Replace(value, @"\s+", " ");
        }

        internal static int QuietDelayMilliseconds(IEnumerable<BuyerMessageBurstItem> items, DateTime startedAt)
        {
            var list = (items ?? new BuyerMessageBurstItem[0]).Where(x => x != null).ToList();
            if (list.Count == 0) return 350;
            if (startedAt != DateTime.MinValue && DateTime.Now - startedAt >= TimeSpan.FromSeconds(4)) return 80;
            var latestItem = list.Last();
            var latest = (latestItem.DisplayText ?? string.Empty).Trim();
            var compact = Regex.Replace(latest, @"\s+", string.Empty);
            int baseline;
            AdaptiveDelayKind kind;
            if (list.Count >= 6) { baseline = 420; kind = AdaptiveDelayKind.DenseBurst; }
            else if (IncomingMessageSafety.IsMediaPlaceholder(latest)) { baseline = 700; kind = AdaptiveDelayKind.Media; }
            else if (IsGreetingOnly(compact)) { baseline = 950; kind = AdaptiveDelayKind.Greeting; }
            else if (IsOpenShortFragment(compact)) { baseline = 1200; kind = AdaptiveDelayKind.Fragment; }
            else if (!EndsLikeCompleteSentence(compact) && compact.Length <= 24) { baseline = 800; kind = AdaptiveDelayKind.Fragment; }
            else { baseline = 350; kind = AdaptiveDelayKind.Complete; }
            return AdaptiveReplyTimingService.AdjustDelay(
                latestItem.SellerNick, latestItem.BuyerNick, baseline, kind);
        }

        private static bool IsGreetingOnly(string text)
        {
            return text == "在吗" || text == "你好" || text == "您好"
                || text == "有人吗" || text == "客服在吗" || text == "亲在吗";
        }

        private static bool IsOpenShortFragment(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 10) return false;
            if (EndsLikeCompleteSentence(text)) return false;
            return text != "好的" && text != "好" && text != "嗯"
                && text != "谢谢" && text != "知道了" && text != "明白了";
        }

        private static bool EndsLikeCompleteSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return "。！？!?；;".IndexOf(text[text.Length - 1]) >= 0;
        }

        private static void DisposeActivity(BurstState state)
        {
            if (state == null || state.ActivityLease == null) return;
            try { state.ActivityLease.Dispose(); } catch { }
            state.ActivityLease = null;
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
