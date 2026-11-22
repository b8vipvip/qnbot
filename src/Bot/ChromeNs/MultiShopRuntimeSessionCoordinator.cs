using Bot.AssistWindow;
using Bot.AssistWindow.Widget;
using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _multiShopRuntimeSessionBootstrap =
            ChromeNs.MultiShopRuntimeSessionCoordinator.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class MultiShopRuntimeSessionCoordinator
    {
        private sealed class ActiveShopProbe
        {
            public QN Qn { get; set; }
            public string Seller { get; set; }
            public string TargetId { get; set; }
            public string Buyer { get; set; }
            public string SessionId { get; set; }
            public bool Focused { get; set; }
            public bool Visible { get; set; }
        }

        private static readonly ConditionalWeakTable<QN, object> Subscribed = new ConditionalWeakTable<QN, object>();
        private static readonly object SubscriptionSync = new object();
        private static Timer _timer;
        private static int _started;
        private static int _probeRunning;
        private static DateTime _lastProbeStartedUtc = DateTime.MinValue;

        public static object InitializeForApp()
        {
            Start();
            return new object();
        }

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new Timer(_ => Refresh(), null, 300, 400);
            Log.Info("多店铺运行时会话协调器已启动：活动店铺由聚焦千牛WebView仲裁。");
        }

        internal static bool EnsureShopBinding(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return false;
            // DisplayName is presentation data and may differ from Nick or be
            // shared by several shops. Resolve the exact persisted identity instead
            // of selecting an arbitrary QN instance by a display string.
            var qn = ShopContextLocator.FindConnectedQnByShopKey(shop.ShopKey);
            if (qn == null || qn.Rpa == null || !ActiveShopSessionRegistry.IsActive(qn)) return false;
            var ok = qn.Rpa.EnsureSellerDeskBinding(true);
            SyncAttachedUi(qn);
            return ok;
        }

        private static void Refresh()
        {
            try
            {
                var snapshot = QN.GetRuntimeSafetySnapshot().Where(x => x != null).ToList();
                foreach (var qn in snapshot)
                {
                    lock (SubscriptionSync)
                    {
                        object marker;
                        if (!Subscribed.TryGetValue(qn, out marker))
                        {
                            Subscribe(qn);
                            Subscribed.Add(qn, new object());
                        }
                    }
                    var seller = Seller(qn);
                    var shopKey = ShopKey(qn);
                    var desk = DeskSellerBindingRegistry.FindSellerDesk(seller, shopKey);
                    if (qn.Rpa != null && ActiveShopSessionRegistry.IsActive(qn)
                        && (desk == null || DeskSellerBindingRegistry.IsSellerForDesk(desk, seller, shopKey)))
                        qn.Rpa.EnsureSellerDeskBinding();
                    SyncAttachedUi(qn);
                }

                // Legacy status/init code can still assign CurQN while refreshing a background
                // seller. Reassert the independently arbitrated seller on every coordinator tick.
                ActiveShopSessionRegistry.ReassertCurrent("runtime-refresh");
                QueueActiveShopProbe(snapshot);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新多店铺运行时会话失败: " + ex.Message, 10);
            }
        }

        private static void QueueActiveShopProbe(IList<QN> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0) return;
            if ((DateTime.UtcNow - _lastProbeStartedUtc).TotalMilliseconds < 900) return;
            if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0) return;
            _lastProbeStartedUtc = DateTime.UtcNow;
            Task.Run(async () =>
            {
                try { await ProbeActiveShopAsync(snapshot).ConfigureAwait(false); }
                catch (Exception ex) { Log.ErrorWithMaxCount("活动店铺WebView探测失败: " + ex.Message, 20); }
                finally { Interlocked.Exchange(ref _probeRunning, 0); }
            });
        }

        private static async Task ProbeActiveShopAsync(IList<QN> qns)
        {
            var tasks = qns.Where(x => x != null && x.CDP != null && !string.IsNullOrWhiteSpace(Seller(x)))
                .Select(ProbeOneAsync).ToArray();
            if (tasks.Length == 0) return;
            var probes = (await Task.WhenAll(tasks).ConfigureAwait(false))
                .Where(x => x != null && x.Visible && x.Focused).ToList();

            if (probes.Count == 1)
            {
                var probe = probes[0];
                if (ActiveShopSessionRegistry.ActivateFromFocusedWebView(
                    probe.Qn, probe.SessionId, probe.TargetId, probe.Buyer, "document.hasFocus+visibility"))
                    EnsureQn(probe.Qn, true);
            }
            else if (probes.Count > 1)
            {
                Log.ErrorWithMaxCount("活动店铺WebView探测出现多个聚焦候选，保持上一次活动店铺避免串店: sellers="
                    + string.Join(",", probes.Select(x => x.Seller)), 20);
            }
            ActiveShopSessionRegistry.ReassertCurrent("webview-probe-complete");
        }

        private static async Task<ActiveShopProbe> ProbeOneAsync(QN qn)
        {
            try
            {
                var expectedSeller = Seller(qn);
                var expectedTargetId = qn.Seller == null ? string.Empty : (qn.Seller.TargetId ?? string.Empty).Trim();
                var cdp = qn.CDP;
                if (cdp == null || string.IsNullOrWhiteSpace(cdp.SessionId)) return null;
                var sessionId = cdp.SessionId;
                const string expression = "(function(){try{var l=(window._vs&&window._vs.loginID)?window._vs.loginID:null;var c=(window._vs&&window._vs.conversationID)?window._vs.conversationID:(window._conversationId||null);return {sellerNick:l&&l.nick?String(l.nick):'',targetId:l&&(l.targetId||l.targetid)?String(l.targetId||l.targetid):'',buyerNick:c&&c.nick?String(c.nick):'',focused:!!(document.hasFocus&&document.hasFocus()),hidden:!!document.hidden,visibilityState:String(document.visibilityState||'')};}catch(e){return {error:String(e)}}})()";
                var raw = await cdp.EvaluateExpressionAsync(expression, "ActiveShopWebViewProbe").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var jo = JObject.Parse(raw);
                var seller = Convert.ToString(jo["sellerNick"] ?? string.Empty).Trim();
                var targetId = Convert.ToString(jo["targetId"] ?? string.Empty).Trim();
                var buyer = Convert.ToString(jo["buyerNick"] ?? string.Empty).Trim();
                var focused = jo.Value<bool?>("focused") == true;
                var hidden = jo.Value<bool?>("hidden") == true;
                var visibility = Convert.ToString(jo["visibilityState"] ?? string.Empty).Trim();
                var visible = !hidden && !string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase);

                if (!string.Equals(seller, expectedSeller, StringComparison.Ordinal))
                {
                    Log.ErrorWithMaxCount("活动店铺探测拒绝：CDP登录seller与QN归属不一致: expected="
                        + expectedSeller + ", actual=" + seller + ", session=" + sessionId, 20);
                    return null;
                }
                if (expectedTargetId.Length > 0
                    && (targetId.Length == 0
                        || !string.Equals(expectedTargetId, targetId, StringComparison.Ordinal)))
                {
                    Log.ErrorWithMaxCount("活动店铺探测拒绝：物理CDP未提供匹配的TargetId，不能仅凭相同seller昵称授予活动身份: seller="
                        + seller + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId), 20);
                    return null;
                }
                return new ActiveShopProbe
                {
                    Qn = qn, Seller = seller, TargetId = targetId, Buyer = buyer,
                    SessionId = sessionId, Focused = focused, Visible = visible
                };
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("单店铺WebView探测失败: seller=" + Seller(qn) + ", " + ex.Message, 20);
                return null;
            }
        }

        private static void Subscribe(QN qn)
        {
            qn.EvSellerSwitched += Qn_EvSellerSwitched;
            qn.EvBuyerSwitched += Qn_EvBuyerSwitched;
            qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
            Log.Info("已订阅店铺运行时会话: seller=" + Seller(qn));
        }

        private static void Qn_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            ActiveShopSessionRegistry.ObserveChatDialogActive(qn, "onChatDlgActive");
            EnsureQn(qn, true);
            ActiveShopSessionRegistry.ReassertCurrent("seller-switched-complete");
        }

        private static void Qn_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            // onConversationChange may come from a background seller. It can refresh that QN's
            // buyer state, but it is not allowed to elect the global active seller.
            EnsureQn(qn, true);
            ActiveShopSessionRegistry.ReassertCurrent("buyer-switched-complete");
        }

        private static void Qn_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            EnsureQn(sender as QN, true);
            ActiveShopSessionRegistry.ReassertCurrent("message-complete");
        }

        private static void EnsureQn(QN qn, bool force)
        {
            if (qn == null) return;
            try
            {
                var seller = Seller(qn);
                var shopKey = ShopKey(qn);
                var desk = DeskSellerBindingRegistry.FindSellerDesk(seller, shopKey);
                if (qn.Rpa != null && ActiveShopSessionRegistry.IsActive(qn)
                    && (desk == null || DeskSellerBindingRegistry.IsSellerForDesk(desk, seller, shopKey)))
                    qn.Rpa.EnsureSellerDeskBinding(force);
                SyncAttachedUi(qn);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("校准店铺运行时会话失败: seller=" + Seller(qn) + ", " + ex.Message, 10);
            }
        }

        private static void SyncAttachedUi(QN qn)
        {
            var seller = Seller(qn);
            var shopKey = ShopKey(qn);
            // This method only refreshes the attached read-only UI. Do not require
            // native-window foreground/focus proof: the user may be interacting with
            // the assistant panel while the Qianniu Desk remains the same bound shop.
            if (seller.Length == 0 || shopKey.Length == 0
                || !ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(qn)) return;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller, shopKey);
            if (desk == null
                || !DeskSellerBindingRegistry.IsBoundToDeskForReadOnlyUi(desk, seller, shopKey)) return;
            var assist = desk.AssistWindow;
            if (assist == null || assist.Dispatcher == null) return;
            Action action = () =>
            {
                try
                {
                    var tab = assist.ctlRightPanel.GetTabItem(RightPanel.TabTypeEnum.Robot);
                    var robot = tab == null ? null : tab.Content as CtlRobot;
                    if (robot != null) robot.SynchronizeSellerSession(qn);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("同步店铺Bot界面失败: seller=" + seller + ", " + ex.Message, 10);
                }
            };
            if (assist.Dispatcher.CheckAccess()) action();
            else if (!assist.Dispatcher.HasShutdownStarted && !assist.Dispatcher.HasShutdownFinished)
                assist.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }

        private static string ShopKey(QN qn)
        {
            try { return qn == null ? string.Empty : (ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey ?? string.Empty).Trim(); }
            catch { return string.Empty; }
        }

        private static string Seller(QN qn)
        {
            return qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// Source of truth for the seller currently owning the visible Bot panel/native composer.
    /// Identity is ShopKey/TargetId + CDP session; the AliWorkbench HWND is only a shared container.
    /// </summary>
    internal static class ActiveShopSessionRegistry
    {
        private static readonly object Sync = new object();
        private static string _activeSeller = string.Empty;
        private static string _activeShopKey = string.Empty;
        private static string _activeTargetId = string.Empty;
        private static string _activeSessionId = string.Empty;
        private static DateTime _lastFocusProofUtc = DateTime.MinValue;
        private static readonly TimeSpan FocusProofMaxAge = TimeSpan.FromSeconds(5);

        private static bool HasFreshFocusProofLocked()
        {
            if (_lastFocusProofUtc == DateTime.MinValue) return false;
            var age = DateTime.UtcNow - _lastFocusProofUtc;
            return age >= TimeSpan.Zero && age <= FocusProofMaxAge;
        }

        internal static string GetActiveSellerNick()
        {
            lock (Sync) return _activeSeller;
        }

        internal static string GetActiveShopKey()
        {
            lock (Sync) return _activeShopKey;
        }

        internal static string GetActiveSessionId()
        {
            // Read under the same lock as activation/retirement so callers never
            // observe a partially updated authoritative session identifier.
            lock (Sync) return _activeSessionId;
        }

        internal static bool IsActive(QN qn)
        {
            if (qn == null || qn.Seller == null) return false;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var shopKey = ShopKey(qn);
            var sessionId = qn.CDP == null ? string.Empty : (qn.CDP.SessionId ?? string.Empty).Trim();
            if (!OwnsAuthoritativeSession(qn, shopKey, sessionId)) return false;
            lock (Sync)
            {
                return seller.Length > 0
                    && _activeShopKey.Length > 0
                    && shopKey.Length > 0
                    && string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    && string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                    && _activeSessionId.Length > 0
                    && sessionId.Length > 0
                    && string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)
                    && HasFreshFocusProofLocked();
            }
        }

        // Read-only UI surfaces may render the last authoritative conversation while the
        // embedded WebView is temporarily unable to prove document focus. Focus proof remains
        // mandatory for native send/automation paths through IsActive/ValidateNativeSend.
        internal static bool IsCurrentSessionForReadOnlyUi(QN qn)
        {
            if (qn == null || qn.Seller == null) return false;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var shopKey = ShopKey(qn);
            var sessionId = qn.CDP == null ? string.Empty : (qn.CDP.SessionId ?? string.Empty).Trim();
            if (!OwnsAuthoritativeSession(qn, shopKey, sessionId)) return false;
            lock (Sync)
            {
                return seller.Length > 0
                    && _activeShopKey.Length > 0
                    && shopKey.Length > 0
                    && string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    && string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                    && _activeSessionId.Length > 0
                    && sessionId.Length > 0
                    && string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
            }
        }

        internal static void ObserveChatDialogActive(QN qn, string evidence)
        {
            if (qn == null || qn.Seller == null) return;
            Activate(qn, qn.CDP == null ? string.Empty : qn.CDP.SessionId,
                qn.Seller.TargetId, false, evidence);
        }

        internal static bool ActivateFromFocusedWebView(QN qn, string sessionId, string targetId, string buyer, string evidence)
        {
            if (qn == null || qn.Seller == null) return false;
            var expectedTargetId = (qn.Seller.TargetId ?? string.Empty).Trim();
            targetId = (targetId ?? string.Empty).Trim();
            if (expectedTargetId.Length > 0
                && (targetId.Length == 0
                    || !string.Equals(expectedTargetId, targetId, StringComparison.Ordinal))) return false;
            return Activate(qn, sessionId, targetId, true,
                evidence + (string.IsNullOrWhiteSpace(buyer) ? string.Empty : ", buyer=" + buyer));
        }

        // A real Qianniu onConversationChange is generated by selecting a buyer conversation
        // inside the currently selected seller tab. The caller must supply the seller identity
        // from that event (not the cached QN nick), and the event must arrive through the exact
        // authoritative ShopKey/CDP session. This is stronger evidence than a background
        // onChatDlgActive notification, which deliberately remains observation-only.
        internal static bool ActivateFromVerifiedConversationChange(
            QN qn,
            string eventSellerNick,
            string eventTargetId,
            string sessionId,
            string buyer,
            string evidence)
        {
            if (qn == null || qn.Seller == null) return false;
            eventSellerNick = (eventSellerNick ?? string.Empty).Trim();
            var expectedSeller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (eventSellerNick.Length == 0
                || expectedSeller.Length == 0
                || !string.Equals(eventSellerNick, expectedSeller, StringComparison.Ordinal))
                return false;

            var expectedTargetId = (qn.Seller.TargetId ?? string.Empty).Trim();
            eventTargetId = (eventTargetId ?? string.Empty).Trim();
            if (expectedTargetId.Length > 0
                && (eventTargetId.Length == 0
                    || !string.Equals(expectedTargetId, eventTargetId, StringComparison.Ordinal)))
                return false;

            buyer = (buyer ?? string.Empty).Trim();
            if (buyer.Length == 0) return false;

            return Activate(qn, sessionId, eventTargetId, true,
                "verified-onConversationChange:" + (evidence ?? string.Empty) + ", buyer=" + buyer);
        }

        internal static bool ValidateNativeSend(QN qn, out string reason)
        {
            reason = string.Empty;
            if (qn == null || qn.Seller == null) { reason = "QN卖家身份为空"; return false; }
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var shopKey = ShopKey(qn);
            var sessionId = qn.CDP == null ? string.Empty : (qn.CDP.SessionId ?? string.Empty).Trim();
            if (!OwnsAuthoritativeSession(qn, shopKey, sessionId))
            { reason = "CDP会话已失效或不再拥有该店铺"; return false; }
            lock (Sync)
            {
                if (_activeSeller.Length == 0) { reason = "尚未确认当前活动店铺"; return false; }
                if (!string.Equals(_activeSeller, seller, StringComparison.Ordinal))
                { reason = "活动seller不一致: active=" + _activeSeller + ", target=" + seller; return false; }
                if (_activeShopKey.Length == 0 || shopKey.Length == 0
                    || !string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal))
                { reason = "ShopKey缺失或不一致"; return false; }
                if (!HasFreshFocusProofLocked())
                { reason = "最近5秒内没有唯一聚焦WebView证明，已阻止共享千牛窗口发送"; return false; }
                if (_activeSessionId.Length == 0 || sessionId.Length == 0
                    || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
                { reason = "CDP会话不一致或缺失: activeSession=" + _activeSessionId + ", targetSession=" + sessionId; return false; }
                return true;
            }
        }

        internal static void ReassertCurrent(string reason)
        {
            string seller;
            string shopKey;
            string sessionId;
            lock (Sync)
            {
                seller = _activeSeller;
                shopKey = _activeShopKey;
                sessionId = _activeSessionId;
            }
            // Seller nickname is not an identity key. If an old or incomplete
            // activation has no ShopKey, fail closed instead of selecting an
            // arbitrary QN wrapper with the same visible nickname.
            if (seller.Length == 0 || shopKey.Length == 0) return;
            var qn = ShopContextLocator.FindConnectedQnByShopKey(shopKey, sessionId);
            var current = QN.CurQN;
            if (ReferenceEquals(qn, current)) return;
            lock (Sync)
            {
                if (!string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                    || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)
                    || !ReferenceEquals(QN.CurQN, current))
                    return;

                if (qn == null)
                {
                    QN.CurQN = null;
                    Log.Info("活动ShopKey尚无匹配CDP会话，已清除过期全局QN引用: activeShopKey="
                        + shopKey + ", reason=" + (reason ?? string.Empty));
                }
                else
                {
                    QN.CurQN = qn;
                    Log.Info("已按ShopKey/CDP会话纠正全局当前店铺，阻止后台WebView抢占: seller="
                        + seller + ", shopKey=" + shopKey + ", reason=" + (reason ?? string.Empty));
                }
            }
            // Reasserting the global QN pointer is not new foreground evidence. Only the
            // focused-WebView probe may update the native Desk's active ShopKey.
        }

        private static bool Activate(QN qn, string sessionId, string targetId, bool focusProof, string evidence)
        {
            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return false;
            var shopKey = ShopKey(qn);
            if (shopKey.Length == 0) return false;
            targetId = (targetId ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (!OwnsAuthoritativeSession(qn, shopKey, sessionId)) return false;

            string previousSeller;
            string previousShopKey;
            bool changed;
            lock (Sync)
            {
                // Evaluate background events and update ownership under the same lock.
                // Only fresh focus evidence may replace an already elected session.
                if (!focusProof && _activeShopKey.Length > 0
                    && (!string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                        || !string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                        || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)))
                    return false;

                previousSeller = _activeSeller;
                previousShopKey = _activeShopKey;
                changed = !string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    || !string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                    || (_activeSessionId.Length > 0 && sessionId.Length > 0
                        && !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal));
                _activeSeller = seller;
                _activeShopKey = shopKey;
                _activeTargetId = targetId.Length > 0 ? targetId : (qn.Seller.TargetId ?? string.Empty).Trim();
                _activeSessionId = sessionId;
                if (focusProof) _lastFocusProofUtc = DateTime.UtcNow;
                else if (changed) _lastFocusProofUtc = DateTime.MinValue;
                QN.CurQN = qn;
            }
            if (focusProof)
                DeskSellerBindingRegistry.BindForegroundSeller(qn, "active-shop:" + (evidence ?? string.Empty));
            if (changed)
            {
                Log.Info("活动店铺会话已切换: previousSeller=" + previousSeller + ", previousShopKey=" + previousShopKey
                    + ", seller=" + seller + ", shopKey=" + shopKey + ", targetId=" + _activeTargetId
                    + ", session=" + sessionId + ", focusProof=" + focusProof + ", evidence=" + (evidence ?? string.Empty));
            }
            return true;
        }

        private static bool OwnsAuthoritativeSession(QN qn, string shopKey, string sessionId)
        {
            var cdp = qn.CDP;
            var server = MyWebSocketServer.WSocketSvrInst;
            return sessionId.Length > 0 && cdp != null && !cdp.IsInvalidated && !cdp.IsShopIdentityRetired
                && string.Equals(cdp.SessionId, sessionId, StringComparison.Ordinal)
                && server != null && server.IsAuthoritativeShopSession(shopKey, sessionId);
        }

        private static string ShopKey(QN qn)
        {
            try
            {
                return qn == null || qn.Seller == null ? string.Empty
                    : (ShopIdentityResolver.Resolve(qn.Seller).ShopKey ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }
    }
}
