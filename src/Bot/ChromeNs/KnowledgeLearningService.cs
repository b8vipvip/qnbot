using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class KnowledgeLearningResult
    {
        public bool Success { get; set; }
        public bool Added { get; set; }
        public bool Updated { get; set; }
        public string Message { get; set; }
    }

    internal static class KnowledgeLearningService
    {
        private sealed class SourceStamp
        {
            public string Source;
            public DateTime ExpiresAt;
        }

        private sealed class BlockStamp
        {
            public string Reason;
            public string ManualAnswer;
            public DateTime ExpiresAt;
        }

        private static readonly ConcurrentDictionary<string, object> SaveLocks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, SourceStamp> Sources =
            new ConcurrentDictionary<string, SourceStamp>();
        private static readonly ConcurrentDictionary<string, BlockStamp> Blocks =
            new ConcurrentDictionary<string, BlockStamp>();
        private static readonly ConcurrentDictionary<string, DateTime> ManualBypass =
            new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, DateTime> ManualComparisons =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public static event EventHandler KnowledgeBaseChanged;

        public static void RegisterAnswerSource(
            string seller,
            string buyer,
            string question,
            string answer,
            string source)
        {
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(source)) return;
            BotReplyPreset preset;
            if (BotReplyPresetService.TryFindPreset(seller, answer, out preset)
                && (string.Equals(source, "本地", StringComparison.Ordinal)
                    || string.Equals(source, "商品链接预设回复", StringComparison.Ordinal)))
                source = BotReplyPresetService.BuildSourceLabel(preset);
            var stamp = new SourceStamp { Source = source, ExpiresAt = DateTime.Now.AddMinutes(30) };
            Sources[AnswerKey(seller, buyer, question, answer)] = stamp;
            Sources[QuestionSourceKey(seller, buyer, question)] = stamp;
            // Keep provenance across process restarts and delayed Qianniu echoes.
            BotConversationHistoryStore.SaveAnswerSourceStamp(seller, buyer, question, answer, source);
        }

        public static void OverrideAnswerSource(
            string seller, string buyer, string question, string answer, string source)
        {
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(source)) return;
            var stamp = new SourceStamp { Source = source.Trim(), ExpiresAt = DateTime.Now.AddMinutes(30) };
            // Operator overrides are answer-specific; do not let a corrected source
            // bleed into a different answer for the same buyer question.
            Sources[AnswerKey(seller, buyer, question, answer)] = stamp;
            BotConversationHistoryStore.SaveAnswerSourceStamp(seller, buyer, question, answer, source.Trim());
        }

        public static string ResolveAnswerSource(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            Cleanup();
            SourceStamp stamp;
            if (Sources.TryGetValue(AnswerKey(seller, buyer, question, answer), out stamp)
                && stamp.ExpiresAt >= DateTime.Now) return stamp.Source;
            // Answer-specific durable evidence must precede question-level
            // fallback, which could incorrectly label a different answer.
            var persisted = BotConversationHistoryStore.ResolveAnswerSourceStamp(seller, buyer, question, answer);
            if (!string.IsNullOrWhiteSpace(persisted)) return persisted;
            // No persisted stamp means the producer is unknown. Text equality to
            // today's editable preset must not fabricate provenance for old
            // messages or for an AI answer with identical wording.
            return string.Empty;
        }

        public static bool TryFindLocalAnswer(
            string seller,
            string buyer,
            string question,
            out KnowledgeBaseEntry matched,
            out double score)
        {
            matched = null;
            score = 0;
            var policy = BotFeatureStore.GetMessagePolicy();
            if (policy == null || !policy.EnableKnowledgeBase || string.IsNullOrWhiteSpace(question)) return false;

            ConversationContextTurn latestAgentPrompt = null;
            if (IsShortContextReply(question))
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 8);
                latestAgentPrompt = turns.LastOrDefault(x => x.Role == "assistant" && !string.IsNullOrWhiteSpace(x.Text));
            }

            foreach (var item in BotFeatureStore.GetKnowledgeBase()
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer)))
            {
                var currentScore = Score(item, question, false);
                if (latestAgentPrompt != null)
                    currentScore = Math.Max(currentScore, Score(item, latestAgentPrompt.Text, true));
                if (currentScore > score)
                {
                    score = currentScore;
                    matched = item;
                }
            }
            return matched != null && score >= 0.84;
        }

        private static bool IsShortContextReply(string value)
        {
            var compact = Normalize(value);
            if (compact.Length == 0 || compact.Length > 32) return false;
            if (compact.IndexOf('?') >= 0 || compact.IndexOf('？') >= 0) return false;
            if (Regex.IsMatch(compact, @"^[a-z0-9@._+\-:/]+$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(compact, @"^\d+$")) return true;
            return compact.Length <= 8;
        }

        private static double Score(KnowledgeBaseEntry item, string query, bool contextOnly)
        {
            var q = KnowledgeAiService.NormalizeQuestion(query);
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(title)) return 0;
            if (q == title) return contextOnly ? 0.91 : 1.0;
            if (Math.Min(q.Length, title.Length) >= 4 && (q.Contains(title) || title.Contains(q)))
                return contextOnly ? 0.87 : 0.95;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length >= 2 && q.Contains(normalizedKeyword))
                    return contextOnly ? 0.85 : 0.90;
            }
            var similarity = BigramSimilarity(q, title);
            if (similarity >= 0.68) return contextOnly ? 0.84 : 0.86;
            return similarity * 0.75;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static double BigramSimilarity(string a, string b)
        {
            var aa = Bigrams(a);
            var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++) set.Add(value.Substring(i, 2));
            return set;
        }

        public static void AllowNextManualSend(string seller, string buyer, string answer)
        {
            ManualBypass[SendKey(seller, buyer, answer)] = DateTime.Now.AddSeconds(15);
        }

        /// <summary>
        /// Detects a substantive human seller reply newer than the current buyer question.
        /// It blocks this exact pending Bot send at the last-mile UI boundary, without cancelling
        /// unrelated buyer generations, and records the human answer for asynchronous comparison.
        /// </summary>
        public static bool TryBlockForManualReply(
            QN qn,
            string buyer,
            string candidateAnswer,
            out string question,
            out string manualAnswer,
            string shopKey,
            out bool bypassed)
        {
            question = string.Empty;
            manualAnswer = string.Empty;
            bypassed = false;
            if (qn == null || qn.Seller == null) return false;
            var seller = qn.Seller.Nick ?? string.Empty;
            string resolvedShopKey;
            ShopContext shop;
            try
            {
                resolvedShopKey = ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey;
                shopKey = string.IsNullOrWhiteSpace(shopKey) ? resolvedShopKey : shopKey.Trim();
                if (!string.Equals(shopKey, resolvedShopKey, StringComparison.Ordinal)) return false;
                if (qn.CDP != null && (qn.CDP.IsInvalidated || qn.CDP.IsShopIdentityRetired
                    || !string.Equals(qn.CDP.ShopKey, shopKey, StringComparison.Ordinal))) return false;
                shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            }
            catch { return false; }
            using (ShopSettingsScope.Enter(shop))
            {
            DateTime bypassUntil;
            var sendKey = SendKey(seller, buyer, candidateAnswer);
            if (ManualBypass.TryRemove(sendKey, out bypassUntil) && bypassUntil >= DateTime.Now)
            {
                bypassed = true;
                return false;
            }

            DateTime questionTime;
            if (!ConversationContextStore.TryGetLatestBuyerQuestion(seller, buyer, out question, out questionTime)) return false;
            var ambiguousSellerEcho = false;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(QN);
                var buyerField = type.GetField("_lastSellerEchoBuyer", flags);
                var textField = type.GetField("_lastSellerEchoText", flags);
                var timeField = type.GetField("_lastSellerEchoTime", flags);
                if (buyerField == null || textField == null || timeField == null) return false;
                var echoBuyer = Convert.ToString(buyerField.GetValue(qn));
                var echoText = Convert.ToString(textField.GetValue(qn));
                var echoTime = (DateTime)timeField.GetValue(qn);
                if (!string.Equals((echoBuyer ?? string.Empty).Trim(), (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)) return false;
                if (echoTime < questionTime.AddMilliseconds(-500) || echoTime < DateTime.Now.AddMinutes(-20)) return false;
                if (string.IsNullOrWhiteSpace(echoText)) return false;

                // Once a recent seller echo is observed, uncertainty must block this
                // exact pending Bot send. It must not be promoted to human evidence.
                ambiguousSellerEcho = true;
                var sellerEcho = echoText.Trim();
                // Seller echoes include both human messages and Bot messages. Read the
                // exact ShopKey/session Bot-card range before treating an unmarked echo
                // as human evidence; an unavailable/truncated index is not proof of a human.
                List<BotConversationHistoryEntity> botCards;
                if (!BotConversationHistoryStore.TryLoadRangeComplete(
                    shopKey, seller, buyer, questionTime.AddMinutes(-1), echoTime.AddMinutes(1), out botCards))
                {
                    Log.Info("Bot回显历史不可可靠读取，按来源不明保护并停止当前Bot发送: seller="
                        + seller + ", buyer=" + buyer);
                    return true;
                }
                if (IsBotAnswer(seller, buyer, sellerEcho, echoTime, shopKey, botCards))
                {
                    ambiguousSellerEcho = false;
                    Log.Info("学习层忽略已确认Bot卖家回显，不作为人工纠正: seller="
                        + seller + ", buyer=" + buyer);
                    return false;
                }

                manualAnswer = sellerEcho;
                var sameAsCandidate = string.Equals(
                    Normalize(sellerEcho), Normalize(candidateAnswer), StringComparison.Ordinal);
                if (!ParallelReplyRelevanceGate.IsGreetingOnly(sellerEcho))
                {
                    // Safety decision must not depend on best-effort learning persistence.
                    // Mark the pending send as superseded before recording any comparison data.
                    SendDeliveryWatchdog.MarkManualTakeover(seller, buyer, candidateAnswer, shopKey);
                    try
                    {
                        RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复");
                        if (!sameAsCandidate)
                        {
                            string buyerMessageId;
                            string humanMessageId;
                            if (TryResolveManualEvidenceMessageIds(
                                seller, buyer, question, questionTime, manualAnswer, echoTime,
                                shopKey, botCards, out buyerMessageId, out humanMessageId))
                            {
                                QueueManualAnswerComparison(
                                    question, candidateAnswer, manualAnswer, seller, buyer,
                                    buyerMessageId, humanMessageId, shopKey);
                            }
                            else
                            {
                                // Do not auto-publish a correction from a reflected text
                                // echo alone. The full idle-session learner can still
                                // recover it later from the complete history with IDs.
                                Log.Info("人工接管已生效，但未找到唯一的原始买家/人工消息ID；即时自动学习已跳过，等待完整会话学习: seller="
                                    + seller + ", buyer=" + buyer);
                            }
                        }
                        MessageProcessingTraceService.RecordManualObservation(
                            seller,
                            buyer,
                            sameAsCandidate
                                ? "人工客服已发送与Bot候选相同的答案；已阻止Bot重复发送"
                                : "人工答案=" + Short(manualAnswer, 500) + "；当前Bot发送已阻止并进入对比学习");
                    }
                    catch (Exception learningError)
                    {
                        Log.Info("人工接管已生效，但人工答案学习记录失败: " + learningError.Message);
                    }
                    Log.Info("检测到本店客服人工接管，已阻止当前Bot发送: seller="
                        + seller + ", buyer=" + buyer + ", sameAsCandidate=" + sameAsCandidate);
                    return true;
                }

                // A short automatic greeting (e.g. “在的”) is not a full takeover.
                // The main send gate still decides whether the reviewed answer may continue.
                MessageProcessingTraceService.RecordManualObservation(
                    seller, buyer, "观察到客服简短接待语；未将其当作完整人工答案");
                Log.Info("检测到本店简短接待语，不作为人工接管: seller=" + seller + ", buyer=" + buyer);
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("检测本店客服回显来源失败：" + ex.Message
                    + (ambiguousSellerEcho ? "；来源不明，停止当前Bot发送" : "；未观察到有效卖家回显"));
                return ambiguousSellerEcho;
            }
            }
        }

        public static bool TryTakeSendBlock(
            string seller,
            string buyer,
            string answer,
            out string reason,
            out string manualAnswer)
        {
            reason = string.Empty;
            manualAnswer = string.Empty;
            BlockStamp stamp;
            if (!Blocks.TryRemove(SendKey(seller, buyer, answer), out stamp) || stamp.ExpiresAt < DateTime.Now) return false;
            reason = stamp.Reason;
            manualAnswer = stamp.ManualAnswer;
            return true;
        }

        private static bool IsBotAnswer(
            string seller, string buyer, string answer, DateTime answerAt,
            string shopKey, IList<BotConversationHistoryEntity> botCards)
        {
            var compact = Regex.Replace(answer ?? string.Empty, @"\s+", string.Empty);
            if (compact.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, answer, shopKey))
                    return true;
            }
            catch { /* The reliable session-scoped Bot-card snapshot remains authoritative. */ }
            if (botCards == null || answerAt == DateTime.MinValue) return true;

            var normalized = NormalizeBotAnswer(answer);
            if (normalized.Length == 0) return true;
            return botCards.Any(card =>
            {
                if (card == null) return false;
                var cardAnswer = NormalizeBotAnswer(card.Answer);
                var cardTicks = card.AnswerReadyAtTicks > 0 ? card.AnswerReadyAtTicks : card.CreatedAtTicks;
                return (string.Equals(cardAnswer, normalized, StringComparison.Ordinal)
                        || (normalized.Length >= 8 && cardAnswer.Contains(normalized)))
                    && cardTicks > 0
                    && Math.Abs((double)(cardTicks - answerAt.Ticks)) <= TimeSpan.FromMinutes(30).Ticks;
            });
        }

        private static string NormalizeBotAnswer(string value)
        {
            value = (value ?? string.Empty).Trim();
            try
            {
                var suffix = BotMessageSuffixService.GetCurrentSuffix();
                if (!string.IsNullOrWhiteSpace(suffix)
                    && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(0, value.Length - suffix.Length).TrimEnd();
            }
            catch { }
            foreach (var marker in new[] { "[A]", "[AI]", "【AI】", "［AI］" })
            {
                if (value.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(0, value.Length - marker.Length).TrimEnd();
            }
            return Normalize(value);
        }

        private static bool TryResolveManualEvidenceMessageIds(
            string seller,
            string buyer,
            string question,
            DateTime questionTime,
            string manualAnswer,
            DateTime answerTime,
            string shopKey,
            IList<BotConversationHistoryEntity> botCards,
            out string buyerMessageId,
            out string humanMessageId)
        {
            buyerMessageId = string.Empty;
            humanMessageId = string.Empty;
            if (questionTime == DateTime.MinValue || answerTime == DateTime.MinValue
                || answerTime <= questionTime
                || answerTime - questionTime > TimeSpan.FromMinutes(30))
                return false;

            var turns = ConversationContextStore.GetMirrorTurns(seller, buyer, 100)
                .Where(x => x != null && !x.Withdrawn)
                .OrderBy(x => x.PreciseTimeTicks > 0 ? x.PreciseTimeTicks : x.Timestamp.Ticks)
                .ToList();
            var normalizedQuestion = Normalize(question);
            var normalizedAnswer = Normalize(manualAnswer);
            var questionMatches = turns.Where(x => x.Role == "user"
                && x.Timestamp != DateTime.MinValue
                && Math.Abs((x.Timestamp - questionTime).TotalSeconds) <= 3
                && string.Equals(Normalize(x.Text), normalizedQuestion, StringComparison.Ordinal)
                && x.NativeAliases != null && x.NativeAliases.Any(y => !string.IsNullOrWhiteSpace(y)))
                .ToList();
            var answerMatches = turns.Where(x => x.Role == "assistant"
                && x.Timestamp != DateTime.MinValue
                && Math.Abs((x.Timestamp - answerTime).TotalSeconds) <= 5
                && string.Equals(Normalize(x.Text), normalizedAnswer, StringComparison.Ordinal)
                && x.NativeAliases != null && x.NativeAliases.Any(y => !string.IsNullOrWhiteSpace(y))
                && !IsBotAnswer(seller, buyer, x.Text, x.Timestamp, shopKey, botCards))
                .ToList();
            if (questionMatches.Count != 1 || answerMatches.Count != 1) return false;

            var questionTurn = questionMatches[0];
            var answerTurn = answerMatches[0];
            var questionIndex = turns.IndexOf(questionTurn);
            var answerIndex = turns.IndexOf(answerTurn);
            if (questionIndex < 0 || answerIndex <= questionIndex) return false;
            if (turns.Skip(questionIndex + 1).Take(answerIndex - questionIndex - 1)
                .Any(x => x.Role == "user"))
                return false;
            var humanReplyTurns = turns.Skip(questionIndex + 1)
                .Take(answerIndex - questionIndex)
                .Where(x => x.Role == "assistant"
                    && !IsBotAnswer(seller, buyer, x.Text, x.Timestamp, shopKey, botCards))
                .ToList();
            if (humanReplyTurns.Count != 1 || !ReferenceEquals(humanReplyTurns[0], answerTurn))
                return false;

            // The native aliases are the actual Qianniu message IDs; a synthesized
            // text/timestamp key is not sufficient provenance for automatic writes.
            buyerMessageId = questionTurn.NativeAliases
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
            humanMessageId = answerTurn.NativeAliases
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
            if (buyerMessageId.Length == 0 || humanMessageId.Length == 0
                || string.Equals(buyerMessageId, humanMessageId, StringComparison.Ordinal))
            {
                buyerMessageId = string.Empty;
                humanMessageId = string.Empty;
                return false;
            }
            return true;
        }

        private static void QueueManualAnswerComparison(
            string question,
            string botAnswer,
            string manualAnswer,
            string seller,
            string buyer,
            string buyerMessageId,
            string humanMessageId,
            string shopKey)
        {
            question = (question ?? string.Empty).Trim();
            botAnswer = StripBotMarker(botAnswer);
            manualAnswer = StripBotMarker(manualAnswer);
            shopKey = (shopKey ?? string.Empty).Trim();
            buyerMessageId = (buyerMessageId ?? string.Empty).Trim();
            humanMessageId = (humanMessageId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || buyerMessageId.Length == 0 || humanMessageId.Length == 0
                || string.Equals(buyerMessageId, humanMessageId, StringComparison.Ordinal)
                || !CanLearn(question, manualAnswer) || string.IsNullOrWhiteSpace(botAnswer)) return;

            var comparisonKey = shopKey + "|" + SendKey(seller, buyer,
                buyerMessageId + "|" + humanMessageId + "|" + question + "|" + botAnswer + "|" + manualAnswer);
            DateTime existing;
            if (ManualComparisons.TryGetValue(comparisonKey, out existing)
                && existing >= DateTime.Now.AddMinutes(-10)) return;
            ManualComparisons[comparisonKey] = DateTime.Now;

            Task.Run(async () =>
            {
                IDisposable scope = null;
                try
                {
                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    if (shop == null
                        || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                        || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal))
                        throw new InvalidOperationException("人工答案学习拒绝使用不匹配的ShopKey/Seller身份");
                    scope = ShopSettingsScope.Enter(shop);
                    Exception lastComparisonError = null;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            await CompareManualAnswerAsync(
                                question, botAnswer, manualAnswer, seller, buyer,
                                buyerMessageId, humanMessageId);
                            lastComparisonError = null;
                            break;
                        }
                        catch (Exception retryableError)
                        {
                            lastComparisonError = retryableError;
                            if (attempt < 2)
                                await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
                        }
                    }
                    if (lastComparisonError != null) throw lastComparisonError;
                }
                catch (OperationCanceledException)
                {
                    Log.Info("人工答案对比学习任务已取消：后台请求或会话生命周期已结束，不作为Bot运行故障。");
                }
                catch (Exception ex)
                {
                    DateTime ignoredComparison;
                    ManualComparisons.TryRemove(comparisonKey, out ignoredComparison);
                    MessageProcessingTraceService.RecordLearningComparison(
                        seller, buyer, "failed", "人工答案对比学习失败（3次尝试后）： " + Short(ex.Message, 500), true);
                    Log.Info("人工答案对比学习失败（3次尝试后）: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                finally
                {
                    if (scope != null) scope.Dispose();
                }
            });
        }

        private static async Task CompareManualAnswerAsync(
            string question,
            string botAnswer,
            string manualAnswer,
            string seller,
            string buyer,
            string buyerMessageId,
            string humanMessageId)
        {
            MessageProcessingTraceService.RecordLearningComparison(
                seller,
                buyer,
                "processing",
                "正在比较Bot答案与人工答案；Bot=" + Short(botAnswer, 350)
                    + "；人工=" + Short(manualAnswer, 350),
                false);

            double similarity = 0;
            try { similarity = KnowledgeEngineV2Semantics.TextSimilarity(botAnswer, manualAnswer); }
            catch { similarity = Normalize(botAnswer) == Normalize(manualAnswer) ? 1 : 0; }
            if (similarity >= 0.92)
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "ready",
                    "Bot答案与人工答案高度一致(similarity=" + similarity.ToString("0.00") + ")，无需修改知识。",
                    true);
                return;
            }

            if (ContainsUnsafeManualLearning(question + " " + manualAnswer))
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "skipped",
                    "人工答案包含一次性/高风险/敏感信息，仅保留会话证据，不自动升级知识。",
                    true);
                return;
            }

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "你是电商客服知识纠错审查器。比较Bot候选答案和真人客服实际答案，判断是否存在可跨买家复用的稳定知识修正。只输出JSON："
                        + "{\"should_learn\":true|false,\"action\":\"add|update|skip\",\"question\":\"通用化问题\",\"answer\":\"最终可复用答案\",\"category\":\"分类\",\"keywords\":[\"关键词\"],\"confidence\":0.0,\"reason\":\"原因\"}。"
                        + "人工答案优先级高于Bot，但不能因为措辞不同就修改知识。只有人工答案明确纠正了事实、补充了稳定必要步骤、或Bot遗漏了可复用关键条件时才should_learn=true。"
                        + "寒暄、临时价格/库存、单个订单进度、个人承诺、退款赔偿投诉、账号安全、验证码、手机号、订单号等必须skip。不得编造人工答案中不存在的事实。confidence低于0.95必须skip。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "买家问题：" + RedactSensitive(question)
                        + "\nBot候选答案：" + RedactSensitive(botAnswer)
                        + "\n人工实际答案：" + RedactSensitive(manualAnswer)
                }
            };

            var result = await Task.Run(() => MyOpenAI.CallStructuredChat(
                messages, 700, 0.03, 25, CancellationToken.None));
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
                throw new InvalidOperationException("AI对比上游未返回可靠结构化结果，等待自动重试。");

            JObject parsed;
            if (!TryParseObject(result.Answer, out parsed))
                throw new InvalidOperationException("AI对比结果不是可恢复的结构化JSON，等待自动重试。");
            bool shouldLearn;
            if (!bool.TryParse(Convert.ToString(parsed["should_learn"]), out shouldLearn)) shouldLearn = false;
            var action = Convert.ToString(parsed["action"]).Trim().ToLowerInvariant();
            double confidence;
            if (!double.TryParse(Convert.ToString(parsed["confidence"]), out confidence)) confidence = 0;
            var learnedQuestion = RedactSensitive(Convert.ToString(parsed["question"])).Trim();
            var learnedAnswer = RedactSensitive(Convert.ToString(parsed["answer"])).Trim();
            var reason = Convert.ToString(parsed["reason"]).Trim();

            if (!shouldLearn
                || (action != "add" && action != "update")
                || confidence < 0.95
                || !CanLearn(question, manualAnswer)
                // The model's normalized question/answer are review metadata only. The
                // durable write below deliberately stores the actual buyer wording and
                // exact human reply, so a paraphrase mismatch must not discard valid evidence.
                || ContainsUnsafeManualLearning(question + " " + manualAnswer)
                || ContainsUnsafeManualLearning(learnedQuestion + " " + learnedAnswer))
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "skipped",
                    "对比结论：未达到高置信真人纠正自动入库门槛。confidence=" + confidence.ToString("0.00")
                        + "；reason=" + Short(reason, 500),
                    true);
                return;
            }

            // The model only decides whether the human reply is a reusable correction.
            // Never store its paraphrase as fact: preserve the actual question and the
            // exact human-authored reply, then let the V2 repository apply its own
            // standalone/high-risk/context checks and immediately refresh the index.
            string saveMessage = string.Empty;
            string savedKnowledgeId = string.Empty;
            var sourceMessageIds = buyerMessageId + "|" + humanMessageId;
            var saved = KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(
                seller, question, manualAnswer, sourceMessageIds, out saveMessage, out savedKnowledgeId);
            if (!saved && (saveMessage ?? string.Empty).StartsWith("人工直答保存失败：", StringComparison.Ordinal))
                throw new InvalidOperationException(saveMessage);
            MessageProcessingTraceService.RecordLearningComparison(
                seller,
                buyer,
                saved ? "ready" : "skipped",
                "对比结论：" + (saved
                    ? "真人实际回复已直接保存到Knowledge V2（知识ID：" + savedKnowledgeId + "）"
                    : "Knowledge V2拒绝自动直答：" + saveMessage)
                    + "；confidence=" + confidence.ToString("0.00")
                    + "；reason=" + Short(reason, 500),
                true);
            Log.Info("人工答案对比学习完成: seller=" + seller + ", buyer=" + buyer
                + ", confidence=" + confidence.ToString("0.00")
                + ", saved=" + saved + ", knowledgeId=" + savedKnowledgeId);
        }

        private static bool ContainsUnsafeManualLearning(string value)
        {
            value = value ?? string.Empty;
            var terms = new[]
            {
                "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡",
                "验证码", "密码", "订单隐私", "订单号", "手机号", "账号安全", "封号", "解封",
                "法律", "报警", "仅此一次", "今天价格", "临时价格", "当前库存", "库存还有"
            };
            if (terms.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            if (Regex.IsMatch(value, @"(?<!\d)1\d{10}(?!\d)")) return true;
            if (Regex.IsMatch(value, @"(?<!\d)\d{12,}(?!\d)")) return true;
            return false;
        }

        private static string StripBotMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = Regex.Replace(value, @"\s*[\[【［]AI[\]】］]\s*$", string.Empty, RegexOptions.IgnoreCase);
            return value.Trim();
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        public static void QueueLearn(
            string question,
            string answer,
            string sourceType,
            string seller,
            string buyer)
        {
            if (!CanLearn(question, answer)) return;
            if (KugouReceptionPolicy.RequiresManualKnowledgeReview(seller, question, answer))
            {
                Log.Info("店铺业务知识自动学习改为会话复盘待审核：source=" + sourceType
                    + ", seller=" + seller);
                return;
            }
            var shop = ResolveShop(seller);
            Task.Run(async () =>
            {
                IDisposable scope = null;
                try
                {
                    if (shop != null) scope = ShopSettingsScope.Enter(shop);
                    await LearnAsync(question, answer, sourceType, seller, buyer);
                }
                catch (Exception ex)
                {
                    Log.Info("本店知识自动学习失败：" + ex.Message);
                }
                finally
                {
                    if (scope != null) scope.Dispose();
                }
            });
        }

        public static async Task<KnowledgeLearningResult> LearnAsync(
            string question,
            string answer,
            string sourceType,
            string seller,
            string buyer)
        {
            var resolvedShop = ResolveShop(seller);
            if (resolvedShop != null && ShopSettingsScope.Current == null)
            {
                using (ShopSettingsScope.Enter(resolvedShop))
                    return await LearnCoreAsync(question, answer, sourceType, seller, buyer);
            }
            return await LearnCoreAsync(question, answer, sourceType, seller, buyer);
        }

        private static async Task<KnowledgeLearningResult> LearnCoreAsync(
            string question,
            string answer,
            string sourceType,
            string seller,
            string buyer)
        {
            if (!CanLearn(question, answer))
            {
                return new KnowledgeLearningResult { Success = false, Message = "问题或答案为空，未写入知识库" };
            }
            // Explicit user edits are review evidence. Automatically observed bot
            // and human turns still go through the five-minute candidate review.
            var explicitOperatorEdit = string.Equals(sourceType, "人工修改", StringComparison.Ordinal)
                || string.Equals(sourceType, "管理员审核", StringComparison.Ordinal);
            if (!explicitOperatorEdit
                && KugouReceptionPolicy.RequiresManualKnowledgeReview(seller, question, answer))
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "涉及设备、会员权益或履约流程，等待人工审核，未自动写入"
                };
            }

            var context = ConversationContextStore.BuildTimelineText(seller, buyer, question, 10);
            var safeQuestion = RedactSensitive(question);
            var safeAnswer = RedactSensitive(answer);
            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = "你是电商客服知识库整理器。只输出一个JSON对象：{\"question\":\"通用化问题\",\"answer\":\"可复用答案\",\"category\":\"分类\",\"keywords\":[\"关键词\"]}。不得保留真实手机号、验证码、订单号、身份证、银行卡、买家账号等个人数据，必须改写成通用占位表达；不要编造事实。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "来源：" + sourceType + "\n原始问题：" + safeQuestion + "\n原始答案：" + safeAnswer
                        + (string.IsNullOrWhiteSpace(context) ? string.Empty : "\n同一买家最近时间线：\n" + RedactSensitive(context))
                }
            };

            var learnedQuestion = safeQuestion;
            var learnedAnswer = safeAnswer;
            var category = "自动学习";
            var keywords = string.Empty;
            try
            {
                var result = await Task.Run(() => MyOpenAI.CallStructuredChat(
                    messages, 500, 0.05, 90, CancellationToken.None));
                if (result.Success)
                {
                    var parsed = ParseObject(result.Answer);
                    learnedQuestion = RedactSensitive(Convert.ToString(parsed["question"])).Trim();
                    learnedAnswer = RedactSensitive(Convert.ToString(parsed["answer"])).Trim();
                    category = Convert.ToString(parsed["category"]).Trim();
                    var arr = parsed["keywords"] as JArray;
                    keywords = arr == null
                        ? Convert.ToString(parsed["keywords"])
                        : string.Join(",", arr.Select(x => x.ToString().Trim()).Where(x => x.Length > 0));
                    // AI may enrich the reusable question/category/keywords, but an answer that was
                    // explicitly confirmed by a human is immutable provenance. Never let the
                    // organizer paraphrase or broaden the human-confirmed answer text.
                    if (KnowledgeV2AuthorityPolicy.IsExplicitHumanConfirmationSource(sourceType))
                        learnedAnswer = safeAnswer;
                }
            }
            catch (Exception ex)
            {
                Log.Info("AI整理本店知识失败，使用安全兜底内容：" + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(learnedQuestion)) learnedQuestion = safeQuestion;
            if (string.IsNullOrWhiteSpace(learnedAnswer)) learnedAnswer = safeAnswer;
            if (string.IsNullOrWhiteSpace(category)) category = "自动学习";
            return SaveLearned(learnedQuestion, learnedAnswer, category, keywords, sourceType);
        }

        private static KnowledgeLearningResult SaveLearned(
            string question,
            string answer,
            string category,
            string keywords,
            string sourceType)
        {
            lock (SaveLocks.GetOrAdd(ScopeKey(), _ => new object()))
            {
                var list = BotFeatureStore.GetKnowledgeBase();
                var qKey = KnowledgeAiService.NormalizeQuestion(question);
                var manualPreferred = (sourceType ?? string.Empty).StartsWith("人工", StringComparison.Ordinal);
                var existing = list.FirstOrDefault(x => KnowledgeAiService.NormalizeQuestion(x.Title) == qKey);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (existing != null)
                {
                    if (manualPreferred && !string.Equals((existing.Answer ?? string.Empty).Trim(), answer.Trim(), StringComparison.Ordinal))
                    {
                        existing.Answer = answer.Trim();
                        existing.Category = category;
                        existing.Keywords = keywords;
                        existing.UpdatedAt = now;
                        existing.SourceType = sourceType;
                        existing.AiGenerated = false;
                        BotFeatureStore.SaveKnowledgeBase(list);
                        RaiseKnowledgeChanged();
                        return new KnowledgeLearningResult { Success = true, Updated = true, Message = "已用人工确认答案更新本店知识库" };
                    }
                    return new KnowledgeLearningResult { Success = true, Message = "本店知识库已存在相同问题，未重复添加" };
                }
                var contentHash = KnowledgeAiService.ContentHash(question, answer);
                if (list.Any(x => KnowledgeAiService.ContentHash(x.Title, x.Answer) == contentHash))
                    return new KnowledgeLearningResult { Success = true, Message = "本店知识库已存在相同内容，未重复添加" };

                list.Add(new KnowledgeBaseEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Enabled = true,
                    Category = category,
                    Title = question.Trim(),
                    Answer = answer.Trim(),
                    Keywords = keywords ?? string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AiGenerated = !manualPreferred,
                    SourceType = sourceType ?? "自动学习"
                });
                BotFeatureStore.SaveKnowledgeBase(list);
                RaiseKnowledgeChanged();
                return new KnowledgeLearningResult { Success = true, Added = true, Message = "已整理并加入本店知识库" };
            }
        }

        private static ShopContext ResolveShop(string seller)
        {
            if (ShopSettingsScope.Current != null) return ShopSettingsScope.Current;
            if (string.IsNullOrWhiteSpace(seller)) return null;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch { return null; }
        }

        private static JObject ParseObject(string text)
        {
            JObject result;
            if (TryParseObject(text, out result)) return result;
            throw new Exception("AI结构化结果无法恢复有效JSON对象（已尝试纯JSON、Markdown代码块、包装对象/数组、字符串包裹和括号平衡恢复）");
        }

        private static bool TryParseObject(string text, out JObject result)
        {
            result = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return false;

            if (TryParseObjectCandidate(text, 0, out result)) return true;

            foreach (Match fence in Regex.Matches(
                text,
                @"```(?:json)?\s*(?<body>[\s\S]*?)```",
                RegexOptions.IgnoreCase))
            {
                if (TryParseObjectCandidate(fence.Groups["body"].Value, 0, out result)) return true;
            }

            foreach (var candidate in ExtractBalancedJsonObjects(text))
            {
                if (TryParseObjectCandidate(candidate, 0, out result)) return true;
            }
            return false;
        }

        private static bool TryParseObjectCandidate(string candidate, int depth, out JObject result)
        {
            result = null;
            candidate = (candidate ?? string.Empty).Trim();
            if (candidate.Length == 0 || depth > 5) return false;
            try
            {
                var token = JToken.Parse(candidate);
                return TrySelectLearningObject(token, depth, out result);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySelectLearningObject(JToken token, int depth, out JObject result)
        {
            result = null;
            if (token == null || depth > 5) return false;

            var obj = token as JObject;
            if (obj != null)
            {
                if (HasLearningObjectShape(obj))
                {
                    result = obj;
                    return true;
                }
                foreach (var property in obj.Properties())
                {
                    if (TrySelectLearningObject(property.Value, depth + 1, out result)) return true;
                }
                return false;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var child in array)
                {
                    if (TrySelectLearningObject(child, depth + 1, out result)) return true;
                }
                return false;
            }

            if (token.Type != JTokenType.String) return false;
            var nestedText = Convert.ToString(((JValue)token).Value);
            if (string.IsNullOrWhiteSpace(nestedText)) return false;

            if (TryParseObjectCandidate(nestedText, depth + 1, out result)) return true;
            foreach (Match fence in Regex.Matches(
                nestedText,
                @"```(?:json)?\s*(?<body>[\s\S]*?)```",
                RegexOptions.IgnoreCase))
            {
                if (TryParseObjectCandidate(fence.Groups["body"].Value, depth + 1, out result)) return true;
            }
            foreach (var candidate in ExtractBalancedJsonObjects(nestedText))
            {
                if (TryParseObjectCandidate(candidate, depth + 1, out result)) return true;
            }
            return false;
        }

        private static bool HasLearningObjectShape(JObject obj)
        {
            if (obj == null) return false;
            var hasQuestion = obj["question"] != null;
            var hasAnswer = obj["answer"] != null;
            var hasReviewDecision = obj["should_learn"] != null || obj["action"] != null;
            return (hasQuestion && hasAnswer) || (hasReviewDecision && hasAnswer);
        }

        private static List<string> ExtractBalancedJsonObjects(string text)
        {
            var result = new List<string>();
            text = text ?? string.Empty;
            var start = -1;
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }
                if (ch == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                    continue;
                }
                if (ch != '}' || depth <= 0) continue;

                depth--;
                if (depth == 0 && start >= 0)
                {
                    result.Add(text.Substring(start, i - start + 1));
                    start = -1;
                    if (result.Count >= 12) break;
                }
            }
            return result;
        }

        private static bool CanLearn(string question, string answer)
        {
            return !string.IsNullOrWhiteSpace(question)
                && !string.IsNullOrWhiteSpace(answer)
                && !answer.StartsWith("错误：", StringComparison.Ordinal)
                && answer.IndexOf("已跳过", StringComparison.Ordinal) < 0;
        }

        private static string RedactSensitive(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{15,19}(?!\d)", "[敏感编号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return value;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string ScopeKey()
        {
            var shop = ShopSettingsScope.Current;
            return shop == null ? "legacy" : shop.ShopKey;
        }

        private static string AnswerKey(string seller, string buyer, string question, string answer)
        {
            return QuestionSourceKey(seller, buyer, question) + "|" + Normalize(BotReplyPresetService.NormalizeReply(answer));
        }

        private static string QuestionSourceKey(string seller, string buyer, string question)
        {
            return ScopeKey() + "|" + Normalize(seller) + "|" + Normalize(buyer) + "|"
                + KnowledgeAiService.NormalizeQuestion(question);
        }

        private static string SendKey(string seller, string buyer, string answer)
        {
            return ScopeKey() + "|" + Normalize(seller) + "|" + Normalize(buyer) + "|" + Normalize(answer);
        }

        private static void Cleanup()
        {
            var now = DateTime.Now;
            foreach (var key in Sources.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList())
            {
                SourceStamp ignored;
                Sources.TryRemove(key, out ignored);
            }
            foreach (var key in Blocks.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList())
            {
                BlockStamp ignored;
                Blocks.TryRemove(key, out ignored);
            }
            foreach (var key in ManualBypass.Where(x => x.Value < now).Select(x => x.Key).ToList())
            {
                DateTime ignored;
                ManualBypass.TryRemove(key, out ignored);
            }
            foreach (var key in ManualComparisons.Where(x => x.Value < now.AddHours(-2)).Select(x => x.Key).ToList())
            {
                DateTime ignored;
                ManualComparisons.TryRemove(key, out ignored);
            }
        }

        private static void RaiseKnowledgeChanged()
        {
            var handler = KnowledgeBaseChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }
}