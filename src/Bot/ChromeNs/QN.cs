using Bot.ChromeNs;
using DbEntity.Response;
using DbEntity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Newtonsoft.Json;
using BotLib;
using Bot.ShopScope;

namespace Bot.ChromeNs
{
    public partial class QN
    {
        public event EventHandler<BuyerSwitchedEventArgs> EvBuyerSwitched;
        public event EventHandler<SellerSwitchedEventArgs> EvSellerSwitched;
        public event EventHandler<MessageNotifyEventArgs> EvMessageNotity;
        public event EventHandler<RecieveNewMessageEventArgs> EvRecieveNewMessage;
        public event EventHandler<ShopRobotReceriveNewMessageEventArgs> EvShopRobotReceriveNewMessage;
        public static HashSet<QN> QNSet { get; set; }
        private static readonly object QNSetLock = new object();
        private static readonly AsyncLocal<int> ShopOwnerSendHandoffDepth = new AsyncLocal<int>();
        public string QnVersion { get; set; }

        private CDPClient cdp;
        public CDPClient CDP
        {
            get { return cdp; }
            set
            {
                // 千牛新版会同时打开多个 recent.html / iframe，多个 WebSocket session 会重复初始化。
                // 旧代码先把 cdp 字段替换成新对象，再 -= 事件，导致旧 CDP 事件没有真正解绑，
                // 后续同一个买家第二条消息可能由旧 session 触发、却用新 session 发送，表现为 Bot 右侧有答案但千牛未发送。
                if (cdp != null)
                {
                    cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                    cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                    cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                    cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                    cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;
                }

                cdp = value;
                if (cdp == null) return;

                cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;

                cdp.EvBuyerSwitched += Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity += Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage += Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched += Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage += Cdp_EvShopRobotReceriveNewMessage;
            }
        }

        private LocalUser _seller;
        public LocalUser Seller
        {
            get { return _seller; }
            set { _seller = value; }
        }

        public QNRpa rpa;
        public QNRpa Rpa { get { return rpa; } }
        public Conversation Buyer { get; set; }

        private readonly object _sellerEchoLock = new object();
        private sealed class SellerEchoEntry
        {
            public string ShopKey;
            public string Buyer;
            public string Text;
            public DateTime ReceivedAt;
        }
        private const int MaxRecentSellerEchoes = 512;
        private static readonly TimeSpan SellerEchoRetention = TimeSpan.FromMinutes(20);
        private readonly Queue<SellerEchoEntry> _recentSellerEchoes = new Queue<SellerEchoEntry>();
        private readonly SemaphoreSlim _incomingMessageGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private long _sendGateHeldSinceUtcTicks;

        internal long CurrentSendGateHeldSeconds
        {
            get
            {
                var since = Interlocked.Read(ref _sendGateHeldSinceUtcTicks);
                if (since <= 0) return 0;
                return Math.Max(0, (DateTime.UtcNow.Ticks - since) / TimeSpan.TicksPerSecond);
            }
        }
        private const int ActiveBuyerConfirmDeadlineMs = 9000;
        private const int ActiveBuyerConfirmPollMs = 250;
        // Seller echoes may be repeated by several injected pages. Keep their transport-level
        // dedupe separate from buyer business ownership so a malformed/non-business duplicate page
        // can never consume a buyer key before the authoritative processing path claims it.
        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);
        // This is the single side-effect claim for buyer messages. Duplicate CDP pages are forwarded
        // into the authoritative QN instance, and only the path that reaches this ledger first may
        // enqueue/order-route the buyer event. Background recovery consults the same ledger.
        private readonly IncomingMessageDeduplicator _handledBuyerMessageDeduplicator =
            new IncomingMessageDeduplicator(4000);
        private readonly DateTime _messageSafetyStartedAt = DateTime.Now;
        private readonly VisionRequestService _visionRequestService = new VisionRequestService();
        private readonly BuyerMessageBurstCoordinator _buyerMessageBurstCoordinator;

        public static QN CurQN = null;

        static QN()
        {
            QNSet = new HashSet<QN>();
        }

        public QN(LocalUser seller)
        {
            this._seller = seller;
            this.rpa = new QNRpa(this);
            this._buyerMessageBurstCoordinator = new BuyerMessageBurstCoordinator(ProcessBuyerBurstAsync);
            // One low-frequency scheduler per stable ShopKey. Resolve the identity
            // through the same profile path used by GetByShopIdentity; never key a timer
            // by the mutable seller nickname.
            try
            {
                if (seller != null)
                    ShopProductCatalogService.EnsureScheduled(
                        ShopContextLocator.ResolveRuntimeBySellerIdentity(seller));
            }
            catch (Exception ex)
            {
                // Scheduling is optional; an unresolved/ambiguous identity must not
                // prevent a valid Qianniu connection from being constructed.
                Log.Info("店铺商品周期更新未安排: identity resolution failed; type="
                    + ex.GetType().Name);
            }
        }

