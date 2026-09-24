using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    internal enum OrderEventType
    {
        Created = 0,
        Paid = 1,
        Closed = 2,
        RefundRequested = 3
    }

    internal sealed class OrderSnapshot
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string OrderId { get; set; }
        public string ItemId { get; set; }
        public string ItemTitle { get; set; }
        public string SkuId { get; set; }
        public string SkuText { get; set; }
        public string BuyerRemark { get; set; }
        public int Quantity { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? PaidAmount { get; set; }
        public string TradeStatus { get; set; }
        public bool? IsPaid { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public string ProductUrl { get; set; }
        public string ImageUrl { get; set; }
        public string Source { get; set; }
        public string RawCardHash { get; set; }
        public DateTime DetectedAt { get; set; }
        public DateTime EventTime { get; set; }
        public OrderEventType EventType { get; set; }
        public string EventText { get; set; }

        public OrderSnapshot()
        {
            DetectedAt = DateTime.Now;
            EventTime = DateTime.Now;
            Source = "千牛订单卡片";
            EventText = string.Empty;
        }

        public string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.Append("买家：").Append(Safe(Buyer, 100)).Append("\n");
            sb.Append("订单号：").Append(Safe(OrderId, 80)).Append("\n");
            if (!string.IsNullOrWhiteSpace(ItemTitle)) sb.Append("商品：").Append(Safe(ItemTitle, 180)).Append("\n");
            if (!string.IsNullOrWhiteSpace(SkuText)) sb.Append("规格：").Append(Safe(SkuText, 160)).Append("\n");
            if (!string.IsNullOrWhiteSpace(BuyerRemark)) sb.Append("买家备注：").Append(Safe(BuyerRemark, 300)).Append("\n");
            if (Quantity > 0) sb.Append("数量：").Append(Quantity).Append("\n");
            if (PaidAmount.HasValue) sb.Append("实付：¥").Append(PaidAmount.Value.ToString("0.00", CultureInfo.InvariantCulture)).Append("\n");
            else if (TotalAmount.HasValue) sb.Append("金额：¥").Append(TotalAmount.Value.ToString("0.00", CultureInfo.InvariantCulture)).Append("\n");
            if (!string.IsNullOrWhiteSpace(TradeStatus)) sb.Append("状态：").Append(Safe(TradeStatus, 100)).Append("\n");
            else sb.Append("状态：").Append(EventType == OrderEventType.Paid ? "已付款" : "新下单").Append("\n");
            var time = PaidAt ?? CreatedAt ?? (DateTime?)EventTime;
            if (time.HasValue) sb.Append("时间：").Append(time.Value.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            sb.Append("来源：").Append(Source ?? "千牛订单卡片");
            return sb.ToString().Trim();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    internal sealed class OrderEventPublishResult
    {
        public bool Detected { get; set; }
        public bool Accepted { get; set; }
        public string Reason { get; set; }
        public OrderSnapshot Snapshot { get; set; }
    }

    internal static class OrderCardParser
    {
        private sealed class FlatValue
        {
            public string Path;
            public string Key;
            public string Value;
        }

        private static readonly Regex OrderIdRegex = new Regex(
            @"(?:订单号|订单编号|主订单号|子订单号|交易号|订单)\s*[:：#]?\s*(\d{8,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] OrderIdKeys =
        {
            "orderid", "bizorderid", "mainorderid", "suborderid", "tradeid", "tid", "biztradeid"
        };
        private static readonly string[] ItemIdKeys =
        {
            "itemid", "numiid", "auctionid", "productid", "goodsid"
        };
        private static readonly string[] ItemTitleKeys =
        {
            "itemtitle", "auctiontitle", "producttitle", "goodstitle", "itemname", "productname", "subject"
        };
        private static readonly string[] SkuIdKeys =
        {
            "skuid", "skuidstr", "skuidentifier"
        };
        private static readonly string[] SkuTextKeys =
        {
            "skutext", "skuname", "skupropertiesname", "propertiesname", "spec", "specification", "skudesc"
        };
        private static readonly string[] BuyerRemarkKeys =
        {
            "buyerremark", "buyermemo", "buyernote", "buyermessage", "buyermessagecontent",
            "buyermsg", "remarkfrombuyer", "memofrombuyer"
        };
        private static readonly string[] QuantityKeys =
        {
            "quantity", "num", "buyamount", "itemcount", "count"
        };
        private static readonly string[] TotalAmountKeys =
        {
            "totalamount", "totalfee", "orderamount", "amount", "totalprice"
        };
        private static readonly string[] PaidAmountKeys =
        {
            "paidamount", "payment", "actualfee", "paidfee", "realpay", "actualamount"
        };
        private static readonly string[] StatusKeys =
        {
            "tradestatus", "orderstatus", "paystatus", "status", "statustext"
        };
        private static readonly string[] CreatedTimeKeys =
        {
            "createdat", "createtime", "ordertime", "createdtime", "tradecreatetime"
        };
        private static readonly string[] PaidTimeKeys =
        {
            "paidat", "paytime", "paidtime", "paymenttime"
        };
        private static readonly string[] ProductUrlKeys =
        {
            "producturl", "itemurl", "auctionurl", "detailurl", "url"
        };
        private static readonly string[] ImageUrlKeys =
        {
            "imageurl", "picurl", "itempic", "pic", "image"
        };

        public static bool TryParse(
            QNChatMessage message,
            string messageText,
            string seller,
            string buyer,
            string source,
            out OrderSnapshot snapshot)
        {
            snapshot = null;
            var json = SerializeMessage(message);
            var flat = Flatten(json);
            var combined = BuildCombinedText(messageText, flat);
            var orderId = ExtractOrderId(combined, flat);
            if (string.IsNullOrWhiteSpace(orderId)) return false;

            var itemTitle = FindValue(flat, ItemTitleKeys);
            if (string.IsNullOrWhiteSpace(itemTitle)) itemTitle = ExtractLabeledText(combined, "商品", "宝贝", "商品名称");
            var skuText = FindValue(flat, SkuTextKeys);
            if (string.IsNullOrWhiteSpace(skuText)) skuText = ExtractLabeledText(combined, "规格", "SKU", "属性");
            var buyerRemark = FindValue(flat, BuyerRemarkKeys);
            if (string.IsNullOrWhiteSpace(buyerRemark)) buyerRemark = ExtractLabeledText(combined, "买家备注", "买家留言");
            var tradeStatus = FindValue(flat, StatusKeys);
            if (string.IsNullOrWhiteSpace(tradeStatus)) tradeStatus = ExtractStatus(combined);

            var hasOrderCue = LooksLikeOrderText(combined);
            var hasStructuredOrderCue = !string.IsNullOrWhiteSpace(itemTitle)
                || FindValue(flat, TotalAmountKeys).Length > 0
                || FindValue(flat, PaidAmountKeys).Length > 0
                || !string.IsNullOrWhiteSpace(tradeStatus);
            if (!hasOrderCue && !hasStructuredOrderCue) return false;

            DateTime eventTime;
            if (!TryGetMessageTime(message, out eventTime)) eventTime = DateTime.Now;
            var paid = ResolvePaidState(combined, tradeStatus);
            var eventType = ResolveEventType(combined, tradeStatus, paid);
            snapshot = new OrderSnapshot
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                OrderId = orderId,
                ItemId = DigitsOnly(FindValue(flat, ItemIdKeys), 6, 32),
                ItemTitle = Clean(itemTitle, 300),
                SkuId = Clean(FindValue(flat, SkuIdKeys), 100),
                SkuText = Clean(skuText, 240),
                BuyerRemark = Clean(buyerRemark, 500),
                Quantity = ParseInt(FindValue(flat, QuantityKeys)),
                TotalAmount = ParseMoney(FindValue(flat, TotalAmountKeys)),
                PaidAmount = ParseMoney(FindValue(flat, PaidAmountKeys)),
                TradeStatus = Clean(tradeStatus, 120),
                IsPaid = paid,
                CreatedAt = ParseDate(FindValue(flat, CreatedTimeKeys)),
                PaidAt = ParseDate(FindValue(flat, PaidTimeKeys)),
                ProductUrl = FindUrl(flat, ProductUrlKeys, "item.taobao.com"),
                ImageUrl = FindUrl(flat, ImageUrlKeys, null),
                Source = string.IsNullOrWhiteSpace(source) ? "千牛订单卡片" : source.Trim(),
                RawCardHash = Hash(json == null ? combined : json.ToString(Formatting.None)),
                DetectedAt = DateTime.Now,
                EventTime = eventTime,
                EventType = eventType,
                EventText = Clean(combined, 2000)
            };
            if (!snapshot.CreatedAt.HasValue && eventType == OrderEventType.Created) snapshot.CreatedAt = eventTime;
            if (!snapshot.PaidAt.HasValue && eventType == OrderEventType.Paid) snapshot.PaidAt = eventTime;
            if (!snapshot.TotalAmount.HasValue && snapshot.PaidAmount.HasValue) snapshot.TotalAmount = snapshot.PaidAmount;
            return true;
        }

        private static JObject SerializeMessage(QNChatMessage message)
        {
            if (message == null) return null;
            try { return JObject.FromObject(message); }
            catch { return null; }
        }

        private static List<FlatValue> Flatten(JToken root)
        {
            var result = new List<FlatValue>();
            Walk(root, string.Empty, result, 0);
            return result;
        }

        private static void Walk(JToken token, string path, List<FlatValue> output, int depth)
        {
            if (token == null || output.Count >= 700 || depth > 14) return;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    var childPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                    Walk(property.Value, childPath, output, depth + 1);
                    if (output.Count >= 700) break;
                }
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                var index = 0;
                foreach (var child in (JArray)token)
                {
                    Walk(child, path + "[" + index + "]", output, depth + 1);
                    index++;
                    if (output.Count >= 700 || index >= 80) break;
                }
                return;
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return;
            var value = token.ToString().Trim();
            if (value.Length == 0 || value.Length > 2500) return;
            var key = path;
            var dot = key.LastIndexOf('.');
            if (dot >= 0) key = key.Substring(dot + 1);
            var bracket = key.IndexOf('[');
            if (bracket >= 0) key = key.Substring(0, bracket);
            output.Add(new FlatValue { Path = path, Key = key, Value = value });
        }

        private static string BuildCombinedText(string messageText, IList<FlatValue> values)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(messageText)) parts.Add(messageText.Trim());
            foreach (var value in values ?? new List<FlatValue>())
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Value)) continue;
                if (value.Value.Length > 600) continue;
                var normalizedKey = NormalizeKey(value.Key);
                if (normalizedKey.Contains("text")
                    || normalizedKey.Contains("title")
                    || normalizedKey.Contains("summary")
                    || normalizedKey.Contains("status")
                    || normalizedKey.Contains("desc")
                    || normalizedKey.Contains("name")
                    || normalizedKey.Contains("amount")
                    || normalizedKey.Contains("fee"))
                {
                    parts.Add(value.Value.Trim());
                }
            }
            var merged = string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
            return Regex.Replace(merged, @"\s+", " ").Trim();
        }

        private static string ExtractOrderId(string text, IList<FlatValue> values)
        {
            var match = OrderIdRegex.Match(text ?? string.Empty);
            if (match.Success) return match.Groups[1].Value;
            var value = FindValue(values, OrderIdKeys);
            return DigitsOnly(value, 8, 40);
        }

        private static string FindValue(IList<FlatValue> values, IEnumerable<string> aliases)
        {
            var normalizedAliases = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? new List<FlatValue>())
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Value)) continue;
                if (normalizedAliases.Contains(NormalizeKey(value.Key))) return value.Value.Trim();
            }
            foreach (var value in values ?? new List<FlatValue>())
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Value)) continue;
                var path = NormalizeKey(value.Path);
                if (normalizedAliases.Any(alias => path.EndsWith(alias, StringComparison.OrdinalIgnoreCase))) return value.Value.Trim();
            }
            return string.Empty;
        }

        private static string FindUrl(IList<FlatValue> values, IEnumerable<string> aliases, string preferredHost)
        {
            var candidates = new List<string>();
            var normalizedAliases = new HashSet<string>((aliases ?? new string[0]).Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? new List<FlatValue>())
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Value)) continue;
                var normalizedKey = NormalizeKey(value.Key);
                if (!normalizedAliases.Contains(normalizedKey)) continue;
                Uri uri;
                if (Uri.TryCreate(value.Value.Trim(), UriKind.Absolute, out uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    candidates.Add(uri.ToString());
                }
            }
            if (!string.IsNullOrWhiteSpace(preferredHost))
            {
                var preferred = candidates.FirstOrDefault(x => x.IndexOf(preferredHost, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
            }
            return candidates.FirstOrDefault() ?? string.Empty;
        }

        private static string ExtractLabeledText(string text, params string[] labels)
        {
            foreach (var label in labels ?? new string[0])
            {
                var match = Regex.Match(text ?? string.Empty, Regex.Escape(label) + @"\s*[:：]\s*([^|，。;；]{2,120})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
            }
            return string.Empty;
        }

        private static string ExtractStatus(string text)
        {
            text = text ?? string.Empty;
            var known = new[] { "已付款", "等待买家付款", "未付款", "交易成功", "订单关闭", "退款中", "买家已下单", "订单创建成功" };
            return known.FirstOrDefault(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) ?? string.Empty;
        }

        private static bool LooksLikeOrderText(string text)
        {
            text = text ?? string.Empty;
            return (text.Contains("件商品") && text.Contains("合计"))
                || text.Contains("交易时间")
                || text.Contains("买家已下单")
                || text.Contains("订单创建成功")
                || text.Contains("等待买家付款")
                || text.Contains("已付款")
                || text.Contains("订单号")
                || text.Contains("订单编号");
        }

        private static bool? ResolvePaidState(string text, string status)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, @"未付款|等待买家付款|待付款|付款关闭|交易关闭")) return false;
            if (Regex.IsMatch(value, @"已付款|付款成功|交易成功|已支付|支付成功")) return true;
            return null;
        }

        private static OrderEventType ResolveEventType(string text, string status, bool? paid)
        {
            var value = (text ?? string.Empty) + " " + (status ?? string.Empty);
            if (Regex.IsMatch(value, @"申请退款|退款中|退货退款|仅退款")) return OrderEventType.RefundRequested;
            if (Regex.IsMatch(value, @"订单关闭|交易关闭|已关闭|已取消")) return OrderEventType.Closed;
            if (paid == true) return OrderEventType.Paid;
            return OrderEventType.Created;
        }

        private static int ParseInt(string value)
        {
            int result;
            var match = Regex.Match(value ?? string.Empty, @"\d+");
            return match.Success && int.TryParse(match.Value, out result) && result > 0 && result <= 10000 ? result : 0;
        }

        private static decimal? ParseMoney(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            decimal amount;
            var match = Regex.Match(value.Replace(",", string.Empty), @"-?\d+(?:\.\d{1,4})?");
            if (!match.Success || !decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) return null;
            if (amount < 0 || amount > 100000000m) return null;
            return decimal.Round(amount, 2);
        }

        private static DateTime? ParseDate(string value)
        {
            DateTime local;
            if (TryParseTime(value, out local)) return local;
            return null;
        }

        internal static bool TryGetMessageTime(QNChatMessage message, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (message == null) return false;
            return TryParseTime(message.sendTime, out localTime)
                || TryParseTime(message.sortTimeMicrosecond, out localTime);
        }

        private static bool TryParseTime(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
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

        private static string Hash(string value)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                    return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch { return string.Empty; }
        }
    }

    internal static class OrderEventHub
    {
        private const int StateMutexWaitMilliseconds = 3000;
        private const int StateIoRetryCount = 4;
        private const int StateIoRetryDelayMilliseconds = 60;

        private sealed class StoredOrderEvent
        {
            public string Key { get; set; }

            // Immutable first-accept timestamp. The Hub may observe the same order again days later
            // (history replay, duplicate messageCenterNotify, cross-process merge). SeenAt is allowed
            // to move forward for retention/diagnostics, but AcceptedAt must never be refreshed by a
            // duplicate because the auto-reply fallback uses it as the side-effect freshness proof.
            public DateTime AcceptedAt { get; set; }
            public DateTime SeenAt { get; set; }
            public OrderSnapshot Snapshot { get; set; }
        }

        private sealed class StoredState
        {
            public List<StoredOrderEvent> Events { get; set; }

            public StoredState()
            {
                Events = new List<StoredOrderEvent>();
            }
        }

        private sealed class StateMutexLease : IDisposable
        {
            private readonly Mutex _mutex;
            public bool Acquired { get; private set; }

            public StateMutexLease(Mutex mutex, bool acquired)
            {
                _mutex = mutex;
                Acquired = acquired;
            }

            public void Dispose()
            {
                if (Acquired)
                {
                    try { _mutex.ReleaseMutex(); }
                    catch { }
                    Acquired = false;
                }
                try { _mutex.Dispose(); }
                catch { }
            }
        }

        private static readonly object Sync = new object();
        private static StoredState _state;

        public static OrderSnapshot RefreshFromCanonical(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId)) return snapshot;
            lock (Sync)
            {
                var path = GetPath();
                using (var lease = AcquireStateMutex(path))
                {
                    if (lease.Acquired) ReloadAndMergeFromDisk(path);
                    else EnsureLoaded();
                    var key = BuildKey(snapshot);
                    var existing = _state.Events.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                    if (existing == null || existing.Snapshot == null) return snapshot;
                    Merge(existing.Snapshot, snapshot);
                    return existing.Snapshot;
                }
            }
        }

        public static OrderEventPublishResult Publish(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId))
            {
                return new OrderEventPublishResult { Detected = false, Accepted = false, Reason = "订单快照为空", Snapshot = snapshot };
            }

            lock (Sync)
            {
                var path = GetPath();
                using (var lease = AcquireStateMutex(path))
                {
                    if (lease.Acquired)
                    {
                        ReloadAndMergeFromDisk(path);
                    }
                    else
                    {
                        EnsureLoaded();
                        Log.ErrorWithMaxCount("订单事件状态跨进程锁等待超时；本次事件继续处理，但持久化去重降级为进程内状态。", 10);
                    }

                    var now = DateTime.Now;
                    _state.Events.RemoveAll(x => x == null || x.SeenAt < now.AddDays(-30));
                    var key = BuildKey(snapshot);
                    var existing = _state.Events.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                    if (existing != null)
                    {
                        // Legacy state files only had SeenAt. Capture that value before refreshing
                        // SeenAt so a duplicate historical event can never become a newly accepted
                        // event for the Hub auto-reply consumer.
                        var previousSeenAt = existing.SeenAt;
                        if (existing.AcceptedAt == DateTime.MinValue)
                        {
                            existing.AcceptedAt = previousSeenAt == DateTime.MinValue ? now : previousSeenAt;
                        }
                        Merge(existing.Snapshot, snapshot);
                        existing.SeenAt = now;
                        if (lease.Acquired) SaveInternal(path);
                        Log.Info("订单事件已去重: key=" + key + ", buyer=" + snapshot.Buyer
                            + ", acceptedAt=" + existing.AcceptedAt.ToString("yyyy-MM-dd HH:mm:ss")
                            + ", lastSeenAt=" + existing.SeenAt.ToString("yyyy-MM-dd HH:mm:ss"));
                        return new OrderEventPublishResult
                        {
                            Detected = true,
                            Accepted = false,
                            Reason = "相同订单事件已处理",
                            Snapshot = existing.Snapshot ?? snapshot
                        };
                    }

                    _state.Events.Add(new StoredOrderEvent
                    {
                        Key = key,
                        AcceptedAt = now,
                        SeenAt = now,
                        Snapshot = snapshot
                    });
                    if (_state.Events.Count > 2000)
                    {
                        _state.Events = _state.Events.OrderByDescending(x => x.SeenAt).Take(2000).ToList();
                    }
                    var persisted = lease.Acquired && SaveInternal(path);
                    Log.Info("识别到结构化订单事件: event=" + snapshot.EventType
                        + ", seller=" + snapshot.Seller
                        + ", buyer=" + snapshot.Buyer
                        + ", orderId=" + snapshot.OrderId
                        + ", item=" + Short(snapshot.ItemTitle, 80)
                        + ", status=" + snapshot.TradeStatus
                        + ", persisted=" + persisted);
                    return new OrderEventPublishResult
                    {
                        Detected = true,
                        Accepted = true,
                        Reason = persisted ? "新订单事件" : "新订单事件（状态持久化暂时降级）",
                        Snapshot = snapshot
                    };
                }
            }
        }

        private static StateMutexLease AcquireStateMutex(string path)
        {
            var mutex = new Mutex(false, BuildStateMutexName(path));
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(StateMutexWaitMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                Log.Info("检测到订单事件状态锁的旧进程已退出，已接管互斥锁并继续恢复状态。");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("获取订单事件跨进程状态锁失败：" + ex.Message, 10);
            }
            return new StateMutexLease(mutex, acquired);
        }

        private static string BuildStateMutexName(string path)
        {
            var normalized = string.Empty;
            try { normalized = Path.GetFullPath(path ?? string.Empty).Trim().ToLowerInvariant(); }
            catch { normalized = (path ?? string.Empty).Trim().ToLowerInvariant(); }
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
                return @"Local\QianniuAiBot.OrderEventState." + hash.Substring(0, Math.Min(32, hash.Length));
            }
        }

        private static string BuildKey(OrderSnapshot snapshot)
        {
            return Normalize(snapshot.Seller) + "#" + snapshot.OrderId + "#" + snapshot.EventType;
        }

        private static void Merge(OrderSnapshot target, OrderSnapshot incoming)
        {
            if (target == null || incoming == null) return;
            if (string.IsNullOrWhiteSpace(target.Buyer)) target.Buyer = incoming.Buyer;
            if (string.IsNullOrWhiteSpace(target.ItemId)) target.ItemId = incoming.ItemId;
            if (string.IsNullOrWhiteSpace(target.ItemTitle)) target.ItemTitle = incoming.ItemTitle;
            if (string.IsNullOrWhiteSpace(target.SkuId)) target.SkuId = incoming.SkuId;
            if (string.IsNullOrWhiteSpace(target.SkuText)) target.SkuText = incoming.SkuText;
            if (string.IsNullOrWhiteSpace(target.BuyerRemark)) target.BuyerRemark = incoming.BuyerRemark;
            if (target.Quantity <= 0) target.Quantity = incoming.Quantity;
            if (!target.TotalAmount.HasValue) target.TotalAmount = incoming.TotalAmount;
            if (!target.PaidAmount.HasValue) target.PaidAmount = incoming.PaidAmount;
            if (string.IsNullOrWhiteSpace(target.TradeStatus)) target.TradeStatus = incoming.TradeStatus;
            if (!target.IsPaid.HasValue) target.IsPaid = incoming.IsPaid;
            if (!target.CreatedAt.HasValue) target.CreatedAt = incoming.CreatedAt;
            if (!target.PaidAt.HasValue) target.PaidAt = incoming.PaidAt;
            if (string.IsNullOrWhiteSpace(target.ProductUrl)) target.ProductUrl = incoming.ProductUrl;
            if (string.IsNullOrWhiteSpace(target.ImageUrl)) target.ImageUrl = incoming.ImageUrl;
            if (string.IsNullOrWhiteSpace(target.RawCardHash)) target.RawCardHash = incoming.RawCardHash;
            if (incoming.DetectedAt > target.DetectedAt) target.DetectedAt = incoming.DetectedAt;
        }

        private static void EnsureLoaded()
        {
            if (_state != null) return;
            var path = GetPath();
            var disk = TryReadState(path);
            _state = disk ?? new StoredState();
            if (_state.Events == null) _state.Events = new List<StoredOrderEvent>();
        }

        private static void ReloadAndMergeFromDisk(string path)
        {
            var disk = TryReadState(path);
            if (disk == null)
            {
                if (_state == null) _state = new StoredState();
                if (_state.Events == null) _state.Events = new List<StoredOrderEvent>();
                return;
            }

            if (_state == null || _state.Events == null || _state.Events.Count == 0)
            {
                _state = disk;
                if (_state.Events == null) _state.Events = new List<StoredOrderEvent>();
                return;
            }

            var merged = disk;
            if (merged.Events == null) merged.Events = new List<StoredOrderEvent>();
            foreach (var local in _state.Events.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Key)))
            {
                var existing = merged.Events.FirstOrDefault(x => x != null && string.Equals(x.Key, local.Key, StringComparison.Ordinal));
                if (existing == null)
                {
                    merged.Events.Add(local);
                    continue;
                }
                if (existing.Snapshot == null) existing.Snapshot = local.Snapshot;
                else Merge(existing.Snapshot, local.Snapshot);

                // AcceptedAt is "first accepted", so cross-process reconciliation keeps the oldest
                // non-empty value. For pre-migration state, the old SeenAt is the best available
                // approximation and is intentionally captured before SeenAt is advanced.
                if (existing.AcceptedAt == DateTime.MinValue) existing.AcceptedAt = existing.SeenAt;
                var localAcceptedAt = local.AcceptedAt == DateTime.MinValue ? local.SeenAt : local.AcceptedAt;
                if (localAcceptedAt != DateTime.MinValue
                    && (existing.AcceptedAt == DateTime.MinValue || localAcceptedAt < existing.AcceptedAt))
                {
                    existing.AcceptedAt = localAcceptedAt;
                }
                if (local.SeenAt > existing.SeenAt) existing.SeenAt = local.SeenAt;
            }
            _state = merged;
        }

        private static StoredState TryReadState(string path)
        {
            if (!File.Exists(path)) return new StoredState();
            Exception last = null;
            for (var attempt = 1; attempt <= StateIoRetryCount; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var loaded = JsonConvert.DeserializeObject<StoredState>(reader.ReadToEnd()) ?? new StoredState();
                        if (loaded.Events == null) loaded.Events = new List<StoredOrderEvent>();
                        return loaded;
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt < StateIoRetryCount) Thread.Sleep(StateIoRetryDelayMilliseconds * attempt);
                }
                catch (Exception ex)
                {
                    Log.Info("读取订单事件去重状态失败，保留上一份内存状态：" + ex.Message);
                    return null;
                }
            }
            if (last != null) Log.ErrorWithMaxCount("读取订单事件状态连续失败，保留上一份内存状态：" + last.Message, 10);
            return null;
        }

        private static bool SaveInternal(string path)
        {
            var directory = Path.GetDirectoryName(path);
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("创建订单事件状态目录失败：" + ex.Message, 10);
                return false;
            }

            var temp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(_state, Formatting.Indented));
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                Exception last = null;
                for (var attempt = 1; attempt <= StateIoRetryCount; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null, true);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException ex)
                    {
                        last = ex;
                        if (attempt < StateIoRetryCount) Thread.Sleep(StateIoRetryDelayMilliseconds * attempt);
                    }
                }
                if (last != null)
                {
                    Log.ErrorWithMaxCount("原子替换订单事件状态连续失败；旧状态文件已保留：" + last.Message, 10);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存订单事件状态失败；旧状态文件已保留：" + ex.Message, 10);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "order-event-state.json");
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
