using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    public enum BuyerSessionAgentState
    {
        Idle = 0,
        Observed = 1,
        Coalescing = 2,
        Processing = 3,
        Generating = 4,
        Ready = 5,
        Sending = 6,
        Waiting = 7,
        Completed = 8,
        Cancelled = 9,
        Failed = 10
    }

    public enum BuyerSessionEventKind
    {
        Unknown = 0,
        BuyerText = 1,
        BuyerImage = 2,
        BuyerProductCard = 3,
        BuyerOrder = 4,
        BuyerSystem = 5,
        BuyerWithdrawal = 6,
        BuyerMedia = 7,
        BuyerActionAccepted = 8,
        SellerHumanReply = 20,
        SellerBotEcho = 21,
        SellerWithdrawal = 22,
        OrderCreated = 30,
        OrderPaid = 31,
        OrderClosed = 32,
        OrderRefund = 33,
        SendStarted = 40,
        SendConfirmed = 41,
        SendFailed = 42
    }

    public sealed class BuyerSessionEventSnapshot
    {
        public long Sequence { get; set; }
        public BuyerSessionEventKind Kind { get; set; }
        public string MessageKey { get; set; }
        public long SortValue { get; set; }
        public DateTime SourceTimestamp { get; set; }
        public DateTime ObservedAt { get; set; }
        public long Generation { get; set; }
        public bool StaleAgainstLatestBuyer { get; set; }
        public string Reason { get; set; }
    }

    public sealed class BuyerSessionEventResult
    {
        public bool Accepted { get; set; }
        public bool Duplicate { get; set; }
        public bool StaleAgainstLatestBuyer { get; set; }
        public bool CancelledCurrentGeneration { get; set; }
        public long Generation { get; set; }
    }

    public sealed class BuyerSessionAgentSnapshot
    {
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public long Generation { get; set; }
        public BuyerSessionAgentState State { get; set; }
        public string LastMessageKey { get; set; }
        public long LastSortValue { get; set; }
        public DateTime LastObservedAt { get; set; }
        public DateTime StateChangedAt { get; set; }
        public string Reason { get; set; }
        public BuyerSessionEventKind LastEventKind { get; set; }
        public DateTime LastEventAt { get; set; }
        public long LastBuyerEventSortValue { get; set; }
        public DateTime LastBuyerEventAt { get; set; }
        public IList<BuyerSessionEventSnapshot> RecentEvents { get; set; }
    }

    public sealed class BuyerSessionAgentObservation
    {
        public string SessionKey { get; set; }
        public long Generation { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public DateTime AcceptedAtUtc { get; set; }
        public bool SupersededPreviousGeneration { get; set; }
        public bool Duplicate { get; set; }
        // Compatibility field retained for callers compiled against the intermediate model.
        // Generations are no longer shared during coalescing; the burst coordinator performs merging.
        public bool ReusedCoalescingGeneration { get; set; }
    }

    /// <summary>
    /// Shared per seller/buyer ordered state machine. Every accepted unique buyer message owns an
    /// independent generation. A later ordinary buyer message never cancels an already dispatched
    /// generation. Consecutive messages may still be merged by BuyerMessageBurstCoordinator, which
    /// explicitly completes the merged-away generations after the burst is formed. Human seller
    /// replies are observational learning evidence only. Only explicit hard invalidation cancels work.
    /// </summary>
    public sealed class BuyerSessionAgent
    {
        private const int MaxRememberedMessageKeys = 64;
        private const int MaxRememberedEvents = 64;
        private const int MaxRememberedGenerationStates = 128;
        // BuyerStreamingReplyPipeline owns the AI deadline. The session watchdog is only a final
        // lifecycle safety net and must never fire before that canonical AI budget can finish.
        internal const int AbsoluteGenerationAgeSeconds = BuyerStreamingReplyPipeline.TotalAiBudgetSeconds + 20;
        private static readonly ConcurrentDictionary<string, SessionState> Sessions =
            new ConcurrentDictionary<string, SessionState>(StringComparer.Ordinal);

        static BuyerSessionAgent()
        {
            try { BuyerSessionAgentRuntimeBridge.EnsureStarted(); } catch { }
        }

        public BuyerSessionAgentObservation ObserveBuyerMessage(
            string sellerNick,
            string buyerNick,
            string messageKey,
            long sortValue,
            DateTime observedAt)
        {
            var key = BuildKey(sellerNick, buyerNick);
            var state = GetOrCreateState(key, sellerNick, buyerNick);

            long generation;
            bool hadParallelGeneration;
            CancellationToken token;
            DateTime acceptedAtUtc;
            lock (state.SyncRoot)
            {
                state.SellerNick = Normalize(sellerNick);
                state.BuyerNick = Normalize(buyerNick);
                state.LastObservedAt = observedAt == default(DateTime) ? DateTime.Now : observedAt;
                state.LastSortValue = Math.Max(state.LastSortValue, sortValue);
                state.LastBuyerEventSortValue = Math.Max(state.LastBuyerEventSortValue, sortValue);
                if (state.LastBuyerEventAt < state.LastObservedAt) state.LastBuyerEventAt = state.LastObservedAt;

                messageKey = Normalize(messageKey);
                if (!string.IsNullOrWhiteSpace(messageKey) && state.RecentMessageKeySet.Contains(messageKey))
                {
                    CancellationTokenSource duplicateCts;
                    token = state.ActiveGenerations.TryGetValue(state.Generation, out duplicateCts)
                        && duplicateCts != null
                        ? duplicateCts.Token
                        : CancellationToken.None;
                    state.GenerationAcceptedAtUtc.TryGetValue(state.Generation, out acceptedAtUtc);
                    return new BuyerSessionAgentObservation
                    {
                        SessionKey = key,
                        Generation = state.Generation,
                        CancellationToken = token,
                        AcceptedAtUtc = acceptedAtUtc,
                        SupersededPreviousGeneration = false,
                        Duplicate = true,
                        ReusedCoalescingGeneration = false
                    };
                }

                RememberMessageKeyLocked(state, messageKey);
                hadParallelGeneration = state.ActiveGenerations.Count > 0;
                state.Generation++;
                generation = state.Generation;
                acceptedAtUtc = DateTime.UtcNow;
                var cts = new CancellationTokenSource();
                state.GenerationCancellation = cts;
                state.ActiveGenerations[generation] = cts;
                state.GenerationAcceptedAtUtc[generation] = acceptedAtUtc;
                SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Observed);
                token = cts.Token;
                state.LastMessageKey = messageKey;
                SetStateLocked(state, BuyerSessionAgentState.Observed, "buyer_message");
                AppendEventLocked(
                    state,
                    BuyerSessionEventKind.BuyerActionAccepted,
                    messageKey,
                    sortValue,
                    state.LastObservedAt,
                    DateTime.Now,
                    generation,
                    false,
                    "actionable_buyer_message");
            }

            BuyerSessionAgentRuntimeBridge.RegisterAcceptedGeneration(
                sellerNick,
                buyerNick,
                generation,
                acceptedAtUtc);

            Log.Info("BuyerSessionAgent observed: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", sort=" + sortValue
                + ", superseded=False"
                + ", independentGeneration=True"
                + ", parallelPrevious=" + hadParallelGeneration);

            return new BuyerSessionAgentObservation
            {
                SessionKey = key,
                Generation = generation,
                CancellationToken = token,
                AcceptedAtUtc = acceptedAtUtc,
                SupersededPreviousGeneration = false,
                Duplicate = false,
                ReusedCoalescingGeneration = false
            };
        }

        public BuyerSessionEventResult RecordEvent(
            string sellerNick,
            string buyerNick,
            BuyerSessionEventKind kind,
            string messageKey,
            long sortValue,
            DateTime sourceTimestamp,
            DateTime observedAt,
            string reason,
            bool cancelCurrentGeneration)
        {
            var key = BuildKey(sellerNick, buyerNick);
            var state = GetOrCreateState(key, sellerNick, buyerNick);
            CancellationTokenSource cancel = null;
            var result = new BuyerSessionEventResult();
            observedAt = observedAt == default(DateTime) ? DateTime.Now : observedAt;
            sourceTimestamp = sourceTimestamp == default(DateTime) ? observedAt : sourceTimestamp;
            messageKey = Normalize(messageKey);

            lock (state.SyncRoot)
            {
                state.SellerNick = Normalize(sellerNick);
                state.BuyerNick = Normalize(buyerNick);
                state.LastObservedAt = observedAt;

                var dedupeKey = BuildEventDedupeKey(kind, messageKey, sortValue, sourceTimestamp);
                if (!string.IsNullOrWhiteSpace(dedupeKey) && state.RecentEventKeySet.Contains(dedupeKey))
                {
                    result.Duplicate = true;
                    result.Generation = state.Generation;
                    return result;
                }
                RememberEventKeyLocked(state, dedupeKey);

                var isBuyer = IsBuyerEvent(kind);
                if (isBuyer)
                {
                    state.LastBuyerEventSortValue = Math.Max(state.LastBuyerEventSortValue, sortValue);
                    if (state.LastBuyerEventAt < sourceTimestamp) state.LastBuyerEventAt = sourceTimestamp;
                }

                var stale = !isBuyer && IsOlderThanLatestBuyerLocked(state, sortValue, sourceTimestamp);
                result.StaleAgainstLatestBuyer = stale;
                result.Generation = state.Generation;
                result.Accepted = true;

                AppendEventLocked(
                    state,
                    kind,
                    messageKey,
                    sortValue,
                    sourceTimestamp,
                    observedAt,
                    state.Generation,
                    stale,
                    reason);

                // Human seller replies are observations only. Non-human callers may still request
                // cancellation of the latest generation, while conversation-wide hard invalidation
                // uses CancelAll from the coordinator.
                if (cancelCurrentGeneration
                    && kind != BuyerSessionEventKind.SellerHumanReply
                    && !stale
                    && state.Generation > 0
                    && state.State != BuyerSessionAgentState.Completed
                    && state.State != BuyerSessionAgentState.Cancelled
                    && state.State != BuyerSessionAgentState.Failed)
                {
                    if (state.ActiveGenerations.TryGetValue(state.Generation, out cancel))
                        state.ActiveGenerations.Remove(state.Generation);
                    if (state.GenerationCancellation == cancel) state.GenerationCancellation = null;
                    SetGenerationStateLocked(state, state.Generation, BuyerSessionAgentState.Cancelled);
                    SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
                    result.CancelledCurrentGeneration = true;
                }
            }

            if (cancel != null)
            {
                try { cancel.Cancel(); } catch { }
                try { cancel.Dispose(); } catch { }
            }

            Log.Info("BuyerSessionAgent event: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", kind=" + kind
                + ", generation=" + result.Generation
                + ", stale=" + result.StaleAgainstLatestBuyer
                + ", cancelled=" + result.CancelledCurrentGeneration
                + ", reason=" + Normalize(reason));
            return result;
        }

        public bool IsCurrent(string sellerNick, string buyerNick, long generation)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            var expired = false;
            long elapsedMs = 0;
            lock (state.SyncRoot)
            {
                CancellationTokenSource cts;
                if (!state.ActiveGenerations.TryGetValue(generation, out cts)
                    || cts == null
                    || cts.IsCancellationRequested)
                {
                    return false;
                }
                expired = IsGenerationExpiredLocked(state, generation, DateTime.UtcNow, out elapsedMs);
                if (!expired) return true;
            }

            Cancel(sellerNick, buyerNick, generation, "absolute_generation_age_current_gate");
            Log.ErrorWithMaxCount(
                "generation超过绝对年龄已被同步current门禁取消: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", elapsedMs=" + elapsedMs
                + ", limitSeconds=" + AbsoluteGenerationAgeSeconds,
                100);
            return false;
        }

        public CancellationToken GetCancellationToken(string sellerNick, string buyerNick, long generation)
        {
            return GetCancellationToken(BuildKey(sellerNick, buyerNick), generation);
        }

        public bool TryTransition(
            string sellerNick,
            string buyerNick,
            long generation,
            BuyerSessionAgentState next,
            string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            BuyerSessionAgentState previous;
            CancellationTokenSource completedCts = null;
            CancellationTokenSource expiredCts = null;
            var updateLatestState = false;
            var expired = false;
            long elapsedMs = 0;
            lock (state.SyncRoot)
            {
                CancellationTokenSource active;
                if (!state.ActiveGenerations.TryGetValue(generation, out active)
                    || active == null
                    || active.IsCancellationRequested)
                {
                    return false;
                }

                if (!state.GenerationStates.TryGetValue(generation, out previous))
                    previous = BuyerSessionAgentState.Observed;

                if (next != BuyerSessionAgentState.Cancelled
                    && next != BuyerSessionAgentState.Failed
                    && IsGenerationExpiredLocked(state, generation, DateTime.UtcNow, out elapsedMs))
                {
                    expired = true;
                    expiredCts = active;
                    state.ActiveGenerations.Remove(generation);
                    SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Cancelled);
                    if (state.GenerationCancellation == expiredCts) state.GenerationCancellation = null;
                    updateLatestState = state.Generation == generation;
                    if (updateLatestState)
                        SetStateLocked(state, BuyerSessionAgentState.Cancelled, "absolute_generation_age_transition_gate");
                }
                else
                {
                    if (!CanTransition(previous, next)) return false;
                    SetGenerationStateLocked(state, generation, next);

                    updateLatestState = state.Generation == generation;
                    if (updateLatestState)
                        SetStateLocked(state, next, reason);

                    if (next == BuyerSessionAgentState.Completed
                        || next == BuyerSessionAgentState.Cancelled
                        || next == BuyerSessionAgentState.Failed)
                    {
                        if (state.ActiveGenerations.TryGetValue(generation, out completedCts))
                            state.ActiveGenerations.Remove(generation);
                        if (state.GenerationCancellation == completedCts) state.GenerationCancellation = null;
                    }
                }
            }

            if (expiredCts != null)
            {
                try { expiredCts.Cancel(); } catch { }
                try { expiredCts.Dispose(); } catch { }
            }
            if (expired)
            {
                Log.ErrorWithMaxCount(
                    "generation超过绝对年龄，状态转换硬门禁已拒绝迟到结果: seller=" + Normalize(sellerNick)
                    + ", buyer=" + Normalize(buyerNick)
                    + ", generation=" + generation
                    + ", attemptedState=" + next
                    + ", elapsedMs=" + elapsedMs
                    + ", limitSeconds=" + AbsoluteGenerationAgeSeconds,
                    100);
                return false;
            }

            if (completedCts != null)
            {
                try { completedCts.Dispose(); } catch { }
            }

            Log.Info("BuyerSessionAgent transition: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", generation=" + generation
                + ", state=" + previous + "->" + next
                + ", latest=" + updateLatestState
                + ", reason=" + Normalize(reason));
            return true;
        }

        public void Cancel(string sellerNick, string buyerNick, long generation, string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return;
            CancellationTokenSource cts = null;
            lock (state.SyncRoot)
            {
                if (!state.ActiveGenerations.TryGetValue(generation, out cts)) return;
                state.ActiveGenerations.Remove(generation);
                SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Cancelled);
                if (state.GenerationCancellation == cts) state.GenerationCancellation = null;
                if (state.Generation == generation)
                    SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        public void CancelAll(string sellerNick, string buyerNick, string reason)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return;
            List<CancellationTokenSource> cancellations;
            lock (state.SyncRoot)
            {
                var generations = state.ActiveGenerations.Keys.ToList();
                cancellations = state.ActiveGenerations.Values
                    .Where(x => x != null)
                    .Distinct()
                    .ToList();
                foreach (var generation in generations)
                    SetGenerationStateLocked(state, generation, BuyerSessionAgentState.Cancelled);
                state.ActiveGenerations.Clear();
                state.GenerationCancellation = null;
                if (state.Generation > 0)
                    SetStateLocked(state, BuyerSessionAgentState.Cancelled, reason);
            }

            foreach (var cts in cancellations)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            Log.Info("BuyerSessionAgent hard-cancelled all generations: seller=" + Normalize(sellerNick)
                + ", buyer=" + Normalize(buyerNick)
                + ", count=" + cancellations.Count
                + ", reason=" + Normalize(reason));
        }

        public bool TryGetGenerationState(
            string sellerNick,
            string buyerNick,
            long generation,
            out BuyerSessionAgentState generationState)
        {
            generationState = BuyerSessionAgentState.Idle;
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            lock (state.SyncRoot)
            {
                return state.GenerationStates.TryGetValue(generation, out generationState);
            }
        }

        public bool TryGetGenerationAcceptedAtUtc(
            string sellerNick,
            string buyerNick,
            long generation,
            out DateTime acceptedAtUtc)
        {
            acceptedAtUtc = default(DateTime);
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return false;
            lock (state.SyncRoot)
            {
                return state.GenerationAcceptedAtUtc.TryGetValue(generation, out acceptedAtUtc)
                    && acceptedAtUtc != default(DateTime);
            }
        }

        public BuyerSessionAgentSnapshot GetSnapshot(string sellerNick, string buyerNick)
        {
            SessionState state;
            if (!Sessions.TryGetValue(BuildKey(sellerNick, buyerNick), out state)) return null;
            lock (state.SyncRoot)
            {
                return new BuyerSessionAgentSnapshot
                {
                    SellerNick = state.SellerNick,
                    BuyerNick = state.BuyerNick,
                    Generation = state.Generation,
                    State = state.State,
                    LastMessageKey = state.LastMessageKey,
                    LastSortValue = state.LastSortValue,
                    LastObservedAt = state.LastObservedAt,
                    StateChangedAt = state.StateChangedAt,
                    Reason = state.Reason,
                    LastEventKind = state.LastEventKind,
                    LastEventAt = state.LastEventAt,
                    LastBuyerEventSortValue = state.LastBuyerEventSortValue,
                    LastBuyerEventAt = state.LastBuyerEventAt,
                    RecentEvents = state.RecentEvents.Select(CloneEvent).ToList()
                };
            }
        }

        public void Prune(TimeSpan maxIdle)
        {
            var cutoff = DateTime.Now - maxIdle;
            foreach (var pair in Sessions.ToArray())
            {
                var remove = false;
                lock (pair.Value.SyncRoot)
                {
                    remove = pair.Value.LastObservedAt != default(DateTime)
                        && pair.Value.LastObservedAt < cutoff
                        && pair.Value.ActiveGenerations.Count == 0
                        && (pair.Value.State == BuyerSessionAgentState.Completed
                            || pair.Value.State == BuyerSessionAgentState.Cancelled
                            || pair.Value.State == BuyerSessionAgentState.Failed);
                }
                SessionState removed;
                if (remove && Sessions.TryRemove(pair.Key, out removed))
                {
                    lock (removed.SyncRoot)
                    {
                        foreach (var cts in removed.ActiveGenerations.Values)
                        {
                            try { if (cts != null) cts.Dispose(); } catch { }
                        }
                        removed.ActiveGenerations.Clear();
                        removed.GenerationCancellation = null;
                    }
                }
            }
        }

        private static SessionState GetOrCreateState(string key, string sellerNick, string buyerNick)
        {
            return Sessions.GetOrAdd(key, _ => new SessionState
            {
                SellerNick = Normalize(sellerNick),
                BuyerNick = Normalize(buyerNick),
                State = BuyerSessionAgentState.Idle,
                StateChangedAt = DateTime.Now
            });
        }

        private CancellationToken GetCancellationToken(string sessionKey, long generation)
        {
            SessionState state;
            if (!Sessions.TryGetValue(sessionKey, out state)) return CancellationToken.None;
            lock (state.SyncRoot)
            {
                CancellationTokenSource cts;
                return state.ActiveGenerations.TryGetValue(generation, out cts) && cts != null
                    ? cts.Token
                    : CancellationToken.None;
            }
        }

        private static bool IsGenerationExpiredLocked(
            SessionState state,
            long generation,
            DateTime nowUtc,
            out long elapsedMs)
        {
            elapsedMs = 0;
            DateTime acceptedAtUtc;
            if (!state.GenerationAcceptedAtUtc.TryGetValue(generation, out acceptedAtUtc)
                || acceptedAtUtc == default(DateTime))
            {
                return false;
            }
            var elapsed = nowUtc - acceptedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            elapsedMs = Math.Max(0, (long)elapsed.TotalMilliseconds);
            return elapsed.TotalSeconds > AbsoluteGenerationAgeSeconds;
        }

        private static bool IsOlderThanLatestBuyerLocked(SessionState state, long sortValue, DateTime sourceTimestamp)
        {
            var newestSort = Math.Max(state.LastSortValue, state.LastBuyerEventSortValue);
            if (sortValue > 0 && newestSort > 0 && sortValue < newestSort) return true;
            if (sourceTimestamp != default(DateTime)
                && state.LastBuyerEventAt != default(DateTime)
                && sourceTimestamp < state.LastBuyerEventAt.AddMilliseconds(-250)) return true;
            return false;
        }

        private static bool IsBuyerEvent(BuyerSessionEventKind kind)
        {
            return kind == BuyerSessionEventKind.BuyerText
                || kind == BuyerSessionEventKind.BuyerImage
                || kind == BuyerSessionEventKind.BuyerProductCard
                || kind == BuyerSessionEventKind.BuyerOrder
                || kind == BuyerSessionEventKind.BuyerSystem
                || kind == BuyerSessionEventKind.BuyerWithdrawal
                || kind == BuyerSessionEventKind.BuyerMedia
                || kind == BuyerSessionEventKind.BuyerActionAccepted;
        }

        private static void AppendEventLocked(
            SessionState state,
            BuyerSessionEventKind kind,
            string messageKey,
            long sortValue,
            DateTime sourceTimestamp,
            DateTime observedAt,
            long generation,
            bool stale,
            string reason)
        {
            state.EventSequence++;
            state.LastEventKind = kind;
            state.LastEventAt = observedAt;
            state.RecentEvents.Enqueue(new BuyerSessionEventSnapshot
            {
                Sequence = state.EventSequence,
                Kind = kind,
                MessageKey = messageKey,
                SortValue = sortValue,
                SourceTimestamp = sourceTimestamp,
                ObservedAt = observedAt,
                Generation = generation,
                StaleAgainstLatestBuyer = stale,
                Reason = Normalize(reason)
            });
            while (state.RecentEvents.Count > MaxRememberedEvents) state.RecentEvents.Dequeue();
        }

        private static BuyerSessionEventSnapshot CloneEvent(BuyerSessionEventSnapshot value)
        {
            return new BuyerSessionEventSnapshot
            {
                Sequence = value.Sequence,
                Kind = value.Kind,
                MessageKey = value.MessageKey,
                SortValue = value.SortValue,
                SourceTimestamp = value.SourceTimestamp,
                ObservedAt = value.ObservedAt,
                Generation = value.Generation,
                StaleAgainstLatestBuyer = value.StaleAgainstLatestBuyer,
                Reason = value.Reason
            };
        }

        private static void RememberMessageKeyLocked(SessionState state, string messageKey)
        {
            if (string.IsNullOrWhiteSpace(messageKey)) return;
            state.RecentMessageKeys.Enqueue(messageKey);
            state.RecentMessageKeySet.Add(messageKey);
            while (state.RecentMessageKeys.Count > MaxRememberedMessageKeys)
            {
                var removed = state.RecentMessageKeys.Dequeue();
                state.RecentMessageKeySet.Remove(removed);
            }
        }

        private static void RememberEventKeyLocked(SessionState state, string eventKey)
        {
            if (string.IsNullOrWhiteSpace(eventKey)) return;
            state.RecentEventKeys.Enqueue(eventKey);
            state.RecentEventKeySet.Add(eventKey);
            while (state.RecentEventKeys.Count > MaxRememberedEvents * 2)
            {
                var removed = state.RecentEventKeys.Dequeue();
                state.RecentEventKeySet.Remove(removed);
            }
        }

        private static string BuildEventDedupeKey(
            BuyerSessionEventKind kind,
            string messageKey,
            long sortValue,
            DateTime sourceTimestamp)
        {
            if (!string.IsNullOrWhiteSpace(messageKey)) return ((int)kind) + ":" + messageKey;
            if (sortValue > 0) return ((int)kind) + ":sort:" + sortValue;
            if (sourceTimestamp != default(DateTime)) return ((int)kind) + ":time:" + sourceTimestamp.Ticks;
            return string.Empty;
        }

        private static bool CanTransition(BuyerSessionAgentState current, BuyerSessionAgentState next)
        {
            if (next == BuyerSessionAgentState.Cancelled || next == BuyerSessionAgentState.Failed) return true;
            if (current == BuyerSessionAgentState.Cancelled || current == BuyerSessionAgentState.Failed) return false;
            if (next == BuyerSessionAgentState.Observed) return true;
            if (next == BuyerSessionAgentState.Completed) return true;
            return (int)next >= (int)current;
        }

        private static void SetGenerationStateLocked(
            SessionState state,
            long generation,
            BuyerSessionAgentState next)
        {
            if (!state.GenerationStates.ContainsKey(generation))
                state.GenerationStateOrder.Enqueue(generation);
            state.GenerationStates[generation] = next;
            while (state.GenerationStateOrder.Count > MaxRememberedGenerationStates)
            {
                var oldest = state.GenerationStateOrder.Dequeue();
                if (oldest == state.Generation || state.ActiveGenerations.ContainsKey(oldest))
                {
                    state.GenerationStateOrder.Enqueue(oldest);
                    break;
                }
                state.GenerationStates.Remove(oldest);
                state.GenerationAcceptedAtUtc.Remove(oldest);
            }
        }

        private static void SetStateLocked(SessionState state, BuyerSessionAgentState next, string reason)
        {
            state.State = next;
            state.StateChangedAt = DateTime.Now;
            state.Reason = Normalize(reason);
        }

        private static string BuildKey(string sellerNick, string buyerNick)
        {
            return Normalize(sellerNick) + "#" + Normalize(buyerNick);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class SessionState
        {
            public readonly object SyncRoot = new object();
            public string SellerNick;
            public string BuyerNick;
            public long Generation;
            public BuyerSessionAgentState State;
            public string LastMessageKey;
            public long LastSortValue;
            public DateTime LastObservedAt;
            public DateTime StateChangedAt;
            public string Reason;
            public CancellationTokenSource GenerationCancellation;
            public readonly Dictionary<long, CancellationTokenSource> ActiveGenerations =
                new Dictionary<long, CancellationTokenSource>();
            public readonly Dictionary<long, BuyerSessionAgentState> GenerationStates =
                new Dictionary<long, BuyerSessionAgentState>();
            public readonly Dictionary<long, DateTime> GenerationAcceptedAtUtc =
                new Dictionary<long, DateTime>();
            public readonly Queue<long> GenerationStateOrder = new Queue<long>();
            public long EventSequence;
            public BuyerSessionEventKind LastEventKind;
            public DateTime LastEventAt;
            public long LastBuyerEventSortValue;
            public DateTime LastBuyerEventAt;
            public readonly Queue<string> RecentMessageKeys = new Queue<string>();
            public readonly HashSet<string> RecentMessageKeySet = new HashSet<string>(StringComparer.Ordinal);
            public readonly Queue<string> RecentEventKeys = new Queue<string>();
            public readonly HashSet<string> RecentEventKeySet = new HashSet<string>(StringComparer.Ordinal);
            public readonly Queue<BuyerSessionEventSnapshot> RecentEvents = new Queue<BuyerSessionEventSnapshot>();
        }
    }
}