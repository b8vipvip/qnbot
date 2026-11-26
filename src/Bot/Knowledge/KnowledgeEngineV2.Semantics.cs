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

        public static KnowledgeV2Record FromLegacy(KnowledgeBaseEntry entry, KnowledgePolicyProfile profile)
        {
            if (entry == null) return null;
            var text = (entry.Title ?? string.Empty) + " " + (entry.Keywords ?? string.Empty) + " " + (entry.Answer ?? string.Empty);
            var query = Parse(entry.Title ?? string.Empty, null);
            var type = ResolveType(entry.Category, query.Intent, query.Predicate, entry.Answer, entry.SourceType);
            var isUnapprovedLearning = KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(entry)
                || IsPendingCandidate(entry.SourceType, entry.Category);
            if (isUnapprovedLearning) type = "learning_candidate";
            var entities = ExtractEntities(text);
            var aliases = new List<string> { entry.Title ?? string.Empty };
            aliases.AddRange(SplitTerms(entry.Keywords));
            aliases.AddRange(entities);
            var confidence = profile == null || profile.Confidence <= 0 ? 0.80 : profile.Confidence;
            var reliability = profile == null ? 0.75 : profile.ReliabilityScore;
            var authority = ResolveAuthority(entry.SourceType, profile);
            var now = DateTime.Now;
            DateTime parsedUpdated;
            if (!DateTime.TryParse(entry.UpdatedAt, out parsedUpdated)) parsedUpdated = now;
            return new KnowledgeV2Record
            {
                Id = !string.IsNullOrWhiteSpace(entry.Id)
                    ? entry.Id.Trim()
                    : KnowledgeAiService.ContentHash(entry.Title ?? string.Empty, entry.Answer ?? string.Empty),
                Type = type,
                Title = (entry.Title ?? string.Empty).Trim(),
                Intent = query.Intent,
                Subject = ResolveSubject(entry.Title, entities),
                Predicate = query.Predicate,
                Entities = entities,
                Aliases = aliases.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Answer = (entry.Answer ?? string.Empty).Trim(),
                ShortAnswer = BuildShortAnswer(entry.Answer),
                Conditions = profile == null ? new List<string>() : SplitTerms(profile.ApplyWhen).ToList(),
                Exclusions = profile == null ? new List<string>() : SplitTerms(profile.DoNotApplyWhen).ToList(),
                RequiredContext = profile == null ? new List<string>() : SplitTerms(profile.RequiredContext).ToList(),
                ProductIds = ExtractProductIds(text),
                RiskLevel = HighRisk.IsMatch(text) ? "high" : "normal",
                SourceType = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy" : entry.SourceType,
                SourceId = entry.Id,
                Authority = authority,
                Confidence = Clamp(confidence * 0.55 + reliability * 0.45),
                UseCount = 0,
                AcceptedCount = profile == null ? 0 : profile.AcceptedCount,
                CorrectionCount = profile == null ? 0 : profile.SellerCorrectionCount,
                WithdrawCount = profile == null ? 0 : profile.SellerWithdrawCount,
                Enabled = entry.Enabled,
                Status = isUnapprovedLearning ? "candidate" : "active",
                CreatedAt = parsedUpdated,
                UpdatedAt = parsedUpdated,
                LastVerifiedAt = parsedUpdated
            };
        }

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

            if (query.ContextDependent && memory != null && memory.UpdatedAt >= DateTime.Now.AddMinutes(-45))
            {
                if (query.Entities.Count == 0 && memory.Entities != null)
                    query.Entities.AddRange(memory.Entities.Take(6));
                if (string.IsNullOrWhiteSpace(query.Subject)) query.Subject = memory.Subject;
                if (query.Intent == "general") query.Intent = memory.Intent;
                if (query.Predicate == "general") query.Predicate = memory.Predicate;
                query.WorkingMemoryReason = "当前消息缺少完整主体，仅补全最近明确业务对象";
            }
            query.Entities = query.Entities
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            return query;
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
            if (ContextCue.IsMatch(compact)) return true;
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
            var recordProductIds = NormalizeProductIds(record.ProductIds);
            var queryProductIds = NormalizeProductIds(
                (query.Entities ?? new List<string>())
                    .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)));
            if (recordProductIds.Count > 0 && queryProductIds.Count > 0
                && !recordProductIds.Any(id => queryProductIds.Any(queryId =>
                    string.Equals(id, queryId, StringComparison.OrdinalIgnoreCase))))
            {
                reason = "商品ID与当前问题中的商品不匹配";
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
            var missing = required.FirstOrDefault(x => !ScopeTermMatches(x, query));
            if (!string.IsNullOrWhiteSpace(missing))
            {
                reason = "缺少必需上下文：" + missing;
                return false;
            }

            var conditions = NormalizeScopeTerms(record.Conditions);
            if (conditions.Count > 0 && !conditions.Any(x => ScopeTermMatches(x, query)))
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
            var products = Signature(record.ProductIds, 8, true);
            var entities = Signature(
                (record.Entities ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)
                        && !x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)),
                6,
                false);
            var conditions = Signature(record.Conditions, 8, false);
            var required = Signature(record.RequiredContext, 8, false);
            var exclusions = Signature(record.Exclusions, 8, false);
            return subject + "|" + predicate + "|" + intent
                + "|p=" + products
                + "|e=" + entities
                + "|c=" + conditions
                + "|r=" + required
                + "|x=" + exclusions;
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
                .Take(20)
                .ToList();
        }

        private static List<string> NormalizeScopeTerms(IEnumerable<string> terms)
        {
            return (terms ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        }

        private static bool ScopeTermMatches(string term, KnowledgeV2Query query)
        {
            term = (term ?? string.Empty).Trim();
            if (term.Length == 0 || query == null) return false;

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
                    .Any(x => x == expected || x.Contains(expected) || expected.Contains(x));
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
            var evidence = Compact((query.Original ?? string.Empty)
                + " " + (query.Subject ?? string.Empty)
                + " " + (query.Intent ?? string.Empty)
                + " " + (query.Predicate ?? string.Empty)
                + " " + string.Join(" ", query.Entities ?? new List<string>()));
            if (evidence.Contains(compact)) return true;
            return compact.Length >= 4 && TextSimilarity(term, query.Original) >= 0.86;
        }

        private static string Signature(IEnumerable<string> values, int max, bool preserveDigits)
        {
            return string.Join(",", (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => preserveDigits ? (x ?? string.Empty).Trim().ToLowerInvariant() : Compact(x))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(max));
        }

        private static string ResolveType(string category, string intent, string predicate, string answer, string sourceType)
        {
            var value = (category ?? string.Empty) + " " + (sourceType ?? string.Empty);
            if (IsLearningSource(sourceType, category)) return "learning_candidate";
            if (HighRisk.IsMatch(answer ?? string.Empty)) return "safety_rule";
            if (Regex.IsMatch(value, @"售后|退款")) return "after_sale";
            if (Regex.IsMatch(value, @"订单|下单|付款")) return "order_rule";
            if (Regex.IsMatch(value, @"短消息|固定|首条|寒暄")) return "fixed_reply";
            if (intent == "how_to" || predicate == "activation_method" || predicate == "account_binding") return "procedure";
            if (Regex.IsMatch(value, @"商品|SKU", RegexOptions.IgnoreCase)) return "product_knowledge";
            if (intent == "capability" || intent == "purchase" || intent == "price") return "presale";
            return "business_fact";
        }

        private static bool IsLearningSource(string sourceType, string category)
        {
            var value = (sourceType ?? string.Empty) + " " + (category ?? string.Empty);
            return Regex.IsMatch(value, @"自动学习|会话学习|人工回复学习|学习候选", RegexOptions.IgnoreCase);
        }

        private static bool IsPendingCandidate(string sourceType, string category)
        {
            var value = (sourceType ?? string.Empty) + " " + (category ?? string.Empty);
            return Regex.IsMatch(value, @"自动学习|会话学习|人工回复学习|学习候选|pending|candidate|AI生成", RegexOptions.IgnoreCase);
        }

        private static double ResolveAuthority(string sourceType, KnowledgePolicyProfile profile)
        {
            var source = (sourceType ?? string.Empty).ToLowerInvariant();
            var authority = source.Contains("人工") || source.Contains("manual") ? 0.98
                : (source.Contains("导入") || source.Contains("fixed") ? 0.95 : 0.90);
            if (profile != null && profile.AcceptedCount >= 3) authority = Math.Max(authority, 0.96);
            if (profile != null && profile.SellerCorrectionCount > profile.AcceptedCount) authority = Math.Min(authority, 0.78);
            return Clamp(authority);
        }

        private static string BuildShortAnswer(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            if (answer.Length <= 80) return answer;
            var split = Regex.Split(answer, @"[。！？!?\r\n]").FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            return string.IsNullOrWhiteSpace(split) ? answer.Substring(0, 80) : split.Trim();
        }

        private static IEnumerable<string> SplitTerms(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', '/', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length >= 2);
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
                .Take(10)
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
