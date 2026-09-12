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
            var stamp = new SourceStamp { Source = source, ExpiresAt = DateTime.Now.AddMinutes(30) };
            Sources[AnswerKey(seller, buyer, question, answer)] = stamp;
            Sources[QuestionSourceKey(seller, buyer, question)] = stamp;
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
            if (Sources.TryGetValue(QuestionSourceKey(seller, buyer, question), out stamp)
                && stamp.ExpiresAt >= DateTime.Now) return stamp.Source;
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
        /// Historical name kept for binary/source compatibility. A human reply no longer blocks
        /// the Bot send. Instead it is captured as high-value evidence and compared asynchronously
        /// with the Bot candidate. Only reusable, high-confidence corrections may upgrade knowledge.
        /// </summary>
        public static bool TryBlockForManualReply(
            QN qn,
            string buyer,
            string candidateAnswer,
            out string question,
            out string manualAnswer)
        {
            question = string.Empty;
            manualAnswer = string.Empty;
            if (qn == null || qn.Seller == null) return false;
            var seller = qn.Seller.Nick ?? string.Empty;
            DateTime bypassUntil;
            var sendKey = SendKey(seller, buyer, candidateAnswer);
            if (ManualBypass.TryRemove(sendKey, out bypassUntil) && bypassUntil >= DateTime.Now) return false;

            DateTime questionTime;
            if (!ConversationContextStore.TryGetLatestBuyerQuestion(seller, buyer, out question, out questionTime)) return false;
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
                if (string.IsNullOrWhiteSpace(echoText) || Normalize(echoText) == Normalize(candidateAnswer)) return false;

                var sellerEcho = echoText.Trim();
                // The seller-echo fields contain both human messages and Bot messages. A previous
                // Bot reply (for example the fixed first-inquiry prelude) legitimately differs from
                // the current candidate and must not be promoted to a manual correction. Reuse the
                // delivery watchdog's authoritative Bot-echo ledger instead of inventing a second
                // text/timing heuristic here.
                if (SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, sellerEcho))
                {
                    Log.Info("学习层忽略已确认Bot卖家回显，不作为人工纠正: seller="
                        + seller + ", buyer=" + buyer);
                    return false;
                }

                manualAnswer = sellerEcho;
                RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复");
                QueueManualAnswerComparison(question, candidateAnswer, manualAnswer, seller, buyer);
                MessageProcessingTraceService.RecordManualObservation(
                    seller,
                    buyer,
                    "人工答案=" + Short(manualAnswer, 500) + "；Bot任务继续发送并进入对比学习");
                Log.Info("检测到本店客服人工回复但不取消Bot发送，已进入答案对比学习: seller="
                    + seller + ", buyer=" + buyer);
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("检测本店客服人工回复失败，继续原发送流程：" + ex.Message);
                return false;
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

        private static void QueueManualAnswerComparison(
            string question,
            string botAnswer,
            string manualAnswer,
            string seller,
            string buyer)
        {
            question = (question ?? string.Empty).Trim();
            botAnswer = StripBotMarker(botAnswer);
            manualAnswer = StripBotMarker(manualAnswer);
            if (!CanLearn(question, manualAnswer) || string.IsNullOrWhiteSpace(botAnswer)) return;

            var comparisonKey = SendKey(seller, buyer, question + "|" + botAnswer + "|" + manualAnswer);
            DateTime existing;
            if (ManualComparisons.TryGetValue(comparisonKey, out existing)
                && existing >= DateTime.Now.AddMinutes(-10)) return;
            ManualComparisons[comparisonKey] = DateTime.Now;

            Task.Run(async () =>
            {
                IDisposable scope = null;
                try
                {
                    var shop = ResolveShop(seller);
                    if (shop != null) scope = ShopSettingsScope.Enter(shop);
                    await CompareManualAnswerAsync(question, botAnswer, manualAnswer, seller, buyer);
                }
                catch (OperationCanceledException)
                {
                    Log.Info("人工答案对比学习任务已取消：后台请求或会话生命周期已结束，不作为Bot运行故障。");
                }
                catch (Exception ex)
                {
                    MessageProcessingTraceService.RecordLearningComparison(
                        seller, buyer, "failed", "人工答案对比学习失败：" + Short(ex.Message, 500), true);
                    Log.Info("人工答案对比学习失败: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
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
            string buyer)
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
                        + "寒暄、临时价格/库存、单个订单进度、个人承诺、退款赔偿投诉、账号安全、验证码、手机号、订单号等必须skip。不得编造人工答案中不存在的事实。confidence低于0.90必须skip。"
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
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "skipped",
                    "AI对比未得到可靠结构化结果，本次不修改知识。",
                    true);
                return;
            }

            JObject parsed;
            if (!TryParseObject(result.Answer, out parsed))
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "skipped",
                    "AI对比结果不是可恢复的结构化JSON，本次不修改知识。",
                    true);
                return;
            }
            bool shouldLearn;
            if (!bool.TryParse(Convert.ToString(parsed["should_learn"]), out shouldLearn)) shouldLearn = false;
            var action = Convert.ToString(parsed["action"]).Trim().ToLowerInvariant();
            double confidence;
            if (!double.TryParse(Convert.ToString(parsed["confidence"]), out confidence)) confidence = 0;
            var learnedQuestion = RedactSensitive(Convert.ToString(parsed["question"])).Trim();
            var learnedAnswer = RedactSensitive(Convert.ToString(parsed["answer"])).Trim();
            var category = Convert.ToString(parsed["category"]).Trim();
            var keywordsToken = parsed["keywords"];
            var keywords = keywordsToken is JArray
                ? string.Join(",", ((JArray)keywordsToken).Select(x => x.ToString().Trim()).Where(x => x.Length > 0))
                : Convert.ToString(keywordsToken).Trim();
            var reason = Convert.ToString(parsed["reason"]).Trim();

            if (!shouldLearn
                || (action != "add" && action != "update")
                || confidence < 0.90
                || !CanLearn(learnedQuestion, learnedAnswer)
                || ContainsUnsafeManualLearning(learnedQuestion + " " + learnedAnswer))
            {
                MessageProcessingTraceService.RecordLearningComparison(
                    seller,
                    buyer,
                    "skipped",
                    "对比结论：无需升级知识。confidence=" + confidence.ToString("0.00")
                        + "；reason=" + Short(reason, 500),
                    true);
                return;
            }

            if (string.IsNullOrWhiteSpace(category)) category = "人工对照学习";
            var saved = SaveLearned(
                learnedQuestion,
                learnedAnswer,
                category,
                keywords,
                "人工对照学习");
            MessageProcessingTraceService.RecordLearningComparison(
                seller,
                buyer,
                saved != null && saved.Success ? "ready" : "failed",
                "对比结论：" + (saved == null ? "知识写入结果为空" : saved.Message)
                    + "；confidence=" + confidence.ToString("0.00")
                    + "；reason=" + Short(reason, 500),
                true);
            Log.Info("人工答案对比学习完成: seller=" + seller + ", buyer=" + buyer
                + ", confidence=" + confidence.ToString("0.00")
                + ", result=" + (saved == null ? "null" : saved.Message));
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
            return QuestionSourceKey(seller, buyer, question) + "|" + Normalize(answer);
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