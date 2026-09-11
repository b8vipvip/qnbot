using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Feeds raw QN/CDP events into the shared BuyerSessionAgent before the independent reply/order
    /// pipelines process them. This gives one ordered seller+buyer timeline for text, image, product,
    /// order, withdrawal, system and seller reply events without replacing the existing stable send
    /// pipeline. Human seller replies are observational learning evidence and never cancel Bot work;
    /// explicit hard invalidation remains reserved for withdrawal/session/send safety paths.
    /// </summary>
    internal static class BuyerSessionAgentRuntimeBridge
    {
        private sealed class WatchedSession
        {
            public string Seller { get; set; }
            public string Buyer { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class WatchedGeneration
        {
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
            Log.Info("BuyerSessionAgent统一事件桥已启动：原始买家/卖家/订单/撤回/系统消息进入同一seller+buyer时间线；人工回复仅记录用于学习；generation绝对年龄看门狗=55s（从本次generation实际接受时起计时）。");
        }

        internal static void RegisterAcceptedGeneration(
            string seller,
            string buyer,
            long generation,
            DateTime acceptedAtUtc)
        {
            seller = Normalize(seller);
            buyer = Normalize(buyer);
            if (seller.Length == 0 || buyer.Length == 0 || generation <= 0) return;
            if (string.Equals(seller, buyer, StringComparison.Ordinal)) return;
            if (acceptedAtUtc == default(DateTime)) acceptedAtUtc = DateTime.UtcNow;
            if (acceptedAtUtc.Kind != DateTimeKind.Utc) acceptedAtUtc = acceptedAtUtc.ToUniversalTime();

            WatchSession(seller, buyer);
            var watchKey = BuildGenerationWatchKey(seller, buyer, generation);
            WatchedGenerations.AddOrUpdate(
                watchKey,
                _ => new WatchedGeneration
                {
                    Seller = seller,
                    Buyer = buyer,
                    Generation = generation,
                    AcceptedAtUtc = acceptedAtUtc
                },
                (_, existing) =>
                {
                    existing = existing ?? new WatchedGeneration();
                    existing.Seller = seller;
                    existing.Buyer = buyer;
                    existing.Generation = generation;
                    // Never replace a real acceptance time with a later discovery/recovery time.
                    if (existing.AcceptedAtUtc == default(DateTime)
                        || acceptedAtUtc < existing.AcceptedAtUtc)
                    {
                        existing.AcceptedAtUtc = acceptedAtUtc;
                    }
                    return existing;
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
                    WatchedGeneration ignored;
                    WatchedGenerations.TryRemove(pair.Key, out ignored);
                    continue;
                }

                BuyerSessionAgentState state;
                if (!Agent.TryGetGenerationState(watched.Seller, watched.Buyer, watched.Generation, out state)
                    || state == BuyerSessionAgentState.Completed
                    || state == BuyerSessionAgentState.Cancelled
                    || state == BuyerSessionAgentState.Failed)
                {
                    WatchedGeneration ignored;
                    WatchedGenerations.TryRemove(pair.Key, out ignored);
                    continue;
                }

                DateTime authoritativeAcceptedAtUtc;
                if (Agent.TryGetGenerationAcceptedAtUtc(
                    watched.Seller,
                    watched.Buyer,
                    watched.Generation,
                    out authoritativeAcceptedAtUtc)
                    && authoritativeAcceptedAtUtc != default(DateTime))
                {
                    watched.AcceptedAtUtc = authoritativeAcceptedAtUtc;
                }

                var elapsed = now - watched.AcceptedAtUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed.TotalSeconds <= BuyerSessionAgent.AbsoluteGenerationAgeSeconds) continue;

                Agent.Cancel(
                    watched.Seller,
                    watched.Buyer,
                    watched.Generation,
                    "absolute_generation_age_exceeded");
                WatchedGeneration removed;
                WatchedGenerations.TryRemove(pair.Key, out removed);
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
                WatchedSession removed;
                WatchedSessions.TryRemove(pair.Key, out removed);
            }
        }

        private static void WatchSession(string seller, string buyer)
        {
            seller = Normalize(seller);
            buyer = Normalize(buyer);
            if (seller.Length == 0 || buyer.Length == 0 || string.Equals(seller, buyer, StringComparison.Ordinal)) return;
            var key = seller + "#" + buyer;
            var now = DateTime.UtcNow;
            WatchedSessions.AddOrUpdate(
                key,
                _ => new WatchedSession { Seller = seller, Buyer = buyer, LastSeenUtc = now },
                (_, existing) =>
                {
                    existing = existing ?? new WatchedSession();
                    existing.Seller = seller;
                    existing.Buyer = buyer;
                    existing.LastSeenUtc = now;
                    return existing;
                });
        }

        private static string BuildGenerationWatchKey(string seller, string buyer, long generation)
        {
            return Normalize(seller) + "#" + Normalize(buyer) + "#" + generation;
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
                        WatchSession(e.Seller.Nick, e.Buyer.Nick);
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
                            false);
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
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (!sellerMessage && NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("BuyerSessionAgent忽略非买家原始消息，禁止污染学习时间线: reason=" + nonBuyerReason);
                return;
            }
            var buyer = sellerMessage ? to : from;
            if (buyer.Length == 0 || string.Equals(buyer, seller, StringComparison.Ordinal)) return;
            WatchSession(seller, buyer);

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
                        sourceTime, now, source + ":seller_withdrawal", false);
                    return;
                }

                // SendDeliveryWatchdog is the single outbound-authorship authority. Do not infer
                // Bot-vs-human from QNRpa.LastSetPlainText here: segmented sends and delayed/replayed
                // seller echoes can arrive after that mutable field already points at another segment.
                // The delivery ledger knows the exact seller+buyer+answer that the Bot submitted and
                // keeps a short known-answer record after confirmation for duplicate/recovered echoes.
                var botEcho = SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text);
                var result = Agent.RecordEvent(
                    seller,
                    buyer,
                    botEcho ? BuyerSessionEventKind.SellerBotEcho : BuyerSessionEventKind.SellerHumanReply,
                    key,
                    sort,
                    sourceTime,
                    now,
                    source + (botEcho ? ":bot_echo" : ":human_reply"),
                    false);

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
                    order.EventTime, now, source + ":order:" + order.EventType, false);
                return;
            }

            var kind = ClassifyBuyerEvent(message, text, display);
            Agent.RecordEvent(seller, buyer, kind, key, sort, sourceTime, now, source + ":raw_buyer_event", false);
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