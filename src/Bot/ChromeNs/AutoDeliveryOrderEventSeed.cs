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

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            AutoDeliveryConfirmationLedger.Initialize();
            lock (Sync) _cursors = LoadCursors();
            _timer = new Timer(_ => Tick(), null, 1200, 1800);
            Log.Info("自动发货订单事件桥已启动：仅消费 OrderEventHub 已确认的新订单/付款事件；首次启用不会追溯历史订单。");
        }

        public static void MarkConfiguration(string seller, bool enabled)
        {
            Initialize();
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            lock (Sync)
            {
                var cursor = GetCursorLocked(seller);
                if (cursor.EnabledObserved != enabled)
                {
                    cursor.EnabledObserved = enabled;
                    cursor.LastSeenAt = DateTime.Now;
                    SaveCursorsLocked();
                }
            }
        }

        private static void Tick()
        {
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
                if (!AutoDeliveryConfirmationLedger.IsOperational())
                {
                    Log.ErrorWithMaxCount(
                        "自动发货长期确认防重账本不可用；暂停消费新订单事件且不推进游标。",
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
                            SaveCursorsLocked();
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
                        AutoDeliveryCoordinator.Enqueue(entry.Snapshot);
                        if (entry.SeenAt > latest) latest = entry.SeenAt;
                    }
                    if (latest > floor)
                    {
                        lock (Sync)
                        {
                            cursor.LastSeenAt = latest;
                            SaveCursorsLocked();
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

        private static CursorState LoadCursors()
        {
            try
            {
                var path = CursorPath();
                if (!File.Exists(path)) return new CursorState();
                var state = JsonConvert.DeserializeObject<CursorState>(File.ReadAllText(path, Encoding.UTF8));
                if (state == null || state.Schema != 1) return new CursorState();
                if (state.Sellers == null) state.Sellers = new List<SellerCursor>();
                return state;
            }
            catch
            {
                // Reset-to-now behavior in GetCursorLocked is fail-safe: a corrupted cursor can skip
                // old events, but can never cause historical orders to be auto-shipped.
                return new CursorState();
            }
        }

        private static bool SaveCursorsLocked()
        {
            var temp = string.Empty;
            try
            {
                var path = CursorPath();
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(
                    temp,
                    JsonConvert.SerializeObject(_cursors, Formatting.Indented),
                    new UTF8Encoding(false));
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
                Log.ErrorWithMaxCount("保存自动发货订单事件游标失败：" + ex.Message, 10);
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
