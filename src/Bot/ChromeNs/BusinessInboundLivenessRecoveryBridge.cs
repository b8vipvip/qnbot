using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using DbEntity;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _businessInboundLivenessRecoveryBootstrap =
            ChromeNs.BusinessInboundLivenessRecoveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Repairs the failure mode where the Qianniu page/WebSocket still looks connected while
    /// imsdk business callbacks have silently stopped. Transport health is not business health:
    /// keep the inbound hook attached to the current imsdk object and independently reconcile the
    /// active conversation against remote history. Recovered messages always re-enter the normal
    /// ProcessIncomingMessageAsync path so the existing business-message ledger remains the single
    /// side-effect owner and prevents duplicate replies when a delayed live event arrives later.
    /// </summary>
    internal static class BusinessInboundLivenessRecoveryBridge
    {
        private static readonly TimeSpan HistoryProbeInterval = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan HookRepairInterval = TimeSpan.FromSeconds(30);
        private static readonly ConcurrentDictionary<QN, DateTime> NextHistoryProbeAt =
            new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, DateTime> NextHookRepairAt =
            new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, byte> Running =
            new ConcurrentDictionary<QN, byte>();
        private static readonly TimeSpan QnStateSweepInterval = TimeSpan.FromMinutes(1);
        private static long _lastQnStateSweepUtcTicks;

        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 1800, 1000);
                Log.Info("业务入站存活恢复已启动：独立监测 imsdk 业务钩子与当前会话远端历史；"
                    + "WebSocket在线不再等同于业务入站在线。historyProbeSeconds="
                    + (int)HistoryProbeInterval.TotalSeconds + ", hookRepairSeconds="
                    + (int)HookRepairInterval.TotalSeconds);
            }
            return new object();
        }

        private static void Tick()
        {
            try
            {
                var sessions = QN.GetRuntimeSafetySnapshot();
                foreach (var qn in sessions)
                {
                    if (qn == null) continue;
                    try
                    {
                        if (qn.CDP == null || qn.CDP.IsInvalidated || qn.CDP.IsShopIdentityRetired || qn.Seller == null
                            || string.IsNullOrWhiteSpace(qn.Seller.Nick))
                            continue;

                        var now = DateTime.UtcNow;
                        DateTime nextHistory;
                        DateTime nextHook;
                        var historyDue = !NextHistoryProbeAt.TryGetValue(qn, out nextHistory) || nextHistory <= now;
                        var hookDue = !NextHookRepairAt.TryGetValue(qn, out nextHook) || nextHook <= now;
                        if (!historyDue && !hookDue) continue;
                        if (!Running.TryAdd(qn, 0)) continue;

                        if (historyDue) NextHistoryProbeAt[qn] = now.Add(HistoryProbeInterval);
                        if (hookDue) NextHookRepairAt[qn] = now.Add(HookRepairInterval);

                        Task.Run(async () =>
                        {
                            try
                            {
                                if (hookDue)
                                    await qn.RepairBusinessInboundHookAsync().ConfigureAwait(false);
                                if (historyDue)
                                    await qn.ProbeActiveConversationForMissedInboundAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Log.ErrorWithMaxCount("业务入站存活恢复异常: " + ex.Message, 50);
                            }
                            finally
                            {
                                byte ignored;
                                Running.TryRemove(qn, out ignored);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过异常千牛会话的业务入站恢复调度并继续：" + ex.Message, 20);
                    }
                }
                try { CleanupRetiredQnState(sessions); }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("清理已退役千牛入站恢复状态失败：" + ex.Message, 10);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("业务入站存活恢复调度失败: " + ex.Message, 20);
            }
        }

        private static void CleanupRetiredQnState(IEnumerable<QN> sessions)
        {
            var now = DateTime.UtcNow;
            var previous = Interlocked.Read(ref _lastQnStateSweepUtcTicks);
            if (previous > 0 && now.Ticks - previous < QnStateSweepInterval.Ticks) return;
            if (Interlocked.CompareExchange(ref _lastQnStateSweepUtcTicks, now.Ticks, previous) != previous) return;
            var alive = new HashSet<QN>((sessions ?? Enumerable.Empty<QN>())
                .Where(x => x != null && x.CDP != null && !x.CDP.IsInvalidated && !x.CDP.IsShopIdentityRetired));
            foreach (var qn in NextHistoryProbeAt.Keys)
            {
                if (alive.Contains(qn)) continue;
                DateTime ignored;
                NextHistoryProbeAt.TryRemove(qn, out ignored);
            }
            foreach (var qn in NextHookRepairAt.Keys)
            {
                if (alive.Contains(qn)) continue;
                DateTime ignored;
                NextHookRepairAt.TryRemove(qn, out ignored);
            }
        }

    }

    public partial class QN
    {
        private static readonly TimeSpan BusinessInboundNormalLookback = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan BusinessInboundMaxCatchupLookback = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan BusinessInboundCatchupOverlap = TimeSpan.FromSeconds(8);
        private readonly SemaphoreSlim _businessInboundLivenessGate = new SemaphoreSlim(1, 1);
        private readonly IncomingMessageDeduplicator _businessInboundHistoryProbeLedger =
            new IncomingMessageDeduplicator(8000);
        private const string BusinessInboundCheckpointSettingKey = "BusinessInboundLastHistoryProbeUtcTicks";
        private readonly object _businessInboundCheckpointSync = new object();
        private string _businessInboundCheckpointShopKey = string.Empty;
        private bool _businessInboundCheckpointLoaded;
        private bool _businessInboundCheckpointRestored;
        private DateTime _lastBusinessInboundHookRepairLogAt = DateTime.MinValue;
        private DateTime _lastBusinessInboundHistoryProbeCompletedAt = DateTime.MinValue;
        private DateTime _lastBusinessInboundProbeGapLogAt = DateTime.MinValue;

        // This hook is deliberately independent from inject.js' original closure. Qianniu can
        // replace window.imsdk while keeping the page and WebSocket alive; the historical injected
        // flag then stayed true and installImsdkHook() never attached to the replacement object.
        // Re-evaluating this small guard makes the hook self-healing without stacking listeners on
        // the same imsdk/on function.
        private const string BusinessInboundHookRepairScript = @"(function(){
try {
  if (!window.imsdk || typeof window.imsdk.on !== 'function' || typeof window.imsdk.invoke !== 'function') return 'imsdk-unavailable';
  if (window.__qnbotBusinessLivenessImsdkTarget === window.imsdk &&
      window.__qnbotBusinessLivenessOnRef === window.imsdk.on &&
      window.__qnbotBusinessLivenessHookInstalled === true) return 'healthy';
  var target = window.imsdk;
  var onRef = target.on;
  var emit = function(type, value) {
    try {
      var ws = window.chatWebsocket;
      if (!ws || ws.readyState !== 1) return;
      var response = typeof value === 'string' ? value : JSON.stringify(value);
      ws.send(JSON.stringify({ type: type, response: response || '' }));
    } catch (e) {}
  };
  var handler = function(cids) {
    try {
      (cids || []).forEach(function(cid) {
        if (!cid || !cid.ccode) return;
        try {
          Promise.resolve(target.invoke('im.singlemsg.GetNewMsg', { ccode: cid.ccode })).then(function(res) {
            if (res) emit('receiveNewMsg', res);
          }, function() {});
        } catch (e) {}
      });
    } catch (e) {}
  };
  target.on(['im.singlemsg.onReceiveNewMsg'], handler);
  window.__qnbotBusinessLivenessImsdkTarget = target;
  window.__qnbotBusinessLivenessOnRef = onRef;
  window.__qnbotBusinessLivenessHookInstalled = true;
  window.__qnbotBusinessLivenessHookInstalledAt = Date.now();
  return 'installed';
} catch (e) { return 'error:' + (e && e.message ? e.message : String(e)); }
})()";


        private void EnsureBusinessInboundCheckpointLoaded(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            lock (_businessInboundCheckpointSync)
            {
                if (_businessInboundCheckpointLoaded
                    && string.Equals(_businessInboundCheckpointShopKey, shop.ShopKey, StringComparison.Ordinal))
                    return;

                _businessInboundCheckpointLoaded = true;
                _businessInboundCheckpointRestored = false;
                _businessInboundCheckpointShopKey = shop.ShopKey;
                _lastBusinessInboundHistoryProbeCompletedAt = DateTime.MinValue;
                try
                {
                    var store = new ShopScopedSettingsStore(shop, new ShopScopedPathProvider());
                    string raw;
                    long ticks;
                    if (store.TryGetString(BusinessInboundCheckpointSettingKey, out raw)
                        && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)
                        && ticks > 0 && ticks <= DateTime.UtcNow.Ticks)
                    {
                        _lastBusinessInboundHistoryProbeCompletedAt =
                            new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                        _businessInboundCheckpointRestored = true;
                        Log.Info("business-inbound-checkpoint-restored: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                            + ", checkpoint=" + _lastBusinessInboundHistoryProbeCompletedAt.ToString("O"));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("读取本店业务入站恢复检查点失败，保留启动安全窗口: "
                        + ex.GetType().Name + ": " + ex.Message, 10);
                }
            }
        }

        private bool PersistBusinessInboundCheckpoint(ShopContext shop, DateTime checkpoint)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return false;
            try
            {
                var utcTicks = checkpoint.ToUniversalTime().Ticks;
                var store = new ShopScopedSettingsStore(shop, new ShopScopedPathProvider());
                store.SetString(
                    BusinessInboundCheckpointSettingKey,
                    utcTicks.ToString(CultureInfo.InvariantCulture));
                lock (_businessInboundCheckpointSync)
                {
                    _businessInboundCheckpointLoaded = true;
                    _businessInboundCheckpointRestored = true;
                    _businessInboundCheckpointShopKey = shop.ShopKey;
                    _lastBusinessInboundHistoryProbeCompletedAt = checkpoint;
                }
                Log.Info("business-inbound-checkpoint-persisted: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                    + ", checkpoint=" + checkpoint.ToString("O"));
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存本店业务入站恢复检查点失败，下一轮将重试: "
                    + ex.GetType().Name + ": " + ex.Message, 10);
                return false;
            }
        }

        private bool HasSellerReplyAfterRecoveredBuyer(
            QNChatMessage buyerMessage,
            IList<QNChatMessage> allMessages,
            IList<QNChatMessage> buyerCandidates,
            string seller,
            string buyer,
            string shopKey)
        {
            DateTime buyerTime;
            if (!IncomingMessageSafety.TryGetMessageTime(buyerMessage, out buyerTime)) return false;
            var buyerSort = buyerTime.Ticks;
            var orderedCandidates = new List<KeyValuePair<QNChatMessage, DateTime>>();
            foreach (var candidate in buyerCandidates ?? new List<QNChatMessage>())
            {
                DateTime candidateTime;
                if (candidate == null || !IncomingMessageSafety.TryGetMessageTime(candidate, out candidateTime))
                {
                    // An unparseable source timestamp makes burst boundaries unknowable. Do not
                    // infer acknowledgement from a later seller message in that case.
                    return false;
                }
                orderedCandidates.Add(new KeyValuePair<QNChatMessage, DateTime>(candidate, candidateTime));
            }
            orderedCandidates = orderedCandidates.OrderBy(x => x.Value).ToList();

            var groupItems = new List<BuyerMessageBurstItem>();
            var groupStartedAt = DateTime.MinValue;
            var previousBuyerTime = DateTime.MinValue;
            var nextBurstStartTicks = long.MaxValue;
            var targetFound = false;
            foreach (var pair in orderedCandidates)
            {
                if (groupItems.Count == 0)
                {
                    groupStartedAt = pair.Value;
                }
                else
                {
                    var quietDelay = BuyerMessageBurstCoordinator.QuietDelayMilliseconds(
                        groupItems, groupStartedAt, pair.Value);
                    var gapMs = (pair.Value - previousBuyerTime).TotalMilliseconds;
                    if (gapMs > quietDelay)
                    {
                        if (targetFound)
                        {
                            nextBurstStartTicks = pair.Value.Ticks;
                            break;
                        }
                        groupItems.Clear();
                        groupStartedAt = pair.Value;
                    }
                }

                var candidateText = GetMessageText(pair.Key);
                groupItems.Add(new BuyerMessageBurstItem
                {
                    ShopKey = shopKey,
                    SellerNick = seller,
                    BuyerNick = buyer,
                    Message = pair.Key,
                    DisplayText = candidateText,
                    SortValue = pair.Value.Ticks,
                    ReceivedAt = pair.Value
                });
                previousBuyerTime = pair.Value;
                if (ReferenceEquals(pair.Key, buyerMessage)) targetFound = true;
            }
            if (!targetFound) return false;

            // A later seller message only acknowledges this buyer turn if it appears before the
            // next distinct burst. A reply after a later, separately dispatched buyer question
            // must not suppress recovery of the earlier unanswered question.
            foreach (var message in allMessages ?? new List<QNChatMessage>())
            {
                if (message == null || !IsSellerMessage(message) || message.toid == null) continue;
                var sellerText = GetMessageText(message);
                if (ConversationContextStore.IsWithdrawalNotice(message, sellerText)
                    || ConversationContextStore.IsPlatformSystemTip(message, sellerText))
                    continue;
                DateTime sellerTime;
                if (!IncomingMessageSafety.TryGetMessageTime(message, out sellerTime)) continue;
                var sellerSort = sellerTime.Ticks;
                if (sellerSort <= buyerSort || sellerSort >= nextBurstStartTicks) continue;
                var recipient = (message.toid.nick ?? string.Empty).Trim();
                if (recipient.Length == 0
                    || AreBuyerAliasesEquivalentInShop(seller, recipient, buyer))
                    return true;
            }
            return false;
        }

        internal async Task RepairBusinessInboundHookAsync()
        {
            var client = CDP;
            var seller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            if (client == null || client.IsInvalidated || client.IsShopIdentityRetired || seller.Length == 0) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(this); }
            catch { return; }
            var server = MyWebSocketServer.WSocketSvrInst;
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)
                || !string.Equals(client.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                || server == null || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
            var result = await client.EvaluateExpressionAsync(
                BusinessInboundHookRepairScript,
                "BusinessInboundLivenessHookRepair").ConfigureAwait(false);
            if (!ReferenceEquals(CDP, client) || client.IsInvalidated || client.IsShopIdentityRetired
                || !string.Equals(client.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
            result = (result ?? string.Empty).Trim().Trim('\"');
            if (string.Equals(result, "installed", StringComparison.Ordinal))
            {
                Log.Info("business-inbound-hook-repaired: seller=" + seller
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", client.SessionId)
                    + ", reason=imsdk_object_or_on_handler_replaced");
                _lastBusinessInboundHookRepairLogAt = DateTime.Now;
            }
            else if (result.StartsWith("error:", StringComparison.Ordinal)
                && DateTime.Now - _lastBusinessInboundHookRepairLogAt > TimeSpan.FromMinutes(1))
            {
                Log.Info("业务入站钩子自愈暂未成功: result=" + result);
                _lastBusinessInboundHookRepairLogAt = DateTime.Now;
            }
        }

        internal async Task ProbeActiveConversationForMissedInboundAsync()
        {
            if (!Params.Robot.CanUseRobotReal) return;
            var client = CDP;
            var seller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            if (client == null || client.IsInvalidated || client.IsShopIdentityRetired || seller.Length == 0) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(this); }
            catch { return; }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)) return;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
            if (!await _businessInboundLivenessGate.WaitAsync(0).ConfigureAwait(false)) return;

            using (ShopSettingsScope.Enter(shop, false))
            {
            EnsureBusinessInboundCheckpointLoaded(shop);
            try
            {
                if (!ReferenceEquals(CDP, client)
                    || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
                var probeStartedAt = DateTime.Now;
                var first = await GetCurrentConversationID().ConfigureAwait(false);
                if (!ReferenceEquals(CDP, client)
                    || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
                var current = first == null ? null : first.Result;
                if (current == null || string.IsNullOrWhiteSpace(current.Nick)) return;

                string nonBuyerReason;
                if (NonBuyerConversationGuard.ShouldBlockConversation(Seller, current, out nonBuyerReason)) return;

                var buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, current.Nick);
                if (string.IsNullOrWhiteSpace(buyer)) return;

                // A status/page transition can expose a transient conversation. If the business
                // event stream missed the switch, require two stable reads before changing QN.Buyer.
                var cachedBuyer = Buyer == null ? string.Empty : (Buyer.Nick ?? string.Empty).Trim();
                if (!BuyerIdentityAliasService.AreEquivalent(seller, cachedBuyer, buyer))
                {
                    await Task.Delay(180).ConfigureAwait(false);
                    var second = await GetCurrentConversationID().ConfigureAwait(false);
                    if (!ReferenceEquals(CDP, client)
                        || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
                    var stable = second == null ? null : second.Result;
                    if (stable == null || string.IsNullOrWhiteSpace(stable.Nick)
                        || !BuyerIdentityAliasService.AreEquivalent(seller, stable.Nick, buyer))
                    {
                        return;
                    }
                    current = stable;
                    BuyerIdentityAliasService.Observe(seller, current.Nick, current.Display, current.TargetId);
                    SetActiveConversationByNick(seller, buyer, "businessInboundLivenessStableProbe");
                }

                var ccode = (current.Ccode ?? string.Empty).Trim();
                if (ccode.Length == 0) return;

                // Normal probes intentionally keep a short two-minute window. If the runtime or
                // scheduler did not complete a history probe for longer than that, resume from the
                // last successful probe instead. This closes the production gap where a 20+ minute
                // scheduling pause made a real buyer message permanently invisible to the recovery
                // bridge because DateTime.Now-2m had already advanced past it when timers resumed.
                var lastCompletedAt = _lastBusinessInboundHistoryProbeCompletedAt;
                var catchupFloor = probeStartedAt.Subtract(BusinessInboundNormalLookback);
                var gapRecovery = lastCompletedAt != DateTime.MinValue
                    && probeStartedAt - lastCompletedAt > BusinessInboundNormalLookback;
                if (lastCompletedAt != DateTime.MinValue)
                {
                    var previousFloor = lastCompletedAt.Subtract(BusinessInboundCatchupOverlap);
                    var oldestAllowed = probeStartedAt.Subtract(BusinessInboundMaxCatchupLookback);
                    if (previousFloor < oldestAllowed) previousFloor = oldestAllowed;
                    if (previousFloor < catchupFloor) catchupFloor = previousFloor;
                }

                if (gapRecovery
                    && DateTime.Now - _lastBusinessInboundProbeGapLogAt > TimeSpan.FromMinutes(1))
                {
                    Log.Info("business-inbound-probe-gap-detected: seller=" + seller
                        + ", gapSeconds=" + Math.Max(0, (int)(probeStartedAt - lastCompletedAt).TotalSeconds)
                        + ", catchupSeconds=" + Math.Max(0, (int)(probeStartedAt - catchupFloor).TotalSeconds)
                        + ", recovery=remote-history");
                    _lastBusinessInboundProbeGapLogAt = DateTime.Now;
                }

                var historyCount = gapRecovery ? 100 : 20;
                var history = await client.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = ccode, type = 1 },
                    count = historyCount,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                }).ConfigureAwait(false);
                if (history == null || !ReferenceEquals(CDP, client)
                    || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;

                var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>()
                    ?? new List<QNChatMessage>();
                // A persisted checkpoint is stronger than the process-start guard: it proves
                // where the previous successful history scan stopped. On a fresh installation,
                // keep the original startup safety window to avoid replaying old unread history.
                var threshold = _businessInboundCheckpointRestored
                    ? catchupFloor.Ticks
                    : Math.Max(_messageSafetyStartedAt.AddSeconds(-8).Ticks, catchupFloor.Ticks);
                var candidates = messages
                    .Where(m => m != null && IsRecoveredBuyerMessageForTarget(m, seller, buyer))
                    .Where(m => IncomingMessageSafety.GetSortValue(m) >= threshold)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();
                if (candidates.Count == 0)
                {
                    PersistBusinessInboundCheckpoint(shop, DateTime.Now);
                    return;
                }

                var recoveryKey = RecoveryKey(seller, buyer);
                DateTime observedAt;
                var hasObserved = _latestBuyerMessageObserved.TryGetValue(recoveryKey, out observedAt);

                // Recovery is source-chronology based. A live-observed buyer turn remains
                // unresolved until a seller message for that same buyer appears after it in
                // remote history. This prevents advancing the durable checkpoint past a message
                // whose generation/send may still be in flight when the client process exits.
                var missedCandidates = candidates
                    .Where(message => !hasObserved
                        || IncomingMessageSafety.GetSortValue(message) > observedAt.Ticks)
                    .ToList();
                var alreadyAnswered = candidates
                    .Where(message => HasSellerReplyAfterRecoveredBuyer(
                        message, messages, candidates, seller, buyer, shop.ShopKey))
                    .ToList();
                var unanswered = candidates
                    .Where(message => !alreadyAnswered.Contains(message))
                    .ToList();
                var missedToReplay = missedCandidates
                    .Where(message => !alreadyAnswered.Contains(message))
                    .ToList();

                if (alreadyAnswered.Count > 0)
                {
                    Log.Info("business-inbound-recovery-skipped-already-answered: seller=" + seller
                        + ", buyer=" + buyer + ", count=" + alreadyAnswered.Count
                        + ", source=remote-seller-history");
                }

                if (missedToReplay.Count > 0)
                {
                    Log.Info("business-event-gap-confirmed: seller=" + seller + ", buyer=" + buyer
                        + ", transport=connected, liveCheckpoint="
                        + (hasObserved ? observedAt.ToString("HH:mm:ss.fff") : "none")
                        + ", recoveredCount=" + missedToReplay.Count
                        + ", recovery=remote-history");
                }

                foreach (var message in missedToReplay)
                {
                    if (!ReferenceEquals(CDP, client)
                        || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)) return;
                    var text = GetMessageText(message);
                    var messageKey = IncomingMessageSafety.BuildMessageKey(message, text);
                    if (!_businessInboundHistoryProbeLedger.TryAccept(messageKey)) continue;

                    Log.Info("unseen-buyer-message-detected: seller=" + seller + ", buyer=" + buyer
                        + ", key=" + messageKey + ", source=remote-history");

                    // Never implement a second reply pipeline here. The normal path owns business
                    // dedupe, Knowledge V2/AI routing, burst coalescing and send verification.
                    await ProcessIncomingMessageAsync(message).ConfigureAwait(false);

                    Log.Info("recovered-inbound-replay: seller=" + seller + ", buyer=" + buyer
                        + ", key=" + messageKey + ", route=ProcessIncomingMessageAsync");
                }

                if (unanswered.Count == 0)
                {
                    PersistBusinessInboundCheckpoint(shop, DateTime.Now);
                }
                else
                {
                    // Do not advance past an unanswered buyer turn. It may still be generating or
                    // sending; retaining the earlier checkpoint lets a new process recover it after
                    // restart. The 45-minute catch-up cap bounds stale replay.
                    if (!_businessInboundCheckpointRestored)
                    {
                        var safeBaseline = new DateTime(threshold, DateTimeKind.Local);
                        PersistBusinessInboundCheckpoint(shop, safeBaseline);
                    }
                    Log.Info("business-inbound-checkpoint-held-for-unanswered-turns: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                        + ", seller=" + seller + ", buyer=" + buyer
                        + ", unansweredCount=" + unanswered.Count
                        + ", replayedCount=" + missedToReplay.Count);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("当前会话业务入站远端历史核对失败: seller=" + seller
                    + ", error=" + ex.Message, 50);
            }
            finally
            {
                _businessInboundLivenessGate.Release();
            }
            }
        }
    }
}