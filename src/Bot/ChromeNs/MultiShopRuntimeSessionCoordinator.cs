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

        private static readonly ConcurrentDictionary<QN, byte> Subscribed = new ConcurrentDictionary<QN, byte>();
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
            if (shop == null || string.IsNullOrWhiteSpace(shop.DisplayName)) return false;
            var qn = QN.FindExistingBySellerNick(shop.DisplayName);
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
                    if (Subscribed.TryAdd(qn, 0)) Subscribe(qn);
                    var seller = Seller(qn);
                    var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
                    if (qn.Rpa != null && ActiveShopSessionRegistry.IsActive(qn)
                        && (desk == null || DeskSellerBindingRegistry.IsSellerForDesk(desk, seller)))
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
                {
                    var desk = DeskSellerBindingRegistry.FindSellerDesk(probe.Seller);
                    if (desk == null) desk = DeskSellerBindingRegistry.BindForegroundSeller(probe.Qn, "focused-webview");
                    if (desk != null) DeskSellerBindingRegistry.MarkActiveSeller(desk, probe.Seller, "focused-webview");
                    EnsureQn(probe.Qn, true);
                }
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
                if (expectedTargetId.Length > 0 && targetId.Length > 0
                    && !string.Equals(expectedTargetId, targetId, StringComparison.Ordinal))
                {
                    Log.ErrorWithMaxCount("活动店铺探测拒绝：TargetId与QN归属不一致: seller=" + seller
                        + ", session=" + sessionId, 20);
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
            if (ActiveShopSessionRegistry.IsActive(qn))
            {
                var desk = DeskSellerBindingRegistry.BindForegroundSeller(qn, "seller-switched-active");
                if (desk != null) DeskSellerBindingRegistry.MarkActiveSeller(desk, Seller(qn), "seller-switched-active");
            }
            EnsureQn(qn, true);
            ActiveShopSessionRegistry.ReassertCurrent("seller-switched-complete");
        }

        private static void Qn_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            // onConversationChange may come from a background seller. It can refresh that QN's
            // buyer state, but it is not allowed to elect the global active seller.
            if (ActiveShopSessionRegistry.IsActive(qn))
            {
                var desk = DeskSellerBindingRegistry.FindSellerDesk(Seller(qn));
                if (desk == null) desk = DeskSellerBindingRegistry.BindForegroundSeller(qn, "buyer-switched-active");
                if (desk != null) DeskSellerBindingRegistry.MarkActiveSeller(desk, Seller(qn), "buyer-switched-active");
            }
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
                var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
                if (qn.Rpa != null && ActiveShopSessionRegistry.IsActive(qn)
                    && (desk == null || DeskSellerBindingRegistry.IsSellerForDesk(desk, seller)))
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
            if (seller.Length == 0 || !ActiveShopSessionRegistry.IsActive(qn)) return;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk == null || !DeskSellerBindingRegistry.IsSellerForDesk(desk, seller)) return;
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

        internal static string GetActiveSellerNick()
        {
            lock (Sync) return _activeSeller;
        }

        internal static bool IsActive(QN qn)
        {
            if (qn == null || qn.Seller == null) return false;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var shopKey = ShopKey(qn);
            lock (Sync)
            {
                return seller.Length > 0
                    && string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    && (_activeShopKey.Length == 0 || shopKey.Length == 0
                        || string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal));
            }
        }

        internal static void ObserveChatDialogActive(QN qn, string evidence)
        {
            if (qn == null || qn.Seller == null) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            bool allow;
            lock (Sync)
            {
                allow = _activeSeller.Length == 0
                    || string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    || (DateTime.UtcNow - _lastFocusProofUtc).TotalSeconds > 3;
            }
            if (!allow)
            {
                Log.Info("忽略后台/迟到的onChatDlgActive，不覆盖最近聚焦确认的店铺: candidate="
                    + seller + ", activeSeller=" + GetActiveSellerNick());
                return;
            }
            Activate(qn, qn.CDP == null ? string.Empty : qn.CDP.SessionId,
                qn.Seller.TargetId, false, evidence);
        }

        internal static bool ActivateFromFocusedWebView(QN qn, string sessionId, string targetId, string buyer, string evidence)
        {
            if (qn == null || qn.Seller == null) return false;
            var expectedTargetId = (qn.Seller.TargetId ?? string.Empty).Trim();
            targetId = (targetId ?? string.Empty).Trim();
            if (expectedTargetId.Length > 0 && targetId.Length > 0
                && !string.Equals(expectedTargetId, targetId, StringComparison.Ordinal)) return false;
            return Activate(qn, sessionId, targetId, true,
                evidence + (string.IsNullOrWhiteSpace(buyer) ? string.Empty : ", buyer=" + buyer));
        }

        internal static bool ValidateNativeSend(QN qn, out string reason)
        {
            reason = string.Empty;
            if (qn == null || qn.Seller == null) { reason = "QN卖家身份为空"; return false; }
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var shopKey = ShopKey(qn);
            var sessionId = qn.CDP == null ? string.Empty : (qn.CDP.SessionId ?? string.Empty).Trim();
            lock (Sync)
            {
                if (_activeSeller.Length == 0) { reason = "尚未确认当前活动店铺"; return false; }
                if (!string.Equals(_activeSeller, seller, StringComparison.Ordinal))
                { reason = "活动seller不一致: active=" + _activeSeller + ", target=" + seller; return false; }
                if (_activeShopKey.Length > 0 && shopKey.Length > 0
                    && !string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal))
                { reason = "ShopKey不一致"; return false; }
                if (_activeSessionId.Length > 0 && sessionId.Length > 0
                    && !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
                { reason = "CDP会话不一致: activeSession=" + _activeSessionId + ", targetSession=" + sessionId; return false; }
                return true;
            }
        }

        internal static void ReassertCurrent(string reason)
        {
            string seller;
            lock (Sync) seller = _activeSeller;
            if (seller.Length == 0) return;
            var qn = QN.FindExistingBySellerNick(seller);
            if (qn == null) return;
            if (!ReferenceEquals(QN.CurQN, qn))
            {
                QN.CurQN = qn;
                Log.Info("已纠正全局当前店铺，阻止后台WebView抢占: seller=" + seller + ", reason=" + (reason ?? string.Empty));
            }
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk != null) DeskSellerBindingRegistry.MarkActiveSeller(desk, seller, "active-shop-reassert:" + (reason ?? string.Empty));
        }

        private static bool Activate(QN qn, string sessionId, string targetId, bool focusProof, string evidence)
        {
            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return false;
            var shopKey = ShopKey(qn);
            targetId = (targetId ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            string previousSeller;
            string previousShopKey;
            bool changed;
            lock (Sync)
            {
                previousSeller = _activeSeller;
                previousShopKey = _activeShopKey;
                changed = !string.Equals(_activeSeller, seller, StringComparison.Ordinal)
                    || !string.Equals(_activeShopKey, shopKey, StringComparison.Ordinal)
                    || (_activeSessionId.Length > 0 && sessionId.Length > 0
                        && !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal));
                _activeSeller = seller;
                _activeShopKey = shopKey;
                _activeTargetId = targetId.Length > 0 ? targetId : (qn.Seller.TargetId ?? string.Empty).Trim();
                if (sessionId.Length > 0) _activeSessionId = sessionId;
                if (focusProof) _lastFocusProofUtc = DateTime.UtcNow;
            }
            QN.CurQN = qn;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk == null) desk = DeskSellerBindingRegistry.BindForegroundSeller(qn, "active-shop:" + (evidence ?? string.Empty));
            if (desk != null) DeskSellerBindingRegistry.MarkActiveSeller(desk, seller, "active-shop:" + (evidence ?? string.Empty));
            if (changed)
            {
                Log.Info("活动店铺会话已切换: previousSeller=" + previousSeller + ", previousShopKey=" + previousShopKey
                    + ", seller=" + seller + ", shopKey=" + shopKey + ", targetId=" + _activeTargetId
                    + ", session=" + sessionId + ", focusProof=" + focusProof + ", evidence=" + (evidence ?? string.Empty));
            }
            return true;
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
