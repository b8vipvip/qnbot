using Bot.Options;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Bridges the already-authoritative, durable OrderEventHub ledger into the auto-delivery queue.
    /// A per-ShopKey cursor prevents same-nickname shops from consuming each other's orders and prevents retroactive shipping,
    /// while still allowing a crash between event acceptance and queue persistence to recover after
    /// the next startup.
    /// </summary>
    internal static class AutoDeliveryOrderEventSeed
    {
        private sealed class HubEvent
        {
            public string Key { get; set; }
            public DateTime AcceptedAt { get; set; }
            public DateTime SeenAt { get; set; }
            public OrderSnapshot Snapshot { get; set; }
        }

        private sealed class HubState
        {
            public List<HubEvent> Events { get; set; }
        }

        private sealed class SellerCursor
        {
            public string ShopKey { get; set; }
            public string Seller { get; set; }
            public bool EnabledObserved { get; set; }
            public DateTime LastSeenAt { get; set; }
            // Timestamp alone is not a unique event cursor. Keep the event keys accepted at
            // LastSeenAt so a later event with the same clock tick is not silently skipped.
            public List<string> LastSeenEventKeys { get; set; }
        }

        private sealed class CursorState
        {
            public int Schema { get; set; }
            public List<SellerCursor> Sellers { get; set; }

            public CursorState()
            {
                Schema = 3;
                Sellers = new List<SellerCursor>();
            }
        }

        private static readonly object Sync = new object();
        private static CursorState _cursors;
        private static Timer _timer;
        private static int _initialized;
        private static int _running;
        private static volatile bool _cursorStateUnavailable;
        private static bool _cursorStateDirty;

        public static void Initialize()
        {
            lock (Sync)
            {
                // Public cursor operations also call Initialize. Keep them blocked
                // until the first disk read has completed and the timer is installed.
                if (_initialized != 0) return;
                AutoDeliveryConfirmationLedger.Initialize();
                _cursorStateUnavailable = false;
                bool loadedSuccessfully;
                _cursors = LoadCursors(out loadedSuccessfully);
                if (!loadedSuccessfully)
                {
                    _cursorStateUnavailable = true;
                    EnsureRecoveryTimerLocked();
                    Log.ErrorWithMaxCount("自动发货订单事件游标读取失败；保留原文件并暂停事件消费，后台将持续重试恢复。", 10);
                    return;
                }
                if (_cursorStateDirty && !SaveCursorsLocked())
                {
                    _cursorStateUnavailable = true;
                    EnsureRecoveryTimerLocked();
                    return;
                }
                _initialized = 1;
                EnsureRecoveryTimerLocked();
            }
            Log.Info("自动发货订单事件桥已启动：仅消费 OrderEventHub 已确认的新订单/付款事件；首次启用不会追溯历史订单。");
        }

        public static void MarkConfiguration(string seller, bool enabled)
        {
            Initialize();
            if (_cursorStateUnavailable) return;
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("自动发货游标配置无法确认店铺身份，拒绝修改同昵称店铺游标: seller="
                    + seller + ", error=" + ex.Message, 20);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)) return;
            lock (Sync)
            {
                var cursor = GetCursorLocked(shop.ShopKey, seller);
                if (cursor.EnabledObserved != enabled)
                {
                    cursor.EnabledObserved = enabled;
                    cursor.LastSeenAt = DateTime.Now;
                    cursor.LastSeenEventKeys = new List<string>();
                    _cursorStateDirty = true;
                }
                if (_cursorStateDirty && !SaveCursorsLocked()) return;
            }
        }

        private static void EnsureRecoveryTimerLocked()
        {
            if (_timer == null)
                _timer = new Timer(_ => OnTimerTick(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(1200, 1800);
        }

        private static void OnTimerTick()
        {
            if (_cursorStateUnavailable && !TryRecoverCursorState()) return;
            if (!_cursorStateUnavailable) Tick();
        }

        private static bool TryRecoverCursorState()
        {
            lock (Sync)
            {
                if (!_cursorStateUnavailable) return true;
                _cursorStateUnavailable = false;
                bool loadedSuccessfully;
                var recovered = LoadCursors(out loadedSuccessfully);
                if (!loadedSuccessfully)
                {
                    _cursorStateUnavailable = true;
                    return false;
                }
                _cursors = recovered;
                if (_cursorStateDirty && !SaveCursorsLocked()) return false;
                _cursorStateUnavailable = false;
                _initialized = 1;
                EnsureRecoveryTimerLocked();
                Log.Info("自动发货事件游标从读取/存储故障中恢复：已重新载入持久游标并恢复事件消费。");
                return true;
            }
        }

        private static void Tick()
        {
            if (_cursorStateUnavailable) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                QN[] qns;
                try
                {
                    qns = QN.QNSet == null
                        ? new QN[0]
                        : QN.QNSet.Where(x => x != null && x.Seller != null).ToArray();
                }
                catch
                {
                    qns = new QN[0];
                }
                if (qns.Length == 0) return;

                // If the irreversible confirmation ledger is corrupted/unwritable, do not advance
                // order-event cursors. That preserves new events for recovery after the ledger is
                // repaired instead of silently consuming them while auto-confirm is fail-closed.
                if (!AutoDeliveryConfirmationLedger.IsOperational()
                    || !AutoDeliveryCoordinator.IsOperational())
                {
                    Log.ErrorWithMaxCount(
                        "自动发货队列或长期确认防重账本不可用；暂停消费新订单事件且不推进游标。",
                        10);
                    return;
                }

                var hub = LoadHubState();
                var events = hub == null || hub.Events == null
                    ? new List<HubEvent>()
                    : hub.Events;
                if (hub == null)
                    Log.ErrorWithMaxCount("自动发货订单事件账本暂时不可读；本轮不推进事件游标，等待后台恢复后重试。", 10);
                foreach (var qn in qns)
                {
                    ShopContext shop;
                    try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
                    catch (Exception ex)
                    {
                        Log.Info("自动发货事件桥跳过无法确认身份的千牛实例: error=" + ex.Message);
                        continue;
                    }
                    if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                    var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                    if (seller.Length == 0 || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)) continue;

                    using (ShopSettingsScope.Enter(shop))
                    {
                        var enabled = AutoDeliverySettings.Load(seller).Enabled;
                        SellerCursor cursor;
                        lock (Sync)
                        {
                            cursor = GetCursorLocked(shop.ShopKey, seller);
                            if (cursor.EnabledObserved != enabled)
                            {
                                // false -> true begins exactly at now; all prior events are history.
                                cursor.EnabledObserved = enabled;
                                cursor.LastSeenAt = DateTime.Now;
                                cursor.LastSeenEventKeys = new List<string>();
                                _cursorStateDirty = true;
                            }
                            if (_cursorStateDirty && !SaveCursorsLocked()) return;
                        }
                        if (!enabled) continue;
                        // A failed ledger read must never be treated as an empty event stream:
                        // doing so would move the cursor past orders that arrive during the outage.
                        if (hub == null) continue;

                        DateTime floor;
                        HashSet<string> lastSeenEventKeys;
                        lock (Sync)
                        {
                            floor = cursor.LastSeenAt;
                            lastSeenEventKeys = new HashSet<string>(
                                cursor.LastSeenEventKeys ?? new List<string>(), StringComparer.Ordinal);
                        }
                        var matching = events
                            .Where(x => x != null && x.Snapshot != null && !string.IsNullOrWhiteSpace(x.Key))
                            .Where(x =>
                            {
                                var acceptedAt = GetAcceptedAt(x);
                                return acceptedAt > floor
                                    || (acceptedAt == floor && !lastSeenEventKeys.Contains(x.Key));
                            })
                            .Where(x => string.Equals((x.Snapshot.ShopKey ?? string.Empty).Trim(),
                                shop.ShopKey, StringComparison.Ordinal))
                            .Where(x => x.Snapshot.EventType == OrderEventType.Created
                                || x.Snapshot.EventType == OrderEventType.Paid)
                            .Where(x => string.Equals((x.Snapshot.Seller ?? string.Empty).Trim(),
                                seller, StringComparison.Ordinal))
                            .OrderBy(x => GetAcceptedAt(x))
                            .ThenBy(x => x.Key, StringComparer.Ordinal)
                            .ToList();

                        var latest = floor;
                        var latestEventKeys = new HashSet<string>(lastSeenEventKeys, StringComparer.Ordinal);
                        foreach (var entry in matching)
                        {
                            // Do not advance the durable event cursor unless the corresponding
                            // queue state was safely persisted or the event was intentionally ignored.
                            if (!AutoDeliveryCoordinator.Enqueue(entry.Snapshot)) return;
                            var acceptedAt = GetAcceptedAt(entry);
                            if (acceptedAt > latest)
                            {
                                latest = acceptedAt;
                                latestEventKeys.Clear();
                            }
                            if (acceptedAt == latest) latestEventKeys.Add(entry.Key);
                        }
                        if (latest > floor || !latestEventKeys.SetEquals(lastSeenEventKeys))
                        {
                            lock (Sync)
                            {
                                // A settings transition may have reset the cursor while this
                                // batch was being enqueued. Never overwrite that newer baseline.
                                var liveKeys = new HashSet<string>(
                                    cursor.LastSeenEventKeys ?? new List<string>(), StringComparer.Ordinal);
                                if (cursor.LastSeenAt != floor || !liveKeys.SetEquals(lastSeenEventKeys))
                                    return;
                                cursor.LastSeenAt = latest;
                                cursor.LastSeenEventKeys = latestEventKeys
                                    .OrderBy(x => x, StringComparer.Ordinal)
                                    .Take(2000)
                                    .ToList();
                                _cursorStateDirty = true;
                                if (!SaveCursorsLocked()) return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("自动发货订单事件桥读取异常：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static SellerCursor GetCursorLocked(string shopKey, string seller)
        {
            if (_cursors == null) _cursors = new CursorState();
            if (_cursors.Sellers == null) _cursors.Sellers = new List<SellerCursor>();
            var cursor = _cursors.Sellers.FirstOrDefault(x => x != null
                && string.Equals((x.ShopKey ?? string.Empty).Trim(), shopKey, StringComparison.Ordinal));
            if (cursor != null) return cursor;

            // Migrate a seller-only cursor only when the nickname resolves uniquely to
            // this exact ShopKey. Otherwise each shop starts at now and old events remain history.
            var legacy = _cursors.Sellers.FirstOrDefault(x => x != null
                && string.IsNullOrWhiteSpace(x.ShopKey)
                && string.Equals(x.Seller ?? string.Empty, seller, StringComparison.Ordinal));
            if (legacy != null)
            {
                try
                {
                    var unique = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                    if (unique != null && string.Equals(unique.ShopKey, shopKey, StringComparison.Ordinal))
                    {
                        legacy.ShopKey = shopKey;
                        _cursorStateDirty = true;
                        return legacy;
                    }
                }
                catch { }
            }

            cursor = new SellerCursor
            {
                ShopKey = shopKey,
                Seller = seller,
                EnabledObserved = false,
                LastSeenAt = DateTime.Now,
                LastSeenEventKeys = new List<string>()
            };
            _cursors.Sellers.Add(cursor);
            _cursorStateDirty = true;
            return cursor;
        }

        private static DateTime GetAcceptedAt(HubEvent entry)
        {
            if (entry == null) return DateTime.MinValue;
            // SeenAt is refreshed when the same order event is observed again. Using it
            // as the cursor can make an old order look newly accepted after a restart.
            // AcceptedAt is immutable; SeenAt is only the legacy fallback.
            return entry.AcceptedAt == DateTime.MinValue ? entry.SeenAt : entry.AcceptedAt;
        }

        private static HubState LoadHubState()
        {
            var path = HubStatePath();
            try
            {
                // File.Exists returns false for access errors as well as a missing file.
                // Only explicit not-found is a safe empty-first-run state.
                File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return new HubState { Events = new List<HubEvent>() };
            }
            catch (DirectoryNotFoundException)
            {
                return new HubState { Events = new List<HubEvent>() };
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("检查自动发货订单事件账本状态失败；按不可读处理并暂停游标推进：" + ex.Message, 10);
                return null;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var state = JsonConvert.DeserializeObject<HubState>(reader.ReadToEnd());
                    if (state == null || state.Events == null)
                    {
                        Log.ErrorWithMaxCount("自动发货订单事件账本内容无效；暂停游标推进以防丢单。", 10);
                        return null;
                    }
                    return state;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取自动发货订单事件账本失败；暂停游标推进以防丢单：" + ex.Message, 10);
                return null;
            }
        }

        private static CursorState LoadCursors(out bool loadedSuccessfully)
        {
            loadedSuccessfully = false;
            try
            {
                var path = CursorPath();
                bool exists;
                if (!TryGetFilePresence(path, out exists)) return new CursorState();
                if (!exists)
                {
                    bool markerExists;
                    if (!TryGetFilePresence(CursorMarkerPath(), out markerExists) || markerExists)
                    {
                        Log.ErrorWithMaxCount("自动发货事件游标文件缺失但持久化标记仍存在；拒绝重置游标并跳过历史订单。", 10);
                        return new CursorState();
                    }
                    loadedSuccessfully = true;
                    return new CursorState();
                }
                var state = JsonConvert.DeserializeObject<CursorState>(File.ReadAllText(path, Encoding.UTF8));
                if (state == null || (state.Schema != 1 && state.Schema != 2 && state.Schema != 3) || state.Sellers == null)
                {
                    Log.ErrorWithMaxCount("自动发货订单事件游标结构无效；拒绝重置或覆盖现有游标文件。", 10);
                    return new CursorState();
                }
                if (state.Schema < 3)
                {
                    // Older cursors tracked timestamps only. Replay keys at the boundary once;
                    // Enqueue and the durable confirmation ledger make that replay idempotent.
                    foreach (var cursor in state.Sellers.Where(x => x != null))
                        cursor.LastSeenEventKeys = new List<string>();
                    state.Schema = 3;
                    _cursorStateDirty = true;
                }
                else
                {
                    foreach (var cursor in state.Sellers.Where(x => x != null))
                    {
                        if (cursor.LastSeenEventKeys == null)
                        {
                            cursor.LastSeenEventKeys = new List<string>();
                            _cursorStateDirty = true;
                        }
                    }
                }
                if (!EnsureCursorMarkerLocked()) return new CursorState();
                loadedSuccessfully = true;
                return state;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取自动发货订单事件游标失败；保留原文件并暂停事件消费：" + ex.Message, 10);
                return new CursorState();
            }
        }

        private static bool SaveCursorsLocked()
        {
            if (_cursorStateUnavailable) return false;
            try
            {
                var path = CursorPath();
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                    path, JsonConvert.SerializeObject(_cursors, Formatting.Indented), 3, 50, out error))
                    throw new IOException("原子保存失败：" + error);
                if (!EnsureCursorMarkerLocked())
                    throw new IOException("自动发货事件游标已保存，但无法持久化初始化标记。");
                _cursorStateDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                _cursorStateUnavailable = true;
                Log.ErrorWithMaxCount("保存自动发货订单事件游标失败；暂停事件消费以防丢失订单：" + ex.Message, 10);
                return false;
            }
        }

        private static string CursorMarkerPath()
        {
            return CursorPath() + ".initialized";
        }

        private static bool TryGetFilePresence(string path, out bool exists)
        {
            exists = false;
            try
            {
                File.GetAttributes(path);
                exists = true;
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("检查自动发货事件游标文件状态失败，按文件可能存在处理：" + ex.Message, 10);
                exists = true;
                return false;
            }
        }

        private static bool EnsureCursorMarkerLocked()
        {
            bool exists;
            if (!TryGetFilePresence(CursorMarkerPath(), out exists)) return false;
            if (exists) return true;
            string error;
            return CrossProcessAtomicStateFile.WriteAllTextAtomic(
                CursorMarkerPath(), "schema=1", 3, 50, out error);
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

        private static string HubStatePath()
        {
            return Path.Combine(DataRoot(), "order-event-state.json");
        }

        private static string CursorPath()
        {
            return Path.Combine(DataRoot(), "virtual-goods-auto-delivery-event-cursor.json");
        }
    }
}
