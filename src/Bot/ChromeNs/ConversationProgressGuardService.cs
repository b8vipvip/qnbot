using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class ConversationProgressSnapshot
    {
        public bool HasOrderEvidence { get; set; }
        public bool HasBuyerImage { get; set; }
        public bool HasDeviceAccountEvidence { get; set; }
        public bool DeviceAccountConfirmed { get; set; }
        public bool HasPhoneNumber { get; set; }
        public bool HasVerificationCode { get; set; }
        public bool BuyerRefusedExchangeLink { get; set; }
        public bool ManualRechargeOffered { get; set; }
        public bool RechargeSubmitted { get; set; }
        public bool ExplicitCompatibilityQuestion { get; set; }
        public bool AsksScreenshotTarget { get; set; }
        public bool AsksPurchaseTarget { get; set; }
        public bool NormalAccountInquiry { get; set; }
        public string CurrentInputKind { get; set; }
        public string Stage { get; set; }
        public string NextAction { get; set; }

        public ConversationProgressSnapshot()
        {
            CurrentInputKind = "text";
            Stage = "售前咨询";
            NextAction = string.Empty;
        }
    }

    /// <summary>
    /// Keeps a buyer conversation moving forward. Business phrases and reply constraints are loaded
    /// from client JSON; this class only contains the generic state machine and privacy-safe booleans.
    /// </summary>
    internal static class ConversationProgressGuardService
    {
        private static Regex R(string key) { return BusinessPolicyProfileService.GetRegex("patterns." + key); }
        private static string T(string key) { return BusinessPolicyProfileService.GetString(key); }

        public static ConversationProgressSnapshot Analyze(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            var ordered = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();
            var buyerTexts = ordered.Where(x => x.Role == "user").Select(x => x.Text ?? string.Empty).ToList();
            var sellerTexts = ordered.Where(x => x.Role == "assistant").Select(x => x.Text ?? string.Empty).ToList();
            var buyerText = string.Join("\n", buyerTexts.Concat(new[] { currentQuestion ?? string.Empty }));
            var sellerText = string.Join("\n", sellerTexts);
            var allText = buyerText + "\n" + sellerText;
            var current = (currentQuestion ?? string.Empty).Trim();

            var latestCodeRequest = ordered
                .Where(x => x.Role == "assistant" && R("codeRequest").IsMatch(x.Text ?? string.Empty))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            var latestPhoneRequest = ordered
                .Where(x => x.Role == "assistant" && R("phoneRequest").IsMatch(x.Text ?? string.Empty))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            var visual = string.Empty;
            try { visual = RecentVisualContextService.BuildPromptAddon(seller, buyer, currentQuestion); }
            catch { visual = string.Empty; }

            // “拍”必须按宾语消歧：页面/界面优先表示拍照，链接/商品/SKU表示下单。
            // 两个正则均由客户端 JSON 管理；页面语义优先，避免“拍哪个页面”被当成购买。
            var asksScreenshotTarget = R("screenshotTargetQuestion").IsMatch(current);
            var asksPurchaseTarget = !asksScreenshotTarget && R("purchaseTargetQuestion").IsMatch(current);

            var progress = new ConversationProgressSnapshot
            {
                HasOrderEvidence = R("orderEvidence").IsMatch(allText),
                HasBuyerImage = R("image").IsMatch(buyerText),
                HasPhoneNumber = R("phone").IsMatch(buyerText),
                BuyerRefusedExchangeLink = R("linkRefusal").IsMatch(buyerText),
                ManualRechargeOffered = R("manualRecharge").IsMatch(sellerText),
                RechargeSubmitted = R("rechargeSubmitted").IsMatch(sellerText),
                ExplicitCompatibilityQuestion = R("compatibilityQuestion").IsMatch(current),
                AsksScreenshotTarget = asksScreenshotTarget,
                AsksPurchaseTarget = asksPurchaseTarget,
                NormalAccountInquiry = R("normalAccountInquiry").IsMatch(current)
            };

            progress.HasDeviceAccountEvidence =
                (progress.HasBuyerImage && R("deviceEvidence").IsMatch(allText))
                || R("deviceEvidence").IsMatch(visual);
            progress.DeviceAccountConfirmed = progress.HasDeviceAccountEvidence
                && (R("deviceConfirmation").IsMatch(sellerText) || R("visualDeviceEvidence").IsMatch(visual));

            var currentIsPhone = R("phone").IsMatch(current);
            var currentIsCode = R("verificationCode").IsMatch(current) && latestCodeRequest != null;
            progress.HasVerificationCode = currentIsCode
                || HasStructuredReplyAfter(ordered, latestCodeRequest, R("verificationCode"));

            if (currentIsPhone) progress.CurrentInputKind = "phone_number";
            else if (currentIsCode) progress.CurrentInputKind = "verification_code";
            else if (R("image").IsMatch(current)) progress.CurrentInputKind = "image";
            else if (R("linkRefusal").IsMatch(current)) progress.CurrentInputKind = "exchange_link_refusal";
            else if (progress.AsksScreenshotTarget) progress.CurrentInputKind = "screenshot_target_question";
            else if (progress.AsksPurchaseTarget) progress.CurrentInputKind = "purchase_target_question";
            else if (progress.NormalAccountInquiry) progress.CurrentInputKind = "normal_account_inquiry";

            if (progress.RechargeSubmitted) ApplyStage(progress, "rechargeSubmitted");
            else if (progress.HasVerificationCode) ApplyStage(progress, "verificationReceived");
            else if (progress.HasPhoneNumber
                && (progress.ManualRechargeOffered || progress.BuyerRefusedExchangeLink || latestPhoneRequest != null))
                ApplyStage(progress, "phoneReceived");
            else if (progress.ManualRechargeOffered || progress.BuyerRefusedExchangeLink)
                ApplyStage(progress, "awaitPhone");
            else if (progress.AsksScreenshotTarget)
                ApplyStage(progress, "screenshotTarget");
            else if (progress.AsksPurchaseTarget)
                ApplyStage(progress, "purchaseTarget");
            else if (progress.HasOrderEvidence && progress.DeviceAccountConfirmed)
                ApplyStage(progress, "deviceConfirmed");
            else if (progress.HasOrderEvidence)
                ApplyStage(progress, "awaitDevice");
            else if (progress.ExplicitCompatibilityQuestion)
                ApplyStage(progress, "preSaleCompatibility");
            else
                ApplyStage(progress, "preSaleGeneral");

            return progress;
        }

        public static void EnrichState(
            ConversationStateSnapshot state,
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            if (state == null) return;
            var progress = Analyze(seller, buyer, currentQuestion, turns);
            state.Progress = progress;
            state.ConversationStage = progress.Stage;

            AddFact(state, progress.HasOrderEvidence, T("facts.order"));
            AddFact(state, progress.HasDeviceAccountEvidence, T("facts.deviceEvidence"));
            AddFact(state, progress.DeviceAccountConfirmed, T("facts.deviceConfirmed"));
            AddFact(state, progress.HasPhoneNumber, T("facts.phone"));
            AddFact(state, progress.HasVerificationCode, T("facts.code"));
            AddFact(state, progress.BuyerRefusedExchangeLink, T("facts.linkRefusal"));
            AddFact(state, progress.ManualRechargeOffered, T("facts.manualRecharge"));
            AddFact(state, progress.RechargeSubmitted, T("facts.submitted"));
            AddFact(state, progress.AsksScreenshotTarget, T("facts.screenshotTarget"));
            AddFact(state, progress.AsksPurchaseTarget, T("facts.purchaseTarget"));

            if (progress.CurrentInputKind == "phone_number") state.BuyerGoal = T("buyerGoals.phone");
            else if (progress.CurrentInputKind == "verification_code") state.BuyerGoal = T("buyerGoals.code");
            else if (progress.CurrentInputKind == "exchange_link_refusal") state.BuyerGoal = T("buyerGoals.linkRefusal");
            else if (progress.CurrentInputKind == "screenshot_target_question") state.BuyerGoal = T("buyerGoals.screenshotTarget");
            else if (progress.CurrentInputKind == "purchase_target_question") state.BuyerGoal = T("buyerGoals.purchaseTarget");
            else if (progress.CurrentInputKind == "normal_account_inquiry") state.BuyerGoal = T("buyerGoals.normalAccount");
        }

        public static string BuildPromptAddon(ConversationStateSnapshot state)
        {
            var progress = state == null ? null : state.Progress;
            if (progress == null) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n【会话流程进度｜最高优先级】\n")
                .Append("阶段：").Append(progress.Stage).Append("\n")
                .Append("下一步：").Append(progress.NextAction).Append("\n")
                .Append(T("prompts.header")).Append("\n");
            AppendIf(sb, progress.HasOrderEvidence, "prompts.order");
            AppendIf(sb, progress.DeviceAccountConfirmed, "prompts.deviceConfirmed");
            AppendIf(sb, progress.HasPhoneNumber, "prompts.phone");
            AppendIf(sb, progress.HasVerificationCode, "prompts.code");
            AppendIf(sb, progress.BuyerRefusedExchangeLink, "prompts.linkRefusal");
            AppendIf(
                sb,
                !progress.HasOrderEvidence
                    && !progress.ExplicitCompatibilityQuestion
                    && !progress.AsksScreenshotTarget
                    && !progress.AsksPurchaseTarget,
                "prompts.preSaleGeneral");
            AppendIf(sb, progress.CurrentInputKind == "phone_number", "prompts.phoneInput");
            AppendIf(sb, progress.CurrentInputKind == "verification_code", "prompts.codeInput");
            AppendIf(sb, progress.AsksScreenshotTarget, "prompts.screenshotTarget");
            AppendIf(sb, progress.AsksPurchaseTarget, "prompts.purchaseTarget");
            AppendIf(sb, progress.NormalAccountInquiry, "prompts.normalAccount");
            return sb.ToString();
        }

        public static bool RequiresContextualHandling(ConversationStateSnapshot state)
        {
            if (state == null) return false;
            var commerce = state.CommerceContext;
            if (commerce != null
                && (commerce.HasStructuredOrder
                    || string.Equals(commerce.PurchasePhase, "post_order_unverified", StringComparison.OrdinalIgnoreCase)))
            {
                // Once an order is known (or strongly suspected), a fixed FAQ answer is no longer
                // a safe terminal action. The same buyer utterance can require a different answer by
                // payment/fulfillment/SKU state, so the single contextual generation path owns it.
                return true;
            }

            var p = state.Progress;
            if (p == null) return false;
            return p.CurrentInputKind == "phone_number"
                || p.CurrentInputKind == "verification_code"
                || p.CurrentInputKind == "image"
                || p.CurrentInputKind == "exchange_link_refusal"
                || p.CurrentInputKind == "screenshot_target_question"
                || p.CurrentInputKind == "purchase_target_question"
                || p.CurrentInputKind == "normal_account_inquiry"
                || p.ManualRechargeOffered
                || p.RechargeSubmitted;
        }

        public static bool AllowKnowledge(KnowledgeBaseEntry entry, ConversationStateSnapshot state, string currentQuestion)
        {
            if (entry == null) return false;

            // Human-corrected commerce variants carry one atomic scope token. Unlike ordinary
            // descriptive ApplyWhen conditions this is an identity boundary: partial matching is
            // unsafe because the same question may have opposite answers before/after payment or
            // across SKUs. A mismatched scope never enters retrieval/prompt candidates.
            var policy = KnowledgePolicyProfileService.GetProfile(entry);
            var requiredCommerceScope = ReadAtomicCommerceScope(policy == null ? string.Empty : policy.RequiredContext);
            if (!string.IsNullOrWhiteSpace(requiredCommerceScope))
            {
                var currentScope = CommerceContextService.BuildPolicyScopeToken(
                    state == null ? null : state.CommerceContext);
                if (!string.Equals(requiredCommerceScope, currentScope, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var p = state == null ? null : state.Progress;
            if (p == null) return true;
            var title = entry.Title ?? string.Empty;
            var answer = entry.Answer ?? string.Empty;
            var combined = title + " " + (entry.Keywords ?? string.Empty) + " " + answer;

            if (p.AsksScreenshotTarget)
            {
                if (R("orderInstruction").IsMatch(combined)) return false;
                if (!R("screenshotTargetKnowledge").IsMatch(combined)) return false;
            }

            if (p.AsksPurchaseTarget)
            {
                if (!R("purchaseTargetKnowledge").IsMatch(combined)) return false;
                if (R("badPurchaseTargetKnowledge").IsMatch(combined)
                    && !R("purchaseTargetKnowledgeStrong").IsMatch(combined)) return false;
            }

            if (p.CurrentInputKind == "phone_number" || p.CurrentInputKind == "verification_code")
            {
                if (RequestsScreenshot(answer) || R("preRechargeKnowledge").IsMatch(combined)) return false;
                if (R("orderInstruction").IsMatch(combined)) return false;
            }
            if (p.DeviceAccountConfirmed && RequestsScreenshot(answer)) return false;
            if (p.HasOrderEvidence
                && R("orderInstruction").IsMatch(combined)
                && !R("explicitOrderQuestion").IsMatch(currentQuestion ?? string.Empty)) return false;
            if (!p.HasOrderEvidence
                && !p.ExplicitCompatibilityQuestion
                && !p.AsksScreenshotTarget
                && !p.AsksPurchaseTarget
                && RequestsScreenshot(answer)) return false;
            return true;
        }

        public static void AddValidationIssues(
            string question,
            string answer,
            ConversationStateSnapshot state,
            IList<ConversationContextTurn> turns,
            IList<string> issues)
        {
            if (issues == null || state == null || state.Progress == null) return;
            var p = state.Progress;
            answer = answer ?? string.Empty;

            if (p.DeviceAccountConfirmed && RequestsScreenshot(answer)) AddIssue(issues, T("validationIssues.repeatScreenshot"));
            if (p.HasOrderEvidence && R("orderInstruction").IsMatch(answer)) AddIssue(issues, T("validationIssues.repeatOrder"));
            if (p.HasPhoneNumber && R("phoneRequest").IsMatch(answer)) AddIssue(issues, T("validationIssues.repeatPhone"));
            if (p.HasVerificationCode && R("codeRequest").IsMatch(answer)) AddIssue(issues, T("validationIssues.repeatCode"));
            if (p.BuyerRefusedExchangeLink && R("linkInstruction").IsMatch(answer)) AddIssue(issues, T("validationIssues.linkRefusal"));
            if (!p.HasOrderEvidence
                && !p.ExplicitCompatibilityQuestion
                && !p.AsksScreenshotTarget
                && !p.AsksPurchaseTarget
                && RequestsScreenshot(answer))
                AddIssue(issues, T("validationIssues.preSaleScreenshot"));
            if ((p.CurrentInputKind == "phone_number" || p.CurrentInputKind == "verification_code") && RequestsScreenshot(answer))
                AddIssue(issues, T("validationIssues.flowRegression"));
            if (p.AsksScreenshotTarget && (R("orderInstruction").IsMatch(answer) || R("badScreenshotTargetAnswer").IsMatch(answer)))
                AddIssue(issues, T("validationIssues.screenshotTargetPurchase"));
            if (p.AsksPurchaseTarget && R("badPurchaseTargetAnswer").IsMatch(answer))
                AddIssue(issues, T("validationIssues.purchaseTargetScreenshot"));
            if (p.AsksPurchaseTarget && !R("purchaseTargetAnswer").IsMatch(answer))
                AddIssue(issues, T("validationIssues.purchaseTargetMissingSelection"));
        }

        internal static bool RequestsScreenshot(string answer)
        {
            return R("screenshotRequest").IsMatch(answer ?? string.Empty);
        }

        private static string ReadAtomicCommerceScope(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.StartsWith("commerce_scope[", StringComparison.OrdinalIgnoreCase)
                && value.EndsWith("]", StringComparison.Ordinal)
                ? value
                : string.Empty;
        }

        private static void ApplyStage(ConversationProgressSnapshot progress, string key)
        {
            var stage = T("stages." + key + ".stage");
            var next = T("stages." + key + ".nextAction");
            if (!string.IsNullOrWhiteSpace(stage)) progress.Stage = stage;
            progress.NextAction = next ?? string.Empty;
        }

        private static void AppendIf(StringBuilder sb, bool condition, string key)
        {
            if (!condition) return;
            var text = T(key);
            if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append("\n");
        }

        private static bool HasStructuredReplyAfter(IList<ConversationContextTurn> turns, ConversationContextTurn request, Regex valueRegex)
        {
            if (request == null || turns == null) return false;
            return turns.Any(x => x != null
                && x.Role == "user"
                && !x.Withdrawn
                && (request.Timestamp == DateTime.MinValue || x.Timestamp == DateTime.MinValue || x.Timestamp >= request.Timestamp)
                && valueRegex.IsMatch((x.Text ?? string.Empty).Trim()));
        }

        private static void AddFact(ConversationStateSnapshot state, bool condition, string fact)
        {
            if (!condition || state == null || string.IsNullOrWhiteSpace(fact)) return;
            if (state.ConfirmedFacts == null) state.ConfirmedFacts = new List<string>();
            if (!state.ConfirmedFacts.Contains(fact)) state.ConfirmedFacts.Add(fact);
        }

        private static void AddIssue(IList<string> issues, string issue)
        {
            if (issues == null || string.IsNullOrWhiteSpace(issue)) return;
            if (!issues.Contains(issue)) issues.Add(issue);
        }
    }
}