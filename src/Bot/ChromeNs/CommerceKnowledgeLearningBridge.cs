using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, byte> Processed =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly DateTime StartedAt = DateTime.Now.AddSeconds(-5);
        private static int _initialized;
        private static int _processing;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                ConversationSessionLearningService.ReportsChanged += OnReportsChanged;
                Log.Info("Commerce知识学习桥已启动：人工纠错将按订单阶段/履约/商品场景分流，旧答案不再仅因问题文本相同而丢失。" );
            }
            return new object();
        }

        private static void OnReportsChanged()
        {
            if (Interlocked.Exchange(ref _processing, 1) != 0) return;
            Task.Run(() =>
            {
                try { ProcessLatest(); }
                catch (Exception ex) { Log.ErrorWithMaxCount("Commerce知识学习桥处理失败: " + ex.Message, 20); }
                finally { Interlocked.Exchange(ref _processing, 0); }
            });
        }

        private static void ProcessLatest()
        {
            var reports = ConversationSessionLearningService.GetReports(40)
                .Where(x => x != null
                    && string.Equals(x.Status, "学习完成", StringComparison.Ordinal)
                    && x.CompletedAt != DateTime.MinValue
                    && x.CompletedAt >= StartedAt)
                .OrderBy(x => x.CompletedAt)
                .ToList();
            foreach (var report in reports)
            {
                if (string.IsNullOrWhiteSpace(report.Id) || !Processed.TryAdd(report.Id, 0)) continue;
                try { ProcessReport(report); }
                catch (Exception ex)
                {
                    byte ignored;
                    Processed.TryRemove(report.Id, out ignored);
                    Log.ErrorWithMaxCount("Commerce场景知识治理失败: report=" + report.Id + ", error=" + ex.Message, 20);
                }
            }
        }

        private static void ProcessReport(ConversationSessionLearningReportView report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.Seller)
                || string.IsNullOrWhiteSpace(report.Buyer) || string.IsNullOrWhiteSpace(report.SuggestionsJson)) return;

            var shop = ShopContextLocator.ResolveBySellerNick(report.Seller);
            if (shop == null) return;

            using (ShopSettingsScope.Enter(shop))
            {
                var turns = ConversationContextStore.GetRecentTurns(report.Seller, report.Buyer, string.Empty, 24);
                var state = ConversationStateService.Build(report.Seller, report.Buyer, string.Empty, turns);
                var commerce = state.CommerceContext
                    ?? CommerceContextService.Build(report.Seller, report.Buyer, string.Empty, turns, state);
                var scopeToken = CommerceContextService.BuildPolicyScopeToken(commerce);
                if (string.IsNullOrWhiteSpace(scopeToken)) return;

                JArray suggestions;
                try { suggestions = JArray.Parse(report.SuggestionsJson); }
                catch { return; }

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
