using Bot.ChromeNs;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private static readonly ConcurrentDictionary<string, byte> LogReadyMarkers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<QN, object> ForegroundSubscribed =
            new ConditionalWeakTable<QN, object>();
        private static readonly object ForegroundSubscriptionSync = new object();
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
                Log.Info("多客服店铺作用域桥已启用：任意同客服WebView承载的真实会话切换都可纠正活动店铺；店铺日志按 ShopKey 独立镜像并主动创建。");
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
                    lock (ForegroundSubscriptionSync)
                    {
                        object marker;
                        if (ForegroundSubscribed.TryGetValue(qn, out marker)) continue;
                        qn.EvBuyerSwitched += Qn_EvBuyerSwitched;
                        ForegroundSubscribed.Add(qn, new object());
                    }
                }
                RefreshProfileCacheIfNeeded();
                EnsureShopLogWriters();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新多客服权威会话订阅失败: " + ex.Message, 10);
            }
        }

        private static void Qn_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || qn.Seller == null || e == null || e.Seller == null) return;

            var forwardedSource = GetForwardedInboundSourceSession();
            var evidence = string.IsNullOrWhiteSpace(forwardedSource)
                ? "authoritative-onConversationChange"
                : "forwarded-onConversationChange:" + forwardedSource;
            var sessionId = qn.CDP == null ? string.Empty : (qn.CDP.SessionId ?? string.Empty).Trim();
            var eventSeller = (e.Seller.Nick ?? string.Empty).Trim();
            var eventTargetId = (e.Seller.TargetId ?? string.Empty).Trim();
            var buyer = e.Buyer == null ? string.Empty : (e.Buyer.Nick ?? string.Empty).Trim();
            if (buyer.Length == 0 && e.Buyer != null)
                buyer = (e.Buyer.Ccode ?? string.Empty).Trim();
            if (buyer.Length == 0 && e.Buyer != null)
                buyer = (e.Buyer.TargetId ?? string.Empty).Trim();

            // CDPClient and DuplicateCdpInboundRecoveryBridge have already verified that this
            // event belongs to the exact authoritative ShopKey. Promote it as foreground
            // evidence; ObserveChatDialogActive is intentionally background-only and cannot
            // replace an already active A shop with B.
            if (!ActiveShopSessionRegistry.ActivateFromVerifiedConversationChange(
                qn, eventSeller, eventTargetId, sessionId, buyer, evidence))
            {
                Log.Info("真实onConversationChange未能晋升活动客服，身份/买家/权威CDP校验未通过: seller="
                    + eventSeller + ", qnSeller=" + qn.Seller.Nick
                    + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId)
                    + ", sourceSessionRef=" + MyWebSocketServer.DiagnosticRef("session", forwardedSource)
                    + ", buyer=" + buyer);
                return;
            }

            ActiveShopSessionRegistry.ReassertCurrent("verified-onConversationChange");
            Log.Info("真实onConversationChange已晋升活动客服: seller=" + eventSeller
                + ", shopRef=" + MyWebSocketServer.DiagnosticRef(
                    "shop", ActiveShopSessionRegistry.GetActiveShopKey())
                + ", sessionRef=" + MyWebSocketServer.DiagnosticRef("session", sessionId)
                + ", sourceSessionRef=" + MyWebSocketServer.DiagnosticRef("session", forwardedSource)
                + ", buyer=" + buyer);
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
            var writer = GetOrCreateWriter(shop);
            writer.Write("[shop=" + shop.ShopKey + "] " + (text ?? string.Empty), tag ?? "Info");
        }

        private static LogWriter GetOrCreateWriter(ShopContext shop)
        {
            return Writers.GetOrAdd(shop.ShopKey, key =>
            {
                var path = Path.Combine(Paths.GetLogRoot(shop), "runtime.txt");
                return new LogWriter(path, true, 8 * 1024 * 1024)
                {
                    LimitSameStringWriteCount = false
                };
            });
        }

        private static void EnsureShopLogWriters()
        {
            foreach (var shop in ShopByKey.Values.Where(x => x != null && !string.IsNullOrWhiteSpace(x.ShopKey)))
            {
                var writer = GetOrCreateWriter(shop);
                if (LogReadyMarkers.TryAdd(shop.ShopKey, 0))
                {
                    writer.Write("[shop=" + shop.ShopKey + "] 店铺独立运行日志已就绪；只接收本 ShopKey 可归属的业务日志。", "Info");
                }
            }
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

                    // Profile display names can be shorter than the authenticated Qianniu login
                    // identity (for example "客服名" vs "主账号:客服名"). Add the live seller
                    // identities as aliases so privacy-safe sellerRef logs always reach the right
                    // per-shop runtime file.
                    foreach (var qn in QN.GetRuntimeSafetySnapshot().Where(x => x != null && x.Seller != null))
                    {
                        try
                        {
                            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                            if (seller.Length == 0) continue;
                            var shop = ShopIdentityResolver.Resolve(qn.Seller);
                            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                            ShopByKey[shop.ShopKey] = shop;
                            var sellerRef = StableSellerRef(seller);
                            if (sellerRef.Length > 0) ShopBySellerRef[sellerRef] = shop;
                        }
                        catch
                        {
                        }
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
