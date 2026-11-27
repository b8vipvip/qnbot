using Bot.ChatRecord;
using Bot.ChromeNs;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using DbEntity;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
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
using System.Windows;
using System.Windows.Controls;

namespace Bot.Options
{
    /// <summary>
    /// 下单后固定回复延迟属于店铺级设置，允许 0-300 秒。
    /// 延迟结束后必须重新校验Bot开关、原店铺ShopKey和CDP会话，避免切店后误发到其他买家。
    /// 旧配置缺少该字段时由 AutoReplyRuleConfig 构造函数保持 0 秒默认值。
    /// </summary>
    public static class OrderPlacedReplyDelaySettings
    {
        public const int DefaultDelaySeconds = 0;
        public const int MaxDelaySeconds = 300;

        public static void Initialize()
        {
            // Capture the full raw order payload before the legacy bridge drops SKU/quantity/payment fields.
            Bot.ChromeNs.OrderRichPayloadBridge.Initialize();
        }

        public static int Clamp(int seconds)
        {
            return Math.Max(0, Math.Min(MaxDelaySeconds, seconds));
        }

        public static int GetSeconds()
        {
            var config = BotFeatureStore.GetAutoReplyRules() ?? AutoReplyRuleConfig.Default();
            return Clamp(config.OrderPlacedReplyDelaySeconds);
        }

        public static void SaveSeconds(int seconds)
        {
            var config = BotFeatureStore.GetAutoReplyRules() ?? AutoReplyRuleConfig.Default();
            config.OrderPlacedReplyDelaySeconds = Clamp(seconds);
            BotFeatureStore.SaveAutoReplyRules(config);
        }
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// 在旧订单桥接丢弃字段之前读取千牛原始消息负载，直接构建完整 OrderSnapshot。
    /// 目标是保证固定模板中的 {sku}、{数量}、{金额}、{实付} 使用真实订单字段，
    /// 而不是只拿到订单号和付款状态后静默替换成空字符串。
    ///
    /// 本桥接不持久化原始 JSON，只保留现有 OrderSnapshot 中的结构化字段和哈希。
    /// </summary>
    internal static class OrderRichPayloadBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private static Timer _timer;
        private static int _initialized;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|已成功下单|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|等待买家付款|已付款|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|TRADE_CREATED|TRADE_PAID|TRADE_FINISHED",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex OrderIdTextRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号)\s*[:：#]?\s*(\d{8,})",
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

        private static readonly string[] ItemIdKeys =
        {
            "itemid", "numiid", "auctionid", "productid", "goodsid"
        };

        private static readonly string[] ItemTitleKeys =
        {
            "itemtitle", "auctiontitle", "producttitle", "goodstitle", "itemname",
            "productname", "subject", "auctionname", "goodsname", "title"
        };

        private static readonly string[] SkuIdKeys =
        {
            "skuid", "skuidstr", "skuidentifier", "sku_id", "subitemid"
        };

        private static readonly string[] SkuTextKeys =
        {
            "skutext", "skuname", "skutitle", "skuinfo", "skudesc", "skudescription",
            "skupropertiesname", "skupropertyname", "propertiesname", "propertyname",
            "propertyvalue", "spec", "specification", "specinfo", "specname",
            "salesproperties", "auctionprops", "outername"
        };

        private static readonly string[] QuantityKeys =
        {
            "quantity", "num", "buynum", "buyamount", "buyquantity", "itemcount",
            "itemnum", "goodsnum", "productcount", "orderquantity", "count", "amountcount"
        };

        private static readonly string[] TotalAmountKeys =
        {
            "totalamount", "totalfee", "orderamount", "ordertotal", "totalprice",
            "totalpayment", "shouldpay", "payableamount", "amount", "price"
        };

        private static readonly string[] PaidAmountKeys =
        {
            "paidamount", "payment", "payamount", "actualfee", "actualpay", "actualpayment",
            "paidfee", "realpay", "realpayment", "actualamount", "receivedamount",
            "receiveamount", "buyerpaid", "buyerpayment"
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

        private static readonly string[] ProductUrlKeys =
        {
            "producturl", "itemurl", "auctionurl", "detailurl", "url"
        };

        private static readonly string[] ImageUrlKeys =
        {
            "imageurl", "picurl", "itempic", "pic", "image"
        };

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            _timer = new Timer(_ => Attach(), null, 0, 100);
            Log.Info("订单完整字段桥接已启动：优先保留 SKU、数量、金额和实付字段。");
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    lock (AttachSync)
                    {
                        object marker;
                        if (Attached.TryGetValue(qn, out marker)) continue;
                        // 此桥接在 App 构造阶段早于旧订单桥接初始化，并以更短轮询间隔绑定。
                        // 同一原始事件会先由这里发布完整快照，旧桥接随后会被 OrderEventHub 去重。
                        qn.EvMessageNotity += OnMessageNotify;
                        qn.EvRecieveNewMessage += OnReceiveNewMessage;
                        Attached.Add(qn, new object());
                    }
                    Log.Info("订单完整字段桥接已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定订单完整字段桥接失败：" + ex.Message, 10);
            }
        }

