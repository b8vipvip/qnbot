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
            // Read-only barrier: lifecycle ownership remains with the actual answer/send path.
            // A timing probe must never publish Ready before the answer is fully materialized.
            return IsCurrent && !CancellationToken.IsCancellationRequested;
        }

        public bool MarkProcessing(string reason)
        {
            return Transition(BuyerSessionAgentState.Processing, reason);
        }

        public bool MarkGenerating(string reason)
        {
            return Transition(BuyerSessionAgentState.Generating, reason);
        }

        public bool MarkReady(string reason)
        {
            return Transition(BuyerSessionAgentState.Ready, reason);
        }

        public bool MarkSending(string reason)
        {
            return Transition(BuyerSessionAgentState.Sending, reason);
        }

        public bool MarkWaiting(string reason)
        {
            return Transition(BuyerSessionAgentState.Waiting, reason);
        }

        public bool MarkCompleted(string reason)
        {
            return Transition(BuyerSessionAgentState.Completed, reason);
        }

        public bool MarkFailed(string reason)
        {
            return Transition(BuyerSessionAgentState.Failed, reason);
        }

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

    internal sealed class BuyerMessageBurstCoordinator
    {
        private sealed class BurstState
        {
            public readonly object Sync = new object();
            public readonly List<BuyerMessageBurstItem> Items = new List<BuyerMessageBurstItem>();
            public CancellationTokenSource DelayCancellation = new CancellationTokenSource();
            public bool WorkerRunning;
            public int Version;
            public int HardCancelVersion;
            public DateTime StartedAt = DateTime.MinValue;
            public BotActivityLease ActivityLease;
            public long LatestSessionGeneration;
        }

        private sealed class RecentBuyerText
        {
            // Anchor is the last substantive unresolved utterance. Punctuation nudges and short
            // elliptical confirmations update Latest* but never erase this semantic anchor.
            public string AnchorText { get; set; }
            public DateTime AnchorReceivedAt { get; set; }
            public long AnchorGeneration { get; set; }
            public string LatestText { get; set; }
            public DateTime LatestReceivedAt { get; set; }
            public long LatestGeneration { get; set; }
        }

        // DeterministicAutoReplyService owns the single per-buyer serialization gate.
        // Do not add a second outer gate or race a still-running fixed-send task against AI.
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

        internal BuyerSessionAgent SessionAgent
        {
            get { return _sessionAgent; }
        }

        public void Enqueue(BuyerMessageBurstItem item)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick))
            {
                return;
            }

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
                "pre_merge_rules");

            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);
            Task.Run(async () =>
            {
                var continueToMerge = true;
                try
                {
                    // The deterministic service owns the only same-buyer gate. Ordinary fixed rules
                    // wait with the current generation cancellation token instead of timing out into AI;
                    // explicit generation invalidation is the bounded escape path. We deliberately await the
                    // selected rule task here: starting AI while that task can still send would
                    // create a duplicate/out-of-order side-effect race.
                    continueToMerge = await DeterministicAutoReplyService.HandleBeforeMergeAsync(
                        item,
                        allowLocalShortReply);
                }
                catch (OperationCanceledException)
                {
                    if (observation.CancellationToken.IsCancellationRequested)
                    {
                        Log.Info("消息合并前固定规则已因generation显式失效取消: seller="
                            + item.SellerNick + ", buyer=" + item.BuyerNick
                            + ", generation=" + item.SessionGeneration);
                        return;
                    }
                    Log.ErrorWithMaxCount(
                        "消息合并前固定规则发生非会话取消，已fail-open继续普通合并链路: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick
                        + ", generation=" + item.SessionGeneration,
                        20);
                    continueToMerge = true;
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount(
                        "消息合并前固定规则处理失败，继续普通合并链路: seller=" + item.SellerNick
                        + ", buyer=" + item.BuyerNick + ", error=" + Safe(ex.Message, 220),
                        20);
                    continueToMerge = true;
                }

                if (observation.CancellationToken.IsCancellationRequested
                    || !_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
                {
                    return;
                }

                if (continueToMerge)
                {
                    try
                    {
                        EnqueueForMerge(item);
                    }
                    catch (Exception ex)
                    {
                        _sessionAgent.TryTransition(
                            item.SellerNick,
                            item.BuyerNick,
                            item.SessionGeneration,
                            BuyerSessionAgentState.Failed,
                            "pre_merge_enqueue_exception");
                        Log.ErrorWithMaxCount(
                            "消息进入合并队列异常，已结束Coalescing避免永久等待: seller="
                            + item.SellerNick + ", buyer=" + item.BuyerNick
                            + ", generation=" + item.SessionGeneration
                            + ", error=" + Safe(ex.Message, 220),
                            50);
                    }
                }
                else
                {
                    BuyerSessionAgentState deterministicState;
                    if (_sessionAgent.TryGetGenerationState(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        out deterministicState)
                        && deterministicState == BuyerSessionAgentState.Failed)
                    {
                        Log.Info("固定规则发送失败后保留Failed终态，禁止升级Completed: seller="
                            + item.SellerNick + ", buyer=" + item.BuyerNick
                            + ", generation=" + item.SessionGeneration);
                    }
                    else
                    {
                        _sessionAgent.TryTransition(
                            item.SellerNick,
                            item.BuyerNick,
                            item.SessionGeneration,
                            BuyerSessionAgentState.Completed,
                            "deterministic_rule_consumed");
                    }
                }
            });
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

            // A dependent fragment is not an independent new topic. It supersedes only the previous
            // generation in the same semantic chain; unrelated ordinary questions remain parallel.
            var supersededGeneration = previous.LatestGeneration > 0
                ? previous.LatestGeneration
                : previous.AnchorGeneration;
            if (supersededGeneration > 0 && supersededGeneration != item.SessionGeneration)
            {
                _sessionAgent.Cancel(
                    item.SellerNick,
                    item.BuyerNick,
                    supersededGeneration,
                    "semantic_continuation_superseded");
            }
            if (previous.LatestReceivedAt != DateTime.MinValue)
            {
                ResponseProgressTracker.MarkContextualContinuationMerged(
                    item.SellerNick,
                    item.BuyerNick,
                    previous.LatestReceivedAt,
                    currentText);
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
                        || receivedAt - existing.AnchorReceivedAt > TimeSpan.FromSeconds(SemanticContinuationWindowSeconds))
                    {
                        break;
                    }
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

                // A pure punctuation nudge has no standalone semantics and must never erase/create an anchor.
                if (IsPunctuationOnlySemanticNudge(text)) return;
            }

            // A substantive question (or a short elliptical question with no usable predecessor) becomes
            // the new anchor. Later punctuation/confirmation fragments can safely inherit it.
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

            // Predicate-only / interrogative-only short turns omit the subject by definition.
            // They are dependent only when an anchor actually exists; RememberRecentBuyerText falls
            // back to treating them as a new anchor when no predecessor is available.
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
            value = Regex.Replace(value, @"\s+", " ");
            return value;
        }

        private bool HasPendingBuyerMessages(string seller, string buyer)
        {
            BurstState state;
            if (!_states.TryGetValue(Key(seller, buyer), out state) || state == null) return false;
            lock (state.Sync)
            {
                return state.Items.Count > 0;
            }
        }

        private void EnqueueForMerge(BuyerMessageBurstItem item)
        {
            if (!_sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
            {
                Log.Info("固定规则返回时本条独立generation已结束，不再进入合并: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", generation=" + item.SessionGeneration);
                return;
            }

            var key = Key(item.SellerNick, item.BuyerNick);
            var state = _states.GetOrAdd(key, _ => new BurstState());
            var startWorker = false;
            List<long> trimmedGenerations = null;
            lock (state.Sync)
            {
                if (!string.IsNullOrWhiteSpace(item.MessageKey)
                    && state.Items.Any(x => string.Equals(x.MessageKey, item.MessageKey, StringComparison.Ordinal)))
                {
                    return;
                }

                var previousReceivedAt = state.Items.Count == 0
                    ? DateTime.MinValue
                    : state.Items[state.Items.Count - 1].ReceivedAt;
                AdaptiveReplyTimingService.RecordInterval(
                    item.SellerNick,
                    item.BuyerNick,
                    previousReceivedAt,
                    item.ReceivedAt);

                if (state.ActivityLease == null)
                {
                    state.ActivityLease = BotActivityCoordinator.Begin("买家消息聚合/回复", item.SellerNick, item.BuyerNick);
                }
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

                if (!state.WorkerRunning)
                {
                    state.WorkerRunning = true;
                    startWorker = true;
                }
            }

            foreach (var generation in trimmedGenerations ?? new List<long>())
            {
                _sessionAgent.TryTransition(
                    item.SellerNick,
                    item.BuyerNick,
                    generation,
                    BuyerSessionAgentState.Completed,
                    "coalescing_buffer_trimmed");
            }

            if (startWorker) Task.Run(() => RunAsync(key, state));
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
                    state.Items.Clear();
                    state.StartedAt = DateTime.MinValue;
                    try { state.DelayCancellation.Cancel(); } catch { }
                    state.DelayCancellation.Dispose();
                    state.DelayCancellation = new CancellationTokenSource();
                    state.WorkerRunning = false;
                    DisposeActivity(state);
                }
                BurstState ignored;
                _states.TryRemove(key, out ignored);
            }

            _sessionAgent.CancelAll(seller, buyer, reason);
            Log.Info("买家自动回复任务已因显式硬失效全部取消: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
        }

        private async Task RunAsync(string key, BurstState state)
        {
            while (true)
            {
                CancellationToken token;
                int capturedVersion;
                int capturedHardCancelVersion;
                int delayMilliseconds;
                lock (state.Sync)
                {
                    if (state.Items.Count < 1)
                    {
                        state.WorkerRunning = false;
                        DisposeActivity(state);
                        BurstState empty;
                        _states.TryRemove(key, out empty);
                        return;
                    }
                    token = state.DelayCancellation.Token;
                    capturedVersion = state.Version;
                    capturedHardCancelVersion = state.HardCancelVersion;
                    delayMilliseconds = QuietDelayMilliseconds(state.Items, state.StartedAt);
                }

                try
                {
                    await Task.Delay(delayMilliseconds, token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                BuyerMessageBurst burst;
                lock (state.Sync)
                {
                    if (state.Version != capturedVersion) continue;
                    if (state.Items.Count < 1) continue;

                    var dispatchedItems = state.Items.ToList();
                    state.Items.Clear();
                    state.StartedAt = DateTime.MinValue;
                    state.WorkerRunning = false;
                    burst = new BuyerMessageBurst(
                        dispatchedItems[0].SellerNick,
                        dispatchedItems[0].BuyerNick,
                        dispatchedItems,
                        capturedVersion);
                }

                CompleteMergedAwayGenerations(burst);
                if (!_sessionAgent.IsCurrent(burst.SellerNick, burst.BuyerNick, burst.SessionGeneration))
                {
                    Log.Info("聚合完成时最终generation已失效，跳过本轮回复: seller=" + burst.SellerNick
                        + ", buyer=" + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                    continue;
                }

                var lease = new BuyerMessageBurstLease(
                    burst,
                    () =>
                    {
                        lock (state.Sync)
                        {
                            return state.HardCancelVersion == capturedHardCancelVersion;
                        }
                    },
                    _sessionAgent);
                lease.MarkProcessing("burst_dispatch");
                lease.MarkGenerating("reply_generation_started");

                try
                {
                    await DispatchScopedAsync(burst, lease);
                    if (lease.IsCurrent)
                    {
                        BuyerSessionAgentState generationState;
                        var hasGenerationState = _sessionAgent.TryGetGenerationState(
                            burst.SellerNick,
                            burst.BuyerNick,
                            burst.SessionGeneration,
                            out generationState);
                        var failed = hasGenerationState && generationState == BuyerSessionAgentState.Failed;
                        var returnedWithoutReady = hasGenerationState && generationState == BuyerSessionAgentState.Generating;
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
                }
                catch (OperationCanceledException)
                {
                    if (lease.IsCurrent) lease.MarkFailed("reply_pipeline_cancelled");
                }
                catch (Exception ex)
                {
                    lease.MarkFailed("reply_pipeline_exception");
                    Log.Exception(ex);
                }

                lock (state.Sync)
                {
                    if (state.Version == capturedVersion
                        && state.Items.Count < 1
                        && !state.WorkerRunning)
                    {
                        DisposeActivity(state);
                        BurstState ignored;
                        _states.TryRemove(key, out ignored);
                    }
                }
                _sessionAgent.Prune(TimeSpan.FromMinutes(30));
                return;
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
                    burst.SellerNick,
                    burst.BuyerNick,
                    generation,
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
                    "买家回复未能解析店铺身份，使用旧全局 AI 配置兼容模式：" + Safe(ex.Message, 220),
                    20);
            }

            if (shop == null)
            {
                await LegacyAiConfigurationGate.WaitAsync(lease.CancellationToken);
                try
                {
                    if (!lease.IsCurrent) return;
                    await _handler(lease);
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
                await _handler(lease);
            }
        }

        internal static int QuietDelayMilliseconds(
            IEnumerable<BuyerMessageBurstItem> items,
            DateTime startedAt)
        {
            var list = (items ?? new BuyerMessageBurstItem[0]).Where(x => x != null).ToList();
            if (list.Count == 0) return 350;
            if (startedAt != DateTime.MinValue && DateTime.Now - startedAt >= TimeSpan.FromSeconds(4))
            {
                return 80;
            }

            var latestItem = list.Last();
            var latest = (latestItem.DisplayText ?? string.Empty).Trim();
            var compact = Regex.Replace(latest, @"\s+", string.Empty);
            int baseline;
            AdaptiveDelayKind kind;
            if (list.Count >= 6)
            {
                baseline = 420;
                kind = AdaptiveDelayKind.DenseBurst;
            }
            else if (IncomingMessageSafety.IsMediaPlaceholder(latest))
            {
                baseline = 700;
                kind = AdaptiveDelayKind.Media;
            }
            else if (IsGreetingOnly(compact))
            {
                baseline = 950;
                kind = AdaptiveDelayKind.Greeting;
            }
            else if (IsOpenShortFragment(compact))
            {
                baseline = 1200;
                kind = AdaptiveDelayKind.Fragment;
            }
            else if (!EndsLikeCompleteSentence(compact) && compact.Length <= 24)
            {
                baseline = 800;
                kind = AdaptiveDelayKind.Fragment;
            }
            else
            {
                baseline = 350;
                kind = AdaptiveDelayKind.Complete;
            }

            return AdaptiveReplyTimingService.AdjustDelay(
                latestItem.SellerNick,
                latestItem.BuyerNick,
                baseline,
                kind);
        }

        private static bool IsGreetingOnly(string text)
        {
            return text == "在吗"
                || text == "你好"
                || text == "您好"
                || text == "有人吗"
                || text == "客服在吗"
                || text == "亲在吗";
        }

        private static bool IsOpenShortFragment(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 10) return false;
            if (EndsLikeCompleteSentence(text)) return false;
            return text != "好的"
                && text != "好"
                && text != "嗯"
                && text != "谢谢"
                && text != "知道了"
                && text != "明白了";
        }

        private static bool EndsLikeCompleteSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var last = text[text.Length - 1];
            return "。！？!?；;".IndexOf(last) >= 0;
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