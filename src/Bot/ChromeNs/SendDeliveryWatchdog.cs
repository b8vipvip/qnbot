using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class SendDeliveryWatchdog
    {
        private const int VerifyDelayMilliseconds = 9000;
        private static readonly ConcurrentDictionary<string, PendingDelivery> Pending =
            new ConcurrentDictionary<string, PendingDelivery>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> KnownBotAnswers =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private sealed class PendingDelivery
        {
            public string Id;
            public ShopContext Shop;
            public string Seller;
            public string Buyer;
            public string Question;
            public string Answer;
            public string Source;
            public DateTime DetectedAt;
            public DateTime AnswerReadyAt;
            public DateTime WatchStartedAt;
            public int Started;
            public long SubmissionAcceptedTicks;
            public string SubmissionEvidence;
        }

        public static void OnBuyerMessageObserved(string seller, string buyer, DateTime observedAt)
        {
        }

        public static void ExpectDelivery(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerReadyAt,
            bool force = false)
        {
            if (!Params.Robot.CanUseRobotReal) return;
            if (!force && !Params.Robot.GetIsAutoReply()) return;
            answer = (answer ?? string.Empty).Trim();
            if (answer.Length == 0 || answer.StartsWith("错误：", StringComparison.Ordinal)) return;

            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;
            var shop = ResolveShop(seller);
            if (shop == null) return;

            var existing = FindPending(shop, seller, buyer, answer);
            if (existing != null)
            {
                existing.Question = question ?? existing.Question;
                existing.Source = source ?? existing.Source;
                return;
            }

            var readyAt = answerReadyAt == DateTime.MinValue ? DateTime.Now : answerReadyAt;
            var pending = new PendingDelivery
            {
                Id = shop.ShopKey + "-" + Guid.NewGuid().ToString("N"),
                Shop = shop,
                Seller = seller,
                Buyer = buyer,
                Question = question ?? string.Empty,
                Answer = answer,
                Source = source ?? string.Empty,
                DetectedAt = detectedAt == DateTime.MinValue ? readyAt : detectedAt,
                AnswerReadyAt = readyAt,
                WatchStartedAt = DateTime.MinValue,
                Started = 0,
                SubmissionAcceptedTicks = 0,
                SubmissionEvidence = string.Empty
            };
            Pending[pending.Id] = pending;
            Log.Info("已准备本店真实发送回显监控（尚未开始计时）: shop=" + shop.ShopKey
                + ", seller=" + pending.Seller + ", buyer=" + pending.Buyer
                + ", watchdogId=" + pending.Id + ", force=" + force);
        }

        public static string EnsurePending(string seller, string buyer, string answer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            answer = (answer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || answer.Length == 0) return string.Empty;
            var shop = ResolveShop(seller);
            if (shop == null) return string.Empty;

            var pending = FindPending(shop, seller, buyer, answer);
            if (pending == null)
            {
                var now = DateTime.Now;
                pending = new PendingDelivery
                {
                    Id = shop.ShopKey + "-" + Guid.NewGuid().ToString("N"),
                    Shop = shop,
                    Seller = seller,
                    Buyer = buyer,
                    Question = string.Empty,
                    Answer = answer,
                    Source = "自动发送",
                    DetectedAt = now,
                    AnswerReadyAt = now,
                    WatchStartedAt = DateTime.MinValue,
                    Started = 0,
                    SubmissionAcceptedTicks = 0,
                    SubmissionEvidence = string.Empty
                };
                Pending[pending.Id] = pending;
            }
            Activate(pending);
            return pending.Id;
        }

        /// <summary>
        /// Record a strong local submission proof without pretending that a seller echo was seen.
        /// Production logs show Qianniu can consume the exact Bot draft and clear the composer while
        /// the real-time seller echo is delayed/missed. That state must suppress retry/anomaly paths,
        /// but the pending watchdog remains alive so a later real echo can still upgrade the proof.
        /// </summary>
        public static bool MarkSubmissionAccepted(string seller, string buyer, string answer, string evidence)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            answer = (answer ?? string.Empty).Trim();
            evidence = (evidence ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            var normalized = Normalize(answer);
            if (seller.Length == 0 || buyer.Length == 0 || normalized.Length == 0) return false;

            var shopKey = ScopeKey(seller);
            var matches = Pending
                .Where(pair => pair.Value != null
                    && pair.Value.Shop != null
                    && string.Equals(pair.Value.Shop.ShopKey, shopKey, StringComparison.Ordinal)
                    && pair.Value.Started != 0
                    && string.Equals(pair.Value.Seller, seller, StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, buyer, StringComparison.Ordinal)
                    && Normalize(pair.Value.Answer) == normalized)
                .Select(pair => pair.Value)
                .ToList();

            if (matches.Count == 0) return false;

            var now = DateTime.Now;
            foreach (var pending in matches)
            {
                pending.SubmissionEvidence = evidence;
                Interlocked.Exchange(ref pending.SubmissionAcceptedTicks, now.Ticks);
            }

            // Mark the outbound body as Bot-owned immediately. A late seller echo/history recovery
            // must not be mistaken for a human reply merely because its live echo event was missed.
            KnownBotAnswers[AnswerKey(seller, buyer, answer)] = now.AddMinutes(2);
            CleanupKnownAnswers();
            Log.Info("发送回显监控已记录千牛提交证据，继续等待真实卖家回显但禁止据此判失败/重发: shop="
                + shopKey + ", seller=" + seller + ", buyer=" + buyer
                + ", matchedWatchdogs=" + matches.Count + ", evidence=" + evidence);
            return true;
        }

        public static int CancelPending(string seller, string buyer, string answer, string reason)
        {
            var shopKey = ScopeKey(seller);
            var normalized = Normalize(answer);
            var matches = Pending
                .Where(pair => pair.Value != null
                    && pair.Value.Shop != null
                    && string.Equals(pair.Value.Shop.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && pair.Value.Started == 0
                    && Normalize(pair.Value.Answer) == normalized)
                .ToList();
            var removedCount = 0;
            foreach (var pair in matches)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed)) removedCount++;
            }
            if (removedCount > 0)
            {
                Log.Info("已取消本店未开始/不再有效的发送回显监控: shop=" + shopKey
                    + ", seller=" + seller + ", buyer=" + buyer + ", count=" + removedCount
                    + ", reason=" + (reason ?? string.Empty));
            }
            return removedCount;
        }

        public static int CancelConversation(string seller, string buyer, string reason)
        {
            var shopKey = ScopeKey(seller);
            var matches = Pending
                .Where(pair => pair.Value != null
                    && pair.Value.Shop != null
                    && string.Equals(pair.Value.Shop.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal))
                .ToList();
            var removedCount = 0;
            foreach (var pair in matches)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed)) removedCount++;
            }
            if (removedCount > 0)
            {
                Log.Info("人工介入后已取消本店发送回显监控: shop=" + shopKey
                    + ", seller=" + seller + ", buyer=" + buyer + ", count=" + removedCount
                    + ", reason=" + (reason ?? string.Empty));
            }
            return removedCount;
        }

        private static PendingDelivery FindPending(ShopContext shop, string seller, string buyer, string answer)
        {
            var normalized = Normalize(answer);
            return Pending.Values.FirstOrDefault(value => value != null
                && value.Shop != null
                && string.Equals(value.Shop.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                && string.Equals(value.Seller, seller, StringComparison.Ordinal)
                && string.Equals(value.Buyer, buyer, StringComparison.Ordinal)
                && Normalize(value.Answer) == normalized);
        }

        private static void Activate(PendingDelivery pending)
        {
            if (pending == null || Interlocked.CompareExchange(ref pending.Started, 1, 0) != 0) return;
            pending.WatchStartedAt = DateTime.Now;
            Log.Info("已启动本店真实发送回显监控: shop=" + pending.Shop.ShopKey
                + ", seller=" + pending.Seller + ", buyer=" + pending.Buyer
                + ", watchdogId=" + pending.Id);

            Task.Run(async () =>
            {
                await Task.Delay(VerifyDelayMilliseconds);
                PendingDelivery current;
                if (!Pending.TryGetValue(pending.Id, out current)
                    || current == null || !ReferenceEquals(current, pending)) return;

                var delivered = false;
                using (ShopSettingsScope.Enter(pending.Shop))
                {
                    try
                    {
                        var qn = FindQn(pending.Shop, pending.Seller);
                        delivered = qn != null
                            && qn.HasRecentSellerEcho(pending.Buyer, pending.Answer, pending.WatchStartedAt);
                    }
                    catch (Exception ex)
                    {
                        Log.Info("本店发送回显监控检查异常: " + ex.Message);
                    }

                    PendingDelivery removed;
                    if (!Pending.TryRemove(pending.Id, out removed) || !ReferenceEquals(removed, pending)) return;

                    var submissionTicks = Interlocked.Read(ref pending.SubmissionAcceptedTicks);
                    if (!delivered && submissionTicks > 0)
                    {
                        ReplyQualityMetricsService.RecordSendResult(
                            true,
                            Math.Max(0, (long)(DateTime.Now - pending.DetectedAt).TotalMilliseconds));
                        var evidence = string.IsNullOrWhiteSpace(pending.SubmissionEvidence)
                            ? "发送动作后本次Bot精确草稿稳定清空"
                            : pending.SubmissionEvidence;
                        ResponseProgressTracker.MarkDeliveryConfirmed(
                            pending.Seller,
                            pending.Buyer,
                            pending.Answer,
                            "千牛已接收发送提交；实时卖家回显缺失，未据此判失败");
                        Log.Info("[本店发送回显缺失但提交已确认] shop=" + pending.Shop.ShopKey
                            + ", seller=" + pending.Seller + ", buyer=" + pending.Buyer
                            + ", watchdogId=" + pending.Id + ", submissionAcceptedAt="
                            + new DateTime(submissionTicks).ToString("HH:mm:ss.fff")
                            + ", evidence=" + evidence
                            + "; 不生成发送失败异常，不触发同文本重发。");
                        return;
                    }

                    if (!delivered)
                    {
                        ReplyQualityMetricsService.RecordSendResult(
                            false,
                            Math.Max(0, (long)(DateTime.Now - pending.DetectedAt).TotalMilliseconds));
                        var reason = "答案已经生成并进入自动发送流程，并且已真正进入发送动作，但在 "
                            + (VerifyDelayMilliseconds / 1000) + " 秒内既未检测到相同内容的卖家消息回显，"
                            + "也没有取得输入框稳定清空等千牛提交证据。"
                            + "可能是输入框/发送按钮操作未真正送达、回显事件缺失，或发送结果无法证明。";
                        ResponseProgressTracker.MarkDeliveryTimedOut(
                            pending.Seller, pending.Buyer, pending.Answer, reason);
                        Log.Error("[本店发送异常] shop=" + pending.Shop.ShopKey
                            + ", seller=" + pending.Seller + ", buyer=" + pending.Buyer
                            + ", watchdogId=" + pending.Id + ", reason=" + reason);
                        SendFailureAnomalyService.Queue(
                            pending.Seller, pending.Buyer, pending.Question, pending.Answer,
                            pending.Source, reason, pending.DetectedAt, pending.AnswerReadyAt, DateTime.Now);
                        return;
                    }

                    ReplyQualityMetricsService.RecordSendResult(
                        true,
                        Math.Max(0, (long)(DateTime.Now - pending.DetectedAt).TotalMilliseconds));
                    ResponseProgressTracker.MarkDeliveryConfirmed(
                        pending.Seller, pending.Buyer, pending.Answer, "延迟回显确认已发送");
                    Log.Info("本店发送回显监控确认成功: shop=" + pending.Shop.ShopKey
                        + ", seller=" + pending.Seller + ", buyer=" + pending.Buyer
                        + ", watchdogId=" + pending.Id);
                }
            });
        }

        public static bool ConfirmDelivery(string seller, string buyer, string answer)
        {
            var shopKey = ScopeKey(seller);
            var normalized = Normalize(answer);
            if (normalized.Length == 0) return false;

            var matched = Pending
                .Where(pair => pair.Value != null
                    && pair.Value.Shop != null
                    && string.Equals(pair.Value.Shop.ShopKey, shopKey, StringComparison.Ordinal)
                    && pair.Value.Started != 0
                    && string.Equals(pair.Value.Seller, (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && string.Equals(pair.Value.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)
                    && Normalize(pair.Value.Answer) == normalized)
                .ToList();

            var confirmed = false;
            foreach (var pair in matched)
            {
                PendingDelivery removed;
                if (Pending.TryRemove(pair.Key, out removed))
                {
                    confirmed = true;
                    if (removed != null)
                    {
                        ReplyQualityMetricsService.RecordSendResult(
                            true,
                            Math.Max(0, (long)(DateTime.Now - removed.DetectedAt).TotalMilliseconds));
                        ResponseProgressTracker.MarkDeliveryConfirmed(
                            removed.Seller, removed.Buyer, removed.Answer,
                            "已通过卖家消息回显确认真实发送");
                    }
                }
            }
            if (confirmed)
            {
                KnownBotAnswers[AnswerKey(seller, buyer, answer)] = DateTime.Now.AddMinutes(2);
                CleanupKnownAnswers();
                Log.Info("通过本店卖家消息回显确认Bot真实发送: shop=" + shopKey
                    + ", seller=" + seller + ", buyer=" + buyer
                    + ", matchedWatchdogs=" + matched.Count);
                return true;
            }

            DateTime expiresAt;
            return KnownBotAnswers.TryGetValue(AnswerKey(seller, buyer, answer), out expiresAt)
                && expiresAt >= DateTime.Now;
        }

        public static bool IsKnownBotAnswer(string seller, string buyer, string answer)
        {
            DateTime expiresAt;
            return KnownBotAnswers.TryGetValue(AnswerKey(seller, buyer, answer), out expiresAt)
                && expiresAt >= DateTime.Now;
        }

        private static QN FindQn(ShopContext shop, string seller)
        {
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    if (!string.Equals((qn.Seller.Nick ?? string.Empty).Trim(),
                        (seller ?? string.Empty).Trim(), StringComparison.Ordinal)) continue;
                    try
                    {
                        if (string.Equals(ShopIdentityResolver.Resolve(qn.Seller).ShopKey,
                            shop.ShopKey, StringComparison.Ordinal)) return qn;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static ShopContext ResolveShop(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch { return null; }
        }

        private static string ScopeKey(string seller)
        {
            var shop = ResolveShop(seller);
            return shop == null
                ? "legacy-" + (seller ?? string.Empty).Trim().ToLowerInvariant()
                : shop.ShopKey;
        }

        private static void CleanupKnownAnswers()
        {
            var now = DateTime.Now;
            foreach (var pair in KnownBotAnswers)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                KnownBotAnswers.TryRemove(pair.Key, out ignored);
            }
        }

        private static string ConversationKey(string seller, string buyer)
        {
            return ScopeKey(seller) + "#" + (seller ?? string.Empty).Trim()
                + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string AnswerKey(string seller, string buyer, string answer)
        {
            return ConversationKey(seller, buyer) + "#" + Normalize(answer);
        }

        private static string Normalize(string value)
        {
            // AnswerKey is the single authority for Bot-owned seller-echo identity. Qianniu may add
            // a transport-only trailing [A] marker to the seller echo, while the confirmed outbound
            // body stored in the ledger does not carry it. Canonicalize that suffix here so every
            // producer/consumer of KnownBotAnswers converges on the same identity.
            value = BotOutboundMessageFormatter.StripAiMarker(value ?? string.Empty);
            value = Regex.Replace(value.Trim(), @"\s*\[A\]\s*$", string.Empty, RegexOptions.IgnoreCase);
            return Regex.Replace(value.Trim(), @"\s+", string.Empty);
        }
    }
}
