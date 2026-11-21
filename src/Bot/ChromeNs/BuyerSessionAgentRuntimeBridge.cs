using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Feeds raw QN/CDP events into the shared BuyerSessionAgent before the independent reply/order
    /// pipelines process them. This gives one ordered ShopKey+seller+buyer timeline for text, image, product,
    /// order, withdrawal, system and seller reply events without replacing the existing stable send
    /// pipeline. Human seller replies are observational learning evidence and never cancel Bot work;
    /// explicit hard invalidation remains reserved for withdrawal/session/send safety paths.
    /// </summary>
    internal static class BuyerSessionAgentRuntimeBridge
    {
        private sealed class WatchedSession
        {
            public string ShopKey { get; set; }
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class WatchedGeneration
        {
            public string ShopKey { get; set; }
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public long Generation { get; set; }
            public DateTime AcceptedAtUtc { get; set; }
        }

        private static readonly Lazy<BuyerSessionAgent> AgentHolder =
            new Lazy<BuyerSessionAgent>(() => new BuyerSessionAgent());
        private static readonly ConcurrentDictionary<QN, byte> Attached = new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<string, WatchedSession> WatchedSessions =
            new ConcurrentDictionary<string, WatchedSession>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, WatchedGeneration> WatchedGenerations =
            new ConcurrentDictionary<string, WatchedGeneration>(StringComparer.Ordinal);
        private const int DeadlineWatchdogSleepMilliseconds = 250;
        private static Timer _timer;
        private static Thread _deadlineWatchdogThread;
        private static int _started;

        private static BuyerSessionAgent Agent
        {
            get { return AgentHolder.Value; }
        }

        public static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new Timer(_ => AttachExisting(), null, 300, 700);
            _deadlineWatchdogThread = new Thread(GenerationDeadlineWatchdogLoop)
            {
                IsBackground = true,
                Name = "QnBot.GenerationDeadlineWatchdog"
            };
            _deadlineWatchdogThread.Start();
            Log.Info("BuyerSessionAgent统一事件桥已启动：原始买家/卖家/订单/撤回/系统消息进入同一seller+buyer时间线；人工回复仅记录用于学习；generation绝对年龄看门狗="
                + BuyerSessionAgent.AbsoluteGenerationAgeSeconds
                + "s（从本次generation实际接受时起计时，AI预算后仅保留发送/状态收尾余量）。");
        }

        internal static void RegisterAcceptedGeneration(
            string shopKey,
            string seller,
            string buyer,
            long generation,
            DateTime acceptedAtUtc)
        {
            shopKey = Normalize(shopKey);
            seller = Normalize(seller);
            buyer = Normalize(buyer);
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || generation <= 0) return;
            if (string.Equals(seller, buyer, StringComparison.Ordinal)) return;
            if (acceptedAtUtc == default(DateTime)) acceptedAtUtc = DateTime.UtcNow;
            if (acceptedAtUtc.Kind != DateTimeKind.Utc) acceptedAtUtc = acceptedAtUtc.ToUniversalTime();

            WatchSession(shopKey, seller, buyer);
            var watchKey = BuildGenerationWatchKey(shopKey, seller, buyer, generation);
            WatchedGenerations.AddOrUpdate(
                watchKey,
                _ => new WatchedGeneration
                {
                    ShopKey = shopKey,
                    Seller = seller,
                    Buyer = buyer,
                    Generation = generation,
                    AcceptedAtUtc = acceptedAtUtc
                },
                (_, existing) =>
                {
                    // Replace the value instead of mutating it in place. A concurrent sweep
                    // can then remove only the exact snapshot it inspected, never a refreshed watch.
                    var preservedAcceptedAt = existing == null
                        || existing.AcceptedAtUtc == default(DateTime)
                        || acceptedAtUtc < existing.AcceptedAtUtc
                        ? acceptedAtUtc
                        : existing.AcceptedAtUtc;
                    return new WatchedGeneration
                    {
                        ShopKey = shopKey,
                        Seller = seller,
                        Buyer = buyer,
                        Generation = generation,
                        AcceptedAtUtc = preservedAcceptedAt
                    };
                });
        }

        private static void GenerationDeadlineWatchdogLoop()
        {
            while (true)
            {
                try
                {
                    SweepGenerationDeadlines();
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("generation绝对年龄看门狗扫描失败: " + ex.Message, 20);
                }
                Thread.Sleep(DeadlineWatchdogSleepMilliseconds);
            }
        }

        private static void SweepGenerationDeadlines()
        {
            var now = DateTime.UtcNow;

            // Every generation is registered synchronously by BuyerSessionAgent at the instant the
            // actionable buyer message is accepted. The deadline therefore no longer depends on a
            // later raw-event observation, the bounded RecentEvents ring, or a transient state sample.
            // This is critical for recovered messages whose source timestamp may be minutes old.
            foreach (var pair in WatchedGenerations.ToArray())
            {
                var watched = pair.Value;
                if (watched == null)
                {
                    RemoveExact(WatchedGenerations, pair);
                    continue;
                }

                BuyerSessionAgentState state;
                if (!Agent.TryGetGenerationState(watched.Seller, watched.Buyer, watched.Generation, out state, watched.ShopKey)
                    || state == BuyerSessionAgentState.Completed
                    || state == BuyerSessionAgentState.Cancelled
                    || state == BuyerSessionAgentState.Failed)
                {
                    RemoveExact(WatchedGenerations, pair);
                    continue;
                }

                var acceptedAtUtc = watched.AcceptedAtUtc;
                DateTime authoritativeAcceptedAtUtc;
                if (Agent.TryGetGenerationAcceptedAtUtc(
                    watched.Seller,
                    watched.Buyer,
                    watched.Generation,
                    out authoritativeAcceptedAtUtc,
                    watched.ShopKey)
                    && authoritativeAcceptedAtUtc != default(DateTime))
                {
                    acceptedAtUtc = authoritativeAcceptedAtUtc;
                }

                var elapsed = now - acceptedAtUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed.TotalSeconds <= BuyerSessionAgent.AbsoluteGenerationAgeSeconds) continue;

                Agent.Cancel(
                    watched.Seller,
                    watched.Buyer,
                    watched.Generation,
                    "absolute_generation_age_exceeded",
                    watched.ShopKey);
                RemoveExact(WatchedGenerations, pair);
                Log.ErrorWithMaxCount(
                    "generation超过绝对年龄已由独立线程硬取消，禁止迟到结果进入Ready/Sending: seller="
                    + watched.Seller + ", buyer=" + watched.Buyer
                    + ", generation=" + watched.Generation
                    + ", elapsedMs=" + (long)elapsed.TotalMilliseconds
                    + ", limitSeconds=" + BuyerSessionAgent.AbsoluteGenerationAgeSeconds,
                    100);
            }

            foreach (var pair in WatchedSessions.ToArray())
            {
                var watched = pair.Value;
                if (watched != null && now - watched.LastSeenUtc <= TimeSpan.FromHours(6)) continue;
                RemoveExact(WatchedSessions, pair);
            }
        }

        private static bool RemoveExact<TValue>(
            ConcurrentDictionary<string, TValue> dictionary,
            KeyValuePair<string, TValue> snapshot)
        {
            return ((ICollection<KeyValuePair<string, TValue>>)dictionary).Remove(snapshot);
        }

        private static void WatchSession(string shopKey, string seller, string buyer)
        {
            shopKey = Normalize(shopKey);
            seller = Normalize(seller);
            buyer = Normalize(buyer);
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0
                || string.Equals(seller, buyer, StringComparison.Ordinal)) return;
            var key = shopKey + "#" + seller + "#" + buyer;
            var now = DateTime.UtcNow;
            WatchedSessions.AddOrUpdate(
                key,
                _ => new WatchedSession { ShopKey = shopKey, Seller = seller, Buyer = buyer, LastSeenUtc = now },
                (_, existing) => new WatchedSession
                {
                    ShopKey = shopKey,
                    Seller = seller,
                    Buyer = buyer,
                    LastSeenUtc = now
                });
        }

        private static string BuildGenerationWatchKey(string shopKey, string seller, string buyer, long generation)
        {
            return Normalize(shopKey) + "#" + Normalize(seller) + "#" + Normalize(buyer) + "#" + generation;
        }

        private static string ResolveShopKey(QN qn)
        {
            if (qn == null || qn.Seller == null) return string.Empty;
            // The event source is an exact QN object; never substitute ambient scope or
            // seller nickname here, because two stores may legitimately share that nickname.
            try { return Normalize(ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey); }
            catch { return string.Empty; }
        }

        private static void AttachExisting()
        {
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
            catch { return; }

            foreach (var qn in qns)
            {
                if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                try
                {
                    qn.EvRecieveNewMessage += (sender, e) =>
                    {
                        if (e != null) ObservePayload(qn, e.Message, "foreground");
                    };
                    qn.EvShopRobotReceriveNewMessage += (sender, e) =>
                    {
                        if (e == null || e.Seller == null || e.Buyer == null) return;
                        string nonBuyerReason;
                        if (NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
                        {
                            Log.Info("BuyerSessionAgent忽略非买家后台通知: reason=" + nonBuyerReason);
                            return;
                        }
                        var shopKey = ResolveShopKey(qn);
                        if (shopKey.Length == 0)
                        {
                            Log.ErrorWithMaxCount("BuyerSessionAgent后台通知缺少唯一ShopKey，已跳过以避免跨店状态污染。", 20);
                            return;
                        }
                        WatchSession(shopKey, e.Seller.Nick, e.Buyer.Nick);
                        var now = DateTime.Now;
                        Agent.RecordEvent(
                            e.Seller.Nick,
                            e.Buyer.Nick,
                            BuyerSessionEventKind.BuyerSystem,
                            string.Empty,
                            0,
                            now,
                            now,
                            "background:new_message_signal",
                            false,
                            shopKey);
                    };
                    Log.Info("BuyerSessionAgent已挂载客服实例原始消息流: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
                catch (Exception ex)
                {
                    byte ignored;
                    Attached.TryRemove(qn, out ignored);
                    Log.ErrorWithMaxCount("BuyerSessionAgent挂载消息流失败: " + ex.Message, 10);
                }
            }
        }

        private static void ObservePayload(QN qn, string payload, string source)
        {
            if (qn == null || string.IsNullOrWhiteSpace(payload)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(payload);
                if (response == null || response.result == null) return;
                foreach (var message in response.result.Where(x => x != null).OrderBy(IncomingMessageSafety.GetSortValue))
                {
                    ObserveMessage(qn, message, source);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("BuyerSessionAgent解析原始消息事件失败: " + ex.Message, 20);
            }
        }

        private static void ObserveMessage(QN qn, QNChatMessage message, string source)
        {
            if (qn == null || message == null || qn.Seller == null) return;
            var seller = Normalize(qn.Seller.Nick);
            var from = Normalize(message.fromid == null ? string.Empty : message.fromid.nick);
            var to = Normalize(message.toid == null ? string.Empty : message.toid.nick);
            if (seller.Length == 0 || from.Length == 0 || to.Length == 0) return;

            var sellerMessage = string.Equals(from, seller, StringComparison.Ordinal);
            if (!IsCurrentSellerIdentity(qn, message, sellerMessage))
            {
                Log.ErrorWithMaxCount("BuyerSessionAgent拒绝与当前千牛店铺身份不匹配的原始消息。", 20);
                return;
            }
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (!sellerMessage && NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("BuyerSessionAgent忽略非买家原始消息，禁止污染学习时间线: reason=" + nonBuyerReason);
                return;
            }
            var buyer = sellerMessage ? to : from;
            if (buyer.Length == 0 || string.Equals(buyer, seller, StringComparison.Ordinal)) return;
            var shopKey = ResolveShopKey(qn);
            if (shopKey.Length == 0)
            {
                Log.ErrorWithMaxCount("BuyerSessionAgent原始消息缺少唯一ShopKey，已跳过以避免跨店generation污染。", 20);
                return;
            }
            WatchSession(shopKey, seller, buyer);

            var display = IncomingMessageSafety.GetDisplayText(message, text);
            var key = IncomingMessageSafety.BuildMessageKey(message, text);
            var sort = IncomingMessageSafety.GetSortValue(message);
            DateTime sourceTime;
            if (!OrderCardParser.TryGetMessageTime(message, out sourceTime)) sourceTime = DateTime.Now;
            var now = DateTime.Now;

            if (sellerMessage)
            {
                if (ConversationContextStore.IsWithdrawalNotice(message, text))
                {
                    Agent.RecordEvent(seller, buyer, BuyerSessionEventKind.SellerWithdrawal, key, sort,
                        sourceTime, now, source + ":seller_withdrawal", false, shopKey);
                    return;
                }

                // SendDeliveryWatchdog is the single outbound-authorship authority. Do not infer
                // Bot-vs-human from QNRpa.LastSetPlainText here: segmented sends and delayed/replayed
                // seller echoes can arrive after that mutable field already points at another segment.
                // The delivery ledger knows the exact seller+buyer+answer that the Bot submitted and
                // keeps a short known-answer record after confirmation for duplicate/recovered echoes.
                var botEcho = SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text, shopKey);
                // A successful transport echo must be represented in the UI timeline
                // immediately, including first-contact and fixed order segments.
                // Historical refresh is a fallback, not the only writer. Keep the
                // original message id so duplicate events/history cannot double-add.
                ConversationContextStore.RecordObservedSellerMessage(
                    seller, buyer, message, text, shopKey);
                var result = Agent.RecordEvent(
                    seller,
                    buyer,
                    botEcho ? BuyerSessionEventKind.SellerBotEcho : BuyerSessionEventKind.SellerHumanReply,
                    key,
                    sort,
                    sourceTime,
                    now,
                    source + (botEcho ? ":bot_echo" : ":human_reply"),
                    false,
                    shopKey);

                if (!botEcho)
                {
                    Log.Info("BuyerSessionAgent记录人工客服回复作为学习证据，未取消Bot generation: seller="
                        + seller + ", buyer=" + buyer + ", stale=" + result.StaleAgainstLatestBuyer
                        + ", sort=" + sort);
                }
                return;
            }

            OrderSnapshot order;
            if (OrderCardParser.TryParse(message, text, seller, buyer, "BuyerSessionAgent:" + source, out order))
            {
                Agent.RecordEvent(seller, buyer, MapOrderKind(order.EventType), key, sort,
                    order.EventTime, now, source + ":order:" + order.EventType, false, shopKey);
                return;
            }

            var kind = ClassifyBuyerEvent(message, text, display);
            Agent.RecordEvent(seller, buyer, kind, key, sort, sourceTime, now, source + ":raw_buyer_event", false, shopKey);
        }

        private static bool IsCurrentSellerIdentity(QN qn, QNChatMessage message, bool sellerMessage)
        {
            if (qn == null || qn.Seller == null || message == null) return false;
            var expectedNick = (qn.Seller.Nick ?? string.Empty).Trim();
            if (expectedNick.Length == 0) return false;

            var endpointNick = sellerMessage
                ? (message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim())
                : (message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim());
            if (!string.Equals(endpointNick, expectedNick, StringComparison.Ordinal))
                return false;

            var loginNick = message.loginid == null ? string.Empty
                : (message.loginid.nick ?? string.Empty).Trim();
            if (loginNick.Length > 0
                && !string.Equals(loginNick, expectedNick, StringComparison.Ordinal))
                return false;

            var expectedTargetId = (qn.Seller.TargetId ?? string.Empty).Trim();
            if (expectedTargetId.Length == 0) return true;

            var endpointTargetId = sellerMessage
                ? (message.fromid == null ? string.Empty : (message.fromid.targetId ?? string.Empty).Trim())
                : (message.toid == null ? string.Empty : (message.toid.targetId ?? string.Empty).Trim());
            if (endpointTargetId.Length > 0
                && !string.Equals(endpointTargetId, expectedTargetId, StringComparison.Ordinal))
                return false;

            var loginTargetId = message.loginid == null ? string.Empty
                : (message.loginid.targetId ?? string.Empty).Trim();
            if (loginTargetId.Length > 0
                && !string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal))
                return false;

            return true;
        }

        private static BuyerSessionEventKind ClassifyBuyerEvent(QNChatMessage message, string text, string display)
        {
            if (ConversationContextStore.IsWithdrawalNotice(message, text)) return BuyerSessionEventKind.BuyerWithdrawal;
            if (ConversationContextStore.IsPlatformSystemTip(message, text)) return BuyerSessionEventKind.BuyerSystem;
            if (ConversationContextStore.IsProductLink(message, text)) return BuyerSessionEventKind.BuyerProductCard;
            if (string.Equals(display, "[图片]", StringComparison.Ordinal)) return BuyerSessionEventKind.BuyerImage;
            if (IncomingMessageSafety.IsMediaPlaceholder(display)) return BuyerSessionEventKind.BuyerMedia;
            return BuyerSessionEventKind.BuyerText;
        }

        private static BuyerSessionEventKind MapOrderKind(OrderEventType eventType)
        {
            if (eventType == OrderEventType.Paid) return BuyerSessionEventKind.OrderPaid;
            if (eventType == OrderEventType.Closed) return BuyerSessionEventKind.OrderClosed;
            if (eventType == OrderEventType.RefundRequested) return BuyerSessionEventKind.OrderRefund;
            return BuyerSessionEventKind.OrderCreated;
        }

        private static string GetMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                    {
                        text += message.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (message.summary ?? string.Empty).Trim();
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}