        private static async void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length < 8) return;
            try
            {
                await ProcessRawAsync(qn, raw, "messageCenterNotify原始订单负载");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("处理 messageCenterNotify 完整订单字段失败：" + ex.Message, 10);
            }
        }

        private static async void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length < 8) return;
            try
            {
                await ProcessRawAsync(qn, raw, "receiveNewMsg原始订单负载");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("处理 receiveNewMsg 完整订单字段失败：" + ex.Message, 10);
            }
        }

        private static async Task ProcessRawAsync(QN qn, string raw, string source)
        {
            CleanupReservations();

            OrderSnapshot snapshot;
            string reason;
            if (!TryParseSnapshot(qn, raw, source, out snapshot, out reason))
            {
                return;
            }

            // 只有原始负载确实带来了模板所需的结构化字段时才抢先处理；
            // 否则继续交给旧的订单号/状态兼容桥接，避免改变其安全语义。
            if (!HasTemplateFieldEvidence(snapshot))
            {
                Log.Info("订单完整字段桥接未发现 SKU/数量/金额证据，交由兼容桥接处理: orderId="
                    + snapshot.OrderId + ", source=" + source);
                return;
            }

            var reserveKey = (snapshot.ShopKey ?? string.Empty).Trim()
                + "#" + Normalize(snapshot.Seller) + "#" + snapshot.OrderId + "#" + snapshot.EventType;
            DateTime until;
            if (Reservations.TryGetValue(reserveKey, out until) && until >= DateTime.Now) return;
            Reservations[reserveKey] = DateTime.Now.AddMinutes(2);

            Log.Info("订单原始字段解析成功: seller=" + snapshot.Seller
                + ", buyer=" + snapshot.Buyer
                + ", orderId=" + snapshot.OrderId
                + ", sku=" + Short(snapshot.SkuText, 100)
                + ", quantity=" + (snapshot.Quantity <= 0 ? "<missing>" : snapshot.Quantity.ToString())
                + ", total=" + FormatMoney(snapshot.TotalAmount)
                + ", paid=" + FormatMoney(snapshot.PaidAmount)
                + ", source=" + source);

            await qn.ProcessRichOrderSnapshotAsync(snapshot, reason);
        }

        private static bool TryParseSnapshot(
            QN qn,
            string raw,
            string source,
            out OrderSnapshot snapshot,
            out string reason)
        {
            snapshot = null;
            reason = string.Empty;

            var root = ParseExpanded(raw);
            var flat = Flatten(root);
            var combined = BuildCombinedText(raw, flat);
            if (!EventCueRegex.IsMatch(combined)) return false;

            var orderId = ResolveOrderId(flat, combined);
            if (string.IsNullOrWhiteSpace(orderId)) return false;

            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = ResolveBuyer(flat, seller, orderId);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer))
            {
                Log.Info("订单完整字段负载缺少可验证客服/买家身份，未猜测当前会话: orderId="
                    + orderId + ", source=" + source);
                return false;
            }

            var itemTitle = FirstNonEmpty(
                FindValue(flat, ItemTitleKeys),
                ExtractLabeledText(combined, "商品名称", "商品", "宝贝名称", "宝贝"));
            var skuText = FirstNonEmpty(
                FindValue(flat, SkuTextKeys),
                ExtractLabeledText(combined, "SKU", "规格名称", "规格", "销售属性", "套餐", "属性"));

            var quantity = ParsePositiveInt(FindValue(flat, QuantityKeys));
            if (quantity <= 0) quantity = ExtractQuantity(combined);

            var paidAmount = ParseMoney(FindValue(flat, PaidAmountKeys));
            if (!paidAmount.HasValue) paidAmount = ExtractPaidAmount(combined);

            var totalAmount = ParseMoney(FindValue(flat, TotalAmountKeys));
            if (!totalAmount.HasValue) totalAmount = ExtractTotalAmount(combined);

            var tradeStatus = FirstNonEmpty(FindValue(flat, StatusKeys), ExtractStatus(combined));
            var paid = ResolvePaidState(combined, tradeStatus);
            var eventType = ResolveEventType(combined, tradeStatus, paid);

            // 某些付款通知只提供一个金额字段（通常叫 amount/totalFee），但语义已明确为已付款。
            // 这种情况下它就是当前可获得的实付证据；保留来源日志，不再让 {实付} 静默为空。
            if (eventType == OrderEventType.Paid && !paidAmount.HasValue && totalAmount.HasValue)
            {
                paidAmount = totalAmount;
                reason = "付款事件仅有一个金额字段，实付采用该金额";
            }
            if (!totalAmount.HasValue && paidAmount.HasValue) totalAmount = paidAmount;

            var eventTime = ParseDate(FindValue(flat, TimeKeys)) ?? DateTime.Now;
            snapshot = new OrderSnapshot
            {
                ShopKey = ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey,
                Seller = seller,
                Buyer = buyer,
                OrderId = orderId,
                ItemId = DigitsOnly(FindValue(flat, ItemIdKeys), 6, 32),
                ItemTitle = Clean(itemTitle, 300),
                SkuId = Clean(FindValue(flat, SkuIdKeys), 100),
                SkuText = Clean(skuText, 240),
                Quantity = quantity,
                TotalAmount = totalAmount,
                PaidAmount = paidAmount,
                TradeStatus = Clean(tradeStatus, 120),
                IsPaid = paid,
                CreatedAt = eventType == OrderEventType.Created ? (DateTime?)eventTime : null,
                PaidAt = eventType == OrderEventType.Paid ? (DateTime?)eventTime : null,
                ProductUrl = FindUrl(flat, ProductUrlKeys, "item.taobao.com"),
                ImageUrl = FindUrl(flat, ImageUrlKeys, null),
                Source = source,
                RawCardHash = Hash(raw),
                DetectedAt = DateTime.Now,
                EventTime = eventTime,
                EventType = eventType
            };
            snapshot.EventText = BuildSafeEventText(snapshot);
            if (string.IsNullOrWhiteSpace(reason)) reason = "原始负载结构化字段优先解析";
            return true;
        }

        private static bool HasTemplateFieldEvidence(OrderSnapshot snapshot)
        {
            return snapshot != null
                && (!string.IsNullOrWhiteSpace(snapshot.SkuText)
                    || snapshot.Quantity > 0
                    || snapshot.PaidAmount.HasValue
                    || snapshot.TotalAmount.HasValue);
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

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || depth > 18 || output.Count >= 1800) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(property.Value,
                        path.Length == 0 ? property.Name : path + "." + property.Name,
                        output,
                        depth + 1);
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
            output.Add(new FlatValue
            {
                Path = path,
                Key = key,
                Value = value.Length > 4000 ? value.Substring(0, 4000) : value
            });

            // 千牛常把真正订单对象再次 JSON 编码进 response/data/content 字符串。
            if (token.Type == JTokenType.String && depth < 16 && LooksLikeJson(value))
            {
                try { Walk(JToken.Parse(value), path + ".json", output, depth + 1); }
                catch { }
            }
        }

        private static bool LooksLikeJson(string value)
        {
            value = (value ?? string.Empty).Trim();
            return (value.StartsWith("{") && value.EndsWith("}"))
                || (value.StartsWith("[") && value.EndsWith("]"));
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

        private static string ResolveOrderId(IList<FlatValue> flat, string combined)
        {
            var direct = FindValue(flat, OrderIdKeys);
            var digits = DigitsOnly(direct, 8, 40);
            if (!string.IsNullOrWhiteSpace(digits)) return digits;
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
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2 || value.Length > 180) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (Regex.Replace(value, @"\D", string.Empty) == orderId) return false;
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
                    && set.Contains(NormalizeKey(item.Key)))
                {
                    return item.Value.Trim();
                }
            }
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var path = NormalizeKey(item.Path);
                if (set.Any(alias => path.EndsWith(alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return item.Value.Trim();
                }
            }
            return string.Empty;
        }

        private static string FindUrl(IList<FlatValue> flat, IEnumerable<string> aliases, string preferredHost)
        {
            var candidates = new List<string>();
            var set = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)
                    || !set.Contains(NormalizeKey(item.Key))) continue;
                Uri uri;
                if (Uri.TryCreate(item.Value.Trim(), UriKind.Absolute, out uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    candidates.Add(uri.ToString());
                }
            }
            if (!string.IsNullOrWhiteSpace(preferredHost))
            {
                var preferred = candidates.FirstOrDefault(x =>
                    x.IndexOf(preferredHost, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
            }
            return candidates.FirstOrDefault() ?? string.Empty;
        }

        private static string ExtractLabeledText(string text, params string[] labels)
        {
            foreach (var label in labels ?? new string[0])
            {
                var pattern = Regex.Escape(label)
                    + @"\s*[:：]\s*(.+?)(?=\s+(?:SKU|规格|数量|件数|实付|实收|金额|合计|订单状态|状态|订单号)\s*[:：]|$)";
                var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var value = Clean(match.Groups[1].Value, 240);
                if (value.Length > 0) return value;
            }
            return string.Empty;
        }

        private static int ExtractQuantity(string text)
        {
            var patterns = new[]
            {
                @"(?:数量|件数|购买数量|商品数量)\s*[:：]?\s*(\d{1,5})",
                @"(?:×|x|X|\*)\s*(\d{1,5})(?:\s*件)?\b",
                @"共\s*(\d{1,5})\s*件"
            };
            foreach (var pattern in patterns)
            {
                var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.IgnoreCase);
                int result;
                if (match.Success && int.TryParse(match.Groups[1].Value, out result)
                    && result > 0 && result <= 10000)
                {
                    return result;
                }
            }
            return 0;
        }

        private static decimal? ExtractPaidAmount(string text)
        {
            return ExtractMoneyByLabels(text,
                "买家实付", "实付金额", "实际付款", "付款金额", "支付金额", "成交金额", "实付", "实收");
        }

        private static decimal? ExtractTotalAmount(string text)
        {
            return ExtractMoneyByLabels(text,
                "订单总金额", "订单总额", "订单总价", "应付金额", "应付", "合计", "总价", "金额");
        }

        private static decimal? ExtractMoneyByLabels(string text, params string[] labels)
        {
            foreach (var label in labels ?? new string[0])
            {
                var match = Regex.Match(
                    text ?? string.Empty,
                    Regex.Escape(label) + @"\s*[:：]?\s*[￥¥]?\s*(-?\d+(?:\.\d{1,4})?)",
                    RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var value = ParseMoney(match.Groups[1].Value);
                if (value.HasValue) return value;
            }
            return null;
        }

        private static int ParsePositiveInt(string value)
        {
            int result;
            var match = Regex.Match(value ?? string.Empty, @"\d+");
            return match.Success && int.TryParse(match.Value, out result)
                && result > 0 && result <= 10000
                ? result
                : 0;
        }

        private static decimal? ParseMoney(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            decimal amount;
            var match = Regex.Match(value.Replace(",", string.Empty), @"-?\d+(?:\.\d{1,4})?");
            if (!match.Success
                || !decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                || amount < 0
                || amount > 100000000m)
            {
                return null;
            }
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

        private static string ExtractStatus(string text)
        {
            var known = new[]
            {
                "已付款", "待卖家发货", "等待买家付款", "未付款", "交易成功",
                "订单关闭", "退款中", "买家已下单", "订单创建成功", "新下单"
            };
            return known.FirstOrDefault(x =>
                (text ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) ?? string.Empty;
        }

        private static bool? ResolvePaidState(string text, string status)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "未付款|等待买家付款|待付款|付款关闭|交易关闭", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(value, "已付款|付款成功|支付成功|交易成功|已支付|待卖家发货|WAIT_SELLER_SEND_GOODS|TRADE_PAID", RegexOptions.IgnoreCase)) return true;
            return null;
        }

        private static OrderEventType ResolveEventType(string text, string status, bool? paid)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "申请退款|退款中|退货退款|仅退款", RegexOptions.IgnoreCase)) return OrderEventType.RefundRequested;
            if (Regex.IsMatch(value, "订单关闭|交易关闭|已关闭|已取消", RegexOptions.IgnoreCase)) return OrderEventType.Closed;
            return paid == true ? OrderEventType.Paid : OrderEventType.Created;
        }

        private static string BuildSafeEventText(OrderSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append(snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ");
            sb.Append("订单号：").Append(snapshot.OrderId);
            if (!string.IsNullOrWhiteSpace(snapshot.ItemTitle)) sb.Append(" 商品：").Append(Short(snapshot.ItemTitle, 220));
            if (!string.IsNullOrWhiteSpace(snapshot.SkuText)) sb.Append(" 规格：").Append(Short(snapshot.SkuText, 180));
            if (snapshot.Quantity > 0) sb.Append(" 数量：").Append(snapshot.Quantity);
            if (snapshot.PaidAmount.HasValue) sb.Append(" 实付：").Append(snapshot.PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            else if (snapshot.TotalAmount.HasValue) sb.Append(" 金额：").Append(snapshot.TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(" 订单状态：").Append(string.IsNullOrWhiteSpace(snapshot.TradeStatus)
                ? (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                : snapshot.TradeStatus);
            return sb.ToString();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string Clean(string value, int max)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string NormalizeKey(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
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

        private static string FormatMoney(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "<missing>";
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
        /// 使用已经从原始订单负载完整解析的快照，复用原有订单队列、去重、固定模板、
        /// 人工保护和可靠发送链路。先发布完整快照后，旧桥接的稀疏 synthetic message
        /// 会被 OrderEventHub 判定为同一订单事件，不会再用空字段抢先发送。
        /// </summary>
        internal async Task ProcessRichOrderSnapshotAsync(OrderSnapshot snapshot, string parseReason)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(this); }
            catch (Exception ex)
            {
                Log.Info("完整订单字段事件无法确认来源ShopKey，已阻止自动回复: " + ex.Message);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || (!string.IsNullOrWhiteSpace(snapshot.ShopKey)
                    && !string.Equals(snapshot.ShopKey, shop.ShopKey, StringComparison.Ordinal))) return;
            snapshot.ShopKey = shop.ShopKey;
            bool botEnabledForShop;
            using (ShopSettingsScope.Enter(shop, false))
                botEnabledForShop = Params.Robot.CanUseRobotReal;
            if (!botEnabledForShop) return;

            if (snapshot.EventTime < _messageSafetyStartedAt.AddSeconds(-8))
            {
                Log.Info("完整订单字段事件已跳过历史通知: orderId=" + snapshot.OrderId
                    + ", eventTime=" + snapshot.EventTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return;
            }

            OrderGuidanceDeliveryGuard.ObserveOrder(snapshot);
            var published = OrderEventHub.Publish(snapshot);
            if (!published.Accepted)
            {
                Log.Info("完整订单字段事件已由其他通道处理或去重: orderId=" + snapshot.OrderId
                    + ", reason=" + published.Reason);
                return;
            }

            if (snapshot.EventType == OrderEventType.Created || snapshot.EventType == OrderEventType.Paid)
            {
                EnqueueNewOrderAttention(snapshot);
            }

            AutoReplyRuleConfig cfg;
            // Rich order payloads are delivered by a background bridge. Resolve the
            // per-shop rule snapshot explicitly; never fall back to the global params row.
            using (ShopSettingsScope.Enter(shop, false))
                cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableOrderPlacedReply) return;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return;

            var plan = new OrderPlacedReplyPlan
            {
                ShopKey = snapshot.ShopKey,
                Seller = snapshot.Seller,
                Buyer = snapshot.Buyer,
                OrderId = snapshot.OrderId,
                EventText = snapshot.EventText,
                EventTime = snapshot.EventTime,
                ReservationKey = (snapshot.ShopKey ?? string.Empty).Trim()
                    + "#" + Regex.Replace((snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                    + "#" + Regex.Replace((snapshot.Buyer ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty)
                    + "#" + snapshot.OrderId,
                Config = cfg,
                Snapshot = snapshot,
                IsBuyerFollowUp = false,
                TriggerText = string.Empty,
                TriggerTime = DateTime.MinValue
            };

            Log.Info("下单固定模板将使用完整订单字段: orderId=" + snapshot.OrderId
                + ", sku=" + (string.IsNullOrWhiteSpace(snapshot.SkuText) ? "<missing>" : snapshot.SkuText)
                + ", quantity=" + (snapshot.Quantity <= 0 ? "<missing>" : snapshot.Quantity.ToString())
                + ", total=" + (snapshot.TotalAmount.HasValue ? snapshot.TotalAmount.Value.ToString("0.00") : "<missing>")
                + ", paid=" + (snapshot.PaidAmount.HasValue ? snapshot.PaidAmount.Value.ToString("0.00") : "<missing>")
                + ", parseReason=" + (parseReason ?? string.Empty));

            await ProcessOrderPlacedReplyAsync(plan);
        }
    }
}
