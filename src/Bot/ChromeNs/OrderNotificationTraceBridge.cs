using Bot.ChatRecord;
using BotLib;
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

namespace Bot.ChromeNs
{
    /// <summary>
    /// 为 messageCenterNotify 提供可追踪的订单诊断与嵌套 JSON 恢复。
    /// 旧链路在最外层正则未命中时直接返回，日志只剩“收到事件”，无法知道缺少状态、订单号还是买家身份。
    /// 本桥接器先展开所有嵌套 JSON，再输出仅包含哈希、长度和字段名的隐私安全诊断。
    /// </summary>
    internal static class OrderNotificationTraceBridge
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly ConditionalWeakTable<QN, object> Attached = new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, DateTime> Reservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _started;

        private static readonly Regex EventCueRegex = new Regex(
            "买家已下单|下单成功|订单创建成功|已成功下单|买家已付款|付款成功|支付成功|待卖家发货|卖家待发货|等待买家付款|申请退款|退款中|交易关闭|订单关闭|WAIT_SELLER_SEND_GOODS|WAIT_BUYER_PAY|TRADE_CREATED|TRADE_PAID|TRADE_BUYER_SIGNED|TRADE_FINISHED|TRADE_CLOSED|REFUND",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LabeledOrderRegex = new Regex(
            "(?:订单号|订单编号|主订单号|子订单号|交易号)\\s*[:：#]?\\s*(\\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] OrderKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "biztradeid", "tradeid", "tid",
            "parentorderid", "bizordercode", "tradecode"
        };

        private static readonly string[] BuyerKeys =
        {
            "buyernick", "buyernickname", "buyername", "buyerid", "buyeruid",
            "customernick", "customername", "customerid", "customeruid",
            "contactnick", "contactname", "contactid", "conversationnick", "conversationname",
            "oppositenick", "oppositename", "peernick", "peername",
            "sendernick", "sendername", "fromnick", "fromname", "usernick", "username"
        };

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new Timer(_ => Attach(), null, 0, 750);
            Log.Info("订单通知可追踪诊断已启动：messageCenterNotify 将记录隐私安全的识别结果。");
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    try
                    {
                        lock (AttachSync)
                        {
                            object marker;
                            if (Attached.TryGetValue(qn, out marker)) continue;
                            var subscriptionAttempted = false;
                            try
                            {
                                subscriptionAttempted = true;
                                qn.EvMessageNotity += OnMessageNotify;
                                Attached.Add(qn, new object());
                            }
                            catch
                            {
                                if (subscriptionAttempted) { try { qn.EvMessageNotity -= OnMessageNotify; } catch { } }
                                throw;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过异常千牛会话的订单通知订阅并继续：" + ex.Message, 10);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定订单通知诊断失败：" + ex.Message, 10);
            }
        }

        private static async void OnMessageNotify(object sender, MessageNotifyEventArgs e)
        {
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty).Trim();
            if (qn == null || raw.Length == 0) return;

            var hash = Hash(raw);
            Cleanup();
            DateTime until;
            if (Reservations.TryGetValue(hash, out until) && until >= DateTime.Now) return;
            Reservations[hash] = DateTime.Now.AddMinutes(2);

            try
            {
                var flat = Flatten(ParseExpanded(raw));
                var combined = raw + " " + string.Join(" ", flat
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Value))
                    .Select(x => x.Value)
                    .Take(350));
                var fields = DiagnosticFields(flat);
                var hasCue = EventCueRegex.IsMatch(combined);
                var orderId = ResolveOrderId(flat, combined);

                if (!hasCue || string.IsNullOrWhiteSpace(orderId))
                {
                    var reason = !hasCue && string.IsNullOrWhiteSpace(orderId)
                        ? "缺少明确订单状态和订单号"
                        : (!hasCue ? "缺少明确下单/付款状态" : "缺少可验证订单号");
                    Log.Info("订单通知未形成自动回复计划: payloadHash=" + hash
                        + ", length=" + raw.Length + ", reason=" + reason + ", fields=" + fields);
                    return;
                }

                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                var buyer = ResolveBuyer(flat, seller, orderId);
                if (string.IsNullOrWhiteSpace(buyer))
                {
                    Log.Info("订单通知已识别状态和订单号但缺少买家身份，未猜测当前会话: payloadHash=" + hash
                        + ", orderId=" + orderId + ", fields=" + fields);
                    return;
                }

