using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Post-learning governance for human-reviewed knowledge.
    /// ConversationSessionLearningService remains responsible for deciding whether evidence is safe
    /// enough to learn. This bridge gives an applied correction an explicit commerce scenario scope,
    /// and preserves the previous answer as a separate variant instead of allowing one question text
    /// to collapse every pre-order/post-order/product state into a single answer.
    /// </summary>
    internal static class CommerceKnowledgeLearningBridge
    {
        private static int _initialized;
        private static int _processing;
        private static int _pendingUpdates;
        private static Timer _retryTimer;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                ConversationSessionLearningService.ReportsChanged += OnReportsChanged;
                // Retry shop-unavailable reports after reconnect and reconcile notifications
                // that arrived while the bridge was processing a previous report.
                _retryTimer = new Timer(_ => OnReportsChanged(), null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
                OnReportsChanged();
                Log.Info("Commerce知识学习桥已启动：人工纠错将按订单阶段/履约/商品场景分流，旧答案不再仅因问题文本相同而丢失。" );
            }
            return new object();
        }

        private static void OnReportsChanged()
        {
            Interlocked.Exchange(ref _pendingUpdates, 1);
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    do
                    {
                        Interlocked.Exchange(ref _pendingUpdates, 0);
                        ProcessLatest();
                    } while (Interlocked.CompareExchange(ref _pendingUpdates, 0, 1) == 1);
                }
                catch (Exception ex) { Log.ErrorWithMaxCount("Commerce知识学习桥处理失败: " + ex.Message, 20); }
                finally
                {
                    Interlocked.Exchange(ref _processing, 0);
                    // A notification arriving between the last drain and releasing the
                    // gate must not be silently lost; the next worker owns that drain.
                    if (Interlocked.CompareExchange(ref _pendingUpdates, 0, 0) != 0)
                        OnReportsChanged();
                }
            });
        }

        private static void ProcessLatest()
        {
            // The durable marker is written only after the ShopKey-scoped operation finishes.
            // A crash before the marker leaves the report pending for the next process; old
            // reports are migrated as historical so current rules are never replayed onto them.
            var reports = ConversationSessionLearningService.GetPendingCommerceReports(
                    ConversationSessionLearningService.MaxReports)
                .Where(x => x != null && x.CompletedAt != DateTime.MinValue)
                .OrderBy(x => x.CompletedAt)
                .ToList();
            foreach (var report in reports)
            {
                if (string.IsNullOrWhiteSpace(report.Id)) continue;
                try
                {
                    if (!ProcessReport(report)) continue;
                    if (!ConversationSessionLearningService.TryMarkCommerceScenarioProcessed(
                        report.Id, report.ShopKey))
                    {
                        Log.ErrorWithMaxCount(
                            "Commerce场景学习报告处理结果未能持久化，等待自动重试: report=" + report.Id, 10);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Commerce场景知识治理失败: report=" + report.Id + ", error=" + ex.Message, 20);
                }
            }
        }

        private static bool ProcessReport(ConversationSessionLearningReportView report)
        {
            if (report == null) return true;
            // A report without ShopKey may be a legacy row awaiting identity migration.
            // Keep it pending rather than consuming it or guessing by seller nickname.
            if (string.IsNullOrWhiteSpace(report.ShopKey)) return false;
            if (string.IsNullOrWhiteSpace(report.Seller)
                || string.IsNullOrWhiteSpace(report.Buyer) || string.IsNullOrWhiteSpace(report.SuggestionsJson)) return true;

            // The report already carries its authoritative ShopKey. Resolving by display nickname
            // is ambiguous when two accounts share the same seller nickname and can bind to the wrong
            // shop if a different account is currently active.
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(report.ShopKey);
            if (shop == null) return false;
            if (!string.Equals(
                (shop.SellerNick ?? string.Empty).Trim(),
                report.Seller.Trim(),
                StringComparison.Ordinal))
            {
                Log.ErrorWithMaxCount(
                    "Commerce场景知识治理拒绝Seller与ShopKey不匹配的报告: report=" + report.Id, 10);
                return true;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                var turns = ConversationContextStore.GetRecentTurns(report.Seller, report.Buyer, report.ShopKey, 24);
                var state = ConversationStateService.Build(report.Seller, report.Buyer, string.Empty, turns);
                var commerce = state.CommerceContext
                    ?? CommerceContextService.Build(report.Seller, report.Buyer, string.Empty, turns, state);
                var scopeToken = CommerceContextService.BuildPolicyScopeToken(commerce);
                if (string.IsNullOrWhiteSpace(scopeToken)) return true;

                JArray suggestions;
                try { suggestions = JArray.Parse(report.SuggestionsJson); }
                catch { return true; }

                var changed = 0;
                foreach (var suggestion in suggestions.OfType<JObject>())
                {
                    if (!ReadBool(suggestion["applied"])) continue;
                    var evidence = Clean(Convert.ToString(suggestion["evidence_type"]), 80).ToLowerInvariant();
                    if (!IsReusableEvidence(evidence)) continue;
                    var question = Clean(Convert.ToString(suggestion["question"]), 400);
                    var answer = Clean(Convert.ToString(suggestion["answer"]), 1200);
                    var oldAnswer = Clean(Convert.ToString(suggestion["old_answer"]), 1200);
                    if (question.Length == 0 || answer.Length == 0) continue;
                    if (ApplyScenarioScope(question, answer, oldAnswer, scopeToken, evidence)) changed++;
                }
                if (changed > 0)
                {
                    Log.Info("Commerce场景知识治理完成: seller=" + report.Seller
                        + ", report=" + report.Id + ", scoped=" + changed
                        + ", scenario=" + scopeToken);
                }
            }
            return true;
        }

        private static bool ApplyScenarioScope(
            string question,
            string answer,
            string oldAnswer,
            string scopeToken,
            string evidenceType)
        {
            var list = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var questionKey = KnowledgeAiService.NormalizeQuestion(question);
            var answerKey = NormalizeAnswer(answer);
            if (questionKey.Length == 0 || answerKey.Length == 0) return false;

            var current = list.LastOrDefault(x => x != null
                && KnowledgeAiService.NormalizeQuestion(x.Title) == questionKey
                && NormalizeAnswer(x.Answer) == answerKey)
                ?? list.LastOrDefault(x => x != null
                    && KnowledgeAiService.NormalizeQuestion(x.Title) == questionKey);
            if (current == null) return false;

            var oldKey = NormalizeAnswer(oldAnswer);
            var currentProfileBefore = KnowledgePolicyProfileService.GetProfile(current);
            KnowledgeBaseEntry preserved = null;
            if (oldKey.Length > 0 && oldKey != answerKey)
            {
                preserved = list.FirstOrDefault(x => x != null
                    && KnowledgeAiService.NormalizeQuestion(x.Title) == questionKey
                    && NormalizeAnswer(x.Answer) == oldKey);
                if (preserved == null)
                {
                    var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    preserved = new KnowledgeBaseEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Enabled = true,
                        Category = current.Category,
                        Title = question,
                        Answer = oldAnswer,
                        Keywords = current.Keywords,
                        CreatedAt = now,
                        UpdatedAt = now,
                        AiGenerated = false,
                        SourceType = "场景分流保留-纠错前答案"
                    };
                    list.Add(preserved);
                    BotFeatureStore.SaveKnowledgeBase(list);

                    var oldProfile = CloneProfile(currentProfileBefore);
                    oldProfile.DoNotApplyWhen = MergeCondition(oldProfile.DoNotApplyWhen, scopeToken);
                    oldProfile.AnswerMode = KnowledgeAnswerModes.Contextual;
                    oldProfile.LastEvidenceType = "scenario_preserved_after_" + evidenceType;
                    KnowledgePolicyProfileService.SaveProfile(preserved, oldProfile);
                }
                else
                {
                    var oldProfile = KnowledgePolicyProfileService.GetProfile(preserved);
                    oldProfile.DoNotApplyWhen = MergeCondition(oldProfile.DoNotApplyWhen, scopeToken);
                    oldProfile.AnswerMode = KnowledgeAnswerModes.Contextual;
                    oldProfile.LastEvidenceType = "scenario_excluded_after_" + evidenceType;
                    KnowledgePolicyProfileService.SaveProfile(preserved, oldProfile);
                }
            }

            // Scenario scope is intentionally one atomic condition. The legacy policy parser uses OR
            // across rows, so merging several phase/product/SKU rows would re-introduce partial-match
            // ambiguity. RequiredContext is replaced by the exact scenario token for this correction.
            var currentProfile = KnowledgePolicyProfileService.GetProfile(current);
            currentProfile.ApplyWhen = scopeToken;
            currentProfile.RequiredContext = scopeToken;
            currentProfile.AnswerMode = KnowledgeAnswerModes.Contextual;
            currentProfile.Confidence = Math.Max(currentProfile.Confidence, 0.94);
            currentProfile.LastEvidenceType = "commerce_" + evidenceType;
            KnowledgePolicyProfileService.SaveProfile(current, currentProfile);
            return true;
        }

        private static KnowledgePolicyProfile CloneProfile(KnowledgePolicyProfile source)
        {
            source = source ?? new KnowledgePolicyProfile();
            return new KnowledgePolicyProfile
            {
                Intent = source.Intent,
                Entities = source.Entities,
                ApplyWhen = source.ApplyWhen,
                DoNotApplyWhen = source.DoNotApplyWhen,
                RequiredContext = source.RequiredContext,
                AnswerMode = source.AnswerMode,
                Confidence = source.Confidence,
                DirectSelectedCount = source.DirectSelectedCount,
                ContextualSelectedCount = source.ContextualSelectedCount,
                AcceptedCount = source.AcceptedCount,
                SellerCorrectionCount = source.SellerCorrectionCount,
                SellerWithdrawCount = source.SellerWithdrawCount,
                LastEvidenceType = source.LastEvidenceType,
                UpdatedAt = source.UpdatedAt
            };
        }

        private static string MergeCondition(string existing, string condition)
        {
            var values = (existing ?? string.Empty)
                .Split(new[] { '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (!values.Any(x => string.Equals(x, condition, StringComparison.OrdinalIgnoreCase)))
                values.Add(condition);
            return string.Join("；", values);
        }

        private static bool IsReusableEvidence(string evidence)
        {
            return evidence == "manual_reply"
                || evidence == "manual_correction"
                || evidence == "withdrawn_bot_then_manual"
                || evidence == "repeated_human_pattern"
                || evidence == "conversation_synthesis";
        }

        private static bool ReadBool(JToken token)
        {
            if (token == null) return false;
            bool value;
            return bool.TryParse(Convert.ToString(token), out value) && value;
        }

        private static string NormalizeAnswer(string value)
        {
            return (value ?? string.Empty)
                .Replace("[AI]", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToLowerInvariant();
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length > max) value = value.Substring(0, max).Trim();
            return value;
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _commerceKnowledgeLearningBootstrap =
            ChromeNs.CommerceKnowledgeLearningBridge.InitializeForApp();
    }
}
