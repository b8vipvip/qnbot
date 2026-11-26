using Bot.ChromeNs;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Generic;
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
            List<QN> sessions;
            try
            {
                sessions = QN.GetRuntimeSafetySnapshot();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取多客服会话快照失败：" + ex.Message, 10);
                sessions = new List<QN>();
            }

            // A single disposed/partially initialized QN wrapper must not prevent later
            // shops from receiving their authoritative conversation-change subscription.
            foreach (var qn in sessions ?? new List<QN>())
            {
                if (qn == null) continue;
                try
                {
                    lock (ForegroundSubscriptionSync)
                    {
                        object marker;
                        if (ForegroundSubscribed.TryGetValue(qn, out marker)) continue;
                        var subscriptionAttempted = false;
                        try
                        {
                            subscriptionAttempted = true;
                            qn.EvBuyerSwitched += Qn_EvBuyerSwitched;
                            ForegroundSubscribed.Add(qn, new object());
                        }
                        catch
                        {
                            if (subscriptionAttempted) { try { qn.EvBuyerSwitched -= Qn_EvBuyerSwitched; } catch { } }
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("跳过异常千牛会话的活动店铺事件订阅并继续：" + ex.Message, 10);
                }
            }

            try { RefreshProfileCacheIfNeeded(); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新多客服店铺身份缓存失败：" + ex.Message, 10);
            }
            try { EnsureShopLogWriters(); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化多店铺运行日志失败：" + ex.Message, 10);
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
            foreach (var shop in ShopByKey.Values.Where(x => x != null && !string.IsNullOrWhiteSpace(x.ShopKey)).ToList())
            {
                try
                {
                    var writer = GetOrCreateWriter(shop);
                    if (LogReadyMarkers.TryAdd(shop.ShopKey, 0))
                    {
                        try
                        {
                            writer.Write("[shop=" + shop.ShopKey + "] 店铺独立运行日志已就绪；只接收本 ShopKey 可归属的业务日志。", "Info");
                        }
                        catch
                        {
                            // Do not permanently mark a failed writer as initialized. The next
                            // timer pass must retry after a transient path/IO failure.
                            byte ignored;
                            LogReadyMarkers.TryRemove(shop.ShopKey, out ignored);
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("单店运行日志初始化失败，继续处理其他店铺：" + ex.Message, 10);
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

                IList<ShopProfile> profiles;
                try { profiles = Profiles.GetAll(); }
                catch (Exception ex)
                {
                    // Keep the last good identity cache if the registry cannot be read.
                    Log.ErrorWithMaxCount("刷新店铺身份缓存读取配置失败，保留上次有效缓存：" + ex.Message, 10);
                    _lastProfileCacheRefreshUtc = DateTime.UtcNow;
                    return;
                }
                if (profiles == null)
                {
                    Log.ErrorWithMaxCount("刷新店铺身份缓存返回空配置列表，保留上次有效缓存。", 10);
                    _lastProfileCacheRefreshUtc = DateTime.UtcNow;
                    return;
                }

                var nextByKey = new Dictionary<string, ShopContext>(StringComparer.Ordinal);
                var nextBySellerRef = new Dictionary<string, ShopContext>(StringComparer.Ordinal);
                var failedShopKeys = new HashSet<string>(StringComparer.Ordinal);
                var failedSellerRefs = new HashSet<string>(StringComparer.Ordinal);
                var ambiguousSellerRefs = new HashSet<string>(StringComparer.Ordinal);
                void AddSellerRefAlias(string sellerRef, ShopContext shop)
                {
                    if (string.IsNullOrWhiteSpace(sellerRef) || shop == null
                        || ambiguousSellerRefs.Contains(sellerRef)) return;
                    ShopContext existing;
                    if (nextBySellerRef.TryGetValue(sellerRef, out existing)
                        && !string.Equals(existing.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    {
                        // A nickname/display name is not a ShopKey. Never route a log by a
                        // sellerRef that resolves to more than one shop.
                        nextBySellerRef.Remove(sellerRef);
                        ambiguousSellerRefs.Add(sellerRef);
                        return;
                    }
                    nextBySellerRef[sellerRef] = shop;
                }
                foreach (var profile in profiles.Where(x => x != null))
                {
                    try
                    {
                        var shop = profile.ToContext();
                        if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                        nextByKey[shop.ShopKey] = shop;
                        // Keep both the authenticated seller nick and the UI display alias.
                        // DisplayName can be a shortened label and is not always the login nick.
                        AddSellerRefAlias(StableSellerRef(profile.SellerNick), shop);
                        AddSellerRefAlias(StableSellerRef(profile.DisplayName), shop);
                    }
                    catch (Exception ex)
                    {
                        var failedKey = (profile.ShopKey ?? string.Empty).Trim();
                        if (failedKey.Length > 0) failedShopKeys.Add(failedKey);
                        var failedRef = StableSellerRef(profile.DisplayName);
                        if (failedRef.Length > 0) failedSellerRefs.Add(failedRef);
                        Log.ErrorWithMaxCount("刷新店铺身份缓存跳过异常店铺并继续：" + ex.Message, 10);
                    }
                }

                // Profile display names can be shorter than the authenticated Qianniu login
                // identity (for example "客服名" vs "主账号:客服名"). Add live seller aliases.
                try
                {
                    var runtimeSellers = QN.GetRuntimeSafetySnapshot();
                    foreach (var qn in (runtimeSellers ?? new System.Collections.Generic.List<QN>()).Where(x => x != null && x.Seller != null))
                    {
                        var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                        var sellerRef = StableSellerRef(seller);
                        try
                        {
                            if (seller.Length == 0) continue;
                            var shop = ShopIdentityResolver.Resolve(qn.Seller);
                            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                            nextByKey[shop.ShopKey] = shop;
                            AddSellerRefAlias(sellerRef, shop);
                        }
                        catch (Exception ex)
                        {
                            if (sellerRef.Length > 0) failedSellerRefs.Add(sellerRef);
                            Log.ErrorWithMaxCount("刷新运行时客服身份别名失败并继续：" + ex.Message, 10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("刷新运行时客服身份快照失败，保留现有别名：" + ex.Message, 10);
                }

                // Preserve the last good mapping for a profile that failed conversion. A bad
                // entry must not make later ShopKeys disappear from the shared identity cache.
                foreach (var failedKey in failedShopKeys)
                {
                    ShopContext previous;
                    if (ShopByKey.TryGetValue(failedKey, out previous)) nextByKey[failedKey] = previous;
                }
                foreach (var failedRef in failedSellerRefs)
                {
                    ShopContext previous;
                    if (!ambiguousSellerRefs.Contains(failedRef)
                        && !nextBySellerRef.ContainsKey(failedRef)
                        && ShopBySellerRef.TryGetValue(failedRef, out previous))
                        nextBySellerRef[failedRef] = previous;
                }
                // Keep last-known seller aliases for shops that remain registered even
                // while their QN wrapper is temporarily offline or absent from the runtime
                // snapshot. Current collisions above still remove and suppress ambiguous refs.
                foreach (var pair in ShopBySellerRef)
                    if (!ambiguousSellerRefs.Contains(pair.Key)
                        && nextByKey.ContainsKey(pair.Value.ShopKey)
                        && !nextBySellerRef.ContainsKey(pair.Key))
                        nextBySellerRef[pair.Key] = pair.Value;

                // Apply the completed snapshot incrementally instead of clearing both shared
                // dictionaries before profile conversion has succeeded.
                foreach (var key in ShopByKey.Keys.ToList())
                    if (!nextByKey.ContainsKey(key))
                    {
                        ShopContext ignored;
                        ShopByKey.TryRemove(key, out ignored);
                    }
                foreach (var pair in nextByKey) ShopByKey[pair.Key] = pair.Value;

                foreach (var key in ShopBySellerRef.Keys.ToList())
                    if (!nextBySellerRef.ContainsKey(key))
                    {
                        ShopContext ignored;
                        ShopBySellerRef.TryRemove(key, out ignored);
                    }
                foreach (var pair in nextBySellerRef) ShopBySellerRef[pair.Key] = pair.Value;

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
