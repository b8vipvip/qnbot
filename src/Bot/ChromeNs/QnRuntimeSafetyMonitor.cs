using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class QnRuntimeSafetyMonitor
    {
        private const int ConversationProbeIntervalMilliseconds = 2500;
        private const int HeartbeatIntervalSeconds = 60;

        private static readonly ConcurrentDictionary<QN, byte> Subscribed =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, byte> VersionGuardLogged =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, DateTime> NextConversationProbeAt =
            new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, byte> ConversationProbeRunning =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, int> ConsecutiveProbeFailures =
            new ConcurrentDictionary<QN, int>();
        private static readonly ConcurrentDictionary<string, long> LatestBuyerSourceSort =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private static Timer _timer;
        private static int _started;
        private static long _lastHeartbeatUtcTicks;
        private static int _lastSampledGen2Collections;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            BuyerIdentityAliasUiBridge.Start();
            OrderNotificationTraceBridge.Start();
            _timer = new Timer(_ => Refresh(), null, 0, 500);
            Log.Info("千牛发送、当前买家与人工回复观察安全监控已启动：人工回复只作为学习证据，不取消Bot任务。" );
        }

        private static void Refresh()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    NormalizeUnknownVersion(qn);
                    if (Subscribed.TryAdd(qn, 0))
                    {
                        qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
                    }
                    ScheduleConversationProbe(qn);
                }
                WriteHeartbeatIfDue();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新千牛运行时安全监控失败：" + ex.Message, 5);
            }
        }

        private static void ScheduleConversationProbe(QN qn)
        {
            if (qn == null || qn.CDP == null || qn.Seller == null
                || string.IsNullOrWhiteSpace(qn.Seller.Nick))
            {
                return;
            }

            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (!HasVerifiedReceptionDesk(seller))
            {
                RecordNoActiveChat(qn, seller, "未检测到已验证的千牛接待聊天窗口，暂停当前买家探测");
                return;
            }

            var now = DateTime.UtcNow;
            DateTime next;
            if (NextConversationProbeAt.TryGetValue(qn, out next) && next > now) return;
            NextConversationProbeAt[qn] = now.AddMilliseconds(ConversationProbeIntervalMilliseconds);
            if (!ConversationProbeRunning.TryAdd(qn, 0)) return;

            Task.Run(async () =>
            {
                try
                {
                    await ProbeCurrentConversationAsync(qn);
                }
                catch (Exception ex)
                {
                    RecordProbeFailure(qn, ex.Message);
                }
                finally
                {
                    byte ignored;
                    ConversationProbeRunning.TryRemove(qn, out ignored);
                }
            });
        }

        private static async Task ProbeCurrentConversationAsync(QN qn)
        {
            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || qn.CDP == null) return;
            if (!HasVerifiedReceptionDesk(seller))
            {
                RecordNoActiveChat(qn, seller, "接待聊天窗口已离开，暂停当前买家探测");
                return;
            }

            var first = await qn.GetCurrentConversationID();
            var firstNick = ReadConversationNick(first);
            if (firstNick.Length == 0)
            {
                RecordNoActiveChat(qn, seller, "接待窗口在线，但当前没有选中的买家会话");
                return;
            }

            var currentNick = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            if (RejectNonBuyerProbe(qn, seller, first, currentNick, "first_read")) return;
            if (AreSameBuyer(seller, currentNick, firstNick))
            {
                RecordProbeSuccess(qn, seller, firstNick, false);
                return;
            }

            await Task.Delay(220);
            var second = await qn.GetCurrentConversationID();
            var secondNick = ReadConversationNick(second);
            if (secondNick.Length == 0)
            {
                RecordNoActiveChat(qn, seller, "当前会话切换中，第二次探测暂时为空");
                return;
            }
            if (RejectNonBuyerProbe(qn, seller, second, currentNick, "stable_read")) return;
            if (!AreSameBuyer(seller, firstNick, secondNick))
            {
                RecordProbeFailure(qn, "当前会话连续两次读取不稳定: first=" + firstNick + ", second=" + secondNick);
                return;
            }

            var resolved = BuyerIdentityAliasService.ResolveInternalNick(seller, secondNick);
            qn.SetActiveConversationByNick(seller, resolved, "runtimeConversationProbe");
            Log.Info("当前买家由主动探测修正: seller=" + seller
                + ", previous=" + currentNick + ", current=" + resolved);
            RecordProbeSuccess(qn, seller, resolved, true);
        }

        private static bool RejectNonBuyerProbe(
            QN qn,
            string seller,
            ConversationResponse response,
            string cachedBuyer,
            string stage)
        {
            if (qn == null || response == null || response.Result == null) return false;
            string reason;
            if (!NonBuyerConversationGuard.ShouldBlockConversation(qn.Seller, response.Result, out reason)) return false;

            int failures;
            ConsecutiveProbeFailures.TryRemove(qn, out failures);
            BotConnectionDiagnostics.RecordCdpStatus(
                true,
                "当前选中的是非买家会话，保持已验证买家不变",
                seller,
                cachedBuyer);
            Log.Info("当前买家主动探测识别到非买家会话，保持已验证buyer不变: seller="
                + seller + ", cachedBuyer=" + (cachedBuyer ?? string.Empty)
                + ", stage=" + stage + ", reason=" + reason);
            return true;
        }

        private static bool HasVerifiedReceptionDesk(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return false;
            try
            {
                var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
                return desk != null && desk.IsAlive;
            }
            catch
            {
                return false;
            }
        }

        private static void RecordNoActiveChat(QN qn, string seller, string reason)
        {
            int failures;
            ConsecutiveProbeFailures.TryRemove(qn, out failures);
            var buyer = qn == null || qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            BotConnectionDiagnostics.RecordCdpStatus(true,
                reason ?? "当前没有活动买家会话",
                seller,
                buyer);
            if (failures > 0)
            {
                Log.Info("当前买家主动探测故障计数已清除：当前没有需要探测的活动聊天会话。seller="
                    + seller + ", previousFailures=" + failures);
            }
        }

        private static string ReadConversationNick(ConversationResponse response)
        {
            return response == null || response.Result == null
                ? string.Empty
                : (response.Result.Nick ?? string.Empty).Trim();
        }

        private static bool AreSameBuyer(string seller, string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (left.Length == 0 || right.Length == 0) return false;
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            return BuyerIdentityAliasService.AreEquivalent(seller, left, right);
        }

        private static void RecordProbeSuccess(QN qn, string seller, string buyer, bool corrected)
        {
            int failures;
            ConsecutiveProbeFailures.TryRemove(qn, out failures);
            BotConnectionDiagnostics.RecordCdpStatus(true,
                corrected ? "主动探测已修正当前买家" : "当前买家主动探测正常",
                seller,
                buyer);
            BotConnectionDiagnostics.RecordBuyerSeller(seller, buyer);
            if (failures > 0)
            {
                Log.Info("当前买家主动探测已恢复: seller=" + seller
                    + ", buyer=" + buyer + ", previousFailures=" + failures);
            }
        }

        private static void RecordProbeFailure(QN qn, string reason)
        {
            var failures = ConsecutiveProbeFailures.AddOrUpdate(qn, 1, (_, count) => count + 1);
            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = qn == null || qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            if (failures >= 2)
            {
                BotConnectionDiagnostics.RecordCdpStatus(false,
                    "当前买家主动探测连续失败" + failures + "次，等待注入/CDP恢复",
                    seller,
                    buyer);
            }
            if (failures == 1 || failures == 3 || failures % 10 == 0)
            {
                Log.Error("当前买家主动探测失败: seller=" + seller
                    + ", cachedBuyer=" + buyer + ", failures=" + failures
                    + ", reason=" + (reason ?? string.Empty));
            }
        }

        private static void WriteHeartbeatIfDue()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastHeartbeatUtcTicks);
            if (previous != 0
                && new TimeSpan(nowTicks - previous).TotalSeconds < HeartbeatIntervalSeconds)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _lastHeartbeatUtcTicks, nowTicks, previous) != previous) return;

            try
            {
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                var age = snapshot == null || snapshot.LastUpdateTime == DateTime.MinValue
                    ? -1
                    : Math.Max(0, (long)(DateTime.Now - snapshot.LastUpdateTime).TotalSeconds);
                Log.Info("Bot运行心跳: ws=" + (snapshot == null ? string.Empty : snapshot.WebSocketStatus)
                    + ", injection=" + (snapshot == null ? string.Empty : snapshot.InjectionStatus)
                    + ", qn=" + (snapshot == null ? string.Empty : snapshot.QnParamStatus)
                    + ", seller=" + (snapshot == null ? string.Empty : snapshot.Seller)
                    + ", buyer=" + (snapshot == null ? string.Empty : snapshot.Buyer)
                    + ", diagnosticsAgeSeconds=" + age);
                // Low-frequency, privacy-free counters allow comparing idle memory
                // growth with active QN subscriptions without forcing a GC cycle.
                try
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        process.Refresh();
                        var gen2Collections = GC.CollectionCount(2);
                        var previousGen2 = Interlocked.Exchange(
                            ref _lastSampledGen2Collections, gen2Collections);
                        Log.Info("Bot资源采样: workingSetMiB=" + (process.WorkingSet64 / 1048576)
                            + ", privateMiB=" + (process.PrivateMemorySize64 / 1048576)
                            + ", managedHeapMiB=" + (GC.GetTotalMemory(false) / 1048576)
                            + ", gen0Collections=" + GC.CollectionCount(0)
                            + ", gen1Collections=" + GC.CollectionCount(1)
                            + ", gen2Collections=" + gen2Collections
                            + ", gen2SinceLastSample=" + Math.Max(0, gen2Collections - previousGen2)
                            + ", handles=" + process.HandleCount
                            + ", threads=" + process.Threads.Count
                            + ", sendGateHeldSeconds=" + QN.GetRuntimeSafetySnapshot()
                                .Where(q => q != null).Select(q => q.CurrentSendGateHeldSeconds)
                                .DefaultIfEmpty(0L).Max()
                            + ", pendingCancelledAi=" + BuyerStreamingReplyPipeline.UnfinishedAnswerTasksAfterCancel
                            + ", cancelledAiTasksTotal=" + BuyerStreamingReplyPipeline.AnswerTasksOutlivedCancellation
                            + ", monitoredQn=" + Subscribed.Count
                            + ", activeWs=" + (snapshot == null ? 0 : snapshot.WebSocketSessionCount)
                            + ", timelineStates=" + ConversationContextStore.CachedTimelineCount
                            + ", workingMemory=" + Bot.Knowledge.KnowledgeEngineV2Service.CachedWorkingMemoryCount
                            + ", imageRecords=" + VisionImageCacheService.CachedRecordCount
                            + ", wsGuardSessionRefs=" + WebSocketPageLifecycleGuard.TrackedSessionCount
                            + ", shopRegistryWrites=" + Bot.ShopScope.ShopProfileStore.RegistryWriteCount
                            + ", shopSnapshotHits=" + BotWebConsoleSyncService.ShopSnapshotCacheHits
                            + ", shopSnapshotRefreshes=" + BotWebConsoleSyncService.ShopSnapshotRefreshes
                            + ", aliasUiNoopSkips=" + BuyerIdentityAliasUiBridge.UnchangedAliasMergeSkips);
                    }
                }
                catch (Exception metricsError)
                {
                    Log.ErrorWithMaxCount("采集Bot资源计数器失败：" + metricsError.GetType().Name, 5);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Bot运行心跳写入失败: " + ex.Message);
            }
        }

        private static void NormalizeUnknownVersion(QN qn)
        {
            var value = (qn.QnVersion ?? string.Empty).Trim();
            Version parsed;
            var normalized = value.TrimEnd('N', 'n');
            if (!string.IsNullOrWhiteSpace(value) && Version.TryParse(normalized, out parsed)) return;

            qn.QnVersion = "999.999.999N";
            if (VersionGuardLogged.TryAdd(qn, 0))
            {
                Log.Info("千牛版本为空或无法解析，已强制启用可靠RPA发送链路，禁止误走SendSmartTipMsg。original=" + value);
            }
        }

        private static void Qn_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                if (seller.Length == 0) return;

                foreach (var message in response.result.Where(x => x != null))
                {
                    if (message.fromid == null || message.toid == null) continue;
                    BuyerIdentityAliasService.ObserveMessage(seller, message);

                    var from = (message.fromid.nick ?? string.Empty).Trim();
                    var to = (message.toid.nick ?? string.Empty).Trim();

                    if (!string.Equals(from, seller, StringComparison.Ordinal))
                    {
                        if (string.Equals(to, seller, StringComparison.Ordinal) && from.Length > 0)
                        {
                            RecordBuyerSourceSort(seller, from, message);
                        }
                        continue;
                    }

                    var buyer = to;
                    if (buyer.Length == 0) continue;

                    var texts = ExtractMessageTextCandidates(message);
                    if (texts.Length == 0) continue;
                    var text = SelectPreferredMessageText(texts);

                    if (TryConfirmBotDelivery(seller, buyer, texts))
                    {
                        continue;
                    }

                    var botAuthoredText = texts.FirstOrDefault(IsExplicitBotAuthoredReply);
                    if (!string.IsNullOrWhiteSpace(botAuthoredText))
                    {
                        ResponseProgressTracker.MarkDeliveryConfirmed(
                            seller,
                            buyer,
                            botAuthoredText,
                            "通过卖家消息多字段中的[AI]署名确认这是Bot回显");
                        Log.Info("卖家消息多字段命中Bot署名，未判定人工回复: seller=" + seller
                            + ", buyer=" + buyer
                            + ", variants=" + texts.Length
                            + ", reply=" + Short(botAuthoredText, 120));
                        continue;
                    }

                    if (IsSellerReplyOlderThanLatestBuyerTurn(seller, buyer, message))
                    {
                        Log.Info("延迟到达的客服旧回复早于买家最新一轮消息，仅作为历史学习证据: seller="
                            + seller + ", buyer=" + buyer + ", reply=" + Short(text, 120));
                        continue;
                    }

                    // Product decision: a human reply no longer takes ownership away from the Bot.
                    // Keep the Bot generation/send path alive and record the human answer so the
                    // learning pipeline can compare both answers and decide whether knowledge needs
                    // reinforcement, correction or no change.
                    ResponseProgressTracker.MarkManualIntervention(seller, buyer, text);
                    Log.Info("人工客服回复已记录为对比学习证据，Bot任务继续: seller=" + seller
                        + ", buyer=" + buyer + ", reply=" + Short(text, 120));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("分析卖家消息以判断人工回复来源失败：" + ex.Message, 10);
            }
        }

        private static void RecordBuyerSourceSort(string seller, string buyer, QNChatMessage message)
        {
            var texts = ExtractMessageTextCandidates(message);
            var text = texts.Length == 0 ? string.Empty : SelectPreferredMessageText(texts);
            if (ConversationContextStore.IsPlatformSystemTip(message, text)
                || ConversationContextStore.IsWithdrawalNotice(message, text))
            {
                return;
            }

            var sort = IncomingMessageSafety.GetSortValue(message);
            if (sort <= 0) return;
            var key = BuyerOrderKey(seller, buyer);
            LatestBuyerSourceSort.AddOrUpdate(key, sort, (_, previous) => Math.Max(previous, sort));
        }

        private static bool IsSellerReplyOlderThanLatestBuyerTurn(
            string seller,
            string buyer,
            QNChatMessage sellerMessage)
        {
            var sellerSort = IncomingMessageSafety.GetSortValue(sellerMessage);
            if (sellerSort <= 0) return false;

            long buyerSort;
            if (!LatestBuyerSourceSort.TryGetValue(BuyerOrderKey(seller, buyer), out buyerSort)
                || buyerSort <= 0)
            {
                return false;
            }
            return buyerSort > sellerSort;
        }

        private static string BuyerOrderKey(string seller, string buyer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            return seller.ToLowerInvariant() + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string[] ExtractMessageTextCandidates(QNChatMessage message)
        {
            var originalText = string.Empty;
            try
            {
                if (message != null && message.originalData != null)
                {
                    originalText = (message.originalData.text ?? string.Empty).Trim();
                }
            }
            catch
            {
            }

            var summaryText = message == null
                ? string.Empty
                : (message.summary ?? string.Empty).Trim();

            return new[] { originalText, summaryText }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string SelectPreferredMessageText(string[] texts)
        {
            if (texts == null || texts.Length == 0) return string.Empty;
            return texts.FirstOrDefault(IsExplicitBotAuthoredReply)
                ?? texts.FirstOrDefault()
                ?? string.Empty;
        }

        private static bool TryConfirmBotDelivery(string seller, string buyer, string[] texts)
        {
            if (texts == null) return false;

            foreach (var candidate in texts)
            {
                if (SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, candidate)) return true;
            }
            foreach (var candidate in texts)
            {
                if (SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, candidate)) return true;
            }
            return false;
        }

        private static bool IsExplicitBotAuthoredReply(string value)
        {
            var compact = new string((value ?? string.Empty)
                .Where(ch => !char.IsWhiteSpace(ch))
                .ToArray());
            return compact.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}