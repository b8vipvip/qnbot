using Bot.Automation.ChatDeskNs.Automators;
using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Bot.Automation.ChatDeskNs
{
    /// <summary>
    /// Maps each authenticated ShopKey to its native Qianniu Desk. Several ShopKeys may share
    /// one AliWorkbench HWND (seller tabs), and different ShopKeys may also use different HWNDs.
    /// Seller nickname is display data only and is never sufficient to choose a Desk when it
    /// resolves to more than one ShopKey.
    /// </summary>
    internal static class DeskSellerBindingRegistry
    {
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, int> ShopToHwnd =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, string> ActiveSellerByHwnd =
            new ConcurrentDictionary<int, string>();
        private static readonly ConcurrentDictionary<int, string> ActiveShopByHwnd =
            new ConcurrentDictionary<int, string>();

        internal static Desk FindSellerDesk(string seller)
        {
            return FindSellerDesk(seller, ResolveUniqueShopKey(seller));
        }

        internal static Desk FindSellerDesk(string seller, string shopKey)
        {
            seller = NormalizeSeller(seller);
            shopKey = NormalizeSeller(shopKey);
            if (seller.Length == 0 || shopKey.Length == 0 || !IsAuthenticatedShop(seller, shopKey))
                return null;

            int hwnd;
            if (!ShopToHwnd.TryGetValue(shopKey, out hwnd)) return null;
            var mapped = Desk.FindExistingByHwnd(hwnd);
            if (mapped != null && mapped.IsAlive) return mapped;
            lock (Sync)
            {
                if (ShopToHwnd.TryGetValue(shopKey, out hwnd))
                {
                    mapped = Desk.FindExistingByHwnd(hwnd);
                    if (mapped != null && mapped.IsAlive) return mapped;
                    ForgetShop(shopKey, hwnd);
                }
            }
            return null;
        }

        internal static string GetSeller(Desk desk)
        {
            if (desk == null || !desk.IsAlive || desk.Hwnd == null) return string.Empty;

            string activeSeller;
            if (ActiveSellerByHwnd.TryGetValue(desk.Hwnd.Handle, out activeSeller))
            {
                activeSeller = NormalizeSeller(activeSeller);
                if (activeSeller.Length > 0 && IsAuthenticatedSeller(activeSeller)) return activeSeller;
                string ignored;
                ActiveSellerByHwnd.TryRemove(desk.Hwnd.Handle, out ignored);
            }

            var title = NormalizeSeller(desk.WndTitle);
            return title.Length > 0
                && !QnAccountFinder.IsGenericReceptionTitle(title)
                && IsAuthenticatedSeller(title)
                ? title
                : string.Empty;
        }

        internal static string GetShopKey(Desk desk)
        {
            if (desk == null || !desk.IsAlive || desk.Hwnd == null) return string.Empty;
            string shopKey;
            return ActiveShopByHwnd.TryGetValue(desk.Hwnd.Handle, out shopKey)
                ? NormalizeSeller(shopKey)
                : string.Empty;
        }

        internal static bool IsSellerForDesk(Desk desk, string seller)
        {
            return IsSellerForDesk(desk, seller, ResolveUniqueShopKey(seller));
        }

        internal static bool IsSellerForDesk(Desk desk, string seller, string shopKey)
        {
            seller = NormalizeSeller(seller);
            shopKey = NormalizeSeller(shopKey);
            if (desk == null || !desk.IsAlive || !desk.IsForeground || desk.Hwnd == null || seller.Length == 0
                || shopKey.Length == 0 || !IsAuthenticatedShop(seller, shopKey))
                return false;

            string activeShopKey;
            return ActiveShopByHwnd.TryGetValue(desk.Hwnd.Handle, out activeShopKey)
                && string.Equals(activeShopKey, shopKey, StringComparison.Ordinal)
                && string.Equals(GetSeller(desk), seller, StringComparison.Ordinal);
        }

        /// <summary>
        /// Validates the stable ShopKey-to-Desk binding for read-only UI.
        /// The assistant window can own keyboard focus while this panel is being used;
        /// that must not make the already-bound Qianniu conversation disappear.
        /// Native automation continues to use IsSellerForDesk, which requires foreground.
        /// </summary>
        internal static bool IsBoundToDeskForReadOnlyUi(Desk desk, string seller, string shopKey)
        {
            seller = NormalizeSeller(seller);
            shopKey = NormalizeSeller(shopKey);
            if (desk == null || !desk.IsAlive || desk.Hwnd == null || seller.Length == 0
                || shopKey.Length == 0 || !IsAuthenticatedShop(seller, shopKey))
                return false;

            var hwnd = desk.Hwnd.Handle;
            lock (Sync)
            {
                int mappedHwnd;
                if (!ShopToHwnd.TryGetValue(shopKey, out mappedHwnd) || mappedHwnd != hwnd)
                    return false;

                string activeShopKey;
                if (!ActiveShopByHwnd.TryGetValue(hwnd, out activeShopKey)
                    || !string.Equals(NormalizeSeller(activeShopKey), shopKey, StringComparison.Ordinal))
                    return false;

                string activeSeller;
                return ActiveSellerByHwnd.TryGetValue(hwnd, out activeSeller)
                    && string.Equals(NormalizeSeller(activeSeller), seller, StringComparison.Ordinal);
            }
        }

        internal static Desk BindResolvedSeller(Desk desk, string seller, string evidence)
        {
            return BindResolvedSeller(desk, seller, evidence, ResolveUniqueShopKey(seller));
        }

        internal static Desk BindResolvedSeller(
            Desk desk, string seller, string evidence, string shopKey, bool allowRebind = false)
        {
            seller = NormalizeSeller(seller);
            shopKey = NormalizeSeller(shopKey);
            if (desk == null || !desk.IsAlive || desk.Hwnd == null || seller.Length == 0 || shopKey.Length == 0
                || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedShop(seller, shopKey)) return null;

            lock (Sync)
            {
                CleanupStaleBindings();
                var hwnd = desk.Hwnd.Handle;
                int existingHwnd;
                if (ShopToHwnd.TryGetValue(shopKey, out existingHwnd) && existingHwnd != hwnd)
                {
                    var existingDesk = Desk.FindExistingByHwnd(existingHwnd);
                    if (existingDesk != null && existingDesk.IsAlive)
                    {
                        if (!allowRebind || !desk.IsForeground)
                        {
                            Log.ErrorWithMaxCount("店铺窗口绑定被拒绝：同一ShopKey不能绑定两个Desk: shopRef="
                                + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                                + ", existingHwnd=" + existingHwnd + ", requestedHwnd=" + hwnd, 20);
                            return null;
                        }
                        Log.Info("依据唯一聚焦WebView证据迁移ShopKey窗口绑定: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                            + ", previousHwnd=" + existingHwnd + ", currentHwnd=" + hwnd);
                    }
                    ForgetShop(shopKey, existingHwnd);
                }

                RememberKnown(desk, seller, shopKey);
                var activeShopKey = ActiveShopSessionRegistry.GetActiveShopKey();
                if (desk.IsForeground
                    && (RuntimeShopCount() <= 1
                        || string.Equals(activeShopKey, shopKey, StringComparison.Ordinal)))
                    MarkActiveSeller(desk, seller, evidence, shopKey);

                Log.Info("已登记ShopKey与千牛窗口: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                    + ", seller=" + seller + ", pid=" + desk.ProcessId
                    + ", hwnd=" + hwnd + ", activeShopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", GetShopKey(desk))
                    + ", evidence=" + (evidence ?? string.Empty));
                return desk;
            }
        }

        internal static Desk BindForegroundSeller(QN qn, string evidence)
        {
            var seller = qn == null || qn.Seller == null
                ? string.Empty
                : NormalizeSeller(qn.Seller.Nick);
            string shopKey;
            try { shopKey = ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey; }
            catch { return null; }
            if (seller.Length == 0 || string.IsNullOrWhiteSpace(shopKey)
                || !IsAuthenticatedShop(seller, shopKey)) return null;

            if (!ActiveShopSessionRegistry.IsActive(qn)) return null;

            var existing = FindSellerDesk(seller, shopKey);
            if (existing != null && existing.IsForeground)
            {
                return MarkActiveSeller(existing, seller, evidence, shopKey) ? existing : null;
            }

            var foreground = Desk.Snapshot()
                .Where(x => x != null && x.IsAlive && x.IsForeground)
                .ToList();
            if (foreground.Count != 1) return null;

            return BindResolvedSeller(
                foreground[0], seller, evidence, shopKey, allowRebind: existing != null);
        }

        private static bool MarkActiveSeller(Desk desk, string seller, string evidence, string shopKey)
        {
            lock (Sync)
            {
                if (!desk.IsAlive || !desk.IsForeground) return false;
                if (!IsSingleShopRuntime
                    && !string.Equals(ActiveShopSessionRegistry.GetActiveShopKey(), shopKey, StringComparison.Ordinal))
                    return false;

                var hwnd = desk.Hwnd.Handle;
                int mappedHwnd;
                if (!ShopToHwnd.TryGetValue(shopKey, out mappedHwnd) || mappedHwnd != hwnd)
                    return false;

                string previousSeller;
                string previousShopKey;
                ActiveSellerByHwnd.TryGetValue(hwnd, out previousSeller);
                ActiveShopByHwnd.TryGetValue(hwnd, out previousShopKey);
                ActiveSellerByHwnd[hwnd] = seller;
                ActiveShopByHwnd[hwnd] = shopKey;
                if (!string.Equals(previousShopKey, shopKey, StringComparison.Ordinal))
                {
                    Log.Info("共享千牛窗口活动ShopKey已切换: hwnd=" + hwnd
                        + ", previousShopRef=" + MyWebSocketServer.DiagnosticRef("shop", previousShopKey)
                        + ", currentShopRef=" + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                        + ", seller=" + seller + ", previousSeller=" + NormalizeSeller(previousSeller)
                        + ", evidence=" + (evidence ?? string.Empty));
                }
                return true;
            }
        }

        private static void RememberKnown(Desk desk, string seller, string shopKey)
        {
            if (desk == null || !desk.IsAlive || desk.Hwnd == null) return;
            seller = NormalizeSeller(seller);
            shopKey = NormalizeSeller(shopKey);
            if (seller.Length == 0 || shopKey.Length == 0
                || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedShop(seller, shopKey)) return;
            ShopToHwnd[shopKey] = desk.Hwnd.Handle;
        }

        private static bool IsAuthenticatedShop(string seller, string shopKey)
        {
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                return shop != null
                    && string.Equals((shop.SellerNick ?? string.Empty).Trim(), NormalizeSeller(seller), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string ResolveUniqueShopKey(string seller)
        {
            seller = NormalizeSeller(seller);
            if (seller.Length == 0) return string.Empty;
            try
            {
                var keys = QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null && qn.Seller != null
                        && string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal))
                    .Select(qn =>
                    {
                        try { return ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey; }
                        catch { return string.Empty; }
                    })
                    .Select(NormalizeSeller)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return keys.Count == 1 && keys[0].Length > 0 ? keys[0] : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool IsAuthenticatedSeller(string seller)
        {
            seller = NormalizeSeller(seller);
            if (seller.Length == 0) return false;
            try
            {
                return QN.GetRuntimeSafetySnapshot().Any(qn => qn != null && qn.Seller != null
                    && string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal));
            }
            catch
            {
                try
                {
                    return QN.QNSet != null && QN.QNSet.Any(qn => qn != null && qn.Seller != null
                        && string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal));
                }
                catch { return false; }
            }
        }

        internal static bool IsSingleShopRuntime
        {
            get { return RuntimeShopCount() <= 1; }
        }

        private static int RuntimeShopCount()
        {
            try
            {
                var qns = QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null && qn.Seller != null)
                    .ToList();
                var shopKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var qn in qns)
                {
                    string shopKey;
                    try { shopKey = ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey; }
                    catch { return 2; }
                    if (string.IsNullOrWhiteSpace(shopKey)) return 2;
                    shopKeys.Add(shopKey.Trim());
                }
                return shopKeys.Count;
            }
            catch { return 2; }
        }

        private static void CleanupStaleBindings()
        {
            foreach (var pair in ShopToHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Value);
                if (desk != null && desk.IsAlive) continue;
                ForgetShop(pair.Key, pair.Value);
            }

            foreach (var pair in ActiveSellerByHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Key);
                if (desk != null && desk.IsAlive) continue;
                string ignored;
                ActiveSellerByHwnd.TryRemove(pair.Key, out ignored);
            }

            foreach (var pair in ActiveShopByHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Key);
                if (desk != null && desk.IsAlive) continue;
                string ignored;
                ActiveShopByHwnd.TryRemove(pair.Key, out ignored);
            }
        }

        private static void ForgetShop(string shopKey, int hwnd)
        {
            int mapped;
            if (ShopToHwnd.TryGetValue(shopKey, out mapped) && mapped == hwnd)
            {
                int ignored;
                ShopToHwnd.TryRemove(shopKey, out ignored);
            }

            string activeShopKey;
            if (ActiveShopByHwnd.TryGetValue(hwnd, out activeShopKey)
                && string.Equals(activeShopKey, shopKey, StringComparison.Ordinal))
            {
                string ignored;
                ActiveShopByHwnd.TryRemove(hwnd, out ignored);
                string ignoredSeller;
                ActiveSellerByHwnd.TryRemove(hwnd, out ignoredSeller);
            }
        }

        private static string NormalizeSeller(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}