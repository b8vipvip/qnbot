using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Generic, shop-scoped evidence for conditional business advice.
    /// Image evidence is domain-neutral; the matched image is linked to the current
    /// buyer/shop and then compared with the selected Knowledge V2 record and order.
    /// Order evidence is required only by rules that explicitly declare it.
    /// </summary>
    internal sealed class ConditionalBusinessAdviceEvidence
    {
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public string ItemId { get; set; }
        public string ItemTitle { get; set; }
        public string SkuText { get; set; }
        public string PurchasePhase { get; set; }
        public string TradeStatus { get; set; }
        public bool HasStructuredOrder { get; set; }
        public bool? IsPaid { get; set; }
        public string OrderEventType { get; set; }
        public DateTime OrderEvidenceTime { get; set; }
        public string ImageKnowledgeId { get; set; }
        public string ImageMessageKey { get; set; }
        public string ImageDescription { get; set; }
        public string ImageTags { get; set; }
        public string ImageOcrText { get; set; }
        public DateTime ImageObservedAt { get; set; }
    }

    internal sealed class ConditionalBusinessAdviceLedgerRecord
    {
        public string Key { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public string AdviceKey { get; set; }
        public DateTime ReservedUntil { get; set; }
        public DateTime DeliveredAt { get; set; }
        public bool DeliveryUncertain { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class ConditionalBusinessAdviceLedgerState
    {
        public int SchemaVersion { get; set; }
        public List<ConditionalBusinessAdviceLedgerRecord> Records { get; set; }

        public ConditionalBusinessAdviceLedgerState()
        {
            SchemaVersion = 1;
            Records = new List<ConditionalBusinessAdviceLedgerRecord>();
        }
    }

    /// <summary>
    /// Conditional advice is a generic evidence policy, not an industry-specific image
    /// classifier. A record opts in by listing typed requirements in RequiredContext:
    /// evidence:image.matches_record, evidence:order.paid,
    /// evidence:order.open, evidence:order.product_matches_record.
    /// The initial action allow-list is intentionally limited to a refund recommendation;
    /// it never performs a refund or claims that one has been completed.
    /// </summary>
    internal static class ConditionalBusinessAdviceService
    {
        private const string ImageMatchesRecord = "evidence:image.matches_record";
        private const string OrderPaid = "evidence:order.paid";
        private const string OrderOpen = "evidence:order.open";
        private const string OrderProductMatches = "evidence:order.product_matches_record";
        private static readonly object LedgerSync = new object();
        private static readonly Regex ClosedOrFulfilledRegex = new Regex(
            "退款|退货|关闭|已关闭|已取消|取消|已发货|卖家已发货|等待买家收货|待收货|已签收|确认收货|买家已收货|交易完成|交易成功|已完成|waitbuyerconfirmgoods|trade[_ ]?closed|trade[_ ]?finished|trade[_ ]?success|refund|cancel|shipped|delivered|completed",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PaidRegex = new Regex(
            "已付款|付款成功|支付成功|waitsellersendgoods|trade[_ ]?paid",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UnsafeRefundActionRegex = new Regex(
            "已经退款|已退款|退款成功|退款已完成|已为您退款|帮您退款|替您退款|自动退款|直接退款|立即退款|马上退款|确认退款|执行退款|退款处理中|已提交退款",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SafeRefundSuggestionRegex = new Regex(
            "建议.{0,24}申请退款|可以.{0,16}申请退款|请.{0,16}申请退款",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsEvidenceRequirement(string value)
        {
            value = (value ?? string.Empty).Trim();
            return string.Equals(value, ImageMatchesRecord, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, OrderPaid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, OrderOpen, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, OrderProductMatches, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryBuild(
            string shopKey,
            string seller,
            string buyer,
            CommerceContextSnapshot commerce,
            out ConditionalBusinessAdviceEvidence evidence)
        {
            evidence = null;
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0
                || commerce == null || !commerce.HasStructuredOrder
                || commerce.IsPaid != true
                || !string.Equals(commerce.PurchasePhase, "paid", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(commerce.OrderId)
                || commerce.EvidenceTime == DateTime.MinValue)
                return false;

            List<OrderSnapshot> orders;
            try
            {
                orders = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 20, shopKey);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("条件性业务建议无法读取权威订单账本，拒绝本地直答：" + ex.Message, 10);
                return false;
            }

            // The order ledger may retain multiple events for one order. Collapse to
            // the latest event per order before checking terminal state or ambiguity.
            var latestOrders = (orders ?? new List<OrderSnapshot>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.OrderId))
                .GroupBy(x => x.OrderId.Trim(), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(x => x.EventTime)
                    .ThenByDescending(x => x.DetectedAt).First())
                .ToList();
            var matchingOrders = latestOrders
                .Where(x => string.Equals((x.OrderId ?? string.Empty).Trim(), commerce.OrderId.Trim(), StringComparison.Ordinal)
                    && string.Equals((x.ShopKey ?? string.Empty).Trim(), shopKey, StringComparison.Ordinal)
                    && string.Equals((x.Seller ?? string.Empty).Trim(), seller, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((x.Buyer ?? string.Empty).Trim(), buyer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var order = matchingOrders.FirstOrDefault();
            if (!IsPaidAndOpen(order)) return false;

            // If multiple paid orders exist and the conversation selector had to choose
            // a latest-order fallback, do not guess which order the image belongs to.
            var paidOpenOrders = latestOrders.Where(IsPaidAndOpen).ToList();
            var selectionReason = commerce.SelectionReason ?? string.Empty;
            var hasCurrentOrderId = selectionReason.IndexOf(
                "current_order_id", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasCurrentItemId = selectionReason.IndexOf(
                "current_item_id", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasCurrentSkuId = selectionReason.IndexOf(
                "current_sku_id", StringComparison.OrdinalIgnoreCase) >= 0;
            if (paidOpenOrders.Count > 1 && !hasCurrentOrderId)
            {
                var selectedByCurrentItem = hasCurrentItemId && !string.IsNullOrWhiteSpace(commerce.ItemId)
                    ? paidOpenOrders.Count(x => string.Equals(
                        (x.ItemId ?? string.Empty).Trim(), commerce.ItemId.Trim(), StringComparison.Ordinal)) == 1
                    : false;
                var selectedByCurrentSku = hasCurrentSkuId && !string.IsNullOrWhiteSpace(commerce.SkuId)
                    ? paidOpenOrders.Count(x => string.Equals(
                        (x.SkuId ?? string.Empty).Trim(), commerce.SkuId.Trim(), StringComparison.Ordinal)) == 1
                    : false;
                if (!selectedByCurrentItem && !selectedByCurrentSku)
                    return false;
            }

            VisualKnowledgeObservationEntity image;
            try
            {
                // Always use the newest image event, including an unmatched one. A newer
                // unmatched/withdrawn image must supersede an older curated match.
                image = RecentVisualContextService.GetLatestImageObservation(shopKey, seller, buyer);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("条件性业务建议无法读取图片证据，拒绝本地直答：" + ex.Message, 10);
                return false;
            }
            if (image == null || string.IsNullOrWhiteSpace(image.MatchedImageKnowledgeId))
                return false;

            var imageObservedAt = SafeTime(image.ObservedAtTicks);
            var orderEvidenceTime = order.PaidAt
                ?? (order.EventType == OrderEventType.Paid ? order.EventTime : commerce.EvidenceTime);
            if (imageObservedAt == DateTime.MinValue || orderEvidenceTime == DateTime.MinValue
                || imageObservedAt < orderEvidenceTime)
                return false;

            if (!IsImageKnowledgeMatchStillValid(shopKey, seller, image))
                return false;

            evidence = new ConditionalBusinessAdviceEvidence
            {
                ShopKey = shopKey,
                Seller = seller,
                Buyer = buyer,
                OrderId = commerce.OrderId,
                ItemId = commerce.ItemId,
                ItemTitle = commerce.ItemTitle,
                SkuText = commerce.SkuText,
                PurchasePhase = commerce.PurchasePhase,
                TradeStatus = commerce.TradeStatus,
                HasStructuredOrder = commerce.HasStructuredOrder,
                IsPaid = commerce.IsPaid,
                OrderEventType = commerce.OrderEventType,
                OrderEvidenceTime = orderEvidenceTime,
                ImageKnowledgeId = image.MatchedImageKnowledgeId,
                ImageMessageKey = image.MessageKey,
                ImageDescription = image.VisualSummary,
                ImageTags = image.VisualTags,
                ImageOcrText = image.OcrText,
                ImageObservedAt = imageObservedAt
            };
            return true;
        }

        public static string BuildImageEvidenceText(ConditionalBusinessAdviceEvidence evidence)
        {
            if (evidence == null) return string.Empty;
            return "【已命中本店人工维护的图片知识】\n"
                + "图片知识ID：" + Safe(evidence.ImageKnowledgeId, 120) + "\n"
                + "图片描述：" + Safe(evidence.ImageDescription, 1200) + "\n"
                + "图片标签：" + Safe(evidence.ImageTags, 800) + "\n"
                + "OCR证据：" + Safe(evidence.ImageOcrText, 6000);
        }

        public static string BuildPromptAddon(ConditionalBusinessAdviceEvidence evidence, bool adviceAlreadyDelivered)
        {
            if (evidence == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            sb.Append("\n\n【条件性业务证据】\n")
                .Append("当前图片知识已在本店、本买家范围内命中，且对应的结构化订单事实已核验。\n")
                .Append("订单阶段：").Append(Safe(evidence.PurchasePhase, 40)).Append("\n")
                .Append("订单状态：").Append(Safe(evidence.TradeStatus, 120)).Append("\n")
                .Append("付款状态：").Append(evidence.IsPaid == true ? "已付款" : "未确认已付款").Append("\n")
                .Append("当前商品：").Append(Safe(evidence.ItemTitle, 180)).Append("\n")
                .Append("当前SKU：").Append(Safe(evidence.SkuText, 160)).Append("\n")
                .Append("图片知识描述：").Append(Safe(evidence.ImageDescription, 600)).Append("\n");
            if (adviceAlreadyDelivered)
                sb.Append("该订单对应的条件性售后建议已有已发送记录或发送状态不确定，不得重复建议退款；可以回答其他问题，必要时转人工核实发送状态。\n");
            else
                sb.Append("只有知识记录明确要求的证据全部满足时，才可使用其条件性建议。聊天文字不能覆盖结构化订单事实；不得声称已经执行退款。\n");
            return sb.ToString();
        }

        public static bool TryAuthorizeRefundSuggestion(
            KnowledgeV2Decision decision,
            ConditionalBusinessAdviceEvidence evidence,
            out string reason)
        {
            reason = string.Empty;
            if (decision == null || evidence == null || decision.HasConflict
                || decision.Matches == null || decision.Matches.Count == 0)
            {
                reason = "缺少无冲突的Knowledge V2候选或业务证据";
                return false;
            }

            foreach (var match in decision.Matches
                .Where(x => x != null && x.Record != null)
                .OrderByDescending(x => x.Score))
            {
                var record = match.Record;
                if (!KnowledgeV2AuthorityPolicy.IsProductionApproved(record)) continue;
                var afterSaleQuestion = string.Equals(record.Intent, "after_sale", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(record.Predicate, "refund_policy", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(record.Predicate, "refund_advice", StringComparison.OrdinalIgnoreCase));
                var compatibilityQuestion = string.Equals(record.Intent, "capability", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(record.Predicate, "device_support", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(record.Predicate, "refund_advice", StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(record.Type, "after_sale", StringComparison.OrdinalIgnoreCase)
                    || !(afterSaleQuestion || compatibilityQuestion)
                    || !string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
                    continue;

                var required = new HashSet<string>(
                    (record.RequiredContext ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()),
                    StringComparer.OrdinalIgnoreCase);
                if (!required.Contains(ImageMatchesRecord)
                    || !required.Contains(OrderPaid)
                    || !required.Contains(OrderOpen)
                    || !required.Contains(OrderProductMatches))
                    continue;

                var exactAlias = decision.Query != null
                    && (record.Aliases ?? new List<string>()).Concat(new[] { record.Title })
                        .Any(alias => string.Equals(
                            KnowledgeV2Semantics.Compact(alias),
                            decision.Query.Normalized,
                            StringComparison.Ordinal));
                if (match.Score < 0.82d || match.ConfidenceScore < 0.94d
                    || (!exactAlias && (match.AliasScore < 0.94d
                        || match.PredicateScore < 0.99d || match.IntentScore < 0.99d)))
                    continue;
                if (!MatchesImage(record, evidence) || !MatchesOrderProduct(record, evidence))
                    continue;
                if (!HasSafeRefundSuggestion(record.Answer))
                    continue;

                decision.CanDirectReply = true;
                decision.Answer = (record.Answer ?? string.Empty).Trim();
                decision.ConditionalAdviceKey = "refund-advice:" + record.Id;
                decision.ConditionalAdviceOrderId = evidence.OrderId;
                decision.ConditionalAdviceItemId = evidence.ItemId;
                decision.ConditionalAdviceImageMessageKey = evidence.ImageMessageKey;
                decision.ConditionalAdviceImageKnowledgeId = evidence.ImageKnowledgeId;
                decision.Reason = "条件性售后建议通过图片知识、商品关联、已付款且未终态订单证据门禁";
                reason = decision.Reason;
                return true;
            }

            reason = "没有知识记录同时满足售后类型、显式证据要求、商品关联和安全建议话术";
            return false;
        }

        public static bool IsEvidenceStillValid(
            string shopKey,
            string seller,
            string buyer,
            string orderId,
            string itemId,
            string imageMessageKey,
            string imageKnowledgeId)
        {
            try
            {
                var order = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 20, shopKey)
                    .Where(x => x != null
                        && string.Equals((x.OrderId ?? string.Empty).Trim(), (orderId ?? string.Empty).Trim(), StringComparison.Ordinal)
                        && string.Equals((x.ShopKey ?? string.Empty).Trim(), (shopKey ?? string.Empty).Trim(), StringComparison.Ordinal))
                    .OrderByDescending(x => x.EventTime)
                    .FirstOrDefault();
                if (!IsPaidAndOpen(order)) return false;
                if (!string.IsNullOrWhiteSpace(itemId)
                    && !string.Equals((order.ItemId ?? string.Empty).Trim(), itemId.Trim(), StringComparison.Ordinal))
                    return false;

                var image = RecentVisualContextService.GetLatestImageObservation(shopKey, seller, buyer);
                if (image == null
                    || !string.Equals(image.MessageKey, imageMessageKey, StringComparison.Ordinal)
                    || !string.Equals(image.MatchedImageKnowledgeId, imageKnowledgeId, StringComparison.Ordinal)
                    || !IsImageKnowledgeMatchStillValid(shopKey, seller, image))
                    return false;
                var imageTime = SafeTime(image.ObservedAtTicks);
                var orderTime = order.PaidAt
                    ?? (order.EventType == OrderEventType.Paid
                        ? order.EventTime
                        : (order.EventTime == DateTime.MinValue ? order.DetectedAt : order.EventTime));
                return imageTime != DateTime.MinValue && orderTime != DateTime.MinValue && imageTime >= orderTime;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("条件性业务建议发送前证据复核失败，已阻止发送：" + ex.Message, 10);
                return false;
            }
        }

        public static bool TryReserveDelivery(
            string shopKey,
            string seller,
            string buyer,
            string orderId,
            string itemId,
            string adviceKey,
            string imageMessageKey,
            string imageKnowledgeId,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(shopKey) || string.IsNullOrWhiteSpace(seller)
                || string.IsNullOrWhiteSpace(buyer) || string.IsNullOrWhiteSpace(orderId)
                || string.IsNullOrWhiteSpace(adviceKey))
            {
                reason = "条件性建议缺少店铺、买家、订单或知识标识";
                return false;
            }
            if (!IsEvidenceStillValid(shopKey, seller, buyer, orderId, itemId, imageMessageKey, imageKnowledgeId))
            {
                reason = "订单或图片证据已变化、失效或无法复核";
                return false;
            }

            var path = GetLedgerPath();
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "conditional-business-advice", 5000))
            lock (LedgerSync)
            {
                if (!lease.Acquired)
                {
                    reason = "条件性建议幂等账本锁不可用，已拒绝发送";
                    return false;
                }
                string loadError;
                var state = LoadLedger(path, out loadError);
                if (state == null)
                {
                    reason = "条件性建议幂等账本不可读取，已拒绝发送：" + loadError;
                    return false;
                }
                CleanupLedger(state);
                var key = BuildLedgerKey(shopKey, seller, buyer, orderId, adviceKey);
                var record = state.Records.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (record != null && record.DeliveredAt != DateTime.MinValue)
                {
                    reason = "该订单的相同售后建议已经发送，不再重复发送";
                    return false;
                }
                if (record != null && record.ReservedUntil > DateTime.Now)
                {
                    reason = record.DeliveryUncertain
                        ? "该订单的售后建议发送状态不确定，需要人工核实，禁止重复发送"
                        : "该订单的售后建议正在发送，禁止并发重复发送";
                    return false;
                }
                if (record == null)
                {
                    record = new ConditionalBusinessAdviceLedgerRecord
                    {
                        Key = key,
                        ShopKey = shopKey,
                        Seller = seller,
                        Buyer = buyer,
                        OrderId = orderId,
                        AdviceKey = adviceKey
                    };
                    state.Records.Add(record);
                }
                // Reserve before entering the send call. If the process crashes before
                // it can record the outcome, remain fail-closed until manual reconciliation.
                record.ReservedUntil = DateTime.MaxValue;
                record.DeliveryUncertain = true;
                record.UpdatedAt = DateTime.Now;
                if (!SaveLedger(path, state, out loadError))
                {
                    reason = "条件性建议幂等账本写入失败，已拒绝发送：" + loadError;
                    return false;
                }
                return true;
            }
        }

        public static void CompleteDelivery(
            string shopKey,
            string seller,
            string buyer,
            string orderId,
            string adviceKey,
            bool delivered,
            bool cancelled)
        {
            var path = GetLedgerPath();
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "conditional-business-advice", 5000))
            lock (LedgerSync)
            {
                if (!lease.Acquired) return;
                string error;
                var state = LoadLedger(path, out error);
                if (state == null) return;
                var key = BuildLedgerKey(shopKey, seller, buyer, orderId, adviceKey);
                var record = state.Records.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (record == null) return;
                if (delivered)
                {
                    record.DeliveredAt = DateTime.Now;
                    record.DeliveryUncertain = false;
                    record.ReservedUntil = DateTime.MinValue;
                }
                else if (cancelled)
                {
                    record.ReservedUntil = DateTime.MinValue;
                    record.DeliveryUncertain = false;
                }
                else
                {
                    // A failed send after entering the delivery call is ambiguous. Hold the
                    // advice key until manual reconciliation instead of risking a duplicate.
                    record.DeliveryUncertain = true;
                    record.ReservedUntil = DateTime.MaxValue;
                }
                record.UpdatedAt = DateTime.Now;
                SaveLedger(path, state, out error);
            }
        }

        private static bool IsImageKnowledgeMatchStillValid(
            string shopKey, string seller, VisualKnowledgeObservationEntity image)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.MatchedImageKnowledgeId)
                || string.IsNullOrWhiteSpace(image.OcrImageSha256))
                return false;
            var active = VisualImageKnowledgeService.Load(shopKey, seller)
                .Any(x => x != null && x.Enabled
                    && string.Equals(x.EntityId, image.MatchedImageKnowledgeId, StringComparison.Ordinal));
            if (!active) return false;
            VisualImageKnowledgeMatch currentMatch;
            return VisualImageKnowledgeService.TryMatch(
                    shopKey, seller, image.OcrText, image.OcrImageSha256, image.OcrConfidence,
                    out currentMatch)
                && currentMatch != null
                && string.Equals(currentMatch.KnowledgeId, image.MatchedImageKnowledgeId, StringComparison.Ordinal);
        }

        private static bool MatchesImage(KnowledgeV2Record record, ConditionalBusinessAdviceEvidence evidence)
        {
            var imageText = Compact((evidence.ImageDescription ?? string.Empty) + " "
                + (evidence.ImageTags ?? string.Empty) + " " + (evidence.ImageOcrText ?? string.Empty));
            return GetSpecificTerms(record).Any(term => imageText.Contains(term));
        }

        private static bool MatchesOrderProduct(KnowledgeV2Record record, ConditionalBusinessAdviceEvidence evidence)
        {
            var productIds = (record.ProductIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().StartsWith("product:", StringComparison.OrdinalIgnoreCase)
                    ? x.Trim().Substring("product:".Length).Trim() : x.Trim())
                .Select(Compact)
                .Where(x => x.Length > 0)
                .ToList();
            if (productIds.Count > 0)
            {
                var itemId = Compact(evidence.ItemId);
                return itemId.Length > 0 && productIds.Contains(itemId, StringComparer.OrdinalIgnoreCase);
            }
            var orderText = Compact((evidence.ItemTitle ?? string.Empty) + " " + (evidence.SkuText ?? string.Empty));
            return orderText.Length > 0 && GetSpecificTerms(record).Any(term => orderText.Contains(term));
        }

        private static IEnumerable<string> GetSpecificTerms(KnowledgeV2Record record)
        {
            var ignored = new HashSet<string>(new[]
            {
                "订单", "商品", "产品", "宝贝", "会员", "充值", "服务", "退款", "售后",
                "设备", "图片", "账号", "系统", "应用", "页面", "支持", "电视", "软件"
            }, StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();
            if (record != null)
            {
                values.AddRange(record.Entities ?? new List<string>());
                values.Add(record.Subject ?? string.Empty);
                values.Add(record.Title ?? string.Empty);
            }
            return values.Select(Compact)
                .Where(x => x.Length >= 2 && !ignored.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasSafeRefundSuggestion(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            return SafeRefundSuggestionRegex.IsMatch(answer)
                && !UnsafeRefundActionRegex.IsMatch(answer);
        }

        private static bool IsPaidAndOpen(OrderSnapshot order)
        {
            if (order == null || order.IsPaid != true) return false;
            var status = (order.TradeStatus ?? string.Empty) + " " + (order.EventText ?? string.Empty);
            if (order.EventType == OrderEventType.Closed
                || order.EventType == OrderEventType.RefundRequested
                || ClosedOrFulfilledRegex.IsMatch(status))
                return false;
            return order.EventType == OrderEventType.Paid || PaidRegex.IsMatch(status);
        }

        private static DateTime SafeTime(long ticks)
        {
            try { return ticks > 0 ? new DateTime(ticks) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】/|]+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string BuildLedgerKey(string shopKey, string seller, string buyer, string orderId, string adviceKey)
        {
            return string.Join("|", new[]
            {
                shopKey ?? string.Empty, seller ?? string.Empty, buyer ?? string.Empty,
                orderId ?? string.Empty, adviceKey ?? string.Empty
            });
        }

        private static string GetLedgerPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot", "data", "conditional-business-advice-ledger.json");
        }

        private static ConditionalBusinessAdviceLedgerState LoadLedger(string path, out string error)
        {
            error = string.Empty;
            if (!File.Exists(path)) return new ConditionalBusinessAdviceLedgerState();
            var json = CrossProcessAtomicStateFile.ReadAllTextShared(path, 3, 50, out error);
            if (string.IsNullOrWhiteSpace(json))
            {
                if (string.IsNullOrWhiteSpace(error)) error = "账本内容为空";
                return null;
            }
            try
            {
                var state = JsonConvert.DeserializeObject<ConditionalBusinessAdviceLedgerState>(json);
                if (state == null || state.Records == null || state.SchemaVersion != 1)
                {
                    error = "账本结构或版本无效";
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static void CleanupLedger(ConditionalBusinessAdviceLedgerState state)
        {
            if (state == null || state.Records == null) return;
            var cutoff = DateTime.Now.AddDays(-90);
            state.Records.RemoveAll(x => x == null || (!x.DeliveryUncertain && x.UpdatedAt < cutoff));
            if (state.Records.Count > 10000)
                state.Records = state.Records.OrderByDescending(x => x.UpdatedAt).Take(10000).ToList();
        }

        private static bool SaveLedger(string path, ConditionalBusinessAdviceLedgerState state, out string error)
        {
            state.SchemaVersion = 1;
            return CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, JsonConvert.SerializeObject(state, Formatting.Indented), 3, 50, out error);
        }
    }
}
