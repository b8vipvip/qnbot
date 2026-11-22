using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// 千牛部分版本把 messageCenterNotify.response 直接作为 JSON 对象发送，
    /// 而旧 WSocketMessage.Response 是 string。仅在目标属性为 string 时保留紧凑 JSON 文本。
    /// </summary>
    internal static class QianniuWebSocketJsonCompatibility
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            var previous = JsonConvert.DefaultSettings;
            JsonConvert.DefaultSettings = () =>
            {
                var settings = previous == null ? new JsonSerializerSettings() : previous();
                if (settings == null) settings = new JsonSerializerSettings();
                if (!settings.Converters.OfType<FlexibleJsonStringConverter>().Any())
                {
                    settings.Converters.Insert(0, new FlexibleJsonStringConverter());
                }
                return settings;
            };
            Log.Info("千牛WebSocket JSON对象响应兼容已启用");
        }
    }

    internal sealed class FlexibleJsonStringConverter : JsonConverter
    {
        public override bool CanWrite { get { return false; } }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null || reader.TokenType == JsonToken.Undefined) return null;
            if (reader.TokenType == JsonToken.String) return Convert.ToString(reader.Value);
            if (reader.TokenType == JsonToken.Integer
                || reader.TokenType == JsonToken.Float
                || reader.TokenType == JsonToken.Boolean
                || reader.TokenType == JsonToken.Date)
            {
                return Convert.ToString(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            var token = JToken.Load(reader);
            return token.Type == JTokenType.String
                ? token.ToString()
                : token.ToString(Formatting.None);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 订单入站统一协调器。
    ///
    /// 设计原则：
    /// 1. receiveNewMsg/messageCenterNotify 只负责提供订单证据，不各自维护长期补扫循环；
    /// 2. 无法从 messageCenterNotify 确认 buyer 时只登记“付款可能发生”的 seller 级唤醒，绝不猜当前会话；
    /// 3. 后续 shopRobot/buyerSwitched 提供经过千牛确认的 buyer 后，再把两类信号按时间窗口关联；
    /// 4. 同一 seller+buyer 全局只允许一个 180 秒补扫任务；
    /// 5. 面板读取前后都验证 buyer，订单号/状态/时间仍沿用严格订单面板解析，最终只发布到 OrderEventHub；
    /// 6. 本类不发送消息，实际发送仍只有 OrderEventHub -> V2 -> ProcessOrderPlacedReplyAsync 一条业务链。
    /// </summary>
    internal static class OrderAutomationCoordinator
    {
        private sealed class GenericWake
        {
            public string ShopKey;
            public DateTime StartedAt;
            public DateTime LastSeenAt;
            public string Trigger;
        }

        private sealed class BuyerSignal
        {
            public QN Qn;
            public string ShopKey;
            public string Seller;
            public string Buyer;
            public DateTime SeenAt;
            public string Source;
        }

        private sealed class ProbeState
        {
            public DateTime StartedAt;
            public int Running;
        }

        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, GenericWake> GenericWakes =
            new ConcurrentDictionary<string, GenericWake>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, BuyerSignal> RecentBuyerSignals =
            new ConcurrentDictionary<string, BuyerSignal>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, ProbeState> Probes =
            new ConcurrentDictionary<string, ProbeState>(StringComparer.Ordinal);

        private static readonly int[] CorrelatedProbeDelaysMs =
            { 500, 1500, 3200, 6000, 10000, 16000, 24000, 36000, 60000, 90000, 120000, 150000, 180000 };
        private static readonly TimeSpan GenericWakeLifetime = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan BackwardBuyerCorrelation = TimeSpan.FromSeconds(20);
        private const int LastSafeAutoFocusProbeMs = 36000;

        internal static void Attach(QN qn)
        {
            if (qn == null) return;
            lock (AttachSync)
            {
                object marker;
                if (Attached.TryGetValue(qn, out marker)) return;
                qn.EvShopRobotReceriveNewMessage += OnShopRobotNewMessage;
                qn.EvBuyerSwitched += OnBuyerSwitched;
                Attached.Add(qn, new object());
            }
            Log.Info("订单入站统一协调器已绑定: seller="
                + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
        }

        internal static void ObserveGenericPaymentSignal(QN qn, string trigger)
        {
            if (qn == null || qn.Seller == null) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch (Exception ex)
            {
                Log.Info("订单入站协调器拒绝无ShopKey的付款唤醒: " + ex.Message);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)) return;

            Cleanup();
            var now = DateTime.Now;
            var key = shop.ShopKey;
            var wake = GenericWakes.AddOrUpdate(
                key,
                _ => new GenericWake { ShopKey = shop.ShopKey, StartedAt = now, LastSeenAt = now, Trigger = trigger ?? string.Empty },
                (_, old) => old == null || old.LastSeenAt < now.Subtract(GenericWakeLifetime)
                    ? new GenericWake { ShopKey = shop.ShopKey, StartedAt = now, LastSeenAt = now, Trigger = trigger ?? string.Empty }
                    : new GenericWake
                    {
                        ShopKey = shop.ShopKey,
                        StartedAt = old.StartedAt,
                        LastSeenAt = now,
                        Trigger = string.IsNullOrWhiteSpace(trigger) ? old.Trigger : trigger
                    });

            Log.Info("订单入站统一协调器记录无买家付款唤醒: seller=" + seller
                + ", trigger=" + Short(trigger, 160));

            // 千牛有时先发 buyer 级后台通知，几秒后才发 generic messageCenterNotify。
            // 反向关联最近 20 秒 buyer 信号；即使碰到无关买家，后续严格面板时间/订单证据也会 fail closed。
            foreach (var pair in RecentBuyerSignals.ToArray())
            {
                var signal = pair.Value;
                if (signal == null || signal.Qn == null) continue;
                if (!string.Equals(signal.ShopKey ?? string.Empty, shop.ShopKey, StringComparison.Ordinal)) continue;
                if (!DirectOrderIdentityResolver.IdentityEquals(signal.Seller, seller)) continue;
                if (signal.SeenAt < now.Subtract(BackwardBuyerCorrelation)) continue;
                ScheduleCorrelatedProbe(signal.Qn, signal.ShopKey, seller, signal.Buyer, wake.StartedAt,
                    "generic-after-" + (signal.Source ?? "buyer-signal"));
            }
        }

        private static void OnShopRobotNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || e.Seller == null || e.Buyer == null) return;
            ObserveBuyerSignal(qn, e.Seller.Nick, e.Buyer.Nick, "shopRobotNotify");
        }

        private static void OnBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || e.Seller == null || e.Buyer == null) return;
            ObserveBuyerSignal(qn, e.Seller.Nick, e.Buyer.Nick, "buyerSwitched");
        }

        private static void ObserveBuyerSignal(QN qn, string seller, string buyer, string source)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (qn == null || seller.Length == 0 || buyer.Length == 0) return;

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch (Exception ex)
            {
                Log.Info("订单入站协调器拒绝无ShopKey的买家信号: " + ex.Message);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)) return;
            var shopKey = shop.ShopKey;
            string normalizedBuyer;
            using (ShopSettingsScope.Enter(shop))
                normalizedBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (!string.IsNullOrWhiteSpace(normalizedBuyer)) buyer = normalizedBuyer;
            var now = DateTime.Now;
            Cleanup();

            var buyerKey = shopKey + "#" + Normalize(seller) + "#" + Normalize(buyer);
            RecentBuyerSignals[buyerKey] = new BuyerSignal
            {
                Qn = qn,
                ShopKey = shopKey,
                Seller = seller,
                Buyer = buyer,
                SeenAt = now,
                Source = source ?? string.Empty
            };

            GenericWake wake;
            if (!GenericWakes.TryGetValue(shopKey, out wake)
                || wake == null
                || wake.LastSeenAt < now.Subtract(GenericWakeLifetime))
            {
                return;
            }

            Log.Info("订单入站统一协调器关联付款唤醒与买家: seller=" + seller
                + ", buyer=" + buyer + ", source=" + (source ?? string.Empty));
            ScheduleCorrelatedProbe(qn, shopKey, seller, buyer, wake.StartedAt, source);
        }

        private static void ScheduleCorrelatedProbe(
            QN qn,
            string shopKey,
            string seller,
            string buyer,
            DateTime startedAt,
            string source)
        {
            if (qn == null || string.IsNullOrWhiteSpace(shopKey)
                || string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            ShopContext resolvedShop;
            try { resolvedShop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch { return; }
            if (resolvedShop == null || !string.Equals(resolvedShop.ShopKey, shopKey, StringComparison.Ordinal)
                || !string.Equals(resolvedShop.SellerNick, seller, StringComparison.Ordinal)) return;
            var key = shopKey + "#" + Normalize(seller) + "#" + Normalize(buyer);
            var state = Probes.GetOrAdd(key, _ => new ProbeState { StartedAt = startedAt });
            if (startedAt < state.StartedAt) state.StartedAt = startedAt;
            if (Interlocked.Exchange(ref state.Running, 1) != 0) return;

            Task.Run(async () =>
            {
                using (ShopSettingsScope.Enter(resolvedShop))
                {
                try
                {
                    Log.Info("订单入站统一协调器启动目标买家面板补扫: seller=" + seller
                        + ", buyer=" + buyer + ", source=" + (source ?? string.Empty));
                    foreach (var targetDelay in CorrelatedProbeDelaysMs)
                    {
                        var elapsed = (int)Math.Max(0, Math.Min(int.MaxValue,
                            (DateTime.Now - state.StartedAt).TotalMilliseconds));
                        var wait = Math.Max(0, targetDelay - elapsed);
                        if (wait > 0) await Task.Delay(wait).ConfigureAwait(false);

                        ProbeState current;
                        if (!Probes.TryGetValue(key, out current) || !ReferenceEquals(current, state)) return;

                        var mayFocus = targetDelay >= 3200 && targetDelay <= LastSafeAutoFocusProbeMs;
                        if (await TryActivateTargetIfSafeAsync(qn, shopKey, seller, buyer, mayFocus).ConfigureAwait(false))
                        {
                            if (await qn.TryRecoverVisibleOrderPanelForCoordinatorAsync(
                                seller,
                                buyer,
                                "统一订单补扫@" + targetDelay + "ms/" + (source ?? string.Empty),
                                state.StartedAt).ConfigureAwait(false))
                            {
                                return;
                            }
                        }
                    }
                    Log.Info("订单入站统一协调器补扫结束：180秒内未发现可确认的新订单。seller="
                        + seller + ", buyer=" + buyer);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("订单入站统一协调器补扫异常: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + Short(ex.Message, 240), 20);
                }
                finally
                {
                    ProbeState current;
                    if (Probes.TryGetValue(key, out current) && ReferenceEquals(current, state))
                    {
                        ProbeState ignored;
                        Probes.TryRemove(key, out ignored);
                    }
                }
                }
            });
        }

        private static async Task<bool> TryActivateTargetIfSafeAsync(
            QN qn,
            string shopKey,
            string seller,
            string buyer,
            bool mayFocus)
        {
            DbEntity.Conversation current = null;
            try
            {
                var response = await qn.GetCurrentConversationID().ConfigureAwait(false);
                current = response == null ? null : response.Result;
            }
            catch { }

            if (current != null && !string.IsNullOrWhiteSpace(current.Nick)
                && BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
            {
                return true;
            }
            if (!mayFocus) return false;

            string blockedReason;
            if (!BotActivityCoordinator.IsSafeToAutoFocus(seller, out blockedReason, shopKey))
            {
                Log.Info("订单入站统一协调器暂不切换买家：" + blockedReason + ", target=" + buyer);
                return false;
            }

            var openNick = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (string.IsNullOrWhiteSpace(openNick)) openNick = buyer;
            qn.OpenChat(openNick);
            if (!string.Equals(openNick, buyer, StringComparison.Ordinal))
            {
                await Task.Delay(180).ConfigureAwait(false);
                qn.OpenChat(buyer);
            }

            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(220).ConfigureAwait(false);
                try
                {
                    var response = await qn.GetCurrentConversationID().ConfigureAwait(false);
                    current = response == null ? null : response.Result;
                }
                catch
                {
                    current = null;
                }
                if (current != null && !string.IsNullOrWhiteSpace(current.Nick)
                    && BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                {
                    Log.Info("订单入站统一协调器已在Bot空闲时切换到目标买家: seller="
                        + seller + ", buyer=" + buyer);
                    return true;
                }
            }
            return false;
        }

        private static void Cleanup()
        {
            var now = DateTime.Now;
            foreach (var pair in GenericWakes)
            {
                if (pair.Value != null && pair.Value.LastSeenAt >= now.Subtract(GenericWakeLifetime)) continue;
                GenericWake ignored;
                GenericWakes.TryRemove(pair.Key, out ignored);
            }
            foreach (var pair in RecentBuyerSignals)
            {
                if (pair.Value != null && pair.Value.SeenAt >= now.AddMinutes(-3)) continue;
                BuyerSignal ignored;
                RecentBuyerSignals.TryRemove(pair.Key, out ignored);
            }
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    /// <summary>
    /// messageCenterNotify 兼容解析只做两件事：
    /// - 有 seller+buyer+orderId+状态的强证据时，交给统一结构化订单链；
    /// - 证据不足时只通知 OrderAutomationCoordinator，等待 buyer 级信号后再核对面板。
    /// 不再在这里维护第二套“当前会话 180 秒补扫”与第二套去重状态。
    /// </summary>
    internal static class OrderPaymentNotificationFallback
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|申请退款|退款中|交易关闭|订单关闭",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LabeledOrderRegex = new Regex(
            "(?:订单号|订单编号|主订单号|子订单号|交易号)\\s*[:：#]?\\s*(\\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] StrongOrderKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "biztradeid", "tradeid", "tid"
        };

        private static readonly string[] BuyerAliases =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "customernick", "customername", "customerid", "customeruid",
            "contactnick", "contactname", "contactid", "conversationnick", "conversationname",
            "oppositenick", "oppositename", "peernick", "peername",
            "sendernick", "sendername", "fromnick", "fromname",
            "targetnick", "targetname", "membernick", "membername",
            "usernick", "username", "usernickname"
        };

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            _timer = new Timer(_ => Attach(), null, 0, 750);
        }

        private static void Attach()
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.Where(x => x != null).ToArray(); }
            catch { return; }

            foreach (var qn in qns)
            {
                lock (Sync)
                {
                    object marker;
                    if (Attached.TryGetValue(qn, out marker)) continue;
                    qn.EvMessageNotity += OnMessageNotify;
                    OrderAutomationCoordinator.Attach(qn);
                    Attached.Add(qn, new object());
                }
                Log.Info("付款通知兼容解析已绑定: seller="
                    + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
            }
        }

        private static async void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty).Trim();
            if (qn == null) return;
            if (raw.Length == 0)
            {
                OrderAutomationCoordinator.ObserveGenericPaymentSignal(qn, "messageCenterNotify空载荷");
                Log.Info("付款通知收到空载荷，已转入独立订单补扫，不等待买家文本/图片处理。");
                return;
            }

            ShopContext messageShop;
            try { messageShop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch (Exception ex)
            {
                Log.Info("付款通知兼容解析拒绝无ShopKey消息: " + ex.Message);
                return;
            }
            if (messageShop == null || string.IsNullOrWhiteSpace(messageShop.ShopKey)) return;
            var hash = messageShop.ShopKey + "#" + Hash(raw);
            CleanupReservations();

            try
            {
                var root = ParseExpanded(raw);
                var flat = Flatten(root);
                var combined = raw + " " + string.Join(" ", flat
                    .Select(x => x == null ? string.Empty : x.Value)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Take(250));

                if (!EventCueRegex.IsMatch(combined))
                {
                    OrderAutomationCoordinator.ObserveGenericPaymentSignal(
                        qn,
                        "messageCenterNotify缺少明确订单状态");
                    return;
                }

                var orderId = ResolveOrderId(flat, combined);
                if (string.IsNullOrWhiteSpace(orderId))
                {
                    OrderAutomationCoordinator.ObserveGenericPaymentSignal(
                        qn,
                        "messageCenterNotify缺少可验证订单号");
                    return;
                }

                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                var buyer = ResolveBuyer(flat, seller, orderId);
                if (string.IsNullOrWhiteSpace(buyer))
                {
                    Log.Info("付款通知已解析订单号但仍缺少买家身份，交给统一协调器等待buyer信号: orderId="
                        + orderId + ", payloadHash=" + hash);
                    OrderAutomationCoordinator.ObserveGenericPaymentSignal(
                        qn,
                        "messageCenterNotify有订单号但缺少买家身份");
                    return;
                }

                DateTime until;
                if (Reservations.TryGetValue(hash, out until) && until >= DateTime.Now) return;
                Reservations[hash] = DateTime.Now.AddMinutes(2);

                var paid = Regex.IsMatch(combined,
                    "已付款|付款成功|支付成功|待卖家发货|WAIT_SELLER_SEND_GOODS",
                    RegexOptions.IgnoreCase);
                var summary = (paid ? "买家已付款 " : "买家已下单 ")
                    + "订单号：" + orderId
                    + " 订单状态：" + (paid ? "已付款" : "新下单");
                var synthetic = new QNChatMessage { summary = summary };
                Log.Info("付款通知兼容解析成功: seller=" + seller
                    + ", buyer=" + buyer + ", orderId=" + orderId + ", paid=" + paid);
                using (ShopSettingsScope.Enter(messageShop))
                {
                    await qn.ProcessDirectOrderMessageAsync(
                        synthetic,
                        seller,
                        buyer,
                        "messageCenterNotify统一兼容解析");
                }
            }
            catch (Exception ex)
            {
                Log.Info("付款通知兼容解析失败，交给统一协调器: payloadHash=" + hash
                    + ", error=" + Short(ex.Message, 240));
                OrderAutomationCoordinator.ObserveGenericPaymentSignal(qn, "messageCenterNotify解析异常");
            }
        }

        private static JToken ParseExpanded(string raw)
        {
            JToken token;
            try { token = JToken.Parse(raw); }
            catch { return new JValue(raw ?? string.Empty); }
            for (var i = 0; i < 5 && token.Type == JTokenType.String; i++)
            {
                var nested = token.ToString().Trim();
                if (!(nested.StartsWith("{") || nested.StartsWith("["))) break;
                try { token = JToken.Parse(nested); }
                catch { break; }
            }
            return token;
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var result = new List<FlatValue>();
            Walk(root, string.Empty, result, 0);
            return result;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 16 || output.Count >= 1400) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value,
                        path.Length == 0 ? property.Name : path + "." + property.Name,
                        output,
                        depth + 1);
                    if (output.Count >= 1400) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 150 || output.Count >= 1400) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;

            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 12000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value.Length > 4000 ? value.Substring(0, 4000) : value });

            if (token.Type == JTokenType.String && depth < 14)
            {
                var nested = value.Trim();
                if (nested.StartsWith("{") || nested.StartsWith("["))
                {
                    try { Walk(JToken.Parse(nested), path + ".json", output, depth + 1); }
                    catch { }
                }
            }
        }

        private static string ResolveOrderId(IList<FlatValue> flat, string combined)
        {
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                if (!StrongOrderKeys.Contains(Normalize(item.Key))) continue;
                var digits = DigitsOnly(item.Value, 8, 40);
                if (digits.Length > 0) return digits;
            }
            var match = LabeledOrderRegex.Match(combined ?? string.Empty);
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
                var key = Normalize(item.Key);
                var path = Normalize(item.Path);
                var score = 0;
                if (BuyerAliases.Contains(key)) score += 100;
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

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static void CleanupReservations()
        {
            var now = DateTime.Now;
            foreach (var pair in Reservations)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                Reservations.TryRemove(pair.Key, out ignored);
            }
        }
    }

    public partial class QN
    {
        /// <summary>
        /// 统一订单协调器专用的严格面板核对：只接受 probeStartedAt 前 20 秒之后的订单。
        /// 与旧 TryRecoverVisibleOrderPanelAsync 的“Bot 启动后”窗口不同，避免切换买家时把历史订单当新订单。
        /// </summary>
        internal async Task<bool> TryRecoverVisibleOrderPanelForCoordinatorAsync(
            string sellerHint,
            string buyerHint,
            string source,
            DateTime probeStartedAt)
        {
            var runtimeSeller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            sellerHint = (sellerHint ?? string.Empty).Trim();
            buyerHint = (buyerHint ?? string.Empty).Trim();
            if (runtimeSeller.Length == 0 || buyerHint.Length == 0 || cdp == null) return false;
            if (sellerHint.Length > 0 && !DirectOrderIdentityResolver.IdentityEquals(runtimeSeller, sellerHint)) return true;

            DbEntity.Conversation before;
            try
            {
                var current = await GetCurrentConversationID().ConfigureAwait(false);
                before = current == null ? null : current.Result;
            }
            catch
            {
                return false;
            }
            if (before == null || string.IsNullOrWhiteSpace(before.Nick)
                || !BuyerIdentityAliasService.AreEquivalent(runtimeSeller, before.Nick, buyerHint))
            {
                return false;
            }

            BuyerIdentityAliasService.Observe(runtimeSeller, before.Nick, before.Display, before.TargetId);
            var verifiedBuyer = BuyerIdentityAliasService.ResolveInternalNick(runtimeSeller, before.Nick);
            if (string.IsNullOrWhiteSpace(verifiedBuyer)) verifiedBuyer = buyerHint;

            string raw;
            try
            {
                raw = await cdp.EvaluateExpressionAsync(
                    VisibleOrderPanelExpression,
                    "统一订单协调器读取当前买家右侧近3个月订单面板").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info("统一订单协调器DOM读取失败: seller=" + runtimeSeller
                    + ", buyer=" + verifiedBuyer + ", error=" + ex.Message);
                return false;
            }

            var panelText = ExtractVisibleOrderPanelText(raw);
            if (string.IsNullOrWhiteSpace(panelText)) return false;

            DbEntity.Conversation after;
            try
            {
                var current = await GetCurrentConversationID().ConfigureAwait(false);
                after = current == null ? null : current.Result;
            }
            catch
            {
                return false;
            }
            if (after == null || string.IsNullOrWhiteSpace(after.Nick)
                || !BuyerIdentityAliasService.AreEquivalent(runtimeSeller, after.Nick, verifiedBuyer))
            {
                Log.Info("统一订单协调器已取消：读取期间当前买家变化。seller=" + runtimeSeller
                    + ", expectedBuyer=" + verifiedBuyer
                    + ", currentBuyer=" + (after == null ? string.Empty : after.Nick));
                return false;
            }

            var candidates = ParseVisibleOrderPanelCandidates(panelText)
                .OrderByDescending(x => x.PaidAt ?? x.CreatedAt ?? DateTime.MinValue)
                .Take(3)
                .ToList();
            if (candidates.Count == 0) return false;

            var now = DateTime.Now;
            var freshFloor = (probeStartedAt == DateTime.MinValue ? now : probeStartedAt).AddSeconds(-20);
            var sawFreshSupportedOrder = false;
            var sawFreshUnsupportedOrder = false;
            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.OrderId)) continue;
                var eventTime = candidate.PaidAt ?? candidate.CreatedAt;
                if (!eventTime.HasValue) continue;
                if (eventTime.Value > now.AddMinutes(2) || eventTime.Value < freshFloor) continue;

                if (VisiblePanelUnsupportedStatuses.Any(x => string.Equals(x, candidate.TradeStatus, StringComparison.Ordinal)))
                {
                    sawFreshUnsupportedOrder = true;
                    continue;
                }

                var paid = candidate.PaidAt.HasValue
                    || VisiblePanelPaidStatuses.Any(x => string.Equals(x, candidate.TradeStatus, StringComparison.Ordinal));
                var eventType = paid ? OrderEventType.Paid : OrderEventType.Created;
                var text = new StringBuilder();
                text.Append(paid ? "买家已付款 " : "买家已下单 ")
                    .Append("订单号：").Append(candidate.OrderId);
                if (!string.IsNullOrWhiteSpace(candidate.TradeStatus))
                    text.Append(" 订单状态：").Append(candidate.TradeStatus);
                if (candidate.CreatedAt.HasValue)
                    text.Append(" 下单时间：").Append(candidate.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                if (candidate.PaidAt.HasValue)
                    text.Append(" 付款时间：").Append(candidate.PaidAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));

                var snapshot = new OrderSnapshot
                {
                    ShopKey = ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey,
                    Seller = runtimeSeller,
                    Buyer = verifiedBuyer,
                    OrderId = candidate.OrderId,
                    TradeStatus = candidate.TradeStatus,
                    IsPaid = paid,
                    CreatedAt = candidate.CreatedAt,
                    PaidAt = candidate.PaidAt,
                    Source = "千牛右侧订单面板统一协调补偿",
                    DetectedAt = now,
                    EventTime = eventTime.Value,
                    EventType = eventType,
                    EventText = text.ToString()
                };

                var publish = OrderEventHub.Publish(snapshot);
                if (publish != null && publish.Detected)
                {
                    sawFreshSupportedOrder = true;
                    Log.Info((publish.Accepted
                        ? "统一订单协调器识别并发布"
                        : "统一订单协调器订单已由其他通道处理/去重")
                        + ": seller=" + runtimeSeller + ", buyer=" + verifiedBuyer
                        + ", orderId=" + candidate.OrderId + ", event=" + eventType
                        + ", trigger=" + (source ?? string.Empty));
                }
            }

            return sawFreshSupportedOrder || sawFreshUnsupportedOrder;
        }
    }
}
