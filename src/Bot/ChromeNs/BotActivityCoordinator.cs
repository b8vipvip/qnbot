using Bot.ChatRecord;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BotActivityLease : IDisposable
    {
        private readonly long _id;
        private int _disposed;

        internal BotActivityLease(long id)
        {
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            BotActivityCoordinator.End(_id);
        }
    }

    internal sealed class BotActivitySnapshot
    {
        public int ActiveCount { get; set; }
        public DateTime LastHumanInteractionAt { get; set; }
        public string LastHumanInteractionReason { get; set; }
        public string BusyReason { get; set; }
    }

    internal static class BotActivityCoordinator
    {
        private sealed class ActivityRecord
        {
            public long Id;
            public string Kind;
            public string Seller;
            public string Buyer;
            public DateTime StartedAt;
        }

        private sealed class HumanRecord
        {
            public DateTime At;
            public string Reason;
        }

        private static long _nextId;
        private static readonly ConcurrentDictionary<long, ActivityRecord> Activities =
            new ConcurrentDictionary<long, ActivityRecord>();
        private static readonly ConcurrentDictionary<string, HumanRecord> HumanInteractions =
            new ConcurrentDictionary<string, HumanRecord>(StringComparer.OrdinalIgnoreCase);

        public static BotActivityLease Begin(string kind, string seller, string buyer)
        {
            var id = Interlocked.Increment(ref _nextId);
            Activities[id] = new ActivityRecord
            {
                Id = id,
                Kind = (kind ?? string.Empty).Trim(),
                Seller = Normalize(seller),
                Buyer = Normalize(buyer),
                StartedAt = DateTime.Now
            };
            return new BotActivityLease(id);
        }

        internal static void End(long id)
        {
            ActivityRecord ignored;
            Activities.TryRemove(id, out ignored);
        }

        public static void MarkHumanInteraction(string seller, string reason)
        {
            seller = Normalize(seller);
            if (seller.Length == 0) return;
            HumanInteractions[seller] = new HumanRecord
            {
                At = DateTime.Now,
                Reason = (reason ?? string.Empty).Trim()
            };
            Log.Info("已记录人工操作保护: seller=" + seller + ", reason=" + (reason ?? string.Empty));
        }

        public static bool IsSafeToAutoFocus(string seller, out string reason)
        {
            seller = Normalize(seller);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            if (active.Count > 0)
            {
                reason = "Bot当前有任务：" + string.Join("、", active.Select(x => x.Kind).Distinct().Take(3));
                return false;
            }

            HumanRecord human;
            var protectSeconds = OrderAttentionSettings.GetHumanProtectionSeconds();
            if (HumanInteractions.TryGetValue(seller, out human)
                && human != null
                && DateTime.Now - human.At < TimeSpan.FromSeconds(protectSeconds))
            {
                var remain = Math.Max(1, protectSeconds - (int)(DateTime.Now - human.At).TotalSeconds);
                reason = "人工操作保护中（约" + remain + "秒）：" + human.Reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static BotActivitySnapshot GetSnapshot(string seller)
        {
            seller = Normalize(seller);
            HumanRecord human;
            HumanInteractions.TryGetValue(seller, out human);
            var active = Activities.Values
                .Where(x => x != null && (x.Seller.Length == 0 || x.Seller == seller))
                .OrderBy(x => x.StartedAt)
                .ToList();
            return new BotActivitySnapshot
            {
                ActiveCount = active.Count,
                LastHumanInteractionAt = human == null ? DateTime.MinValue : human.At,
                LastHumanInteractionReason = human == null ? string.Empty : human.Reason,
                BusyReason = active.Count == 0
                    ? string.Empty
                    : string.Join("、", active.Select(x => x.Kind).Distinct().Take(3))
            };
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}

namespace Bot
{
    public partial class App
    {
        // OrderEventHub is the authoritative order-recognition boundary. Some Qianniu builds can
        // publish a confirmed paid/created order (and therefore show the right-side order card)
        // without the raw chat-card path ever creating OrderPlacedReplyPlan. Keep a small runtime
        // fallback attached to the hub's persisted state so a confirmed order cannot stop at UI only.
        private readonly object _orderEventAutoReplyFallbackBootstrap =
            ChromeNs.OrderEventAutoReplyFallback.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Fast consumer for newly accepted OrderEventHub events.
    ///
    /// A confirmed order is already strong enough evidence to start a configured fixed preset. The
    /// persisted action ledger in ProcessOrderPlacedReplyAsync is the side-effect source of truth, so
    /// this consumer may race the raw receiveNewMsg/messageCenterNotify paths without creating a
    /// duplicate send. Fixed presets therefore do not wait for trade-detail enrichment, UI auto-focus,
    /// AI, message merge or the human-interaction auto-focus guard. HTTP mode keeps the enrichment path.
    /// </summary>
    internal static class OrderEventAutoReplyFallback
    {
        private static readonly DateTime StartedAt = DateTime.Now;

        // Late order recovery probes run for at most 180 seconds. Five minutes leaves margin for
        // file polling / scheduling while making a historical persisted event permanently ineligible
        // for a new customer-facing send. This is independent of process uptime.
        internal static readonly TimeSpan HubEventFreshnessWindow = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, DateTime> Scheduled =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;
        private static int _tickRunning;
        private static string _lastFileStamp = string.Empty;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                // OrderEventHub writes an atomic local state file synchronously. Poll it at a short
                // interval so a newly confirmed Created/Paid event reaches the mandatory send path
                // in hundreds of milliseconds instead of waiting behind enrichment retries.
                _timer = new Timer(_ => Tick(), null, 100, 200);
                Log.Info("订单事件Hub即时自动回复消费者已启动：固定预设不等待订单字段补全。");
            }
            return new object();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                var path = GetOrderEventStatePath();
                if (!File.Exists(path)) return;

                var info = new FileInfo(path);
                var stamp = info.LastWriteTimeUtc.Ticks + "#" + info.Length;
                if (string.Equals(stamp, _lastFileStamp, StringComparison.Ordinal)) return;
                _lastFileStamp = stamp;

                JObject root;
                try
                {
                    root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (IOException)
                {
                    // OrderEventHub writes atomically through a temp file. A read that races the
                    // replace is harmless; the next 200ms tick will retry.
                    return;
                }

                var events = root["Events"] as JArray;
                if (events == null || events.Count == 0) return;
                CleanupScheduled();

                foreach (var item in events.OfType<JObject>())
                {
                    DateTime seenAt;
                    if (!TryReadLocalDateTime(item["SeenAt"], out seenAt)) continue;

                    DateTime acceptedAt;
                    if (!TryReadLocalDateTime(item["AcceptedAt"], out acceptedAt))
                    {
                        // Backward compatibility for state files written before AcceptedAt existed.
                        acceptedAt = seenAt;
                    }
                    if (!IsFreshAcceptedAt(acceptedAt, StartedAt)) continue;

                    OrderSnapshot snapshot;
                    try
                    {
                        snapshot = item["Snapshot"] == null
                            ? null
                            : item["Snapshot"].ToObject<OrderSnapshot>();
                    }
                    catch
                    {
                        continue;
                    }
                    if (snapshot == null
                        || string.IsNullOrWhiteSpace(snapshot.Seller)
                        || string.IsNullOrWhiteSpace(snapshot.Buyer)
                        || string.IsNullOrWhiteSpace(snapshot.OrderId)) continue;
                    if (snapshot.EventType != OrderEventType.Created
                        && snapshot.EventType != OrderEventType.Paid) continue;

                    var key = BuildKey(snapshot);
                    if (!Scheduled.TryAdd(key, DateTime.Now)) continue;
                    Task.Run(async () => await HandleAsync(snapshot, acceptedAt, key));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("订单事件Hub即时自动回复检查失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static async Task HandleAsync(OrderSnapshot snapshot, DateTime seenAt, string key)
        {
            try
            {
                // Do not wait for another order pipeline here. ProcessOrderPlacedReplyAsync owns a
                // durable action ledger keyed by seller+buyer+order, so whichever legitimate path
                // reaches the send boundary first wins and every later path becomes a no-op.
                QN qn = null;
                for (var attempt = 0; attempt < 20 && qn == null; attempt++)
                {
                    qn = ResolveQn(snapshot.Seller);
                    if (qn == null) await Task.Delay(250);
                }
                if (qn == null)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub即时消费者暂未找到对应千牛实例，等待后续订单事件重试: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId);
                    return;
                }

                ShopContext shop;
                try
                {
                    shop = ShopContextLocator.ResolveBySellerNick(snapshot.Seller);
                }
                catch (Exception ex)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub即时消费者无法解析店铺作用域，拒绝跨店猜测: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId + ", error=" + ex.Message);
                    return;
                }
                if (shop == null)
                {
                    DateTime ignored;
                    Scheduled.TryRemove(key, out ignored);
                    Log.Info("[订单自动回复] Hub即时消费者缺少店铺作用域，未发送: seller="
                        + snapshot.Seller + ", orderId=" + snapshot.OrderId);
                    return;
                }

                using (ShopSettingsScope.Enter(shop))
                {
                    await qn.ProcessAcceptedOrderEventFallbackAsync(snapshot, seenAt);
                }
            }
            catch (Exception ex)
            {
                DateTime ignored;
                Scheduled.TryRemove(key, out ignored);
                Log.ErrorWithMaxCount("订单事件Hub即时自动回复执行失败：" + ex.Message, 10);
            }
        }

        private static QN ResolveQn(string seller)
        {
            var exact = QN.FindExistingBySellerNick(seller);
            if (exact != null) return exact;
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .FirstOrDefault(x => x != null
                        && x.Seller != null
                        && DirectOrderIdentityResolver.IdentityEquals(x.Seller.Nick, seller));
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadLocalDateTime(JToken token, out DateTime value)
        {
            value = DateTime.MinValue;
            if (token == null) return false;
            if (!DateTime.TryParse(token.ToString(), out value)) return false;
            if (value.Kind == DateTimeKind.Utc) value = value.ToLocalTime();
            return true;
        }

        internal static bool IsFreshAcceptedAt(DateTime acceptedAt, DateTime runtimeStartedAt)
        {
            if (acceptedAt == DateTime.MinValue) return false;
            if (acceptedAt.Kind == DateTimeKind.Utc) acceptedAt = acceptedAt.ToLocalTime();
            if (runtimeStartedAt.Kind == DateTimeKind.Utc) runtimeStartedAt = runtimeStartedAt.ToLocalTime();

            var now = DateTime.Now;
            if (acceptedAt < runtimeStartedAt.AddSeconds(-2)) return false;
            if (acceptedAt < now.Subtract(HubEventFreshnessWindow)) return false;

            // A materially future timestamp is not valid freshness evidence either. Allow a small
            // clock/serialization tolerance but never let a corrupted future value stay eligible.
            if (acceptedAt > now.AddMinutes(1)) return false;
            return true;
        }

        private static string GetOrderEventStatePath()
        {
            // Keep this exactly aligned with OrderEventHub.GetPath().
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-event-state.json");
        }

        private static string BuildKey(OrderSnapshot snapshot)
        {
            return (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }

        private static void CleanupScheduled()
        {
            // OrderEventHub retains facts for 30 days. Keep the in-memory "already considered"
            // ledger slightly longer so a long-running Bot cannot re-arm yesterday's order merely
            // because this dictionary used to evict keys after 24 hours.
            var cutoff = DateTime.Now.AddDays(-31);
            foreach (var pair in Scheduled)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                Scheduled.TryRemove(pair.Key, out ignored);
            }
        }
    }

    public partial class QN
    {
        /// <summary>
        /// Consume an order that has already been accepted by OrderEventHub, without publishing it
        /// a second time. This is intentionally separate from TryCreatePlan, whose Publish call would
        /// correctly deduplicate the event and therefore never create a plan for this fallback.
        /// </summary>
        internal async Task ProcessAcceptedOrderEventFallbackAsync(OrderSnapshot snapshot, DateTime hubSeenAt)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return;

            // Defense in depth: Tick() filters persisted history, but the execution boundary must
            // independently reject stale acceptance timestamps. This protects direct callers and
            // future refactors from replaying an old Hub fact into a customer-facing fixed preset.
            if (!OrderEventAutoReplyFallback.IsFreshAcceptedAt(hubSeenAt, _messageSafetyStartedAt))
            {
                Log.Info("[订单自动回复] Hub兜底拒绝历史回放，未发送: orderId=" + snapshot.OrderId
                    + ", hubAcceptedAt=" + hubSeenAt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + ", botStartedAt=" + _messageSafetyStartedAt.ToString("yyyy-MM-dd HH:mm:ss")
                    + ", freshnessMinutes=" + OrderEventAutoReplyFallback.HubEventFreshnessWindow.TotalMinutes.ToString("0"));
                return;
            }

            var runtimeSeller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(runtimeSeller)
                || !DirectOrderIdentityResolver.IdentityEquals(runtimeSeller, snapshot.Seller))
            {
                Log.Info("[订单自动回复] Hub兜底卖家身份不匹配，未发送: snapshotSeller="
                    + snapshot.Seller + ", runtimeSeller=" + runtimeSeller + ", orderId=" + snapshot.OrderId);
                return;
            }
            snapshot.Seller = runtimeSeller;

            var resolvedBuyer = BuyerIdentityAliasService.ResolveInternalNick(snapshot.Seller, snapshot.Buyer);
            if (!string.IsNullOrWhiteSpace(resolvedBuyer)) snapshot.Buyer = resolvedBuyer;
            if (string.IsNullOrWhiteSpace(snapshot.Buyer))
            {
                Log.Info("[订单自动回复] Hub兜底缺少可验证买家身份，未发送: orderId=" + snapshot.OrderId);
                return;
            }

            var eventTime = snapshot.EventTime == DateTime.MinValue ? hubSeenAt : snapshot.EventTime;
            if (eventTime.Kind == DateTimeKind.Utc) eventTime = eventTime.ToLocalTime();
            snapshot.EventTime = eventTime;
            if (eventTime < _messageSafetyStartedAt.AddSeconds(-8))
            {
                Log.Info("[订单自动回复] Hub兜底跳过历史订单: orderId=" + snapshot.OrderId
                    + ", eventTime=" + eventTime.ToString("yyyy-MM-dd HH:mm:ss")
                    + ", botStartedAt=" + _messageSafetyStartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                return;
            }

            if (!Params.Robot.CanUseRobotReal)
            {
                Log.Info("[订单自动回复] 未发送原因=Bot已停用, orderId=" + snapshot.OrderId
                    + ", buyer=" + snapshot.Buyer);
                return;
            }

            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply)
            {
                Log.Info("[订单自动回复] 未发送原因=本店下单自动发送关闭, orderId=" + snapshot.OrderId
                    + ", buyer=" + snapshot.Buyer);
                return;
            }

            if (string.IsNullOrWhiteSpace(snapshot.Source)) snapshot.Source = "OrderEventHub统一兜底";
            if (string.IsNullOrWhiteSpace(snapshot.EventText))
            {
                snapshot.EventText = (snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ")
                    + "订单号：" + snapshot.OrderId
                    + " 订单状态：" + (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单");
            }

            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
            EnqueueNewOrderAttention(snapshot);

            var plan = new OrderPlacedReplyPlan
            {
                Seller = snapshot.Seller,
                Buyer = snapshot.Buyer,
                OrderId = snapshot.OrderId,
                EventText = snapshot.EventText,
                EventTime = snapshot.EventTime,
                ReservationKey = BuildOrderEventFallbackReservationKey(snapshot.Seller, snapshot.Buyer, snapshot.OrderId),
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = false,
                TriggerText = string.Empty,
                TriggerTime = DateTime.MinValue
            };

            var mode = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)
                ? "固定预设答案"
                : cfg.OrderPlacedReplyMode.Trim();
            var fixedPreset = !string.Equals(mode, "调用HTTP接口", StringComparison.Ordinal);

            Log.Info("[订单自动回复] Hub兜底接管已确认订单: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                + ", event=" + snapshot.EventType
                + ", source=" + snapshot.Source
                + ", hubAcceptedAt=" + hubSeenAt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + ", mode=" + mode
                + ", autoReply=" + Params.Robot.GetIsAutoReply());

            // Fixed presets are mandatory immediate business replies. Render whatever fields are
            // already present in the accepted event and let RenderTemplate safely remove unavailable
            // placeholders. Never wait for the 0/0.5/1/2/3/5/7s trade-detail enrichment sequence.
            // HTTP mode retains V2 enrichment because the external contract may explicitly require
            // those dynamic fields before the request is made.
            if (!fixedPreset
                && OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, "OrderEventHub统一兜底"))
            {
                Log.Info("[订单自动回复] Hub兜底HTTP模式已交给订单模板字段补全V2: orderId=" + plan.OrderId);
                return;
            }

            if (fixedPreset)
            {
                Log.Info("[订单自动回复] 固定预设进入即时发送路径，不等待交易字段补全: orderId="
                    + plan.OrderId);
            }
            await ProcessOrderPlacedReplyAsync(plan);
        }

        private static string BuildOrderEventFallbackReservationKey(string seller, string buyer, string orderId)
        {
            return Regex.Replace((seller ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                + "#" + Regex.Replace((buyer ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                + "#" + (orderId ?? string.Empty).Trim();
        }
    }
}
