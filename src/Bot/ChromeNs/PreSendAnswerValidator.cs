using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal enum AnswerValidationAction
    {
        Pass,
        Regenerate,
        Manual
    }

    internal sealed class AnswerValidationResult
    {
        public AnswerValidationAction Action { get; set; }
        public List<string> Issues { get; set; }
        public string Reason { get; set; }
        public string RegenerationInstruction { get; set; }

        public AnswerValidationResult()
        {
            Action = AnswerValidationAction.Pass;
            Issues = new List<string>();
            Reason = "通过";
            RegenerationInstruction = string.Empty;
        }
    }

    internal static class PreSendAnswerValidator
    {
        private static readonly string[] MachinePhrases =
        {
            "作为ai", "作为一个ai", "作为人工智能", "我是ai", "我是一个ai", "根据我的训练",
            "语言模型", "系统提示词", "知识库显示", "候选知识", "上下文问题还原", "智能路由"
        };

        private static readonly string[] AbsolutePromisePhrases =
        {
            "百分百", "100%", "一定可以", "一定能", "保证可以", "保证能", "绝对可以", "绝对没问题",
            "无条件退款", "永久有效", "终身有效", "包过", "肯定到账", "立即到账"
        };

        private static readonly string[] ConcreteStatusClaims =
        {
            "已退款", "已经退款", "退款成功", "已发货", "已经发货", "已到账", "已经到账",
            "订单已完成", "订单完成了", "审核通过", "核实通过", "已经处理完成", "已处理完成",
            "已经补偿", "已赔偿", "已经赔偿", "已经解封", "已解封"
        };

        private static readonly string[] HighRiskTerms =
        {
            "退款", "退货", "赔偿", "补偿", "投诉", "差评", "举报", "仲裁", "封号", "解封",
            "验证码", "密码", "身份证", "银行卡", "账号安全", "订单状态"
        };

        private static readonly Regex ConcreteNumberRegex = new Regex(
            @"(?<!\d)\d+(?:\.\d+)?\s*(?:元|块|天|小时|分钟|秒|次|台|个|件|年|月|个月|工作日|%|折)(?![\dA-Za-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static AnswerValidationResult Validate(
            string seller,
            string buyer,
            string question,
            string answer,
            KnowledgeBaseEntry knowledge,
            bool secondAttempt)
        {
            var result = new AnswerValidationResult();
            answer = BotOutboundMessageFormatter.StripAiMarker(answer);
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(answer))
            {
                return Manual(result, "答案为空");
            }
            if (answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                return Manual(result, "答案本身是错误状态");
            }

            var compactAnswer = Compact(answer);
            var state = BuildState(seller, buyer, question);
            var authoritativeEvidence = BuildAuthoritativeEvidence(knowledge, state);

            if (MachinePhrases.Any(x => compactAnswer.Contains(Compact(x))))
            {
                result.Issues.Add("包含AI、系统提示词或内部路由等机器化措辞");
            }

            if (answer.Length > 700)
            {
                result.Issues.Add("答案过长，不符合即时客服回复习惯");
            }

            if (IsTooGeneric(question, answer))
            {
                result.Issues.Add("回复过于笼统，没有实际回答买家当前问题");
            }

            AddIntentCoverageIssue(question, answer, state, result.Issues);
            var recentTurns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 16);
            ConversationProgressGuardService.AddValidationIssues(
                question, answer, state, recentTurns, result.Issues);

            var unsupportedNumbers = ConcreteNumberRegex.Matches(answer)
                .Cast<Match>()
                .Select(x => x.Value.Trim())
                .Where(x => !ContainsEvidence(authoritativeEvidence, x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            if (unsupportedNumbers.Count > 0)
            {
                result.Issues.Add("出现店铺提示词、知识或已确认订单事实中没有依据的具体数字/时效：" + string.Join("、", unsupportedNumbers));
            }

            var unsupportedAbsolute = AbsolutePromisePhrases
                .Where(x => compactAnswer.Contains(Compact(x)) && !ContainsEvidence(authoritativeEvidence, x))
                .Take(4)
                .ToList();
            if (unsupportedAbsolute.Count > 0)
            {
                result.Issues.Add("出现没有事实依据的绝对承诺：" + string.Join("、", unsupportedAbsolute));
            }

            var unsupportedStatus = ConcreteStatusClaims
                .Where(x => compactAnswer.Contains(Compact(x)) && !ContainsEvidence(authoritativeEvidence, x))
                .Take(4)
                .ToList();
            if (unsupportedStatus.Count > 0)
            {
                result.Issues.Add("声称已经核实或完成具体订单/售后状态，但没有可靠依据：" + string.Join("、", unsupportedStatus));
            }

            if (HasKnowledgeContradiction(answer, knowledge))
            {
                result.Issues.Add("生成答案与高可信知识或事实约束的支持/不支持结论相反");
            }

            if (result.Issues.Count == 0)
            {
                Log.Info("发送前答案校验通过: seller=" + Safe(seller, 60)
                    + ", buyer=" + Safe(buyer, 60));
                return result;
            }

            var highRisk = unsupportedAbsolute.Count > 0
                || unsupportedStatus.Count > 0
                || HasHighRiskUnsupportedClaim(answer, authoritativeEvidence)
                || HasKnowledgeContradiction(answer, knowledge);
            if (highRisk || secondAttempt)
            {
                result.Action = AnswerValidationAction.Manual;
                result.Reason = string.Join("；", result.Issues);
                Log.Info("发送前答案校验阻止自动发送: seller=" + Safe(seller, 60)
                    + ", buyer=" + Safe(buyer, 60)
                    + ", reason=" + Safe(result.Reason, 500));
                return result;
            }

            result.Action = AnswerValidationAction.Regenerate;
            result.Reason = string.Join("；", result.Issues);
            result.RegenerationInstruction = BuildRegenerationInstruction(result.Issues);
            Log.Info("发送前答案校验要求重答: seller=" + Safe(seller, 60)
                + ", buyer=" + Safe(buyer, 60)
                + ", reason=" + Safe(result.Reason, 500));
            return result;
        }

        public static string BuildEvidenceText(KnowledgeBaseEntry knowledge)
        {
            var sb = new StringBuilder();
            var store = StorePromptProfileService.GetStandardPrompt();
            if (!string.IsNullOrWhiteSpace(store))
            {
                sb.Append("【店铺固定事实】\n").Append(Safe(store, 5000)).Append("\n");
            }
            if (knowledge != null)
            {
                sb.Append("【最相关知识】\n问题：")
                    .Append(Safe(knowledge.Title, 500))
                    .Append("\n答案：")
                    .Append(Safe(knowledge.Answer, 1600))
                    .Append("\n");
                var policy = KnowledgePolicyProfileService.GetProfile(knowledge);
                if (policy != null)
                {
                    if (!string.IsNullOrWhiteSpace(policy.ApplyWhen))
                        sb.Append("适用条件：").Append(Safe(policy.ApplyWhen, 800)).Append("\n");
                    if (!string.IsNullOrWhiteSpace(policy.DoNotApplyWhen))
                        sb.Append("禁止适用条件：").Append(Safe(policy.DoNotApplyWhen, 800)).Append("\n");
                    if (!string.IsNullOrWhiteSpace(policy.RequiredContext))
                        sb.Append("必要上下文：").Append(Safe(policy.RequiredContext, 800)).Append("\n");
                    sb.Append("回答模式：").Append(KnowledgeAnswerModes.Display(policy.AnswerMode)).Append("\n");
                }
            }
            return sb.ToString().Trim();
        }

        private static string BuildAuthoritativeEvidence(
            KnowledgeBaseEntry knowledge,
            ConversationStateSnapshot state)
        {
            var sb = new StringBuilder(BuildEvidenceText(knowledge));
            var commerce = state == null ? null : state.CommerceContext;
            if (commerce != null && commerce.HasStructuredOrder)
            {
                // Only verified order snapshots become validator evidence. Conversation-only phase
                // guesses remain prompt context and can never authorize a concrete order claim.
                sb.Append("\n【已确认订单事实】\n")
                    .Append("购买阶段：").Append(commerce.PurchasePhaseDisplay).Append("\n");
                if (!string.IsNullOrWhiteSpace(commerce.TradeStatus))
                    sb.Append("订单状态：").Append(Safe(commerce.TradeStatus, 160)).Append("\n");
                if (commerce.IsPaid.HasValue)
                    sb.Append("付款状态：").Append(commerce.IsPaid.Value ? "已付款" : "未付款").Append("\n");
                if (!string.IsNullOrWhiteSpace(commerce.ItemTitle))
                    sb.Append("商品：").Append(Safe(commerce.ItemTitle, 220)).Append("\n");
                if (!string.IsNullOrWhiteSpace(commerce.SkuText))
                    sb.Append("SKU：").Append(Safe(commerce.SkuText, 180)).Append("\n");
                if (!string.Equals(commerce.FulfillmentType, "unknown", StringComparison.OrdinalIgnoreCase))
                    sb.Append("履约类型：").Append(commerce.FulfillmentTypeDisplay).Append("\n");
            }
            return Compact(sb.ToString());
        }

        private static ConversationStateSnapshot BuildState(string seller, string buyer, string question)
        {
            try
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 12);
                return ConversationStateService.Build(seller, buyer, question, turns);
            }
            catch
            {
                return new ConversationStateSnapshot();
            }
        }

        private static void AddIntentCoverageIssue(
            string question,
            string answer,
            ConversationStateSnapshot state,
            List<string> issues)
        {
            var intent = state == null || string.IsNullOrWhiteSpace(state.BuyerGoal)
                ? ConversationStateService.DetectIntent(question)
                : state.BuyerGoal;
            var compact = Compact(answer);
            if (intent == "询问价格"
                && !ContainsAny(compact, "价格", "元", "块", "页面", "链接", "无法确认", "不能确认", "请问哪款", "具体哪款"))
            {
                issues.Add("买家询问价格，但回复没有给出价格依据，也没有说明需要确认具体商品/页面");
            }
            else if (intent == "询问时间/时效"
                && !ContainsAny(compact, "时间", "时效", "天", "小时", "分钟", "尽快", "页面为准", "无法确认", "不能确认", "人工确认"))
            {
                issues.Add("买家询问时间或时效，但回复没有回答时效，也没有说明无法确认");
            }
            else if (intent == "确认是否支持"
                && !ContainsAny(compact, "支持", "不支持", "可以", "不可以", "能", "不能", "需要", "取决于", "确认"))
            {
                issues.Add("买家在确认是否支持，但回复没有给出明确支持/不支持或条件性结论");
            }
            else if (intent == "询问操作方法"
                && !ContainsAny(compact, "请", "先", "打开", "进入", "点击", "选择", "输入", "提供", "发送", "联系", "操作", "步骤"))
            {
                issues.Add("买家询问操作方法，但回复没有提供可执行步骤或下一步");
            }
            else if (intent == "故障排查"
                && !ContainsAny(compact, "请", "先", "检查", "确认", "尝试", "重新", "如果", "可能", "原因", "人工", "核查"))
            {
                issues.Add("买家在排查故障，但回复没有提供排查方向、原因或下一步");
            }
        }

        private static bool IsTooGeneric(string question, string answer)
        {
            var q = Compact(question);
            var a = Compact(answer);
            if (q.Length <= 3) return false;
            if (a.Length <= 3) return true;
            return a == "好的"
                || a == "可以的"
                || a == "不可以"
                || a == "不知道"
                || a == "不清楚"
                || a == "请稍等"
                || a == "稍等一下";
        }

        private static bool HasKnowledgeContradiction(string answer, KnowledgeBaseEntry knowledge)
        {
            if (knowledge == null || string.IsNullOrWhiteSpace(knowledge.Answer)) return false;
            var policy = KnowledgePolicyProfileService.GetProfile(knowledge);
            var trusted = policy != null
                && (KnowledgeAnswerModes.Normalize(policy.AnswerMode) == KnowledgeAnswerModes.Constraint
                    || policy.Confidence >= 0.80
                    || policy.ReliabilityScore >= 0.72);
            if (!trusted) return false;
            var expected = DetectPolarity(knowledge.Answer);
            var actual = DetectPolarity(answer);
            return expected != 0 && actual != 0 && expected != actual;
        }

        private static int DetectPolarity(string value)
        {
            var compact = Compact(value);
            var negative = CountAny(compact, "不支持", "不能", "不可以", "无法", "不通用", "不可用", "不行", "没有");
            var positive = CountAny(compact, "支持", "可以", "能用", "能够", "可用", "通用", "没问题");
            if (negative > positive) return -1;
            if (positive > negative) return 1;
            return 0;
        }

        private static bool HasHighRiskUnsupportedClaim(string answer, string authoritativeEvidence)
        {
            var compactAnswer = Compact(answer);
            foreach (var term in HighRiskTerms)
            {
                var compactTerm = Compact(term);
                if (!compactAnswer.Contains(compactTerm)) continue;
                if (authoritativeEvidence.Contains(compactTerm)) continue;
                if (Regex.IsMatch(compactAnswer,
                    @"(可以|会|将|已经|已|一定|保证|同意|为您|给您).{0,8}" + Regex.Escape(compactTerm)
                    + @"|" + Regex.Escape(compactTerm) + @".{0,8}(成功|完成|到账|通过|可以)"))
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildRegenerationInstruction(IEnumerable<string> issues)
        {
            return "上一版答案未通过发送前校验，必须重新生成。问题如下："
                + string.Join("；", issues ?? Enumerable.Empty<string>())
                + "。只输出修正后的买家回复，不要解释校验过程，不要提AI、知识库、系统提示词或内部规则。"
                + "严格使用店铺固定提示词、给出的知识和已确认订单事实作为事实边界；没有依据的价格、数字、时效、订单状态、退款、赔偿或售后承诺一律不要猜。"
                + "直接回答买家当前问题，保持简短自然；无法确认时明确说明需要买家补充信息或转人工核查。";
        }

        private static AnswerValidationResult Manual(AnswerValidationResult result, string issue)
        {
            result.Action = AnswerValidationAction.Manual;
            result.Issues.Add(issue);
            result.Reason = issue;
            return result;
        }

        private static bool ContainsEvidence(string compactEvidence, string value)
        {
            var compactValue = Compact(value);
            return compactValue.Length > 0 && compactEvidence.Contains(compactValue);
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            return terms.Any(x => value.IndexOf(Compact(x), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int CountAny(string value, params string[] terms)
        {
            return terms.Count(x => value.IndexOf(Compact(x), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?i)(验证码|校验码)[：:\s]*\d{4,8}", "$1：[已脱敏]");
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
