using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Long-lived, independent at-most-once ledger for the irreversible “确认发货” action.
    /// The ledger is written BEFORE confirm and survives pending-queue removal, restart, duplicate
    /// order events, feature disable/re-enable, and uncertain UI results.
    /// </summary>
    internal static class AutoDeliveryConfirmationLedger
    {
        private sealed class ConfirmationRecord
        {
            public string Key { get; set; }
            public string ShopKey { get; set; }
            public string Seller { get; set; }
            public string OrderId { get; set; }
            public DateTime IntentAt { get; set; }
            public DateTime KeepUntil { get; set; }
            public string Resolution { get; set; }
            public DateTime? ResolvedAt { get; set; }
        }

        private sealed class LedgerState
        {
            public int Schema { get; set; }
            public List<ConfirmationRecord> Records { get; set; }

            public LedgerState()
            {
                Schema = 2;
                Records = new List<ConfirmationRecord>();
            }
        }

        private static readonly object Sync = new object();
        private static readonly TimeSpan Retention = TimeSpan.FromDays(365);
        private const int MaxRecords = 20000;
        private static LedgerState _state;
        private static bool _initialized;
        private static bool _failClosed;
        private static bool _migrationRequired;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                var path = StatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000))
                {
                    if (!lease.Acquired)
                    {
                        _state = new LedgerState();
                        _failClosed = true;
                        Log.ErrorWithMaxCount("获取自动发货长期确认账本跨进程锁失败；本次运行禁止新的自动确认。", 10);
                    }
                    else
                    {
                        _state = LoadState();
                        if (!_failClosed)
                        {
                            CleanupLocked(DateTime.Now);
                            var migrated = MigratePendingConfirmationIntentsLocked();
                            // Never replace an unreadable/invalid ledger, even if legacy queue
                            // migration found records. A partial replacement could erase old barriers.
                            if (!_failClosed && (migrated || _migrationRequired || !File.Exists(StatePath())))
                            {
                                if (!SaveLocked()) _failClosed = true;
                                else _migrationRequired = false;
                            }
                        }
                    }
                }
                _initialized = true;
                Log.Info("自动发货长期确认防重账本已加载: records=" + _state.Records.Count
                    + ", retentionDays=" + (int)Retention.TotalDays
                    + ", failClosed=" + _failClosed);
            }
        }

        public static bool IsOperational()
        {
            Initialize();
            lock (Sync)
            {
                var path = StatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000))
                {
                    if (!lease.Acquired || !ReloadStateForMutationLocked()) return false;
                    CleanupLocked(DateTime.Now);
                    return !_failClosed && !IsAtCapacityLocked();
                }
            }
        }

        public static bool IsSafeOrderId(string orderId)
        {
            orderId = (orderId ?? string.Empty).Trim();
            if (orderId.Length < 16 || orderId.Length > 24) return false;
            for (var i = 0; i < orderId.Length; i++)
            {
                if (orderId[i] < '0' || orderId[i] > '9') return false;
            }
            return true;
        }

        public static bool HasIntent(string shopKey, string seller, string orderId)
        {
            bool hasIntent, atCapacity;
            if (!TryReadIntent(shopKey, seller, orderId, out hasIntent, out atCapacity)) return true;
            // Admission remains fail-closed at capacity even when this exact order has no record.
            if (atCapacity)
                Log.ErrorWithMaxCount("自动发货长期确认账本已达到容量上限；保留全部防重记录并暂停新的自动确认。", 10);
            return hasIntent || atCapacity;
        }

        /// <summary>
        /// Reads whether this exact order already has a durable confirmation barrier.
        /// Unlike HasIntent, this query does not turn a full-but-readable ledger into
        /// a synthetic "intent exists" result; terminal preflight outcomes need to know
        /// whether there is an actual barrier to resolve.
        /// </summary>
        public static bool TryGetIntent(string shopKey, string seller, string orderId, out bool hasIntent)
        {
            bool atCapacity;
            return TryReadIntent(shopKey, seller, orderId, out hasIntent, out atCapacity);
        }

        private static bool TryReadIntent(
            string shopKey,
            string seller,
            string orderId,
            out bool hasIntent,
            out bool atCapacity)
        {
            hasIntent = false;
            atCapacity = false;
            Initialize();
            if (!IsSafeOrderId(orderId) || string.IsNullOrWhiteSpace(shopKey)) return false;
            var key = BuildKey(shopKey, seller, orderId);
            var legacyKey = BuildLegacyKey(seller, orderId);
            lock (Sync)
            {
                if (_failClosed) return false;
                var path = StatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000))
                {
                    if (!lease.Acquired || !ReloadStateForMutationLocked()) return false;
                    CleanupLocked(DateTime.Now);
                    hasIntent = _state.Records.Any(x => x != null
                        && (string.Equals(x.Key, key, StringComparison.Ordinal)
                            || string.Equals(x.Key, legacyKey, StringComparison.Ordinal)));
                    atCapacity = IsAtCapacityLocked();
                    return true;
                }
            }
        }

        /// <summary>
        /// Returns true only for the first successfully persisted intent. An existing record returns
        /// false by design: a previous barrier denies another confirm, never authorizes a retry.
        /// </summary>
        public static bool TryRecordIntent(string shopKey, string seller, string orderId, DateTime intentAt)
        {
            Initialize();
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            orderId = (orderId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || !IsSafeOrderId(orderId)) return false;
            var key = BuildKey(shopKey, seller, orderId);
            var legacyKey = BuildLegacyKey(seller, orderId);

            lock (Sync)
            {
                var path = StatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("获取自动发货确认意图跨进程锁失败；禁止执行确认发货。", 10);
                        return false;
                    }
                    if (!ReloadStateForMutationLocked()) return false;
                    CleanupLocked(DateTime.Now);
                    if (_state.Records.Any(x => x != null
                        && (string.Equals(x.Key, key, StringComparison.Ordinal)
                            || string.Equals(x.Key, legacyKey, StringComparison.Ordinal)))) return false;
                    if (IsAtCapacityLocked())
                    {
                        Log.ErrorWithMaxCount("自动发货长期确认账本已达到容量上限；拒绝写入新确认意图，禁止执行确认发货。", 10);
                        return false;
                    }

                    var record = new ConfirmationRecord
                    {
                        Key = key,
                        ShopKey = shopKey,
                        Seller = seller,
                        OrderId = orderId,
                        IntentAt = intentAt == DateTime.MinValue ? DateTime.Now : intentAt,
                        KeepUntil = DateTime.Now.Add(Retention),
                        Resolution = "intent_persisted",
                        ResolvedAt = null
                    };
                    _state.Records.Add(record);
                    if (SaveLocked()) return true;

                    _state.Records.Remove(record);
                    _failClosed = true;
                    return false;
                }
            }
        }

        public static bool MarkResolved(string shopKey, string seller, string orderId, string resolution)
        {
            Initialize();
            if (string.IsNullOrWhiteSpace(shopKey) || !IsSafeOrderId(orderId)) return false;
            var key = BuildKey(shopKey, seller, orderId);
            lock (Sync)
            {
                var path = StatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000))
                {
                    // A missing/unreadable ledger is not proof that the at-most-once
                    // barrier was durably resolved. Keep the queue's read-only task.
                    if (!lease.Acquired || !ReloadStateForMutationLocked()) return false;
                    var legacyKey = BuildLegacyKey(seller, orderId);
                    var records = _state.Records.Where(x => x != null
                        && (string.Equals(x.Key, key, StringComparison.Ordinal)
                            || string.Equals(x.Key, legacyKey, StringComparison.Ordinal)))
                        .ToList();
                    if (records.Count == 0) return false;
                    foreach (var record in records)
                    {
                        record.Resolution = string.IsNullOrWhiteSpace(resolution)
                            ? "resolved"
                            : resolution.Trim();
                        record.ResolvedAt = DateTime.Now;
                        var keepUntil = DateTime.Now.Add(Retention);
                        if (record.KeepUntil < keepUntil) record.KeepUntil = keepUntil;
                    }
                    if (!SaveLocked())
                    {
                        _failClosed = true;
                        return false;
                    }
                    return true;
                }
            }
        }

        private static bool MigratePendingConfirmationIntentsLocked()
        {
            var path = QueuePath();
            if (!File.Exists(path)) return false;
            var changed = false;
            try
            {
                JObject root;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    root = JsonConvert.DeserializeObject<JObject>(reader.ReadToEnd());
                }
                var pending = root == null ? null : root["Pending"] as JArray;
                if (pending == null) return false;

                foreach (var token in pending.OfType<JObject>())
                {
                    var intentToken = token["ConfirmationIntentAt"];
                    if (intentToken == null || intentToken.Type == JTokenType.Null) continue;
                    DateTime intentAt;
                    if (!DateTime.TryParse(intentToken.ToString(), out intentAt)) intentAt = DateTime.Now;
                    var snapshot = token["Snapshot"] as JObject;
                    var seller = snapshot == null ? string.Empty : Convert.ToString(snapshot["Seller"]);
                    var shopKey = snapshot == null ? string.Empty : Convert.ToString(snapshot["ShopKey"]);
                    var orderId = snapshot == null ? string.Empty : Convert.ToString(snapshot["OrderId"]);
                    seller = (seller ?? string.Empty).Trim();
                    shopKey = (shopKey ?? string.Empty).Trim();
                    orderId = (orderId ?? string.Empty).Trim();
                    if (seller.Length == 0 || !IsSafeOrderId(orderId)) continue;
                    if (shopKey.Length == 0)
                    {
                        try
                        {
                            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                            if (shop != null && string.Equals(shop.SellerNick, seller, StringComparison.Ordinal))
                                shopKey = shop.ShopKey;
                        }
                        catch
                        {
                            // Preserve the old intent as a seller-wide barrier. It may block
                            // more than one same-nickname shop, but can never permit a repeat confirm.
                        }
                    }
                    var key = shopKey.Length == 0
                        ? BuildLegacyKey(seller, orderId)
                        : BuildKey(shopKey, seller, orderId);
                    if (_state.Records.Any(x => x != null
                        && string.Equals(x.Key, key, StringComparison.Ordinal))) continue;
                    _state.Records.Add(new ConfirmationRecord
                    {
                        Key = key,
                        ShopKey = shopKey,
                        Seller = seller,
                        OrderId = orderId,
                        IntentAt = intentAt,
                        KeepUntil = DateTime.Now.Add(Retention),
                        Resolution = "migrated_pending_intent",
                        ResolvedAt = null
                    });
                    changed = true;
                }
                if (changed)
                {
                    // Preserve every migrated at-most-once barrier. If migration reaches
                    // the ledger capacity, new confirmations are paused by IsAtCapacityLocked.
                    Log.Info("已将旧自动发货队列中的确认防重屏障迁移到长期账本，避免升级后重复确认。");
                }
            }
            catch (Exception ex)
            {
                _failClosed = true;
                Log.ErrorWithMaxCount("迁移自动发货长期确认防重账本失败；本次运行禁止新的自动确认：" + ex.Message, 10);
            }
            return changed;
        }

        private static LedgerState LoadState()
        {
            try
            {
                var path = StatePath();
                if (!File.Exists(path)) return new LedgerState();
                var loaded = JsonConvert.DeserializeObject<LedgerState>(File.ReadAllText(path, Encoding.UTF8));
                if (loaded == null || (loaded.Schema != 1 && loaded.Schema != 2) || loaded.Records == null)
                {
                    _failClosed = true;
                    Log.ErrorWithMaxCount("自动发货长期确认防重账本结构无效（包括 Records 缺失或为 null）；本次运行禁止新的自动确认且保留原文件。", 10);
                    return new LedgerState();
                }
                if (loaded.Schema == 1)
                {
                    // Old records used seller nickname + order ID only. Keep each as a
                    // seller-wide at-most-once barrier instead of guessing which same-nick
                    // ShopKey owned the irreversible confirmation.
                    foreach (var record in loaded.Records.Where(x => x != null))
                    {
                        record.ShopKey = string.Empty;
                        record.Key = BuildLegacyKey(record.Seller, record.OrderId);
                    }
                    loaded.Schema = 2;
                    _migrationRequired = true;
                }
                else
                {
                    foreach (var record in loaded.Records.Where(x => x != null && string.IsNullOrWhiteSpace(x.ShopKey)))
                        record.Key = BuildLegacyKey(record.Seller, record.OrderId);
                }
                return loaded;
            }
            catch (Exception ex)
            {
                _failClosed = true;
                Log.ErrorWithMaxCount("读取自动发货长期确认防重账本失败；本次运行将禁止新的自动确认：" + ex.Message, 10);
                return new LedgerState();
            }
        }

        // Caller must hold the named cross-process mutex. Reloading before each ledger
        // transaction prevents one Bot process from overwriting another process's new barrier.
        private static bool ReloadStateForMutationLocked()
        {
            if (_failClosed) return false;
            var path = StatePath();
            if (!File.Exists(path))
            {
                _failClosed = true;
                Log.ErrorWithMaxCount("自动发货长期确认账本在运行期间丢失；禁止新的自动确认。", 10);
                return false;
            }

            _migrationRequired = false;
            _state = LoadState();
            if (_failClosed) return false;
            CleanupLocked(DateTime.Now);
            if (_migrationRequired)
            {
                var migrated = MigratePendingConfirmationIntentsLocked();
                if (!_failClosed && (migrated || _migrationRequired))
                {
                    if (!SaveLocked()) _failClosed = true;
                    else _migrationRequired = false;
                }
            }
            return !_failClosed;
        }

        private static void CleanupLocked(DateTime now)
        {
            if (_state == null) _state = new LedgerState();
            if (_state.Records == null) _state.Records = new List<ConfirmationRecord>();
            _state.Records.RemoveAll(x => x == null || x.KeepUntil <= now);
        }

        private static bool IsAtCapacityLocked()
        {
            return _state != null
                && _state.Records != null
                && _state.Records.Count >= MaxRecords;
        }

        private static bool SaveLocked()
        {
            try
            {
                var path = StatePath();
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                    path, JsonConvert.SerializeObject(_state, Formatting.Indented), 3, 50, out error))
                    throw new IOException("原子保存失败：" + error);
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存自动发货长期确认防重账本失败：" + ex.Message, 10);
                return false;
            }
        }

        private static string BuildKey(string shopKey, string seller, string orderId)
        {
            return (shopKey ?? string.Empty).Trim() + "#"
                + (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (orderId ?? string.Empty).Trim();
        }

        private static string BuildLegacyKey(string seller, string orderId)
        {
            return "legacy#" + (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (orderId ?? string.Empty).Trim();
        }

        private static string DataRoot()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string StatePath()
        {
            return Path.Combine(DataRoot(), "virtual-goods-auto-delivery-confirmation-ledger.json");
        }

        private static string QueuePath()
        {
            return Path.Combine(DataRoot(), "virtual-goods-auto-delivery-queue.json");
        }
    }
}
