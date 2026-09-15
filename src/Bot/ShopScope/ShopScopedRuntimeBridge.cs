using Bot.ChromeNs;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopScopedRuntimeBridgeBootstrap =
            ShopScope.ShopScopedRuntimeBridge.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    internal static class ShopScopedRuntimeBridge
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, LogWriter> Writers =
            new ConcurrentDictionary<string, LogWriter>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<QN, byte> ForegroundSubscribed =
            new ConcurrentDictionary<QN, byte>();
        private static readonly object ProfileCacheSync = new object();
        private static readonly ConcurrentDictionary<string, ShopContext> ShopByKey =
            new ConcurrentDictionary<string, ShopContext>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, ShopContext> ShopBySellerRef =
            new ConcurrentDictionary<string, ShopContext>(StringComparer.Ordinal);
        private static readonly FieldInfo ForwardedInboundSourceSessionField =
            typeof(CDPClient).GetField("ForwardedInboundSourceSession", BindingFlags.NonPublic | BindingFlags.Static);
        private static Timer _foregroundTimer;
        private static DateTime _lastProfileCacheRefreshUtc = DateTime.MinValue;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                ScopedDataPathRouter.Configure(TryResolveDataRoot);
                ScopedLogRouter.Configure(WriteShopLog);
                _foregroundTimer = new Timer(_ => RefreshForegroundSubscriptions(), null, 250, 500);
                Log.Info("多客服店铺作用域桥已启用：权威会话切换用于纠正活动店铺，重复页面入站不会抢占；店铺日志按 ShopKey 独立镜像。");
            }
            return new object();
        }

        private static bool TryResolveDataRoot(out string dataRoot)
        {
            dataRoot = string.Empty;
            var shop = ShopSettingsScope.Current;
            if (shop == null) return false;
            dataRoot = Paths.GetCompatibilityDataRoot(shop);
            return true;
        }

        private static void RefreshForegroundSubscriptions()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot().Where(x => x != null))
                {
                    if (!ForegroundSubscribed.TryAdd(qn, 0)) continue;
                    qn.EvBuyerSwitched += Qn_EvBuyerSwitched;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新多客服权威会话订阅失败: " + ex.Message, 10);
            }
        }

        private static void Qn_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick)) return;

            var forwardedSource = GetForwardedInboundSourceSession();
            if (!string.IsNullOrWhiteSpace(forwardedSource))
            {
                Log.Info("重复页面onConversationChange仅更新本店买家，不切换活动客服: seller="
                    + qn.Seller.Nick + ", sourceSession=" + forwardedSource);
                return;
            }

            // A direct onConversationChange raised by the seller's authoritative CDP is stronger
            // foreground evidence than the old document.hasFocus probe. Embedded CEF pages can all
            // report focus=false, while duplicate/standby pages are explicitly marked by
            // BeginForwardedInbound and are rejected above.
            ActiveShopSessionRegistry.ObserveChatDialogActive(qn, "authoritative-onConversationChange");
            ActiveShopSessionRegistry.ReassertCurrent("authoritative-onConversationChange");
        }

        private static string GetForwardedInboundSourceSession()
        {
            try
            {
                var ambient = ForwardedInboundSourceSessionField == null
                    ? null
                    : ForwardedInboundSourceSessionField.GetValue(null) as AsyncLocal<string>;
                return ambient == null ? string.Empty : (ambient.Value ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteShopLog(string tag, string text)
        {
            var shop = ShopSettingsScope.Current ?? ResolveShopFromSafeLog(text);
            if (shop == null) return;
            var writer = Writers.GetOrAdd(shop.ShopKey, key =>
            {
                var path = Path.Combine(Paths.GetLogRoot(shop), "runtime.txt");
                return new LogWriter(path, true, 8 * 1024 * 1024)
                {
                    LimitSameStringWriteCount = false
                };
            });
            writer.Write("[shop=" + shop.ShopKey + "] " + (text ?? string.Empty), tag ?? "Info");
        }

        private static ShopContext ResolveShopFromSafeLog(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            RefreshProfileCacheIfNeeded();

            foreach (var pair in ShopByKey)
            {
                if (text.IndexOf("shop=" + pair.Key, StringComparison.Ordinal) >= 0
                    || text.IndexOf("shopKey=" + pair.Key, StringComparison.Ordinal) >= 0)
                    return pair.Value;
            }

            foreach (var pair in ShopBySellerRef)
            {
                if (text.IndexOf(pair.Key, StringComparison.Ordinal) >= 0)
                    return pair.Value;
            }
            return null;
        }

        private static void RefreshProfileCacheIfNeeded()
        {
            if (DateTime.UtcNow - _lastProfileCacheRefreshUtc < TimeSpan.FromSeconds(5)
                && !ShopByKey.IsEmpty) return;
            lock (ProfileCacheSync)
            {
                if (DateTime.UtcNow - _lastProfileCacheRefreshUtc < TimeSpan.FromSeconds(5)
                    && !ShopByKey.IsEmpty) return;
                try
                {
                    ShopByKey.Clear();
                    ShopBySellerRef.Clear();
                    foreach (var profile in Profiles.GetAll().Where(x => x != null))
                    {
                        var shop = profile.ToContext();
                        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                        ShopByKey[shop.ShopKey] = shop;
                        var sellerRef = StableSellerRef(profile.DisplayName);
                        if (sellerRef.Length > 0) ShopBySellerRef[sellerRef] = shop;
                    }
                }
                catch
                {
                }
                _lastProfileCacheRefreshUtc = DateTime.UtcNow;
            }
        }

        private static string StableSellerRef(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return string.Empty;
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in seller)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return "seller#" + hash.ToString("x16").Substring(0, 10);
        }
    }
}
