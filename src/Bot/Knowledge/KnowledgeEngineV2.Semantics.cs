using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal static class KnowledgeEngineV2Semantics
    {
        private static readonly Regex HighRisk = new Regex(
            @"退款|退货|赔偿|投诉|差评|举报|仲裁|验证码|密码|身份证|银行卡|法律|起诉|封号|解封",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ContextCue = new Regex(
            @"^(这个|那个|这款|那款|这种|那种|它|这样|那样|上面|前面|刚才|然后呢|那呢|这个呢|那个呢)|^(可以吗|行吗|对吗|是不是|怎么弄|怎么办)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static KnowledgeV2Query Parse(string message, KnowledgeV2WorkingMemory memory)
        {
            message = (message ?? string.Empty).Trim();
            var query = new KnowledgeV2Query
            {
                Original = message,
                Normalized = Compact(message),
                Intent = DetectIntent(message),
                Predicate = DetectPredicate(message),
                Entities = ExtractEntities(message),
                ContextDependent = IsContextDependent(message),
                WorkingMemoryReason = string.Empty
            };
            query.Subject = ResolveSubject(message, query.Entities);
            // A generic device/feature mentioned in a demonstrative follow-up
            // does not independently identify the product being discussed.
            if (memory != null && memory.UpdatedAt >= DateTime.Now.AddMinutes(-45)
                && ContextCue.IsMatch(query.Normalized) && !query.Entities.Any(IsProductEntity)
                && (memory.ProductEvidenceConflict || (memory.Entities ?? new List<string>()).Any(IsProductEntity)))
                query.ContextDependent = true;

            if (query.ContextDependent && memory != null && memory.UpdatedAt >= DateTime.Now.AddMinutes(-45))
            {
                var hasCurrentProduct = query.Entities.Any(IsProductEntity);
                if (!hasCurrentProduct)
                {
                    // A follow-up can introduce a device/feature without naming a
                    // new product. Preserve every prior explicit product, never
                    // silently narrow an ambiguous multi-product question.
                    if (memory.ProductEvidenceConflict) query.ProductEvidenceConflict = true;
                    if (memory.Entities != null)
                        query.Entities.AddRange(query.Entities.Count == 0
                            ? memory.Entities : memory.Entities.Where(IsProductEntity));
                }
                if (string.IsNullOrWhiteSpace(query.Subject)) query.Subject = memory.Subject;
                if (query.Intent == "general") query.Intent = memory.Intent;
                if (query.Predicate == "general") query.Predicate = memory.Predicate;
                query.WorkingMemoryReason = "当前消息缺少完整主体，仅补全最近明确业务对象";
            }
            if (query.Entities.Where(IsProductEntity).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 10)
                query.ProductEvidenceConflict = true;
            query.Entities = query.Entities
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(IsProductEntity)
                .Take(10)
                .ToList();
            return query;
        }

        internal static bool IsProductEntity(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.StartsWith("product:", StringComparison.OrdinalIgnoreCase);
        }

        public static string DetectIntent(string text)
        {
            var value = Compact(text);
            if (value.Length == 0) return "general";
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉")) return "after_sale";
            if (Regex.IsMatch(value, @"支付方式|付款方式|支持.*(支付|付款|微信|支付宝)|能用.*(微信|支付宝)|(?:微信|支付宝).{0,4}(支持|可以|能用|能否|吗)")) return "payment_method";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "price";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效|到账")) return "time";
            if (Regex.IsMatch(value, @"怎么|如何|步骤|操作|登录|绑定|充值方法|怎么充|激活")) return "how_to";
            if (Regex.IsMatch(value, @"为什么|失败|不行|报错|异常|不能用|没反应|没到账")) return "troubleshoot";
            if (Regex.IsMatch(value, @"需要什么|提供什么|要什么|资料|条件|截图")) return "requirements";
            if (Regex.IsMatch(value, @"哪里买|在哪买|购买|下单|链接")) return "purchase";
            if (Regex.IsMatch(value, @"是不是|是否|能不能|能否|可以|支持|可不可以|会员吗|能用吗|可以用吗")) return "capability";
            return "general";
        }

        public static string DetectPredicate(string text)
        {
            var value = Compact(text);
            if (Regex.IsMatch(value, @"哪里买|在哪买|购买|下单|链接")) return "purchase_channel";
            if (Regex.IsMatch(value, @"支付方式|付款方式|支持.*(支付|付款|微信|支付宝)|能用.*(微信|支付宝)|(?:微信|支付宝).{0,4}(支持|可以|能用|能否|吗)")) return "payment_method";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "price";
            if (Regex.IsMatch(value, @"登录|绑定|账号")) return "account_binding";
            if (Regex.IsMatch(value, @"充值|怎么充|如何充|激活|开通")) return "activation_method";
            if (Regex.IsMatch(value, @"退款|退货|赔偿")) return "refund_policy";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效|到账")) return "time";
            if (Regex.IsMatch(value, @"失败|不行|报错|异常|不能用|没反应|没到账")) return "troubleshooting";
            if (Regex.IsMatch(value, @"是什么会员|什么会员|是不是.*会员|会员吗|电视会员|tv会员")) return "membership_type";
            if (Regex.IsMatch(value, @"k歌|听歌|功能|权益")) return "feature_support";
            if (Regex.IsMatch(value, @"电视|tv|大屏|车机|手机|电脑|平板|设备")
                && Regex.IsMatch(value, @"支持|可以|能用|能不能|能否|可不可以")) return "device_support";
            if (Regex.IsMatch(value, @"需要什么|提供什么|资料|条件|截图")) return "requirements";
            return "general";
        }

        public static List<string> ExtractEntities(string value)
        {
            var result = new List<string>();
            value = value ?? string.Empty;
            // Structured product identifiers are useful retrieval keys, but arbitrary
            // 8-16 digit values are often phone numbers, account IDs or order numbers.
            // Only explicit product/item labels and known product URL forms become entities.
            foreach (var productId in ExtractProductIds(value))
                result.Add("product:" + productId);
            foreach (Match match in Regex.Matches(value,
                @"超级会员|豪华会员|电视机|电视|TV|大屏|车机|手机|电脑|平板|账号|会员|K歌|充值|支付|付款|验证码|订单|退款|支付宝|微信|APP|软件|TCL|海信|创维|小米|华为",
                RegexOptions.IgnoreCase))
            {
                result.Add(NormalizeEntity(match.Value));
            }
            foreach (Match match in Regex.Matches(value,
                @"[\u4e00-\u9fff]{2,8}(?=(?:音乐|视频|应用|软件|APP|TV))",
                RegexOptions.IgnoreCase))
            {
                result.Add(match.Value);
            }
            return result.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string ResolveSubject(string text, IEnumerable<string> entities)
        {
            var list = (entities ?? Enumerable.Empty<string>()).Where(x => !x.StartsWith("product:", StringComparison.Ordinal)).ToList();
            var selected = new List<string>();
            var appMatch = Regex.Match(text ?? string.Empty,
                @"[\u4e00-\u9fff]{2,8}(?=(?:音乐|视频|应用|软件|APP|TV))",
                RegexOptions.IgnoreCase);
            if (appMatch.Success) selected.Add(appMatch.Value);
            var preferred = new[] { "电视", "TV", "车机", "手机", "电脑", "平板", "会员", "K歌", "账号", "微信", "支付宝", "支付", "付款" };
            selected.AddRange(preferred.Where(x => list.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase))));
            selected = selected.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (selected.Count > 0) return string.Join("/", selected);
            var cleaned = Regex.Replace((text ?? string.Empty).Trim(), @"^(这个|那个|这款|那款|请问|想问下)+", string.Empty);
            cleaned = Regex.Replace(cleaned, @"[？?！!。,.，]", string.Empty).Trim();
            return cleaned.Length <= 18 ? cleaned : string.Empty;
        }

        public static string NormalizeType(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "business_fact":
                case "procedure":
                case "presale":
                case "order_rule":
                case "after_sale":
                case "safety_rule":
                case "fixed_reply":
                case "product_knowledge":
                case "learning_candidate":
                case "temporary":
                    return value;
                default:
                    return "business_fact";
            }
        }

        public static string NormalizeIntent(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(value) ? "general" : value;
        }

        public static string NormalizePredicate(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(value) ? "general" : value;
        }

        public static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        public static double TextSimilarity(string left, string right)
        {
            left = Compact(left);
            right = Compact(right);
            if (left.Length == 0 || right.Length == 0) return 0;
            if (left == right) return 1;
            if (Math.Min(left.Length, right.Length) >= 4 && (left.Contains(right) || right.Contains(left))) return 0.93;
            var a = Ngrams(left, 2);
            var b = Ngrams(right, 2);
            if (a.Count == 0 || b.Count == 0) return 0;
            var common = a.Intersect(b, StringComparer.Ordinal).Count();
            return Clamp((2.0 * common) / (a.Count + b.Count));
        }

        public static HashSet<string> Ngrams(string value, int n)
        {
            value = Compact(value);
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (value.Length < n) return set;
            for (var i = 0; i <= value.Length - n; i++) set.Add(value.Substring(i, n));
            return set;
        }

        public static List<string> RecallNgrams(string value, int n, int maxCount)
        {
            value = Compact(value);
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (n <= 0 || value.Length < n || maxCount <= 0) return ordered;
            for (var i = 0; i <= value.Length - n; i++)
            {
                var gram = value.Substring(i, n);
                if (seen.Add(gram)) ordered.Add(gram);
            }
            if (ordered.Count <= maxCount) return ordered;
            if (maxCount == 1) return new List<string> { ordered[ordered.Count / 2] };

            // Deterministic even sampling preserves beginning/middle/end coverage.
            // HashSet enumeration order must never decide which query terms get indexed.
            var sampled = new List<string>(maxCount);
            for (var i = 0; i < maxCount; i++)
            {
                var index = (int)Math.Round((double)i * (ordered.Count - 1) / (maxCount - 1));
                var gram = ordered[index];
                if (!sampled.Contains(gram)) sampled.Add(gram);
            }
            return sampled;
        }

        public static bool IsContextDependent(string message)
        {
            var compact = Compact(message);
            if (ContextCue.IsMatch(compact)) return !IsSelfContainedDemonstrativeQuestion(message);
            if (compact.Length <= 8)
            {
                // Short does not mean contextual by itself. A concise question with
                // an explicit business entity and a recognized predicate/intent is
                // still a standalone FAQ (e.g. "电视支持吗", "会员价格", "如何绑定账号").
                var entities = ExtractEntities(compact);
                var hasExplicitIntent = !string.Equals(DetectIntent(compact), "general", StringComparison.Ordinal)
                    || !string.Equals(DetectPredicate(compact), "general", StringComparison.Ordinal);
                return entities.Count == 0 || !hasExplicitIntent;
            }
            return false;
        }

        internal static bool IsSelfContainedDemonstrativeQuestion(string message)
        {
            var compact = Compact(message);
            if (!ContextCue.IsMatch(compact) || DetectPredicate(message) == "general") return false;
            // A demonstrative alone cannot identify a product. Permit a current,
            // independently stated business object or exact product ID, not a
            // subject inherited from the previous conversation.
            return ExtractEntities(message).Any(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                || Regex.IsMatch(compact, @"(?:电视机?|tv|大屏|车机|手机|电脑|平板|支付宝|微信)", RegexOptions.IgnoreCase);
        }

        public static bool IsHighRisk(string text)
        {
            return HighRisk.IsMatch(text ?? string.Empty);
        }

        public static bool IsApplicable(KnowledgeV2Record record, KnowledgeV2Query query, out string reason)
        {
            reason = string.Empty;
            if (record == null || query == null)
            {
                reason = "知识或查询为空";
                return false;
            }

            // ProductIds is a structured product scope as well as a recall key.
            // A buyer may identify a product by name without supplying its numeric ID,
            // so keep name-based retrieval available in that case. When the current
            // message does contain an explicit product ID, however, never let a record
            // bound to a different product answer or enter the approved context set.
            // Multi-product queries must be covered in full, not by a single shared ID.
            var recordProductIds = NormalizeProductIds(record.ProductIds);
            var queryProductIds = NormalizeProductIds(
                (query.Entities ?? new List<string>())
                    .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)));
            if (queryProductIds.Count > 1 && recordProductIds.Count == 0)
            {
                reason = "多商品问题缺少明确覆盖全部商品的知识范围";
                return false;
            }
            if (recordProductIds.Count > 0 && queryProductIds.Count > 0
                && !queryProductIds.All(queryId => recordProductIds.Any(id =>
                    string.Equals(id, queryId, StringComparison.OrdinalIgnoreCase))))
            {
                reason = "商品ID与当前问题中的商品不匹配";
                return false;
            }

            // Commerce scenario keys are atomic evidence, not natural-language keywords.
            // Evaluate all of them before bounded text matching (including late list entries).
            var scopeExclusions = CommerceScopes(record.Exclusions);
            var scopeRequirements = CommerceScopes(record.RequiredContext);
            var scopeConditions = CommerceScopes(record.Conditions);
            if (scopeRequirements.Concat(scopeConditions).Concat(scopeExclusions).Any()
                && string.IsNullOrWhiteSpace(query.VerifiedCommerceScope))
            {
                reason = "缺少已核验的电商场景证据";
                return false;
            }
            if (scopeExclusions.Any(x => ScopeTermMatches(x, query))
                || scopeRequirements.Any(x => !ScopeTermMatches(x, query))
                || (scopeConditions.Count > 0 && !scopeConditions.Any(x => ScopeTermMatches(x, query))))
            {
                reason = "电商场景键不匹配或命中排除条件";
                return false;
            }

            var exclusions = NormalizeScopeTerms(record.Exclusions);
            var excluded = exclusions.FirstOrDefault(x => ScopeTermMatches(x, query));
            if (!string.IsNullOrWhiteSpace(excluded))
            {
                reason = "命中排除条件：" + excluded;
                return false;
            }

            // Typed business-evidence requirements are evaluated by the dedicated
            // conditional-advice gate, which has access to authoritative order and image
            // ledgers. They must not be treated as buyer-authored text terms here.
            var allRequired = NormalizeScopeTerms(record.RequiredContext);
            var evidenceRequirements = allRequired
                .Where(ConditionalBusinessAdviceService.IsEvidenceRequirement)
                .ToList();
            var isConditionalAfterSale = string.Equals(record.Type, "after_sale", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
            if (evidenceRequirements.Count > 0 && !isConditionalAfterSale)
            {
                reason = "当前知识类型不支持专用业务证据条件";
                return false;
            }
            var required = allRequired
                .Where(x => !ConditionalBusinessAdviceService.IsEvidenceRequirement(x))
                .ToList();
            var missing = required.FirstOrDefault(x => !ScopeTermMatches(x, query, true));
            if (!string.IsNullOrWhiteSpace(missing))
            {
                reason = "缺少必需上下文：" + missing;
                return false;
            }

            var conditions = NormalizeScopeTerms(record.Conditions).Where(x => !IsCommerceScope(x)).ToList();
            if (conditions.Count > 0 && !conditions.Any(x => ScopeTermMatches(x, query, true)))
            {
                reason = "当前消息不满足适用条件";
                return false;
            }

            return true;
        }

        public static string FactKey(KnowledgeV2Record record)
        {
            if (record == null) return string.Empty;
            var subject = Compact(record.Subject);
            if (subject.Length == 0) subject = Compact(record.Title);
            var predicate = NormalizePredicate(record.Predicate);
            var intent = NormalizeIntent(record.Intent);
            var products = Signature(record.ProductIds, int.MaxValue, true);
            var entities = Signature(
                (record.Entities ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)
                        && !x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)),
                24,
                false);
            var conditions = Signature(record.Conditions, int.MaxValue, false);
            var required = Signature(record.RequiredContext, int.MaxValue, false);
            var exclusions = Signature(record.Exclusions, int.MaxValue, false);
            return subject + "|" + predicate + "|" + intent
                + "|p=" + products
                + "|e=" + entities
                + "|c=" + conditions
                + "|r=" + required
                + "|x=" + exclusions;
        }

        internal static bool ProductScopesOverlap(KnowledgeV2Record left, KnowledgeV2Record right)
        {
            if (left == null || right == null) return false;
            var a = NormalizeProductIds(left.ProductIds);
            var b = NormalizeProductIds(right.ProductIds);
            return a.Count == 0 || b.Count == 0
                || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();
        }

        private static List<string> NormalizeProductIds(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Select(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)
                    ? x.Substring("product:".Length).Trim()
                    : x)
                .Select(Compact)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> NormalizeScopeTerms(IEnumerable<string> terms)
        {
            return (terms ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static bool IsCommerceScope(string term)
        {
            return (term ?? string.Empty).Trim().StartsWith("commerce_scope", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> CommerceScopes(IEnumerable<string> terms)
        {
            return (terms ?? Enumerable.Empty<string>()).Where(IsCommerceScope)
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Explicit, bounded condition grammar. Existing flat lists keep their
        // OR (Conditions) / AND (RequiredContext) / deny (Exclusions) semantics.
        // Unknown is not false, so "!X" requires explicit evidence of absence.
        private sealed class ScopeExpressionParser
        {
            private readonly string[] tokens;
            private readonly KnowledgeV2Query query;
            private int pos;
            private bool invalid;

            public ScopeExpressionParser(string expression, KnowledgeV2Query value)
            {
                query = value;
                if (expression.Length > 1024) { tokens = new string[0]; invalid = true; return; }
                tokens = Regex.Split(expression, @"(&&|\|\||!|\(|\)|并且|或者)")
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                if (tokens.Length > 64) invalid = true;
            }

            public bool? Evaluate()
            {
                if (invalid) return null;
                var result = Or(0);
                return invalid || pos != tokens.Length ? (bool?)null : result;
            }

            private bool Take(string token)
            {
                if (pos >= tokens.Length || tokens[pos] != token) return false;
                pos++;
                return true;
            }

            private bool? Or(int depth)
            {
                var left = And(depth);
                while (Take("||") || Take("或者"))
                {
                    var right = And(depth);
                    left = left == true || right == true ? true : left == false && right == false ? (bool?)false : null;
                }
                return left;
            }

            private bool? And(int depth)
            {
                var left = Unary(depth);
                while (Take("&&") || Take("并且"))
                {
                    var right = Unary(depth);
                    left = left == false || right == false ? false : left == true && right == true ? (bool?)true : null;
                }
                return left;
            }

            private bool? Unary(int depth)
            {
                if (depth > 16 || pos >= tokens.Length) { invalid = true; return null; }
                if (Take("!"))
                {
                    var inner = Unary(depth + 1);
                    return inner.HasValue ? (bool?)!inner.Value : null;
                }
                if (Take("("))
                {
                    var inner = Or(depth + 1);
                    if (!Take(")")) invalid = true;
                    return inner;
                }
                var atom = tokens[pos++];
                if (atom == ")" || atom == "&&" || atom == "||" || atom == "并且" || atom == "或者"
                    || atom.StartsWith("expr:", StringComparison.OrdinalIgnoreCase)
                    || ConditionalBusinessAdviceService.IsEvidenceRequirement(atom))
                { invalid = true; return null; }
                if (IsCommerceScope(atom))
                    return string.IsNullOrWhiteSpace(query.VerifiedCommerceScope) ? (bool?)null
                        : string.Equals(atom, query.VerifiedCommerceScope, StringComparison.OrdinalIgnoreCase);
                if (Regex.IsMatch(atom, @"^(?:intent|predicate|subject|entity|product)[:=]", RegexOptions.IgnoreCase))
                    return ScopeTermMatches(atom, query, true) ? (bool?)true : null;
                return ExplicitConditionEvidence(query.Original, atom);
            }
        }

        private static bool? ExplicitConditionEvidence(string text, string term)
        {
            var source = Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
            var expected = Regex.Replace(term ?? string.Empty, @"\s+", string.Empty);
            if (expected.Length == 0) return null;
            var positive = false;
            var negative = false;
            foreach (var clause in Regex.Split(source, @"[，,。；;！？!?\r\n]+"))
            {
                if (Regex.IsMatch(clause, @"如果|假如|假设|是否|能否|有没有|不确定|不知道|想确认|吗$|么$")) continue;
                foreach (Match hit in Regex.Matches(clause, Regex.Escape(expected), RegexOptions.IgnoreCase))
                {
                    var prefix = clause.Substring(0, hit.Index);
                    var suffix = clause.Substring(hit.Index + hit.Length);
                    if (Regex.IsMatch(prefix, @"(?:并非|不是|没有|尚未|未曾|不含|不具备|不支持|不使用|未|无|没|不)(?:有|开通|安装|购买|启用|绑定|激活)?$")
                        || Regex.IsMatch(suffix, @"^(?:还|尚|仍|现在|目前)?(?:没有|尚未|未开通|未购买|未安装|未启用|未绑定|未激活|不具备|不存在|已取消|已失效|已过期)"))
                        negative = true;
                    else if (HasAffirmativeTerm(clause, expected))
                        positive = true;
                }
            }
            return positive == negative ? (bool?)null : positive;
        }

        private static bool ScopeTermMatches(string term, KnowledgeV2Query query, bool requireAffirmative = false)
        {
            term = (term ?? string.Empty).Trim();
            if (term.Length == 0 || query == null) return false;

            if (term.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
            {
                var value = new ScopeExpressionParser(term.Substring(5), query).Evaluate();
                // Exclusion is fail-closed even when its expression is unknown.
                return requireAffirmative ? value == true : value != false;
            }

            if (IsCommerceScope(term))
                return !string.IsNullOrWhiteSpace(query.VerifiedCommerceScope)
                    && string.Equals(term, query.VerifiedCommerceScope, StringComparison.OrdinalIgnoreCase);

            var lower = term.ToLowerInvariant();
            if (lower.StartsWith("intent:") || lower.StartsWith("intent="))
                return string.Equals(NormalizeIntent(term.Substring(7)), NormalizeIntent(query.Intent), StringComparison.OrdinalIgnoreCase);
            if (lower.StartsWith("predicate:") || lower.StartsWith("predicate="))
            {
                var split = term.IndexOfAny(new[] { ':', '=' });
                return split >= 0 && string.Equals(
                    NormalizePredicate(term.Substring(split + 1)),
                    NormalizePredicate(query.Predicate),
                    StringComparison.OrdinalIgnoreCase);
            }
            if (lower.StartsWith("subject:") || lower.StartsWith("subject="))
            {
                var split = term.IndexOfAny(new[] { ':', '=' });
                return split >= 0 && TextSimilarity(term.Substring(split + 1), query.Subject) >= 0.86;
            }
            if (lower.StartsWith("entity:") || lower.StartsWith("entity="))
            {
                var split = term.IndexOfAny(new[] { ':', '=' });
                var expected = split >= 0 ? Compact(term.Substring(split + 1)) : string.Empty;
                return expected.Length > 0 && (query.Entities ?? new List<string>())
                    .Select(Compact)
                    .Any(x => x == expected && (!requireAffirmative || HasAffirmativeTerm(query.Original, term.Substring(split + 1))));
            }
            if (lower.StartsWith("product:") || lower.StartsWith("product="))
            {
                var split = term.IndexOfAny(new[] { ':', '=' });
                var expected = split >= 0 ? Compact(term.Substring(split + 1)) : string.Empty;
                return expected.Length > 0 && (query.Entities ?? new List<string>())
                    .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                    .Select(x => Compact(x.Substring("product:".Length)))
                    .Any(x => x == expected);
            }

            var compact = Compact(term);
            if (compact.Length == 0) return false;
            if (requireAffirmative)
                return HasAffirmativeTerm(query.Original, term);
            var evidence = Compact((query.Original ?? string.Empty)
                + " " + (query.Subject ?? string.Empty)
                + " " + (query.Intent ?? string.Empty)
                + " " + (query.Predicate ?? string.Empty)
                + " " + string.Join(" ", query.Entities ?? new List<string>()));
            if (evidence.Contains(compact)) return true;
            return compact.Length >= 4 && TextSimilarity(term, query.Original) >= 0.86;
        }

        internal static bool HasAffirmativeTerm(string text, string term)
        {
            // Positive scope conditions require explicit wording in the query,
            // not a fuzzy match against inferred subject/intent or joined entities.
            // Preserve punctuation and decimal points; do not turn "没有X" into X.
            var source = Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
            var expected = Regex.Replace(term ?? string.Empty, @"\s+", string.Empty);
            if (expected.Length == 0) return false;
            var occurrences = Regex.Matches(source, Regex.Escape(expected), RegexOptions.IgnoreCase);
            if (occurrences.Count == 0) return false;
            foreach (Match occurrence in occurrences)
            {
                var prefix = source.Substring(0, occurrence.Index);
                if (Regex.IsMatch(prefix, @"(?:并非|不是|没有|未曾|尚未|不具备|不含|不支持|不使用|不需要|未|无|非|没|不)(?:[\u4e00-\u9fffA-Za-z0-9]{0,3})$", RegexOptions.IgnoreCase))
                    return false;
                // Suffix negation and uncertainty are also evidence of absence,
                // not proof of a positive condition. Restrict to the same clause.
                var suffix = source.Substring(occurrence.Index + occurrence.Length);
                if (Regex.IsMatch(suffix, @"^(?:还|尚|仍|现在|目前)?(?:没有|尚未|未开通|未购买|未安装|未启用|未绑定|未激活|未完成|未满足|不具备|不存在|已取消|已失效|已过期)", RegexOptions.IgnoreCase))
                    return false;
                if (Regex.IsMatch(prefix, @"(?:是否|有没有|能否|假如|假设|如果|不确定|不知道|想确认)(?:[\u4e00-\u9fffA-Za-z0-9]{0,3})$", RegexOptions.IgnoreCase))
                    return false;
                // A numeric condition must not match a suffix of a larger number.
                if (char.IsDigit(expected[0]) && occurrence.Index > 0
                    && (char.IsDigit(source[occurrence.Index - 1]) || source[occurrence.Index - 1] == '.'))
                    return false;
                if (char.IsDigit(expected[expected.Length - 1]) && suffix.Length > 0
                    && (char.IsDigit(suffix[0]) || suffix[0] == '.'))
                    return false;
            }
            return true;
        }

        private static string Signature(IEnumerable<string> values, int max, bool preserveDigits)
        {
            return string.Join(",", (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                // Scope identifiers and conditions retain punctuation (decimal
                // points, version separators, commerce-scope keys).
                .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(max).Select(x => x.Length + ":" + x));
        }

        private static List<string> ExtractProductIds(string text)
        {
            text = text ?? string.Empty;
            var result = new List<string>();
            var labeled = Regex.Matches(
                text,
                @"(?:(?:商品|产品)\s*(?:ID|编号|编码)|宝贝\s*(?:ID|编号|编码)|(?:product|item|goods)[_\s-]?id)\s*[""']?\s*[:：=#]?\s*[""']?(\d{8,20})",
                RegexOptions.IgnoreCase);
            result.AddRange(labeled.Cast<Match>()
                .Where(x => x.Groups.Count > 1 && x.Groups[1].Success)
                .Select(x => x.Groups[1].Value));

            var urls = Regex.Matches(
                text,
                @"(?:item\.taobao\.com/item\.htm|detail\.tmall\.com/item\.htm)[^\s<>""']*[?&]id=(\d{8,20})|item\.jd\.com/(\d{8,20})\.html|[?&](?:itemId|productId|goods_id|goodsId)=(\d{8,20})(?:[&#\s""']|$)",
                RegexOptions.IgnoreCase);
            foreach (Match match in urls)
            {
                for (var group = 1; group < match.Groups.Count; group++)
                {
                    if (!match.Groups[group].Success) continue;
                    result.Add(match.Groups[group].Value);
                    break;
                }
            }

            return result.Where(x => Regex.IsMatch(x ?? string.Empty, @"^\d{8,20}$"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string NormalizeEntity(string value)
        {
            if (string.Equals(value, "电视机", StringComparison.OrdinalIgnoreCase)) return "电视";
            if (string.Equals(value, "tv", StringComparison.OrdinalIgnoreCase)) return "TV";
            return (value ?? string.Empty).Trim();
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Max(0, Math.Min(1, value));
        }
    }
}
