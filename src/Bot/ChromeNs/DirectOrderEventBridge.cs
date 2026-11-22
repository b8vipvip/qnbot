using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using DbEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    /// 直接下单/付款可能以千牛系统卡片或 messageCenterNotify 到达，而不是普通 buyer -> seller 消息。
    /// 本桥接器订阅 QN 暴露的原始事件，在原有 IsBuyerMessage 过滤之前处理订单。
    /// 当新版千牛只刷新右侧“近3个月订单”而不投递订单卡片时，也会对当前已验证买家做被动面板兜底。
    /// </summary>
    internal static class DirectOrderEventBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private sealed class NotificationEnvelope
        {
            public string Seller;
            public string Buyer;
            public string OrderId;
            public string Text;
        }

        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly ConcurrentDictionary<string, DateTime> RawReservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> VisiblePanelScanVersions =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private static readonly int[] VisiblePanelScanDelaysMs = { 250, 900, 1800, 3200, 5200, 8000 };
        private static long _visiblePanelScanVersion;
        private static Timer _timer;
        private static bool _initialized;

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Never rely on a JToken/double round-trip for order identifiers. Taobao order IDs are
        // commonly above JavaScript's 2^53 exact-integer boundary, so preserve the literal digits
        // from the raw WebSocket payload before any JSON object conversion can round them.
        private static readonly Regex RawOrderIdKeyRegex = new Regex(
            @"[""']?(?:orderid|bizorderid|mainorderid|suborderid|tradeid|biztradeid|tid)[""']?\s*[:=]\s*[""']?(?<id>\d{8,40})[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] OrderIdKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "tradeid", "tid", "biztradeid"
        };

        private static readonly string[] BuyerKeys =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "contactnick", "contactname", "conversationnick", "usernick", "usernickname"
        };

        private static readonly string[] SellerKeys =
        {
            "sellernick", "sellernickname", "sellername", "loginid", "shopnick", "shopname"
        };

        private static readonly string[] ItemTitleKeys =
        {
            "itemtitle", "auctiontitle", "producttitle", "goodstitle", "itemname", "productname", "subject"
        };

        private static readonly string[] SkuTextKeys =
        {
            "skutext", "skuname", "skupropertiesname", "propertiesname", "spec", "specification", "skudesc"
        };

        private static readonly string[] AmountKeys =
        {
            "paidamount", "payment", "actualfee", "paidfee", "realpay", "actualamount",
            "totalamount", "totalfee", "orderamount", "amount", "totalprice"
        };

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                _timer = new Timer(_ => Attach(), null, 0, 750);
            }
        }

        private static void Attach()
        {
            QN[] instances;
            try
            {
                instances = QN.QNSet == null ? new QN[0] : QN.QNSet.Where(x => x != null).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var qn in instances)
            {
                lock (Sync)
                {
                    object marker;
                    if (Attached.TryGetValue(qn, out marker)) continue;
                    qn.EvRecieveNewMessage += OnReceiveNewMessage;
                    qn.EvMessageNotity += OnMessageCenterNotify;
                    qn.EvShopRobotReceriveNewMessage += OnShopRobotNewMessage;
                    qn.EvBuyerSwitched += OnBuyerSwitched;
                    Attached.Add(qn, new object());
                }
                Log.Info("直接下单系统事件桥接已绑定: seller="
                    + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
            }
        }

        private static async void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length == 0 || !LooksPotential(raw)) return;

            ShopContext shop;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
            }
            catch (Exception ex)
            {
                Log.Info("receiveNewMsg订单卡片无法确认来源店铺，已跳过: error=" + ex.Message);
                return;
            }
            var runtimeSeller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, runtimeSeller, StringComparison.Ordinal))
            {
                Log.Info("receiveNewMsg订单卡片店铺身份不一致，已跳过: seller=" + runtimeSeller);
                return;
            }

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();
                var potential = messages.Where(MessageLooksPotential).ToList();
                // A receiveNewMsg batch may contain several cards. Only pass a raw exact hint when
                // both the payload and the business batch identify one unambiguous order.
                var exactOrderIdHint = potential.Count == 1 ? ExtractExactOrderIdFromRaw(raw) : string.Empty;
                using (ShopSettingsScope.Enter(shop, false))
                {
                    foreach (var message in potential)
                    {
                        var messageSeller = DirectOrderIdentityResolver.ResolveSeller(qn, message, null);
                        if (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, messageSeller))
                        {
                            Log.Info("receiveNewMsg订单卡片卖家身份与来源店铺不一致，已跳过: shop=" + shop.ShopKey);
                            continue;
                        }
                        await qn.ProcessDirectOrderMessageAsync(
                            message,
                            messageSeller,
                            DirectOrderIdentityResolver.ResolveBuyer(qn, message, null),
                            "receiveNewMsg系统订单卡片",
                            exactOrderIdHint);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("直接下单 receiveNewMsg 桥接解析失败: length=" + raw.Length + ", error=" + ex.Message);
            }
        }

        private static async void OnMessageCenterNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length == 0 || !LooksPotential(raw)) return;

            ShopContext shop;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
            }
            catch (Exception ex)
            {
                Log.Info("消息中心订单通知无法确认来源店铺，已跳过: error=" + ex.Message);
                return;
            }
            var runtimeSeller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, runtimeSeller, StringComparison.Ordinal))
            {
                Log.Info("消息中心订单通知店铺身份不一致，已跳过: seller=" + runtimeSeller);
                return;
            }

            var hash = Hash(raw);
            var reservationKey = shop.ShopKey + "#" + hash;
            CleanupReservations();
            DateTime until;
            if (RawReservations.TryGetValue(reservationKey, out until) && until >= DateTime.Now) return;
            RawReservations[reservationKey] = DateTime.Now.AddMinutes(2);

            try
            {
                JToken token;
                try { token = JToken.Parse(raw); }
                catch { token = new JValue(raw); }
                var envelope = BuildEnvelope(qn, token, raw);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.OrderId)) return;
                if (string.IsNullOrWhiteSpace(envelope.Buyer))
                {
                    Log.Info("消息中心检测到订单通知但缺少买家昵称，等待详细卡片补偿: orderId="
                        + envelope.OrderId + ", payloadHash=" + hash);
                    return;
                }
                if (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, envelope.Seller))
                {
                    Log.Info("消息中心订单通知卖家身份与来源千牛会话不一致，已跳过: shop="
                        + shop.ShopKey + ", orderId=" + envelope.OrderId);
                    return;
                }

                var synthetic = new QNChatMessage { summary = envelope.Text };
                using (ShopSettingsScope.Enter(shop, false))
                {
                    await qn.ProcessDirectOrderMessageAsync(
                        synthetic,
                        envelope.Seller,
                        envelope.Buyer,
                        "messageCenterNotify订单通知");
                }
            }
            catch (Exception ex)
            {
                Log.Info("消息中心订单通知解析失败: length=" + raw.Length
                    + ", payloadHash=" + hash + ", error=" + ex.Message);
            }
        }

        private static void OnShopRobotNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || e.Seller == null || e.Buyer == null) return;
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
            var buyer = (e.Buyer.Nick ?? string.Empty).Trim();
            Log.Info("直接下单桥接观察到后台会话通知: seller=" + seller + ", buyer=" + buyer);
            ScheduleVisibleOrderPanelScan(qn, seller, buyer, "shopRobotNotify");
        }

        private static void OnBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || e.Seller == null || e.Buyer == null) return;
            ScheduleVisibleOrderPanelScan(
                qn,
                e.Seller.Nick,
                e.Buyer.Nick,
                "buyerSwitched");
        }

        private static void ScheduleVisibleOrderPanelScan(QN qn, string seller, string buyer, string source)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (qn == null || seller.Length == 0 || buyer.Length == 0) return;

            ShopContext shop;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
            }
            catch (Exception ex)
            {
                Log.Info("右侧订单面板兜底无法确认来源店铺，已跳过: seller=" + seller
                    + ", buyer=" + buyer + ", error=" + ex.Message);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal))
            {
                Log.Info("右侧订单面板兜底来源店铺与卖家昵称不一致，已跳过: seller=" + seller
                    + ", buyer=" + buyer);
                return;
            }

            string normalizedBuyer;
            using (ShopSettingsScope.Enter(shop, false))
            {
                normalizedBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            }
            if (!string.IsNullOrWhiteSpace(normalizedBuyer)) buyer = normalizedBuyer;
            // Same seller/buyer nicknames can exist in different shops. Scope both the
            // reservation and the async retry generation by the exact QN ShopKey.
            var key = shop.ShopKey + "#" + NormalizeIdentityKey(seller) + "#" + NormalizeIdentityKey(buyer);
            var capturedShopKey = shop.ShopKey;

            var version = Interlocked.Increment(ref _visiblePanelScanVersion);
            VisiblePanelScanVersions[key] = version;
            Task.Run(async () =>
            {
                var elapsed = 0;
                try
                {
                    foreach (var targetDelay in VisiblePanelScanDelaysMs)
                    {
                        var wait = Math.Max(0, targetDelay - elapsed);
                        if (wait > 0) await Task.Delay(wait).ConfigureAwait(false);
                        elapsed = targetDelay;

                        long latest;
                        if (!VisiblePanelScanVersions.TryGetValue(key, out latest) || latest != version) return;
                        ShopContext currentShop;
                        try { currentShop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
                        catch { return; }
                        if (currentShop == null
                            || !string.Equals(currentShop.ShopKey, capturedShopKey, StringComparison.Ordinal)
                            || !string.Equals(currentShop.SellerNick, seller, StringComparison.Ordinal))
                            return;

                        using (ShopSettingsScope.Enter(currentShop, false))
                        {
                            if (await qn.TryRecoverVisibleOrderPanelAsync(seller, buyer, source).ConfigureAwait(false))
                                return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("右侧订单面板兜底调度异常: seller=" + seller + ", buyer=" + buyer
                        + ", source=" + source + ", error=" + ex.Message);
                }
                finally
                {
                    long latest;
                    if (VisiblePanelScanVersions.TryGetValue(key, out latest) && latest == version)
                    {
                        long ignored;
                        VisiblePanelScanVersions.TryRemove(key, out ignored);
                    }
                }
            });
        }

        private static string NormalizeIdentityKey(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static bool MessageLooksPotential(QNChatMessage message)
        {
            if (message == null) return false;
            try { return LooksPotential(JObject.FromObject(message).ToString(Formatting.None)); }
            catch
            {
                var text = (message.summary ?? string.Empty)
                    + " " + (message.originalData == null ? string.Empty : message.originalData.text ?? string.Empty);
                return LooksPotential(text);
            }
        }

        private static bool LooksPotential(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length < 8) return false;
            var lower = raw.ToLowerInvariant();
            var cue = raw.Contains("订单") || raw.Contains("下单") || raw.Contains("已付款")
                || raw.Contains("支付成功") || raw.Contains("待发货")
                || lower.Contains("orderid") || lower.Contains("bizorderid")
                || lower.Contains("tradeid") || lower.Contains("wait_seller_send_goods");
            if (!cue) return false;
            return OrderIdTextRegex.IsMatch(raw)
                || Regex.IsMatch(lower,
                    "(?:orderid|bizorderid|mainorderid|suborderid|tradeid|\\\"tid\\\")\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\d{8,}");
        }

        private static NotificationEnvelope BuildEnvelope(QN qn, JToken token, string raw)
        {
            var flat = Flatten(token);
            var text = string.Join(" ", flat
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 500)
                .Select(x => x.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(180));
            if (string.IsNullOrWhiteSpace(text)) text = Short(raw, 1800);

            // Prefer the literal raw payload over parsed numeric tokens. This is the only
            // representation guaranteed to preserve IDs above 2^53 exactly.
            var orderId = ExtractExactOrderIdFromRaw(raw);
            if (string.IsNullOrWhiteSpace(orderId))
                orderId = DigitsOnly(FindValue(flat, OrderIdKeys), 8, 40);
            if (string.IsNullOrWhiteSpace(orderId))
            {
                var match = OrderIdTextRegex.Match(text);
                if (match.Success) orderId = match.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(orderId)) return null;

            var seller = FindValue(flat, SellerKeys);
            if (string.IsNullOrWhiteSpace(seller) && qn.Seller != null) seller = qn.Seller.Nick;
            var buyer = FindIdentity(flat, seller);
            var item = FindValue(flat, ItemTitleKeys);
            var sku = FindValue(flat, SkuTextKeys);
            var amount = FindValue(flat, AmountKeys);
            var paid = IsPaid(text);

            var summary = new StringBuilder();
            summary.Append(paid ? "买家已付款 " : "买家已下单 ");
            summary.Append("订单号：").Append(orderId);
            if (!string.IsNullOrWhiteSpace(item)) summary.Append(" 商品：").Append(Short(item, 240));
            if (!string.IsNullOrWhiteSpace(sku)) summary.Append(" 规格：").Append(Short(sku, 180));
            if (!string.IsNullOrWhiteSpace(amount)) summary.Append(paid ? " 实付：" : " 金额：").Append(Short(amount, 60));
            summary.Append(" 订单状态：").Append(paid ? "已付款" : "新下单");

            return new NotificationEnvelope
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = orderId,
                Text = summary.ToString()
            };
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var result = new List<FlatValue>();
            Walk(root, string.Empty, result, 0);
            return result;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 14 || output.Count >= 900) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var p in ((JObject)token).Properties())
                {
                    Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, output, depth + 1);
                    if (output.Count >= 900) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var i = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + i + "]", output, depth + 1);
                    if (++i >= 100 || output.Count >= 900) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;
            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 3000) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value });
        }

        internal static string ExtractExactOrderIdFromRaw(string raw)
        {
            raw = raw ?? string.Empty;
            if (raw.Length == 0) return string.Empty;

            var candidates = new List<string>();
            foreach (Match match in RawOrderIdKeyRegex.Matches(raw))
            {
                var id = match.Groups["id"].Value;
                if (!string.IsNullOrWhiteSpace(id)) candidates.Add(id);
            }
            foreach (Match match in OrderIdTextRegex.Matches(raw))
            {
                var id = match.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(id)) candidates.Add(id);
            }

            var distinct = candidates
                .Where(x => x.Length >= 8 && x.Length <= 40)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return distinct.Count == 1 ? distinct[0] : string.Empty;
        }

        private static string FindIdentity(IList<FlatValue> flat, string seller)
        {
            var direct = FindValue(flat, BuyerKeys);
            if (UsableIdentity(direct, seller)) return direct.Trim();
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || !UsableIdentity(item.Value, seller)) continue;
                var path = NormalizeKey(item.Path);
                if ((path.Contains("buyer") || path.Contains("contact") || path.Contains("conversation"))
                    && (path.EndsWith("nick") || path.EndsWith("name") || path.EndsWith("uid")))
                {
                    return item.Value.Trim();
                }
            }
            return string.Empty;
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
                if (set.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static bool IsPaid(string text)
        {
            text = (text ?? string.Empty).ToLowerInvariant();
            return text.Contains("已付款") || text.Contains("支付成功")
                || text.Contains("买家已付款") || text.Contains("wait_seller_send_goods")
                || text.Contains("paid");
        }

        private static bool UsableIdentity(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
        }

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string NormalizeKey(string value)
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
            foreach (var pair in RawReservations)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                RawReservations.TryRemove(pair.Key, out ignored);
            }
        }
    }

    internal static class DirectOrderIdentityResolver
    {
        public static string ResolveSeller(QN qn, QNChatMessage message, string hint)
        {
            if (!string.IsNullOrWhiteSpace(hint)) return hint.Trim();
            if (qn != null && qn.Seller != null && !string.IsNullOrWhiteSpace(qn.Seller.Nick)) return qn.Seller.Nick.Trim();
            if (message != null && message.loginid != null && !string.IsNullOrWhiteSpace(message.loginid.nick)) return message.loginid.nick.Trim();
            return string.Empty;
        }

        public static string ResolveBuyer(QN qn, QNChatMessage message, string hint)
        {
            var seller = ResolveSeller(qn, message, null);
            if (Candidate(hint, seller)) return hint.Trim();
            if (message == null) return string.Empty;

            var from = message.fromid == null ? string.Empty : message.fromid.nick;
            var to = message.toid == null ? string.Empty : message.toid.nick;
            if (Candidate(from, seller)) return from.Trim();
            if (Candidate(to, seller)) return to.Trim();

            try
            {
                foreach (var token in JObject.FromObject(message).DescendantsAndSelf())
                {
                    var property = token.Parent as JProperty;
                    if (property == null || token.Type == JTokenType.Object || token.Type == JTokenType.Array) continue;
                    var key = Normalize(property.Name);
                    var path = Normalize(token.Path);
                    var allowed = key == "buyernick" || key == "buyername" || key == "contactnick"
                        || key == "conversationnick" || key == "usernick"
                        || ((path.Contains("buyer") || path.Contains("conversation") || path.Contains("contact"))
                            && (key == "nick" || key == "name"));
                    if (!allowed) continue;
                    var value = token.ToString();
                    if (Candidate(value, seller)) return value.Trim();
                }
            }
            catch { }
            return string.Empty;
        }

        public static bool IdentityEquals(string left, string right)
        {
            var a = NormalizeIdentity(left);
            var b = NormalizeIdentity(right);
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var ah = a.Split(':')[0];
            var bh = b.Split(':')[0];
            return ah.Length >= 4 && ah == bh;
        }

        public static bool IsMessageFromShop(QNChatMessage message, ShopContext shop)
        {
            if (message == null || message.fromid == null || message.toid == null
                || shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || string.IsNullOrWhiteSpace(shop.SellerNick)) return false;
            var seller = shop.SellerNick.Trim();
            var fromIsSeller = IdentityEquals(message.fromid.nick, seller);
            var toIsSeller = IdentityEquals(message.toid.nick, seller);
            if (fromIsSeller == toIsSeller) return false;

            var loginNick = message.loginid == null ? string.Empty : (message.loginid.nick ?? string.Empty).Trim();
            if (loginNick.Length > 0 && !IdentityEquals(loginNick, seller)) return false;
            if (!shop.HasStableSellerId) return true;

            var expectedTargetId = (shop.SellerId ?? string.Empty).Trim();
            var endpointTargetId = fromIsSeller
                ? (message.fromid.targetId ?? string.Empty).Trim()
                : (message.toid.targetId ?? string.Empty).Trim();
            var loginTargetId = message.loginid == null ? string.Empty
                : (message.loginid.targetId ?? string.Empty).Trim();
            return (endpointTargetId.Length == 0 || string.Equals(endpointTargetId, expectedTargetId, StringComparison.Ordinal))
                && (loginTargetId.Length == 0 || string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal));
        }

        private static bool Candidate(string value, string seller)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 160) return false;
            if (IdentityEquals(value, seller)) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品")) return false;
            return !Regex.IsMatch(value, @"^\d{16,}$");
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string NormalizeIdentity(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("cntaobao", StringComparison.Ordinal)) value = value.Substring("cntaobao".Length);
            return value;
        }
    }

    public partial class QN
    {
        private sealed class VisibleOrderPanelCandidate
        {
            public string OrderId;
            public string TradeStatus;
            public DateTime? CreatedAt;
            public DateTime? PaidAt;
            public string Segment;
        }

        private static readonly Regex VisiblePanelOrderIdRegex = new Regex(
            @"(?<!\d)(\d{16,24})(?!\d)",
            RegexOptions.Compiled);
        private static readonly Regex VisiblePanelCreatedAtRegex = new Regex(
            @"(?<time>20\d{2}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2})?)\s*下单",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VisiblePanelPaidAtRegex = new Regex(
            @"(?<time>20\d{2}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2})?)\s*(?:付款|支付)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly string[] VisiblePanelPaidStatuses =
        {
            "待发货", "已付款", "已发货", "交易成功", "已完成"
        };
        private static readonly string[] VisiblePanelUnsupportedStatuses =
        {
            "退款中", "订单关闭", "交易关闭", "已关闭", "已取消"
        };
        private static readonly string[] VisiblePanelStatuses =
        {
            "等待买家付款", "待付款", "待发货", "已付款", "已发货", "交易成功", "已完成",
            "退款中", "订单关闭", "交易关闭", "已关闭", "已取消"
        };

        // 只读取带“近3个月订单/近三个月订单”锚点的局部 DOM。用文本节点定位后仅向上找有限层祖先，
        // 避免遍历整页元素反复读取 innerText，在低内存 Windows Server 上也保持轻量。
        private const string VisibleOrderPanelExpression = @"(function(){
var anchors=['近3个月订单','近三个月订单'];
var orderRe=/(?:订单号\s*[:：]?\s*)?\d{16,24}/;
var strongRe=/(?:下单|付款|支付|待发货|待付款|已付款|已发货|交易成功|已完成|订单关闭|交易关闭|退款中)/;
var best='';
function norm(v){return (v||'').replace(/\s+/g,' ').trim();}
function hasAnchor(v){for(var i=0;i<anchors.length;i++){if(v.indexOf(anchors[i])>=0)return true;}return false;}
function consider(v){
  v=norm(v);
  if(v.length<24||v.length>16000||!hasAnchor(v)||!orderRe.test(v)||!strongRe.test(v))return;
  if(!best||v.length<best.length)best=v;
}
function scan(doc,depth){
  if(!doc||depth>3)return;
  var root=doc.body||doc.documentElement;
  if(!root)return;
  try{
    var walker=doc.createTreeWalker(root,4,null,false),node,visited=0;
    while((node=walker.nextNode())&&visited++<12000){
      var raw=norm(node.nodeValue);
      if(!hasAnchor(raw))continue;
      var el=node.parentElement;
      for(var level=0;el&&level<10;level++,el=el.parentElement){
        var text=norm(el.innerText||el.textContent);
        if(text.length>16000)break;
        consider(text);
      }
    }
  }catch(e){}
  try{
    var frames=doc.querySelectorAll('iframe,frame');
    for(var i=0;i<frames.length&&i<12;i++){
      try{scan(frames[i].contentDocument,depth+1);}catch(e){}
    }
  }catch(e){}
}
scan(document,0);
return JSON.stringify({ok:!!best,text:best});
})()";

        internal async Task<bool> TryRecoverVisibleOrderPanelAsync(string sellerHint, string buyerHint, string source)
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
            catch (Exception ex)
            {
                Log.Info("右侧订单面板兜底读取前会话确认失败: seller=" + runtimeSeller
                    + ", buyer=" + buyerHint + ", error=" + ex.Message);
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
                    "读取当前买家右侧近3个月订单面板").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info("右侧订单面板兜底DOM读取失败: seller=" + runtimeSeller
                    + ", buyer=" + verifiedBuyer + ", error=" + ex.Message);
                return false;
            }

            var panelText = ExtractVisibleOrderPanelText(raw);
            if (string.IsNullOrWhiteSpace(panelText)) return false;

            // DOM读取期间人工可能切换会话。发布订单前必须再次确认仍然是同一买家。
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
                Log.Info("右侧订单面板兜底已取消：DOM读取期间当前买家发生变化。seller="
                    + runtimeSeller + ", expectedBuyer=" + verifiedBuyer
                    + ", currentBuyer=" + (after == null ? string.Empty : after.Nick));
                return false;
            }

            var candidates = ParseVisibleOrderPanelCandidates(panelText)
                .OrderByDescending(x => x.PaidAt ?? x.CreatedAt ?? DateTime.MinValue)
                .Take(3)
                .ToList();
            if (candidates.Count == 0) return false;

            var now = DateTime.Now;
            var freshFloor = _messageSafetyStartedAt.AddSeconds(-8);
            var sawFreshSupportedOrder = false;
            var sawFreshUnsupportedOrder = false;
            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.OrderId)) continue;
                var eventTime = candidate.PaidAt ?? candidate.CreatedAt;
                if (!eventTime.HasValue)
                {
                    Log.Info("右侧订单面板兜底暂缺少可验证下单/付款时间，将等待面板继续加载: seller="
                        + runtimeSeller + ", buyer=" + verifiedBuyer + ", orderId=" + candidate.OrderId);
                    continue;
                }
                if (eventTime.Value > now.AddMinutes(2)) continue;
                if (eventTime.Value < freshFloor)
                {
                    Log.Info("右侧订单面板兜底跳过历史订单: seller=" + runtimeSeller
                        + ", buyer=" + verifiedBuyer + ", orderId=" + candidate.OrderId
                        + ", eventTime=" + eventTime.Value.ToString("yyyy-MM-dd HH:mm:ss")
                        + ", botStartedAt=" + _messageSafetyStartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                    continue;
                }

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
                    Source = "千牛右侧订单面板兜底",
                    DetectedAt = now,
                    EventTime = eventTime.Value,
                    EventType = eventType,
                    EventText = text.ToString()
                };

                var publish = OrderEventHub.Publish(snapshot);
                if (publish != null && publish.Detected)
                {
                    sawFreshSupportedOrder = true;
                    if (publish.Accepted)
                    {
                        Log.Info("右侧订单面板兜底识别并发布: seller=" + runtimeSeller
                            + ", buyer=" + verifiedBuyer + ", orderId=" + candidate.OrderId
                            + ", status=" + candidate.TradeStatus + ", event=" + eventType
                            + ", trigger=" + (source ?? string.Empty));
                    }
                    else
                    {
                        Log.Info("右侧订单面板兜底订单已由其他通道处理/去重: seller=" + runtimeSeller
                            + ", buyer=" + verifiedBuyer + ", orderId=" + candidate.OrderId
                            + ", event=" + eventType);
                    }
                }
            }

            // 找到并发布（或确认已去重）的新订单即可停止本轮重试；若当前只看到历史订单，
            // 继续短暂重试，给新版千牛右侧面板时间把刚产生的新订单渲染出来。
            return sawFreshSupportedOrder || sawFreshUnsupportedOrder;
        }

        private static string ExtractVisibleOrderPanelText(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            for (var i = 0; i < 3; i++)
            {
                JToken token;
                try { token = JToken.Parse(value); }
                catch { break; }

                if (token.Type == JTokenType.String)
                {
                    value = token.ToString().Trim();
                    continue;
                }

                var obj = token as JObject;
                if (obj == null) break;
                var direct = obj["text"];
                if (direct != null && direct.Type != JTokenType.Null)
                {
                    value = direct.ToString().Trim();
                    break;
                }
                var nested = obj.SelectToken("result.value") ?? obj.SelectToken("value");
                if (nested == null) break;
                value = nested.ToString().Trim();
            }

            return value.Contains("近3个月订单") || value.Contains("近三个月订单")
                ? Regex.Replace(value.Replace('\u00a0', ' '), @"\s+", " ").Trim()
                : string.Empty;
        }

        private static List<VisibleOrderPanelCandidate> ParseVisibleOrderPanelCandidates(string panelText)
        {
            var result = new List<VisibleOrderPanelCandidate>();
            panelText = Regex.Replace((panelText ?? string.Empty).Replace('\u00a0', ' '), @"\s+", " ").Trim();
            if (panelText.Length == 0) return result;
            var matches = VisiblePanelOrderIdRegex.Matches(panelText).Cast<Match>().ToList();
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (!match.Success) continue;
                var start = Math.Max(0, match.Index - 140);
                var nextStart = i + 1 < matches.Count ? matches[i + 1].Index : panelText.Length;
                var end = Math.Min(panelText.Length, Math.Max(match.Index + match.Length + 160, Math.Min(nextStart + 80, match.Index + 900)));
                if (end <= start) continue;
                var segment = panelText.Substring(start, end - start);
                var createdAt = ParseVisiblePanelTime(VisiblePanelCreatedAtRegex.Match(segment));
                var paidAt = ParseVisiblePanelTime(VisiblePanelPaidAtRegex.Match(segment));
                var status = ResolveVisiblePanelStatus(segment);

                // 右侧面板兜底比聊天卡解析更严格：必须是 16~24 位真实订单号，且至少
                // 有订单状态或下单/付款时间之一；仅凭任意长数字绝不发布订单事件。
                if (!createdAt.HasValue && !paidAt.HasValue && string.IsNullOrWhiteSpace(status)) continue;
                result.Add(new VisibleOrderPanelCandidate
                {
                    OrderId = match.Groups[1].Value,
                    TradeStatus = status,
                    CreatedAt = createdAt,
                    PaidAt = paidAt,
                    Segment = segment
                });
            }
            return result
                .GroupBy(x => x.OrderId, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(x => x.PaidAt ?? x.CreatedAt ?? DateTime.MinValue).First())
                .ToList();
        }

        private static DateTime? ParseVisiblePanelTime(Match match)
        {
            if (match == null || !match.Success) return null;
            DateTime value;
            var text = match.Groups["time"].Value.Trim();
            if (DateTime.TryParseExact(
                text,
                new[] { "yyyy-M-d H:mm:ss", "yyyy-M-d H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out value)) return value;
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out value)) return value;
            return null;
        }

        private static string ResolveVisiblePanelStatus(string segment)
        {
            segment = segment ?? string.Empty;
            foreach (var status in VisiblePanelStatuses)
            {
                if (segment.IndexOf(status, StringComparison.Ordinal) >= 0) return status;
            }
            return string.Empty;
        }

        internal async Task ProcessDirectOrderMessageAsync(
            QNChatMessage message,
            string sellerHint,
            string buyerHint,
            string source,
            string exactOrderIdHint = null)
        {
            if (message == null) return;
            var seller = DirectOrderIdentityResolver.ResolveSeller(this, message, sellerHint);
            var buyer = DirectOrderIdentityResolver.ResolveBuyer(this, message, buyerHint);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer))
            {
                Log.Info("检测到疑似订单系统卡片但无法解析客服/买家身份，暂不自动处理: source="
                    + source + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            OrderPlacedReplyPlan plan;
            if (!OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                GetMessageText(message),
                seller,
                buyer,
                _messageSafetyStartedAt,
                out plan,
                exactOrderIdHint,
                ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey)) return;

            if (plan == null)
            {
                Log.Info("订单系统事件已由其他通道处理或已去重: source=" + source
                    + ", seller=" + seller + ", buyer=" + buyer);
                return;
            }

            Log.Info("优先识别到直接下单系统事件: source=" + source
                + ", seller=" + seller + ", buyer=" + buyer + ", orderId=" + plan.OrderId);
            // 展开诊断桥接能够解析嵌套 messageCenterNotify，但不得绕过统一字段补全。
            // 计划已包含准确 seller、buyer 和 orderId，交给 V2 查询交易详情后再发送。
            if (OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)) return;

            await ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
