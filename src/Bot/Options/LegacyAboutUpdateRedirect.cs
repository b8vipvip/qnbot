using Bot.ChatRecord;
using BotLib;
using DbEntity;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Bot.Options
{
    /// <summary>
    /// 右侧面板旧“关于”菜单仍弹出静态 AI客服 v2 文本。
    /// 在不改动旧 XAML 事件绑定的前提下，用 WPF 类处理器优先拦截该菜单，统一打开“关于与版本更新”。
    /// </summary>
    internal static class LegacyAboutUpdateRedirect
    {
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(MenuItem),
                MenuItem.ClickEvent,
                new RoutedEventHandler(OnMenuItemClick),
                true);
            Log.Info("旧关于菜单已重定向到关于与版本更新中心。");
        }

        private static void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            if (item == null || !string.Equals(HeaderText(item), "关于", StringComparison.Ordinal)) return;

            e.Handled = true;
            var dispatcher = item.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(BotAboutUpdateLauncher.Show), DispatcherPriority.Background);
        }

        private static string HeaderText(MenuItem item)
        {
            var textBlock = item.Header as TextBlock;
            if (textBlock != null) return (textBlock.Text ?? string.Empty).Trim();
            return Convert.ToString(item.Header ?? string.Empty).Trim();
        }
    }
}

namespace Bot
{
    /// <summary>
    /// 字段初始化早于 App 构造函数执行，用来保证 SKU 恢复桥接先于旧订单桥接开始轮询和订阅。
    /// 这样完整 SKU 快照会先进入原有订单发送管线，后续稀疏订单事件只会被去重合并。
    /// </summary>
    public partial class App
    {
        private readonly object _orderSkuPayloadRecoveryBootstrap =
            ChromeNs.OrderSkuPayloadRecoveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// 千牛部分订单负载不会提供单一 skuText，而是把规格拆成：
    /// pName/vName、propertyName/propertyValue、name/value 等属性对。
    /// 右侧订单面板能组合显示 SKU，但旧解析器只读取单一字段，因此 {sku} 为空。
    ///
    /// 本桥接在旧订单桥接之前订阅原始事件，重建“属性名:属性值”规格文本，并复用既有
    /// OrderEventHub、去重、人工保护、固定模板和可靠发送流程。原始 JSON 不落盘、不写日志。
    /// </summary>
    internal static class OrderSkuPayloadRecoveryBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly ConcurrentDictionary<QN, byte> Attached =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

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
            "productname", "subject", "auctionname", "goodsname"
        };

        private static readonly string[] SkuIdKeys =
        {
            "skuid", "skuidstr", "skuidentifier", "sku_id", "subitemid"
        };

        // 这里只放“本身就应当是完整规格文字”的字段。
        // propertyName/propertyValue 之类的拆分字段由 ResolveStructuredSkuPairs 组合。
        private static readonly string[] DirectSkuTextKeys =
        {
            "skutext", "skuname", "skutitle", "skudesc", "skudescription",
            "skupropertiesname", "propertiesname", "spec", "specification",
            "specinfo", "salesproperties", "auctionprops", "outername",
            "skucontent", "skudisplay", "skudisplaytext", "skuproperties", "sku"
        };

        private static readonly string[] SkuNameKeys =
        {
            "pname", "propname", "propertyname", "specname", "attributename",
            "optiongroupname", "dimensionname", "label", "key"
        };

        private static readonly string[] SkuValueKeys =
        {
            "vname", "propvalue", "propertyvalue", "specvalue", "attributevalue",
            "optionname", "selectedvalue", "displayvalue", "value", "text"
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

        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            Initialize();
            return new object();
        }