                var paid = Regex.IsMatch(combined,
                    "买家已付款|付款成功|支付成功|待卖家发货|WAIT_SELLER_SEND_GOODS|TRADE_PAID",
                    RegexOptions.IgnoreCase);
                var summary = (paid ? "买家已付款 " : "买家已下单 ")
                    + "订单号：" + orderId + " 订单状态：" + (paid ? "已付款" : "新下单");
                Log.Info("订单通知诊断桥接解析成功: seller=" + seller + ", buyer=" + buyer
                    + ", orderId=" + orderId + ", paid=" + paid + ", payloadHash=" + hash);
                await qn.ProcessDirectOrderMessageAsync(
                    new QNChatMessage { summary = summary },
                    seller,
                    buyer,
                    "messageCenterNotify完整展开诊断桥接");
            }
            catch (Exception ex)
            {
                Log.Info("订单通知诊断解析异常: payloadHash=" + hash + ", length=" + raw.Length
                    + ", error=" + ex.Message);
            }
        }

        private static JToken ParseExpanded(string raw)
        {
            JToken token;
            try { token = JToken.Parse(raw); }
            catch { return new JValue(raw); }
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
                var i = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + i + "]", output, depth + 1);
                    if (++i >= 180 || output.Count >= 1800) break;
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

            if (token.Type == JTokenType.String && depth < 16)
            {
                var nested = value.Trim();
                if ((nested.StartsWith("{") && nested.EndsWith("}"))
                    || (nested.StartsWith("[") && nested.EndsWith("]")))
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
                if (item == null || !OrderKeys.Contains(Normalize(item.Key))) continue;
                var digits = Regex.Replace(item.Value ?? string.Empty, "\\D", string.Empty);
                if (digits.Length >= 8 && digits.Length <= 40) return digits;
            }
            var match = LabeledOrderRegex.Match(combined ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ResolveBuyer(IList<FlatValue> flat, string seller, string orderId)
        {
            var best = string.Empty;
            var scoreBest = 0;
            foreach (var item in flat ?? new List<FlatValue>())
            {
                if (item == null) continue;
                var value = (item.Value ?? string.Empty).Trim();
                if (!UsableBuyer(value, seller, orderId)) continue;
                var key = Normalize(item.Key);
                var path = Normalize(item.Path);
                var score = 0;
                if (BuyerKeys.Contains(key)) score += 100;
                if (path.Contains("buyer")) score += 90;
                if (path.Contains("customer")) score += 85;
                if (path.Contains("contact")) score += 75;
                if (path.Contains("opposite") || path.Contains("peer")) score += 70;
                if (path.Contains("conversation")) score += 65;
                if (path.Contains("sender") || path.Contains("from")) score += 45;
                if (key.EndsWith("nick") || key.EndsWith("name")) score += 25;
                if (score <= scoreBest) continue;
                scoreBest = score;
                best = value;
            }
            return scoreBest >= 55 ? best : string.Empty;
        }

        private static bool UsableBuyer(string value, string seller, string orderId)
        {
            if (value.Length < 2 || value.Length > 180) return false;
            if (DirectOrderIdentityResolver.IdentityEquals(value, seller)) return false;
            if (Regex.Replace(value, "\\D", string.Empty) == orderId) return false;
            if (value.Contains("订单") || value.Contains("付款") || value.Contains("商品") || value.Contains("金额")) return false;
            if (value.StartsWith("{") || value.StartsWith("[")) return false;
            return !Regex.IsMatch(value, "^\\d{16,}$");
        }

        private static string DiagnosticFields(IEnumerable<FlatValue> flat)
        {
            return string.Join(",", (flat ?? new List<FlatValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => Normalize(x.Key))
                .Where(x => x.Length > 0)
                .Distinct()
                .Take(35));
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void Cleanup()
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
