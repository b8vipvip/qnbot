using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Canonical commerce context consumed by reply routing and knowledge learning.
    /// Structured order facts outrank text inference. Conversation inference is only a fallback
    /// when no verified order snapshot exists; it must never overwrite a structured order fact.
    /// </summary>
    internal sealed class CommerceContextSnapshot
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Source { get; set; }
        public bool HasStructuredOrder { get; set; }
        public string PurchasePhase { get; set; }
        public string PurchasePhaseDisplay { get; set; }
        public string OrderId { get; set; }
        public string TradeStatus { get; set; }
        public bool? IsPaid { get; set; }
        public string ItemId { get; set; }
        public string ItemTitle { get; set; }
        public string SkuId { get; set; }
        public string SkuText { get; set; }
        public string ProductCategory { get; set; }
        public string FulfillmentType { get; set; }
        public string FulfillmentTypeDisplay { get; set; }
        public string OrderEventType { get; set; }
        public DateTime EvidenceTime { get; set; }
        public string SelectionReason { get; set; }

        public CommerceContextSnapshot()
        {
            Source = "conversation_fallback";
            PurchasePhase = "unknown";
            PurchasePhaseDisplay = "未知";
            FulfillmentType = "unknown";
            FulfillmentTypeDisplay = "未知";
            ProductCategory = string.Empty;
        }

        public bool IsSpecificScenario
        {
            get
            {
                return HasStructuredOrder
                    || !string.Equals(PurchasePhase, "unknown", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(FulfillmentType, "unknown", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(ItemId)
                    || !string.IsNullOrWhiteSpace(SkuId);
            }
        }
    }

    internal static class CommerceContextService
    {
        public static CommerceContextSnapshot Build(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns,
            ConversationStateSnapshot state)
        {
            var result = new CommerceContextSnapshot
            {
                Seller = Clean(seller, 120),
                Buyer = Clean(buyer, 120)
            };

            string selectionReason;
            var order = SelectRelevantOrderSnapshot(
                seller,
                buyer,
                currentQuestion,
                turns,
                state,
                out selectionReason);
            if (order != null)
            {
                ApplyOrder(result, order);
                result.SelectionReason = selectionReason;
                Log.Info("Commerce订单选择: sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", seller)
                    + ", buyerRef=" + MyWebSocketServer.DiagnosticRef("buyer", buyer)
                    + ", orderRef=" + MyWebSocketServer.DiagnosticRef("order", order.OrderId)
                    + ", itemRef=" + MyWebSocketServer.DiagnosticRef("item", order.ItemId)
                    + ", skuRef=" + MyWebSocketServer.DiagnosticRef("sku", order.SkuId)
                    + ", phase=" + result.PurchasePhase
                    + ", tradeStatus=" + Safe(result.TradeStatus, 80)
                    + ", eventType=" + result.OrderEventType
                    + ", evidenceTime=" + result.EvidenceTime.ToString("o")
                    + ", selectionReason=" + selectionReason);
                return result;
            }

            result.SelectionReason = "no_structured_order";
            ApplyConversationFallback(result, currentQuestion, turns, state);
            return result;
        }

        internal static OrderSnapshot SelectRelevantOrderSnapshot(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns,
            ConversationStateSnapshot state,
            out string selectionReason)
        {
            selectionReason = "none";
            var candidates = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 12);
            if (candidates == null || candidates.Count == 0) return null;

            var current = Compact(currentQuestion);
            var recent = Compact(string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn)
                .OrderByDescending(x => x.Timestamp)
                .Take(12)
                .Select(x => x.Text ?? string.Empty)));
            var currentEntity = Compact(state == null ? string.Empty : state.CurrentEntity);

            OrderSnapshot selected = null;
            var selectedScore = int.MinValue;
            var selectedReason = "latest_confirmed_fallback";
            DateTime selectedEvidence = DateTime.MinValue;

            foreach (var candidate in candidates.Where(x => x != null))
            {
                var score = 0;
                var reasons = new List<string>();
                var orderId = Compact(candidate.OrderId);
                var itemId = Compact(candidate.ItemId);
                var skuId = Compact(candidate.SkuId);
                var title = Compact(candidate.ItemTitle);
                var skuText = Compact(candidate.SkuText);

                if (ContainsSignal(current, orderId, 6)) { score += 240; reasons.Add("current_order_id"); }
                else if (ContainsSignal(recent, orderId, 6)) { score += 150; reasons.Add("recent_order_id"); }

                if (ContainsSignal(current, skuId, 3)) { score += 150; reasons.Add("current_sku_id"); }
                else if (ContainsSignal(recent, skuId, 3)) { score += 90; reasons.Add("recent_sku_id"); }

                if (ContainsSignal(current, itemId, 3)) { score += 130; reasons.Add("current_item_id"); }
                else if (ContainsSignal(recent, itemId, 3)) { score += 75; reasons.Add("recent_item_id"); }

                if (ContainsSignal(current, title, 4)) { score += 95; reasons.Add("current_title"); }
                else if (ContainsSignal(recent, title, 4)) { score += 55; reasons.Add("recent_title"); }

                if (ContainsSignal(current, skuText, 3)) { score += 85; reasons.Add("current_sku_text"); }
                else if (ContainsSignal(recent, skuText, 3)) { score += 45; reasons.Add("recent_sku_text"); }

                if (currentEntity.Length >= 3
                    && (ContainsSignal(title, currentEntity, 3) || ContainsSignal(skuText, currentEntity, 3)
                        || ContainsSignal(currentEntity, title, 4) || ContainsSignal(currentEntity, skuText, 3)))
                {
                    score += 100;
                    reasons.Add("current_entity");
                }

                var statusText = Compact((candidate.TradeStatus ?? string.Empty) + " " + (candidate.EventText ?? string.Empty));
                var asksAfterSale = Regex.IsMatch(current, "退款|退货|售后|关闭|取消|refund|closed");
                var isAfterSale = candidate.EventType == OrderEventType.RefundRequested
                    || candidate.EventType == OrderEventType.Closed
                    || Regex.IsMatch(statusText, "退款|退货|关闭|取消|refund|closed");
                if (asksAfterSale && isAfterSale)
                {
                    score += 70;
                    reasons.Add("after_sale_intent");
                }

                var evidence = candidate.EventTime == DateTime.MinValue ? candidate.DetectedAt : candidate.EventTime;
                if (selected == null || score > selectedScore || (score == selectedScore && evidence > selectedEvidence))
                {
                    selected = candidate;
                    selectedScore = score;
                    selectedEvidence = evidence;
                    selectedReason = reasons.Count == 0
                        ? "latest_confirmed_fallback"
                        : string.Join("+", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }

            selectionReason = selectedReason + ";score=" + Math.Max(0, selectedScore);
            return selected;
        }

        private static bool ContainsSignal(string haystack, string needle, int minLength)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
            if (needle.Length < minLength) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void EnrichState(
            ConversationStateSnapshot state,
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            if (state == null) return;
            var commerce = Build(seller, buyer, currentQuestion, turns, state);
            state.CommerceContext = commerce;

            foreach (var fact in BuildFacts(commerce))
            {
                if (!state.ConfirmedFacts.Any(x => string.Equals(x, fact, StringComparison.OrdinalIgnoreCase)))
                    state.ConfirmedFacts.Add(fact);
            }

            // A verified structured order is the source of truth. Conversation regex state may
            // still describe the process details, but it must not downgrade the purchase phase.
            if (commerce.HasStructuredOrder)
            {
                state.ConversationStage = commerce.PurchasePhaseDisplay;
                if (state.Progress != null)
                {
                    state.Progress.HasOrderEvidence = true;
                    state.Progress.Stage = commerce.PurchasePhaseDisplay;
                }
                if (!string.IsNullOrWhiteSpace(commerce.ItemTitle))
                {
                    state.CurrentEntity = commerce.ItemTitle;
                    if (!state.Entities.Any(x => string.Equals(x, commerce.ItemTitle, StringComparison.OrdinalIgnoreCase)))
                        state.Entities.Insert(0, commerce.ItemTitle);
                }
            }
        }

        public static string BuildPromptAddon(CommerceContextSnapshot context)
        {
            if (context == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("\n【电商业务上下文｜高于聊天文字推断】\n");
            if (context.HasStructuredOrder)
            {
                sb.Append("以下订单事实来自已经严格确认的结构化订单事件。回复必须以这些事实为准，禁止被聊天中的旧说法覆盖。\n");
            }
            else
            {
                sb.Append("当前没有经过严格确认的结构化订单；以下阶段仅作为会话理解辅助，禁止据此声称订单已经付款、发货、退款或完成。\n");
            }
            sb.Append("购买阶段：").Append(context.PurchasePhaseDisplay).Append("\n");
            if (!string.IsNullOrWhiteSpace(context.TradeStatus))
                sb.Append("订单状态：").Append(Safe(context.TradeStatus, 120)).Append("\n");
            if (context.IsPaid.HasValue)
                sb.Append("付款状态：").Append(context.IsPaid.Value ? "已付款" : "未付款").Append("\n");
            if (!string.IsNullOrWhiteSpace(context.ItemTitle))
                sb.Append("当前订单商品：").Append(Safe(context.ItemTitle, 180)).Append("\n");
            if (!string.IsNullOrWhiteSpace(context.SkuText))
                sb.Append("当前SKU：").Append(Safe(context.SkuText, 160)).Append("\n");
            if (!string.IsNullOrWhiteSpace(context.ProductCategory))
                sb.Append("商品类目：").Append(Safe(context.ProductCategory, 120)).Append("\n");
            if (!string.Equals(context.FulfillmentType, "unknown", StringComparison.OrdinalIgnoreCase))
                sb.Append("履约类型：").Append(context.FulfillmentTypeDisplay).Append("\n");
            sb.Append("场景键：").Append(BuildPolicyScopeToken(context)).Append("\n");
            sb.Append("同一句买家问题在不同购买阶段、订单状态、SKU或履约类型下可能有不同答案；必须先匹配当前场景，再使用知识。")
                .Append("订单级临时事实只能用于当前会话，不能当作跨买家通用规则。\n");
            return sb.ToString();
        }

        /// <summary>
        /// KnowledgePolicyProfileService historically matches condition rows with OR semantics.
        /// Commerce scope therefore uses one atomic composite token rather than several independent
        /// conditions; a paid/SKU-specific correction cannot accidentally match only one dimension.
        /// The token deliberately excludes order id and buyer id so it can be reused across buyers.
        /// </summary>
        public static string BuildPolicyScopeToken(CommerceContextSnapshot context)
        {
            if (context == null || !context.IsSpecificScenario) return string.Empty;
            var phase = ScopePart(context.PurchasePhase, "unknown");
            var fulfillment = ScopePart(context.FulfillmentType, "unknown");
            var item = ScopePart(context.ItemId, "any");
            var sku = ScopePart(context.SkuId, "any");
            var category = ScopePart(context.ProductCategory, "any");
            return "commerce_scope[phase=" + phase
                + "|fulfillment=" + fulfillment
                + "|item=" + item
                + "|sku=" + sku
                + "|category=" + category + "]";
        }

        public static List<string> BuildScopeTerms(CommerceContextSnapshot context)
        {
            var result = new List<string>();
            if (context == null) return result;
            var policyScope = BuildPolicyScopeToken(context);
            if (!string.IsNullOrWhiteSpace(policyScope)) result.Add(policyScope);
            if (!string.IsNullOrWhiteSpace(context.PurchasePhaseDisplay))
                result.Add("订单阶段：" + context.PurchasePhaseDisplay);
            if (!string.Equals(context.FulfillmentType, "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(context.FulfillmentTypeDisplay))
                result.Add("履约类型：" + context.FulfillmentTypeDisplay);
            if (!string.IsNullOrWhiteSpace(context.ItemId)) result.Add("商品ID：" + context.ItemId);
            if (!string.IsNullOrWhiteSpace(context.SkuId)) result.Add("SKU ID：" + context.SkuId);
            if (!string.IsNullOrWhiteSpace(context.ProductCategory)) result.Add("商品类目：" + context.ProductCategory);
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> BuildFacts(CommerceContextSnapshot context)
        {
            if (context == null) yield break;
            var policyScope = BuildPolicyScopeToken(context);
            if (!string.IsNullOrWhiteSpace(policyScope)) yield return policyScope;
            yield return "订单阶段：" + context.PurchasePhaseDisplay;
            if (context.HasStructuredOrder) yield return "已取得结构化订单证据";
            if (!string.IsNullOrWhiteSpace(context.TradeStatus)) yield return "订单状态：" + context.TradeStatus;
            if (context.IsPaid.HasValue) yield return context.IsPaid.Value ? "付款状态：已付款" : "付款状态：未付款";
            if (!string.IsNullOrWhiteSpace(context.ItemTitle)) yield return "订单商品：" + context.ItemTitle;
            if (!string.IsNullOrWhiteSpace(context.SkuText)) yield return "订单SKU：" + context.SkuText;
            if (!string.IsNullOrWhiteSpace(context.ItemId)) yield return "商品ID：" + context.ItemId;
            if (!string.IsNullOrWhiteSpace(context.SkuId)) yield return "SKU ID：" + context.SkuId;
            if (!string.IsNullOrWhiteSpace(context.ProductCategory)) yield return "商品类目：" + context.ProductCategory;
            if (!string.Equals(context.FulfillmentType, "unknown", StringComparison.OrdinalIgnoreCase))
                yield return "履约类型：" + context.FulfillmentTypeDisplay;
        }

        private static void ApplyOrder(CommerceContextSnapshot target, OrderSnapshot order)
        {
            target.Source = "structured_order";
            target.HasStructuredOrder = true;
            target.OrderId = Clean(order.OrderId, 80);
            target.TradeStatus = Clean(order.TradeStatus, 120);
            target.IsPaid = order.IsPaid;
            target.ItemId = Clean(order.ItemId, 80);
            target.ItemTitle = Clean(order.ItemTitle, 260);
            target.SkuId = Clean(order.SkuId, 100);
            target.SkuText = Clean(order.SkuText, 220);
            target.OrderEventType = order.EventType.ToString();
            target.EvidenceTime = order.EventTime == DateTime.MinValue ? order.DetectedAt : order.EventTime;
            ResolvePurchasePhase(target, order);
            ResolveFulfillment(target, order);
        }

        private static void ResolvePurchasePhase(CommerceContextSnapshot target, OrderSnapshot order)
        {
            var status = Compact((order.TradeStatus ?? string.Empty) + " " + (order.EventText ?? string.Empty));
            if (order.EventType == OrderEventType.RefundRequested || Regex.IsMatch(status, "退款|退货|refund"))
            {
                SetPhase(target, "after_sale", "售后/退款处理中");
                return;
            }
            if (order.EventType == OrderEventType.Closed || Regex.IsMatch(status, "订单关闭|交易关闭|已关闭|已取消|tradeclosed"))
            {
                SetPhase(target, "closed", "订单已关闭");
                return;
            }
            if (Regex.IsMatch(status, "已签收|确认收货|tradebuyersigned|交易完成|tradefinished"))
            {
                SetPhase(target, "delivered", "已收货/已完成");
                return;
            }
            if (Regex.IsMatch(status, "已发货|卖家已发货|等待买家收货|waitbuyerconfirmgoods"))
            {
                SetPhase(target, "shipped", "已发货");
                return;
            }
            if (order.EventType == OrderEventType.Paid || order.IsPaid == true
                || Regex.IsMatch(status, "已付款|付款成功|支付成功|waitsellersendgoods"))
            {
                SetPhase(target, "paid", "已付款/待履约");
                return;
            }
            if (order.IsPaid == false || Regex.IsMatch(status, "等待买家付款|待付款|未付款|waitbuyerpay"))
            {
                SetPhase(target, "unpaid", "已下单/待付款");
                return;
            }
            SetPhase(target, "order_created", "已下单");
        }

        private static void ApplyConversationFallback(
            CommerceContextSnapshot target,
            string currentQuestion,
            IList<ConversationContextTurn> turns,
            ConversationStateSnapshot state)
        {
            var text = string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn)
                .Select(x => x.Text ?? string.Empty)) + " " + (currentQuestion ?? string.Empty);
            var compact = Compact(text);
            if (state != null && state.Progress != null && state.Progress.HasOrderEvidence)
                SetPhase(target, "post_order_unverified", "疑似下单后（未结构化确认）");
            else if (Regex.IsMatch(compact, "已下单|订单|付款|支付|发货|物流|收货"))
                SetPhase(target, "post_order_unverified", "疑似下单后（未结构化确认）");
            else
                SetPhase(target, "pre_order", "下单前");
        }

        private static void ResolveFulfillment(CommerceContextSnapshot target, OrderSnapshot order)
        {
            var text = Compact((order.ItemTitle ?? string.Empty) + " " + (order.SkuText ?? string.Empty) + " " + (order.EventText ?? string.Empty));
            if (Regex.IsMatch(text, "自动发货|自动充值|秒充|卡密|兑换码|自动到账"))
            {
                SetFulfillment(target, "automatic_digital", "自动虚拟履约");
                return;
            }
            if (Regex.IsMatch(text, "人工充值|人工代充|代充|手机号充值|账号充值"))
            {
                SetFulfillment(target, "manual_digital", "人工虚拟履约");
                return;
            }
            if (Regex.IsMatch(text, "会员|充值|激活|虚拟|卡券|软件|服务"))
            {
                SetFulfillment(target, "digital", "虚拟商品/服务");
                return;
            }
            if (Regex.IsMatch(text, "快递|物流|包邮|实物|收货地址"))
            {
                SetFulfillment(target, "physical", "实物物流");
            }
        }

        private static void SetPhase(CommerceContextSnapshot target, string value, string display)
        {
            target.PurchasePhase = value;
            target.PurchasePhaseDisplay = display;
        }

        private static void SetFulfillment(CommerceContextSnapshot target, string value, string display)
        {
            target.FulfillmentType = value;
            target.FulfillmentTypeDisplay = display;
        }

        private static string ScopePart(string value, string fallback)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) value = fallback;
            return Regex.Replace(value, @"[^a-z0-9\u4e00-\u9fff]+", "_").Trim('_');
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Safe(string value, int max)
        {
            return Clean(value, max);
        }
    }
}