        private static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            // App 字段初始化阶段已启动；25ms 轮询用于先于旧 100ms 订单桥接绑定。
            _timer = new Timer(_ => Attach(), null, 0, 25);
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                    qn.EvMessageNotity += OnMessageNotify;
                    qn.EvRecieveNewMessage += OnReceiveNewMessage;
                    Log.Info("订单SKU属性恢复桥接已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定订单SKU属性恢复桥接失败：" + ex.Message, 10);
            }
        }

        private static async void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty);
            if (qn == null || raw.Length < 8) return;
            try
            {
                await ProcessRawAsync(qn, raw, "messageCenterNotify-SKU属性恢复");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("处理消息中心SKU属性恢复失败：" + ex.Message, 10);
            }
        }

        private static async void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length < 8) return;
            try
            {
                await ProcessRawAsync(qn, raw, "receiveNewMsg-SKU属性恢复");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("处理聊天订单SKU属性恢复失败：" + ex.Message, 10);
            }
        }

        private static async Task ProcessRawAsync(QN qn, string raw, string source)
        {
            CleanupReservations();

            OrderSnapshot snapshot;
            string skuStrategy;
            if (!TryParseCompleteSnapshot(qn, raw, source, out snapshot, out skuStrategy)) return;

            var reserveKey = Normalize(snapshot.Seller) + "#" + snapshot.OrderId + "#" + snapshot.EventType;
            DateTime until;
            if (Reservations.TryGetValue(reserveKey, out until) && until >= DateTime.Now) return;
            Reservations[reserveKey] = DateTime.Now.AddMinutes(2);

            Log.Info("订单SKU结构恢复成功: seller=" + snapshot.Seller
                + ", buyer=" + snapshot.Buyer
                + ", orderId=" + snapshot.OrderId
                + ", sku=" + Short(snapshot.SkuText, 140)
                + ", quantity=" + snapshot.Quantity
                + ", paid=" + FormatMoney(snapshot.PaidAmount)
                + ", strategy=" + skuStrategy
                + ", source=" + source);

            await qn.ProcessRichOrderSnapshotAsync(
                snapshot,
                "SKU结构恢复：" + skuStrategy);
        }

        private static bool TryParseCompleteSnapshot(
            QN qn,
            string raw,
            string source,
            out OrderSnapshot snapshot,
            out string skuStrategy)
        {
            snapshot = null;
            skuStrategy = string.Empty;

            var root = ParseExpanded(raw);
            var flat = Flatten(root);
            var combined = BuildCombinedText(raw, flat);
            if (!EventCueRegex.IsMatch(combined)) return false;

            var orderId = ResolveOrderId(flat, combined);
            if (string.IsNullOrWhiteSpace(orderId)) return false;

            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = ResolveBuyer(flat, seller, orderId);
            if (seller.Length == 0 || buyer.Length == 0) return false;

            var skuText = ResolveSkuText(flat, combined, out skuStrategy);
            if (string.IsNullOrWhiteSpace(skuText)) return false;

            var quantity = ParsePositiveInt(FindValue(flat, QuantityKeys));
            if (quantity <= 0) quantity = ExtractQuantity(combined);

            var paidAmount = ParseMoney(FindValue(flat, PaidAmountKeys));
            if (!paidAmount.HasValue) paidAmount = ExtractPaidAmount(combined);

            var totalAmount = ParseMoney(FindValue(flat, TotalAmountKeys));
            if (!totalAmount.HasValue) totalAmount = ExtractTotalAmount(combined);

            var tradeStatus = FirstNonEmpty(FindValue(flat, StatusKeys), ExtractStatus(combined));
            var paid = ResolvePaidState(combined, tradeStatus);
            var eventType = ResolveEventType(combined, tradeStatus, paid);

            if (eventType == OrderEventType.Paid && !paidAmount.HasValue && totalAmount.HasValue)
            {
                paidAmount = totalAmount;
            }
            if (!totalAmount.HasValue && paidAmount.HasValue) totalAmount = paidAmount;

            // 只有规格、数量和金额证据都齐全时才抢先发送，避免修复 SKU 的同时
            // 反过来覆盖旧桥接本可取得的数量或实付字段。
            if (quantity <= 0 || (!paidAmount.HasValue && !totalAmount.HasValue)) return false;

            var itemTitle = FirstNonEmpty(
                FindValue(flat, ItemTitleKeys),
                ExtractLabeledText(combined, "商品名称", "商品", "宝贝名称", "宝贝"));
            var eventTime = ParseDate(FindValue(flat, TimeKeys)) ?? DateTime.Now;

            snapshot = new OrderSnapshot
            {
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
                Source = source,
                RawCardHash = Hash(raw),
                DetectedAt = DateTime.Now,
                EventTime = eventTime,
                EventType = eventType
            };
            snapshot.EventText = BuildSafeEventText(snapshot);
            return true;
        }

        internal static string ResolveSkuTextFromPayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            try
            {
                var root = ParseExpanded(raw);
                var flat = Flatten(root);
                var combined = BuildCombinedText(raw, flat);
                string strategy;
                return Clean(ResolveSkuText(flat, combined, out strategy), 240);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveSkuText(
            IList<FlatValue> flat,
            string combined,
            out string strategy)
        {
            strategy = string.Empty;

            var direct = ResolveDirectSkuText(flat);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                strategy = "完整SKU字段";
                return direct;
            }

            var pairs = ResolveStructuredSkuPairs(flat);
            if (!string.IsNullOrWhiteSpace(pairs))
            {
                strategy = "属性名/属性值组合";
                return pairs;
            }

            var labeled = NormalizeSkuCandidate(
                ExtractLabeledText(combined, "SKU", "规格名称", "规格", "销售属性", "套餐", "属性"));
            if (!string.IsNullOrWhiteSpace(labeled))
            {
                strategy = "可见标签文本";
                return labeled;
            }

            return string.Empty;
        }

        private static string ResolveDirectSkuText(IList<FlatValue> flat)
        {
            var aliases = new HashSet<string>(
                DirectSkuTextKeys.Select(NormalizeKey),
                StringComparer.OrdinalIgnoreCase);
            string best = string.Empty;
            var bestScore = 0;

            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var key = NormalizeKey(item.Key);
                if (!aliases.Contains(key)) continue;

                var candidate = NormalizeSkuCandidate(item.Value);
                if (candidate.Length == 0) continue;

                var score = 50;
                if (key == "skutext" || key == "skupropertiesname" || key == "propertiesname") score += 80;
                if (key.Contains("display") || key.Contains("description") || key.Contains("desc")) score += 45;
                if (candidate.Contains(":")) score += 35;
                if (candidate.Any(ch => ch >= 0x3400 && ch <= 0x9fff)) score += 20;
                score += Math.Min(30, candidate.Length / 4);

                if (score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return best;
        }

        private static string ResolveStructuredSkuPairs(IList<FlatValue> flat)
        {
            var values = (flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value))
                .ToList();
            var output = new List<string>();

            foreach (var group in values.GroupBy(x => ParentPath(x.Path), StringComparer.OrdinalIgnoreCase))
            {
                var parent = group.Key ?? string.Empty;
                var related = IsSkuRelatedPath(parent)
                    || group.Any(x => IsSkuRelatedPath(x.Path));
                if (!related) continue;

                var name = FindPairValue(group, SkuNameKeys);
                var value = FindPairValue(group, SkuValueKeys);

                // 很多接口使用最普通的 name/value，只有在父路径明确属于 SKU 时才接受，
                // 防止把商品标题、状态名称等无关字段误拼为规格。
                if (string.IsNullOrWhiteSpace(name) && IsSkuRelatedPath(parent))
                {
                    name = FindPairValue(group, new[] { "name", "title" });
                }
                if (string.IsNullOrWhiteSpace(value) && IsSkuRelatedPath(parent))
                {
                    value = FindPairValue(group, new[] { "selected", "content", "desc" });
                }

                name = NormalizeSkuPart(name);
                value = NormalizeSkuPart(value);
                if (name.Length == 0 || value.Length == 0) continue;
                if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIdentifierOnly(name) || IsIdentifierOnly(value)) continue;

                var pair = NormalizeSkuCandidate(name + ":" + value);
                if (pair.Length == 0 || output.Contains(pair, StringComparer.OrdinalIgnoreCase)) continue;
                output.Add(pair);
            }

            if (output.Count == 0) return string.Empty;
            return string.Join("; ", output.Take(8));
        }

        private static string FindPairValue(
            IEnumerable<FlatValue> group,
            IEnumerable<string> aliases)
        {
            var set = new HashSet<string>(
                (aliases ?? new string[0]).Select(NormalizeKey),
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in group ?? new FlatValue[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                if (set.Contains(NormalizeKey(item.Key))) return item.Value.Trim();
            }
            return string.Empty;
        }

        private static string NormalizeSkuCandidate(string value)
        {
            value = NormalizeSkuPart(value);
            if (value.Length == 0 || value.Length > 500) return string.Empty;
            if (LooksLikeJson(value)) return string.Empty;
            if (Regex.IsMatch(value, @"^https?://", RegexOptions.IgnoreCase)) return string.Empty;
            if (IsIdentifierOnly(value)) return string.Empty;

            value = Regex.Replace(
                value,
                @"^(?:SKU|规格名称|规格|销售属性|套餐|属性)\s*[:：]\s*",
                string.Empty,
                RegexOptions.IgnoreCase);
            value = value.Replace('：', ':');
            value = Regex.Replace(value, @"\s*:\s*", ":");
            value = Regex.Replace(value, @"\s*;\s*", "; ");

            if (!value.Contains(":"))
            {
                // 千牛可见订单卡片有时显示为“专辑名称一个月（老账号特价）”，
                // 结构化标签丢失分隔符时，恢复为“专辑名称:一个月（老账号特价）”。
                var known = Regex.Match(
                    value,
                    @"^(专辑名称|套餐名称|套餐|期限|时长|会员类型|充值类型|账号类型|商品规格|版本)\s*(.+)$");
                if (known.Success && known.Groups[2].Value.Trim().Length > 0)
                {
                    value = known.Groups[1].Value.Trim() + ":" + known.Groups[2].Value.Trim();
                }
            }

            if (value.Length < 2
                || string.Equals(value, "SKU", StringComparison.OrdinalIgnoreCase)
                || value == "规格"
                || value == "属性"
                || value == "套餐")
            {
                return string.Empty;
            }
            return value.Length <= 240 ? value : value.Substring(0, 240);
        }

        private static string NormalizeSkuPart(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = value.Trim('"', '\'', ',', ';', '，', '；', '{', '}', '[', ']');
            value = value.Replace("\\\"", "\"");
            return Regex.Replace(value.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        }

        private static bool IsIdentifierOnly(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (Regex.IsMatch(value, @"^\d{5,}$")) return true;
            return Regex.IsMatch(value, @"^[a-f0-9\-]{16,}$", RegexOptions.IgnoreCase);
        }

        private static bool IsSkuRelatedPath(string path)
        {
            var normalized = NormalizeKey(path);
            return normalized.Contains("sku")
                || normalized.Contains("spec")
                || normalized.Contains("property")
                || normalized.Contains("properties")
                || normalized.Contains("prop")
                || normalized.Contains("salesattribute")
                || normalized.Contains("saleattribute")
                || normalized.Contains("attribute")
                || normalized.Contains("option");
        }

        private static string ParentPath(string path)
        {
            path = path ?? string.Empty;
            var dot = path.LastIndexOf('.');
            return dot < 0 ? string.Empty : path.Substring(0, dot);
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
            if (token == null || depth > 18 || output.Count >= 2200) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    Walk(
                        property.Value,
                        path.Length == 0 ? property.Name : path + "." + property.Name,
                        output,
                        depth + 1);
                    if (output.Count >= 2200) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    if (++index >= 220 || output.Count >= 2200) break;
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
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 16000) parts.Add(raw);
            parts.AddRange((flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 1200)
                .Select(x => (x.Key ?? string.Empty) + ":" + x.Value.Trim())
                .Take(700));
            return Regex.Replace(
                string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)),
                @"\s+",
                " ").Trim();
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
            var set = new HashSet<string>(
                (aliases ?? new string[0]).Select(NormalizeKey),
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item != null
                    && !string.IsNullOrWhiteSpace(item.Value)
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
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, out result)
                    && result > 0
                    && result <= 10000)
                {
                    return result;
                }
            }
            return 0;
        }

        private static decimal? ExtractPaidAmount(string text)
        {
            return ExtractMoneyByLabels(
                text,
                "买家实付", "实付金额", "实际付款", "付款金额",
                "支付金额", "成交金额", "实付", "实收");
        }

        private static decimal? ExtractTotalAmount(string text)
        {
            return ExtractMoneyByLabels(
                text,
                "订单总金额", "订单总额", "订单总价", "应付金额",
                "应付", "合计", "总价", "金额");
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
            return match.Success
                && int.TryParse(match.Value, out result)
                && result > 0
                && result <= 10000
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
                    if (raw > 1000000000000000L)
                        return DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    if (raw > 100000000000L)
                        return DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    if (raw > 1000000000L)
                        return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                }
                catch { }
            }

            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal,
                    out dto)
                || DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out dto))
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
                (text ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? string.Empty;
        }

        private static bool? ResolvePaidState(string text, string status)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(
                value,
                "未付款|等待买家付款|待付款|付款关闭|交易关闭",
                RegexOptions.IgnoreCase))
            {
                return false;
            }
            if (Regex.IsMatch(
                value,
                "已付款|付款成功|支付成功|交易成功|已支付|待卖家发货|WAIT_SELLER_SEND_GOODS|TRADE_PAID",
                RegexOptions.IgnoreCase))
            {
                return true;
            }
            return null;
        }

        private static OrderEventType ResolveEventType(string text, string status, bool? paid)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, "申请退款|退款中|退货退款|仅退款", RegexOptions.IgnoreCase))
                return OrderEventType.RefundRequested;
            if (Regex.IsMatch(value, "订单关闭|交易关闭|已关闭|已取消", RegexOptions.IgnoreCase))
                return OrderEventType.Closed;
            return paid == true ? OrderEventType.Paid : OrderEventType.Created;
        }

        private static string BuildSafeEventText(OrderSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append(snapshot.EventType == OrderEventType.Paid ? "买家已付款 " : "买家已下单 ");
            sb.Append("订单号：").Append(snapshot.OrderId);
            if (!string.IsNullOrWhiteSpace(snapshot.ItemTitle))
                sb.Append(" 商品：").Append(Short(snapshot.ItemTitle, 220));
            if (!string.IsNullOrWhiteSpace(snapshot.SkuText))
                sb.Append(" 规格：").Append(Short(snapshot.SkuText, 180));
            if (snapshot.Quantity > 0)
                sb.Append(" 数量：").Append(snapshot.Quantity);
            if (snapshot.PaidAmount.HasValue)
                sb.Append(" 实付：").Append(snapshot.PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            else if (snapshot.TotalAmount.HasValue)
                sb.Append(" 金额：").Append(snapshot.TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(" 订单状态：").Append(
                string.IsNullOrWhiteSpace(snapshot.TradeStatus)
                    ? (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                    : snapshot.TradeStatus);
            return sb.ToString();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0])
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                ?? string.Empty;
        }

        private static string DigitsOnly(string value, int min, int max)
        {
            var digits = Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
            return digits.Length >= min && digits.Length <= max ? digits : string.Empty;
        }

        private static string Clean(string value, int max)
        {
            value = Regex.Replace(
                (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(),
                @"\s+",
                " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string NormalizeKey(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).ToLowerInvariant(),
                @"[^a-z0-9]",
                string.Empty);
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"\s+",
                string.Empty);
        }

        private static string Hash(string value)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(
                            sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatMoney(decimal? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "<missing>";
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
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
}
