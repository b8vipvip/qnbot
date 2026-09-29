using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    /// <summary>
    /// 千牛普通聊天消息中也可能包含 tid、status、count 等通用字段。
    /// 这些字段绝不能单独作为订单证据，否则任意买家文字都会被误判为下单事件。
    /// </summary>
    internal static class OrderMessageClassifier
    {
        private static readonly Regex LabeledOrderIdRegex = new Regex(
            "(?:订单号|订单编号|主订单号|子订单号|交易号)\\s*[:：#]?\\s*(\\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongOrderIdKeyRegex = new Regex(
            "[\\\"'](?:orderid|bizorderid|mainorderid|suborderid|biztradeid|tradeid)[\\\"']\\s*:\\s*[\\\"']?(\\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongSystemCueRegex = new Regex(
            "买家已下单|买家下单成功|订单创建成功|已成功下单|买家已付款|付款成功|支付成功|交易成功|等待买家付款|待卖家发货|卖家待发货|订单关闭|交易关闭|申请退款|退款中",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongStatusCodeRegex = new Regex(
            "WAIT_BUYER_PAY|WAIT_SELLER_SEND_GOODS|TRADE_BUYER_SIGNED|TRADE_FINISHED|TRADE_CLOSED|REFUND",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongItemFieldRegex = new Regex(
            "[\\\"'](?:itemtitle|auctiontitle|producttitle|goodstitle|itemname|productname)[\\\"']\\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongSkuFieldRegex = new Regex(
            "[\\\"'](?:skuid|skutext|skuname|skupropertiesname|propertiesname|specification)[\\\"']\\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongAmountFieldRegex = new Regex(
            "[\\\"'](?:paidamount|paidfee|actualfee|realpay|totalamount|totalfee|orderamount)[\\\"']\\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StrongStatusFieldRegex = new Regex(
            "[\\\"'](?:tradestatus|orderstatus|paystatus|statustext)[\\\"']\\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsConfirmedOrderEvent(QNChatMessage message, string visibleText, out string reason)
        {
            reason = string.Empty;
            var text = (visibleText ?? string.Empty).Trim();
            var raw = Serialize(message);
            var combined = text + " " + raw;

            var labeledId = LabeledOrderIdRegex.IsMatch(combined);
            var strongKeyId = StrongOrderIdKeyRegex.IsMatch(raw);
            if (!labeledId && !strongKeyId)
            {
                reason = "缺少明确订单号字段；普通消息 tid 不作为订单号";
                return false;
            }

            var systemCue = StrongSystemCueRegex.IsMatch(combined) || StrongStatusCodeRegex.IsMatch(combined);
            var structureScore = 0;
            if (StrongItemFieldRegex.IsMatch(raw)
                || Regex.IsMatch(text, "(?:商品|宝贝)\\s*[:：]", RegexOptions.IgnoreCase)) structureScore++;
            if (StrongSkuFieldRegex.IsMatch(raw)
                || Regex.IsMatch(text, "(?:SKU|规格|属性)\\s*[:：]", RegexOptions.IgnoreCase)) structureScore++;
            if (StrongAmountFieldRegex.IsMatch(raw)
                || Regex.IsMatch(text, "(?:实付|合计|金额|订单总价)\\s*[:：]?\\s*[¥￥]?\\d", RegexOptions.IgnoreCase)) structureScore++;
            if (StrongStatusFieldRegex.IsMatch(raw)
                || Regex.IsMatch(text, "(?:订单状态|交易状态)\\s*[:：]", RegexOptions.IgnoreCase)) structureScore++;
            if (Regex.IsMatch(text, "\\d+\\s*件商品") && text.Contains("合计")) structureScore += 2;

            if (systemCue)
            {
                reason = "明确下单/付款系统事件";
                return true;
            }
            if (structureScore >= 2)
            {
                reason = "订单号与订单卡片结构同时成立";
                return true;
            }

            reason = "只有订单号或通用状态字段，没有下单/付款事件证据";
            return false;
        }

        public static bool IsConfirmedOrderEvent(QNChatMessage message, string visibleText)
        {
            string ignored;
            return IsConfirmedOrderEvent(message, visibleText, out ignored);
        }

        private static string Serialize(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try { return JObject.FromObject(message).ToString(Formatting.None); }
            catch { return string.Empty; }
        }
    }

    internal sealed class OrderGuidanceRecord
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public DateTime EventTime { get; set; }
        public DateTime ObservedAt { get; set; }
        public OrderSnapshot Snapshot { get; set; }
        public DateTime? InitialDeliveredAt { get; set; }
        public string InitialDeliveredBy { get; set; }
        public DateTime? FollowUpDeliveredAt { get; set; }
        public string FollowUpDeliveredBy { get; set; }
        public string FollowUpTriggerHash { get; set; }
    }

    internal sealed class OrderGuidanceState
    {
        public int SchemaVersion { get; set; }
        public List<OrderGuidanceRecord> Records { get; set; }

        public OrderGuidanceState()
        {
            SchemaVersion = 1;
            Records = new List<OrderGuidanceRecord>();
        }
    }

    /// <summary>
    /// 同一订单的充值流程首次最多发送一次；无论由 Bot 还是人工客服发送都视为已经完成。
    /// 买家明确说“拍了 / 下单了 / 怎么充”等续问时，只允许额外补发一次。
    /// </summary>
    internal static class OrderGuidanceDeliveryGuard
    {
        private static readonly object Sync = new object();
        private static OrderGuidanceState _state;

        private static readonly Regex NegativeFollowUpRegex = new Regex(
            "没下单|没有下单|未下单|还没下单|没付款|未付款|还没付款|没拍|还没拍|怎么下单|不能下单|无法下单",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PositiveFollowUpRegex = new Regex(
            "^(?:我)?(?:已经|已)?(?:拍了|拍好了|拍完了|截图了|截好了|照片拍了|发图了|图片发了|下单了|付款了|付好了|买好了)$|^(?:请问)?(?:怎么充|怎么充值|如何充值|接下来怎么弄|下一步怎么弄|然后呢|下一步呢)[呀啊吗呢？?]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static void ObserveOrder(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Seller)
                || string.IsNullOrWhiteSpace(snapshot.Buyer) || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return;

            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                var record = FindInternal(snapshot.Seller, snapshot.Buyer, snapshot.OrderId);
                if (record == null)
                {
                    record = NewRecord(snapshot);
                    _state.Records.Add(record);
                }
                else
                {
                    record.ObservedAt = DateTime.Now;
                    if (snapshot.EventTime > record.EventTime) record.EventTime = snapshot.EventTime;
                    MergeSnapshot(record.Snapshot, snapshot);
                }
                SaveInternal();
            }
        }

        public static bool IsExplicitBuyerFollowUp(string messageText)
        {
            var compact = Compact(messageText);
            if (compact.Length == 0 || compact.Length > 30) return false;
            if (NegativeFollowUpRegex.IsMatch(compact)) return false;
            return PositiveFollowUpRegex.IsMatch(compact);
        }

        /// <summary>
        /// Read-only projection of the latest strictly confirmed order for one seller/buyer.
        /// This is the supported query boundary for CommerceContext; callers receive a clone and
        /// therefore cannot mutate the order-guidance ledger or its delivery idempotency state.
        /// </summary>
        public static bool TryGetLatestOrderSnapshot(string seller, string buyer, out OrderSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return false;
            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                var record = FindLatestInternal(seller, buyer);
                if (record == null || record.Snapshot == null) return false;
                snapshot = CloneSnapshot(record.Snapshot);
                return snapshot != null;
            }
        }

        public static bool CanCreateFollowUp(
            string seller,
            string buyer,
            string triggerText,
            out OrderSnapshot snapshot,
            out string reason)
        {
            snapshot = null;
            reason = string.Empty;
            if (!IsExplicitBuyerFollowUp(triggerText))
            {
                reason = "不是明确的充值流程续问";
                return false;
            }

            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                var record = FindLatestInternal(seller, buyer);
                if (record == null || record.Snapshot == null)
                {
                    reason = "该买家没有经过严格确认的近期订单";
                    return false;
                }
                if (record.FollowUpDeliveredAt.HasValue)
                {
                    reason = "该订单已经补发过一次充值流程";
                    return false;
                }
                snapshot = CloneSnapshot(record.Snapshot);
                return true;
            }
        }

        public static bool ShouldSuppressBeforeSend(
            QN qn,
            OrderPlacedReplyPlan plan,
            string answer,
            out string reason)
        {
            reason = string.Empty;
            if (plan == null || string.IsNullOrWhiteSpace(plan.OrderId)) return false;

            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                var record = FindInternal(plan.Seller, plan.Buyer, plan.OrderId);
                if (record == null)
                {
                    record = NewRecord(plan.Snapshot, plan.Seller, plan.Buyer, plan.OrderId, plan.EventTime);
                    _state.Records.Add(record);
                }

                if (plan.IsBuyerFollowUp)
                {
                    if (record.FollowUpDeliveredAt.HasValue)
                    {
                        reason = "该订单已经按买家续问补发过一次充值流程";
                        return true;
                    }
                }
                else if (record.InitialDeliveredAt.HasValue)
                {
                    reason = "该订单的充值流程已经发送过一次（" + (record.InitialDeliveredBy ?? "已处理") + "）";
                    return true;
                }

                // The configured initial order preset often asks the buyer to photograph the
                // KuGou account/application screen. If a recent pre-order image has already been
                // analyzed as the official KuGou TV application, that requirement is satisfied.
                // Login state is intentionally irrelevant for identifying the official app UI.
                if (!plan.IsBuyerFollowUp)
                {
                    string visualEvidence;
                    if (RecentVisualContextService.TrySatisfyOrderPhotoRequirement(
                        plan.Seller,
                        plan.Buyer,
                        answer,
                        plan.EventTime,
                        out visualEvidence))
                    {
                        MarkDeliveredInternal(record, plan, "下单前图片已满足确认要求");
                        SaveInternal();
                        reason = "买家下单前已发送可确认的酷狗官方APP界面图片，Bot不再重复索要照片";
                        Log.Info("下单充值流程发送已取消：近期图片已经满足酷狗官方APP界面确认要求。seller="
                            + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                            + ", evidence=" + Short(visualEvidence, 160));
                        return true;
                    }
                }

                string matched;
                var since = plan.IsBuyerFollowUp
                    ? plan.TriggerTime.AddSeconds(-5)
                    : plan.EventTime.AddSeconds(-20);
                if (FindEquivalentSellerReply(plan.Seller, plan.Buyer, answer, since, out matched))
                {
                    MarkDeliveredInternal(record, plan, "人工客服/已有卖家消息");
                    SaveInternal();
                    reason = "检测到客服已经发送相同充值流程，Bot不再重复发送";
                    Log.Info("下单充值流程发送已取消：检测到已有卖家同类消息。seller="
                        + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", matched=" + Short(matched, 120));
                    return true;
                }
                return false;
            }
        }

        public static void MarkDelivered(OrderPlacedReplyPlan plan, string deliveredBy)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.OrderId)) return;
            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                var record = FindInternal(plan.Seller, plan.Buyer, plan.OrderId);
                if (record == null)
                {
                    record = NewRecord(plan.Snapshot, plan.Seller, plan.Buyer, plan.OrderId, plan.EventTime);
                    _state.Records.Add(record);
                }
                MarkDeliveredInternal(record, plan, deliveredBy);
                SaveInternal();
            }
        }

        private static OrderGuidanceRecord NewRecord(OrderSnapshot snapshot)
        {
            return NewRecord(
                snapshot,
                snapshot == null ? string.Empty : snapshot.Seller,
                snapshot == null ? string.Empty : snapshot.Buyer,
                snapshot == null ? string.Empty : snapshot.OrderId,
                snapshot == null ? DateTime.Now : snapshot.EventTime);
        }

        private static OrderGuidanceRecord NewRecord(
            OrderSnapshot snapshot,
            string seller,
            string buyer,
            string orderId,
            DateTime eventTime)
        {
            return new OrderGuidanceRecord
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = (orderId ?? string.Empty).Trim(),
                EventTime = eventTime,
                ObservedAt = DateTime.Now,
                Snapshot = CloneSnapshot(snapshot),
                InitialDeliveredBy = string.Empty,
                FollowUpDeliveredBy = string.Empty,
                FollowUpTriggerHash = string.Empty
            };
        }

        private static void MarkDeliveredInternal(OrderGuidanceRecord record, OrderPlacedReplyPlan plan, string source)
        {
            var now = DateTime.Now;
            source = string.IsNullOrWhiteSpace(source) ? "已发送" : source.Trim();
            if (plan.IsBuyerFollowUp)
            {
                record.FollowUpDeliveredAt = now;
                record.FollowUpDeliveredBy = source;
                record.FollowUpTriggerHash = Hash(Compact(plan.TriggerText));
                if (!record.InitialDeliveredAt.HasValue)
                {
                    record.InitialDeliveredAt = now;
                    record.InitialDeliveredBy = source + "（买家续问时发送）";
                }
            }
            else
            {
                record.InitialDeliveredAt = now;
                record.InitialDeliveredBy = source;
            }
            record.ObservedAt = now;
        }

        private static bool FindEquivalentSellerReply(
            string seller,
            string buyer,
            string expected,
            DateTime since,
            out string matched)
        {
            matched = string.Empty;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
            foreach (var turn in turns
                .Where(x => x != null && x.Role == "assistant" && !x.Withdrawn)
                .OrderByDescending(x => x.Timestamp))
            {
                if (turn.Timestamp != DateTime.MinValue && turn.Timestamp < since) continue;
                if (!EquivalentGuidance(turn.Text, expected)) continue;
                matched = turn.Text;
                return true;
            }
            return false;
        }

        internal static bool EquivalentGuidance(string left, string right)
        {
            var a = NormalizeGuidance(left);
            var b = NormalizeGuidance(right);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var min = Math.Min(a.Length, b.Length);
            var max = Math.Max(a.Length, b.Length);
            if (min >= 12 && (a.Contains(b) || b.Contains(a)) && min * 100 / max >= 65) return true;
            return HasGuidanceSignature(a) && HasGuidanceSignature(b);
        }

        private static bool HasGuidanceSignature(string value)
        {
            return value.Contains("酷狗账号")
                && (value.Contains("拍照") || value.Contains("截图") || value.Contains("发图"))
                && (value.Contains("确认") || value.Contains("支持") || value.Contains("使用"));
        }

        private static string NormalizeGuidance(string value)
        {
            value = (value ?? string.Empty)
                .Replace("[AI]", string.Empty)
                .Replace("【AI】", string.Empty)
                .Replace("亲", string.Empty)
                .ToLowerInvariant();
            return Regex.Replace(value, "[^a-z0-9\\u4e00-\\u9fff]", string.Empty);
        }

        private static string Compact(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim(),
                "[\\s，。！!、；;：:‘’“”\\\"']+",
                string.Empty);
        }

        private static OrderGuidanceRecord FindLatestInternal(string seller, string buyer)
        {
            return _state.Records
                .Where(x => x != null
                    && Same(x.Seller, seller)
                    && Same(x.Buyer, buyer)
                    && x.ObservedAt >= DateTime.Now.AddDays(-7))
                .OrderByDescending(x => x.EventTime)
                .ThenByDescending(x => x.ObservedAt)
                .FirstOrDefault();
        }

        private static OrderGuidanceRecord FindInternal(string seller, string buyer, string orderId)
        {
            return _state.Records.FirstOrDefault(x => x != null
                && Same(x.Seller, seller)
                && Same(x.Buyer, buyer)
                && string.Equals((x.OrderId ?? string.Empty).Trim(), (orderId ?? string.Empty).Trim(), StringComparison.Ordinal));
        }

        private static bool Same(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureLoaded()
        {
            if (_state != null) return;
            try
            {
                var path = GetPath();
                _state = File.Exists(path)
                    ? JsonConvert.DeserializeObject<OrderGuidanceState>(File.ReadAllText(path, Encoding.UTF8))
                    : new OrderGuidanceState();
                if (_state == null || _state.SchemaVersion != 1) _state = new OrderGuidanceState();
                if (_state.Records == null) _state.Records = new List<OrderGuidanceRecord>();
            }
            catch (Exception ex)
            {
                Log.Info("读取下单充值流程发送状态失败，使用空状态：" + ex.Message);
                _state = new OrderGuidanceState();
            }
        }

        private static void CleanupInternal()
        {
            var cutoff = DateTime.Now.AddDays(-30);
            _state.Records.RemoveAll(x => x == null || x.ObservedAt < cutoff);
            if (_state.Records.Count > 2000)
            {
                _state.Records = _state.Records.OrderByDescending(x => x.ObservedAt).Take(2000).ToList();
            }
        }

        private static void SaveInternal()
        {
            try
            {
                var path = GetPath();
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_state, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存下单充值流程发送状态失败：" + ex.Message, 10);
            }
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-guidance-delivery-state.json");
        }

        private static OrderSnapshot CloneSnapshot(OrderSnapshot source)
        {
            if (source == null) return null;
            return JsonConvert.DeserializeObject<OrderSnapshot>(JsonConvert.SerializeObject(source));
        }

        private static void MergeSnapshot(OrderSnapshot target, OrderSnapshot incoming)
        {
            if (target == null || incoming == null) return;
            if (string.IsNullOrWhiteSpace(target.ItemId)) target.ItemId = incoming.ItemId;
            if (string.IsNullOrWhiteSpace(target.ItemTitle)) target.ItemTitle = incoming.ItemTitle;
            if (string.IsNullOrWhiteSpace(target.SkuId)) target.SkuId = incoming.SkuId;
            if (string.IsNullOrWhiteSpace(target.SkuText)) target.SkuText = incoming.SkuText;
            if (target.Quantity <= 0) target.Quantity = incoming.Quantity;
            if (!target.TotalAmount.HasValue) target.TotalAmount = incoming.TotalAmount;
            if (!target.PaidAmount.HasValue) target.PaidAmount = incoming.PaidAmount;
            if (string.IsNullOrWhiteSpace(target.TradeStatus)) target.TradeStatus = incoming.TradeStatus;
            if (!target.IsPaid.HasValue) target.IsPaid = incoming.IsPaid;
            if (!target.CreatedAt.HasValue) target.CreatedAt = incoming.CreatedAt;
            if (!target.PaidAt.HasValue) target.PaidAt = incoming.PaidAt;
            if (incoming.EventTime > target.EventTime) target.EventTime = incoming.EventTime;
            if (incoming.EventType == OrderEventType.Paid) target.EventType = OrderEventType.Paid;
        }

        private static string Hash(string value)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
            catch { return string.Empty; }
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
