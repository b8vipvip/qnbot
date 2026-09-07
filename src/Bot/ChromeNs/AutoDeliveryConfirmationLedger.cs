using BotLib;
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
    ///
    /// The pending queue is intentionally disposable: disabling the feature, expiry, or a terminal
    /// result may remove queue records. Confirmation authority therefore must never live only inside
    /// that queue. This ledger is written BEFORE the confirm click and survives queue removal,
    /// restart, duplicate order events, feature disable/re-enable, and uncertain UI results.
    /// </summary>
    internal static class AutoDeliveryConfirmationLedger
    {
        private sealed class ConfirmationRecord
        {
            public string Key { get; set; }
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
                Schema = 1;
                Records = new List<ConfirmationRecord>();
            }
        }

        private static readonly object Sync = new object();
        private static readonly TimeSpan Retention = TimeSpan.FromDays(365);
        private const int MaxRecords = 20000;
        private static LedgerState _state;
        private static bool _initialized;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _state = LoadState();
                CleanupLocked(DateTime.Now);
                var migrated = MigratePendingConfirmationIntentsLocked();
                if (migrated || !File.Exists(StatePath())) SaveLocked();
                _initialized = true;
                Log.Info("自动发货长期确认防重账本已加载: records=" + _state.Records.Count
                    + ", retentionDays=" + (int)Retention.TotalDays);
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

        public static bool HasIntent(string seller, string orderId)
        {
            Initialize();
            if (!IsSafeOrderId(orderId)) return true; // fail closed for an unsafe irreversible key
            var key = BuildKey(seller, orderId);
            lock (Sync)
            {
                CleanupLocked(DateTime.Now);
                return _state.Records.Any(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Writes the independent durable barrier. Returns true only for the first successfully
        /// persisted intent. An existing record returns false by design: an old barrier is evidence
        /// to deny another confirm click, never permission to repeat it.
        /// </summary>
        public static bool TryRecordIntent(string seller, string orderId, DateTime intentAt)
        {
            Initialize();
            seller = (seller ?? string.Empty).Trim();
            orderId = (orderId ?? string.Empty).Trim();
            if (seller.Length == 0 || !IsSafeOrderId(orderId)) return false;
            var key = BuildKey(seller, orderId);

            lock (Sync)
            {
                CleanupLocked(DateTime.Now);
                if (_state.Records.Any(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal))) return false;

                var record = new ConfirmationRecord
                {
                    Key = key,
                    Seller = seller,
                    OrderId = orderId,
                    IntentAt = intentAt == DateTime.MinValue ? DateTime.Now : intentAt,
                    KeepUntil = DateTime.Now.Add(Retention),
                    Resolution = "intent_persisted",
                    ResolvedAt = null
                };
                _state.Records.Add(record);
                TrimLocked();
                if (SaveLocked()) return true;

                // No confirm click has happened yet. If the durable write failed, remove only the
                // in-memory addition and fail closed so the caller cannot proceed to confirmation.
                _state.Records.Remove(record);
                return false;
            }
        }

        public static void MarkResolved(string seller, string orderId, string resolution)
        {
            Initialize();
            if (!IsSafeOrderId(orderId)) return;
            var key = BuildKey(seller, orderId);
            lock (Sync)
            {
                var record = _state.Records.FirstOrDefault(x => x != null
                    && string.Equals(x.Key, key, StringComparison.Ordinal));
                if (record == null) return;
                record.Resolution = string.IsNullOrWhiteSpace(resolution)
                    ? "resolved"
                    : resolution.Trim();
                record.ResolvedAt = DateTime.Now;
                var minimumRetention = DateTime.Now.Add(Retention);
                if (record.KeepUntil < minimumRetention) record.KeepUntil = minimumRetention;
                SaveLocked();
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
                    var orderId = snapshot == null ? string.Empty : Convert.ToString(snapshot["OrderId"]);
                    seller = (seller ?? string.Empty).Trim();
                    orderId = (orderId ?? string.Empty).Trim();
                    if (seller.Length == 0 || !IsSafeOrderId(orderId)) continue;
                    var key = BuildKey(seller, orderId);
                    if (_state.Records.Any(x => x != null
                        && string.Equals(x.Key, key, StringComparison.Ordinal))) continue;
                    _state.Records.Add(new ConfirmationRecord
                    {
                        Key = key,
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
                    TrimLocked();
                    Log.Info("已将旧自动发货队列中的确认防重屏障迁移到长期账本，避免升级后重复确认。");
                }
            }
            catch (Exception ex)
            {
                // Migration failure is logged, but TryRecordIntent remains fail-closed for all new
                // confirms. Existing queue records also retain ConfirmationIntentAt verification-only
                // behavior, so failure here does not grant new confirmation authority.
                Log.ErrorWithMaxCount("迁移自动发货长期确认防重账本失败：" + ex.Message, 10);
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
                if (loaded == null || loaded.Schema != 1) return new LedgerState();
                if (loaded.Records == null) loaded.Records = new List<ConfirmationRecord>();
                return loaded;
            }
            catch (Exception ex)
            {
                // A corrupted confirmation ledger must never silently become an empty permission
                // store. Keep an in-memory sentinel so HasIntent fails closed for the current run.
                Log.ErrorWithMaxCount("读取自动发货长期确认防重账本失败；本次运行将禁止新的自动确认：" + ex.Message, 10);
                return new LedgerState
                {
                    Records = new List<ConfirmationRecord>
                    {
                        new ConfirmationRecord
                        {
                            Key = "__corrupt_fail_closed__",
                            Seller = "*",
                            OrderId = "0000000000000000",
                            IntentAt = DateTime.Now,
                            KeepUntil = DateTime.Now.Add(Retention),
                            Resolution = "ledger_corrupt_fail_closed"
                        }
                    }
                };
            }
        }

        private static void CleanupLocked(DateTime now)
        {
            if (_state == null) _state = new LedgerState();
            if (_state.Records == null) _state.Records = new List<ConfirmationRecord>();
            _state.Records.RemoveAll(x => x == null || x.KeepUntil <= now);
            TrimLocked();
        }

        private static void TrimLocked()
        {
            if (_state.Records.Count <= MaxRecords) return;
            // Keep the newest irreversible intents. A 20k cap is far above normal local usage while
            // still bounding disk growth.
            _state.Records = _state.Records
                .Where(x => x != null)
                .OrderByDescending(x => x.IntentAt)
                .Take(MaxRecords)
                .ToList();
        }

        private static bool SaveLocked()
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
                    catch (IOException)
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
                Log.ErrorWithMaxCount("保存自动发货长期确认防重账本失败：" + ex.Message, 10);
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

        private static string BuildKey(string seller, string orderId)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
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
