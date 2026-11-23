using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using BotLib.Extensions;
using Bot.Automation.ChatDeskNs;
using Bot.Common;
using System.Reflection;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Web;
using DbEntity;
using Newtonsoft.Json;
using BotLib.Misc;
using DbEntity.Response;
using SuperWebSocket;
using Bot.ChatRecord;
using Bot.ShopScope;

namespace Bot.ChromeNs
{
    public class CDPClient
    {
        public event EventHandler<BuyerSwitchedEventArgs> EvBuyerSwitched;
        public event EventHandler<SellerSwitchedEventArgs> EvSellerSwitched;
        public event EventHandler<MessageNotifyEventArgs> EvMessageNotity;
        public event EventHandler<RecieveNewMessageEventArgs> EvRecieveNewMessage;
        public event EventHandler<ShopRobotReceriveNewMessageEventArgs> EvShopRobotReceriveNewMessage;

        // One global WebSocket dispatcher is enough for the entire Bot process. The historical
        // implementation subscribed one bound handler per CDPClient and never unsubscribed it when
        // a Qianniu WebView session closed. After hours of WebView churn, every inbound event was
        // fanned out through all dead CDPClient instances. Keep only weak session references so a
        // closed session can be collected as soon as MyWebSocketServer releases it.
        private static readonly ConcurrentDictionary<string, WeakReference> SessionClients =
            new ConcurrentDictionary<string, WeakReference>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> PreferredShopSessions =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly object PreferredShopSessionSync = new object();
        private static readonly AsyncLocal<string> ForwardedInboundSourceSession = new AsyncLocal<string>();
        private static int _dispatcherInstalled;

        // The execute protocol has no request id, therefore each physical WebSocket session remains
        // strictly single-flight. TaskCompletionSource avoids consuming a ThreadPool worker for the
        // entire response timeout. The serial gate itself is also bounded: otherwise a burst of
        // current-buyer/status probes can wait behind several 8s calls and silently turn one send
        // into a multi-minute operation before its own timeout even starts.
        private readonly ConcurrentQueue<TaskCompletionSource<string>> _requestWaiters =
            new ConcurrentQueue<TaskCompletionSource<string>>();
        private readonly SemaphoreSlim _executeGate = new SemaphoreSlim(1, 1);
        private WebSocketSession _webSocketSession;
        private const int InvokeTimeoutMs = 8000;
        private const int ExecuteGateWaitTimeoutMs = 1500;
        // History reads are asynchronous and user-visible. Let them wait behind one
        // in-flight CDP command; all ordinary probes/sends keep the short fail-fast gate.
        private const int HistoryGateWaitTimeoutMs = 10000;
        private int _sessionInvalidated;
        private string _shopKey = string.Empty;
        private int _shopIdentityRetired;
        public string Nick { get; set; }
        internal string ShopKey
        {
            get { return Volatile.Read(ref _shopKey); }
            set { Volatile.Write(ref _shopKey, value ?? string.Empty); }
        }
        internal bool IsShopIdentityRetired
        {
            get { return Volatile.Read(ref _shopIdentityRetired) != 0; }
            set { Volatile.Write(ref _shopIdentityRetired, value ? 1 : 0); }
        }

        private sealed class ForwardedInboundScope : IDisposable
        {
            private readonly string _previous;
            private int _disposed;

            public ForwardedInboundScope(string sourceSession)
            {
                _previous = ForwardedInboundSourceSession.Value;
                ForwardedInboundSourceSession.Value = (sourceSession ?? string.Empty).Trim();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                ForwardedInboundSourceSession.Value = _previous;
            }
        }

        internal static IDisposable BeginForwardedInbound(string sourceSession)
        {
            return new ForwardedInboundScope(sourceSession);
        }

        internal string SessionId
        {
            get { return _webSocketSession == null ? string.Empty : _webSocketSession.SessionID; }
        }

        internal bool IsInvalidated
        {
            get { return Volatile.Read(ref _sessionInvalidated) != 0; }
        }

        public CDPClient(WebSocketSession session)
        {
            _webSocketSession = session;
            var sessionId = SessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                SessionClients[sessionId] = new WeakReference(this);
            }
            EnsureDispatcherInstalled();
        }