        public async Task<bool> SendTextAsync(string buyer, string text, Func<bool> preSendGuard = null)
        {
            var comparison = String.Compare(QnVersion, "9.19.06N", StringComparison.OrdinalIgnoreCase);
            if (comparison < 0)
            {
                ShopContext shop;
                try { shop = ShopContextLocator.ResolveRuntimeBySeller(this); }
                catch
                {
                    rpa.SetSendFailure("旧版发送店铺身份确认", "无法解析当前QN的唯一ShopKey，已阻止旧版接口发送");
                    return false;
                }
                var client = CDP;
                var server = MyWebSocketServer.WSocketSvrInst;
                if (shop == null || client == null || server == null
                    || !await rpa.VerifyShopIdentityBeforeSendAsync(shop.ShopKey).ConfigureAwait(false)
                    || !ReferenceEquals(CDP, client))
                {
                    rpa.SetSendFailure("旧版发送店铺身份确认", "当前CDP不是该ShopKey的权威登录会话，已阻止旧版接口发送");
                    return false;
                }

                if (!IsPreSendGuardSatisfied(preSendGuard, "旧版接口发送"))
                    return false;

                var seller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
                var now = DateTime.Now;
                SendDeliveryWatchdog.ExpectDelivery(
                    seller, buyer, string.Empty, text, "旧版SendSmartTipMsg", now, now,
                    force: true, shopKey: shop.ShopKey);
                try
                {
                    client.SendTimiMsg(buyer, text);
                }
                catch (Exception ex)
                {
                    SendDeliveryWatchdog.CancelPending(
                        seller, buyer, text, "旧版SendSmartTipMsg执行异常：" + ex.Message, shop.ShopKey);
                    rpa.SetSendFailure("旧版接口发送", ex.Message);
                    return false;
                }

                if (!ReferenceEquals(CDP, client) || client.IsInvalidated || client.IsShopIdentityRetired
                    || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId))
                {
                    SendDeliveryWatchdog.EnsurePending(seller, buyer, text, shop.ShopKey);
                    rpa.SetSendCancellation(
                        "旧版接口发送结果不确定",
                        "发送期间CDP身份或权威会话发生变化，已停止重试并等待卖家回显核验");
                    return false;
                }

                SendDeliveryWatchdog.EnsurePending(seller, buyer, text, shop.ShopKey);
                SendDeliveryWatchdog.MarkSubmissionAccepted(
                    seller, buyer, text, "旧版SendSmartTipMsg执行完成且CDP权威身份保持不变", shop.ShopKey);
                BotConnectionDiagnostics.RecordSendAttempt(true, "旧版接口发送");
                return true;
            }
            else
            {
                return await rpa.SendTextAsync(buyer, text, preSendGuard);
            }
        }


        public Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)
        {
            return SendTextWithRetryAsync(buyer, text, retryCount, CancellationToken.None);
        }

        public async Task<bool> SendTextWithRetryAsync(
            string buyer,
            string text,
            int retryCount,
            CancellationToken cancellationToken,
            Func<bool> preSendGuard = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shopOwnerHandoffDepth = ShopOwnerSendHandoffDepth.Value;
            var scopedShop = ShopSettingsScope.Current;
            var expectedShopKey = scopedShop == null
                ? string.Empty
                : (scopedShop.ShopKey ?? string.Empty).Trim();
            if (expectedShopKey.Length > 0 && shopOwnerHandoffDepth < 1)
            {
                var owner = FindAuthoritativeShopSendOwner(expectedShopKey);
                if (owner != null && !ReferenceEquals(owner, this))
                {
                    var handoff = await TryHandoffToAuthoritativeShopQnAsync(
                        owner, expectedShopKey, buyer, text, retryCount,
                        cancellationToken, preSendGuard, shopOwnerHandoffDepth).ConfigureAwait(false);
                    if (handoff.HasValue) return handoff.Value;
                }
            }

            const string segmentToken = "{分段符}";
            if (!string.IsNullOrEmpty(text) && text.IndexOf(segmentToken, StringComparison.Ordinal) >= 0)
            {
                var segments = text.Split(new[] { segmentToken }, StringSplitOptions.None)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (segments.Count == 0) return false;
                for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    var segment = segments[segmentIndex];
                    Log.Info("分段自动发送: buyer=" + buyer + ", segment=" + (segmentIndex + 1) + "/" + segments.Count);
                    if (!await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken, preSendGuard))
                        return false;
                    if (segmentIndex + 1 < segments.Count) await Task.Delay(220, cancellationToken);
                }
                return true;
            }

            text = BotMessageSuffixService.Apply(
                Seller == null ? string.Empty : Seller.Nick,
                text);

            var sendGateWaitAt = DateTime.UtcNow;
            await _sendGate.WaitAsync(cancellationToken);
            var sendGateAcquiredAt = DateTime.UtcNow;
            var sendGateHeld = true;
            long sendGateHeldMs = 0;
            Interlocked.Exchange(ref _sendGateHeldSinceUtcTicks, sendGateAcquiredAt.Ticks);
            var sendGateWaitMs = (long)(sendGateAcquiredAt - sendGateWaitAt).TotalMilliseconds;
            if (sendGateWaitMs >= 2000)
            {
                Log.Info("可靠发送等待锁耗时较长: buyerRef="
                    + MyWebSocketServer.DiagnosticRef("buyer", buyer)
                    + ", waitMs=" + sendGateWaitMs);
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A long wait behind another buyer's native send can outlive the CDP wrapper
                // that originally received this message. Re-resolve only the exact ShopKey
                // after the gate is acquired; never borrow the globally active QN.
                if (expectedShopKey.Length > 0 && shopOwnerHandoffDepth < 1)
                {
                    var currentOwner = FindAuthoritativeShopSendOwner(expectedShopKey);
                    if (currentOwner != null && !ReferenceEquals(currentOwner, this))
                    {
                        ReleaseSendGate(ref sendGateHeld, sendGateAcquiredAt, ref sendGateHeldMs);
                        var handoff = await TryHandoffToAuthoritativeShopQnAsync(
                            currentOwner, expectedShopKey, buyer, text, retryCount,
                            cancellationToken, preSendGuard, shopOwnerHandoffDepth).ConfigureAwait(false);
                        if (handoff.HasValue) return handoff.Value;
                        rpa.SetSendFailure("同ShopKey权威会话转交", "转交期间本店权威会话再次变化，已停止发送");
                        return false;
                    }
                }

                rpa.ResetSendFailure();
                string shopScopeReason;
                shopScopeReason = await WaitForCurrentShopAuthorityAsync(
                    cancellationToken, 5000, "发送").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(shopScopeReason))
                {
                    var replacement = expectedShopKey.Length == 0 || shopOwnerHandoffDepth >= 1
                        ? null
                        : FindAuthoritativeShopSendOwner(expectedShopKey);
                    if (replacement != null && !ReferenceEquals(replacement, this))
                    {
                        ReleaseSendGate(ref sendGateHeld, sendGateAcquiredAt, ref sendGateHeldMs);
                        var handoff = await TryHandoffToAuthoritativeShopQnAsync(
                            replacement, expectedShopKey, buyer, text, retryCount,
                            cancellationToken, preSendGuard, shopOwnerHandoffDepth).ConfigureAwait(false);
                        if (handoff.HasValue) return handoff.Value;
                        rpa.SetSendFailure("同ShopKey权威会话转交", "转交期间本店权威会话再次变化，已停止发送");
                        return false;
                    }
                    rpa.SetSendFailure("发送ShopKey作用域确认", shopScopeReason);
                    return false;
                }
                if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                {
                    var replacement = expectedShopKey.Length == 0 || shopOwnerHandoffDepth >= 1
                        ? null
                        : FindAuthoritativeShopSendOwner(expectedShopKey);
                    if (replacement != null && !ReferenceEquals(replacement, this))
                    {
                        ReleaseSendGate(ref sendGateHeld, sendGateAcquiredAt, ref sendGateHeldMs);
                        var handoff = await TryHandoffToAuthoritativeShopQnAsync(
                            replacement, expectedShopKey, buyer, text, retryCount,
                            cancellationToken, preSendGuard, shopOwnerHandoffDepth).ConfigureAwait(false);
                        if (handoff.HasValue) return handoff.Value;
                        rpa.SetSendFailure("同ShopKey权威会话转交", "转交期间本店权威会话再次变化，已停止发送");
                        return false;
                    }
                    rpa.SetSendFailure("发送ShopKey作用域确认", shopScopeReason);
                    return false;
                }
                if (!await EnsureActiveBuyerForSendAsync(buyer, cancellationToken))
                {
                    rpa.SetSendFailure("会话确认", "无法确认目标买家会话");
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                {
                    var replacement = expectedShopKey.Length == 0 || shopOwnerHandoffDepth >= 1
                        ? null
                        : FindAuthoritativeShopSendOwner(expectedShopKey);
                    if (replacement != null && !ReferenceEquals(replacement, this))
                    {
                        ReleaseSendGate(ref sendGateHeld, sendGateAcquiredAt, ref sendGateHeldMs);
                        var handoff = await TryHandoffToAuthoritativeShopQnAsync(
                            replacement, expectedShopKey, buyer, text, retryCount,
                            cancellationToken, preSendGuard, shopOwnerHandoffDepth).ConfigureAwait(false);
                        if (handoff.HasValue) return handoff.Value;
                        rpa.SetSendFailure("同ShopKey权威会话转交", "转交期间本店权威会话再次变化，已停止发送");
                        return false;
                    }
                    rpa.SetSendFailure("发送ShopKey作用域复核", shopScopeReason);
                    return false;
                }
                if (!IsPreSendGuardSatisfied(preSendGuard, "首次发送"))
                    return false;
                var sendStartedAt = DateTime.Now;
                var ok = await SendTextAsync(buyer, text, preSendGuard);
                if (!ok && rpa.LastSendWasCancelled)
                {
                    Log.Info("自动发送因人工接管或发送前状态门禁取消，禁止重试: buyer=" + buyer
                        + ", reason=" + rpa.GetSendFailureReason());
                    return false;
                }
                if (!ok && rpa.LastSendWasIndeterminate)
                {
                    if (await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 2800, cancellationToken))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("发送动作结果不确定，但已从本店当前/旧QN会话收到精确卖家回显，按真实送达处理: buyer=" + buyer);
                    }
                    else
                    {
                        Log.Error("发送动作结果不确定，已禁止自动重试以避免重复消息；保留SendDeliveryWatchdog等待提交/回显核验: buyer="
                            + buyer + ", reason=" + rpa.GetSendFailureReason());
                        return false;
                    }
                }
                if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))
                {
                    rpa.ResetSendFailure();
                    ok = true;
                    Log.Info("自动发送动作结果未确认，但已收到同买家同文本卖家回显，按真实送达处理并取消重试: buyer="
                        + buyer + ", text=" + text);
                }

                var retry = Math.Max(0, retryCount);
                for (var i = 0; !ok && i < retry; i++)
                {
                    Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer
                        + ", reason=" + rpa.GetSendFailureReason() + ", text=" + text);
                    cancellationToken.ThrowIfCancellationRequested();
                    rpa.InvalidateChatControls();
                    await Task.Delay(1800, cancellationToken);

                    // Seller echo is the source of truth. It can arrive after UIA timed out but
                    // before this retry window ends; sending again would create a duplicate.
                    if (HasRecentSellerEcho(buyer, text, sendStartedAt))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("重试前已收到同买家同文本卖家回显，按真实送达处理并取消重复发送: buyer="
                            + buyer + ", text=" + text);
                        break;
                    }

                    shopScopeReason = await WaitForCurrentShopAuthorityAsync(
                        cancellationToken, 3000, "重试发送").ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(shopScopeReason))
                    {
                        rpa.SetSendFailure("重试ShopKey作用域复核", shopScopeReason);
                        return false;
                    }
                    if (!await EnsureActiveBuyerForSendAsync(buyer, cancellationToken))
                    {
                        rpa.SetSendFailure("重试会话确认", "无法确认目标买家会话");
                        return false;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                    {
                        rpa.SetSendFailure("重试ShopKey作用域复核", shopScopeReason);
                        return false;
                    }
                    // Shop authority and buyer activation can take several seconds.
                    // An echo arriving during those awaits still confirms the first send.
                    if (HasRecentSellerEcho(buyer, text, sendStartedAt))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("重试会话确认期间收到卖家回显，取消重复发送: buyer=" + buyer);
                        break;
                    }
                    if (!IsPreSendGuardSatisfied(preSendGuard, "重试发送"))
                        return false;
                    ok = await SendTextAsync(buyer, text, preSendGuard);
                    if (!ok && rpa.LastSendWasCancelled)
                    {
                        Log.Info("自动发送重试期间人工接管或发送前状态门禁取消，立即停止后续重试: buyer=" + buyer
                            + ", reason=" + rpa.GetSendFailureReason());
                        return false;
                    }
                    if (!ok && rpa.LastSendWasIndeterminate)
                    {
                        if (await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 2800, cancellationToken))
                        {
                            rpa.ResetSendFailure();
                            ok = true;
                            Log.Info("重试动作结果不确定，但已从本店当前/旧QN会话收到精确卖家回显: buyer=" + buyer);
                        }
                        else
                        {
                            Log.Error("重试动作结果不确定，停止后续重试并等待发送回显监控: buyer="
                                + buyer + ", reason=" + rpa.GetSendFailureReason());
                            return false;
                        }
                    }
                    if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("重试动作结果未确认，但卖家回显已证明真实送达: buyer=" + buyer
                            + ", text=" + text);
                    }
                }

                if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 1400, cancellationToken))
                {
                    rpa.ResetSendFailure();
                    ok = true;
                    Log.Info("最终失败判定前收到延迟卖家回显，改判真实送达: buyer=" + buyer
                        + ", text=" + text);
                }
                if (!ok)
                {
                    Log.Error("自动发送最终失败: buyer=" + buyer + ", reason=" + rpa.GetSendFailureReason());
                }
                return ok;
            }
            finally
            {
                ReleaseSendGate(ref sendGateHeld, sendGateAcquiredAt, ref sendGateHeldMs);
                if (sendGateHeldMs >= 5000)
                {
                    Log.Info("可靠发送锁已释放: buyerRef="
                        + MyWebSocketServer.DiagnosticRef("buyer", buyer)
                        + ", heldMs=" + sendGateHeldMs
                        + ", cancelled=" + rpa.LastSendWasCancelled
                        + ", reason=" + rpa.GetSendFailureReason());
                }
            }
        }

        private QN FindAuthoritativeShopSendOwner(string expectedShopKey)
        {
            expectedShopKey = (expectedShopKey ?? string.Empty).Trim();
            var scope = ShopSettingsScope.Current;
            if (expectedShopKey.Length == 0 || scope == null
                || !string.Equals((scope.ShopKey ?? string.Empty).Trim(),
                    expectedShopKey, StringComparison.Ordinal))
                return null;

            try
            {
                var qn = ShopContextLocator.FindConnectedQnByShopKey(expectedShopKey);
                var client = qn == null ? null : qn.CDP;
                var server = MyWebSocketServer.WSocketSvrInst;
                if (qn == null || qn.Seller == null || client == null
                    || client.IsInvalidated || client.IsShopIdentityRetired
                    || !string.Equals((client.ShopKey ?? string.Empty).Trim(),
                        expectedShopKey, StringComparison.Ordinal)
                    || server == null
                    || !server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId))
                    return null;
                return qn;
            }
            catch (Exception ex)
            {
                Log.Info("解析本店当前权威发送QN失败，保持fail-closed: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                    + ", errorType=" + ex.GetType().Name);
                return null;
            }
        }

        private async Task<bool?> TryHandoffToAuthoritativeShopQnAsync(
            QN owner,
            string expectedShopKey,
            string buyer,
            string text,
            int retryCount,
            CancellationToken cancellationToken,
            Func<bool> preSendGuard,
            int handoffDepth)
        {
            if (owner == null || ReferenceEquals(owner, this) || handoffDepth >= 1)
                return null;

            var currentOwner = FindAuthoritativeShopSendOwner(expectedShopKey);
            if (currentOwner == null || !ReferenceEquals(currentOwner, owner))
                return null;

            var fromClient = CDP;
            var toClient = owner.CDP;
            Log.Info("自动回复发送已转交同ShopKey当前权威QN实例: shopRef="
                + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                + ", fromSessionRef=" + MyWebSocketServer.DiagnosticRef("session", fromClient == null ? string.Empty : fromClient.SessionId)
                + ", toSessionRef=" + MyWebSocketServer.DiagnosticRef("session", toClient == null ? string.Empty : toClient.SessionId)
                + ", buyerRef=" + MyWebSocketServer.DiagnosticRef("buyer", buyer));

            var previousDepth = ShopOwnerSendHandoffDepth.Value;
            ShopOwnerSendHandoffDepth.Value = handoffDepth + 1;
            try
            {
                var ok = await owner.SendTextWithRetryAsync(
                    buyer, text, retryCount, cancellationToken, preSendGuard).ConfigureAwait(false);
                if (!ok && rpa != null && owner.rpa != null)
                {
                    var reason = owner.rpa.GetSendFailureReason();
                    if (owner.rpa.LastSendWasCancelled)
                        rpa.SetSendCancellation("同ShopKey权威会话转交", reason);
                    else
                        rpa.SetSendFailure("同ShopKey权威会话转交", reason);
                }
                return ok;
            }
            finally
            {
                ShopOwnerSendHandoffDepth.Value = previousDepth;
            }
        }

        private void ReleaseSendGate(ref bool sendGateHeld, DateTime sendGateAcquiredAt, ref long sendGateHeldMs)
        {
            if (!sendGateHeld) return;
            sendGateHeldMs = (long)(DateTime.UtcNow - sendGateAcquiredAt).TotalMilliseconds;
            sendGateHeld = false;
            Interlocked.Exchange(ref _sendGateHeldSinceUtcTicks, 0);
            _sendGate.Release();
        }

        private bool IsPreSendGuardSatisfied(Func<bool> preSendGuard, string stage)
        {
            if (preSendGuard == null) return true;
            try
            {
                if (preSendGuard()) return true;
                rpa.SetSendCancellation(
                    "发送前业务状态复核",
                    "会话或业务证据在" + (stage ?? "发送") + "前发生变化，已阻止发送");
                Log.Info("可靠发送在最终业务状态复核处被抑制: stage=" + (stage ?? string.Empty));
                return false;
            }
            catch (Exception ex)
            {
                rpa.SetSendCancellation(
                    "发送前业务状态复核",
                    "复核发生异常，已按安全策略阻止发送");
                Log.ErrorWithMaxCount(
                    "可靠发送最终业务状态复核异常，已阻止发送: " + ex.Message, 10);
                return false;
            }
        }

        private async Task<bool> WaitForSellerEchoGraceAsync(
            string buyer,
            string text,
            DateTime since,
            int milliseconds,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.Now.AddMilliseconds(Math.Max(0, milliseconds));
            while (DateTime.Now <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasRecentSellerEcho(buyer, text, since)) return true;
                await Task.Delay(120, cancellationToken);
            }
            return HasRecentSellerEcho(buyer, text, since);
        }

        private bool IsCurrentShopScopeValidForSend(out string reason)
        {
            reason = string.Empty;
            var scopedShop = ShopSettingsScope.Current;
            if (scopedShop == null) return true;

            var expectedShopKey = (scopedShop.ShopKey ?? string.Empty).Trim();
            var client = CDP;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (expectedShopKey.Length == 0 || client == null || server == null
                || client.IsInvalidated || client.IsShopIdentityRetired
                || !string.Equals(client.ShopKey, expectedShopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId))
            {
                reason = "当前CDP会话不属于显式ShopSettingsScope，已阻止跨店发送: expectedShopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                    + ", actualShopRef=" + MyWebSocketServer.DiagnosticRef("shop", client == null ? string.Empty : client.ShopKey)
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", client == null ? string.Empty : client.SessionId);
                return false;
            }

            try
            {
                var runtimeShop = ShopContextLocator.ResolveRuntimeBySeller(this);
                if (runtimeShop == null
                    || !string.Equals(runtimeShop.ShopKey, expectedShopKey, StringComparison.Ordinal))
                {
                    reason = "当前QN运行身份与显式ShopSettingsScope不匹配，已阻止跨店发送";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "无法解析当前QN的ShopKey，已阻止跨店发送: " + ex.Message;
                return false;
            }

            if (!ReferenceEquals(CDP, client) || client.IsInvalidated || client.IsShopIdentityRetired
                || !server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId))
            {
                reason = "发送准备期间CDP权威会话发生变化，已阻止跨店发送";
                return false;
            }
            return true;
        }

        // A message may wait behind the single native send gate while the authoritative WebView
        // session is being replaced. Wait only for the same ShopKey to reclaim authority; never
        // borrow QN.CurQN or relax the last-mile cross-shop guard.
        private async Task<string> WaitForCurrentShopAuthorityAsync(
            CancellationToken cancellationToken,
            int timeoutMs,
            string stage)
        {
            var scopedShop = ShopSettingsScope.Current;
            if (scopedShop == null) return string.Empty;

            var expectedShopKey = (scopedShop.ShopKey ?? string.Empty).Trim();
            if (expectedShopKey.Length == 0) return "显式店铺作用域缺少ShopKey，已阻止发送";

            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            var loggedWaiting = false;
            var loggedMismatch = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentScope = ShopSettingsScope.Current;
                if (currentScope == null
                    || !string.Equals((currentScope.ShopKey ?? string.Empty).Trim(),
                        expectedShopKey, StringComparison.Ordinal))
                    return "等待期间显式ShopKey作用域发生变化，已阻止发送";

                var client = CDP;
                var server = MyWebSocketServer.WSocketSvrInst;
                var clientShopMismatch = client != null
                    && !string.IsNullOrWhiteSpace(client.ShopKey)
                    && !string.Equals((client.ShopKey ?? string.Empty).Trim(),
                        expectedShopKey, StringComparison.Ordinal);
                if (clientShopMismatch)
                {
                    if (!loggedMismatch)
                    {
                        Log.Info((stage ?? "发送") + "当前QN实例绑定了其他ShopKey，已阻止跨店发送并等待同店权威会话接管: expectedShopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                            + ", actualShopRef=" + MyWebSocketServer.DiagnosticRef("shop", client.ShopKey)
                            + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", client.SessionId));
                        loggedMismatch = true;
                    }
                }
                else
                {
                    var authoritative = client != null
                        && !client.IsInvalidated
                        && !client.IsShopIdentityRetired
                        && string.Equals((client.ShopKey ?? string.Empty).Trim(),
                            expectedShopKey, StringComparison.Ordinal)
                        && server != null
                        && server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId);
                    if (authoritative)
                    {
                        string reason;
                        return IsCurrentShopScopeValidForSend(out reason) ? string.Empty : reason;
                    }
                }

                // The old QN wrapper may remain alive after the same ShopKey is rebound to a
                // replacement wrapper. Detect that exact authoritative owner while waiting,
                // then let SendTextWithRetryAsync release this gate and hand off before any send.
                var replacement = FindAuthoritativeShopSendOwner(expectedShopKey);
                if (replacement != null && !ReferenceEquals(replacement, this))
                {
                    Log.Info((stage ?? "发送") + "检测到同ShopKey权威QN实例已切换，交由发送层安全转交: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                        + ", fromSessionRef=" + MyWebSocketServer.DiagnosticRef("session", client == null ? string.Empty : client.SessionId)
                        + ", toSessionRef=" + MyWebSocketServer.DiagnosticRef("session", replacement.CDP == null ? string.Empty : replacement.CDP.SessionId));
                    return "同ShopKey权威QN实例已切换，等待发送层安全转交";
                }

                if (!loggedWaiting)
                {
                    Log.Info((stage ?? "发送") + "等待本店权威CDP会话恢复: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                        + ", timeoutMs=" + Math.Max(0, timeoutMs));
                    loggedWaiting = true;
                }

                var remainingMs = (int)Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                if (remainingMs <= 0) break;
                await Task.Delay(Math.Min(150, remainingMs), cancellationToken).ConfigureAwait(false);
            }

            string finalReason;
            if (IsCurrentShopScopeValidForSend(out finalReason)) return string.Empty;
            Log.Info((stage ?? "发送") + "等待本店权威CDP会话恢复超时: shopRef="
                + MyWebSocketServer.DiagnosticRef("shop", expectedShopKey)
                + ", reason=" + (finalReason ?? string.Empty));
            return string.IsNullOrWhiteSpace(finalReason)
                ? "等待本店权威CDP会话恢复超时"
                : finalReason;
        }

        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer, CancellationToken cancellationToken)
        {
            buyer = (buyer ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer) || cdp == null) return false;
            string shopScopeReason;
            if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
            {
                rpa.SetSendFailure("发送ShopKey作用域确认", shopScopeReason);
                return false;
            }
            var sellerNick = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ActiveBuyerConfirmDeadlineMs);

            for (var attempt = 0; attempt < 22 && DateTime.UtcNow < deadlineUtc; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                {
                    rpa.SetSendFailure("发送ShopKey作用域确认", shopScopeReason);
                    return false;
                }
                try
                {
                    var current = await GetCurrentConversationID();
                    if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                    {
                        rpa.SetSendFailure("发送ShopKey作用域复核", shopScopeReason);
                        return false;
                    }
                    var currentConversation = current == null ? null : current.Result;
                    var currentNick = currentConversation == null ? string.Empty : (currentConversation.Nick ?? string.Empty).Trim();
                    if (currentConversation != null)
                    {
                        BuyerIdentityAliasService.Observe(
                            sellerNick,
                            currentConversation.Nick,
                            currentConversation.Display,
                            currentConversation.TargetId);
                    }
                    if (BuyerIdentityAliasService.AreEquivalent(sellerNick, currentNick, buyer))
                    {
                        if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                        {
                            rpa.SetSendFailure("发送ShopKey作用域复核", shopScopeReason);
                            return false;
                        }
                        SetActiveConversationByNick(sellerNick, currentNick, "sendVerified");
                        return true;
                    }
                    if (attempt == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                        {
                            rpa.SetSendFailure("发送ShopKey作用域复核", shopScopeReason);
                            return false;
                        }
                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);
                        OpenChat(buyer);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("发送前确认买家会话失败: " + ex.Message);
                    if (attempt == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrentShopScopeValidForSend(out shopScopeReason))
                        {
                            rpa.SetSendFailure("发送ShopKey作用域复核", shopScopeReason);
                            return false;
                        }
                        OpenChat(buyer);
                    }
                }

                var remainingMs = (int)Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                if (remainingMs <= 0) break;
                await Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs), cancellationToken);
            }

            Log.Error("发送已阻止：无法在会话确认总预算内确认当前会话为目标买家。target=" + buyer
                + ", deadlineMs=" + ActiveBuyerConfirmDeadlineMs);
            return false;
        }

        public async void SendImageAsync(string buyer, string imagePath)
        {
            await rpa.SendImageAsync(buyer, imagePath);
        }

        private static string GetMessageText(QNChatMessage m)
        {
            if (m == null) return string.Empty;
            try
            {
                if (m.originalData != null)
                {
                    var text = m.originalData.text ?? string.Empty;
                    if (m.originalData.header != null)
                    {
                        text += m.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (m.summary ?? string.Empty).Trim();
        }

        private static string NormalizeMessageText(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        }

        private void RecordSellerEcho(string buyerNick, string text)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return;

            var client = CDP;
            var shopKey = client == null ? string.Empty : (client.ShopKey ?? string.Empty).Trim();
            var scopedShop = ShopSettingsScope.Current;
            if (shopKey.Length == 0 || client.IsInvalidated || client.IsShopIdentityRetired
                || (scopedShop != null && !string.Equals(scopedShop.ShopKey, shopKey, StringComparison.Ordinal))) return;
            var now = DateTime.Now;
            lock (_sellerEchoLock)
            {
                while (_recentSellerEchoes.Count > 0
                    && (_recentSellerEchoes.Count >= MaxRecentSellerEchoes
                        || _recentSellerEchoes.Peek().ReceivedAt < now - SellerEchoRetention))
                    _recentSellerEchoes.Dequeue();
                _recentSellerEchoes.Enqueue(new SellerEchoEntry
                {
                    ShopKey = shopKey,
                    Buyer = buyerNick,
                    Text = text,
                    ReceivedAt = now
                });
            }
            Log.Info("已记录卖家消息回显: buyer=" + buyerNick + ", text=" + text);
        }

        public bool HasRecentSellerEcho(string buyerNick, string text, DateTime since)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return false;

            var scopedShop = ShopSettingsScope.Current;
            var client = CDP;
            var shopKey = scopedShop == null
                ? (client == null ? string.Empty : (client.ShopKey ?? string.Empty).Trim())
                : (scopedShop.ShopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return false;

            // The send may have started on an old QN wrapper just before CDP authority moved
            // to a replacement wrapper for the same ShopKey. The seller echo is then recorded
            // on the replacement's queue, not this instance's queue. Check that exact current
            // owner first; never inspect another ShopKey or the global active QN.
            var owner = FindAuthoritativeShopSendOwner(shopKey);
            if (owner != null && !ReferenceEquals(owner, this)
                && owner.HasRecentSellerEchoLocal(buyerNick, text, since, shopKey))
                return true;

            // Retain this wrapper's exact-ShopKey observations as a second source. They may
            // contain the echo that arrived immediately before the authoritative handoff.
            return HasRecentSellerEchoLocal(buyerNick, text, since, shopKey);
        }

        private bool HasRecentSellerEchoLocal(
            string buyerNick, string text, DateTime since, string expectedShopKey)
        {
            expectedShopKey = (expectedShopKey ?? string.Empty).Trim();
            if (expectedShopKey.Length == 0) return false;
            var cutoff = since.AddMilliseconds(-500);
            var now = DateTime.Now;
            if (cutoff < now - SellerEchoRetention) cutoff = now - SellerEchoRetention;
            var sellerNick = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            lock (_sellerEchoLock)
            {
                return _recentSellerEchoes.Any(echo => echo.ReceivedAt >= cutoff
                    && string.Equals(echo.ShopKey, expectedShopKey, StringComparison.Ordinal)
                    && echo.Text == text
                    && BuyerIdentityAliasService.AreEquivalent(sellerNick, echo.Buyer, buyerNick));
            }
        }

        internal DateTime LastSellerEchoTimeForShop(string shopKey)
        {
            if (string.IsNullOrWhiteSpace(shopKey)) return DateTime.MinValue;
            lock (_sellerEchoLock)
            {
                return _recentSellerEchoes.Where(x => string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal))
                    .Select(x => x.ReceivedAt).DefaultIfEmpty(DateTime.MinValue).Max();
            }
        }

        internal bool TryGetRecentSellerEchoForLearning(string shopKey, string buyer, DateTime since,
            out string text, out DateTime receivedAt)
        {
            text = string.Empty;
            receivedAt = DateTime.MinValue;
            var client = CDP;
            if (client == null || client.IsInvalidated || client.IsShopIdentityRetired
                || string.IsNullOrWhiteSpace(shopKey)
                || !string.Equals(client.ShopKey, shopKey, StringComparison.Ordinal)) return false;
            var cutoff = since.AddMilliseconds(-500);
            var oldest = DateTime.Now - SellerEchoRetention;
            if (cutoff < oldest) cutoff = oldest;
            lock (_sellerEchoLock)
            {
                // Preserve the strict buyer identity used by the learning path; never
                // turn a fuzzy alias or a different shop's echo into human evidence.
                var echo = _recentSellerEchoes.LastOrDefault(x => x.ReceivedAt >= cutoff
                    && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(x.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal));
                if (echo == null) return false;
                text = echo.Text;
                receivedAt = echo.ReceivedAt;
                return true;
            }
        }

        private bool IsBuyerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && !DirectOrderIdentityResolver.IdentityEquals(m.fromid.nick, _seller.Nick)
                && IsCurrentSellerEndpoint(m, false);
        }

        private bool IsSellerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && !DirectOrderIdentityResolver.IdentityEquals(m.toid.nick, _seller.Nick)
                && IsCurrentSellerEndpoint(m, true);
        }

        private bool IsCurrentSellerEndpoint(QNChatMessage message, bool sellerIsFrom)
        {
            if (message == null || _seller == null) return false;
            var sellerNick = (_seller.Nick ?? string.Empty).Trim();
            if (sellerNick.Length == 0) return false;

            var endpointNick = sellerIsFrom
                ? (message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim())
                : (message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim());
            if (!DirectOrderIdentityResolver.IdentityEquals(endpointNick, sellerNick))
                return false;

            var loginNick = message.loginid == null ? string.Empty : (message.loginid.nick ?? string.Empty).Trim();
            if (loginNick.Length > 0
                && !DirectOrderIdentityResolver.IdentityEquals(loginNick, sellerNick))
                return false;

            // Nicknames are not unique across logged-in Qianniu accounts. When the
            // session has a stable TargetId, reject any conflicting seller identity
            // carried by the message instead of routing it into this shop's pipeline.
            var expectedTargetId = (_seller.TargetId ?? string.Empty).Trim();
            if (expectedTargetId.Length == 0) return true;

            var endpointTargetId = sellerIsFrom
                ? (message.fromid == null ? string.Empty : (message.fromid.targetId ?? string.Empty).Trim())
                : (message.toid == null ? string.Empty : (message.toid.targetId ?? string.Empty).Trim());
            if (endpointTargetId.Length > 0
                && !string.Equals(endpointTargetId, expectedTargetId, StringComparison.Ordinal))
                return false;

            var loginTargetId = message.loginid == null ? string.Empty : (message.loginid.targetId ?? string.Empty).Trim();
            if (loginTargetId.Length > 0
                && !string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal))
                return false;

            return true;
        }

        public void SetActiveConversationByNick(string sellerNick, string buyerNick, string source)
        {
            try
            {
                sellerNick = (sellerNick ?? string.Empty).Trim();
                buyerNick = (buyerNick ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sellerNick) && string.IsNullOrWhiteSpace(buyerNick)) return;

                string nonBuyerReason;
                if (NonBuyerConversationGuard.ShouldBlockIdentity(sellerNick, buyerNick, out nonBuyerReason))
                {
                    Log.Info("非买家会话身份已拒绝，不更新当前buyer: source=" + source + ", reason=" + nonBuyerReason);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(sellerNick) && (_seller == null || _seller.Nick != sellerNick))
                {
                    _seller = new LocalUser { Nick = sellerNick, Display = sellerNick };
                }

                if (!string.IsNullOrWhiteSpace(buyerNick) && (Buyer == null || Buyer.Nick != buyerNick))
                {
                    Buyer = new Conversation { Nick = buyerNick, Display = buyerNick };
                }

                CurQN = this;
                BotConnectionDiagnostics.RecordBuyerSeller(_seller == null ? sellerNick : _seller.Nick, Buyer == null ? buyerNick : Buyer.Nick);

                try
                {
                    // Desk.Inst is replaced/disposed during startup reconciliation. Snapshot it once;
                    // checking Desk.Inst and then dereferencing it again creates a classic TOCTOU null
                    // race that was observed in the 1.1.1406 field log.
                    var desk = Desk.Inst;
                    if (desk != null)
                    {
                        if (_seller != null && !string.IsNullOrWhiteSpace(_seller.Nick)) desk.ChangeSeller(_seller.Nick);
                        if (Buyer != null && !string.IsNullOrWhiteSpace(Buyer.Nick)) desk.ChangeBuyer(Buyer.Nick);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }

                Log.Info("当前会话已更新: source=" + source + ", seller=" + (_seller == null ? string.Empty : _seller.Nick) + ", buyer=" + (Buyer == null ? string.Empty : Buyer.Nick));
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            string nonBuyerReason;
            if (e != null && e.Seller != null && e.Buyer != null
                && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家后台消息通知已丢弃，不触发首问/补偿/学习: reason=" + nonBuyerReason);
                return;
            }

            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
            // 绝不能在这里修改 QN.Buyer 或提前打开聊天，否则后台买家的答案可能发到当前可见买家。
            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
                ScheduleBackgroundMessageRecovery(e);
            }
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, e);
            }
        }

        private void Cdp_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            if (e == null || e.Seller == null) return;
            var client = sender as CDPClient;
            var server = MyWebSocketServer.WSocketSvrInst;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(e.Seller); }
            catch { return; }
            if (client == null || !ReferenceEquals(CDP, client)
                || client.IsInvalidated || client.IsShopIdentityRetired
                || server == null || shop == null
                || !string.Equals(client.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shop.ShopKey, client.SessionId))
            {
                Log.Info("QN已拒绝非当前ShopKey权威CDP的SellerSwitched事件，不修改Seller/Buyer/CurQN: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shop == null ? string.Empty : shop.ShopKey)
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", client == null ? string.Empty : client.SessionId));
                return;
            }
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("卖家切换事件携带非买家会话，保留当前真实buyer: reason=" + nonBuyerReason);
                return;
            }
            Seller = e.Seller;
            CurQN = this;
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "sellerSwitched");

            if (EvSellerSwitched != null)
            {
                EvSellerSwitched(this, e);
            }
        }

        private Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(
                message,
                _seller == null ? string.Empty : _seller.Nick,
                messageText,
                out nonBuyerReason))
            {
                Log.Info("非买家普通入站消息已丢弃，未进入订单/首问/商品链接/AI链: reason=" + nonBuyerReason);
                return Task.CompletedTask;
            }
            BuyerIdentityAliasService.ObserveMessage(_seller == null ? string.Empty : _seller.Nick, message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);

            // Classify before consuming any transport dedupe key. A duplicate/partially hydrated CDP
            // page can emit a frame that is not yet a valid buyer business event; allowing that frame
            // to reserve the key would make the later authoritative copy disappear. Seller echoes use
            // the transport ledger, while valid buyer messages use the handled-business ledger below.
            if (IsSellerMessage(message))
            {
                if (!_incomingMessageDeduplicator.TryAccept(messageKey))
                {
                    Log.Info("重复卖家回显已跳过: key=" + messageKey);
                    return Task.CompletedTask;
                }
                try
                {
                    ConversationContextStore.RefreshAndRecord(message, messageText);
                    RecordSellerEcho(message.toid.nick, messageText);
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    // A transport claim is not delivery evidence. If timeline/echo recording
                    // fails synchronously, let the same seller echo be replayed from history.
                    _incomingMessageDeduplicator.TryRelease(messageKey);
                    Log.ErrorWithMaxCount("卖家发送回显记录失败，已释放传输去重占位以便历史补偿重试: " + ex.Message, 20);
                    throw;
                }
            }
            if (!IsBuyerMessage(message)) return Task.CompletedTask;

            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var detectedAt = DateTime.Now;
            string shopKey;
            try
            {
                shopKey = ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("买家消息缺少可验证ShopKey，已阻断回复链路: seller="
                    + sellerNick + ", error=" + ex.Message, 20);
                return Task.CompletedTask;
            }

            // This is the authoritative business claim. It is shared with background recovery,
            // but it is not treated as proof of successful ingress until a route accepts the event.
            if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("已实际处理的买家消息不再重复入队: key=" + messageKey);
                return Task.CompletedTask;
            }

            try
            {
                ConversationContextStore.RefreshAndRecord(message, messageText);
                MarkBuyerMessageObserved(sellerNick, buyerNick);

                OrderPlacedReplyPlan orderPlan;
                if (OrderPlacedAutoReplyService.TryCreatePlan(
                    message,
                    messageText,
                    sellerNick,
                    buyerNick,
                    _messageSafetyStartedAt,
                    out orderPlan,
                    null,
                    shopKey))
                {
                    if (orderPlan != null && orderPlan.IsBuyerFollowUp)
                        ResponseProgressTracker.ObserveNewBuyerTurn(sellerNick, buyerNick, shopKey);
                    // ProcessOrderPlacedReplyAsync owns its durable action/confirmation ledger.
                    return orderPlan == null
                        ? Task.CompletedTask
                        : ProcessOrderPlacedReplyAsync(orderPlan);
                }

                var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey, CDP);
                var displayQuestion = IncomingMessageSafety.GetDisplayText(message, messageText);
                var visionDecision = VisionMessageDecision.Decide(
                    message,
                    messageText,
                    decision,
                    shopKey);
                Log.Info("买家消息路由决策: seller=" + sellerNick
                    + ", buyer=" + buyerNick
                    + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                    + ", kind=" + (visionDecision == null ? "null" : visionDecision.Kind.ToString())
                    + ", label=" + (visionDecision == null ? string.Empty : visionDecision.QuestionLabel)
                    + ", note=" + (visionDecision == null ? string.Empty : visionDecision.Note));

                if (!Params.Robot.CanUseRobotReal)
                {
                    AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                    return Task.CompletedTask;
                }

                if (visionDecision.Kind == VisionDecisionKind.Skip
                    && !IncomingMessageSafety.IsMediaPlaceholder(displayQuestion))
                {
                    AddSkippedConversation(sellerNick, buyerNick, visionDecision.QuestionLabel, visionDecision.Note);
                    Log.Info("买家消息安全跳过: buyer=" + buyerNick + ", reason=" + visionDecision.Note);
                    return Task.CompletedTask;
                }

                ResponseProgressTracker.ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey);
                if (visionDecision.Kind == VisionDecisionKind.Text)
                {
                    BotFlowTestService.RecordCandidate(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey);
                }
                Log.Info("买家消息已识别并展示: seller=" + sellerNick + ", buyer=" + buyerNick
                    + ", detectedAt=" + detectedAt.ToString("HH:mm:ss.fff") + ", question=" + displayQuestion);

                _buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem
                {
                    ShopKey = shopKey,
                    SellerNick = sellerNick,
                    BuyerNick = buyerNick,
                    MessageKey = messageKey,
                    DisplayText = displayQuestion,
                    Message = message,
                    SafetyDecision = decision,
                    VisionDecision = visionDecision,
                    SortValue = IncomingMessageSafety.GetSortValue(message),
                    ReceivedAt = detectedAt
                });
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                // Release only synchronous pre-acceptance failures. If an order Task was
                // returned, its durable action ledger owns retries and no claim is reopened.
                _handledBuyerMessageDeduplicator.TryRelease(messageKey);
                Log.ErrorWithMaxCount("买家消息同步入站处理异常，已释放去重占位以便后台补偿重试: " + ex.Message, 20);
                throw;
            }
        }

        private async Task ProcessBuyerBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (burst == null || burst.Items.Count < 1) return;

            if (burst.InputOverflow)
            {
                if (!lease.IsCurrent) return;
                var note = string.IsNullOrWhiteSpace(burst.InputOverflowReason)
                    ? "本轮买家连续消息超过安全处理上限，已停止自动回复，避免因截断造成答非所问。"
                    : burst.InputOverflowReason;
                var detectedAt = burst.Items.Min(x => x.ReceivedAt);
                ResponseProgressTracker.BeginAnswer(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, burst.ShopKey);
                lease.MarkFailed("buyer_burst_input_overflow");
                ResponseProgressTracker.Fail(
                    burst.SellerNick, burst.BuyerNick, note, burst.ShopKey);
                Log.ErrorWithMaxCount("买家连续消息超过安全处理上限，已拒绝将截断预览发送给文本/视觉AI: seller="
                    + burst.SellerNick + ", buyer=" + burst.BuyerNick
                    + ", itemCount=" + burst.Items.Count
                    + ", previewChars=" + (burst.CombinedQuestion ?? string.Empty).Length
                    + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", burst.ShopKey), 20);
                return;
            }

            if (string.IsNullOrWhiteSpace(burst.CombinedQuestion)) return;

            if (!burst.HasReplyableItem)
            {
                if (!lease.IsCurrent) return;
                var note = "已合并收到买家的媒体消息，但当前未配置对应内容理解能力，未自动回复。";
                AddSkippedConversation(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, note);
                Log.Info("买家媒体消息合并跳过: buyer=" + burst.BuyerNick + ", messages=" + burst.CombinedQuestion.Replace("\n", " | "));
                return;
            }

            var visionItem = burst.LatestVisionItem;
            if (visionItem != null)
            {
                await ProcessVisionBurstAsync(lease, visionItem);
                return;
            }
            await ProcessTextBurstAsync(lease);
        }

        private async Task ProcessTextBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt,
                burst.ShopKey);
            var aiStartedAt = DateTime.Now;
            Log.Info("文本回复开始: buyer=" + burst.BuyerNick + ", queueMs="
                + Math.Max(0, (long)(aiStartedAt - detectedAt).TotalMilliseconds));

            string answer;
            var usedFirstInquiryFixedReply = FirstInquiryFixedReplyService.TryResolve(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                out answer,
                burst.ShopKey);
            if (usedFirstInquiryFixedReply)
            {
                answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);
                KnowledgeLearningService.RegisterAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    "首条咨询固定回复");
                Log.Info("首条咨询固定回复已命中，跳过AI调用: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick);
            }
            else
            {
                answer = await Task.Run(() => MyOpenAI.GetAnswer(
                    burst.SellerNick,
                    burst.BuyerNick,
                    string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion,
                    true));
            }

            if (!lease.IsCurrent)
            {
                Log.Info("买家在AI生成期间发送了新消息，旧文本草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            if (!usedFirstInquiryFixedReply)
            {
                var deduplication = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer);
                answer = deduplication.Answer;
            }

            if (!await lease.ConfirmStableAsync(220))
            {
                Log.Info("发送前发现买家补充了新消息，旧文本答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            // Defensive legacy path: never publish an error/empty model result as AnswerReady.
            // BuyerStreamingReplyPipeline normally replaces this handler, but if that patch is ever
            // unavailable the fallback must preserve the same terminal failure semantics.
            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                var failure = string.IsNullOrWhiteSpace(answer) ? "错误：AI未返回有效答案。" : answer;
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing("AI未生成可用答案");
                    conversationCtl.SetStatus(failure, false);
                }
                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, failure, burst.ShopKey);
                Log.Info("旧文本回复路径AI失败，保持失败态且不进入答案就绪/完成: buyer="
                    + burst.BuyerNick);
                return;
            }

            var answerReadyAt = DateTime.Now;
            var answerSource = KnowledgeLearningService.ResolveAnswerSource(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            conversationCtl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                answerSource,
                detectedAt,
                answerReadyAt,
                burst.ShopKey);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("文本答案已生成: buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds)
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            if (!lease.IsCurrent)
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                return;
            }

            if (!lease.MarkSending("legacy_text_send_started"))
            {
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "旧文本发送路径generation已失效", burst.ShopKey);
                return;
            }
            Func<bool> preSendGuard = () =>
            {
                if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                    || !Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                    return false;
                string latestReason;
                var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestReason, false, burst.ShopKey);
                return shouldSend;
            };
            var sendOk = await SendTextWithRetryAsync(
                burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard);
            var sendCancelled = !sendOk && rpa != null && rpa.LastSendWasCancelled;
            var sendIsIndeterminate = !sendOk && rpa != null && rpa.LastSendWasIndeterminate;
            var sendFailureReason = rpa == null
                ? "发送器不可用，未能确认实际送达状态"
                : rpa.GetSendFailureReason();
            if (sendOk)
            {
                lease.MarkCompleted("legacy_text_send_completed");
                ReplyDeduplicationService.RememberDelivered(
                    burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
                if (usedFirstInquiryFixedReply)
                    FirstInquiryFixedReplyService.MarkDelivered(
                        burst.SellerNick, burst.BuyerNick, burst.ShopKey);
            }
            else if (sendCancelled)
            {
                lease.MarkCompleted("legacy_text_send_cancelled");
            }
            else
            {
                lease.MarkFailed("legacy_text_send_failed");
            }
            if (usedFirstInquiryFixedReply && !sendOk)
            {
                if (sendIsIndeterminate)
                {
                    Log.Error("旧文本路径首条咨询发送结果不确定，保留首条资格占位等待回显核验: seller="
                        + burst.SellerNick + ", buyer=" + burst.BuyerNick
                        + ", reason=" + sendFailureReason);
                }
                else
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        burst.SellerNick, burst.BuyerNick, sendFailureReason, burst.ShopKey);
                }
            }
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(sendOk, sendOk
                    ? "已发送（合并本轮买家消息）"
                    : sendCancelled ? "未发送：人工接管或发送前会话门禁已取消"
                    : "发送失败：" + sendFailureReason);
            }
            Log.Info("文本真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", cancelled=" + sendCancelled
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            if (sendCancelled)
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "旧文本发送前会话门禁取消", burst.ShopKey);
            else if (sendOk)
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
            else
                ResponseProgressTracker.Fail(
                    burst.SellerNick, burst.BuyerNick,
                    "文本答案已生成，但实际发送未确认：" + sendFailureReason, burst.ShopKey);
        }

        private async Task ProcessVisionBurstAsync(
            BuyerMessageBurstLease lease,
            BuyerMessageBurstItem visionItem)
        {
            var burst = lease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt,
                burst.ShopKey);
            var task = new VisionReplyTask
            {
                ShopKey = burst.ShopKey,
                SellerNick = burst.SellerNick,
                BuyerNick = burst.BuyerNick,
                MessageKey = visionItem.MessageKey,
                Message = visionItem.Message,
                BrowserClient = CDP,
                CombinedQuestion = burst.CombinedQuestion,
                DeferLearningUntilDelivered = true
            };
            var result = await _visionRequestService.ExecuteAsync(task, CancellationToken.None);
            if (!lease.IsCurrent)
            {
                Log.Info("买家在视觉AI生成期间发送了新消息，旧视觉草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                var note = "已跳过：" + (string.IsNullOrWhiteSpace(result.Error) ? "视觉识别失败" : result.Error) + "，未向买家发送消息。";
                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, note, burst.ShopKey);
                Log.Info("视觉消息跳过: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", messageId=" + visionItem.MessageKey + ", endpoint=" + result.EndpointName + ", model=" + result.VisionModel + ", latencyMs=" + result.LatencyMs + ", reason=" + result.Error);
                return;
            }

            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                result.Answer,
                CancellationToken.None,
                result.IsKnowledgeV2DirectAnswer,
                result.ApprovedV2KnowledgeEvidence);
            var answer = deduplication.Answer;
            if (!await lease.ConfirmStableAsync(220))
            {
                Log.Info("发送前发现买家补充了新消息，旧视觉答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var source = deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                ? deduplication.Source
                : "AI生成";
            var answerReadyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                source,
                detectedAt,
                answerReadyAt,
                burst.ShopKey);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("视觉答案已生成: buyer=" + burst.BuyerNick
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds)
                + ", visionApiMs=" + result.LatencyMs);
            if (!autoSend)
            {
                if (ctl != null) ctl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            if (!lease.IsCurrent)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "视觉回复generation已失效", burst.ShopKey);
                return;
            }

            string visionRelevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                out visionRelevanceReason, false, burst.ShopKey))
            {
                var suppressed = "发送前检测到人工接管或买家问题已变化，已阻止视觉旧回复：" + visionRelevanceReason;
                lease.MarkCompleted("vision_final_relevance_suppressed");
                if (ctl != null) ctl.SetSendResult(false, suppressed);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, suppressed, burst.ShopKey);
                Log.Info("视觉自动回复最终发送门禁阻止迟到回复: buyer=" + burst.BuyerNick
                    + ", reason=" + visionRelevanceReason);
                return;
            }
            if (!lease.MarkSending("vision_send_started"))
            {
                const string invalidBeforeVisionSend = "未发送：视觉回复generation在发送前已失效";
                if (ctl != null) ctl.SetSendResult(false, invalidBeforeVisionSend);
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, invalidBeforeVisionSend, burst.ShopKey);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.ConditionalAdviceKey)
                && !ConditionalBusinessAdviceService.IsSafeRefundSuggestionText(answer))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：条件性售后建议最终话术未通过安全复核");
                lease.MarkCompleted("conditional_advice_final_text_unsafe");
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            var conditionalAdviceReserved = false;
            if (!string.IsNullOrWhiteSpace(result.ConditionalAdviceKey))
            {
                string reserveReason;
                conditionalAdviceReserved = ConditionalBusinessAdviceService.TryReserveDelivery(
                    burst.ShopKey, burst.SellerNick, burst.BuyerNick,
                    result.ConditionalAdviceOrderId, result.ConditionalAdviceItemId,
                    result.ConditionalAdviceItemTitle, result.ConditionalAdviceSkuText, result.ConditionalAdviceKey,
                    result.ConditionalAdviceImageMessageKey, result.ConditionalAdviceImageKnowledgeId,
                    out reserveReason);
                if (!conditionalAdviceReserved)
                {
                    if (ctl != null) ctl.SetSendResult(false, "未发送：" + reserveReason);
                    lease.MarkCompleted("conditional_business_advice_duplicate_or_unverified");
                    ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                    return;
                }
            }

            // A later cancellation may follow an earlier transport attempt; do not
            // release the idempotency key unless no attempt ever passed this guard.
            var conditionalAdviceSendMayHaveStarted = false;
            Func<bool> visionPreSendGuard = () =>
            {
                if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                    || !Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                    return false;
                if (conditionalAdviceReserved
                    && !ConditionalBusinessAdviceService.IsEvidenceStillValid(
                        burst.ShopKey, burst.SellerNick, burst.BuyerNick,
                        result.ConditionalAdviceOrderId,
                        result.ConditionalAdviceItemId,
                        result.ConditionalAdviceItemTitle,
                        result.ConditionalAdviceSkuText,
                        result.ConditionalAdviceImageMessageKey,
                        result.ConditionalAdviceImageKnowledgeId))
                    return false;
                string globalLeaseReason;
                if (conditionalAdviceReserved
                    && !ConditionalBusinessAdviceService.RenewDistributedDelivery(
                        burst.ShopKey, burst.SellerNick, burst.BuyerNick,
                        result.ConditionalAdviceOrderId, result.ConditionalAdviceKey,
                        out globalLeaseReason))
                {
                    Log.Info("旧视觉发送路径全局幂等租约已失效，阻止发送: " + globalLeaseReason);
                    return false;
                }
                string latestReason;
                var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestReason, false, burst.ShopKey);
                if (shouldSend && conditionalAdviceReserved)
                    conditionalAdviceSendMayHaveStarted = true;
                return shouldSend;
            };
            bool sendOk;
            try
            {
                sendOk = await SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lease.CancellationToken, visionPreSendGuard);
            }
            catch (OperationCanceledException)
            {
                if (conditionalAdviceReserved)
                    ConditionalBusinessAdviceService.CompleteDelivery(
                        burst.ShopKey, burst.SellerNick, burst.BuyerNick,
                        result.ConditionalAdviceOrderId, result.ConditionalAdviceKey, false, !conditionalAdviceSendMayHaveStarted);
                throw;
            }
            var sendCancelled = !sendOk && rpa != null && rpa.LastSendWasCancelled;
            var sendFailureReason = rpa == null
                ? "发送器不可用，未能确认实际送达状态"
                : rpa.GetSendFailureReason();
            if (conditionalAdviceReserved)
                ConditionalBusinessAdviceService.CompleteDelivery(
                    burst.ShopKey, burst.SellerNick, burst.BuyerNick,
                    result.ConditionalAdviceOrderId, result.ConditionalAdviceKey,
                    sendOk, !conditionalAdviceSendMayHaveStarted);
            if (sendOk) lease.MarkCompleted("vision_send_completed");
            else if (sendCancelled) lease.MarkCompleted("vision_send_cancelled");
            else lease.MarkFailed("vision_send_failed");
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(
                    burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
            }
            if (ctl != null) ctl.SetSendResult(sendOk, sendOk
                ? "已发送（合并图片与本轮消息）"
                : sendCancelled ? "未发送：人工接管或发送前会话门禁已取消"
                : "识别完成，但目标买家会话未确认，未发送。原因：" + sendFailureReason);
            Log.Info("视觉真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", cancelled=" + sendCancelled
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            if (sendCancelled)
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "旧视觉发送前会话门禁取消", burst.ShopKey);
            else if (sendOk)
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
            else
                ResponseProgressTracker.Fail(
                    burst.SellerNick, burst.BuyerNick,
                    "视觉答案已生成，但实际发送未确认：" + sendFailureReason, burst.ShopKey);
        }

        private void AddSkippedConversation(string seller, string buyer, string question, string note)
        {
            if (Desk.Inst == null) return;
            var ctl = Desk.Inst.AddConversation(seller, buyer, question, note, false);
            if (ctl != null) ctl.SetSkipped(note);
        }

        private async void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(this); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("千牛消息处理因无法确认源店铺而停止: " + ex.Message, 10);
                return;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                await _incomingMessageGate.WaitAsync();
                try
            {
                if (EvRecieveNewMessage != null)
                {
                    EvRecieveNewMessage(this, e);
                }

                Log.Info("收到千牛新消息事件: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));
                var chatRes = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (chatRes == null || chatRes.result == null)
                {
                    Log.Error("收到新消息但无法解析: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));
                    return;
                }

                var messages = chatRes.result
                    .Where(m => m != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();

                // 同一批次和随后几秒到达的消息全部进入按买家隔离的聚合器。
                // 聚合器只在买家停止输入后生成一次答案，不再丢弃较早的短片段。
                foreach (var message in messages)
                {
                    try
                    {
                        var processingTask = ProcessIncomingMessageAsync(message);
                        if (processingTask != null && !processingTask.IsCompleted)
                        {
                            // Order replies have a durable action ledger and may take much longer
                            // than ingress. Observe them asynchronously so the shared receive gate
                            // is released for the next buyer message instead of serializing on send.
                            _ = ObserveInboundProcessingAsync(processingTask);
                        }
                        else if (processingTask != null)
                        {
                            await processingTask;
                        }
                    }
                    catch (Exception ex)
                    {
                        // One malformed or transiently failing message must not abort the
                        // remaining messages in the same Qianniu payload.
                        Log.ErrorWithMaxCount("单条千牛消息处理失败，已隔离并继续处理同批次后续消息: " + ex.Message, 20);
                    }
                    await Task.Delay(30);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
                finally
                {
                    _incomingMessageGate.Release();
                }
            }
        }

        private async Task ObserveInboundProcessingAsync(Task processingTask)
        {
            if (processingTask == null) return;
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Info("异步订单入站处理已取消；买家消息接收门禁不受影响。");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("异步订单入站处理失败（独立于消息接收门禁）: " + ex.Message, 20);
            }
        }

        private void Cdp_EvMessageNotity(object sender, MessageNotifyEventArgs e)
        {
            if (EvMessageNotity != null)
            {
                EvMessageNotity(this, e);
            }
        }

        private void Cdp_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            CurQN = this;
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家会话切换已拒绝，不污染当前buyer也不触发买家切换订阅: reason=" + nonBuyerReason);
                return;
            }
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "buyerSwitched");
            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, e);
            }
        }

        public static QN GetByShopIdentity(LocalUser seller)
        {
            if (seller == null) throw new ArgumentNullException("seller");
            var shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(seller);
            lock (QNSetLock)
            {
                var matches = QNSet
                    .Where(q => q != null && q._seller != null)
                    .Select(q => new { Qn = q, ShopKey = TryGetShopKey(q._seller) })
                    .Where(x => string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count > 1 && matches.Any(x =>
                    !ShopIdentityResolver.Resolve(x.Qn._seller).HasStableSellerId))
                    throw new InvalidOperationException("昵称回退身份对应多个千牛实例，已阻止合并。");

                var qn = matches
                    .OrderByDescending(x => ActiveShopSessionRegistry.IsActive(x.Qn))
                    .Select(x => x.Qn)
                    .FirstOrDefault();
                if (qn == null)
                {
                    qn = new QN(seller);
                    QNSet.Add(qn);
                }
                else
                {
                    // Same stable ShopKey may receive refreshed login metadata/nickname.
                    qn._seller = seller;
                }
                return qn;
            }
        }

        // Compatibility alias. Identity is resolved by TargetId/ShopKey, not nick or display.
        public static QN GetByNick(LocalUser seller)
        {
            return GetByShopIdentity(seller);
        }

        public static QN FindExistingByShopKey(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return null;
            lock (QNSetLock)
            {
                return QNSet
                    .Where(q => q != null && q._seller != null)
                    .Select(q => new { Qn = q, ShopKey = TryGetShopKey(q._seller) })
                    .Where(x => string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal))
                    .OrderByDescending(x => ActiveShopSessionRegistry.IsActive(x.Qn))
                    .Select(x => x.Qn)
                    .FirstOrDefault();
            }
        }

        public static QN FindExistingBySellerNick(string sellerNick)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            if (sellerNick.Length == 0) return null;

            var scopedShop = ShopSettingsScope.Current;
            if (scopedShop != null)
            {
                return string.Equals((scopedShop.SellerNick ?? string.Empty).Trim(),
                    sellerNick, StringComparison.Ordinal)
                    ? FindExistingByShopKey(scopedShop.ShopKey)
                    : null;
            }

            var activeShopKey = ActiveShopSessionRegistry.GetActiveShopKey();
            if (activeShopKey.Length > 0)
            {
                if (!string.Equals(ActiveShopSessionRegistry.GetActiveSellerNick(),
                    sellerNick, StringComparison.Ordinal)) return null;
                return FindExistingByShopKey(activeShopKey);
            }

            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick);
                return FindExistingByShopKey(shop.ShopKey);
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetShopKey(LocalUser seller)
        {
            try { return ShopIdentityResolver.Resolve(seller).ShopKey; }
            catch { return string.Empty; }
        }

        public void SendTimiMsg(string userId, string smartTip)
        {
            cdp.SendTimiMsg(userId, smartTip);
        }

        public void TransferContact(string contactID, string targetID, string reason = "")
        {
            cdp.TransferContact(contactID, targetID, reason);
        }

        public void LightOff(string ccode)
        {
            cdp.LightOff(ccode);
        }

        public void MarkRead(string ccode, string clientId, string messageId)
        {
            cdp.MarkRead(ccode, clientId, messageId);
        }

        public async Task<LocalUserResponse> GetCurrentUser()
        {
            return await cdp.GetCurrentUser();
        }

        public void InsertText2Inputbox(string uid, string text)
        {
            cdp.InsertText2Inputbox(uid, text);
        }

        public async Task<bool> IsInputboxEmpty()
        {
            return await cdp.IsInputboxEmpty();
        }

        public void BrowserUrl(string url)
        {
            cdp.BrowserUrl(url);
        }

        public void SendRemindPayCard(string encryptedBuyerId, string orderId)
        {
            cdp.SendRemindPayCard(encryptedBuyerId, orderId);
        }

        public void RecallMessage(string ccode, string clientId, string messageId)
        {
            cdp.RecallMessage(ccode, clientId, messageId);
        }

        public void OpenChat(string nick)
        {
            cdp.OpenChat(nick);
        }

        public void SendCoupon(string buyerNick, string activityId)
        {
            cdp.SendCoupon(buyerNick, activityId);
        }

        public void CloseChat(string contactID)
        {
            cdp.CloseChat(contactID);
        }

        public void GetRemoteHisMsg(string ccode)
        {
            cdp.GetRemoteHisMsg(ccode);
        }

        public async Task<AccountStatusResponse> GetAccountStatus()
        {
            return await cdp.GetAccountStatus();
        }

        public async Task<ItemRecordResponse> GetItemRecords(string encryptId)
        {
            return await cdp.GetItemRecords(encryptId);
        }

        public async Task<SearchUserResponse> SearchBuyerUser(string searchQuery)
        {
            return await cdp.SearchBuyerUser(searchQuery);
        }

        public async Task<BuyerInfoResponse> GetBuyerInfo(string encryptId)
        {
            return await cdp.GetBuyerInfo(encryptId);
        }

        public async Task<ZnkfTradeQueryResponse> GetBuyerTrades(string securityBuyerUid, string bizOrderId)
        {
            return await cdp.GetBuyerTrades(securityBuyerUid, bizOrderId);
        }

        public async Task<ConversationResponse> GetCurrentConversationID()
        {
            return await cdp.GetCurrentConversationID();
        }
    }
}
