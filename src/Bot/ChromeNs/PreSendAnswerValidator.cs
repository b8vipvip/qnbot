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
            @"(?<![\d.,])[-−]?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?\s*(?:工作日|个月|小时|分钟|元|块|天|秒|次|台|个|件|年|月|%|折)(?![\dA-Za-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static AnswerValidationResult Validate(
            string seller,
            string buyer,
            string question,
            string answer,
            bool secondAttempt,
            string approvedV2ContextEvidence = null,
            KnowledgeV2Decision decision = null)
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
            var authoritativeEvidence = BuildAuthoritativeEvidence(state, approvedV2ContextEvidence);
            var verifiedStatusEvidence = BuildVerifiedStatusEvidence(state);
            var scopedQuestion = question;
            if (decision != null && decision.Query != null)
                scopedQuestion += " " + string.Join(" ", (decision.Query.Entities ?? new List<string>())
                    .Where(KnowledgeEngineV2Semantics.IsProductEntity)
                    .Select(id => "商品ID:" + id.Substring("product:".Length)));

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
                .Where(x => !ContainsNumericEvidence(authoritativeEvidence, x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            if (unsupportedNumbers.Count > 0)
            {
                result.Issues.Add("出现店铺提示词、知识或已确认订单事实中没有依据的具体数字/时效：" + string.Join("、", unsupportedNumbers));
            }

            if (HasUnverifiedScopedNumbers(scopedQuestion, answer, approvedV2ContextEvidence,
                StorePromptProfileService.GetStandardPrompt()))
                result.Issues.Add("数字事实与商品、价格/时效类型或适用条件不一致");

            var unsupportedAbsolute = AbsolutePromisePhrases
                .Where(x => compactAnswer.Contains(Compact(x)) && !ContainsEvidence(authoritativeEvidence, x))
                .Take(4)
                .ToList();
            if (unsupportedAbsolute.Count > 0)
            {
                result.Issues.Add("出现没有事实依据的绝对承诺：" + string.Join("、", unsupportedAbsolute));
            }

            var unsupportedStatus = ConcreteStatusClaims
                .Where(x => compactAnswer.Contains(Compact(x)) && !string.Equals(verifiedStatusEvidence, Compact(x), StringComparison.Ordinal))
                .Take(4)
                .ToList();
            if (unsupportedStatus.Count > 0)
            {
                result.Issues.Add("声称已经核实或完成具体订单/售后状态，但没有可靠依据：" + string.Join("、", unsupportedStatus));
            }

            if (HasUnverifiedProductConclusion(scopedQuestion, answer, approvedV2ContextEvidence))
                result.Issues.Add("商品支持/不支持结论缺少明确适用的Knowledge V2证据");

            if (HasKnowledgeContradiction(answer, approvedV2ContextEvidence))
            {
                result.Issues.Add("生成答案与高可信知识或事实约束的支持/不支持结论相反");
            }

            var unsupportedRisk = HasHighRiskUnsupportedClaim(answer, authoritativeEvidence);
            if (unsupportedRisk)
                result.Issues.Add("回复包含无事实依据的高风险业务承诺");
            var unauthorizedTransaction = HasTransactionalPromise(answer);
            if (unauthorizedTransaction)
                result.Issues.Add("回复承诺代买家执行订单或售后操作，但当前只是客服回答而非已授权的业务执行");

            if (result.Issues.Count == 0)
            {
                Log.Info("发送前答案校验通过: seller=" + Safe(seller, 60)
                    + ", buyer=" + Safe(buyer, 60));
                return result;
            }

            var highRisk = unsupportedAbsolute.Count > 0
                || unsupportedStatus.Count > 0
                || unsupportedRisk || unauthorizedTransaction
                || HasKnowledgeContradiction(answer, approvedV2ContextEvidence);
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

        public static string BuildEvidenceText(string approvedV2ContextEvidence)
        {
            var sb = new StringBuilder();
            var store = StorePromptProfileService.GetStandardPrompt();
            if (!string.IsNullOrWhiteSpace(store))
                sb.Append("【店铺固定事实】\n").Append(Safe(store, 5000)).Append("\n");
            if (!string.IsNullOrWhiteSpace(approvedV2ContextEvidence))
                sb.Append("【Knowledge V2已批准上下文证据】\n")
                    .Append(Safe(approvedV2ContextEvidence, 5000)).Append("\n");
            return sb.ToString().Trim();
        }

        private static string BuildAuthoritativeEvidence(
            ConversationStateSnapshot state,
            string approvedV2ContextEvidence)
        {
            // Prompt metadata (questions, aliases, confidence, conditions and product IDs)
            // is not an assertion of a price or promise. Keep only actual answer lines.
            var sb = new StringBuilder(StorePromptProfileService.GetStandardPrompt() ?? string.Empty);
            foreach (Match match in Regex.Matches(approvedV2ContextEvidence ?? string.Empty, @"(?m)^答案：([^\r\n]+)"))
                sb.Append("\n").Append(match.Groups[1].Value);
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
            return sb.ToString();
        }

        private static string BuildVerifiedStatusEvidence(ConversationStateSnapshot state)
        {
            var commerce = state == null ? null : state.CommerceContext;
            // Generic policy/FAQ text and buyer messages cannot prove a live order
            // action. Only the current structured order status can support a claim.
            if (commerce == null || !commerce.HasStructuredOrder) return string.Empty;
            return Compact(commerce.TradeStatus);
        }

        internal static bool ContainsNumericEvidence(string evidence, string claim)
        {
            var expected = ConcreteNumberRegex.Match(claim ?? string.Empty);
            if (!expected.Success || expected.Length != (claim ?? string.Empty).Length) return false;
            var token = NormalizeNumericClaim(expected.Value);
            return ConcreteNumberRegex.Matches(evidence ?? string.Empty).Cast<Match>()
                .Any(x => string.Equals(NormalizeNumericClaim(x.Value), token, StringComparison.OrdinalIgnoreCase)
                    && KnowledgeEngineV2Semantics.HasAffirmativeTerm(evidence, x.Value));
        }

        private static string NormalizeNumericClaim(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"[\s,]+", string.Empty).Replace('−', '-');
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

        private sealed class ApprovedFact
        {
            public List<string> Products;
            public string Question;
            public string Answer;
            public string Conditions;
        }

        private static List<ApprovedFact> ApprovedFacts(string evidence)
        {
            return Regex.Matches(evidence ?? string.Empty,
                @"(?ms)^V2知识\d+：[^\r\n]*\r?\n(?<body>.*?)(?=^V2知识\d+：|\z)")
                .Cast<Match>().Select(x =>
                {
                    var body = x.Groups["body"].Value;
                    Func<string, string> field = label => Regex.Match(body, @"(?m)^" + label + @"：([^\r\n]*)").Groups[1].Value;
                    return new ApprovedFact
                    {
                        Question = field("问题"),
                        Answer = field("答案"),
                        Conditions = field("适用条件") + " " + field("必要上下文"),
                        Products = field("商品ID").Split(new[] { '；', ';', '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(id => id.Trim()).Select(id => KnowledgeEngineV2Semantics.IsProductEntity(id) ? id : "product:" + id)
                            .Where(id => Regex.IsMatch(id, @"^product:\d{8,20}$", RegexOptions.IgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    };
                }).Where(x => !string.IsNullOrWhiteSpace(x.Answer)).ToList();
        }

        private static List<string> ExplicitProducts(string value)
        {
            return KnowledgeEngineV2Semantics.ExtractEntities(value).Where(KnowledgeEngineV2Semantics.IsProductEntity).ToList();
        }

        private static List<string> RequestedProducts(string question, string evidence)
        {
            var ids = ExplicitProducts(question);
            if (ids.Count > 0) return ids;
            // This line is constructed by the runtime from the parsed query,
            // including verified order/OCR and preserved working-memory IDs.
            var header = Regex.Match(evidence ?? string.Empty, @"(?m)^结构化解析：[^\r\n]*；entities=([^\r\n]*)").Groups[1].Value;
            return Regex.Matches(header, @"(?i)\bproduct:\d{8,20}\b").Cast<Match>()
                .Select(x => x.Value.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> FactClauses(string text)
        {
            return Regex.Split(text ?? string.Empty, @"[，。；;！？!?\r\n]+|(?<!\d),|,(?!\d)")
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static readonly string[] FactQualifiers =
        {
            "首单", "续费", "新用户", "老用户", "首次", "复购", "限时", "活动", "优惠券",
            "会员", "非会员", "工作日", "自然日", "节假日", "周末", "预售", "现货",
            "退款", "退货", "发货", "到账", "激活", "充值", "配送", "运费", "定金", "尾款"
        };

        private static string NumericKind(string clause, string token)
        {
            var normalized = NormalizeNumericClaim(token);
            if (Regex.IsMatch(normalized, @"(?:元|块)$"))
            {
                if (Regex.IsMatch(clause, @"退款|退回|赔偿|补偿")) return "refund";
                if (Regex.IsMatch(clause, @"优惠|减免|立减|折抵")) return "discount";
                if (clause.Contains("运费")) return "shipping_fee";
                if (clause.Contains("定金")) return "deposit";
                if (clause.Contains("尾款")) return "balance";
                return "price";
            }
            if (Regex.IsMatch(normalized, @"(?:工作日|天|小时|分钟|秒|个月|年|月)$"))
            {
                if (Regex.IsMatch(clause, @"退款|退回")) return "refund_time";
                if (Regex.IsMatch(clause, @"发货|配送|快递|物流")) return "shipping_time";
                if (Regex.IsMatch(clause, @"有效|期限|到期|保修")) return "validity";
                if (Regex.IsMatch(clause, @"到账|激活|开通|充值")) return "activation_time";
                return "duration";
            }
            return "quantity";
        }

        private static bool QualifiersCompatible(string claim, string evidence, string question)
        {
            // Reject transfer of a conditional number from one scenario to
            // another. A qualifier can be supplied by the buyer's current
            // question or the answer itself, but never invented from elsewhere.
            foreach (var term in FactQualifiers)
            {
                var inClaim = claim.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                var inEvidence = evidence.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                if (inClaim && !inEvidence) return false;
                if (inEvidence && !inClaim && !KnowledgeEngineV2Semantics.HasAffirmativeTerm(question, term))
                    return false;
            }
            return true;
        }

        internal static bool HasUnverifiedScopedNumbers(string question, string answer, string evidence, string storeFacts)
        {
            var requested = RequestedProducts(question, evidence);
            var mentioned = ExplicitProducts(answer);
            var approved = ApprovedFacts(evidence);
            foreach (var clause in FactClauses(answer))
            {
                var scope = ExplicitProducts(clause);
                if (scope.Count == 0) scope = requested.Count > 0 ? requested : mentioned;
                foreach (Match number in ConcreteNumberRegex.Matches(clause))
                {
                    // Questions/uncertainty are not new factual assertions,
                    // but the existing global number validator still applies.
                    var kind = NumericKind(clause, number.Value);
                    Func<string, string, bool> matches = (source, conditions) => FactClauses(source).Any(candidate =>
                        ContainsNumericEvidence(candidate, number.Value)
                        && NumericKind(candidate, number.Value) == kind
                        && QualifiersCompatible(clause, candidate + " " + conditions, question));
                    if (scope.Count > 0)
                    {
                        if (requested.Count > 0 && scope.Any(id => !requested.Contains(id, StringComparer.OrdinalIgnoreCase)))
                            return true;
                        if (!scope.All(id => approved.Any(fact => fact.Products.Contains(id, StringComparer.OrdinalIgnoreCase)
                            && matches(fact.Answer, fact.Conditions)))) return true;
                    }
                    else if (!approved.Any(fact => fact.Products.Count == 0 && matches(fact.Answer, fact.Conditions))
                        && !matches(storeFacts, string.Empty)) return true;
                }
            }
            return false;
        }

        internal static bool HasUnverifiedProductConclusion(string question, string answer, string evidence)
        {
            var requested = RequestedProducts(question, evidence);
            var mentioned = ExplicitProducts(answer);
            if (requested.Count == 0 && mentioned.Count == 0) return false;
            var blocks = ApprovedFacts(evidence);
            foreach (var clause in FactClauses(answer))
            {
                var directions = ProductAssertionPolarities(clause).Distinct().ToList();
                if (directions.Count == 0) continue;
                var scope = ExplicitProducts(clause);
                if (scope.Count == 0) scope = requested.Count > 0 ? requested : mentioned;
                var features = KnowledgeEngineV2Semantics.ExtractEntities(clause)
                    .Where(x => !KnowledgeEngineV2Semantics.IsProductEntity(x)).ToList();
                if (directions.Count != 1 || scope.Count == 0
                    || (requested.Count > 0 && scope.Any(id => !requested.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    || !scope.All(id => blocks.Any(block =>
                        block.Products.Contains(id, StringComparer.OrdinalIgnoreCase)
                        && FactClauses(block.Answer).Any(approved =>
                        {
                            var polarity = ProductAssertionPolarities(approved).Distinct().ToList();
                            return polarity.Count == 1 && polarity[0] == directions[0]
                                && features.All(feature => KnowledgeEngineV2Semantics.HasAffirmativeTerm(
                                    block.Question + " " + approved + " " + block.Conditions, feature))
                                && QualifiersCompatible(clause, approved + " " + block.Conditions, question);
                        }))))
                    return true;
            }
            return false;
        }

        private static IEnumerable<int> ProductAssertionPolarities(string answer)
        {
            // Split before removing punctuation: uncertainty about one product
            // must not authorize an unsupported statement about another.
            foreach (var raw in Regex.Split(answer ?? string.Empty, @"[，,。；;！？!?\r\n]+"))
            {
                var clause = Compact(raw);
                if (clause.Length == 0) continue;
                if (Regex.IsMatch(clause, @"^(请问|麻烦确认|需要确认|需要核实|请提供|请补充|暂不确定|目前无法确认|无法确认|不能确认|不确定是否|暂时无法确认)")
                    && !Regex.IsMatch(clause, @"但|不过|然而|并且|同时"))
                    continue;
                // Uncertainty which follows an explicitly named product.
                if (Regex.IsMatch(clause, @"(无法确认|不能确认|暂不确定|不确定是否).{0,12}(支持|兼容|能用|可用)")
                    && !Regex.IsMatch(clause, @"但|不过|然而|并且|同时"))
                    continue;
                foreach (Match m in Regex.Matches(clause,
                    @"(?<negative>不支持|不兼容|不能使用|不可以使用|不可以用|不能用|不可用|不通用|无法使用)|(?<positive>支持|兼容|能用|可用|通用|可以使用|能够使用|可以用)"))
                    yield return m.Groups["negative"].Success ? -1 : 1;
            }
        }

        internal static bool AllowsUnresolvedKnowledgeReply(KnowledgeV2Decision decision, string answer)
        {
            if (decision == null || (!decision.HasConflict
                && (decision.Query == null || !decision.Query.ProductEvidenceConflict)))
                return true;
            // Unresolved evidence only authorizes a clarification/handoff, not
            // a new business conclusion. Never convert conflicting facts to a yes/no.
            var text = Compact(answer);
            return !ProductAssertionPolarities(answer).Any()
                && !ConcreteNumberRegex.IsMatch(answer ?? string.Empty)
                && !ConcreteStatusClaims.Any(x => text.Contains(Compact(x)))
                && !HasTransactionalPromise(answer)
                && Regex.IsMatch(text, @"请问|请提供|请补充|需要确认|需要核实|无法确认|不能确认|人工|进一步核查|具体哪款");
        }

        internal static bool HasTransactionalPromise(string answer)
        {
            // Static shop FAQ or an OCR screenshot cannot authorize a new
            // irreversible transaction. Actual execution has a separate ledger.
            return Regex.IsMatch(Compact(answer),
                @"(?:我|我们|这边|客服)(?:会|将|马上|立即|现在|可以|给您|帮您|为您){1,4}(?:退款|退货|赔偿|补偿|发货|充值|解封)|(?:给您|帮您|为您)(?:直接|马上|立即|自动)?(?:退款|赔偿|补偿|解封)");
        }

        private static bool HasKnowledgeContradiction(
            string answer,
            string approvedV2ContextEvidence)
        {
            // Only compare explicit capability assertions. For a unanimous
            // approved answer set, any contrary clause is a contradiction,
            // even if another clause of the generated answer agrees.
            var expected = Regex.Matches(approvedV2ContextEvidence ?? string.Empty, @"(?m)^答案：([^\r\n]+)")
                .Cast<Match>().SelectMany(x => ProductAssertionPolarities(x.Groups[1].Value))
                .Distinct().ToList();
            if (expected.Count != 1) return false;
            return ProductAssertionPolarities(answer).Any(x => x != expected[0]);
        }

        private static int DetectPolarity(string value)
        {
            var compact = Compact(value);
            // Match negative alternatives first, consuming the entire phrase so
            // "不支持" cannot also count as positive "支持". Mixed claims have
            // no single polarity (e.g. A supports it but B does not).
            var matches = Regex.Matches(compact,
                @"(?<negative>不支持|不能用|不能够|不能|不可以|无法|不通用|不可用|不行|没有)|(?<positive>支持|可以|能用|能够|可用|通用|没问题)");
            var negative = matches.Cast<Match>().Any(x => x.Groups["negative"].Success);
            var positive = matches.Cast<Match>().Any(x => x.Groups["positive"].Success);
            if (negative == positive) return 0;
            return negative ? -1 : 1;
        }

        private static bool HasHighRiskUnsupportedClaim(string answer, string authoritativeEvidence)
        {
            var compactAnswer = Compact(answer);
            foreach (var term in HighRiskTerms)
            {
                var compactTerm = Compact(term);
                if (!compactAnswer.Contains(compactTerm)) continue;
                if (ContainsEvidence(authoritativeEvidence, term)) continue;
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
            return compactValue.Length > 0 && Compact(compactEvidence).Contains(compactValue);
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
