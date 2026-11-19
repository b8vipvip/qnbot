using Bot.Options;
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
    /// A per-seller cursor prevents enabling the feature from retroactively shipping old orders,
    /// while still allowing a crash between event acceptance and queue persistence to recover after
    /// the next startup.
    /// </summary>
    internal static class AutoDeliveryOrderEventSeed
    {
        private sealed class HubEvent
        {
            public string Key { get; set; }
            public DateTime SeenAt { get; set; }
            public OrderSnapshot Snapshot { get; set; }
        }

        private sealed class HubState
        {
            public List<HubEvent> Events { get; set; }
        }

        private sealed class SellerCursor
        {
            public string Seller { get; set; }
            public bool EnabledObserved { get; set; }
            public DateTime LastSeenAt { get; set; }
        }

        private sealed class CursorState
        {
            public int Schema { get; set; }
            public List<SellerCursor> Sellers { get; set; }

            public CursorState()
            {
                Schema = 1;
                Sellers = new List<SellerCursor>();
            }
        }

        private static readonly object Sync = new object();
        private static CursorState _cursors;
        private static Timer _timer;
        private static int _initialized;
        private static int _running;
        private static volatile bool _cursorStateUnavailable;

        public static void Initialize()
        {
            lock (Sync)
            {
                // Public cursor operations also call Initialize. Keep them blocked
                // until the first disk read has completed and the timer is installed.
                if (_initialized != 0) return;
                _initialized = 1;
                AutoDeliveryConfirmationLedger.Initialize();
                bool loadedSuccessfully;
                _cursors = LoadCursors(out loadedSuccessfully);
                if (!loadedSuccessfully)
                {
                    _cursorStateUnavailable = true;
                    Log.ErrorWithMaxCount("自动发货订单事件游标读取失败；保留原文件并暂停事件消费，避免跳过未处理订单。", 10);
                    return;
                }
                _timer = new Timer(_ => Tick(), null, 1200, 1800);
            }
            Log.Info("自动发货订单事件桥已启动：仅消费 OrderEventHub 已确认的新订单/付款事件；首次启用不会追溯历史订单。");
        }

        public static void MarkConfiguration(string seller, bool enabled)
        {
            Initialize();
            if (_cursorStateUnavailable) return;
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            lock (Sync)
            {
                var cursor = GetCursorLocked(seller);
                if (cursor.EnabledObserved != enabled)
                {
                    cursor.EnabledObserved = enabled;
                    cursor.LastSeenAt = DateTime.Now;
                    if (!SaveCursorsLocked()) return;
                }
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
                foreach (var qn in qns)
                {
                    var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                    if (seller.Length == 0) continue;
                    var enabled = AutoDeliverySettings.Load(seller).Enabled;
                    SellerCursor cursor;
                    lock (Sync)
                    {
                        cursor = GetCursorLocked(seller);
                        if (cursor.EnabledObserved != enabled)
                        {
                            // false -> true begins exactly at now; all prior events are history.
                            cursor.EnabledObserved = enabled;
                            cursor.LastSeenAt = DateTime.Now;
                            if (!SaveCursorsLocked()) return;
                        }
                    }
                    if (!enabled) continue;

                    DateTime floor;
                    lock (Sync) floor = cursor.LastSeenAt;
                    var matching = events
                        .Where(x => x != null && x.Snapshot != null)
                        .Where(x => x.SeenAt > floor)
                        .Where(x => x.Snapshot.EventType == OrderEventType.Created
                            || x.Snapshot.EventType == OrderEventType.Paid)
                        .Where(x => DirectOrderIdentityResolver.IdentityEquals(x.Snapshot.Seller, seller))
                        .OrderBy(x => x.SeenAt)
                        .ToList();

                    var latest = floor;
                    foreach (var entry in matching)
                    {
                        // Do not advance the durable event cursor unless the corresponding
                        // queue state was safely persisted or the event was intentionally ignored.
                        if (!AutoDeliveryCoordinator.Enqueue(entry.Snapshot)) return;
                        if (entry.SeenAt > latest) latest = entry.SeenAt;
                    }
                    if (latest > floor)
                    {
                        lock (Sync)
                        {
                            cursor.LastSeenAt = latest;
                            if (!SaveCursorsLocked()) return;
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

        private static SellerCursor GetCursorLocked(string seller)
        {
            if (_cursors == null) _cursors = new CursorState();
            if (_cursors.Sellers == null) _cursors.Sellers = new List<SellerCursor>();
            var cursor = _cursors.Sellers.FirstOrDefault(x => x != null
                && string.Equals(x.Seller ?? string.Empty, seller, StringComparison.Ordinal));
            if (cursor != null) return cursor;

            cursor = new SellerCursor
            {
                Seller = seller,
                EnabledObserved = false,
                LastSeenAt = DateTime.Now
            };
            _cursors.Sellers.Add(cursor);
            return cursor;
        }

        private static HubState LoadHubState()
        {
            var path = HubStatePath();
            if (!File.Exists(path)) return new HubState { Events = new List<HubEvent>() };
            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var state = JsonConvert.DeserializeObject<HubState>(reader.ReadToEnd())
                        ?? new HubState { Events = new List<HubEvent>() };
                    if (state.Events == null) state.Events = new List<HubEvent>();
                    return state;
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static CursorState LoadCursors(out bool loadedSuccessfully)
        {
            loadedSuccessfully = false;
            try
            {
                var path = CursorPath();
                if (!File.Exists(path))
                {
                    loadedSuccessfully = true;
                    return new CursorState();
                }
                var state = JsonConvert.DeserializeObject<CursorState>(File.ReadAllText(path, Encoding.UTF8));
                if (state == null || state.Schema != 1 || state.Sellers == null)
                {
                    Log.ErrorWithMaxCount("自动发货订单事件游标结构无效；拒绝重置或覆盖现有游标文件。", 10);
                    return new CursorState();
                }
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
                return true;
            }
            catch (Exception ex)
            {
                _cursorStateUnavailable = true;
                Log.ErrorWithMaxCount("保存自动发货订单事件游标失败；暂停事件消费以防丢失订单：" + ex.Message, 10);
                return false;
            }
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
