using Bot.ChatRecord;
using BotLib;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Filter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    // KnowledgeCenterWindow.cs is compiled inside the Bot.ChromeNs namespace for the
    // optimization service, while the legacy database facade lives in Bot.Common.
    // Keep a namespace-local compatibility alias so normal and WPF temporary builds
    // resolve the same shared database facade without duplicating database state.
    internal class DbHelper : Bot.Common.DbHelper
    {
    }

    // Keeps startup calls beside the handoff service while the actual list
    // management implementation lives with the knowledge UI types.
    internal static class BulkListManagementUi
    {
        public static void Initialize()
        {
            HandoffPolicyLegacyMigrationService.StartOnce();
            Bot.Knowledge.BulkListManagementUi.Initialize();
        }
    }

    // log4net 2.0.3 used by this legacy client does not ship a RegexFilter. The runtime
    // noise filter needs deterministic regex matching, so provide the tiny FilterSkeleton
    // implementation locally. It is used by both normal and WPF temporary builds.
    internal sealed class RegexFilter : FilterSkeleton
    {
        private Regex _regex;
        public string RegexToMatch { get; set; }
        public bool AcceptOnMatch { get; set; }

        public override void ActivateOptions()
        {
            _regex = string.IsNullOrWhiteSpace(RegexToMatch)
                ? null
                : new Regex(RegexToMatch, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            base.ActivateOptions();
        }

        public override FilterDecision Decide(LoggingEvent loggingEvent)
        {
            if (loggingEvent == null) return FilterDecision.Neutral;
            var rendered = loggingEvent.RenderedMessage ?? string.Empty;

            // These are diagnostic fault signals, never ordinary noise. Keep them visible even
            // during the first few seconds before RuntimeLogNoiseSafetyOverride replaces an old
            // over-broad filter that may still be present in a temporary WPF build.
            if (rendered.IndexOf("SendForGetText", StringComparison.OrdinalIgnoreCase) >= 0)
                return FilterDecision.Neutral;
            if (rendered.IndexOf("千牛注入状态:", StringComparison.Ordinal) >= 0
                && rendered.IndexOf("\"extra\":\"loop\"", StringComparison.Ordinal) < 0)
                return FilterDecision.Neutral;

            if (_regex == null) ActivateOptions();
            if (_regex == null) return FilterDecision.Neutral;
            var matched = _regex.IsMatch(rendered);
            if (!matched) return FilterDecision.Neutral;
            return AcceptOnMatch ? FilterDecision.Accept : FilterDecision.Deny;
        }
    }

    // In WPF temporary projects FirstInquiryDeliveryBridge.cs is not part of the generated
    // compile set, while it is present in the real Bot project. This extension satisfies the
    // temporary compile and, if ever invoked without the real instance method, reflects into it.
    internal static class QnVisibleOrderPanelProbeCompatibility
    {
        public static async Task<bool> TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
            this QN qn,
            string seller,
            string buyer,
            string source,
            DateTime notBefore,
            bool requireFresh)
        {
            if (qn == null) return false;
            var method = typeof(QN).GetMethod(
                "TryRecoverVisibleOrderPanelForBackgroundProbeAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(DateTime), typeof(bool) },
                null);
            if (method == null || method.DeclaringType == typeof(QnVisibleOrderPanelProbeCompatibility)) return false;
            var task = method.Invoke(qn, new object[] { seller, buyer, source, notBefore, requireFresh }) as Task<bool>;
            return task != null && await task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single source-of-truth for recovery/reconciliation replay ownership. Live ingress keeps its
    /// normal business deduplicators; every recovery path must claim a physical message here before
    /// re-injecting it, so periodic watchdogs cannot repeatedly replay the same remote-history row.
    /// </summary>
    internal static class ConversationIngressRecoveryLedger
    {
        private const int MaxClaims = 20000;
        private static readonly TimeSpan ClaimRetention = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, DateTime> Claims =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static int _cleanupTick;

        public static bool TryClaim(
            string seller,
            QNChatMessage message,
            string rawFallback,
            out string claimKey)
        {
            claimKey = BuildClaimKey(seller, message, rawFallback);
            if (claimKey.Length == 0) return true;
            var now = DateTime.UtcNow;
            if (!Claims.TryAdd(claimKey, now)) return false;
            Cleanup(now);
            return true;
        }

        public static void Release(string claimKey)
        {
            if (string.IsNullOrWhiteSpace(claimKey)) return;
            DateTime ignored;
            Claims.TryRemove(claimKey, out ignored);
        }

        private static string BuildClaimKey(string seller, QNChatMessage message, string rawFallback)
        {
            seller = (seller ?? string.Empty).Trim();
            var messageKey = message == null
                ? string.Empty
                : IncomingMessageSafety.BuildMessageKey(message, string.Empty);
            if (string.IsNullOrWhiteSpace(messageKey) && !string.IsNullOrWhiteSpace(rawFallback))
                messageKey = "raw:" + StableHash64(rawFallback).ToString("x16");
            return string.IsNullOrWhiteSpace(messageKey)
                ? string.Empty
                : seller + "#" + messageKey;
        }

        private static ulong StableHash64(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return hash;
        }

        private static void Cleanup(DateTime now)
        {
            if ((Interlocked.Increment(ref _cleanupTick) & 127) != 0 && Claims.Count <= MaxClaims) return;
            var cutoff = now - ClaimRetention;
            foreach (var pair in Claims)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                Claims.TryRemove(pair.Key, out ignored);
            }
            var overflow = Claims.Count - MaxClaims;
            if (overflow <= 0) return;
            foreach (var pair in Claims.OrderBy(x => x.Value).Take(overflow))
            {
                DateTime ignored;
                Claims.TryRemove(pair.Key, out ignored);
            }
        }
    }

    // The WebSocket/CDP transport can stay alive while Qianniu replaces an SDK object or one of
    // the page callbacks that inject.js wrapped. The historical booleans such as
    // __qnbotImsdkHookInstalled then stay true even though the business callback is no longer the
    // active one. A green socket therefore must not be treated as proof that buyer/order events
    // are fresh.
    //
    // This watchdog does not send messages and never changes the selected conversation. It reads
    // Qianniu's own local conversation cache, identifies ccodes whose cached message list changed,
    // asks the existing SDK for unread/recent canonical messages, then feeds those messages back
    // through CDPClient.DispatchInboundEvent("receiveNewMsg", ...). Existing QN deduplication,
    // startup-history guards, order dedupe, manual-intervention checks and the normal safe send
    // state machine remain authoritative.
    internal static class ConversationMapIngressWatchdog
    {
        private sealed class PendingSession
        {
            public readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
            public readonly ConcurrentDictionary<string, byte> Keys =
                new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        }

        private static readonly ConcurrentDictionary<QN, PendingSession> PendingByQn =
            new ConcurrentDictionary<QN, PendingSession>();
        private static readonly ConcurrentDictionary<QN, DateTime> LastWideOrderProbe =
            new ConcurrentDictionary<QN, DateTime>();
        private static Timer _timer;
        private static int _started;
        private static int _running;

        private const int TickSeconds = 15;
        private const int MaxRecoveriesPerTickPerQn = 4;
        private const int RemoteHistoryMinutes = 4;

        // Kept as an execute expression instead of modifying the protected inject.js production
        // payload. It is read-only except for qnbot's own watchdog state and for clearing an
        // installed flag when we can directly prove that the wrapped SDK/object was replaced.
        private const string ConversationMapProbeExpression = @"
(function () {
  try {
    var map = window._db && window._db.msgDataMap;
    if (!map || typeof map.forEach !== 'function') return { ok: false, ccodes: [], repaired: [] };
    var state = window.__qnbotIngressMapWatchdogState;
    if (!state) {
      state = window.__qnbotIngressMapWatchdogState = { ready: false, fingerprints: Object.create(null), imsdk: null, qn: null };
    }
    var repaired = [];
    if (state.imsdk && window.imsdk && state.imsdk !== window.imsdk) {
      window.__qnbotImsdkHookInstalled = false;
      repaired.push('imsdk-object');
    }
    if (state.qn && window.QN && state.qn !== window.QN) {
      window.__qnbotQnNotifyInstalled = false;
      repaired.push('qn-object');
    }
    state.imsdk = window.imsdk || null;
    state.qn = window.QN || null;
    try {
      if (window.__qnbotOnEventNotifyInstalled && typeof window.onEventNotify === 'function') {
        var eventSource = Function.prototype.toString.call(window.onEventNotify);
        if (eventSource.indexOf('onEventNotify wrapper failed') < 0) {
          window.__qnbotOnEventNotifyInstalled = false;
          repaired.push('onEventNotify');
        }
      }
    } catch (ignoreEvent) {}
    try {
      if (window.__qnbotImsdkHookInstalled && window.onInvokeNotifyDelegate && typeof window.onInvokeNotify === 'function') {
        var invokeSource = Function.prototype.toString.call(window.onInvokeNotify);
        if (invokeSource.indexOf('onInvokeNotify wrapper failed') < 0) {
          window.__qnbotImsdkHookInstalled = false;
          repaired.push('onInvokeNotify');
        }
      }
    } catch (ignoreInvoke) {}

    function toMillis(value) {
      var n = Number(value || 0);
      if (!isFinite(n) || n <= 0) return 0;
      if (n > 1000000000000000) return n / 1000;
      if (n > 100000000000) return n;
      if (n > 1000000000) return n * 1000;
      return 0;
    }

    var next = Object.create(null);
    var changed = [];
    map.forEach(function (messages, ccodeValue) {
      if (!ccodeValue) return;
      var ccode = String(ccodeValue);
      var arr = Array.isArray(messages) ? messages : [];
      var last = arr.length ? (arr[arr.length - 1] || {}) : {};
      var origin = last.originBanamaMessage || last || {};
      var ext = origin.ext || last.ext || {};
      var mcode = origin.mcode || {};
      var timeValue = origin.sortTimeMicrosecond || origin.sendTime || last.sortTimeMicrosecond || last.sendTime || '';
      var fingerprint = [
        arr.length,
        ext.ww_msgid || '',
        mcode.clientId || '',
        mcode.messageId || '',
        timeValue,
        origin.summary || last.summary || ''
      ].join('|');
      next[ccode] = fingerprint;
      var changedNow = state.ready && state.fingerprints[ccode] !== fingerprint;
      var recentOnFirstPass = !state.ready && toMillis(timeValue) >= Date.now() - (5 * 60 * 1000);
      if (changedNow || recentOnFirstPass) changed.push(ccode);
    });
    state.fingerprints = next;
    state.ready = true;
    return { ok: true, ccodes: changed, repaired: repaired };
  } catch (e) {
    return { ok: false, ccodes: [], repaired: [], error: String(e && e.message ? e.message : e) };
  }
})()";

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _timer = new Timer(Tick, null, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(TickSeconds));
            return new object();
        }

        private static async void Tick(object state)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                var sessions = GetQnSnapshot();
                foreach (var qn in sessions)
                {
                    if (qn == null || qn.CDP == null) continue;
                    await DiscoverChangedConversationsAsync(qn).ConfigureAwait(false);
                    await DrainPendingAsync(qn).ConfigureAwait(false);
                    await ProbeWideVisibleOrderAsync(qn).ConfigureAwait(false);
                }
                CleanupDeadQnState(sessions);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("业务入站主动核对失败：" + ex.Message, 5);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static List<QN> GetQnSnapshot()
        {
            var result = new List<QN>();
            try
            {
                var method = typeof(QN).GetMethod(
                    "GetRuntimeSafetySnapshot",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var reflected = method == null ? null : method.Invoke(null, null) as IEnumerable<QN>;
                if (reflected != null) result.AddRange(reflected.Where(x => x != null));
            }
            catch
            {
            }
            if (result.Count < 1 && QN.CurQN != null) result.Add(QN.CurQN);
            return result.Distinct().ToList();
        }

        private static async Task DiscoverChangedConversationsAsync(QN qn)
        {
            var cdp = qn == null ? null : qn.CDP;
            if (cdp == null) return;
            var raw = await EvaluateExpressionCompatAsync(cdp, ConversationMapProbeExpression, "IngressConversationMapProbe")
                .ConfigureAwait(false);
            var result = ParseObject(raw);
            if (result == null) return;

            var repaired = result["repaired"] as JArray;
            if (repaired != null && repaired.Count > 0)
            {
                var names = repaired.Select(x => (x == null ? string.Empty : x.ToString()).Trim())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (names.Length > 0)
                    Log.Info("千牛业务入站监听自校验触发重挂: components=" + string.Join(",", names));
            }

            var ccodes = result["ccodes"] as JArray;
            if (ccodes == null || ccodes.Count < 1) return;
            var pending = PendingByQn.GetOrAdd(qn, ignored => new PendingSession());
            foreach (var token in ccodes)
            {
                var ccode = (token == null ? string.Empty : token.ToString()).Trim();
                if (ccode.Length < 1) continue;
                if (pending.Keys.TryAdd(ccode, 0)) pending.Queue.Enqueue(ccode);
            }
        }

        private static async Task DrainPendingAsync(QN qn)
        {
            PendingSession pending;
            if (qn == null || !PendingByQn.TryGetValue(qn, out pending) || pending == null) return;
            for (var i = 0; i < MaxRecoveriesPerTickPerQn; i++)
            {
                string ccode;
                if (!pending.Queue.TryDequeue(out ccode)) break;
                var completed = false;
                try
                {
                    completed = await RecoverConversationAsync(qn, ccode).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("业务入站会话补偿失败：" + ex.Message, 8);
                }

                if (completed)
                {
                    byte removed;
                    pending.Keys.TryRemove(ccode, out removed);
                }
                else
                {
                    pending.Queue.Enqueue(ccode);
                    break;
                }
            }
        }

        private static async Task<bool> RecoverConversationAsync(QN qn, string ccode)
        {
            var cdp = qn == null ? null : qn.CDP;
            if (cdp == null || string.IsNullOrWhiteSpace(ccode)) return false;

            // Prefer unread canonical data. This is the same SDK call already used by inject.js
            // after im.singlemsg.onReceiveNewMsg, so the fallback does not invent a second parser.
            var unread = await cdp.Invoke<JObject>("im.singlemsg.GetNewMsg", new { ccode = ccode }).ConfigureAwait(false);
            if (HasDispatchableResult(unread))
            {
                var unreadMessages = unread["result"] as JArray;
                var claimedUnread = ClaimRecoveryMessages(qn, unreadMessages);
                if (claimedUnread.Messages.Count > 0)
                {
                    try
                    {
                        var unreadPayload = (JObject)unread.DeepClone();
                        unreadPayload["result"] = claimedUnread.Messages;
                        DispatchReceiveNewMsgCompat(cdp, unreadPayload.ToString(Formatting.None));
                    }
                    catch
                    {
                        ReleaseRecoveryClaims(claimedUnread.ClaimKeys);
                        throw;
                    }
                }
                return true;
            }

            // A human may have opened the missed conversation before the watchdog notices it,
            // which marks unread data as read. In that case inspect only a tightly bounded recent
            // history window; unknown-time records fail closed and are never replayed.
            var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
            {
                cid = new { ccode = ccode, type = 1 },
                count = 20,
                gohistory = 1,
                msgid = "-1",
                msgtime = "-1"
            }).ConfigureAwait(false);
            if (history == null) return false;
            var messages = history["result"]?["msgs"] as JArray;
            if (messages == null || messages.Count < 1) return true;

            var threshold = DateTime.Now.AddMinutes(-RemoteHistoryMinutes);
            var recent = new JArray();
            foreach (var message in messages)
            {
                if (message == null || !IsRecentMessageToken(message, threshold)) continue;
                recent.Add(message.DeepClone());
            }
            if (recent.Count < 1) return true;

            var claimedRecent = ClaimRecoveryMessages(qn, recent);
            if (claimedRecent.Messages.Count < 1) return true;
            try
            {
                var payload = new JObject { ["result"] = claimedRecent.Messages };
                DispatchReceiveNewMsgCompat(cdp, payload.ToString(Formatting.None));
                Log.Info("业务入站缓存补偿已回灌新增候选消息: count=" + claimedRecent.Messages.Count + ", source=conversation-map");
            }
            catch
            {
                ReleaseRecoveryClaims(claimedRecent.ClaimKeys);
                throw;
            }
            return true;
        }

        private sealed class ClaimedRecoveryBatch
        {
            public readonly JArray Messages = new JArray();
            public readonly List<string> ClaimKeys = new List<string>();
        }

        private static ClaimedRecoveryBatch ClaimRecoveryMessages(QN qn, JArray messages)
        {
            var batch = new ClaimedRecoveryBatch();
            if (messages == null || messages.Count < 1) return batch;
            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            foreach (var token in messages)
            {
                if (token == null) continue;
                QNChatMessage model = null;
                try { model = token.ToObject<QNChatMessage>(); } catch { }
                string claimKey;
                if (!ConversationIngressRecoveryLedger.TryClaim(
                    seller, model, token.ToString(Formatting.None), out claimKey)) continue;
                batch.Messages.Add(token.DeepClone());
                if (!string.IsNullOrWhiteSpace(claimKey)) batch.ClaimKeys.Add(claimKey);
            }
            return batch;
        }

        private static void ReleaseRecoveryClaims(IEnumerable<string> claimKeys)
        {
            foreach (var claimKey in claimKeys ?? Enumerable.Empty<string>())
                ConversationIngressRecoveryLedger.Release(claimKey);
        }

        private static bool HasDispatchableResult(JObject response)
        {
            if (response == null) return false;
            var array = response["result"] as JArray;
            return array != null && array.Count > 0;
        }

        private static void DispatchReceiveNewMsgCompat(CDPClient cdp, string payload)
        {
            if (cdp == null || string.IsNullOrWhiteSpace(payload)) return;
            var method = typeof(CDPClient).GetMethod(
                "DispatchInboundEvent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            if (method == null) return;
            method.Invoke(cdp, new object[] { "receiveNewMsg", payload });
        }

        private static async Task<string> EvaluateExpressionCompatAsync(CDPClient cdp, string expression, string description)
        {
            if (cdp == null) return string.Empty;
            var method = typeof(CDPClient).GetMethod(
                "EvaluateExpressionAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            if (method == null) return string.Empty;
            var task = method.Invoke(cdp, new object[] { expression, description }) as Task<string>;
            return task == null ? string.Empty : (await task.ConfigureAwait(false) ?? string.Empty);
        }

        private static JObject ParseObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var token = JToken.Parse(raw);
                if (token.Type == JTokenType.String)
                {
                    var nested = token.ToString();
                    if (string.IsNullOrWhiteSpace(nested)) return null;
                    token = JToken.Parse(nested);
                }
                return token as JObject;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRecentMessageToken(JToken token, DateTime notBefore)
        {
            if (token == null) return false;
            DateTime value;
            if (TryParseMessageTime(token["sendTime"], out value)
                || TryParseMessageTime(token["sortTimeMicrosecond"], out value))
            {
                return value >= notBefore && value <= DateTime.Now.AddMinutes(2);
            }
            return false;
        }

        private static bool TryParseMessageTime(JToken token, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            var text = token == null ? string.Empty : token.ToString().Trim();
            if (text.Length < 1) return false;
            long raw;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                try
                {
                    if (raw > 1000000000000000L)
                        localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L)
                        localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L)
                        localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch
                {
                }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static async Task ProbeWideVisibleOrderAsync(QN qn)
        {
            if (qn == null || qn.Seller == null || qn.Buyer == null) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = (qn.Buyer.Nick ?? string.Empty).Trim();
            if (seller.Length < 1 || buyer.Length < 1) return;

            var now = DateTime.Now;
            DateTime last;
            if (LastWideOrderProbe.TryGetValue(qn, out last) && (now - last).TotalSeconds < 60) return;
            LastWideOrderProbe.AddOrUpdate(qn, now, (ignored, old) => now);
            try
            {
                await qn.TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
                    seller,
                    buyer,
                    "runtime-ingress-wide-order",
                    now.AddMinutes(-4),
                    true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("业务入站订单面板宽窗口核对失败：" + ex.Message, 5);
            }
        }

        private static void CleanupDeadQnState(ICollection<QN> sessions)
        {
            var alive = new HashSet<QN>((sessions ?? new List<QN>()).Where(x => x != null));
            foreach (var qn in PendingByQn.Keys)
            {
                if (alive.Contains(qn)) continue;
                PendingSession ignored;
                PendingByQn.TryRemove(qn, out ignored);
                DateTime ignoredTime;
                LastWideOrderProbe.TryRemove(qn, out ignoredTime);
            }
        }
    }

    // The first implementation of RuntimeLogNoiseFilterBootstrap filtered two signals too
    // broadly. Repair that exact filter in place after startup: SendForGetText remains visible,
    // and only stable qnbotStatus/extra=loop injection status is hidden.
    internal static class RuntimeLogNoiseSafetyOverride
    {
        private static Timer _timer;
        private static int _started;
        private static int _reported;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _timer = new Timer(_ => Apply(), null, 4000, 10000);
            return new object();
        }

        private static void Apply()
        {
            try
            {
                var pattern = BuildPattern();
                foreach (var appender in LogManager.GetRepository().GetAppenders().OfType<AppenderSkeleton>())
                {
                    for (var filter = appender.FilterHead; filter != null; filter = filter.Next)
                    {
                        var regex = filter as RegexFilter;
                        if (regex == null) continue;
                        var current = regex.RegexToMatch ?? string.Empty;
                        if (current.IndexOf("设置界面已将“人工客服工作时间与下班回复”迁移", StringComparison.Ordinal) < 0)
                            continue;
                        if (current.IndexOf("SendForGetText", StringComparison.Ordinal) < 0
                            && current.IndexOf("千牛注入状态:", StringComparison.Ordinal) < 0)
                            continue;
                        regex.RegexToMatch = pattern;
                        regex.ActivateOptions();
                    }
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                    Log.ErrorWithMaxCount("运行日志降噪安全边界校正失败：" + ex.Message, 3);
            }
        }

        private static string BuildPattern()
        {
            var phrases = new[]
            {
                "设置界面已将“人工客服工作时间与下班回复”迁移",
                "设置界面已在构造阶段将“启用转人工规则”",
                "设置界面已直接构造“转人工策略”页面并迁移转人工规则",
                "UIA控件刷新成功:",
                "收到千牛WebSocket事件: type=qnbotStatus",
                "检测到卖家重复千牛WebSocket页面，保留已稳定的权威CDP会话",
                "RPA已绑定卖家专属千牛窗口:",
                "IMSDK璋冪敤璺熻釜:",
                "后台订单面板延迟兜底订单已由其他通道处理/去重"
            };
            return string.Join("|", phrases.Select(Regex.Escape))
                + "|千牛注入状态:.*" + Regex.Escape("\"extra\":\"loop\"");
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _runtimeLogNoiseSafetyOverrideBootstrap =
            ChromeNs.RuntimeLogNoiseSafetyOverride.InitializeForApp();
        private readonly object _conversationMapIngressWatchdogBootstrap =
            ChromeNs.ConversationMapIngressWatchdog.InitializeForApp();
    }
}
