using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class ReceptionWorkflowEvidenceReference
    {
        public string Alias { get; set; }
        public string Kind { get; set; }
        public string Digest { get; set; }
        public string MessageKey { get; set; }
        public string ReplyText { get; set; }
        public string ProductId { get; set; }
        public string SkuId { get; set; }
        public string Summary { get; set; }
        public long ObservedAtUtcTicks { get; set; }
    }

    internal sealed class ReceptionWorkflowEvidenceContext
    {
        public string PromptText { get; set; }
        public Dictionary<string, ReceptionWorkflowEvidenceReference> References { get; private set; }

        public ReceptionWorkflowEvidenceContext()
        {
            PromptText = string.Empty;
            References = new Dictionary<string, ReceptionWorkflowEvidenceReference>(StringComparer.Ordinal);
        }
    }

    internal sealed class ReceptionWorkflowPublishItem
    {
        public string Stage { get; set; }
        public string FlowId { get; set; }
        public bool Published { get; set; }
        public string Message { get; set; }
    }

    internal sealed class ReceptionWorkflowPublishResult
    {
        public int PublishedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<ReceptionWorkflowPublishItem> Items { get; private set; }

        public ReceptionWorkflowPublishResult()
        {
            Items = new List<ReceptionWorkflowPublishItem>();
        }
    }

    /// <summary>
    /// Turns AI workflow proposals into active catalog entries only when every claim
    /// can be bound to exact ShopKey-scoped buyer, human-reply, visual and order evidence.
    /// AI has no administrator approval dependency, but it cannot invent source evidence.
    /// </summary>
    internal static class ReceptionWorkflowAgentService
    {
        private static readonly HashSet<string> EvidenceKinds = new HashSet<string>(
            new[] { "buyer_message", "image_message", "vision_observation", "order_snapshot", "manual_correction" },
            StringComparer.Ordinal);
        private static readonly HashSet<string> Actions = new HashSet<string>(
            new[] { "WAIT", "ASK_EVIDENCE", "HANDOFF", "REPLY" }, StringComparer.Ordinal);
        private static readonly Regex ValidState = new Regex(
            @"^[A-Za-z0-9_.-]{1,80}$", RegexOptions.Compiled);
        private static readonly Regex UnsafeAskText = new Regex(
            @"退款|退货|赔偿|投诉|支付|付款|充值|发货|银行卡|验证码|密码|身份证|手机号|解封|改账号",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string Digest(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static bool IsBotTurn(ConversationContextTurn turn, IList<BotConversationHistoryEntity> cards)
        {
            if (turn == null) return false;
            var text = (turn.Text ?? string.Empty).TrimEnd();
            if (text.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)) return true;
            var normalized = Regex.Replace(text, @"[\s\p{P}\p{S}]+", string.Empty).ToLowerInvariant();
            if (normalized.Length == 0 || turn.Timestamp == DateTime.MinValue || cards == null) return false;
            return cards.Any(card =>
            {
                if (card == null) return false;
                var answer = Regex.Replace((card.Answer ?? string.Empty).Trim(), @"[\s\p{P}\p{S}]+", string.Empty)
                    .ToLowerInvariant();
                var cardTicks = card.AnswerReadyAtTicks > 0 ? card.AnswerReadyAtTicks : card.CreatedAtTicks;
                return answer == normalized && cardTicks > 0
                    && Math.Abs((double)(cardTicks - turn.Timestamp.Ticks)) <= TimeSpan.FromMinutes(30).Ticks;
            });
        }

        internal static ReceptionWorkflowEvidenceContext BuildEvidenceContext(
            string seller,
            string buyer,
            string shopKey,
            IList<ConversationContextTurn> turns,
            IList<BotConversationHistoryEntity> cards)
        {
            var context = new ReceptionWorkflowEvidenceContext();
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            shopKey = Clean(shopKey, 200);
            if (seller.Length == 0 || buyer.Length == 0 || shopKey.Length == 0) return context;

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey); }
            catch { return context; }
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || !string.Equals(shop.SellerNick, seller, StringComparison.OrdinalIgnoreCase)) return context;

            var prompt = new StringBuilder();
            prompt.AppendLine("仅可引用下列真实证据别名；不得编造别名、订单、商品、图片内容或人工回复。");
            var messageIndex = 0;
            foreach (var turn in (turns ?? new ConversationContextTurn[0])
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text)
                    && !string.IsNullOrWhiteSpace(x.MessageKey))
                .Where(x => x.Role == "user" || (x.Role == "assistant" && !IsBotTurn(x, cards)))
                .OrderBy(x => x.Timestamp).Take(40))
            {
                var kind = turn.Role == "user" ? "buyer_message" : "manual_correction";
                var alias = "M" + (++messageIndex).ToString(CultureInfo.InvariantCulture);
                var summary = BuyerMessageArchiveService.Redact(Clean(turn.Text, 900));
                if (summary.Length == 0) continue;
                var reference = new ReceptionWorkflowEvidenceReference
                {
                    Alias = alias,
                    Kind = kind,
                    Digest = Digest(shopKey + "|" + turn.MessageKey),
                    MessageKey = turn.MessageKey,
                    ReplyText = kind == "manual_correction" ? summary : string.Empty,
                    Summary = summary,
                    ObservedAtUtcTicks = turn.Timestamp == DateTime.MinValue
                        ? 0 : turn.Timestamp.ToUniversalTime().Ticks
                };
                context.References[alias] = reference;
                prompt.Append('[').Append(alias).Append("][").Append(kind).Append("] ")
                    .Append(summary).AppendLine();
            }

            var visualIndex = 0;
            foreach (var visual in RecentVisualContextService.GetRecentWorkflowEvidence(
                shopKey, seller, buyer, 12))
            {
                if (visual == null || string.IsNullOrWhiteSpace(visual.MessageKey)
                    || string.IsNullOrWhiteSpace(visual.VisualSummary)) continue;
                var alias = "V" + (++visualIndex).ToString(CultureInfo.InvariantCulture);
                var summary = BuyerMessageArchiveService.Redact(Clean(
                    visual.VisualSummary + " " + visual.VisualTags, 900));
                if (summary.Length == 0) continue;
                context.References[alias] = new ReceptionWorkflowEvidenceReference
                {
                    Alias = alias,
                    Kind = "vision_observation",
                    Digest = Digest(shopKey + "|" + visual.MessageKey),
                    MessageKey = visual.MessageKey,
                    Summary = summary,
                    ObservedAtUtcTicks = visual.ObservedAtTicks
                };
                prompt.Append('[').Append(alias).Append("][vision_observation] ")
                    .Append(summary).AppendLine();
            }

            var orders = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 12, shopKey);
            var orderIndex = 0;
            foreach (var order in (orders ?? new List<OrderSnapshot>())
                .Where(x => x != null
                    && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(x.Seller, seller, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Buyer, buyer, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(x.OrderId)
                    && !string.IsNullOrWhiteSpace(x.ItemId)
                    && (x.EventType == OrderEventType.Created || x.EventType == OrderEventType.Paid)
                    && x.EventTime >= DateTime.Now.AddDays(-7))
                .OrderByDescending(x => x.EventTime))
            {
                var alias = "O" + (++orderIndex).ToString(CultureInfo.InvariantCulture);
                var summary = Clean(
                    "商品：" + order.ItemTitle + "；规格：" + order.SkuText
                    + "；数量：" + order.Quantity.ToString(CultureInfo.InvariantCulture)
                    + "；订单状态：" + order.TradeStatus
                    + "；付款确认：" + (order.IsPaid == true ? "是" : "未确认"),
                    700);
                context.References[alias] = new ReceptionWorkflowEvidenceReference
                {
                    Alias = alias,
                    Kind = "order_snapshot",
                    Digest = Digest(shopKey + "|" + order.OrderId + "|" + order.RawCardHash),
                    ProductId = Clean(order.ItemId, 128),
                    SkuId = Clean(order.SkuId, 128),
                    Summary = summary,
                    ObservedAtUtcTicks = order.EventTime == DateTime.MinValue
                        ? 0 : order.EventTime.ToUniversalTime().Ticks
                };
                prompt.Append('[').Append(alias).Append("][order_snapshot] ")
                    .Append(summary).AppendLine();
            }

            context.PromptText = prompt.ToString().Trim();
            return context;
        }

        private static bool IsSafeKey(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 128
                && value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');
        }

        private static bool TryBuildFlow(
            JObject proposal,
            ReceptionWorkflowEvidenceContext evidence,
            KugouReceptionConfig config,
            string shopKey,
            out JObject flow,
            out string reason)
        {
            flow = null;
            reason = string.Empty;
            if (proposal == null) { reason = "流程候选不是对象"; return false; }
            var confidence = proposal.Value<double?>("confidence") ?? 0;
            if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.90 || confidence > 1)
            {
                reason = "流程置信度低于0.90或数值无效";
                return false;
            }

            var refs = proposal["evidence_refs"] as JArray;
            if (refs == null || refs.Count < 3 || refs.Count > 12)
            {
                reason = "必须引用至少3条真实证据";
                return false;
            }
            var selected = new List<ReceptionWorkflowEvidenceReference>();
            foreach (var token in refs)
            {
                var alias = Convert.ToString(token);
                ReceptionWorkflowEvidenceReference reference;
                if (string.IsNullOrWhiteSpace(alias) || !evidence.References.TryGetValue(alias, out reference))
                {
                    reason = "引用了不存在的证据别名";
                    return false;
                }
                if (!selected.Any(x => x.Digest == reference.Digest)) selected.Add(reference);
            }
            if (!selected.Any(x => x.Kind == "buyer_message")
                || !selected.Any(x => x.Kind == "manual_correction")
                || !selected.Any(x => x.Kind == "order_snapshot"))
            {
                reason = "缺少买家原始消息、人工实际回复或已核验订单证据";
                return false;
            }

            var productIds = selected.Where(x => x.Kind == "order_snapshot")
                .Select(x => x.ProductId).Where(IsSafeKey).Distinct(StringComparer.Ordinal)
                .Where(x => config.ProductIds != null && config.ProductIds.Contains(x, StringComparer.Ordinal))
                .Take(20).ToList();
            if (productIds.Count == 0)
            {
                reason = "订单商品不属于当前店铺已配置的Reception Agent商品范围";
                return false;
            }
            var skuIds = selected.Where(x => x.Kind == "order_snapshot")
                .Select(x => x.SkuId).Where(IsSafeKey).Distinct(StringComparer.Ordinal)
                .Take(40).ToList();

            var stepTokens = proposal["steps"] as JArray;
            if (stepTokens == null || stepTokens.Count < 1 || stepTokens.Count > 8)
            {
                reason = "流程必须包含1到8个状态步骤";
                return false;
            }
            var states = new HashSet<string>(StringComparer.Ordinal);
            var steps = new JArray();
            foreach (var token in stepTokens)
            {
                var step = token as JObject;
                if (step == null) { reason = "流程步骤结构无效"; return false; }
                var state = Clean(Convert.ToString(step["state"]), 80);
                var nextState = Clean(Convert.ToString(step["next_state"]), 80);
                var action = Clean(Convert.ToString(step["action"]), 40).ToUpperInvariant();
                if (!ValidState.IsMatch(state) || !states.Add(state)
                    || !ValidState.IsMatch(nextState) || !Actions.Contains(action))
                {
                    reason = "步骤状态、下一状态或动作不受支持";
                    return false;
                }

                var required = step["required_evidence"] as JArray;
                if (required == null || required.Count < 1 || required.Count > 8
                    || required.Any(x => x == null || !EvidenceKinds.Contains(Convert.ToString(x))))
                {
                    reason = "步骤缺少受支持的必要证据类型";
                    return false;
                }

                var keywords = step["trigger_keywords"] as JArray
                    ?? proposal["trigger_keywords"] as JArray;
                if (keywords == null || keywords.Count < 1 || keywords.Count > 8
                    || keywords.Any(x => string.IsNullOrWhiteSpace(Convert.ToString(x))
                        || Convert.ToString(x).Length > 40))
                {
                    reason = "步骤触发关键词缺失或超出范围";
                    return false;
                }

                var replyTemplate = string.Empty;
                if (action == "ASK_EVIDENCE")
                {
                    replyTemplate = BuyerMessageArchiveService.Redact(Clean(
                        Convert.ToString(step["reply_text"]), 500));
                    if (replyTemplate.Length < 4 || UnsafeAskText.IsMatch(replyTemplate))
                    {
                        reason = "索证话术为空或包含高风险业务承诺";
                        return false;
                    }
                    if (!required.Values<string>().Contains("buyer_message", StringComparer.Ordinal))
                    {
                        reason = "索证步骤必须以真实买家消息为触发依据";
                        return false;
                    }
                }
                else if (action == "REPLY")
                {
                    var replyAlias = Clean(Convert.ToString(step["reply_evidence_ref"]), 20);
                    ReceptionWorkflowEvidenceReference replyEvidence;
                    if (!evidence.References.TryGetValue(replyAlias, out replyEvidence)
                        || replyEvidence.Kind != "manual_correction"
                        || string.IsNullOrWhiteSpace(replyEvidence.ReplyText))
                    {
                        reason = "回复步骤必须引用同一会话中的真实人工回复";
                        return false;
                    }
                    replyTemplate = BuyerMessageArchiveService.Redact(Clean(replyEvidence.ReplyText, 1200));
                    if (replyTemplate.Length < 2)
                    {
                        reason = "人工回复证据为空";
                        return false;
                    }
                }

                steps.Add(new JObject
                {
                    ["state"] = state,
                    ["trigger_keywords"] = new JArray(keywords.Values<string>()
                        .Select(x => Clean(x, 40)).Distinct(StringComparer.Ordinal).Take(8)),
                    ["required_evidence"] = required.DeepClone(),
                    ["action"] = action == "REPLY" ? "REPLY_REVIEWED" : action,
                    ["reply_template"] = replyTemplate,
                    ["next_state"] = nextState,
                    ["timeout_minutes"] = Math.Max(1, Math.Min(10080,
                        step.Value<int?>("timeout_minutes") ?? 60))
                });
            }

            foreach (var step in steps.OfType<JObject>())
            {
                if (!states.Contains(Convert.ToString(step["next_state"])))
                {
                    reason = "下一状态未在流程中定义";
                    return false;
                }
            }

            var trigger = (proposal["trigger_keywords"] as JArray ?? new JArray())
                .Values<string>().Select(x => Clean(x, 40)).Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (trigger.Count == 0)
            {
                reason = "流程缺少触发关键词";
                return false;
            }
            var idMaterial = shopKey + "|" + string.Join(",", productIds.OrderBy(x => x, StringComparer.Ordinal))
                + "|" + string.Join(",", trigger);
            var flowId = "wf-" + Digest(idMaterial).Substring(0, 24);
            var sourceEvidence = new JArray(selected.Take(12).Select(x => new JObject
            {
                ["kind"] = x.Kind,
                ["digest"] = x.Digest,
                ["observed_at_utc_ticks"] = x.ObservedAtUtcTicks
            }));
            flow = new JObject
            {
                ["flow_id"] = flowId,
                ["flow_version"] = 1,
                ["status"] = "active",
                ["product_ids"] = new JArray(productIds),
                ["sku_ids"] = new JArray(skuIds),
                ["confidence"] = confidence,
                ["source_evidence"] = sourceEvidence,
                ["updated_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["steps"] = steps
            };
            return true;
        }

        internal static ReceptionWorkflowPublishResult PublishProposals(
            string seller,
            string buyer,
            string shopKey,
            JArray proposals,
            ReceptionWorkflowEvidenceContext evidence)
        {
            var result = new ReceptionWorkflowPublishResult();
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            shopKey = Clean(shopKey, 200);
            if (proposals == null || proposals.Count == 0) return result;
            if (seller.Length == 0 || buyer.Length == 0 || shopKey.Length == 0
                || evidence == null || evidence.References.Count == 0)
            {
                result.SkippedCount = proposals.Count;
                result.Items.Add(new ReceptionWorkflowPublishItem
                {
                    Message = "缺少精确店铺或可追溯证据，未发布"
                });
                return result;
            }

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey); }
            catch
            {
                result.SkippedCount = proposals.Count;
                result.Items.Add(new ReceptionWorkflowPublishItem { Message = "无法解析精确ShopKey，未发布" });
                return result;
            }
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || !string.Equals(shop.SellerNick, seller, StringComparison.OrdinalIgnoreCase))
            {
                result.SkippedCount = proposals.Count;
                result.Items.Add(new ReceptionWorkflowPublishItem { Message = "Seller/ShopKey不匹配，未发布" });
                return result;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                var config = KugouReceptionConfigService.GetConfig(shop);
                if (!config.Enabled || config.ProductIds == null || config.ProductIds.Count == 0)
                {
                    result.SkippedCount = proposals.Count;
                    result.Items.Add(new ReceptionWorkflowPublishItem
                    {
                        Message = "当前ShopKey未启用Reception Agent商品范围，未发布"
                    });
                    return result;
                }

                var built = new List<JObject>();
                foreach (var proposal in proposals.OfType<JObject>().Take(4))
                {
                    JObject flow;
                    string reason;
                    if (!TryBuildFlow(proposal, evidence, config, shopKey, out flow, out reason))
                    {
                        result.SkippedCount++;
                        result.Items.Add(new ReceptionWorkflowPublishItem
                        {
                            Stage = Clean(Convert.ToString(proposal["name"]), 80),
                            Message = reason
                        });
                        continue;
                    }
                    built.Add(flow);
                    result.Items.Add(new ReceptionWorkflowPublishItem
                    {
                        Stage = Clean(Convert.ToString(proposal["name"]), 80),
                        FlowId = Convert.ToString(flow["flow_id"]),
                        Published = false,
                        Message = "已通过证据与流程结构校验，等待原子发布"
                    });
                }
                if (built.Count == 0) return result;

                Exception lastError = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var catalog = JObject.Parse(ReceptionWorkflowCatalogService.GetActiveJson(seller, shopKey));
                        var flows = catalog["flows"] as JArray ?? new JArray();
                        foreach (var flow in built)
                        {
                            var flowId = Convert.ToString(flow["flow_id"]);
                            var existing = flows.OfType<JObject>().FirstOrDefault(x =>
                                string.Equals(Convert.ToString(x["flow_id"]), flowId, StringComparison.Ordinal));
                            if (existing != null) existing.Replace(flow);
                            else flows.Add(flow);
                        }
                        catalog["flows"] = flows;
                        ReceptionWorkflowCatalogService.Publish(seller, catalog.ToString(), shopKey);
                        ReceptionWorkflowRuntimeService.InvalidateShop(shopKey);
                        result.PublishedCount = built.Count;
                        foreach (var item in result.Items.Where(x => !string.IsNullOrWhiteSpace(x.FlowId)))
                        {
                            item.Published = true;
                            item.Message = "AI流程已自动校验并立即发布";
                        }
                        return result;
                    }
                    catch (InvalidOperationException ex)
                    {
                        lastError = ex;
                        if (attempt == 1) break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        break;
                    }
                }

                foreach (var item in result.Items.Where(x => !string.IsNullOrWhiteSpace(x.FlowId)))
                    item.Message = "发布失败，未覆盖当前目录：" + Clean(lastError == null ? "unknown" : lastError.Message, 180);
                result.SkippedCount += built.Count;
                Log.ErrorWithMaxCount("Reception Agent自动发布失败，现有正式目录保持不变：" 
                    + (lastError == null ? "unknown" : lastError.Message), 10);
                return result;
            }
        }
    }
}