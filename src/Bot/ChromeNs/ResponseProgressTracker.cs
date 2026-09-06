using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// UI/metrics observer only. BuyerSessionAgent is the sole business lifecycle authority.
    /// A terminal/stale observation may remove or update an existing turn, but must never recreate
    /// a turn or fall through onto another generation.
    /// </summary>
    internal static class ResponseProgressTracker
    {
        private sealed class Entry
        {
            public readonly object Sync = new object();
            public string ConversationKey = string.Empty;
            public string TurnKey = string.Empty;
            public CtlConversation Control;
            public string Question = string.Empty;
            public string Answer = string.Empty;
            public DateTime DetectedAt = DateTime.MinValue;
            public DateTime AnswerStartedAt = DateTime.MinValue;
            public DateTime AnswerReadyAt = DateTime.MinValue;
        }

        private sealed class DeliveryUiEntry
        {
            public CtlConversation Control;
            public string Source = string.Empty;
            public DateTime ExpiresAt;
        }

        // Entries are keyed by seller+buyer+turn timestamp rather than only seller+buyer. This is
        // required because ordinary later buyer messages no longer cancel an already-dispatched Bot
        // generation; Q1 and Q2 can therefore legitimately be in-flight at the same time.
        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> CurrentTurns =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DeliveryUiEntry> DeliveryUi =
            new ConcurrentDictionary<string, DeliveryUiEntry>(StringComparer.Ordinal);
        private static readonly AsyncLocal<string> OperationTurnKey = new AsyncLocal<string>();

        private static string Key(string seller, string buyer)
        {
            return ScopeKey(seller) + "#" + (seller ?? string.Empty).Trim()
                + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string ScopeKey(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch { return "legacy-" + (seller ?? string.Empty).Trim().ToLowerInvariant(); }
        }

        private static string TurnKey(string seller, string buyer, DateTime detectedAt)
        {
            return Key(seller, buyer) + "#turn:" + detectedAt.Ticks;
        }

        private static string DeliveryKey(string seller, string buyer, string answer)
        {
            return Key(seller, buyer) + "#"
                + Regex.Replace((answer ?? string.Empty).Trim(), @"\s+", string.Empty);
        }

        public static CtlConversation ObserveQuestion(
            string seller,
            string buyer,
            string question,
            DateTime detectedAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return null;

            var observedAt = NormalizeDetectedAt(detectedAt);
            ObserveNewBuyerTurn(seller, buyer);
            SendDeliveryWatchdog.OnBuyerMessageObserved(seller, buyer, observedAt);
            CleanupEntries();
            CleanupDeliveryUi();
            if (ShouldDeferUnsupportedMediaCard(question)) return null;

            var conversationKey = Key(seller, buyer);
            var turnKey = TurnKey(seller, buyer, observedAt);
            var entry = Entries.GetOrAdd(turnKey, _ => new Entry
            {
                ConversationKey = conversationKey,
                TurnKey = turnKey,
                DetectedAt = observedAt
            });

            var firstObservation = false;
            lock (entry.Sync)
            {
                firstObservation = string.IsNullOrWhiteSpace(entry.Question);
                if (entry.DetectedAt == DateTime.MinValue) entry.DetectedAt = observedAt;
                entry.Question = MergeQuestion(entry.Question, question);
                var sellerDesk = Desk.FindExistingBySellerNick(seller);
                if (entry.Control == null && sellerDesk != null)
                {
                    try
                    {
                        entry.Control = sellerDesk.AddConversation(
                            seller, buyer, entry.Question,
                            "正在识别并等待买家本轮消息结束...", false, "处理中");
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("创建本店回复进度卡片失败，已忽略UI异常继续处理消息：" + ex.Message, 10);
                        entry.Control = null;
                    }
                }
                if (entry.Control != null)
                {
                    entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                    if (entry.AnswerStartedAt == DateTime.MinValue)
                        entry.Control.SetProcessing("已识别，等待合并本轮消息...");
                }
            }

            PromoteCurrentTurn(conversationKey, entry, seller, buyer);
            if (firstObservation)
                MessageProcessingTraceService.RecordQuestion(seller, buyer, entry.Question);
            return entry.Control;
        }

        public static void MarkContextualContinuationMerged(
            string seller,
            string buyer,
            DateTime previousDetectedAt,
            string currentFragment)
        {
            if (previousDetectedAt == DateTime.MinValue) return;
            var turnKey = TurnKey(seller, buyer, NormalizeDetectedAt(previousDetectedAt));
            Entry entry;
            if (!Entries.TryGetValue(turnKey, out entry) || entry == null) return;

            lock (entry.Sync)
            {
                // Once an answer is already ready it is historical evidence, not a pending card.
                if (entry.AnswerReadyAt != DateTime.MinValue) return;
                if (entry.Control != null)
                {
                    entry.Control.SetStatus(
                        "买家后续发送了省略补充/催问，本条已合并到最新问题语义中，不再独立生成答案",
                        false);
                }
            }
            Entry removed;
            Entries.TryRemove(turnKey, out removed);
            Log.Info("未完成买家问题已并入后续省略/催问: seller=" + seller
                + ", buyer=" + buyer + ", fragment=" + (currentFragment ?? string.Empty));
        }

        public static CtlConversation BeginAnswer(
            string seller, string buyer, string combinedQuestion, DateTime detectedAt)
        {
            var detected = NormalizeDetectedAt(detectedAt);
            var control = ObserveQuestion(seller, buyer, combinedQuestion, detected);
            var turnKey = TurnKey(seller, buyer, detected);
            ConsolidatePendingBurstEntries(seller, buyer, combinedQuestion, detected, turnKey);
            OperationTurnKey.Value = turnKey;
            control = SetExactQuestionByTurn(turnKey, combinedQuestion, detected) ?? control;
            var startedAt = MarkAnswerStarted(turnKey, DateTime.Now);
            if (control != null) control.SetProcessing("正在获取答案...");
            var queueMs = Math.Max(0, (long)(startedAt - detected).TotalMilliseconds);
            MessageProcessingTraceService.RecordGenerationStarted(
                seller, buyer, combinedQuestion, queueMs);
            Log.Info("本店回复进度进入答案生成: seller=" + seller + ", buyer=" + buyer
                + ", turn=" + turnKey + ", queueMs=" + queueMs);
            return control;
        }

        public static CtlConversation SetAnswerReady(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerReadyAt)
        {
            if (answerReadyAt == DateTime.MinValue) answerReadyAt = DateTime.Now;
            var detected = detectedAt == DateTime.MinValue ? answerReadyAt : detectedAt;
            var turnKey = ResolveOperationOrDetectedTurnKey(seller, buyer, detected);
            Entry entry;
            if (string.IsNullOrWhiteSpace(turnKey)
                || !Entries.TryGetValue(turnKey, out entry)
                || entry == null)
            {
                Log.Info("已丢弃失效turn的迟到答案就绪观察，不重建回复进度: seller=" + seller
                    + ", buyer=" + buyer + ", turn=" + (turnKey ?? string.Empty));
                return null;
            }
            OperationTurnKey.Value = turnKey;
            var control = SetExactQuestionByTurn(turnKey, question, detected);
            var answerStartedAt = detected;
            lock (entry.Sync)
            {
                entry.AnswerReadyAt = answerReadyAt;
                entry.Answer = answer ?? string.Empty;
                answerStartedAt = entry.AnswerStartedAt == DateTime.MinValue ? detected : entry.AnswerStartedAt;
                control = entry.Control ?? control;
            }
            if (control != null)
            {
                control.SetAnswer(answer, source, answerReadyAt);
                control.SetSendPending("答案已生成，准备发送...");
                DeliveryUi[DeliveryKey(seller, buyer, answer)] = new DeliveryUiEntry
                {
                    Control = control,
                    Source = source ?? string.Empty,
                    ExpiresAt = DateTime.Now.AddMinutes(3)
                };
            }
            var responseMs = Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds);
            MessageProcessingTraceService.RecordAnswerReady(
                seller, buyer, question, answer, source, responseMs);
            Log.Info("本店回复进度答案就绪: seller=" + seller + ", buyer=" + buyer
                + ", turn=" + turnKey
                + ", responseMs=" + responseMs
                + ", source=" + (source ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(answer) && !answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReplyQualityMetricsService.RecordRoute(ResolveQualityRoute(source), false, 0);
                ReplyQualityMetricsService.RecordAnswerReady(
                    Math.Max(0, (long)(answerReadyAt - answerStartedAt).TotalMilliseconds),
                    Math.Max(0, (long)(answerReadyAt - detected).TotalMilliseconds),
                    Params.Robot.GetIsAutoReply());
            }

            SlowResponseAnomalyService.QueueIfSlow(
                seller, buyer, question, answer, source, detected, answerStartedAt, answerReadyAt);
            SendDeliveryWatchdog.ExpectDelivery(
                seller, buyer, question, answer, source, detected, answerReadyAt);
            return control;
        }

        public static void MarkDeliveryConfirmed(string seller, string buyer, string answer, string detail)
        {
            DeliveryUiEntry ui;
            if (DeliveryUi.TryRemove(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null && ui.Control != null)
            {
                ui.Control.SetSendResult(true,
                    string.IsNullOrWhiteSpace(detail) ? "已通过卖家消息回显确认真实发送" : detail);
            }
            BotConnectionDiagnostics.RecordSendAttempt(true,
                string.IsNullOrWhiteSpace(detail) ? "卖家消息回显确认真实发送" : detail);
            MessageProcessingTraceService.RecordDelivery(seller, buyer, true, detail);
            Log.Info("本店回复卡片已按卖家回显恢复为发送成功: seller=" + seller
                + ", buyer=" + buyer + ", detail=" + (detail ?? string.Empty));
        }

        public static void MarkDeliveryTimedOut(string seller, string buyer, string answer, string detail)
        {
            DeliveryUiEntry ui;
            if (DeliveryUi.TryRemove(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null && ui.Control != null)
                ui.Control.SetSendResult(false, "发送失败：" + (detail ?? string.Empty));
            MessageProcessingTraceService.RecordDelivery(seller, buyer, false, detail);
        }

        /// <summary>
        /// A human seller reply is observation/learning evidence only. Every active turn keeps its
        /// own card and Bot generation; no turn is removed or cancelled here.
        /// </summary>
        public static void MarkManualIntervention(string seller, string buyer, string sellerReply)
        {
            MessageProcessingTraceService.RecordManualObservation(seller, buyer, sellerReply);
            var conversationKey = Key(seller, buyer);
            foreach (var entry in Entries.Values
                .Where(x => x != null
                    && string.Equals(x.ConversationKey, conversationKey, StringComparison.Ordinal)
                    && x.DetectedAt >= DateTime.Now.AddMinutes(-30))
                .ToList())
            {
                lock (entry.Sync)
                {
                    if (entry.Control == null) continue;
                    if (entry.AnswerReadyAt == DateTime.MinValue)
                        entry.Control.SetProcessing("已观察到人工客服回复；Bot继续获取答案，稍后自动对比学习");
                    else
                        entry.Control.SetStatus("已观察到人工客服回复；Bot仍按原任务发送，并自动对比人工答案学习", false);
                }
            }
            Log.Info("已观察到人工客服回复但不取消Bot任务: seller=" + seller + ", buyer=" + buyer
                + ", reply=" + (sellerReply ?? string.Empty));
        }

        public static void ObserveNewBuyerTurn(string seller, string buyer)
        {
            // Human replies no longer create a conversation-wide intervention latch.
        }

        public static bool HasActiveManualIntervention(string seller, string buyer)
        {
            // Compatibility API: callers must not block Bot sending merely because a human replied.
            return false;
        }

        public static void Fail(string seller, string buyer, string detail)
        {
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
            MessageProcessingTraceService.RecordFailure(seller, buyer, detail);
            lock (entry.Sync)
            {
                if (entry.Control != null)
                {
                    entry.Control.SetAnswer(detail ?? string.Empty, "系统", DateTime.Now);
                    entry.Control.SetSkipped(detail);
                }
            }
        }

        public static void Cancel(string seller, string buyer, string detail)
        {
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!TryRemoveTurn(turnKey, out entry) || entry == null) return;
            MessageProcessingTraceService.RecordCancelled(seller, buyer, detail);
            lock (entry.Sync)
            {
                if (entry.Control != null)
                    entry.Control.SetStatus(string.IsNullOrWhiteSpace(detail) ? "回复任务已取消" : detail, false);
            }
        }

        public static void Complete(string seller, string buyer)
        {
            var turnKey = ResolveTerminalTurnKey(seller, buyer);
            Entry entry;
            if (!Entries.TryGetValue(turnKey, out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.AnswerReadyAt == DateTime.MinValue) return;
            }
            Entry ignored;
            TryRemoveTurn(turnKey, out ignored);
        }

        private static void PromoteCurrentTurn(string conversationKey, Entry entry, string seller, string buyer)
        {
            if (entry == null) return;
            while (true)
            {
                string previousKey;
                if (!CurrentTurns.TryGetValue(conversationKey, out previousKey))
                {
                    if (CurrentTurns.TryAdd(conversationKey, entry.TurnKey)) return;
                    continue;
                }
                if (string.Equals(previousKey, entry.TurnKey, StringComparison.Ordinal)) return;

                Entry previous;
                if (!Entries.TryGetValue(previousKey, out previous) || previous == null)
                {
                    if (CurrentTurns.TryUpdate(conversationKey, entry.TurnKey, previousKey)) return;
                    continue;
                }
                if (previous.DetectedAt > entry.DetectedAt) return;
                if (!CurrentTurns.TryUpdate(conversationKey, entry.TurnKey, previousKey)) continue;

                lock (previous.Sync)
                {
                    if (previous.Control != null)
                    {
                        previous.Control.SetStatus(
                  previous.AnswerReadyAt == DateTime.MinValue
                      ? "买家补充了新消息，上一条Bot任务继续独立处理，发送前会再次检查相关性"
                      : "买家已补充新消息，上一条答案保留；实际发送资格由业务/会话权威层决定",
                  false);
                    }
                }
                return;
            }
        }

        private static void ConsolidatePendingBurstEntries(
            string seller,
            string buyer,
            string combinedQuestion,
            DateTime detectedAt,
            string selectedTurnKey)
        {
            var conversationKey = Key(seller, buyer);
            var removedCurrent = false;
            foreach (var pair in Entries.ToArray())
            {
                var entry = pair.Value;
                if (entry == null
                    || string.Equals(pair.Key, selectedTurnKey, StringComparison.Ordinal)
                    || !string.Equals(entry.ConversationKey, conversationKey, StringComparison.Ordinal)
                    || entry.DetectedAt <= detectedAt
                    || entry.DetectedAt > detectedAt.AddSeconds(6)
                    || entry.AnswerStartedAt != DateTime.MinValue
                    || !QuestionIncluded(combinedQuestion, entry.Question))
                {
                    continue;
                }

                Entry removed;
                if (!Entries.TryRemove(pair.Key, out removed) || removed == null) continue;
                lock (removed.Sync)
                {
                    if (removed.Control != null)
                        removed.Control.SetStatus("该条消息已合并到同一轮连续消息中，由合并后的Bot任务统一处理", true);
                }
                string currentKey;
                if (CurrentTurns.TryGetValue(conversationKey, out currentKey)
                    && string.Equals(currentKey, pair.Key, StringComparison.Ordinal))
                {
                    removedCurrent = true;
                }
            }

            string existingCurrent;
            if (removedCurrent
                || !CurrentTurns.TryGetValue(conversationKey, out existingCurrent)
                || !Entries.ContainsKey(existingCurrent))
            {
                CurrentTurns[conversationKey] = selectedTurnKey;
            }
        }

        private static bool QuestionIncluded(string combinedQuestion, string candidate)
        {
            var combined = NormalizeQuestionForMerge(combinedQuestion);
            var part = NormalizeQuestionForMerge(candidate);
            return part.Length > 0 && combined.Contains(part);
        }

        private static string NormalizeQuestionForMerge(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static DateTime MarkAnswerStarted(string turnKey, DateTime startedAt)
        {
            Entry entry;
            if (!Entries.TryGetValue(turnKey, out entry) || entry == null) return startedAt;
            lock (entry.Sync)
            {
                if (entry.AnswerStartedAt == DateTime.MinValue)
                    entry.AnswerStartedAt = startedAt == DateTime.MinValue ? DateTime.Now : startedAt;
                return entry.AnswerStartedAt;
            }
        }

        private static CtlConversation SetExactQuestionByTurn(
            string turnKey, string question, DateTime detectedAt)
        {
            Entry entry;
            if (!Entries.TryGetValue(turnKey, out entry) || entry == null) return null;
            lock (entry.Sync)
            {
                entry.Question = (question ?? string.Empty).Trim();
                if (entry.DetectedAt == DateTime.MinValue) entry.DetectedAt = NormalizeDetectedAt(detectedAt);
                if (entry.Control != null)
                {
                    entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                    return entry.Control;
                }
            }
            return null;
        }

        private static string ResolveOperationOrDetectedTurnKey(string seller, string buyer, DateTime detectedAt)
        {
            var conversationKey = Key(seller, buyer);
            var operationKey = OperationTurnKey.Value;
            if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;
            return TurnKey(seller, buyer, NormalizeDetectedAt(detectedAt));
        }

        private static string ResolveTerminalTurnKey(string seller, string buyer)
        {
            var conversationKey = Key(seller, buyer);
            var operationKey = OperationTurnKey.Value;
            if (!string.IsNullOrWhiteSpace(operationKey)) return operationKey;
            string currentKey;
            return CurrentTurns.TryGetValue(conversationKey, out currentKey)
                ? currentKey
                : string.Empty;
        }

        private static bool TryRemoveTurn(string turnKey, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(turnKey) || !Entries.TryRemove(turnKey, out entry)) return false;
            var conversationKey = entry == null ? string.Empty : entry.ConversationKey;
            if (!string.IsNullOrWhiteSpace(conversationKey))
            {
                string currentKey;
                if (CurrentTurns.TryGetValue(conversationKey, out currentKey)
                    && string.Equals(currentKey, turnKey, StringComparison.Ordinal))
                {
                    var replacement = Entries.Values
                        .Where(x => x != null && string.Equals(x.ConversationKey, conversationKey, StringComparison.Ordinal))
                        .OrderByDescending(x => x.DetectedAt)
                        .FirstOrDefault();
                    if (replacement == null)
                    {
                        string ignored;
                        CurrentTurns.TryRemove(conversationKey, out ignored);
                    }
                    else
                    {
                        CurrentTurns[conversationKey] = replacement.TurnKey;
                    }
                }
            }
            if (string.Equals(OperationTurnKey.Value, turnKey, StringComparison.Ordinal))
                OperationTurnKey.Value = null;
            return true;
        }

        private static void CleanupEntries()
        {
            var cutoff = DateTime.Now.AddMinutes(-30);
            foreach (var pair in Entries.ToArray())
            {
                var entry = pair.Value;
                if (entry != null && entry.DetectedAt >= cutoff) continue;
                Entry ignored;
                TryRemoveTurn(pair.Key, out ignored);
            }
        }

        private static void CleanupDeliveryUi()
        {
            var now = DateTime.Now;
            foreach (var pair in DeliveryUi)
            {
                if (pair.Value != null && pair.Value.ExpiresAt >= now) continue;
                DeliveryUiEntry ignored;
                DeliveryUi.TryRemove(pair.Key, out ignored);
            }
        }

        private static DateTime NormalizeDetectedAt(DateTime value)
        {
            return value == DateTime.MinValue ? DateTime.Now : value;
        }

        private static bool ShouldDeferUnsupportedMediaCard(string question)
        {
            question = (question ?? string.Empty).Trim();
            if (!IncomingMessageSafety.IsMediaPlaceholder(question)) return false;
            if (string.Equals(question, "[图片]", StringComparison.Ordinal)
                && AiEndpointStore.GetVisionEnabledEndpoints().Count > 0) return false;
            return true;
        }

        private static string MergeQuestion(string existing, string latest)
        {
            existing = (existing ?? string.Empty).Trim();
            latest = (latest ?? string.Empty).Trim();
            if (latest.Length == 0) return existing;
            if (existing.Length == 0) return latest;
            if (string.Equals(existing, latest, StringComparison.Ordinal)) return existing;
            foreach (var line in existing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(line.Trim(), latest, StringComparison.Ordinal)) return existing;
            var merged = existing + "\n" + latest;
            return merged.Length <= 1600 ? merged : merged.Substring(merged.Length - 1600);
        }

        private static string ResolveQualityRoute(string source)
        {
            source = (source ?? string.Empty).Trim();
            if (source.IndexOf("本地直答", StringComparison.OrdinalIgnoreCase) >= 0) return "DIRECT_KNOWLEDGE";
            if (source.IndexOf("知识上下文", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("本地知识库上下文", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CONTEXTUAL_KNOWLEDGE";
            if (source.IndexOf("视觉", StringComparison.OrdinalIgnoreCase) >= 0) return "VISION";
            if (source.IndexOf("转人工", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工确认", StringComparison.OrdinalIgnoreCase) >= 0) return "MANUAL";
            if (source.IndexOf("本地", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("预设", StringComparison.OrdinalIgnoreCase) >= 0) return "PRESET";
            return "AI_GENERAL";
        }
    }
}