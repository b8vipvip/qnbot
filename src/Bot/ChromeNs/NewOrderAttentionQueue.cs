using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class OrderAttentionItem
    {
        public OrderSnapshot Snapshot { get; set; }
        public CDPClient SourceClient { get; set; }
        public DateTime EnqueuedAt { get; set; }
        public int Attempts { get; set; }
        public DateTime LastUiUpdateAt { get; set; }
    }

    internal static class OrderAttentionUiService
    {
        private static readonly ConcurrentDictionary<string, CtlConversation> Cards =
            new ConcurrentDictionary<string, CtlConversation>(StringComparer.Ordinal);

        public static void ShowPending(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ShopKey)) return;

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeByShopKey(snapshot.ShopKey); }
            catch { return; }
            if (shop == null
                || !string.Equals(shop.SellerNick, (snapshot.Seller ?? string.Empty).Trim(), StringComparison.Ordinal))
                return;

            var key = Key(snapshot);
            if (Cards.ContainsKey(key)) return;
            var question = "【新订单待处理】" + (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                + " · 订单号 " + snapshot.OrderId;
            // Desk.Inst is ShopSettingsScope-aware. Resolve it under the order's exact
            // ShopKey so a background B-store order is never rendered into the active A-store panel.
            using (ShopSettingsScope.Enter(shop, false))
            {
                var desk = Desk.Inst;
                if (desk == null) return;
                var ctl = desk.AddConversation(
                    snapshot.Seller,
                    snapshot.Buyer,
                    question,
                    snapshot.BuildSummary(),
                    false,
                    "新订单待处理");
                if (ctl == null) return;
                Cards[key] = ctl;
                ctl.SetProcessing(OrderAttentionSettings.IsEnabled()
                    ? "订单已进入待处理队列，等待Bot空闲后自动切换到该买家..."
                    : "订单已识别；空闲自动切换当前已关闭。"
                );
            }
        }

        public static void SetDeferred(OrderSnapshot snapshot, string reason)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetProcessing("新订单等待中：" + (reason ?? "当前仍有任务"));
        }

        public static void SetFocused(OrderSnapshot snapshot)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus("已自动切换到该买家会话，等待处理", true);
        }

        public static void SetFocusFailed(OrderSnapshot snapshot, string reason)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus("未自动切换：" + (reason ?? "未知原因") + "，订单仍保留在右侧待处理", false);
        }

        public static void SetReplyResult(OrderSnapshot snapshot, bool delivered)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus(
                delivered ? "已切换并发送下单自动消息" : "已识别订单，但下单自动消息未成功发送",
                delivered);
        }

        private static string Key(OrderSnapshot snapshot)
        {
            return (snapshot.ShopKey ?? string.Empty).Trim()
                + "#" + (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }
    }

    public partial class QN
    {
        private sealed class AutoFocusCheck
        {
            public bool Allowed;
            public string Reason;
        }

        private sealed class InputboxCheck
        {
            public bool Success;
            public bool Empty;
        }

        private readonly object _orderAttentionSync = new object();
        private readonly List<OrderAttentionItem> _orderAttentionItems = new List<OrderAttentionItem>();
        private int _orderAttentionWorkerRunning;
        private DateTime _orderAttentionLastAutoFocusAt = DateTime.MinValue;
        private string _orderAttentionLastAutoFocusedBuyer = string.Empty;
        private string _orderAttentionLastObservedBuyer = string.Empty;
        private DateTime _orderAttentionLastObservedBuyerAt = DateTime.MinValue;

        internal void EnqueueNewOrderAttention(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Buyer) || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;
            OrderAttentionUiService.ShowPending(snapshot);
            if (!OrderAttentionSettings.IsEnabled()) return;

            var item = new OrderAttentionItem
            {
                Snapshot = snapshot,
                SourceClient = cdp,
                EnqueuedAt = DateTime.Now,
                Attempts = 0,
                LastUiUpdateAt = DateTime.MinValue
            };
            if (!TryRefreshOrderAttentionSource(item))
            {
                OrderAttentionUiService.SetFocusFailed(snapshot, "订单来源店铺或CDP会话身份无法确认，已阻止自动切换");
                return;
            }

            lock (_orderAttentionSync)
            {
                var key = AttentionKey(snapshot);
                if (_orderAttentionItems.Any(x => x != null && x.Snapshot != null && AttentionKey(x.Snapshot) == key)) return;
                _orderAttentionItems.Add(item);
            }

            Log.Info("新订单已加入空闲自动切换队列: seller=" + snapshot.Seller
                + ", buyer=" + snapshot.Buyer
                + ", orderId=" + snapshot.OrderId
                + ", event=" + snapshot.EventType);
            StartOrderAttentionWorker();
        }

        private void StartOrderAttentionWorker()
        {
            if (Interlocked.CompareExchange(ref _orderAttentionWorkerRunning, 1, 0) != 0) return;
            Task.Run(RunOrderAttentionWorkerAsync);
        }

        private async Task RunOrderAttentionWorkerAsync()
        {
            try
            {
                while (true)
                {
                    var item = PeekNextOrderAttention();
                    if (item == null) return;
                    if (!OrderAttentionSettings.IsEnabled())
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocusFailed(item.Snapshot, "设置中已关闭空闲自动切换");
                        continue;
                    }
                    if (DateTime.Now - item.EnqueuedAt > TimeSpan.FromMinutes(30))
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocusFailed(item.Snapshot, "等待超过30分钟");
                        continue;
                    }

                    var check = await CanAutoFocusOrderAsync(item);
                    if (!check.Allowed)
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, check.Reason);
                        await Task.Delay(700);
                        continue;
                    }

                    // 二次稳定确认，避免刚判定为空闲时恰好开始生成、发送或人工输入。
                    await Task.Delay(650);
                    check = await CanAutoFocusOrderAsync(item);
                    if (!check.Allowed)
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, check.Reason);
                        await Task.Delay(500);
                        continue;
                    }

                    var focused = await FocusOrderBuyerAsync(item);
                    if (focused)
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocused(item.Snapshot);
                        await Task.Delay(TimeSpan.FromSeconds(OrderAttentionSettings.GetSwitchIntervalSeconds()));
                    }
                    else
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, "目标会话暂时无法确认，稍后重试");
                        if (item.Attempts >= 30)
                        {
                            RemoveOrderAttention(item);
                            OrderAttentionUiService.SetFocusFailed(item.Snapshot, "连续多次无法确认目标买家会话");
                        }
                        await Task.Delay(1200);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("新订单自动切换队列异常：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _orderAttentionWorkerRunning, 0);
                lock (_orderAttentionSync)
                {
                    if (_orderAttentionItems.Count > 0 && OrderAttentionSettings.IsEnabled())
                    {
                        StartOrderAttentionWorker();
                    }
                }
            }
        }

        private OrderAttentionItem PeekNextOrderAttention()
        {
            lock (_orderAttentionSync)
            {
                return _orderAttentionItems
                    .Where(x => x != null && x.Snapshot != null)
                    .OrderByDescending(x => x.Snapshot.EventType == OrderEventType.Paid)
                    .ThenBy(x => x.Snapshot.EventTime)
                    .ThenBy(x => x.EnqueuedAt)
                    .FirstOrDefault();
            }
        }

        private void RemoveOrderAttention(OrderAttentionItem item)
        {
            lock (_orderAttentionSync)
            {
                _orderAttentionItems.Remove(item);
            }
        }

        private void UpdateDeferredUi(OrderAttentionItem item, string reason)
        {
            if (item == null) return;
            if (DateTime.Now - item.LastUiUpdateAt < TimeSpan.FromSeconds(2)) return;
            item.LastUiUpdateAt = DateTime.Now;
            OrderAttentionUiService.SetDeferred(item.Snapshot, reason);
        }

        private async Task<AutoFocusCheck> CanAutoFocusOrderAsync(OrderAttentionItem item)
        {
            var snapshot = item == null ? null : item.Snapshot;
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Buyer)) return Denied("订单缺少买家信息");
            if (!Params.Robot.CanUseRobotReal) return Denied("Bot当前未启用");
            if (!TryRefreshOrderAttentionSource(item)) return Denied("订单来源店铺身份或千牛CDP会话已变化");
            if (_incomingMessageGate.CurrentCount < 1) return Denied("正在处理千牛新消息");
            if (_sendGate.CurrentCount < 1) return Denied("正在切换会话或发送消息");
            if (_backgroundRecoveryGate.CurrentCount < 1) return Denied("正在补抓后台消息");

            string reason;
            if (!BotActivityCoordinator.IsSafeToAutoFocus(snapshot.Seller, out reason, snapshot.ShopKey)) return Denied(reason);

            var protectSeconds = OrderAttentionSettings.GetHumanProtectionSeconds();
            var lastSellerEcho = LastSellerEchoTimeForShop(snapshot.ShopKey);
            if (lastSellerEcho != DateTime.MinValue
                && DateTime.Now - lastSellerEcho < TimeSpan.FromSeconds(protectSeconds))
            {
                return Denied("客服或Bot刚发送过消息，人工操作保护中");
            }

            var input = await TryGetInputboxEmptyAsync();
            if (!IsOrderAttentionSourceCurrent(item)) return Denied("读取输入框期间订单来源CDP会话发生变化");
            if (!input.Success) return Denied("暂时无法确认输入框状态");
            if (!input.Empty)
            {
                if (rpa != null && await rpa.IsKnownBotOwnedDraftAsync().ConfigureAwait(false))
                {
                    return Denied("Bot发送失败草稿仍在输入框，等待发送恢复且不计为人工操作");
                }
                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "客服输入框中存在未发送内容", snapshot.ShopKey);
                return Denied("客服正在输入消息");
            }

            var current = await TryGetCurrentBuyerAsync();
            if (!IsOrderAttentionSourceCurrent(item)) return Denied("读取当前买家期间订单来源CDP会话发生变化");
            ObserveVisibleBuyer(snapshot.ShopKey, snapshot.Seller, current);
            if (!BotActivityCoordinator.IsSafeToAutoFocus(snapshot.Seller, out reason, snapshot.ShopKey)) return Denied(reason);
            if (!string.Equals(current, snapshot.Buyer, StringComparison.Ordinal)
                && _orderAttentionLastObservedBuyerAt != DateTime.MinValue
                && DateTime.Now - _orderAttentionLastObservedBuyerAt < TimeSpan.FromSeconds(2))
            {
                return Denied("当前买家会话刚发生变化，等待界面稳定");
            }

            var interval = OrderAttentionSettings.GetSwitchIntervalSeconds();
            if (_orderAttentionLastAutoFocusAt != DateTime.MinValue
                && DateTime.Now - _orderAttentionLastAutoFocusAt < TimeSpan.FromSeconds(interval))
            {
                return Denied("等待最短自动切换间隔");
            }
            return new AutoFocusCheck { Allowed = true, Reason = string.Empty };
        }

        private async Task<bool> FocusOrderBuyerAsync(OrderAttentionItem item)
        {
            var snapshot = item == null ? null : item.Snapshot;
            if (snapshot == null || !TryRefreshOrderAttentionSource(item)) return false;
            await _sendGate.WaitAsync();
            using (BotActivityCoordinator.Begin("新订单自动切换", snapshot.Seller, snapshot.Buyer, snapshot.ShopKey))
            {
                try
                {
                    if (!IsOrderAttentionSourceCurrent(item)) return false;
                    var input = await TryGetInputboxEmptyAsync();
                    if (!IsOrderAttentionSourceCurrent(item)) return false;
                    if (!input.Success || !input.Empty)
                    {
                        if (input.Success && !input.Empty)
                        {
                            if (rpa != null && await rpa.IsKnownBotOwnedDraftAsync().ConfigureAwait(false))
                            {
                                Log.Info("自动切换前发现Bot自有失败草稿，未标记人工操作: seller="
                                    + snapshot.Seller + ", buyer=" + snapshot.Buyer);
                            }
                            else
                            {
                                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "自动切换前检测到客服输入内容", snapshot.ShopKey);
                            }
                        }
                        return false;
                    }

                    var current = await TryGetCurrentBuyerAsync();
                    if (!IsOrderAttentionSourceCurrent(item)) return false;
                    if (string.Equals(current, snapshot.Buyer, StringComparison.Ordinal))
                    {
                        SetActiveConversationByNick(snapshot.Seller, snapshot.Buyer, "newOrderAlreadyActive");
                        _orderAttentionLastAutoFocusAt = DateTime.Now;
                        _orderAttentionLastAutoFocusedBuyer = snapshot.Buyer;
                        return true;
                    }

                    Log.Info("Bot空闲，准备自动切换到新下单买家: seller=" + snapshot.Seller
                        + ", buyer=" + snapshot.Buyer
                        + ", orderId=" + snapshot.OrderId
                        + ", current=" + current);
                    if (!IsOrderAttentionSourceCurrent(item)) return false;
                    OpenChat(snapshot.Buyer);
                    if (!IsOrderAttentionSourceCurrent(item)) return false;
                    for (var attempt = 0; attempt < 24; attempt++)
                    {
                        await Task.Delay(250);
                        if (!IsOrderAttentionSourceCurrent(item)) return false;
                        current = await TryGetCurrentBuyerAsync();
                        if (!IsOrderAttentionSourceCurrent(item)) return false;
                        if (string.Equals(current, snapshot.Buyer, StringComparison.Ordinal))
                        {
                            SetActiveConversationByNick(snapshot.Seller, snapshot.Buyer, "newOrderAutoFocus");
                            _orderAttentionLastAutoFocusAt = DateTime.Now;
                            _orderAttentionLastAutoFocusedBuyer = snapshot.Buyer;
                            _orderAttentionLastObservedBuyer = snapshot.Buyer;
                            _orderAttentionLastObservedBuyerAt = DateTime.Now;
                            Log.Info("新订单买家自动切换成功: buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId);
                            return true;
                        }
                    }
                    Log.Info("新订单买家自动切换未确认: buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Info("新订单买家自动切换异常: buyer=" + snapshot.Buyer + ", error=" + ex.Message);
                    return false;
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }

        private bool TryRefreshOrderAttentionSource(OrderAttentionItem item)
        {
            var snapshot = item == null ? null : item.Snapshot;
            var shopKey = snapshot == null ? string.Empty : (snapshot.ShopKey ?? string.Empty).Trim();
            var sourceClient = cdp;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (shopKey.Length == 0 || sourceClient == null || server == null
                || sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired
                || !string.Equals(sourceClient.ShopKey, shopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shopKey, sourceClient.SessionId))
                return false;

            try
            {
                var runtimeShop = ShopContextLocator.ResolveRuntimeBySeller(this);
                if (runtimeShop == null
                    || !string.Equals(runtimeShop.ShopKey, shopKey, StringComparison.Ordinal)
                    || !ReferenceEquals(ShopContextLocator.FindConnectedQnByShopKey(shopKey, sourceClient.SessionId), this))
                    return false;

                // A reconnect is accepted only after the new physical session proves the
                // same persisted ShopKey. A same-nickname different shop cannot inherit
                // the queued order's focus action.
                item.SourceClient = sourceClient;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsOrderAttentionSourceCurrent(OrderAttentionItem item)
        {
            var snapshot = item == null ? null : item.Snapshot;
            var sourceClient = item == null ? null : item.SourceClient;
            var shopKey = snapshot == null ? string.Empty : (snapshot.ShopKey ?? string.Empty).Trim();
            var server = MyWebSocketServer.WSocketSvrInst;
            if (shopKey.Length == 0 || sourceClient == null || server == null
                || !ReferenceEquals(cdp, sourceClient)
                || sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired
                || !string.Equals(sourceClient.ShopKey, shopKey, StringComparison.Ordinal)
                || !server.IsAuthoritativeShopSession(shopKey, sourceClient.SessionId))
                return false;
            try
            {
                var runtimeShop = ShopContextLocator.ResolveRuntimeBySeller(this);
                return runtimeShop != null
                    && string.Equals(runtimeShop.ShopKey, shopKey, StringComparison.Ordinal)
                    && ReferenceEquals(ShopContextLocator.FindConnectedQnByShopKey(shopKey, sourceClient.SessionId), this);
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> TryGetCurrentBuyerAsync()
        {
            try
            {
                var task = GetCurrentConversationID();
                var completed = await Task.WhenAny(task, Task.Delay(1200));
                if (completed != task) return string.Empty;
                var response = await task;
                return response == null || response.Result == null
                    ? string.Empty
                    : (response.Result.Nick ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }

        private async Task<InputboxCheck> TryGetInputboxEmptyAsync()
        {
            try
            {
                var task = IsInputboxEmpty();
                var completed = await Task.WhenAny(task, Task.Delay(1200));
                if (completed != task) return new InputboxCheck { Success = false, Empty = false };
                return new InputboxCheck { Success = true, Empty = await task };
            }
            catch { return new InputboxCheck { Success = false, Empty = false }; }
        }

        private void ObserveVisibleBuyer(string shopKey, string seller, string currentBuyer)
        {
            currentBuyer = (currentBuyer ?? string.Empty).Trim();
            if (currentBuyer.Length == 0) return;
            if (_orderAttentionLastObservedBuyer.Length == 0)
            {
                _orderAttentionLastObservedBuyer = currentBuyer;
                _orderAttentionLastObservedBuyerAt = DateTime.Now;
                return;
            }
            if (string.Equals(_orderAttentionLastObservedBuyer, currentBuyer, StringComparison.Ordinal)) return;

            var wasOwnRecentSwitch = string.Equals(_orderAttentionLastAutoFocusedBuyer, currentBuyer, StringComparison.Ordinal)
                && _orderAttentionLastAutoFocusAt != DateTime.MinValue
                && DateTime.Now - _orderAttentionLastAutoFocusAt < TimeSpan.FromSeconds(3);
            _orderAttentionLastObservedBuyer = currentBuyer;
            _orderAttentionLastObservedBuyerAt = DateTime.Now;
            if (!wasOwnRecentSwitch)
            {
                BotActivityCoordinator.MarkHumanInteraction(seller, "客服或其他流程刚切换到买家 " + currentBuyer, shopKey);
            }
        }

        private static AutoFocusCheck Denied(string reason)
        {
            return new AutoFocusCheck { Allowed = false, Reason = reason ?? string.Empty };
        }

        private static string AttentionKey(OrderSnapshot snapshot)
        {
            return (snapshot.ShopKey ?? string.Empty).Trim()
                + "#" + (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }
    }
}
