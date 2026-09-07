using Bot.Automation.ChatDeskNs;
using Bot.Options;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal enum AutoDeliveryAttemptOutcome
    {
        Deferred = 0,
        Completed = 1,
        TerminalNoAction = 2,
        ConfirmationUncertain = 3
    }

    internal sealed class AutoDeliveryAttemptResult
    {
        public AutoDeliveryAttemptOutcome Outcome { get; set; }
        public string Reason { get; set; }

        public static AutoDeliveryAttemptResult Deferred(string reason)
        {
            return new AutoDeliveryAttemptResult { Outcome = AutoDeliveryAttemptOutcome.Deferred, Reason = reason ?? string.Empty };
        }

        public static AutoDeliveryAttemptResult Completed(string reason)
        {
            return new AutoDeliveryAttemptResult { Outcome = AutoDeliveryAttemptOutcome.Completed, Reason = reason ?? string.Empty };
        }

        public static AutoDeliveryAttemptResult Terminal(string reason)
        {
            return new AutoDeliveryAttemptResult { Outcome = AutoDeliveryAttemptOutcome.TerminalNoAction, Reason = reason ?? string.Empty };
        }

        public static AutoDeliveryAttemptResult Uncertain(string reason)
        {
            return new AutoDeliveryAttemptResult { Outcome = AutoDeliveryAttemptOutcome.ConfirmationUncertain, Reason = reason ?? string.Empty };
        }
    }

    internal static class AutoDeliveryCoordinator
    {
        private sealed class PendingRecord
        {
            public string Key { get; set; }
            public OrderSnapshot Snapshot { get; set; }
            public DateTime EnqueuedAt { get; set; }
            public DateTime DueAt { get; set; }
            public DateTime NextAttemptAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public int Attempts { get; set; }
            public int ConfirmationUncertainCount { get; set; }
            // Persisted BEFORE the irreversible “确认发货” click. Once present, every future
            // attempt is read-only, including after a process crash/restart. This prevents a
            // network/UI ambiguity from ever causing the Bot to submit the same shipment twice.
            public DateTime? ConfirmationIntentAt { get; set; }
        }

        private sealed class StateDocument
        {
            public int Schema { get; set; }
            public List<PendingRecord> Pending { get; set; }

            public StateDocument()
            {
                Schema = 1;
                Pending = new List<PendingRecord>();
            }
        }

        private static readonly object Sync = new object();
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan UncertainRetryDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(24);
        private static StateDocument _state;
        private static Timer _timer;
        private static int _initialized;
        private static int _workerRunning;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            lock (Sync)
            {
                _state = LoadState();
                CleanupExpiredLocked(DateTime.Now);
                SaveStateLocked();
            }
            _timer = new Timer(_ => ScheduleWorker(), null, 3000, 5000);
            Log.Info("虚拟商品自动发货协调器已启动：仅处理准确订单号 + 待发货；固定动作=发货→无需物流→确认发货；确认动作前写入持久防重屏障。 pending="
                + PendingCount());
        }

        public static void Enqueue(OrderSnapshot snapshot)
        {
            if (snapshot == null
                || string.IsNullOrWhiteSpace(snapshot.Seller)
                || string.IsNullOrWhiteSpace(snapshot.Buyer)
                || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;

            Initialize();
            var cfg = AutoDeliverySettings.Load(snapshot.Seller);
            if (cfg == null || !cfg.Enabled) return;
            if (snapshot.EventType != OrderEventType.Created && snapshot.EventType != OrderEventType.Paid) return;

            var now = DateTime.Now;
            var anchor = ResolveAnchor(snapshot, now);
            var due = anchor.AddMinutes(cfg.DelayMinutes);
            if (due < now) due = now;
            var key = BuildKey(snapshot.Seller, snapshot.OrderId);

            lock (Sync)
            {
                var existing = _state.Pending.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new PendingRecord
                    {
                        Key = key,
                        Snapshot = CloneSnapshot(snapshot),
                        EnqueuedAt = now,
                        DueAt = due,
                        NextAttemptAt = due,
                        ExpiresAt = now.Add(MaxPendingAge),
                        Attempts = 0,
                        ConfirmationUncertainCount = 0,
                        ConfirmationIntentAt = null
                    };
                    _state.Pending.Add(existing);
                    Log.Info("虚拟商品自动发货任务已入队: seller=" + snapshot.Seller
                        + ", buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId
                        + ", delayMinutes=" + cfg.DelayMinutes + ", due=" + due.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                else
                {
                    MergeSnapshot(existing.Snapshot, snapshot);
                    var refreshedAnchor = ResolveAnchor(existing.Snapshot, now);
                    existing.DueAt = refreshedAnchor.AddMinutes(cfg.DelayMinutes);
                    if (existing.DueAt < now) existing.DueAt = now;
                    // Never let a later Created/Paid event clear or accelerate the durable
                    // confirmation barrier. After confirm intent, only read-only verification runs.
                    if (!existing.ConfirmationIntentAt.HasValue && existing.NextAttemptAt < existing.DueAt)
                        existing.NextAttemptAt = existing.DueAt;
                    existing.ExpiresAt = now.Add(MaxPendingAge);
                    Log.Info("虚拟商品自动发货任务已用新订单状态刷新: seller=" + snapshot.Seller
                        + ", orderId=" + snapshot.OrderId + ", due=" + existing.DueAt.ToString("yyyy-MM-dd HH:mm:ss")
                        + ", confirmationIntent=" + existing.ConfirmationIntentAt.HasValue);
                }
                CleanupExpiredLocked(now);
                SaveStateLocked();
            }
            ScheduleWorker();
        }

        public static void ReconfigureSeller(string seller, bool enabled, int delayMinutes)
        {
            Initialize();
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            delayMinutes = AutoDeliverySettings.Clamp(delayMinutes);
            var now = DateTime.Now;
            lock (Sync)
            {
                if (!enabled)
                {
                    var removed = _state.Pending.RemoveAll(x => x != null
                        && string.Equals((x.Snapshot == null ? string.Empty : x.Snapshot.Seller) ?? string.Empty, seller, StringComparison.Ordinal));
                    if (removed > 0)
                        Log.Info("关闭自动发货后已取消该店铺未执行任务: seller=" + seller + ", count=" + removed);
                }
                else
                {
                    foreach (var record in _state.Pending.Where(x => x != null && x.Snapshot != null
                        && string.Equals(x.Snapshot.Seller ?? string.Empty, seller, StringComparison.Ordinal)))
                    {
                        var anchor = ResolveAnchor(record.Snapshot, now);
                        record.DueAt = anchor.AddMinutes(delayMinutes);
                        if (record.DueAt < now) record.DueAt = now;
                        if (!record.ConfirmationIntentAt.HasValue)
                            record.NextAttemptAt = record.DueAt;
                    }
                }
                SaveStateLocked();
            }
            if (enabled) ScheduleWorker();
        }

        /// <summary>
        /// Durable write-ahead barrier for the irreversible confirm action. The caller MUST NOT
        /// click “确认发货” unless this returns true. Once written, retries are permanently read-only
        /// for this queue record, including after a crash between the click and result observation.
        /// </summary>
        public static bool TryPersistConfirmationIntent(string seller, string orderId)
        {
            Initialize();
            var key = BuildKey(seller, orderId);
            lock (Sync)
            {
                var live = FindLiveRecordLocked(key);
                if (live == null || live.Snapshot == null) return false;
                if (live.ConfirmationIntentAt.HasValue) return true;

                var now = DateTime.Now;
                live.ConfirmationIntentAt = now;
                live.NextAttemptAt = now.Add(UncertainRetryDelay);
                if (!SaveStateLocked())
                {
                    // No confirm click has happened yet. Revert the in-memory barrier so the same
                    // live process can retry persistence later; failing closed is safer than an
                    // irreversible click that cannot survive a crash.
                    live.ConfirmationIntentAt = null;
                    live.NextAttemptAt = now.Add(RetryDelay);
                    return false;
                }

                Log.Info("自动发货确认动作持久防重屏障已写入: seller=" + seller
                    + ", orderId=" + orderId + ", at=" + now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                return true;
            }
        }

        private static void ScheduleWorker()
        {
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0) return;
            Task.Run(RunWorkerAsync);
        }

        private static async Task RunWorkerAsync()
        {
            try
            {
                while (true)
                {
                    PendingRecord record = null;
                    var now = DateTime.Now;
                    lock (Sync)
                    {
                        CleanupExpiredLocked(now);
                        record = _state.Pending
                            .Where(x => x != null && x.Snapshot != null && x.NextAttemptAt <= now)
                            .OrderBy(x => x.NextAttemptAt)
                            .FirstOrDefault();
                        if (record == null)
                        {
                            SaveStateLocked();
                            return;
                        }
                        // Reserve this exact record before leaving the lock so a timer tick cannot
                        // launch the same irreversible order action concurrently.
                        record.NextAttemptAt = now.Add(RetryDelay);
                        record.Attempts++;
                        SaveStateLocked();
                    }

                    await ProcessRecordAsync(record).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("虚拟商品自动发货队列异常：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _workerRunning, 0);
            }
        }

        private static async Task ProcessRecordAsync(PendingRecord record)
        {
            if (record == null || record.Snapshot == null) return;
            var snapshot = record.Snapshot;
            var cfg = AutoDeliverySettings.Load(snapshot.Seller);
            if (cfg == null || !cfg.Enabled)
            {
                RemoveRecord(record, "功能已关闭");
                return;
            }

            var qn = QN.FindExistingBySellerNick(snapshot.Seller);
            if (qn == null)
            {
                DeferRecord(record, RetryDelay, "当前店铺千牛会话尚未连接");
                return;
            }

            var verificationOnly = record.ConfirmationIntentAt.HasValue;
            AutoDeliveryAttemptResult result;
            try
            {
                result = await qn.TryExecuteVirtualGoodsAutoDeliveryAsync(snapshot, verificationOnly).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核异常：" + ex.Message)
                    : AutoDeliveryAttemptResult.Deferred("执行异常：" + ex.Message);
            }
            if (result == null)
                result = verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核未返回结果")
                    : AutoDeliveryAttemptResult.Deferred("未返回执行结果");

            switch (result.Outcome)
            {
                case AutoDeliveryAttemptOutcome.Completed:
                    RemoveRecord(record, "已确认完成：" + result.Reason);
                    break;
                case AutoDeliveryAttemptOutcome.TerminalNoAction:
                    RemoveRecord(record, "订单无需再执行：" + result.Reason);
                    break;
                case AutoDeliveryAttemptOutcome.ConfirmationUncertain:
                    lock (Sync)
                    {
                        var live = FindLiveRecordLocked(record.Key);
                        if (live == null) return;
                        live.ConfirmationUncertainCount++;
                        if (live.ConfirmationUncertainCount >= 5)
                        {
                            _state.Pending.Remove(live);
                            Log.Error("自动发货确认结果连续不确定，已停止任务且不会重复确认发货: seller="
                                + snapshot.Seller + ", buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId
                                + ", reason=" + result.Reason);
                        }
                        else
                        {
                            live.NextAttemptAt = DateTime.Now.Add(UncertainRetryDelay);
                            Log.Info("自动发货确认后状态暂未确定，进入只读复核等待: seller=" + snapshot.Seller
                                + ", orderId=" + snapshot.OrderId + ", uncertain=" + live.ConfirmationUncertainCount
                                + "/5, next=" + live.NextAttemptAt.ToString("HH:mm:ss") + ", reason=" + result.Reason
                                + ", repeatConfirm=false");
                        }
                        SaveStateLocked();
                    }
                    break;
                default:
                    DeferRecord(record, RetryDelay, result.Reason);
                    break;
            }
        }

        private static void DeferRecord(PendingRecord record, TimeSpan delay, string reason)
        {
            lock (Sync)
            {
                var live = FindLiveRecordLocked(record.Key);
                if (live == null) return;
                live.NextAttemptAt = DateTime.Now.Add(delay);
                SaveStateLocked();
            }
            if (record.Attempts == 1 || record.Attempts % 8 == 0)
            {
                Log.Info("虚拟商品自动发货暂缓: seller=" + record.Snapshot.Seller
                    + ", buyer=" + record.Snapshot.Buyer + ", orderId=" + record.Snapshot.OrderId
                    + ", reason=" + (reason ?? string.Empty));
            }
        }

        private static void RemoveRecord(PendingRecord record, string reason)
        {
            lock (Sync)
            {
                var live = FindLiveRecordLocked(record.Key);
                if (live != null) _state.Pending.Remove(live);
                SaveStateLocked();
            }
            Log.Info("虚拟商品自动发货任务结束: seller=" + record.Snapshot.Seller
                + ", buyer=" + record.Snapshot.Buyer + ", orderId=" + record.Snapshot.OrderId
                + ", detail=" + (reason ?? string.Empty));
        }

        private static PendingRecord FindLiveRecordLocked(string key)
        {
            return _state.Pending.FirstOrDefault(x => x != null && string.Equals(x.Key, key, StringComparison.Ordinal));
        }

        private static int PendingCount()
        {
            lock (Sync) return _state == null || _state.Pending == null ? 0 : _state.Pending.Count;
        }

        private static void CleanupExpiredLocked(DateTime now)
        {
            if (_state == null) _state = new StateDocument();
            if (_state.Pending == null) _state.Pending = new List<PendingRecord>();
            var expired = _state.Pending.Where(x => x == null || x.Snapshot == null || x.ExpiresAt <= now).ToList();
            foreach (var item in expired)
            {
                if (item != null && item.Snapshot != null)
                {
                    Log.Info("虚拟商品自动发货任务已过期并停止: seller=" + item.Snapshot.Seller
                        + ", orderId=" + item.Snapshot.OrderId
                        + ", confirmationIntent=" + item.ConfirmationIntentAt.HasValue);
                }
                _state.Pending.Remove(item);
            }
        }

        private static DateTime ResolveAnchor(OrderSnapshot snapshot, DateTime fallback)
        {
            if (snapshot == null) return fallback;
            // The setting is defined as “new order + N minutes”. Prefer the order creation time.
            // If only a paid event contains a usable timestamp, fall back to PaidAt/event time.
            if (snapshot.CreatedAt.HasValue) return snapshot.CreatedAt.Value;
            if (snapshot.PaidAt.HasValue) return snapshot.PaidAt.Value;
            return snapshot.EventTime == DateTime.MinValue ? fallback : snapshot.EventTime;
        }

        private static string BuildKey(string seller, string orderId)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant() + "#" + (orderId ?? string.Empty).Trim();
        }

        private static string StatePath()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "virtual-goods-auto-delivery-queue.json");
        }

        private static StateDocument LoadState()
        {
            try
            {
                var path = StatePath();
                if (!File.Exists(path)) return new StateDocument();
                var loaded = JsonConvert.DeserializeObject<StateDocument>(File.ReadAllText(path, Encoding.UTF8));
                if (loaded == null || loaded.Schema != 1) return new StateDocument();
                if (loaded.Pending == null) loaded.Pending = new List<PendingRecord>();
                return loaded;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取虚拟商品自动发货任务状态失败，使用空队列：" + ex.Message, 5);
                return new StateDocument();
            }
        }

        private static bool SaveStateLocked()
        {
            var temp = string.Empty;
            try
            {
                var path = StatePath();
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_state, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存虚拟商品自动发货任务状态失败：" + ex.Message, 10);
                return false;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp)) File.Delete(temp);
                }
                catch { }
            }
        }

        private static OrderSnapshot CloneSnapshot(OrderSnapshot source)
        {
            if (source == null) return null;
            return JsonConvert.DeserializeObject<OrderSnapshot>(JsonConvert.SerializeObject(source));
        }

        private static void MergeSnapshot(OrderSnapshot target, OrderSnapshot incoming)
        {
            if (target == null || incoming == null) return;
            if (!string.IsNullOrWhiteSpace(incoming.Buyer)) target.Buyer = incoming.Buyer;
            if (!string.IsNullOrWhiteSpace(incoming.TradeStatus)) target.TradeStatus = incoming.TradeStatus;
            if (incoming.IsPaid.HasValue) target.IsPaid = incoming.IsPaid;
            if (incoming.CreatedAt.HasValue) target.CreatedAt = incoming.CreatedAt;
            if (incoming.PaidAt.HasValue) target.PaidAt = incoming.PaidAt;
            if (incoming.EventTime != DateTime.MinValue) target.EventTime = incoming.EventTime;
            target.EventType = incoming.EventType;
            if (!string.IsNullOrWhiteSpace(incoming.ItemTitle)) target.ItemTitle = incoming.ItemTitle;
            if (!string.IsNullOrWhiteSpace(incoming.SkuText)) target.SkuText = incoming.SkuText;
        }
    }

    public partial class QN
    {
        private sealed class AutoDeliveryDomState
        {
            [JsonProperty("found")]
            public bool Found { get; set; }
            [JsonProperty("pending")]
            public bool Pending { get; set; }
            [JsonProperty("done")]
            public bool Done { get; set; }
            [JsonProperty("terminal")]
            public bool Terminal { get; set; }
            [JsonProperty("status")]
            public string Status { get; set; }
            [JsonProperty("shipButton")]
            public bool ShipButton { get; set; }
            [JsonProperty("clicked")]
            public bool Clicked { get; set; }
        }

        private sealed class AutoDeliveryModalState
        {
            [JsonProperty("found")]
            public bool Found { get; set; }
            [JsonProperty("noLogistics")]
            public bool NoLogistics { get; set; }
            [JsonProperty("confirm")]
            public bool Confirm { get; set; }
            [JsonProperty("selected")]
            public bool Selected { get; set; }
            [JsonProperty("clicked")]
            public bool Clicked { get; set; }
        }

        internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync(
            OrderSnapshot snapshot,
            bool verificationOnly)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.OrderId))
                return AutoDeliveryAttemptResult.Terminal("订单信息无效");

            var seller = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || !DirectOrderIdentityResolver.IdentityEquals(seller, snapshot.Seller))
                return AutoDeliveryAttemptResult.Deferred("当前客服与任务店铺不一致");
            if (!AutoDeliverySettings.Load(seller).Enabled)
                return AutoDeliveryAttemptResult.Terminal("自动发货已关闭");
            if (cdp == null || rpa == null)
                return verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核时千牛控制通道尚未连接")
                    : AutoDeliveryAttemptResult.Deferred("千牛控制通道尚未连接");
            if (_sendGate.CurrentCount < 1 || _incomingMessageGate.CurrentCount < 1 || _backgroundRecoveryGate.CurrentCount < 1)
                return verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核等待当前消息/发送任务完成")
                    : AutoDeliveryAttemptResult.Deferred("当前正在处理消息、发送或后台恢复任务");

            string activityReason;
            if (!BotActivityCoordinator.IsSafeToAutoFocus(seller, out activityReason))
                return verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核等待人工操作结束：" + activityReason)
                    : AutoDeliveryAttemptResult.Deferred(activityReason);

            var input = await TryGetInputboxEmptyAsync().ConfigureAwait(false);
            if (!input.Success)
                return verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核暂时无法确认客服输入框状态")
                    : AutoDeliveryAttemptResult.Deferred("暂时无法确认客服输入框状态");
            if (!input.Empty)
            {
                if (!(await rpa.IsKnownBotOwnedDraftAsync().ConfigureAwait(false)))
                    BotActivityCoordinator.MarkHumanInteraction(seller, "自动发货前检测到客服输入内容");
                return verificationOnly
                    ? AutoDeliveryAttemptResult.Uncertain("只读复核等待客服输入结束")
                    : AutoDeliveryAttemptResult.Deferred("客服输入框存在未发送内容");
            }

            var expectedBuyer = (snapshot.Buyer ?? string.Empty).Trim();
            if (expectedBuyer.Length == 0) return AutoDeliveryAttemptResult.Terminal("订单缺少买家身份");
            var currentBuyer = await TryGetCurrentBuyerAsync().ConfigureAwait(false);
            if (!BuyerIdentityAliasService.AreEquivalent(seller, currentBuyer, expectedBuyer))
            {
                if (!BotActivityCoordinator.IsSafeToAutoFocus(seller, out activityReason))
                    return verificationOnly
                        ? AutoDeliveryAttemptResult.Uncertain("只读复核等待人工操作结束：" + activityReason)
                        : AutoDeliveryAttemptResult.Deferred(activityReason);

                Log.Info((verificationOnly ? "虚拟商品自动发货只读复核" : "虚拟商品自动发货")
                    + "准备切换目标买家: seller=" + seller
                    + ", targetBuyer=" + expectedBuyer + ", currentBuyer=" + currentBuyer
                    + ", orderId=" + snapshot.OrderId);
                OpenChat(expectedBuyer);
                var focused = false;
                for (var attempt = 0; attempt < 24; attempt++)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    currentBuyer = await TryGetCurrentBuyerAsync().ConfigureAwait(false);
                    if (BuyerIdentityAliasService.AreEquivalent(seller, currentBuyer, expectedBuyer))
                    {
                        focused = true;
                        SetActiveConversationByNick(seller, currentBuyer, "virtualGoodsAutoDelivery");
                        break;
                    }
                }
                if (!focused)
                    return verificationOnly
                        ? AutoDeliveryAttemptResult.Uncertain("只读复核无法确认已切换到订单买家会话")
                        : AutoDeliveryAttemptResult.Deferred("无法确认已切换到订单买家会话");
            }

            await _sendGate.WaitAsync().ConfigureAwait(false);
            using (BotActivityCoordinator.Begin(
                verificationOnly ? "虚拟商品自动发货只读复核" : "虚拟商品自动发货",
                seller,
                expectedBuyer))
            {
                try
                {
                    currentBuyer = await TryGetCurrentBuyerAsync().ConfigureAwait(false);
                    if (!BuyerIdentityAliasService.AreEquivalent(seller, currentBuyer, expectedBuyer))
                        return verificationOnly
                            ? AutoDeliveryAttemptResult.Uncertain("只读复核前买家会话发生变化")
                            : AutoDeliveryAttemptResult.Deferred("执行前买家会话发生变化");

                    input = await TryGetInputboxEmptyAsync().ConfigureAwait(false);
                    if (!input.Success || !input.Empty)
                        return verificationOnly
                            ? AutoDeliveryAttemptResult.Uncertain("只读复核时客服输入框不为空或状态不可确认")
                            : AutoDeliveryAttemptResult.Deferred("执行前客服输入框不为空或状态不可确认");

                    var before = await ReadOrderStateAsync(snapshot.OrderId, false).ConfigureAwait(false);
                    if (before == null || !before.Found)
                        return verificationOnly
                            ? AutoDeliveryAttemptResult.Uncertain("只读复核尚未找到准确订单号")
                            : AutoDeliveryAttemptResult.Deferred("当前买家右侧订单面板尚未找到准确订单号");
                    if (before.Done)
                        return AutoDeliveryAttemptResult.Completed("订单已显示为“" + (before.Status ?? "已发货") + "”");
                    if (before.Terminal)
                        return AutoDeliveryAttemptResult.Terminal("订单当前状态为“" + (before.Status ?? "终态") + "”");
                    if (verificationOnly)
                    {
                        return AutoDeliveryAttemptResult.Uncertain(
                            "确认动作此前已提交；当前只读状态=" + (before.Status ?? "未识别")
                            + "，为避免重复业务操作不会再次点击发货或确认发货");
                    }
                    if (!before.Pending)
                        return AutoDeliveryAttemptResult.Deferred("订单当前不是待发货状态：" + (before.Status ?? "状态未识别"));
                    if (!before.ShipButton)
                        return AutoDeliveryAttemptResult.Deferred("已确认待发货订单，但未唯一找到该订单的“发货”按钮");

                    var shipClick = await ReadOrderStateAsync(snapshot.OrderId, true).ConfigureAwait(false);
                    if (shipClick == null || !shipClick.Found || !shipClick.Pending || !shipClick.Clicked)
                        return AutoDeliveryAttemptResult.Deferred("发货按钮点击前的二次订单校验未通过");

                    await Task.Delay(500).ConfigureAwait(false);
                    var noLogistics = await ReadModalStateAsync("select").ConfigureAwait(false);
                    if (noLogistics == null || !noLogistics.Found || !noLogistics.NoLogistics || !noLogistics.Confirm || !noLogistics.Clicked)
                        return AutoDeliveryAttemptResult.Uncertain("发货弹窗未能同时确认“无需物流”和“确认发货”，已停止后续点击");

                    // Do not trust click() return alone. React/Ant-style radios may reject or defer
                    // state changes. Re-read the modal and require positive selection evidence before
                    // the irreversible confirm action.
                    await Task.Delay(350).ConfigureAwait(false);
                    var selected = await ReadModalStateAsync("verify").ConfigureAwait(false);
                    if (selected == null || !selected.Found || !selected.NoLogistics
                        || !selected.Confirm || !selected.Selected)
                    {
                        return AutoDeliveryAttemptResult.Uncertain(
                            "已点击“无需物流”，但未能读取到其选中状态；为避免误用其它发货方式，不会点击确认发货");
                    }

                    // Write-ahead, durable at-most-once barrier. If disk persistence fails, we fail
                    // closed BEFORE clicking confirm. If the process crashes after this point, the
                    // next process sees ConfirmationIntentAt and can only perform read-only checks.
                    if (!AutoDeliveryCoordinator.TryPersistConfirmationIntent(snapshot.Seller, snapshot.OrderId))
                    {
                        return AutoDeliveryAttemptResult.Uncertain(
                            "无法持久化确认发货防重屏障，已拒绝点击确认发货");
                    }

                    var confirm = await ReadModalStateAsync("confirm").ConfigureAwait(false);
                    if (confirm == null || !confirm.Found || !confirm.NoLogistics
                        || !confirm.Confirm || !confirm.Selected || !confirm.Clicked)
                    {
                        return AutoDeliveryAttemptResult.Uncertain(
                            "已持久化防重屏障，但无法在“无需物流”仍选中的条件下唯一点击“确认发货”；后续只读复核");
                    }

                    Log.Info("虚拟商品自动发货已提交确认动作，开始只读核验真实订单状态: seller=" + seller
                        + ", buyer=" + expectedBuyer + ", orderId=" + snapshot.OrderId
                        + ", repeatConfirm=false");
                    for (var attempt = 0; attempt < 16; attempt++)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        var after = await ReadOrderStateAsync(snapshot.OrderId, false).ConfigureAwait(false);
                        if (after == null) continue;
                        if (after.Found && after.Done)
                        {
                            Log.Info("虚拟商品自动发货已真实确认: seller=" + seller
                                + ", buyer=" + expectedBuyer + ", orderId=" + snapshot.OrderId
                                + ", status=" + after.Status);
                            return AutoDeliveryAttemptResult.Completed("确认后的订单状态=" + after.Status);
                        }
                        if (after.Found && after.Terminal)
                            return AutoDeliveryAttemptResult.Terminal("确认后订单进入状态=" + after.Status);
                    }
                    return AutoDeliveryAttemptResult.Uncertain(
                        "已点击确认发货，但 8 秒内尚未读到“已发货/交易成功/已完成”等确定状态；后续只读复核且绝不重复确认");
                }
                catch (Exception ex)
                {
                    Log.Info("虚拟商品自动发货执行异常: seller=" + seller + ", orderId=" + snapshot.OrderId
                        + ", verificationOnly=" + verificationOnly + ", error=" + ex.Message);
                    return verificationOnly
                        ? AutoDeliveryAttemptResult.Uncertain("只读复核异常：" + ex.Message)
                        : AutoDeliveryAttemptResult.Deferred("执行异常：" + ex.Message);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }

        private async Task<AutoDeliveryDomState> ReadOrderStateAsync(string orderId, bool clickShip)
        {
            var expression = BuildOrderStateExpression(orderId, clickShip);
            var raw = await cdp.EvaluateExpressionAsync(
                expression,
                clickShip ? "准确订单待发货校验并点击发货" : "读取准确订单发货状态").ConfigureAwait(false);
            return ParseEvaluationResult<AutoDeliveryDomState>(raw);
        }

        private async Task<AutoDeliveryModalState> ReadModalStateAsync(string action)
        {
            var raw = await cdp.EvaluateExpressionAsync(
                BuildDeliveryModalExpression(action),
                action == "confirm" ? "确认无需物流发货"
                    : action == "verify" ? "核验无需物流已选中"
                    : "选择无需物流发货").ConfigureAwait(false);
            return ParseEvaluationResult<AutoDeliveryModalState>(raw);
        }

        private static T ParseEvaluationResult<T>(string raw) where T : class
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return null;
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    var token = JToken.Parse(value);
                    if (token.Type == JTokenType.String)
                    {
                        value = token.ToString().Trim();
                        continue;
                    }
                    var obj = token as JObject;
                    if (obj != null)
                    {
                        var nested = obj.SelectToken("result.value") ?? obj.SelectToken("value");
                        if (nested != null && nested.Type != JTokenType.Null)
                        {
                            value = nested.ToString().Trim();
                            continue;
                        }
                    }
                    return token.ToObject<T>();
                }
                catch
                {
                    break;
                }
            }
            try { return JsonConvert.DeserializeObject<T>(value); }
            catch { return null; }
        }

        private static string BuildOrderStateExpression(string orderId, bool clickShip)
        {
            var target = JsonConvert.SerializeObject((orderId ?? string.Empty).Trim());
            var click = clickShip ? "true" : "false";
            return @"(function(){
var target=" + target + @";
var doClick=" + click + @";
var statuses=['待发货','等待买家付款','待付款','已付款','已发货','交易成功','已完成','退款中','订单关闭','交易关闭','已关闭','已取消'];
function norm(v){return (v||'').replace(/\s+/g,' ').trim();}
function visible(e){if(!e||!e.getBoundingClientRect)return false;var r=e.getBoundingClientRect();if(r.width<1||r.height<1)return false;var s=e.ownerDocument.defaultView.getComputedStyle(e);return s&&s.display!=='none'&&s.visibility!=='hidden'&&s.opacity!=='0';}
function statusOf(t){for(var i=0;i<statuses.length;i++){if(t.indexOf(statuses[i])>=0)return statuses[i];}return '';}
function clickNode(e){if(!e)return false;var n=e;for(var i=0;n&&i<5;i++,n=n.parentElement){var tag=(n.tagName||'').toUpperCase(),role=(n.getAttribute&&n.getAttribute('role')||'').toLowerCase();if(tag==='BUTTON'||tag==='A'||tag==='LABEL'||role==='button'||role==='radio'){try{n.click();return true;}catch(x){}}}try{e.click();return true;}catch(x){return false;}}
function exact(root,text){var all=root.querySelectorAll('*');for(var i=0;i<all.length&&i<1800;i++){var e=all[i];if(!visible(e))continue;var t=norm(e.innerText||e.textContent);if(t===text)return e;}return null;}
var best=null,bestText='';
function scan(doc,depth){if(!doc||depth>3)return;var root=doc.body||doc.documentElement;if(!root)return;try{var w=doc.createTreeWalker(root,4,null,false),n,count=0;while((n=w.nextNode())&&count++<16000){if((n.nodeValue||'').indexOf(target)<0)continue;var e=n.parentElement;for(var level=0;e&&level<11;level++,e=e.parentElement){var t=norm(e.innerText||e.textContent);if(t.indexOf(target)<0||t.length<20||t.length>5000)continue;var st=statusOf(t);if(!st)continue;if(!best||t.length<bestText.length){best=e;bestText=t;}}}}catch(x){}try{var fs=doc.querySelectorAll('iframe,frame');for(var j=0;j<fs.length&&j<12;j++){try{scan(fs[j].contentDocument,depth+1);}catch(x){}}}catch(x){}}
scan(document,0);
if(!best)return JSON.stringify({found:false,pending:false,done:false,terminal:false,status:'',shipButton:false,clicked:false});
var st=statusOf(bestText),pending=st==='待发货',done=(st==='已发货'||st==='交易成功'||st==='已完成'),terminal=(st==='退款中'||st==='订单关闭'||st==='交易关闭'||st==='已关闭'||st==='已取消');
var ship=exact(best,'发货'),clicked=false;
if(doClick&&pending&&ship)clicked=clickNode(ship);
return JSON.stringify({found:true,pending:pending,done:done,terminal:terminal,status:st,shipButton:!!ship,clicked:clicked});
})()";
        }

        private static string BuildDeliveryModalExpression(string action)
        {
            var select = string.Equals(action, "select", StringComparison.Ordinal) ? "true" : "false";
            var confirm = string.Equals(action, "confirm", StringComparison.Ordinal) ? "true" : "false";
            return @"(function(){
var doSelect=" + select + @";
var doConfirm=" + confirm + @";
function norm(v){return (v||'').replace(/\s+/g,' ').trim();}
function visible(e){if(!e||!e.getBoundingClientRect)return false;var r=e.getBoundingClientRect();if(r.width<1||r.height<1)return false;var s=e.ownerDocument.defaultView.getComputedStyle(e);return s&&s.display!=='none'&&s.visibility!=='hidden'&&s.opacity!=='0';}
function clickNode(e){if(!e)return false;var n=e;for(var i=0;n&&i<5;i++,n=n.parentElement){var tag=(n.tagName||'').toUpperCase(),role=(n.getAttribute&&n.getAttribute('role')||'').toLowerCase();if(tag==='BUTTON'||tag==='A'||tag==='LABEL'||role==='button'||role==='radio'){try{n.click();return true;}catch(x){}}}try{e.click();return true;}catch(x){return false;}}
function exact(root,text){var all=root.querySelectorAll('*');for(var i=0;i<all.length&&i<2200;i++){var e=all[i];if(!visible(e))continue;var t=norm(e.innerText||e.textContent);if(t===text)return e;}return null;}
function selectedState(e){var n=e;for(var i=0;n&&i<7;i++,n=n.parentElement){try{if(n.matches&&n.matches('input[type=radio],input[type=checkbox]')&&n.checked)return true;}catch(x){}try{var aria=(n.getAttribute&&n.getAttribute('aria-checked')||'').toLowerCase();if(aria==='true')return true;}catch(x){}try{var ds=(n.getAttribute&&n.getAttribute('data-state')||'').toLowerCase();if(ds==='checked'||ds==='selected')return true;}catch(x){}try{var input=n.querySelector&&n.querySelector('input[type=radio],input[type=checkbox]');if(input&&input.checked)return true;}catch(x){}try{var cls=(' '+(typeof n.className==='string'?n.className:'')+' ').toLowerCase();if(/(^|[\s_-])(checked|selected)([\s_-]|$)/.test(cls))return true;}catch(x){}}return false;}
var modal=null,modalText='';
function consider(e){if(!e||!visible(e))return;var t=norm(e.innerText||e.textContent);if(t.length<20||t.length>5000||t.indexOf('无需物流')<0||t.indexOf('确认发货')<0)return;if(!modal||t.length<modalText.length){modal=e;modalText=t;}}
function scan(doc,depth){if(!doc||depth>3)return;var root=doc.body||doc.documentElement;if(!root)return;try{var w=doc.createTreeWalker(root,4,null,false),n,count=0;while((n=w.nextNode())&&count++<16000){var raw=norm(n.nodeValue);if(raw!=='无需物流'&&raw!=='确认发货')continue;var e=n.parentElement;for(var level=0;e&&level<10;level++,e=e.parentElement)consider(e);}}catch(x){}try{var fs=doc.querySelectorAll('iframe,frame');for(var j=0;j<fs.length&&j<12;j++){try{scan(fs[j].contentDocument,depth+1);}catch(x){}}}catch(x){}}
scan(document,0);
if(!modal)return JSON.stringify({found:false,noLogistics:false,confirm:false,selected:false,clicked:false});
var no=exact(modal,'无需物流'),ok=exact(modal,'确认发货'),clicked=false;
var selectedNo=!!no&&selectedState(no);
if(doSelect){if(no&&ok)clicked=clickNode(no);}else if(doConfirm){if(ok&&selectedNo)clicked=clickNode(ok);}
return JSON.stringify({found:true,noLogistics:!!no,confirm:!!ok,selected:selectedNo,clicked:clicked});
})()";
        }
    }
}