        private static void EnsureDispatcherInstalled()
        {
            if (Interlocked.Exchange(ref _dispatcherInstalled, 1) != 0) return;
            MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += DispatchWebSocketMessage;
            Log.Info("CDP WebSocket事件已切换为单一会话路由器：关闭页面不再累积事件订阅。" );
        }

        private static void DispatchWebSocketMessage(object sender, WSocketNewMessageEventArgs e)
        {
            var session = sender as WebSocketSession;
            if (session == null || e == null || string.IsNullOrWhiteSpace(session.SessionID)) return;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null || !server.IsWebSocketSessionConnected(session.SessionID)) return;

            WeakReference weak;
            if (!SessionClients.TryGetValue(session.SessionID, out weak) || weak == null) return;
            var client = weak.Target as CDPClient;
            if (client == null)
            {
                WeakReference ignored;
                SessionClients.TryRemove(session.SessionID, out ignored);
                return;
            }
            client.OnWSocketRecieveMessage(session, e);
        }

        internal static bool TryGetBySessionId(string sessionId, out CDPClient client)
        {
            client = null;
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return false;
            WeakReference weak;
            if (!SessionClients.TryGetValue(sessionId, out weak) || weak == null) return false;
            client = weak.Target as CDPClient;
            if (client != null && !client.IsInvalidated) return true;
            WeakReference ignored;
            SessionClients.TryRemove(sessionId, out ignored);
            client = null;
            return false;
        }

        internal static void ReleaseClosedSession(string sessionId, string reason, CDPClient knownClient)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;

            var client = knownClient;
            WeakReference weak;
            if (SessionClients.TryRemove(sessionId, out weak) && client == null && weak != null)
            {
                client = weak.Target as CDPClient;
            }

            lock (PreferredShopSessionSync)
            {
                foreach (var pair in PreferredShopSessions
                    .Where(x => string.Equals(x.Value, sessionId, StringComparison.Ordinal))
                    .ToList())
                {
                    string ignored;
                    PreferredShopSessions.TryRemove(pair.Key, out ignored);
                    Log.Info("已释放关闭WebSocket对应的CDP活动会话偏好: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", pair.Key)
                        + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId));
                }
            }

            if (client != null)
            {
                client.MarkSessionClosed(sessionId, reason);
            }
        }

        private void MarkSessionClosed(string sessionId, string reason)
        {
            var first = Interlocked.Exchange(ref _sessionInvalidated, 1) == 0;
            CancelPendingWaiters();
            var socket = _webSocketSession;
            if (socket != null && string.Equals(socket.SessionID, sessionId, StringComparison.Ordinal))
            {
                _webSocketSession = null;
            }
            if (first)
            {
                Log.Info("CDP客户端已随WebSocket关闭立即失效并清理等待请求: session=" + sessionId
                    + ", reason=" + (reason ?? string.Empty));
            }
        }

        private void CancelPendingWaiters()
        {
            TaskCompletionSource<string> waiter;
            while (_requestWaiters.TryDequeue(out waiter))
            {
                if (waiter != null) waiter.TrySetCanceled();
            }
        }

        internal static void ReleasePreferredRuntimeSession(string shopKey, string sessionId)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || sessionId.Length == 0) return;
            lock (PreferredShopSessionSync)
            {
                string current;
                if (PreferredShopSessions.TryGetValue(shopKey, out current)
                    && string.Equals(current, sessionId, StringComparison.Ordinal))
                {
                    string ignored;
                    PreferredShopSessions.TryRemove(shopKey, out ignored);
                }
            }
        }

        internal static void PreferRuntimeSession(string shopKey, string sellerNick, string sessionId, string buyerNick, string reason)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            sellerNick = (sellerNick ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            buyerNick = (buyerNick ?? string.Empty).Trim();
            if (shopKey.Length == 0 || sessionId.Length == 0) return;

            CDPClient client;
            if (!TryGetBySessionId(sessionId, out client)) return;
            client.ShopKey = shopKey;
            client.IsShopIdentityRetired = false;
            client.Nick = sellerNick;

            string previous = string.Empty;
            var changed = false;
            lock (PreferredShopSessionSync)
            {
                PreferredShopSessions.TryGetValue(shopKey, out previous);
                if (!string.Equals(previous, sessionId, StringComparison.Ordinal))
                {
                    PreferredShopSessions[shopKey] = sessionId;
                    changed = true;
                }
            }

            if (buyerNick.Length > 0)
                BotConnectionDiagnostics.RecordBuyerSeller(sellerNick, buyerNick);
            if (changed)
            {
                Log.Info("检测到真实会话切换，CDP命令已跟随ShopKey对应的活动千牛WebView: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                    + ", sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", sellerNick)
                    + ", buyerRef=" + MyWebSocketServer.DiagnosticRef("buyer", buyerNick)
                    + ", previousSessionRef=" + MyWebSocketServer.DiagnosticRef("session", previous)
                    + ", currentSessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId)
                    + ", reason=" + (reason ?? string.Empty));
            }
        }

        private CDPClient ResolvePreferredRuntimeClient()
        {
            var shopKey = (ShopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return null;

            string preferredSession;
            if (!PreferredShopSessions.TryGetValue(shopKey, out preferredSession)
                || string.IsNullOrWhiteSpace(preferredSession)
                || string.Equals(preferredSession, SessionId, StringComparison.Ordinal))
            {
                return null;
            }

            CDPClient preferred;
            if (TryGetBySessionId(preferredSession, out preferred)) return preferred;

            lock (PreferredShopSessionSync)
            {
                string current;
                if (PreferredShopSessions.TryGetValue(shopKey, out current)
                    && string.Equals(current, preferredSession, StringComparison.Ordinal))
                {
                    string ignored;
                    PreferredShopSessions.TryRemove(shopKey, out ignored);
                }
            }
            return null;
        }

        private void OnWSocketRecieveMessage(WebSocketSession session, WSocketNewMessageEventArgs e)
        {
            if (session == null || _webSocketSession == null || session.SessionID != _webSocketSession.SessionID) return;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (IsInvalidated || server == null || !server.IsWebSocketSessionConnected(session.SessionID)) return;

            var response = e.Value ?? string.Empty;

            // execute 响应即使为空也必须完成等待任务，否则非聊天 WebView 上的初始化会卡到超时。
            if (e.Type == "execute")
            {
                TaskCompletionSource<string> waiter;
                if (_requestWaiters.TryDequeue(out waiter) && waiter != null)
                {
                    waiter.TrySetResult(response);
                }
                return;
            }

            DuplicateCdpInboundRecoveryBridge.TryDispatchRawInbound(this, e.Type, response);
        }

        // The raw WebSocket dispatcher is a separate path from duplicate-page recovery.
        // It must enforce the same ShopKey authority so a retired physical socket cannot
        // deliver directly to its former QN while the identity-transition queue is drained.
        // qnbotStatus remains available on standby/retired pages because it is needed to
        // discover and rebind the new login identity.
        private bool HasCurrentShopAuthority()
        {
            var sessionId = SessionId;
            if (string.IsNullOrWhiteSpace(sessionId)
                || IsInvalidated
                || IsShopIdentityRetired
                || DuplicateCdpInboundRecoveryBridge.IsSessionIdentityTransitioning(sessionId))
                return false;

            var shopKey = (ShopKey ?? string.Empty).Trim();
            var server = MyWebSocketServer.WSocketSvrInst;
            return shopKey.Length > 0
                && server != null
                && server.IsWebSocketSessionConnected(sessionId)
                && server.IsAuthoritativeShopSession(shopKey, sessionId);
        }

        private bool CanDispatchInboundEvent(string type)
        {
            var sessionId = SessionId;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (string.IsNullOrWhiteSpace(sessionId)
                || server == null
                || !server.IsWebSocketSessionConnected(sessionId))
                return false;
            if (string.Equals(type, "qnbotStatus", StringComparison.Ordinal)) return true;
            return HasCurrentShopAuthority();
        }

        internal void DispatchInboundEvent(string type, string response)
        {
            if (string.IsNullOrEmpty(response)) return;
            if (!CanDispatchInboundEvent(type)) return;
            if (type == "receiveNewMsg")
            {
                RecieveNewMessage(response);
            }
            else if (type == "onConversationChange")
            {
                BuyerSwitched(response);
            }
            else if (type == "onShopRobotReceriveNewMsgs")
            {
                ShopRobotReceriveNewMessage(response);
            }
            else if (type == "onChatDlgActive")
            {
                SellerSwitched(response);
            }
            else if (type == "messageCenterNotify")
            {
                BenchMessageNotify(response);
            }
            else if (type == "qnbotStatus")
            {
                Log.Info("千牛注入状态: " + response);
                RepairDuplicateStatusDiagnostics(response);
            }
        }

        private void RepairDuplicateStatusDiagnostics(string response)
        {
            try
            {
                var jo = JObject.Parse(response ?? "{}");
                var sellerNick = Convert.ToString(jo["loginNick"] ?? string.Empty).Trim();
                var shopKey = (ShopKey ?? string.Empty).Trim();
                if (shopKey.Length == 0) return;
                var qn = QN.FindExistingByShopKey(shopKey);
                if (qn == null) return;

                string effectiveSession;
                if (!PreferredShopSessions.TryGetValue(shopKey, out effectiveSession)
                    || string.IsNullOrWhiteSpace(effectiveSession))
                {
                    effectiveSession = qn.CDP == null ? string.Empty : qn.CDP.SessionId;
                }
                if (string.IsNullOrWhiteSpace(effectiveSession)
                    || string.Equals(effectiveSession, SessionId, StringComparison.Ordinal))
                {
                    return;
                }

                var logicalBuyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
                BotConnectionDiagnostics.RecordBuyerSeller(sellerNick, logicalBuyer);
            }
            catch
            {
            }
        }

        private async Task<string> SendExecuteAndWaitAsync(
            string cmd, string desc, bool allowRetiredIdentityRead = false,
            int gateWaitTimeoutMs = ExecuteGateWaitTimeoutMs,
            int responseTimeoutMs = InvokeTimeoutMs)
        {
            if (IsShopIdentityRetired && !allowRetiredIdentityRead)
            {
                Log.Info("CDP调用已阻止：当前WebSocket登录身份已变化，等待ShopKey重新绑定。desc="
                    + desc + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                return string.Empty;
            }

            // Identity discovery must execute on this physical page, never on the old
            // preferred page; it is how a switched account is safely rebound.
            if (!allowRetiredIdentityRead)
            {
                var preferred = ResolvePreferredRuntimeClient();
                if (preferred != null && !ReferenceEquals(preferred, this))
                    return await preferred.SendExecuteAndWaitCoreAsync(
                        cmd, desc + "@runtime-active-session", allowRetiredIdentityRead,
                        gateWaitTimeoutMs, responseTimeoutMs).ConfigureAwait(false);
            }
            return await SendExecuteAndWaitCoreAsync(
                cmd, desc, allowRetiredIdentityRead, gateWaitTimeoutMs, responseTimeoutMs).ConfigureAwait(false);
        }

        private async Task<string> SendExecuteAndWaitCoreAsync(
            string cmd, string desc, bool allowRetiredIdentityRead = false,
            int gateWaitTimeoutMs = ExecuteGateWaitTimeoutMs,
            int responseTimeoutMs = InvokeTimeoutMs)
        {
            var gateAcquired = false;
            var effectiveGateWaitTimeoutMs = Math.Max(
                ExecuteGateWaitTimeoutMs,
                Math.Min(HistoryGateWaitTimeoutMs, gateWaitTimeoutMs));
            try
            {
                gateAcquired = await _executeGate.WaitAsync(effectiveGateWaitTimeoutMs).ConfigureAwait(false);
                if (!gateAcquired)
                {
                    Log.ErrorWithMaxCount(
                        "CDP调用等待串行门超时，已快速失败避免排队放大: desc=" + desc
                        + ", timeoutMs=" + effectiveGateWaitTimeoutMs
                        + ", session=" + SessionId,
                        100);
                    return string.Empty;
                }

                if (IsInvalidated || _webSocketSession == null)
                {
                    Log.Info("CDP调用已跳过：会话已失效，等待注入脚本重连。desc=" + desc + ", session=" + SessionId);
                    return string.Empty;
                }
                if (IsShopIdentityRetired && !allowRetiredIdentityRead)
                {
                    Log.Info("CDP调用已阻止：登录身份已变化，禁止旧ShopKey继续发送。desc="
                        + desc + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                    return string.Empty;
                }
                if (!allowRetiredIdentityRead && !HasCurrentShopAuthority())
                {
                    Log.Info("CDP业务命令已阻止：当前WebSocket不再拥有该ShopKey权威身份。desc="
                        + desc + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", ShopKey)
                        + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                    return string.Empty;
                }

                // 当前注入协议的 execute 响应没有请求 ID，只能保证同一 WebSocket 会话同时存在
                // 一个等待请求。旧实现允许多个异步调用并发时还会为每个等待额外占用线程池线程；
                // 现在清理极端超时竞争留下的旧 waiter，但不再阻塞 ThreadPool 线程。
                TaskCompletionSource<string> staleWaiter;
                while (_requestWaiters.TryDequeue(out staleWaiter))
                {
                    if (staleWaiter != null) staleWaiter.TrySetCanceled();
                }

                var requestCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _requestWaiters.Enqueue(requestCompletion);
                try
                {
                    _webSocketSession.Send(JsonConvert.SerializeObject(new { method = "execute", expression = cmd }));
                }
                catch (Exception ex)
                {
                    TaskCompletionSource<string> droppedOnSend;
                    _requestWaiters.TryDequeue(out droppedOnSend);
                    if (droppedOnSend != null) droppedOnSend.TrySetCanceled();
                    InvalidateSession("发送execute请求失败: " + ex.Message);
                    return string.Empty;
                }

                var safeResponseTimeoutMs = Math.Max(InvokeTimeoutMs, Math.Min(30000, responseTimeoutMs));
                var timeoutTask = Task.Delay(safeResponseTimeoutMs);
                var completed = await Task.WhenAny(requestCompletion.Task, timeoutTask).ConfigureAwait(false);
                if (completed != requestCompletion.Task && !requestCompletion.Task.IsCompleted)
                {
                    TaskCompletionSource<string> dropped;
                    _requestWaiters.TryDequeue(out dropped);
                    requestCompletion.TrySetCanceled();
                    Log.Error("CDP调用超时: " + desc + ", session=" + SessionId);
                    InvalidateSession("调用超时: " + desc);
                    return string.Empty;
                }

                try
                {
                    return (await requestCompletion.Task.ConfigureAwait(false)) ?? string.Empty;
                }
                catch (TaskCanceledException)
                {
                    return string.Empty;
                }
            }
            finally
            {
                if (gateAcquired) _executeGate.Release();
            }
        }

        internal Task<string> EvaluateExpressionAsync(string expression, string description)
        {
            return EvaluateExpressionAsync(expression, description, InvokeTimeoutMs);
        }

        internal Task<string> EvaluateExpressionAsync(
            string expression,
            string description,
            int responseTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(expression)) return Task.FromResult(string.Empty);
            return SendExecuteAndWaitAsync(
                expression,
                string.IsNullOrWhiteSpace(description) ? "EvaluateExpression" : description,
                responseTimeoutMs: responseTimeoutMs);
        }

        private string SendExecuteAndWait(string cmd, string desc)
        {
            try
            {
                return SendExecuteAndWaitAsync(cmd, desc).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Info("CDP同步调用失败: desc=" + desc + ", error=" + ex.Message);
                return string.Empty;
            }
        }

        private void InvalidateSession(string reason)
        {
            if (Interlocked.Exchange(ref _sessionInvalidated, 1) != 0) return;

            var sessionId = SessionId;
            WeakReference ignoredClient;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                SessionClients.TryRemove(sessionId, out ignoredClient);
                lock (PreferredShopSessionSync)
                {
                    foreach (var pair in PreferredShopSessions
                        .Where(x => string.Equals(x.Value, sessionId, StringComparison.Ordinal))
                        .ToList())
                    {
                        string ignored;
                        PreferredShopSessions.TryRemove(pair.Key, out ignored);
                        Log.Info("活动CDP会话失效，已撤销ShopKey会话偏好并回退权威通道: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", pair.Key)
                            + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId));
                    }
                }
            }

            CancelPendingWaiters();

            Log.Error("CDP会话已失效并请求WebSocket重连: session=" + sessionId + ", reason=" + (reason ?? string.Empty));
            BotConnectionDiagnostics.RecordCdpStatus(false, "CDP会话已失效，等待注入重连", Nick, string.Empty);
            try
            {
                var socket = _webSocketSession;
                _webSocketSession = null;
                if (socket != null) socket.Close();
            }
            catch (Exception ex)
            {
                Log.Info("关闭失效CDP WebSocket会话失败: " + ex.Message);
            }
        }

        private void BenchMessageNotify(string response)
        {
            if (EvMessageNotity != null)
            {
                EvMessageNotity(this, new MessageNotifyEventArgs
                {
                    NotifyContent = response
                });
            }
        }

        private void ShopRobotReceriveNewMessage(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
            if (localUser == null) return;
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, new ShopRobotReceriveNewMessageEventArgs
                {
                    Buyer = localUser.Conversation,
                    Seller = localUser.LoginID
                });
            }
        }

        private void RecieveNewMessage(string msg)
        {
            if (EvRecieveNewMessage != null)
            {
                EvRecieveNewMessage(this, new RecieveNewMessageEventArgs
                {
                    Buyer = string.Empty,
                    Message = msg
                });
            }
        }

        private void BuyerSwitched(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
            if (localUser == null) return;

            var sellerNick = localUser.LoginID == null ? string.Empty : (localUser.LoginID.Nick ?? string.Empty).Trim();
            var buyerNick = localUser.Conversation == null ? string.Empty : (localUser.Conversation.Nick ?? string.Empty).Trim();
            string shopKey = string.Empty;
            try
            {
                if (localUser.LoginID != null)
                    shopKey = ShopContextLocator.ResolveRuntimeBySellerIdentity(localUser.LoginID).ShopKey;
            }
            catch { }
            var server = MyWebSocketServer.WSocketSvrInst;
            var sessionShopKey = (ShopKey ?? string.Empty).Trim();
            if (server == null || shopKey.Length == 0
                || !string.Equals(sessionShopKey, shopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shopKey, SessionId))
            {
                if (sessionShopKey.Length > 0
                    && !string.Equals(sessionShopKey, shopKey, StringComparison.Ordinal))
                    IsShopIdentityRetired = true;
                Log.Info("千牛会话身份与当前CDP绑定不一致，已拒绝修改旧QN身份，不接管CDP命令路由: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                    + ", boundShopRef=" + MyWebSocketServer.DiagnosticRef("shop", sessionShopKey)
                    + ", sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", sellerNick)
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                return;
            }

            PreferRuntimeSession(shopKey, sellerNick, SessionId, buyerNick, "onConversationChange");

            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, new BuyerSwitchedEventArgs
                {
                    Seller = localUser.LoginID,
                    Buyer = localUser.Conversation
                });
            }
        }

        private void SellerSwitched(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
            if (localUser == null || localUser.LoginID == null || IsInvalidated || IsShopIdentityRetired) return;

            var sellerNick = (localUser.LoginID.Nick ?? string.Empty).Trim();
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(localUser.LoginID); }
            catch { return; }
            var server = MyWebSocketServer.WSocketSvrInst;
            var boundShopKey = (ShopKey ?? string.Empty).Trim();
            if (server == null || shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(boundShopKey, shop.ShopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shop.ShopKey, SessionId))
            {
                if (boundShopKey.Length > 0 && shop != null
                    && !string.Equals(boundShopKey, shop.ShopKey, StringComparison.Ordinal))
                    IsShopIdentityRetired = true;
                Log.Info("已拒绝非当前ShopKey权威CDP的SellerSwitched迟到事件: sellerRef="
                    + MyWebSocketServer.DiagnosticRef("seller", sellerNick)
                    + ", boundShopRef=" + MyWebSocketServer.DiagnosticRef("shop", boundShopKey)
                    + ", eventShopRef=" + MyWebSocketServer.DiagnosticRef("shop", shop == null ? string.Empty : shop.ShopKey)
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                return;
            }

            if (EvSellerSwitched != null)
            {
                EvSellerSwitched(this, new SellerSwitchedEventArgs
                {
                    Seller = localUser.LoginID,
                    Buyer = localUser.Conversation
                });
            }
        }

        public async Task<T> InvokeMTop<T>(string apiName, object param = null, string version = "1.0")
        {
            param = param ?? new object();
            var cmd = $@"
                imsdk.invoke('application.invokeMTopChannelService', 
                {{
                  method: '{apiName}',
                  param: {JsonConvert.SerializeObject(param)},
                  httpMethod: 'post',
                  version: '{version}',
                }})";
            var response = await SendExecuteAndWaitAsync(cmd, "InvokeMTop:" + apiName);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        public void InvokeMTop(string apiName, object param = null, string version = "1.0")
        {
            param = param ?? new object();
            var cmd = $@"
                imsdk.invoke('application.invokeMTopChannelService', 
                {{
                  method: '{apiName}',
                  param: {JsonConvert.SerializeObject(param)},
                  httpMethod: 'post',
                  version: '{version}',
                }})";
            SendExecuteAndWait(cmd, "InvokeMTop:" + apiName);
        }

        public async Task<T> Invoke<T>(string apiName, object param = null)
        {
            param = param ?? new object();
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(param)})";
            var response = await SendExecuteAndWaitAsync(cmd, "Invoke:" + apiName);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        // Read-only history operations may wait for the single-flight CDP channel.
        // This does not relax the gate for message sends, probes, or other normal calls.
        public async Task<T> InvokeForHistory<T>(string apiName, object param = null)
        {
            param = param ?? new object();
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(param)})";
            var response = await SendExecuteAndWaitAsync(
                cmd, "InvokeHistory:" + apiName,
                gateWaitTimeoutMs: HistoryGateWaitTimeoutMs).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        public void Invoke(string apiName, object param = null)
        {
            param = param ?? new object();
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(param)})";
            SendExecuteAndWait(cmd, "Invoke:" + apiName);
        }

        public async Task<QnVersionResponse> GetVersion()
        {
            return await Invoke<QnVersionResponse>("application.getVersion");
        }

        public void SendTimiMsg(string userId, string smartTip)
        {
            Invoke("intelligentservice.SendSmartTipMsg",
                new
                {
                    userId,
                    smartTip
                });
        }

        public void TransferContact(string contactID, string targetID, string reason = "")
        {
            Invoke("application.transferContact",
                new
                {
                    contactID,
                    targetID,
                    reason
                });
        }

        public void LightOff(string ccode)
        {
            Invoke("im.singlemsg.SetConversationRead",
                new List<object> {
                    new
                    {
                        cid = new
                        {
                            ccode,
                            bizeType="11001"
                        }
                    }
                });
        }

        public void MarkRead(string ccode, string clientId, string messageId)
        {
            Invoke("im.singlemsg.SetFlagsPeerMsgReaded",
                new
                {
                    cid = new
                    {
                        ccode
                    },
                    mcodes = new List<object> { new { clientId, messageId } }
                });
        }

        private async Task<T> InvokeShopIdentity<T>(string apiName)
        {
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(new object())})";
            var response = await SendExecuteAndWaitAsync(
                cmd, "InvokeShopIdentity:" + apiName, allowRetiredIdentityRead: true).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        public async Task<LocalUserResponse> GetCurrentUser()
        {
            var user = await InvokeShopIdentity<LocalUserResponse>("im.login.GetCurrentLoginID");
            if (user == null || user.Result == null || string.IsNullOrEmpty(user.Result.Nick))
            {
                throw new Exception("GetCurrentLoginID 返回为空，可能不是千牛聊天 WebView。session=" + SessionId);
            }
            var boundShopKey = (ShopKey ?? string.Empty).Trim();
            if (boundShopKey.Length > 0)
            {
                try
                {
                    var resolvedShopKey = ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result).ShopKey;
                    if (!string.Equals(boundShopKey, resolvedShopKey, StringComparison.Ordinal))
                    {
                        IsShopIdentityRetired = true;
                        Log.Info("CDP当前登录身份与已绑定ShopKey不一致，已立即暂停旧店铺命令发送: boundShopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", boundShopKey)
                            + ", currentShopRef=" + MyWebSocketServer.DiagnosticRef("shop", resolvedShopKey)
                            + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                    }
                }
                catch
                {
                    IsShopIdentityRetired = true;
                    Log.Info("CDP当前登录身份无法验证，已立即暂停旧ShopKey命令发送: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", boundShopKey)
                        + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", SessionId));
                }
            }
            Nick = user.Result.Nick;
            return user;
        }

        public void InsertText2Inputbox(string uid, string text)
        {
            Invoke("application.insertText2Inputbox", new { uid = "cntaobao" + uid, text });
        }

        public async Task<bool> IsInputboxEmpty()
        {
            var inputboxEmpty = await Invoke<InputboxEmptyResponse>("application.isInputboxEmpty");
            return inputboxEmpty != null && inputboxEmpty.isEmpty;
        }

        public void BrowserUrl(string url)
        {
            Invoke("application.browserUrl", new { url });
        }

        public void SendRemindPayCard(string encryptedBuyerId, string orderId)
        {
            InvokeMTop("mtop.taobao.customer.service.remind.pay.manual", new { encryptedBuyerId, orderId });
        }

        public void RecallMessage(string ccode, string clientId, string messageId)
        {
            Invoke("im.singlemsg.DoChatMsgWithdraw",
                new
                {
                    cid = new
                    {
                        ccode
                    },
                    mcodes = new List<object> { new { clientId, messageId } }
                });
        }

        public void OpenChat(string nick)
        {
            Invoke("application.openChat", new { nick = "cntaobao" + nick });
        }

        public void SendCoupon(string buyerNick, string activityId)
        {
            InvokeMTop("mtop.taobao.qianniu.airisland.coupon.send.card", new { activityId, buyerNick });
        }

        public void CloseChat(string contactID)
        {
            Invoke("application.closeChat", new { contactID });
        }

        public void GetRemoteHisMsg(string ccode)
        {
            Invoke("im.singlemsg.GetRemoteHisMsg", new
            {
                cid = new
                {
                    ccode,
                    type = 1
                },
                count = 100,
                gohistory = 1,
                msgid = "-1",
                msgtime = "-1",
            });
        }

        public async Task<AccountStatusResponse> GetAccountStatus()
        {
            return await InvokeMTop<AccountStatusResponse>("mtop.taobao.qianniu.cloudkefu.accountstatus.getbyid");
        }

        public async Task<ItemRecordResponse> GetItemRecords(string encryptId)
        {
            return await InvokeMTop<ItemRecordResponse>("mtop.taobao.qianniu.cs.item.record.query", new { encryptId });
        }

        public async Task<SearchUserResponse> SearchBuyerUser(string searchQuery)
        {
            return await InvokeMTop<SearchUserResponse>("mtop.taobao.qianniu.airisland.contact.search",
                new
                {
                    accessKey = "qianniu-pc",
                    accessSecret = "qianniu-pc-secret",
                    accountType = 3,
                    searchQuery
                });
        }

        public async Task<BuyerInfoResponse> GetBuyerInfo(string encryptId)
        {
            return await InvokeMTop<BuyerInfoResponse>("mtop.taobao.qianniu.cs.user.query", new { encryptId });
        }

        public async Task<ZnkfTradeQueryResponse> GetBuyerTrades(string securityBuyerUid, string bizOrderId)
        {
            return await InvokeMTop<ZnkfTradeQueryResponse>("mtop.taobao.qianniu.cs.trade.query", new
            {
                securityBuyerUid,
                bizOrderId
            });
        }

        public async Task<ConversationResponse> GetCurrentConversationID()
        {
            return await Invoke<ConversationResponse>("im.uiutil.GetCurrentConversationID");
        }

        public async Task<ConversationResponse> GetCurrentConversationIDForHistory()
        {
            return await InvokeForHistory<ConversationResponse>("im.uiutil.GetCurrentConversationID");
        }
    }
}