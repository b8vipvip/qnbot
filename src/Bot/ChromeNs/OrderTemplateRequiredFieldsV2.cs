using Bot.ChatRecord;
using Bot.Options;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot
{
    /// <summary>
    /// 统一接管需要订单字段的下单模板。先尽力查询交易详情；部分字段缺失时保留并发送
    /// 已取得的其他字段，只有模板所需动态字段全部缺失时才阻止空壳消息并释放发送占位。
    /// 订单模板统一使用唯一 SKU 占位符 {sku}。
    /// </summary>
    public partial class App
    {
        private static readonly object OrderTemplateRequiredFieldsV2Bootstrap =
            ChromeNs.OrderTemplateRequiredFieldsV2.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class OrderTemplateRequiredFieldsV2
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private sealed class EnrichmentProbe
        {
            public bool TradeFound;
            public bool BuyerSecurityIdFound;
            public bool SkuFound;
            public bool QuantityFound;
            public bool PaidFound;
            public bool TotalFound;
            public bool BuyerRemarkFound;
            public bool BuyerSearchAttempted;
            public int TradeQueryAttempts;
            public int TradeItemCount;
            public int SkuItemCount;
            public bool PayloadSkuRecoveryAttempted;
            public string Error;
        }

        private static readonly ConcurrentDictionary<QN, byte> Attached =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, DateTime> Inflight =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> BuyerSecurityIds =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly DateTime StartedAt = DateTime.Now;
        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|已成功下单|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|等待买家付款|已付款|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|TRADE_CREATED|TRADE_PAID|TRADE_FINISHED",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] OrderIdKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "tradeid", "tid",
            "biztradeid", "parentorderid", "bizordercode", "tradecode"
        };

        private static readonly string[] BuyerKeys =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "customernick", "customername", "customerid", "customeruid",
            "contactnick", "contactname", "contactid", "conversationnick", "conversationname",
            "oppositenick", "oppositename", "peernick", "peername",
            "sendernick", "sendername", "fromnick", "fromname", "usernick", "username"
        };

        private static readonly string[] StatusKeys =
        {
            "tradestatus", "orderstatus", "paystatus", "status", "statustext", "trade_status"
        };

        private static readonly string[] TimeKeys =
        {
            "paidat", "paytime", "paidtime", "paymenttime", "createdat", "createtime",
            "ordertime", "createdtime", "tradecreatetime", "eventtime", "sendtime", "timestamp"
        };

        private static readonly string[] BuyerRemarkKeys =
        {
            "buyerremark", "buyermemo", "buyernote", "buyermessage", "buyermessagecontent",
            "buyermsg", "remarkfrombuyer", "memofrombuyer"
        };

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                OrderTemplateSkuUiMigration.Initialize();
                // 本桥接替代旧的两个 10ms 补全桥接；1ms 仅用于尽早绑定，不在回调中忙等。
                _timer = new Timer(_ => Attach(), null, 0, 1);
                Log.Info("订单模板字段完整性 V2 已启动：唯一SKU占位符={sku}；缺失字段仅诊断，不阻断自动发送。");
            }
            return new object();
        }

        private static void Attach()
        {
            try
            {
                CleanupInflight();
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                    qn.EvRecieveNewMessage += OnReceiveNewMessage;
                    qn.EvMessageNotity += OnMessageNotify;
                    Log.Info("订单模板字段完整性 V2 已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定订单模板字段完整性 V2 失败：" + Safe(ex.Message, 300), 10);
            }
        }

        private static void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length < 8 || !LooksPotential(raw)) return;

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (!ShouldOwnConfiguredTemplate(cfg)) return;

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();

                foreach (var message in messages)
                {
                    if (!MessageLooksPotential(message)) continue;
                    TryOwnReceivePlan(qn, message);
                }
            }
            catch (Exception ex)
            {
                Log.Info("订单字段 V2 receiveNewMsg 解析失败: length=" + raw.Length
                    + ", error=" + Safe(ex.Message, 300));
            }
        }

        private static void TryOwnReceivePlan(QN qn, QNChatMessage message)
        {
            var seller = DirectOrderIdentityResolver.ResolveSeller(qn, message, null);
            var buyer = DirectOrderIdentityResolver.ResolveBuyer(qn, message, null);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            OrderPlacedReplyPlan plan;
            if (!OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                BuildMessageText(message),
                seller,
                buyer,
                StartedAt,
                out plan))
            {
                return;
            }

            if (plan == null || plan.IsBuyerFollowUp) return;
            StartOwnedPlan(qn, plan, "receiveNewMsg");
        }

        private static void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length < 8 || !LooksPotential(raw)) return;

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (!ShouldOwnConfiguredTemplate(cfg) || !Params.Robot.CanUseRobotReal) return;

            try
            {
                OrderSnapshot snapshot;
                if (!TryParseSparseSnapshot(qn, raw, out snapshot)) return;
                if (snapshot.EventTime < StartedAt.AddSeconds(-8)) return;

                var plan = new OrderPlacedReplyPlan
                {
                    Seller = snapshot.Seller,
                    Buyer = snapshot.Buyer,
                    OrderId = snapshot.OrderId,
                    EventText = snapshot.EventText,
                    EventTime = snapshot.EventTime,
                    ReservationKey = BuildOrderKey(snapshot.Seller, snapshot.Buyer, snapshot.OrderId),
                    Config = cfg,
                    Snapshot = snapshot,
                    IsBuyerFollowUp = false,
                    TriggerText = string.Empty,
                    TriggerTime = DateTime.MinValue
                };
                StartOwnedPlan(qn, plan, "messageCenterNotify");
            }
            catch (Exception ex)
            {
                Log.Info("订单字段 V2 messageCenterNotify 解析失败: length=" + raw.Length
                    + ", error=" + Safe(ex.Message, 300));
            }
        }

        internal static bool TryOwnExistingPlan(
            QN qn,
            OrderPlacedReplyPlan plan,
            string source)
        {
            if (qn == null || plan == null || plan.Config == null || plan.Snapshot == null)
            {
                return false;
            }
            if (plan.IsBuyerFollowUp || !ShouldOwnConfiguredTemplate(plan.Config))
            {
                return false;
            }

            Log.Info("订单模板字段 V2 接收已解析计划: source=" + source
                + ", seller=" + plan.Seller
                + ", buyer=" + plan.Buyer
                + ", orderId=" + plan.OrderId);
            StartOwnedPlan(qn, plan, source + "->requiredFieldsV2");
            return true;
        }

        private static void StartOwnedPlan(QN qn, OrderPlacedReplyPlan plan, string source)
        {
            if (qn == null || plan == null || plan.Config == null || plan.Snapshot == null) return;
            var key = BuildOrderKey(plan.Seller, plan.Buyer, plan.OrderId)
                + "#" + plan.Snapshot.EventType;
            DateTime until;
            if (Inflight.TryGetValue(key, out until) && until > DateTime.Now) return;
            Inflight[key] = DateTime.Now.AddMinutes(2);

            // 在异步交易查询之前先占住原有发送去重表，阻止旧直接订单桥接抢先发空模板。
            OrderPlacedAutoReplyService.Complete(plan, true);
            Log.Info("订单模板字段 V2 已接管: source=" + source
                + ", seller=" + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
            Task.Run(async () => await EnrichValidateAndSendAsync(qn, plan, key, source));
        }

        private static async Task EnrichValidateAndSendAsync(
            QN qn,
            OrderPlacedReplyPlan plan,
            string inflightKey,
            string source)
        {
            try
            {
                using (BotActivityCoordinator.Begin("订单模板字段发送V2", plan.Seller, plan.Buyer))
                {
                    var snapshot = plan.Snapshot;
                    if (snapshot != null)
                    {
                        snapshot.EventText = BuildSafeEventText(snapshot);
                        plan.EventText = snapshot.EventText;
                        plan.EventTime = snapshot.EventTime;
                    }

                    // Missing dynamic fields are optional enhancements. Never wait for a trade
                    // query before the configured automatic reply. Render missing values as empty
                    // strings and continue through the existing safe send pipeline immediately.
                    var preSendProbe = new EnrichmentProbe();
                    UpdateProbe(preSendProbe, snapshot);
                    var missing = MissingRequiredFields(plan.Config, snapshot);
                    var present = PresentRequiredFields(plan.Config, snapshot);
                    var missingReasons = BuildMissingReasons(plan.Config, snapshot, preSendProbe);
                    LogProbe(plan, preSendProbe, false, missing, present, missingReasons, source + "->preSend");

                    if (missing.Count > 0)
                    {
                        Log.Info("order_template_partial_send=true, orderId=" + plan.OrderId
                            + ", present=" + string.Join(",", present)
                            + ", missing=" + string.Join(",", missing)
                            + ", missing_reason=" + string.Join("|", missingReasons)
                            + ", field_lookup_policy=post_send_nonblocking");
                    }

                    if (snapshot != null)
                    {
                        OrderEventHub.Publish(snapshot);
                        OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
                        qn.EnqueueNewOrderAttention(snapshot);
                    }

                    await qn.ProcessOrderTemplateRequiredFieldsPlanAsync(plan);

                    // Probe only after the send path has completed. This can enrich cached state and
                    // explain why SKU was missing, but it has no authority to delay, cancel or retry
                    // the already-decided automatic reply.
                    if (missing.Count > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var postProbe = await TryEnrichFromTradeApiAsync(qn, plan).ConfigureAwait(false);
                                var afterMissing = MissingRequiredFields(plan.Config, plan.Snapshot);
                                var afterPresent = PresentRequiredFields(plan.Config, plan.Snapshot);
                                var afterReasons = BuildMissingReasons(plan.Config, plan.Snapshot, postProbe);
                                LogProbe(plan, postProbe, false, afterMissing, afterPresent, afterReasons,
                                    source + "->postSendProbe");
                            }
                            catch (Exception ex)
                            {
                                Log.Info("订单模板字段 post-send probe 失败，不影响已执行自动回复: "
                                    + Safe(ex.Message, 300));
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                OrderPlacedAutoReplyService.Complete(plan, false);
                Log.ErrorWithMaxCount("订单模板字段 V2 发送失败：" + Safe(ex.Message, 300), 10);
            }
            finally
            {
                DateTime ignored;
                Inflight.TryRemove(inflightKey, out ignored);
            }
        }

        private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync(
            QN qn,
            OrderPlacedReplyPlan plan)
        {
            var probe = new EnrichmentProbe();
            var snapshot = plan == null ? null : plan.Snapshot;
            if (qn == null || plan == null || snapshot == null) return probe;

            var securityBuyerUid = GetCachedBuyerSecurityId(plan.Seller, plan.Buyer);
            probe.BuyerSecurityIdFound = !string.IsNullOrWhiteSpace(securityBuyerUid);
            // One immediate best-effort lookup only. Missing SKU/other fields must never create
            // a retry-delay ladder or pause the automatic-send task waiting for eventual consistency.
            var delays = new[] { 0 };

            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0) await Task.Delay(delays[attempt]);
                probe.TradeQueryAttempts++;
                try
                {
                    var response = await qn.GetBuyerTrades(securityBuyerUid ?? string.Empty, plan.OrderId);
                    var trade = FindExactTrade(response, plan.OrderId);

                    if (trade == null && string.IsNullOrWhiteSpace(securityBuyerUid))
                    {
                        probe.BuyerSearchAttempted = true;
                        securityBuyerUid = await ResolveBuyerSecurityIdAsync(qn, plan.Seller, plan.Buyer);
                        probe.BuyerSecurityIdFound = !string.IsNullOrWhiteSpace(securityBuyerUid);
                        if (probe.BuyerSecurityIdFound)
                        {
                            response = await qn.GetBuyerTrades(securityBuyerUid, plan.OrderId);
                            trade = FindExactTrade(response, plan.OrderId);
                        }
                    }

                    if (trade == null) continue;
                    probe.TradeFound = true;
                    var tradeItems = (trade.itemList ?? new List<ZnkfTradeItem>()).Where(x => x != null).ToList();
                    probe.TradeItemCount = tradeItems.Count;
                    probe.SkuItemCount = tradeItems.Count(x => !string.IsNullOrWhiteSpace(x.sku));
                    probe.PayloadSkuRecoveryAttempted = probe.SkuItemCount == 0;
                    MergeTrade(snapshot, trade);
                    UpdateProbe(probe, snapshot);
                    var remaining = MissingRequiredFields(plan.Config, snapshot);
                    if (remaining.Count == 0
                        || (!remaining.Contains("sku") && !remaining.Contains("buyer_remark")))
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    probe.Error = Safe(ex.Message, 300);
                }
            }

            UpdateProbe(probe, snapshot);
            return probe;
        }

        private static void UpdateProbe(EnrichmentProbe probe, OrderSnapshot snapshot)
        {
            if (probe == null || snapshot == null) return;
            probe.SkuFound = !string.IsNullOrWhiteSpace(snapshot.SkuText);
            probe.QuantityFound = snapshot.Quantity > 0;
            probe.PaidFound = snapshot.PaidAmount.HasValue;
            probe.TotalFound = snapshot.TotalAmount.HasValue;
            probe.BuyerRemarkFound = !string.IsNullOrWhiteSpace(snapshot.BuyerRemark);
        }

        private static void LogProbe(
            OrderPlacedReplyPlan plan,
            EnrichmentProbe probe,
            bool blocked,
            IList<string> missing,
            IList<string> present,
            IList<string> missingReasons,
            string source)
        {
            probe = probe ?? new EnrichmentProbe();
            Log.Info("order_template_enrichment_v2"
                + " source=" + source
                + " orderId=" + (plan == null ? string.Empty : plan.OrderId)
                + " trade_found=" + probe.TradeFound.ToString().ToLowerInvariant()
                + " buyer_security_id_found=" + probe.BuyerSecurityIdFound.ToString().ToLowerInvariant()
                + " sku_found=" + probe.SkuFound.ToString().ToLowerInvariant()
                + " quantity_found=" + probe.QuantityFound.ToString().ToLowerInvariant()
                + " paid_found=" + probe.PaidFound.ToString().ToLowerInvariant()
                + " total_found=" + probe.TotalFound.ToString().ToLowerInvariant()
                + " buyer_search_attempted=" + probe.BuyerSearchAttempted.ToString().ToLowerInvariant()
                + " trade_query_attempts=" + probe.TradeQueryAttempts
                + " trade_item_count=" + probe.TradeItemCount
                + " sku_item_count=" + probe.SkuItemCount
                + " payload_sku_recovery_attempted=" + probe.PayloadSkuRecoveryAttempted.ToString().ToLowerInvariant()
                + " non_blocking=true"
                + " blocked_blank_template=" + blocked.ToString().ToLowerInvariant()
                + " present=" + string.Join(",", present ?? new List<string>())
                + " missing=" + string.Join(",", missing ?? new List<string>())
                + " missing_reason=" + string.Join("|", missingReasons ?? new List<string>())
                + " snapshot_source=" + Safe(plan == null || plan.Snapshot == null ? string.Empty : plan.Snapshot.Source, 100)
                + " event_type=" + (plan == null || plan.Snapshot == null ? string.Empty : plan.Snapshot.EventType.ToString())
                + (string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : " error=" + probe.Error));
        }

        private static List<string> PresentRequiredFields(
            AutoReplyRuleConfig cfg,
            OrderSnapshot snapshot)
        {
            var present = new List<string>();
            if (cfg == null || snapshot == null) return present;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            if (template.Contains("{sku}")
                && !string.IsNullOrWhiteSpace(snapshot.SkuText)) present.Add("sku");
            if (template.Contains("{买家备注}") && !string.IsNullOrWhiteSpace(snapshot.BuyerRemark)) present.Add("buyer_remark");
            if (template.Contains("{数量}") && snapshot.Quantity > 0) present.Add("quantity");
            if (template.Contains("{实付}") && snapshot.PaidAmount.HasValue) present.Add("paid");
            if (template.Contains("{金额}") && snapshot.TotalAmount.HasValue) present.Add("total");
            if (template.Contains("{商品}") && !string.IsNullOrWhiteSpace(snapshot.ItemTitle)) present.Add("item");
            if (template.Contains("{订单状态}") && !string.IsNullOrWhiteSpace(snapshot.TradeStatus)) present.Add("status");
            return present;
        }

        private static bool HasKnownNonOrderTemplateField(
            AutoReplyRuleConfig cfg,
            OrderPlacedReplyPlan plan)
        {
            if (cfg == null || plan == null) return false;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            return (template.Contains("{客服}") && !string.IsNullOrWhiteSpace(plan.Seller))
                || (template.Contains("{买家}") && !string.IsNullOrWhiteSpace(plan.Buyer))
                || (template.Contains("{订单号}") && !string.IsNullOrWhiteSpace(plan.OrderId))
                || (template.Contains("{时间}") && plan.EventTime != DateTime.MinValue);
        }

        private static List<string> BuildMissingReasons(
            AutoReplyRuleConfig cfg,
            OrderSnapshot snapshot,
            EnrichmentProbe probe)
        {
            var reasons = new List<string>();
            probe = probe ?? new EnrichmentProbe();
            foreach (var field in MissingRequiredFields(cfg, snapshot))
            {
                string reason;
                if (snapshot == null)
                {
                    reason = "snapshot_null";
                }
                else if (probe.TradeFound)
                {
                    switch (field)
                    {
                        case "sku": reason = "trade_found_but_sku_empty"; break;
                        case "buyer_remark": reason = "trade_found_but_buyer_remark_empty"; break;
                        case "quantity": reason = "trade_found_but_quantity_zero"; break;
                        case "paid": reason = "trade_found_but_paid_amount_null"; break;
                        case "total": reason = "trade_found_but_total_amount_null"; break;
                        case "item": reason = "trade_found_but_item_title_empty"; break;
                        case "status": reason = "trade_found_but_status_empty"; break;
                        default: reason = "field_unavailable"; break;
                    }
                }
                else if (probe.TradeQueryAttempts == 0)
                {
                    reason = "not_queried_before_send";
                }
                else if (!string.IsNullOrWhiteSpace(probe.Error))
                {
                    reason = "trade_query_error_after_" + probe.TradeQueryAttempts + "_attempts";
                }
                else
                {
                    reason = probe.BuyerSearchAttempted && !probe.BuyerSecurityIdFound
                        ? "buyer_security_id_not_found_trade_not_found"
                        : "trade_not_found_after_" + probe.TradeQueryAttempts + "_attempts";
                }
                reasons.Add(field + ":" + reason);
            }
            return reasons;
        }

        private static bool ShouldOwnConfiguredTemplate(AutoReplyRuleConfig cfg)
        {
            if (cfg == null || !cfg.EnableOrderPlacedReply) return false;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;
            return template.Contains("{sku}")
                || template.Contains("{数量}")
                || template.Contains("{金额}")
                || template.Contains("{实付}")
                || template.Contains("{商品}")
                || template.Contains("{订单状态}")
                || template.Contains("{买家备注}");
        }

        private static List<string> MissingRequiredFields(
            AutoReplyRuleConfig cfg,
            OrderSnapshot snapshot)
        {
            var missing = new List<string>();
            if (cfg == null) return missing;
            var template = cfg.OrderPlacedReplyText ?? string.Empty;

            if (template.Contains("{sku}")
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SkuText)))
            {
                missing.Add("sku");
            }
            if (template.Contains("{买家备注}")
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BuyerRemark)))
            {
                missing.Add("buyer_remark");
            }
            if (template.Contains("{数量}") && (snapshot == null || snapshot.Quantity <= 0))
            {
                missing.Add("quantity");
            }
            if (template.Contains("{实付}") && (snapshot == null || !snapshot.PaidAmount.HasValue))
            {
                missing.Add("paid");
            }
            if (template.Contains("{金额}") && (snapshot == null || !snapshot.TotalAmount.HasValue))
            {
                missing.Add("total");
            }
            if (template.Contains("{商品}")
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ItemTitle)))
            {
                missing.Add("item");
            }
            if (template.Contains("{订单状态}")
                && (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TradeStatus)))
            {
                missing.Add("status");
            }
            return missing;
        }

        private static ZnkfTrade FindExactTrade(ZnkfTradeQueryResponse response, string orderId)
        {
            var orders = response == null || response.data == null || response.data.orders == null
                ? new List<ZnkfTrade>()
                : response.data.orders.Where(x => x != null).ToList();
            if (orders.Count == 0) return null;

            var normalized = DigitsOnly(orderId, 8, 40);
            var exact = orders.FirstOrDefault(x =>
                string.Equals(DigitsOnly(x.bizOrderId, 8, 40), normalized, StringComparison.Ordinal));
            if (exact != null) return exact;

            exact = orders.FirstOrDefault(x => (x.itemList ?? new List<ZnkfTradeItem>()).Any(item =>
                item != null && (
                    string.Equals(DigitsOnly(item.bizOrderId, 8, 40), normalized, StringComparison.Ordinal)
                    || string.Equals(DigitsOnly(item.subOrderId, 8, 40), normalized, StringComparison.Ordinal))));
            return exact ?? (orders.Count == 1 ? orders[0] : null);
        }

        private static void MergeTrade(OrderSnapshot snapshot, ZnkfTrade trade)
        {
            if (snapshot == null || trade == null) return;
            var items = (trade.itemList ?? new List<ZnkfTradeItem>())
                .Where(x => x != null)
                .ToList();

            var sku = string.Join("；", items
                .Select(x => NormalizeSku(x.sku))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(sku))
            {
                try
                {
                    sku = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(
                        JObject.FromObject(trade).ToString(Formatting.None));
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(snapshot.SkuText) && sku.Length > 0)
            {
                snapshot.SkuText = Safe(sku, 240);
            }

            var quantity = items.Sum(x => x.buyAmount > 0 ? x.buyAmount : Math.Max(0, x.buyerAmount));
            if (quantity <= 0) quantity = Math.Max(0, trade.buyAmount);
            if (snapshot.Quantity <= 0 && quantity > 0) snapshot.Quantity = quantity;

            if (string.IsNullOrWhiteSpace(snapshot.BuyerRemark))
            {
                try
                {
                    var tradeFlat = Flatten(JObject.FromObject(trade));
                    var buyerRemark = FindValue(tradeFlat, BuyerRemarkKeys);
                    if (!string.IsNullOrWhiteSpace(buyerRemark)) snapshot.BuyerRemark = Safe(buyerRemark, 500);
                }
                catch { }
            }

            var title = string.Join("；", items
                .Select(x => Safe(x.auctionTitle, 180))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(snapshot.ItemTitle) && title.Length > 0)
            {
                snapshot.ItemTitle = Safe(title, 300);
            }

            var first = items.FirstOrDefault();
            if (first != null)
            {
                if (string.IsNullOrWhiteSpace(snapshot.ItemId)) snapshot.ItemId = Safe(first.auctionId, 64);
                if (string.IsNullOrWhiteSpace(snapshot.ProductUrl)) snapshot.ProductUrl = Safe(first.auctionUrl, 500);
                if (string.IsNullOrWhiteSpace(snapshot.ImageUrl)) snapshot.ImageUrl = Safe(first.picUrl, 500);
            }

            var total = ParseMoney(trade.orderPrice);
            if (!total.HasValue)
            {
                decimal sum = 0m;
                var hasPrice = false;
                foreach (var item in items)
                {
                    var price = ParseMoney(FirstNonEmpty(item.price, item.auctionPrice));
                    var count = item.buyAmount > 0 ? item.buyAmount : Math.Max(1, item.buyerAmount);
                    if (!price.HasValue) continue;
                    sum += price.Value * count;
                    hasPrice = true;
                }
                if (hasPrice) total = decimal.Round(sum, 2);
            }
            if (!snapshot.TotalAmount.HasValue && total.HasValue) snapshot.TotalAmount = total;

            var itemPayTime = items
                .Where(x => x.payTime.HasValue)
                .Select(x => x.payTime)
                .FirstOrDefault();
            var paidAt = trade.payTime ?? itemPayTime;
            var paid = paidAt.HasValue || snapshot.EventType == OrderEventType.Paid || snapshot.IsPaid == true;
            if (paid && !snapshot.PaidAmount.HasValue && total.HasValue) snapshot.PaidAmount = total;
            if (paidAt.HasValue)
            {
                snapshot.PaidAt = paidAt;
                snapshot.IsPaid = true;
                snapshot.TradeStatus = "已付款";
                snapshot.EventType = OrderEventType.Paid;
            }
            else if (string.IsNullOrWhiteSpace(snapshot.TradeStatus))
            {
                snapshot.TradeStatus = paid ? "已付款" : "新下单";
            }
        }

        private static async Task<string> ResolveBuyerSecurityIdAsync(
            QN qn,
            string seller,
            string buyer)
        {
            var response = await qn.SearchBuyerUser(buyer);
            var accounts = response == null || response.Data == null || response.Data.Data == null
                ? new List<Account>()
                : response.Data.Data.Where(x => x != null).ToList();
            if (accounts.Count == 0) return string.Empty;

            var account = accounts.FirstOrDefault(x =>
                    DirectOrderIdentityResolver.IdentityEquals(x.Nick, buyer))
                ?? accounts.FirstOrDefault(x =>
                    string.Equals((x.Nick ?? string.Empty).Trim(), (buyer ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.EncryptAccountId));
            var id = account == null ? string.Empty : (account.EncryptAccountId ?? string.Empty).Trim();
            if (id.Length > 0) BuyerSecurityIds[BuildBuyerKey(seller, buyer)] = id;
            return id;
        }

        private static string GetCachedBuyerSecurityId(string seller, string buyer)
        {
            string value;
            return BuyerSecurityIds.TryGetValue(BuildBuyerKey(seller, buyer), out value)
                ? value
                : string.Empty;
        }

        private static bool TryParseSparseSnapshot(QN qn, string raw, out OrderSnapshot snapshot)
        {
            snapshot = null;
            var root = ParseExpanded(raw);
            var flat = Flatten(root);
            var combined = BuildCombinedText(raw, flat);
            if (!EventCueRegex.IsMatch(combined)) return false;

            var orderId = ResolveOrderId(flat, combined);
            if (string.IsNullOrWhiteSpace(orderId)) return false;
            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = ResolveBuyer(flat, seller, orderId);
            if (seller.Length == 0 || buyer.Length == 0) return false;

            var status = FirstNonEmpty(FindValue(flat, StatusKeys), ExtractStatus(combined));
            var paid = ResolvePaidState(combined, status);
            var eventType = paid == true ? OrderEventType.Paid : OrderEventType.Created;
            var eventTime = ParseDate(FindValue(flat, TimeKeys)) ?? DateTime.Now;

            snapshot = new OrderSnapshot
            {
                Seller = seller,
                Buyer = buyer,
                OrderId = orderId,
                SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw),
                BuyerRemark = Safe(FindValue(flat, BuyerRemarkKeys), 500),
                TradeStatus = string.IsNullOrWhiteSpace(status) ? (paid == true ? "已付款" : "新下单") : status,
                IsPaid = paid,
                CreatedAt = eventType == OrderEventType.Created ? (DateTime?)eventTime : null,
                PaidAt = eventType == OrderEventType.Paid ? (DateTime?)eventTime : null,
                Source = "messageCenterNotify订单模板字段V2",
                RawCardHash = Hash(raw),
                DetectedAt = DateTime.Now,
                EventTime = eventTime,
                EventType = eventType
            };
            snapshot.EventText = BuildSafeEventText(snapshot);
            return true;
        }

        private static JToken ParseExpanded(string raw)
        {
            JToken token;
            try { token = JToken.Parse(raw); }
            catch { return new JValue(raw ?? string.Empty); }
            for (var i = 0; i < 5 && token.Type == JTokenType.String; i++)
            {
                var nested = token.ToString().Trim();
                if (!LooksLikeJson(nested)) break;
                try { token = JToken.Parse(nested); }
                catch { break; }
            }
            return token;
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var output = new List<FlatValue>();
            Walk(root, string.Empty, output, 0);
            return output;
        }

        private static void Walk(JToken token, string path, ICollection<FlatValue> output, int depth)
        {
            if (token == null || depth > 18 || output.Count >= 1800) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, output, depth + 1);
                    if (output.Count >= 1800) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 180 || output.Count >= 1800) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;

            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 16000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value.Length > 4000 ? value.Substring(0, 4000) : value });

            if (token.Type == JTokenType.String && depth < 16 && LooksLikeJson(value))
            {
                try { Walk(JToken.Parse(value), path + ".json", output, depth + 1); }
                catch { }
            }
        }

        private static string ResolveOrderId(IList<FlatValue> flat, string combined)
        {
            var direct = FindValue(flat, OrderIdKeys);
            var digits = DigitsOnly(direct, 8, 40);
            if (digits.Length > 0) return digits;
            var match = OrderIdTextRegex.Match(combined ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ResolveBuyer(IList<FlatValue> flat, string seller, string orderId)
        {
            var best = string.Empty;
            var bestScore = 0;
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null) continue;
                var value = (item.Value ?? string.Empty).Trim();
                if (!UsableBuyer(value, seller, orderId)) continue;
                var key = NormalizeKey(item.Key);
                var path = NormalizeKey(item.Path);
                var score = 0;
                if (BuyerKeys.Contains(key)) score += 100;
                if (path.Contains("buyer")) score += 90;
                if (path.Contains("customer")) score += 85;
                if (path.Contains("contact")) score += 75;
                if (path.Contains("opposite") || path.Contains("peer")) score += 70;
                if (path.Contains("conversation")) score += 65;
                if (path.Contains("sender") || path.Contains("from")) score += 45;
                if (key.EndsWith("nick") || key.EndsWith("name")) score += 25;
                if (score <= bestScore) continue;
                bestScore = score;
                best = value;
            }
            return bestScore >= 55 ? best : string.Empty;
        }

        private static bool UsableBuyer(string value, string seller, string orderId)
        {
            if (value.Length < 2 || value.Length > 180) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (string.Equals(DigitsOnly(value, 8, 40), orderId, StringComparison.Ordinal)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品") || value.Contains("金额")) return false;
            if (value.StartsWith("{") || value.StartsWith("[")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
        }

        private static string FindValue(IList<FlatValue> flat, IEnumerable<string> aliases)
        {
            var set = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Value)
                    && set.Contains(NormalizeKey(item.Key))) return item.Value.Trim();
            }
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var path = NormalizeKey(item.Path);
                if (set.Any(alias => path.EndsWith(alias, StringComparison.OrdinalIgnoreCase))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static string BuildCombinedText(string raw, IEnumerable<FlatValue> flat)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 12000) parts.Add(raw);
            parts.AddRange((flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 1200)
                .Select(x => x.Value.Trim())
                .Take(500));
            return Regex.Replace(string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), @"\s+", " ").Trim();
        }

        private static string BuildMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(message.summary)) parts.Add(message.summary.Trim());
            if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text)) parts.Add(message.originalData.text.Trim());
            return Regex.Replace(string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), @"\s+", " ").Trim();
        }

        private static bool MessageLooksPotential(QNChatMessage message)
        {
            if (message == null) return false;
            try { return LooksPotential(JObject.FromObject(message).ToString(Formatting.None)); }
            catch { return LooksPotential(BuildMessageText(message)); }
        }

        private static bool LooksPotential(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length < 8) return false;
            var lower = raw.ToLowerInvariant();
            var cue = raw.Contains("订单") || raw.Contains("下单") || raw.Contains("已付款")
                || raw.Contains("支付成功") || raw.Contains("待发货") || raw.Contains("交易时间")
                || (raw.Contains("件商品") && raw.Contains("合计"))
                || lower.Contains("orderid") || lower.Contains("bizorderid")
                || lower.Contains("tradeid") || lower.Contains("wait_seller_send_goods");
            if (!cue) return false;
            return OrderIdTextRegex.IsMatch(raw)
                || Regex.IsMatch(lower,
                    "(?:orderid|bizorderid|mainorderid|suborderid|tradeid|\\\"tid\\\")\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\d{8,}");
        }

        private static string NormalizeSku(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace('：', ':'), @"\s+", " ").Trim();
            if (value.Length == 0) return string.Empty;
            if (!value.Contains(":"))
            {
                var known = Regex.Match(value,
                    @"^(专辑名称|套餐名称|套餐|期限|时长|会员类型|充值类型|账号类型|商品规格|版本)\s*(.+)$",
                    RegexOptions.IgnoreCase);
                if (known.Success && known.Groups[2].Value.Trim().Length > 0)
                {
                    value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim();
                }
            }
            return Safe(value, 240);
        }

        private static decimal? ParseMoney(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            decimal amount;
            var match = Regex.Match(value.Replace(",", string.Empty), @"-?\d+(?:\.\d{1,4})?");
            if (!match.Success
                || !decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                || amount < 0 || amount > 100000000m) return null;
            return decimal.Round(amount, 2);
        }

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    if (raw > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    if (raw > 1000000000L) return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                return dto.LocalDateTime;
            }
            return null;
        }

        private static bool? ResolvePaidState(string text, string status)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "未付款|等待买家付款|待付款|付款关闭|交易关闭", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(value, "已付款|付款成功|支付成功|交易成功|已支付|待卖家发货|WAIT_SELLER_SEND_GOODS|TRADE_PAID", RegexOptions.IgnoreCase)) return true;
            return null;
        }

        private static string ExtractStatus(string text)
        {
            var known = new[] { "已付款", "待卖家发货", "等待买家付款", "未付款", "交易成功", "订单关闭", "退款中", "买家已下单", "订单创建成功", "新下单" };
            return known.FirstOrDefault(x => (text ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) ?? string.Empty;
        }

        private static string BuildSafeEventText(OrderSnapshot snapshot)
        {
            if (snapshot == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append(snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ");
            sb.Append("订单号：").Append(snapshot.OrderId);
            if (!string.IsNullOrWhiteSpace(snapshot.ItemTitle)) sb.Append(" 商品：").Append(Safe(snapshot.ItemTitle, 220));
            if (!string.IsNullOrWhiteSpace(snapshot.SkuText)) sb.Append(" SKU：").Append(Safe(snapshot.SkuText, 180));
            if (!string.IsNullOrWhiteSpace(snapshot.BuyerRemark)) sb.Append(" 买家备注：").Append(Safe(snapshot.BuyerRemark, 300));
            if (snapshot.Quantity > 0) sb.Append(" 数量：").Append(snapshot.Quantity);
            if (snapshot.PaidAmount.HasValue) sb.Append(" 实付：").Append(snapshot.PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            else if (snapshot.TotalAmount.HasValue) sb.Append(" 金额：").Append(snapshot.TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(" 订单状态：").Append(string.IsNullOrWhiteSpace(snapshot.TradeStatus)
                ? (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                : snapshot.TradeStatus);
            return sb.ToString();
        }

        private static string BuildOrderKey(string seller, string buyer, string orderId)
        {
            return NormalizeIdentity(seller) + "#" + NormalizeIdentity(buyer) + "#" + (orderId ?? string.Empty).Trim();
        }

        private static string BuildBuyerKey(string seller, string buyer)
        {
            return NormalizeIdentity(seller) + "#" + NormalizeIdentity(buyer);
        }

        private static string NormalizeIdentity(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("cntaobao", StringComparison.Ordinal)) value = value.Substring("cntaobao".Length);
            return value;
        }

        private static string NormalizeKey(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string Safe(string value, int max)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string Hash(string value)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                        .Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch { return string.Empty; }
        }

        private static bool LooksLikeJson(string value)
        {
            value = (value ?? string.Empty).Trim();
            return (value.StartsWith("{") && value.EndsWith("}"))
                || (value.StartsWith("[") && value.EndsWith("]"));
        }

        private static void CleanupInflight()
        {
            var now = DateTime.Now;
            foreach (var pair in Inflight)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                Inflight.TryRemove(pair.Key, out ignored);
            }
        }
    }

    internal static class OrderTemplateSkuUiMigration
    {
        private static readonly ConditionalWeakTable<TextBlock, object> EnhancedHints =
            new ConditionalWeakTable<TextBlock, object>();
        private static int _initialized;

        private static readonly string[] OrderTemplateTokens =
        {
            "{客服}", "{买家}", "{订单号}", "{时间}", "{商品}", "{sku}",
            "{数量}", "{金额}", "{实付}", "{订单状态}", "{买家备注}", "{分段符}"
        };

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(TextBlock),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnTextBlockLoaded),
                true);
        }

        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            var hint = sender as TextBlock;
            if (hint == null) return;
            var value = hint.Text ?? string.Empty;
            if (value.IndexOf("支持 {客服}", StringComparison.Ordinal) < 0
                || value.IndexOf("接口失败", StringComparison.Ordinal) < 0) return;
            object marker;
            if (EnhancedHints.TryGetValue(hint, out marker)) return;
            var target = FindOrderTemplateTextBox(hint);
            if (target == null) return;
            EnhancedHints.Add(hint, new object());
            RewritePlaceholderHint(hint, target);
        }

        private static void RewritePlaceholderHint(TextBlock hint, TextBox target)
        {
            hint.Inlines.Clear();
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Inlines.Add(new Run("支持 "));
            var blue = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            for (var i = 0; i < OrderTemplateTokens.Length; i++)
            {
                if (i > 0) hint.Inlines.Add(new Run("、"));
                var token = OrderTemplateTokens[i];
                var link = new Hyperlink(new Run(token))
                {
                    Foreground = blue,
                    Cursor = Cursors.Hand,
                    TextDecorations = null,
                    ToolTip = "点击插入 " + token
                };
                link.Click += delegate { InsertAtCaret(target, token); };
                hint.Inlines.Add(link);
            }
            hint.Inlines.Add(new Run("。点击蓝色变量可插入到当前光标处；分段符会拆成多条千牛消息依次发送；接口失败时也会使用这段话兜底。"));
        }

        private static void InsertAtCaret(TextBox box, string token)
        {
            if (box == null || string.IsNullOrEmpty(token)) return;
            var value = box.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(box.SelectionStart, value.Length));
            var length = Math.Max(0, Math.Min(box.SelectionLength, value.Length - start));
            box.Text = value.Remove(start, length).Insert(start, token);
            box.SelectionStart = start + token.Length;
            box.SelectionLength = 0;
            box.Focus();
        }

        private static TextBox FindOrderTemplateTextBox(DependencyObject start)
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var configured = cfg == null ? string.Empty : (cfg.OrderPlacedReplyText ?? string.Empty);
            DependencyObject current = start;
            for (var depth = 0; current != null && depth < 10; depth++)
            {
                var boxes = Descendants(current).OfType<TextBox>().Where(x => x.AcceptsReturn).ToList();
                var exact = boxes.FirstOrDefault(x => string.Equals(x.Text ?? string.Empty, configured, StringComparison.Ordinal));
                if (exact != null) return exact;
                if (boxes.Count == 1) return boxes[0];
                current = ParentOf(current);
            }
            return null;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            foreach (var child in LogicalChildren(root))
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        private static DependencyObject ParentOf(DependencyObject value)
        {
            if (value == null) return null;
            try
            {
                var logical = LogicalTreeHelper.GetParent(value);
                if (logical != null) return logical;
            }
            catch { }
            try { return VisualTreeHelper.GetParent(value); }
            catch { return null; }
        }

        private static DependencyObject[] LogicalChildren(DependencyObject root)
        {
            try
            {
                return LogicalTreeHelper.GetChildren(root)
                    .Cast<object>()
                    .OfType<DependencyObject>()
                    .ToArray();
            }
            catch { return new DependencyObject[0]; }
        }
    }

    public partial class QN
    {
        internal Task ProcessOrderTemplateRequiredFieldsPlanAsync(OrderPlacedReplyPlan plan)
        {
            return ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
