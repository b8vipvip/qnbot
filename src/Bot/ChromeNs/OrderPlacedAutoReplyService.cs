using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Async-flow scoped authority for a business side effect that has already been accepted by its
    /// own durable action ledger. This is intentionally separate from ResponseProgressTracker:
    /// UI/metrics state can disappear or be recreated and must never decide whether a business
    /// message keeps its send eligibility. The scope is inherited only by the exact async send call.
    /// </summary>
    internal static class ReliableSendAuthority
    {
        private sealed class ScopeState
        {
            public string Seller;
            public string Buyer;
            public string Text;
            public string Reason;
            public ScopeState Previous;
        }

        private sealed class ScopeLease : IDisposable
        {
            private readonly ScopeState _state;
            private readonly ScopeState _previous;
            private bool _disposed;

            public ScopeLease(ScopeState state, ScopeState previous)
            {
                _state = state;
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (ReferenceEquals(Current.Value, _state)) Current.Value = _previous;
            }
        }

        private static readonly AsyncLocal<ScopeState> Current = new AsyncLocal<ScopeState>();

        public static IDisposable BeginBusinessCritical(string seller, string buyer, string text, string reason)
        {
            var previous = Current.Value;
            var state = new ScopeState
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Text = (text ?? string.Empty).Trim(),
                Reason = (reason ?? string.Empty).Trim(),
                Previous = previous
            };
            Current.Value = state;
            return new ScopeLease(state, previous);
        }

        public static bool IsProtectedFromBuyerUpdate(string seller, string buyer, string text, out string reason)
        {
            reason = string.Empty;
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();

            // Text is intentionally diagnostic rather than the identity key: QN applies the
            // configured outbound suffix after the order layer opens this async scope. AsyncLocal
            // already constrains the authority to this one send call, while seller+buyer prevents a
            // nested send for another conversation from inheriting the privilege.
            for (var state = Current.Value; state != null; state = state.Previous)
            {
                if (!string.Equals(state.Seller, seller, StringComparison.Ordinal)) continue;
                if (!BuyerIdentityAliasService.AreEquivalent(seller, state.Buyer, buyer)) continue;
                reason = string.IsNullOrWhiteSpace(state.Reason) ? "business_critical" : state.Reason;
                return true;
            }
            return false;
        }
    }

    internal sealed class OrderPlacedReplyPlan
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public string EventText { get; set; }
        public DateTime EventTime { get; set; }
        public string ReservationKey { get; set; }
        public AutoReplyRuleConfig Config { get; set; }
        public OrderSnapshot Snapshot { get; set; }
        public bool IsBuyerFollowUp { get; set; }
        public string TriggerText { get; set; }
        public DateTime TriggerTime { get; set; }
    }

    internal sealed class OrderPlacedReplyResolution
    {
        public bool Success { get; set; }
        public string Reply { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
    }

    internal static class OrderPlacedAutoReplyService
    {
        private sealed class OrderReplyActionRecord
        {
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public string OrderId { get; set; }
            public bool FollowUp { get; set; }
            public DateTime Until { get; set; }
            public bool Delivered { get; set; }
            public bool DeliveryUncertain { get; set; }
            public bool InFlight { get; set; }
        }

        private sealed class OrderReplyActionState
        {
            public List<OrderReplyActionRecord> Records { get; set; }

            public OrderReplyActionState()
            {
                Records = new List<OrderReplyActionRecord>();
            }
        }

        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly object ActionSync = new object();
        private static readonly List<OrderReplyActionRecord> ActiveActions = new List<OrderReplyActionRecord>();
        private static OrderReplyActionState _actionState;

        public static bool TryCreatePlan(
            QNChatMessage message,
            string messageText,
            string seller,
            string buyer,
            DateTime botStartedAt,
            out OrderPlacedReplyPlan plan,
            string exactOrderIdHint = null)
        {
            plan = null;
            if (!Params.Robot.CanUseRobotReal) return false;

            string classificationReason;
            if (!OrderMessageClassifier.IsConfirmedOrderEvent(message, messageText, out classificationReason))
            {
                return TryCreateBuyerFollowUpPlan(messageText, seller, buyer, out plan);
            }

            OrderSnapshot snapshot;
            if (!OrderCardParser.TryParse(
                message,
                messageText,
                seller,
                buyer,
                "千牛消息/远端历史订单卡片",
                out snapshot))
            {
                return false;
            }

            var exactOrderId = Regex.Replace(exactOrderIdHint ?? string.Empty, @"\D", string.Empty);
            if (exactOrderId.Length >= 8 && exactOrderId.Length <= 40
                && !string.Equals(snapshot.OrderId, exactOrderId, StringComparison.Ordinal))
            {
                // Raw WebSocket digits outrank a parsed numeric token. Do this before publishing the
                // snapshot/reserving the action so a rounded ghost ID can never become a second order.
                Log.Info("订单号使用原始载荷精确字符串覆盖解析值: parsedOrderId=" + snapshot.OrderId
                    + ", exactOrderId=" + exactOrderId);
                snapshot.OrderId = exactOrderId;
            }
            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);
            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
            Log.Info("订单事件通过严格证据校验: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId
                + ", reason=" + classificationReason);

            if (snapshot.EventTime < botStartedAt.AddSeconds(-8))
            {
                Log.Info("订单事件已跳过历史卡片: orderId=" + snapshot.OrderId
                    + ", eventTime=" + snapshot.EventTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return true;
            }

            var published = OrderEventHub.Publish(snapshot);
            if (!published.Accepted)
            {
                return true;
            }

            if (snapshot.EventType == OrderEventType.Created || snapshot.EventType == OrderEventType.Paid)
            {
                var qn = QN.FindExistingBySellerNick(seller);
                if (qn != null)
                {
                    qn.EnqueueNewOrderAttention(snapshot);
                }
            }

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return true;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return true;

            var key = BuildReservationKey(seller, buyer, snapshot.OrderId, false);
            var now = DateTime.Now;
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > now)
            {
                Log.Info("下单自动消息已去重: orderId=" + snapshot.OrderId + ", buyer=" + buyer);
                return true;
            }

            var reserveMinutes = Math.Max(2, Math.Min(30, cfg.OrderPlacedApiTimeoutSeconds <= 0 ? 2 : cfg.OrderPlacedApiTimeoutSeconds / 2 + 2));
            Reservations[key] = now.AddMinutes(reserveMinutes);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = snapshot.OrderId,
                EventText = snapshot.EventText,
                EventTime = snapshot.EventTime,
                ReservationKey = key,
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = false,
                TriggerText = string.Empty,
                TriggerTime = DateTime.MinValue
            };
            Log.Info("下单自动回复规则已建立强制发送计划: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId
                + ", manualReplyDoesNotSuppress=true");
            return true;
        }

        private static bool TryCreateBuyerFollowUpPlan(
            string messageText,
            string seller,
            string buyer,
            out OrderPlacedReplyPlan plan)
        {
            plan = null;
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return false;

            OrderSnapshot snapshot;
            string reason;
            if (!OrderGuidanceDeliveryGuard.CanCreateFollowUp(seller, buyer, messageText, out snapshot, out reason))
                return false;

            ObserveCanonicalOrderId(seller, buyer, snapshot.OrderId);
            var trigger = (messageText ?? string.Empty).Trim();
            var key = BuildReservationKey(seller, buyer, snapshot.OrderId, true);
            DateTime until;
            if (Reservations.TryGetValue(key, out until) && until > DateTime.Now)
            {
                Log.Info("买家充值流程续问已去重: buyer=" + buyer + ", orderId=" + snapshot.OrderId);
                return true;
            }
            Reservations[key] = DateTime.Now.AddMinutes(5);
            plan = new OrderPlacedReplyPlan
            {
                Seller = (seller ?? string.Empty).Trim(), Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = snapshot.OrderId, EventText = trigger, EventTime = snapshot.EventTime,
                ReservationKey = key, Config = cfg, Snapshot = snapshot, IsBuyerFollowUp = true,
                TriggerText = trigger, TriggerTime = DateTime.Now
            };
            Log.Info("买家明确询问充值流程，允许额外补发一次: seller=" + seller
                + ", buyer=" + buyer + ", orderId=" + snapshot.OrderId + ", trigger=" + trigger);
            return true;
        }

        private static List<string> MissingTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var missing = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && (plan == null || string.IsNullOrWhiteSpace(plan.Seller))) missing.Add("seller");
            if (template.Contains("{买家}") && (plan == null || string.IsNullOrWhiteSpace(plan.Buyer))) missing.Add("buyer");
            if (template.Contains("{订单号}") && (plan == null || string.IsNullOrWhiteSpace(plan.OrderId))) missing.Add("order_id");
            if (template.Contains("{时间}") && (plan == null || plan.EventTime == DateTime.MinValue)) missing.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}")) && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText))) missing.Add("sku");
            if (template.Contains("{买家备注}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BuyerRemark))) missing.Add("buyer_remark");
            if (template.Contains("{数量}") && (snapshot == null || snapshot.Quantity <= 0)) missing.Add("quantity");
            if (template.Contains("{金额}") && (snapshot == null || !snapshot.TotalAmount.HasValue)) missing.Add("total");
            if (template.Contains("{实付}") && (snapshot == null || !snapshot.PaidAmount.HasValue)) missing.Add("paid");
            if (template.Contains("{商品}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ItemTitle))) missing.Add("item");
            if (template.Contains("{订单状态}") && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TradeStatus))) missing.Add("status");
            return missing;
        }

        private static List<string> PresentTemplateFields(string template, OrderPlacedReplyPlan plan)
        {
            var present = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            template = template ?? string.Empty;
            if (template.Contains("{客服}") && plan != null && !string.IsNullOrWhiteSpace(plan.Seller)) present.Add("seller");
            if (template.Contains("{买家}") && plan != null && !string.IsNullOrWhiteSpace(plan.Buyer)) present.Add("buyer");
            if (template.Contains("{订单号}") && plan != null && !string.IsNullOrWhiteSpace(plan.OrderId)) present.Add("order_id");
            if (template.Contains("{时间}") && plan != null && plan.EventTime != DateTime.MinValue) present.Add("event_time");
            if ((template.Contains("{sku}") || template.Contains("{规格}")) && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{买家备注}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BuyerRemark)) present.Add("buyer_remark");
            if (template.Contains("{数量}") && snapshot != null && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{金额}") && snapshot != null && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{实付}") && snapshot != null && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{商品}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static List<string> BuildRenderMissingReasons(IList<string> missing, OrderPlacedReplyPlan plan)
        {
            var reasons = new List<string>();
            var snapshot = plan == null ? null : plan.Snapshot;
            foreach (var field in missing ?? new List<string>())
            {
                string reason;
                if (plan == null) reason = "plan_null";
                else if (snapshot == null && field != "seller" && field != "buyer" && field != "order_id" && field != "event_time") reason = "snapshot_null";
                else
                {
                    switch (field)
                    {
                        case "seller": reason = "seller_empty"; break;
                        case "buyer": reason = "buyer_empty"; break;
                        case "order_id": reason = "order_id_empty"; break;
                        case "event_time": reason = "event_time_min_value"; break;
                        case "sku": reason = "snapshot_sku_empty"; break;
                        case "buyer_remark": reason = "snapshot_buyer_remark_empty"; break;
                        case "quantity": reason = "snapshot_quantity_zero"; break;
                        case "total": reason = "snapshot_total_amount_null"; break;
                        case "paid": reason = "snapshot_paid_amount_null"; break;
                        case "item": reason = "snapshot_item_title_empty"; break;
                        case "status": reason = "snapshot_trade_status_empty"; break;
                        default: reason = "field_unavailable"; break;
                    }
                }
                reasons.Add(field + ":" + reason);
            }
            return reasons;
        }

        public static async Task<OrderPlacedReplyResolution> ResolveAsync(OrderPlacedReplyPlan plan)
        {
            if (plan == null || plan.Config == null) return Fail("下单自动回复计划为空");
            var cfg = plan.Config;
            await RefreshLocalSnapshotBeforeRenderAsync(plan, cfg.OrderPlacedReplyText);
            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode) ? "固定预设答案" : cfg.OrderPlacedReplyMode.Trim();
            if (string.Equals(mode, "调用HTTP接口", StringComparison.Ordinal))
            {
                var api = await CallReplyApiAsync(plan);
                if (api.Success)
                {
                    if (plan.IsBuyerFollowUp) api.Source += "（买家明确续问）";
                    return api;
                }
                var fallback = RenderTemplate(cfg.OrderPlacedReplyText, plan, "http-fallback");
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    Log.Info("下单回复接口失败，使用固定预设兜底: orderId=" + plan.OrderId + ", error=" + api.Error);
                    return new OrderPlacedReplyResolution { Success = true, Reply = fallback, Source = plan.IsBuyerFollowUp ? "下单自动回复-接口失败兜底（买家明确续问）" : "下单自动回复-接口失败兜底" };
                }
                return api;
            }
            var reply = RenderTemplate(cfg.OrderPlacedReplyText, plan, "fixed-preset");
            if (string.IsNullOrWhiteSpace(reply)) return Fail("下单固定预设答案为空");
            return new OrderPlacedReplyResolution { Success = true, Reply = reply, Source = plan.IsBuyerFollowUp ? "下单自动回复-固定预设（买家明确续问）" : "下单自动回复-固定预设" };
        }

        private static async Task RefreshLocalSnapshotBeforeRenderAsync(OrderPlacedReplyPlan plan, string template)
        {
            if (plan == null || plan.Snapshot == null || string.IsNullOrWhiteSpace(plan.OrderId)) return;
            var missingBefore = MissingTemplateFields(template, plan);
            if (missingBefore.Count > 0)
            {
                // Same-process parsers often publish a richer snapshot a few milliseconds after the
                // first confirmed order event. Allow only a tiny local settle; never wait on trade API.
                await Task.Delay(120).ConfigureAwait(false);
            }
            var refreshed = OrderEventHub.RefreshFromCanonical(plan.Snapshot);
            if (refreshed != null) plan.Snapshot = refreshed;
            var missingAfter = MissingTemplateFields(template, plan);
            Log.Info("订单固定预设渲染前已刷新Hub本地快照: orderId=" + plan.OrderId
                + ", missingBefore=" + string.Join(",", missingBefore)
                + ", missingAfter=" + string.Join(",", missingAfter)
                + ", quantity=" + (plan.Snapshot == null ? 0 : plan.Snapshot.Quantity)
                + ", paid=" + (plan.Snapshot == null || !plan.Snapshot.PaidAmount.HasValue ? "" : plan.Snapshot.PaidAmount.Value.ToString("0.##"))
                + ", skuPresent=" + (plan.Snapshot != null && !string.IsNullOrWhiteSpace(plan.Snapshot.SkuText)));
        }

        public static void Complete(OrderPlacedReplyPlan plan, bool delivered)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.ReservationKey)) return;
            if (!delivered)
            {
                DateTime ignored;
                Reservations.TryRemove(plan.ReservationKey, out ignored);
                return;
            }
            var hours = plan.IsBuyerFollowUp ? 720 : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
            Reservations[plan.ReservationKey] = DateTime.Now.AddHours(hours);
        }

        internal static bool TryBeginExecution(OrderPlacedReplyPlan plan, out string reason)
        {
            reason = string.Empty;
            if (plan == null || string.IsNullOrWhiteSpace(plan.Seller)
                || string.IsNullOrWhiteSpace(plan.Buyer) || string.IsNullOrWhiteSpace(plan.OrderId))
            {
                reason = "invalid_plan";
                return false;
            }

            lock (ActionSync)
            {
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        reason = "action_state_lock_timeout";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：无法取得跨进程动作状态锁，避免多实例重复发送。", 20);
                        return false;
                    }

                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        reason = "action_state_unavailable";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：动作级持久状态不可可靠读取，禁止把未知状态当空状态发送。 error="
                            + Short(reloadError, 220), 20);
                        return false;
                    }
                    var now = DateTime.Now;
                    ActiveActions.RemoveAll(x => x == null || x.Until <= now);
                    _actionState.Records.RemoveAll(x => x == null || x.Until <= now);

                    var canonical = FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId);
                    if (!string.IsNullOrWhiteSpace(canonical)
                        && !string.Equals(canonical, plan.OrderId, StringComparison.Ordinal))
                    {
                        Log.Info("订单号精度别名已归一化: orderId=" + plan.OrderId + ", canonicalOrderId=" + canonical);
                        plan.OrderId = canonical;
                        if (plan.Snapshot != null) plan.Snapshot.OrderId = canonical;
                        plan.ReservationKey = BuildReservationKey(plan.Seller, plan.Buyer, canonical, plan.IsBuyerFollowUp);
                    }

                    if (IsSuspiciousRoundedOrderId(plan.OrderId)
                        && string.IsNullOrWhiteSpace(FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId, true)))
                    {
                        reason = "precision_risk_order_id";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：检测到疑似 JavaScript Number 精度损失的长订单号，等待精确字符串订单事件补偿。 orderId="
                            + plan.OrderId, 50);
                        return false;
                    }

                    if (ActiveActions.Any(x => SameAction(x, plan)))
                    {
                        reason = "action_inflight";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.Delivered && SameAction(x, plan)))
                    {
                        reason = "action_already_delivered";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.DeliveryUncertain && SameAction(x, plan)))
                    {
                        reason = "action_delivery_uncertain";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.InFlight && x.Until > now && SameAction(x, plan)))
                    {
                        reason = "action_inflight_cross_process";
                        return false;
                    }

                    var durable = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (durable == null)
                    {
                        durable = new OrderReplyActionRecord();
                        _actionState.Records.Add(durable);
                    }
                    durable.Seller = Normalize(plan.Seller);
                    durable.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                    durable.OrderId = plan.OrderId.Trim();
                    durable.FollowUp = plan.IsBuyerFollowUp;
                    durable.Until = now.AddMinutes(10);
                    durable.Delivered = false;
                    durable.DeliveryUncertain = false;
                    durable.InFlight = true;

                    ActiveActions.Add(new OrderReplyActionRecord
                    {
                        Seller = durable.Seller,
                        Buyer = durable.Buyer,
                        OrderId = durable.OrderId,
                        FollowUp = durable.FollowUp,
                        Until = durable.Until,
                        Delivered = false,
                        DeliveryUncertain = false,
                        InFlight = true
                    });

                    if (!SaveActionStateLocked(path))
                    {
                        ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                        durable.InFlight = false;
                        reason = "action_state_persist_failed";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：动作级in-flight状态无法原子持久化，避免多实例重复发送。", 20);
                        return false;
                    }
                    return true;
                }
            }
        }

        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("记录订单发送不确定状态时跨进程锁超时；保留既有durable in-flight窗口以防重复。", 20);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录订单发送不确定状态时无法可靠读取动作ledger；保留磁盘中既有in-flight安全窗口。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (existing == null)
                    {
                        existing = new OrderReplyActionRecord();
                        _actionState.Records.Add(existing);
                    }
                    existing.Seller = Normalize(plan.Seller);
                    existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                    existing.OrderId = (plan.OrderId ?? string.Empty).Trim();
                    existing.FollowUp = plan.IsBuyerFollowUp;
                    existing.Until = DateTime.Now.AddMinutes(10);
                    existing.Delivered = false;
                    existing.DeliveryUncertain = true;
                    existing.InFlight = false;
                    SaveActionStateLocked(path);
                }
            }
            Log.ErrorWithMaxCount(
                "订单发送状态不确定，10分钟内禁止自动重发以避免重复: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                + ", reason=" + (reason ?? string.Empty),
                20);
        }

        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("完成订单动作时跨进程锁超时；durable in-flight将按10分钟安全窗口自然过期。", 20);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("完成订单动作时无法可靠读取动作ledger；不覆盖磁盘状态，让既有in-flight安全窗口自然过期。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (existing != null) existing.InFlight = false;

                    if (delivered || sentSegments > 0)
                    {
                        var now = DateTime.Now;
                        var hours = plan.IsBuyerFollowUp
                            ? 720
                            : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
                        var until = delivered ? now.AddHours(hours) : now.AddMinutes(10);
                        if (existing == null)
                        {
                            existing = new OrderReplyActionRecord();
                            _actionState.Records.Add(existing);
                        }
                        existing.Seller = Normalize(plan.Seller);
                        existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                        existing.OrderId = plan.OrderId.Trim();
                        existing.FollowUp = plan.IsBuyerFollowUp;
                        existing.Until = until;
                        existing.Delivered = delivered || sentSegments > 0;
                        existing.DeliveryUncertain = false;
                        existing.InFlight = false;
                    }
                    _actionState.Records.RemoveAll(x => x == null || x.Until <= DateTime.Now);
                    SaveActionStateLocked(path);
                }
            }
        }

        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)
        {
            orderId = (orderId ?? string.Empty).Trim();
            if (orderId.Length < 8 || IsSuspiciousRoundedOrderId(orderId)) return;
            lock (ActionSync)
            {
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("记录精确订单号时跨进程动作状态锁超时，本次仅跳过持久化观察。 orderId=" + orderId, 10);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录精确订单号时无法可靠读取动作ledger，本次跳过持久化观察。 error="
                            + Short(reloadError, 220), 10);
                        return;
                    }
                    var exists = _actionState.Records.Any(x => x != null
                        && !x.FollowUp
                        && Normalize(x.Seller) == Normalize(seller)
                        && NormalizeBuyer(x.Seller, x.Buyer) == NormalizeBuyer(seller, buyer)
                        && string.Equals(x.OrderId, orderId, StringComparison.Ordinal));
                    if (!exists)
                    {
                        _actionState.Records.Add(new OrderReplyActionRecord
                        {
                            Seller = Normalize(seller),
                            Buyer = NormalizeBuyer(seller, buyer),
                            OrderId = orderId,
                            FollowUp = false,
                            Until = DateTime.Now.AddHours(2),
                            Delivered = false,
                            DeliveryUncertain = false,
                            InFlight = false
                        });
                        SaveActionStateLocked(path);
                    }
                }
            }
        }

        private static string FindCanonicalOrderIdLocked(string seller, string buyer, string orderId, bool requireExactCandidate = false)
        {
            var normalizedSeller = Normalize(seller);
            var normalizedBuyer = NormalizeBuyer(seller, buyer);
            orderId = (orderId ?? string.Empty).Trim();
            var candidates = ActiveActions.Concat(_actionState == null ? new List<OrderReplyActionRecord>() : _actionState.Records)
                .Where(x => x != null
                    && Normalize(x.Seller) == normalizedSeller
                    && NormalizeBuyer(x.Seller, x.Buyer) == normalizedBuyer
                    && !string.IsNullOrWhiteSpace(x.OrderId))
                .Select(x => x.OrderId.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var exact = candidates.FirstOrDefault(x => string.Equals(x, orderId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(exact) && !requireExactCandidate) return exact;
            return candidates.FirstOrDefault(x => !IsSuspiciousRoundedOrderId(x) && ArePrecisionAliases(x, orderId)) ?? string.Empty;
        }

        private static bool SameAction(OrderReplyActionRecord record, OrderPlacedReplyPlan plan)
        {
            if (record == null || plan == null) return false;
            return record.FollowUp == plan.IsBuyerFollowUp
                && Normalize(record.Seller) == Normalize(plan.Seller)
                && NormalizeBuyer(record.Seller, record.Buyer) == NormalizeBuyer(plan.Seller, plan.Buyer)
                && (string.Equals((record.OrderId ?? string.Empty).Trim(), (plan.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || ArePrecisionAliases(record.OrderId, plan.OrderId));
        }

        internal static bool ArePrecisionAliases(string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (left.Length < 16 || right.Length != left.Length) return false;
            if (!Regex.IsMatch(left, @"^\d+$") || !Regex.IsMatch(right, @"^\d+$")) return false;
            if (!IsSuspiciousRoundedOrderId(left) && !IsSuspiciousRoundedOrderId(right)) return false;
            ulong a;
            ulong b;
            if (!ulong.TryParse(left, out a) || !ulong.TryParse(right, out b)) return false;
            var delta = a >= b ? a - b : b - a;
            return delta > 0 && delta <= 4096UL;
        }

        internal static bool IsSuspiciousRoundedOrderId(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length >= 16
                && Regex.IsMatch(value, @"^\d+$")
                && Regex.IsMatch(value, @"0{3,}$");
        }

        private static string BuildReservationKey(string seller, string buyer, string orderId, bool followUp)
        {
            return Normalize(seller) + "#" + NormalizeBuyer(seller, buyer) + "#" + (orderId ?? string.Empty).Trim()
                + (followUp ? "#guidance-followup" : string.Empty);
        }

        private static void EnsureActionStateLoadedLocked()
        {
            if (_actionState != null) return;
            OrderReplyActionState disk;
            string error;
            if (TryReadActionStateFromDiskLocked(GetActionStatePath(), out disk, out error))
            {
                _actionState = disk;
                return;
            }
            // Compatibility-only observation loader. The actual send reservation path always calls
            // ReloadAndMergeActionStateLocked and fails closed on any read/parse uncertainty.
            Log.ErrorWithMaxCount("初始化订单自动回复动作状态失败；发送路径将继续保持fail-closed。 error="
                + Short(error, 220), 10);
            _actionState = new OrderReplyActionState();
        }

        private static bool TryReadActionStateFromDiskLocked(
            string path,
            out OrderReplyActionState state,
            out string error)
        {
            state = new OrderReplyActionState();
            error = string.Empty;
            string readError;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 4, 60, out readError);
            if (!string.IsNullOrWhiteSpace(readError))
            {
                error = "read_failed: " + readError;
                return false;
            }
            if (string.IsNullOrWhiteSpace(raw)) return true;
            try
            {
                state = JsonConvert.DeserializeObject<OrderReplyActionState>(raw) ?? new OrderReplyActionState();
                if (state.Records == null) state.Records = new List<OrderReplyActionRecord>();
                return true;
            }
            catch (Exception ex)
            {
                state = new OrderReplyActionState();
                error = "parse_failed: " + ex.Message;
                return false;
            }
        }

        private static bool ReloadAndMergeActionStateLocked(string path, out string error)
        {
            OrderReplyActionState disk;
            if (!TryReadActionStateFromDiskLocked(path, out disk, out error)) return false;
            if (_actionState == null || _actionState.Records == null || _actionState.Records.Count == 0)
            {
                _actionState = disk;
                return true;
            }
            if (disk.Records == null) disk.Records = new List<OrderReplyActionRecord>();
            foreach (var local in _actionState.Records.Where(x => x != null))
            {
                var existing = disk.Records.FirstOrDefault(x => SameStoredAction(x, local));
                if (existing == null)
                {
                    disk.Records.Add(local);
                    continue;
                }
                if (local.Until > existing.Until) existing.Until = local.Until;
                existing.Delivered = existing.Delivered || local.Delivered;
                existing.DeliveryUncertain = !existing.Delivered
                    && (existing.DeliveryUncertain || local.DeliveryUncertain);
                existing.InFlight = !existing.Delivered
                    && (existing.InFlight || local.InFlight);
                if (IsSuspiciousRoundedOrderId(existing.OrderId) && !IsSuspiciousRoundedOrderId(local.OrderId))
                    existing.OrderId = local.OrderId;
            }
            _actionState = disk;
            return true;
        }

        private static bool SameStoredAction(OrderReplyActionRecord left, OrderReplyActionRecord right)
        {
            if (left == null || right == null || left.FollowUp != right.FollowUp) return false;
            return Normalize(left.Seller) == Normalize(right.Seller)
                && Normalize(left.Buyer) == Normalize(right.Buyer)
                && (string.Equals((left.OrderId ?? string.Empty).Trim(), (right.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || ArePrecisionAliases(left.OrderId, right.OrderId));
        }

        private static bool SaveActionStateLocked(string path)
        {
            if (_actionState == null) return true;
            string error;
            var ok = CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path,
                JsonConvert.SerializeObject(_actionState, Formatting.Indented),
                4,
                60,
                out error);
            if (!ok)
            {
                Log.ErrorWithMaxCount("保存订单自动回复动作幂等状态失败；旧有效文件已保留：" + Short(error, 220), 10);
            }
            return ok;
        }

        private static string GetActionStatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-reply-action-state.json");
        }

        private static async Task<OrderPlacedReplyResolution> CallReplyApiAsync(OrderPlacedReplyPlan plan)
        {
            Uri uri;
            if (!Uri.TryCreate((plan.Config.OrderPlacedApiUrl ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return Fail("下单回复接口地址无效");
            var timeout = Math.Max(3, Math.Min(60, plan.Config.OrderPlacedApiTimeoutSeconds));
            var snapshot = plan.Snapshot;
            var payload = new JObject
            {
                ["event"] = plan.IsBuyerFollowUp ? "buyer_order_guidance_followup" : (snapshot != null && snapshot.EventType == OrderEventType.Paid ? "buyer_order_paid" : "buyer_order_created"),
                ["seller"] = plan.Seller, ["buyer"] = plan.Buyer, ["orderId"] = plan.OrderId,
                ["eventTime"] = plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"), ["message"] = Short(plan.EventText, 1200),
                ["buyerFollowUp"] = plan.IsBuyerFollowUp, ["triggerText"] = plan.TriggerText ?? string.Empty
            };
            if (snapshot != null)
            {
                payload["itemId"] = snapshot.ItemId ?? string.Empty; payload["itemTitle"] = snapshot.ItemTitle ?? string.Empty;
                payload["skuId"] = snapshot.SkuId ?? string.Empty; payload["skuText"] = snapshot.SkuText ?? string.Empty;
                payload["quantity"] = snapshot.Quantity;
                payload["totalAmount"] = snapshot.TotalAmount.HasValue ? (JToken)snapshot.TotalAmount.Value : JValue.CreateNull();
                payload["paidAmount"] = snapshot.PaidAmount.HasValue ? (JToken)snapshot.PaidAmount.Value : JValue.CreateNull();
                payload["tradeStatus"] = snapshot.TradeStatus ?? string.Empty;
                payload["isPaid"] = snapshot.IsPaid.HasValue ? (JToken)snapshot.IsPaid.Value : JValue.CreateNull();
                payload["productUrl"] = snapshot.ProductUrl ?? string.Empty;
            }
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) })
                {
                    var token = (plan.Config.OrderPlacedApiToken ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"))
                    using (var response = await http.PostAsync(uri, content))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) return Fail("HTTP " + (int)response.StatusCode + " " + Short(body, 300));
                        var reply = ExtractReply(body);
                        if (string.IsNullOrWhiteSpace(reply)) return Fail("接口成功但未返回 reply/answer/message");
                        return new OrderPlacedReplyResolution { Success = true, Reply = RenderTemplate(reply, plan, "http-response"), Source = "下单自动回复-HTTP接口" };
                    }
                }
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private static string ExtractReply(string body)
        {
            body = (body ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var token = JToken.Parse(body);
                var reply = token["reply"] ?? token["answer"] ?? token["message"] ?? token["data"]?["reply"] ?? token["data"]?["answer"] ?? token["data"]?["message"];
                return reply == null ? string.Empty : reply.ToString().Trim();
            }
            catch { return body.Length <= 1000 ? body : string.Empty; }
        }

        private static string RenderTemplate(string template, OrderPlacedReplyPlan plan, string source)
        {
            var snapshot = plan == null ? null : plan.Snapshot;
            var missing = MissingTemplateFields(template, plan);
            var present = PresentTemplateFields(template, plan);
            var missingReasons = BuildRenderMissingReasons(missing, plan);
            var rendered = (template ?? string.Empty)
                .Replace("{客服}", plan == null ? string.Empty : plan.Seller ?? string.Empty)
                .Replace("{买家}", plan == null ? string.Empty : plan.Buyer ?? string.Empty)
                .Replace("{订单号}", plan == null ? string.Empty : plan.OrderId ?? string.Empty)
                .Replace("{时间}", plan == null || plan.EventTime == DateTime.MinValue ? string.Empty : plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{商品}", snapshot == null ? string.Empty : snapshot.ItemTitle ?? string.Empty)
                .Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{规格}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)
                .Replace("{买家备注}", snapshot == null ? string.Empty : snapshot.BuyerRemark ?? string.Empty)
                .Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())
                .Replace("{金额}", snapshot == null || !snapshot.TotalAmount.HasValue ? string.Empty : snapshot.TotalAmount.Value.ToString("0.00"))
                .Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))
                .Replace("{订单状态}", FormatTradeStatusForTemplate(snapshot));
            var allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0;
            Log.Info("order_template_render source=" + source + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " partial=" + (missing.Count > 0 && present.Count > 0).ToString().ToLowerInvariant()
                + " all_requested_fields_missing=" + allRequestedFieldsMissing.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present) + " missing=" + string.Join(",", missing)
                + " missing_reason=" + string.Join("|", missingReasons)
                + " snapshot_source=" + Short(snapshot == null ? string.Empty : snapshot.Source, 100)
                + " rendered_length=" + rendered.Length);
            return allRequestedFieldsMissing ? string.Empty : rendered;
        }

        private static string FormatTradeStatusForTemplate(OrderSnapshot snapshot)
{
    if (snapshot == null) return string.Empty;
    var raw = (snapshot.TradeStatus ?? string.Empty).Trim();
    var key = Regex.Replace(raw, @"[\s_-]", string.Empty).ToLowerInvariant();
    switch (key)
    {
        case "tradebuyerpay":
        case "waitsellersendgoods":
            return "已付款";
        case "waitbuyerpay":
        case "tradenocreatepay":
            return "待付款";
        case "waitbuyerconfirmgoods":
        case "sellerconsignedpart":
            return "已发货";
        case "tradefinished":
        case "tradesuccess":
            return "交易完成";
        case "tradeclosed":
        case "tradeclosedbytaobao":
            return "已关闭";
    }
    if (snapshot.EventType == OrderEventType.Paid || snapshot.IsPaid == true) return "已付款";
    if (snapshot.EventType == OrderEventType.RefundRequested) return "退款中";
    if (snapshot.EventType == OrderEventType.Closed) return "已关闭";
    if (snapshot.EventType == OrderEventType.Created) return "已下单";
    return raw;
}

        private static OrderPlacedReplyResolution Fail(string error) { return new OrderPlacedReplyResolution { Success = false, Error = Short(error, 500) }; }
        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty); }
        private static string NormalizeBuyer(string seller, string buyer)
        {
            var canonical = BuyerIdentityAliasService.ResolveInternalNick(
                (seller ?? string.Empty).Trim(),
                (buyer ?? string.Empty).Trim());
            return Normalize(string.IsNullOrWhiteSpace(canonical) ? buyer : canonical);
        }
        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    public partial class QN
    {
        private const string OrderPresetSegmentToken = "{分段符}";

        private sealed class OrderPresetSendResult
        {
            public bool Success { get; set; }
            public int SentSegments { get; set; }
            public int SatisfiedSegments { get; set; }
        }

        private static List<string> SplitOrderPresetSegments(string answer)
        {
            var result = new List<string>();
            foreach (var part in (answer ?? string.Empty).Split(new[] { OrderPresetSegmentToken }, StringSplitOptions.None))
                if (!string.IsNullOrWhiteSpace(part)) result.Add(part);
            return result;
        }

        private async Task<bool> SendMandatoryOrderTextAsync(OrderPlacedReplyPlan plan, string text)
        {
            if (plan == null || string.IsNullOrWhiteSpace(text)) return false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // The generic chat path intentionally yields to a human agent. Configured order
                // business rules are different: once a Created/Paid event has reserved a plan,
                // manual replies must never consume or cancel this configured message.
                var sendStartedAt = DateTime.Now;
                bool sent;
                using (ReliableSendAuthority.BeginBusinessCritical(
                    plan.Seller,
                    plan.Buyer,
                    text,
                    plan.IsBuyerFollowUp ? "order_followup_action_ledger" : "order_action_ledger"))
                {
                    KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);
                    sent = await SendTextWithRetryAsync(plan.Buyer, text, 0);
                }
                if (sent) return true;

                // Live seller echo can be lost when the authoritative CDP page reconnects. Before
                // retrying a mandatory order message, query the verified buyer conversation history.
                // This prevents a false-negative live echo from becoming a duplicate customer send.
                var remote = await VerifySellerEchoInRemoteHistoryAsync(
                    plan.Seller,
                    plan.Buyer,
                    text,
                    sendStartedAt).ConfigureAwait(false);
                if (remote == RemoteSellerEchoVerification.Delivered)
                {
                    Log.Info("订单发送已由远端历史二次确认，取消自动重试: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                    return true;
                }
                if (remote == RemoteSellerEchoVerification.Unavailable)
                {
                    OrderPlacedAutoReplyService.MarkDeliveryUncertain(
                        plan,
                        "live_echo_missing_and_remote_history_unavailable");
                    return false;
                }

                if (attempt == 0)
                {
                    Log.Info("强制订单规则发送失败且远端历史确认未送达，准备单次安全重试: seller="
                        + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", attempt=1");
                    await Task.Delay(180).ConfigureAwait(false);
                }
            }
            return false;
        }

        private async Task<bool> IsOrderPresetSegmentAlreadySatisfiedAsync(OrderPlacedReplyPlan plan, string text)
        {
            if (plan == null || string.IsNullOrWhiteSpace(text)) return false;
            var expected = BotOutboundMessageFormatter.StripAiMarker(text).Trim();
            if (expected.Length == 0) return false;
            var since = plan.IsBuyerFollowUp && plan.TriggerTime != DateTime.MinValue
                ? plan.TriggerTime.AddSeconds(-5)
                : plan.EventTime.AddSeconds(-20);

            if (HasRecentSellerEcho(plan.Buyer, expected, since))
            {
                Log.Info("下单固定预设分段已由人工/现有卖家实时回显精确满足，跳过本段但继续后续分段: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                return true;
            }

            var remote = await VerifySellerEchoInRemoteHistoryAsync(
                plan.Seller,
                plan.Buyer,
                expected,
                since).ConfigureAwait(false);
            if (remote == RemoteSellerEchoVerification.Delivered)
            {
                Log.Info("下单固定预设分段已由远端卖家历史精确满足，跳过本段但继续后续分段: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                return true;
            }
            if (remote == RemoteSellerEchoVerification.Unavailable)
            {
                Log.Info("下单固定预设分段发送前远端历史不可用；没有精确已满足证据，继续执行配置的订单动作: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
            }
            return false;
        }

        private async Task<OrderPresetSendResult> SendOrderPresetAnswerAsync(OrderPlacedReplyPlan plan, string answer)
        {
            var result = new OrderPresetSendResult();
            var segments = SplitOrderPresetSegments(answer);
            if (segments.Count == 0) return result;
            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) await Task.Delay(220);
                if (await IsOrderPresetSegmentAlreadySatisfiedAsync(plan, segments[i]).ConfigureAwait(false))
                {
                    result.SatisfiedSegments++;
                    continue;
                }
                Log.Info("下单固定预设分段强制自动发送: buyer=" + plan.Buyer
                    + ", segment=" + (i + 1) + "/" + segments.Count
                    + ", manualReplyDoesNotSuppress=true, exactSellerEchoSatisfied=false");
                if (!await SendMandatoryOrderTextAsync(plan, segments[i]))
                {
                    result.Success = false;
                    return result;
                }
                result.SentSegments++;
            }
            result.Success = result.SentSegments + result.SatisfiedSegments == segments.Count;
            Log.Info("下单固定预设分段动作完成: buyer=" + plan.Buyer
                + ", orderId=" + plan.OrderId
                + ", botSentSegments=" + result.SentSegments
                + ", exactSellerEchoSatisfiedSegments=" + result.SatisfiedSegments
                + ", totalSegments=" + segments.Count);
            return result;
        }

        private async Task ProcessOrderPlacedReplyAsync(OrderPlacedReplyPlan plan)
        {
            string actionReason;
            if (!OrderPlacedAutoReplyService.TryBeginExecution(plan, out actionReason))
            {
                if (plan != null)
                {
                    // Only a durably delivered action may extend the normal long reservation.
                    // In-flight/precision-risk/uncertain outcomes are not delivery success. In
                    // particular, delivery-uncertain has its own 10-minute durable safety window;
                    // converting it to Complete(true) here would suppress a legitimate retry for
                    // the full order dedup period (often 24h).
                    if (string.Equals(actionReason, "action_already_delivered", StringComparison.Ordinal))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, true);
                    }
                    else if (!string.Equals(actionReason, "action_inflight", StringComparison.Ordinal))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                    }
                    Log.Info("下单自动回复动作级幂等已阻止重复执行: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", reason=" + actionReason);
                }
                return;
            }

            using (BotActivityCoordinator.Begin("下单自动回复", plan == null ? string.Empty : plan.Seller, plan == null ? string.Empty : plan.Buyer))
            {
                try
                {
                    var resolution = await OrderPlacedAutoReplyService.ResolveAsync(plan);
                    if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.Reply))
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                        OrderAttentionUiService.SetReplyResult(plan == null ? null : plan.Snapshot, false);
                        var note = "下单自动回复未发送：" + (string.IsNullOrWhiteSpace(resolution.Error) ? "未生成回复" : resolution.Error);
                        AddSkippedConversation(plan.Seller, plan.Buyer, BuildPlanQuestion(plan), note);
                        Log.Info(note + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                        return;
                    }

                    var preserveTemplateLayout = !string.IsNullOrWhiteSpace(resolution.Source)
                        && (resolution.Source.IndexOf("固定预设", StringComparison.Ordinal) >= 0
                            || resolution.Source.IndexOf("接口失败兜底", StringComparison.Ordinal) >= 0);
                    var rawReply = resolution.Reply ?? string.Empty;
                    var answer = preserveTemplateLayout
                        ? (Regex.IsMatch(rawReply, @"(?:\[AI\]|【AI】|［AI］)\s*$", RegexOptions.IgnoreCase) ? rawReply : rawReply + " [AI]")
                        : BotOutboundMessageFormatter.EnsureAiMarker(BotFeatureStore.ApplyOutputPolicy(rawReply));

                    var autoSend = Params.Robot.GetIsAutoReply();
                    KnowledgeLearningService.RegisterAnswerSource(
                        plan.Seller, plan.Buyer, BuildPlanQuestion(plan),
                        BotOutboundMessageFormatter.StripAiMarker(answer), resolution.Source);
                    var ctl = Desk.Inst == null ? null : Desk.Inst.AddConversation(
                        plan.Seller, plan.Buyer, BuildPlanQuestion(plan), answer, autoSend, resolution.Source);

                    if (!autoSend)
                    {
                        OrderPlacedAutoReplyService.Complete(plan, false);
                        OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                        if (ctl != null) ctl.SetSendResult(false, "未发送：自动回复开关已关闭");
                        return;
                    }

                    var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();
                    if (delaySeconds > 0)
                    {
                        Log.Info("下单自动回复等待延时发送: seller=" + plan.Seller
                            + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                            + ", delaySeconds=" + delaySeconds + ", manualReplyDoesNotSuppress=true");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                        if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                        {
                            OrderPlacedAutoReplyService.Complete(plan, false);
                            OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                            if (ctl != null) ctl.SetSendResult(false, "未发送：延时期间 Bot 或自动回复开关已关闭");
                            return;
                        }
                    }

                    // EventHub is lifecycle-event dedupe; the action ledger above is the source of truth
                    // for the business side effect. Created -> Paid may update the same order snapshot,
                    // but can never execute the configured initial order reply twice.
                    OrderPresetSendResult presetSendResult = null;
                    bool sendOk;
                    if (preserveTemplateLayout)
                    {
                        presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);
                        sendOk = presetSendResult.Success;
                    }
                    else
                    {
                        sendOk = await SendMandatoryOrderTextAsync(plan, answer);
                    }

                    OrderPlacedAutoReplyService.Complete(plan, sendOk);
                    OrderAttentionUiService.SetReplyResult(plan.Snapshot, sendOk);
                    if (sendOk)
                    {
                        OrderGuidanceDeliveryGuard.MarkDelivered(plan, plan.IsBuyerFollowUp ? "Bot强制补发" : "Bot强制订单规则发送");
                        ReplyDeduplicationService.RememberDelivered(plan.Seller, plan.Buyer, answer);
                    }
                    OrderPlacedAutoReplyService.FinishExecution(
                        plan,
                        sendOk,
                        presetSendResult == null ? (sendOk ? 1 : 0) : presetSendResult.SentSegments);
                    if (ctl != null)
                    {
                        var successDetail = plan.IsBuyerFollowUp
                            ? "已发送（买家明确续问，订单规则强制补发一次）"
                            : "已发送（订单自动回复规则强制执行，订单号 " + plan.OrderId + "）";
                        ctl.SetSendResult(sendOk, sendOk ? successDetail : "发送失败：" + rpa.GetSendFailureReason());
                    }
                    Log.Info("下单自动回复规则执行完成: seller=" + plan.Seller
                        + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                        + ", delivered=" + sendOk + ", manualReplyIgnored=true");
                }
                catch
                {
                    OrderPlacedAutoReplyService.Complete(plan, false);
                    OrderPlacedAutoReplyService.FinishExecution(plan, false, 0);
                    throw;
                }
            }
        }

        private static string BuildPlanQuestion(OrderPlacedReplyPlan plan)
        {
            if (plan == null) return "[买家下单]";
            return plan.IsBuyerFollowUp
                ? "[买家续问充值流程] " + (plan.TriggerText ?? string.Empty)
                : "[买家下单] 订单号 " + plan.OrderId;
        }
    }
}