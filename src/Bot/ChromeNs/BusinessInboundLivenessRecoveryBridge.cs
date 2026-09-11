using Bot.ChatRecord;
using BotLib;
using DbEntity;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
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
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || qn.CDP == null || qn.CDP.IsInvalidated || qn.Seller == null
                        || string.IsNullOrWhiteSpace(qn.Seller.Nick))
                    {
                        continue;
                    }

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
                            {
                                await qn.RepairBusinessInboundHookAsync().ConfigureAwait(false);
                            }
                            if (historyDue)
                            {
                                await qn.ProbeActiveConversationForMissedInboundAsync().ConfigureAwait(false);
                            }
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
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("业务入站存活恢复调度失败: " + ex.Message, 20);
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

        internal async Task RepairBusinessInboundHookAsync()
        {
            var client = CDP;
            if (client == null || client.IsInvalidated) return;
            var result = await client.EvaluateExpressionAsync(
                BusinessInboundHookRepairScript,
                "BusinessInboundLivenessHookRepair").ConfigureAwait(false);
            result = (result ?? string.Empty).Trim().Trim('"');
            if (string.Equals(result, "installed", StringComparison.Ordinal))
            {
                var seller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
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
            if (client == null || client.IsInvalidated || seller.Length == 0) return;
            if (!await _businessInboundLivenessGate.WaitAsync(0).ConfigureAwait(false)) return;

            try
            {
                var probeStartedAt = DateTime.Now;
                var first = await GetCurrentConversationID().ConfigureAwait(false);
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
                if (history == null) return;

                var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>()
                    ?? new List<QNChatMessage>();
                var threshold = Math.Max(
                    _messageSafetyStartedAt.AddSeconds(-8).Ticks,
                    catchupFloor.Ticks);
                var candidates = messages
                    .Where(m => m != null && IsRecoveredBuyerMessageForTarget(m, seller, buyer))
                    .Where(m => IncomingMessageSafety.GetSortValue(m) >= threshold)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();
                if (candidates.Count == 0)
                {
                    _lastBusinessInboundHistoryProbeCompletedAt = DateTime.Now;
                    return;
                }

                var recoveryKey = RecoveryKey(seller, buyer);
                DateTime observedAt;
                var hasObserved = _latestBuyerMessageObserved.TryGetValue(recoveryKey, out observedAt);

                // Recovery must be decided by source chronology, not by a wall-clock freshness
                // timeout. A 12-second history probe naturally runs more than two seconds after a
                // healthy live event; treating that age as "stale" replayed already-observed messages
                // and let the recovery bridge compete with the live path. The last live observation
                // is the transport checkpoint. Only source messages newer than that checkpoint are
                // eligible for replay; the normal ProcessIncomingMessageAsync path remains the sole
                // business side-effect owner.
                var missedCandidates = candidates
                    .Where(message => !hasObserved
                        || IncomingMessageSafety.GetSortValue(message) > observedAt.Ticks)
                    .ToList();
                if (missedCandidates.Count == 0)
                {
                    _lastBusinessInboundHistoryProbeCompletedAt = DateTime.Now;
                    return;
                }

                Log.Info("business-event-gap-confirmed: seller=" + seller + ", buyer=" + buyer
                    + ", transport=connected, liveCheckpoint="
                    + (hasObserved ? observedAt.ToString("HH:mm:ss.fff") : "none")
                    + ", recoveredCount=" + missedCandidates.Count
                    + ", recovery=remote-history");

                foreach (var message in missedCandidates)
                {
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

                _lastBusinessInboundHistoryProbeCompletedAt = DateTime.Now;
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