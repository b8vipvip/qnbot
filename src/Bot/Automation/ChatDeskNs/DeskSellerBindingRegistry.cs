using Bot.Automation.ChatDeskNs.Automators;
using Bot.ChromeNs;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Bot.Automation.ChatDeskNs
{
    /// <summary>
    /// Tracks seller ownership for Qianniu reception windows.
    ///
    /// Qianniu 9.97 can host several authenticated customer-service accounts as tabs inside
    /// one AliWorkbench top-level HWND. Therefore seller -> HWND is many-to-one, while the
    /// visible/native composer still has exactly one active seller at a time. Keep those two
    /// concepts separate so shop data remains isolated without inventing a second native Desk.
    /// </summary>
    internal static class DeskSellerBindingRegistry
    {
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, int> SellerToHwnd =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, string> ActiveSellerByHwnd =
            new ConcurrentDictionary<int, string>();

        internal static Desk FindSellerDesk(string seller)
        {
            seller = NormalizeSeller(seller);
            if (seller.Length == 0 || !IsAuthenticatedSeller(seller)) return null;

            int hwnd;
            if (SellerToHwnd.TryGetValue(seller, out hwnd))
            {
                var mapped = Desk.FindExistingByHwnd(hwnd);
                if (mapped != null && mapped.IsAlive) return mapped;
                ForgetSeller(seller, hwnd);
            }

            var legacy = Desk.FindExistingBySellerNick(seller);
            if (legacy != null)
            {
                RememberKnown(legacy, seller);
                if (legacy.IsForeground) MarkActiveSeller(legacy, seller, "legacy-title");
                return legacy;
            }
            return null;
        }

        internal static string GetSeller(Desk desk)
        {
            if (desk == null || desk.Hwnd == null) return string.Empty;

            string activeSeller;
            if (ActiveSellerByHwnd.TryGetValue(desk.Hwnd.Handle, out activeSeller))
            {
                activeSeller = NormalizeSeller(activeSeller);
                if (activeSeller.Length > 0 && IsAuthenticatedSeller(activeSeller)) return activeSeller;

                string ignored;
                ActiveSellerByHwnd.TryRemove(desk.Hwnd.Handle, out ignored);
            }

            var title = NormalizeSeller(desk.WndTitle);
            if (title.Length > 0
                && !QnAccountFinder.IsGenericReceptionTitle(title)
                && IsAuthenticatedSeller(title))
            {
                RememberKnown(desk, title);
                MarkActiveSeller(desk, title, "desk-title");
                return title;
            }
            return string.Empty;
        }

        /// <summary>
        /// True only when this seller owns the currently visible composer on the Desk.
        /// Multiple sellers may be known for the same HWND, but UIA/HWND send operations are
        /// allowed only for the active seller so a hidden seller can never send through another
        /// customer-service account's visible input box.
        /// </summary>
        internal static bool IsSellerForDesk(Desk desk, string seller)
        {
            seller = NormalizeSeller(seller);
            if (desk == null || seller.Length == 0) return false;
            return string.Equals(GetSeller(desk), seller, StringComparison.Ordinal);
        }

        internal static Desk BindResolvedSeller(Desk desk, string seller, string evidence)
        {
            seller = NormalizeSeller(seller);
            if (desk == null || desk.Hwnd == null || seller.Length == 0
                || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedSeller(seller)) return null;

            lock (Sync)
            {
                CleanupStaleBindings();

                var hwnd = desk.Hwnd.Handle;
                int sellerHwnd;
                if (SellerToHwnd.TryGetValue(seller, out sellerHwnd) && sellerHwnd != hwnd)
                {
                    var existingSellerDesk = Desk.FindExistingByHwnd(sellerHwnd);
                    if (existingSellerDesk != null && existingSellerDesk.IsAlive)
                    {
                        Log.ErrorWithMaxCount("店铺窗口绑定被拒绝：同一seller不能绑定两个Desk: seller=" + seller
                            + ", existingHwnd=" + sellerHwnd + ", requestedHwnd=" + hwnd, 20);
                        return null;
                    }
                    ForgetSeller(seller, sellerHwnd);
                }

                // Do not reject another authenticated seller merely because it resolves to the
                // same HWND. Current Qianniu intentionally renders multiple seller tabs inside one
                // reception window. Also do not Dispose/Create the Desk here: that old identity
                // upgrade path could disturb the attached Qt window while it is live.
                RememberKnown(desk, seller);

                var currentTitle = NormalizeSeller(desk.WndTitle);
                string currentActive;
                var hasActive = ActiveSellerByHwnd.TryGetValue(hwnd, out currentActive)
                    && IsAuthenticatedSeller(currentActive);
                if (!hasActive && desk.IsForeground
                    && (string.Equals(currentTitle, seller, StringComparison.Ordinal)
                        || RuntimeSellerCount() <= 1))
                {
                    MarkActiveSeller(desk, seller, "initial:" + (evidence ?? string.Empty));
                }

                Log.Info("已登记卖家与千牛窗口: seller=" + seller + ", pid=" + desk.ProcessId
                    + ", hwnd=" + hwnd + ", activeSeller=" + GetSeller(desk)
                    + ", evidence=" + (evidence ?? string.Empty));
                return desk;
            }
        }

        internal static Desk BindForegroundSeller(QN qn, string evidence)
        {
            var seller = qn == null || qn.Seller == null
                ? string.Empty
                : NormalizeSeller(qn.Seller.Nick);
            if (seller.Length == 0 || !IsAuthenticatedSeller(seller)) return null;

            var existing = FindSellerDesk(seller);
            if (existing != null)
            {
                if (existing.IsForeground)
                {
                    MarkActiveSeller(existing, seller, evidence);
                }
                return existing;
            }

            var foreground = Desk.Snapshot()
                .Where(x => x != null && x.IsAlive && x.IsForeground)
                .ToList();
            if (foreground.Count != 1) return null;

            var bound = BindResolvedSeller(foreground[0], seller, evidence);
            if (bound != null)
            {
                MarkActiveSeller(bound, seller, evidence);
            }
            return bound;
        }

        internal static bool MarkActiveSeller(Desk desk, string seller, string evidence)
        {
            seller = NormalizeSeller(seller);
            if (desk == null || desk.Hwnd == null || seller.Length == 0
                || !IsAuthenticatedSeller(seller)) return false;

            RememberKnown(desk, seller);
            var hwnd = desk.Hwnd.Handle;
            string previous;
            ActiveSellerByHwnd.TryGetValue(hwnd, out previous);
            ActiveSellerByHwnd[hwnd] = seller;
            if (!string.Equals(previous, seller, StringComparison.Ordinal))
            {
                Log.Info("共享千牛窗口活动客服已切换: hwnd=" + hwnd
                    + ", previousSeller=" + NormalizeSeller(previous)
                    + ", currentSeller=" + seller
                    + ", evidence=" + (evidence ?? string.Empty));
            }
            return true;
        }

        private static void RememberKnown(Desk desk, string seller)
        {
            if (desk == null || desk.Hwnd == null) return;
            seller = NormalizeSeller(seller);
            if (seller.Length == 0 || QnAccountFinder.IsGenericReceptionTitle(seller)
                || !IsAuthenticatedSeller(seller)) return;
            SellerToHwnd[seller] = desk.Hwnd.Handle;
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
                catch
                {
                    return false;
                }
            }
        }

        private static int RuntimeSellerCount()
        {
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null && qn.Seller != null
                        && !string.IsNullOrWhiteSpace(qn.Seller.Nick))
                    .Select(qn => qn.Seller.Nick.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
            }
            catch
            {
                return 0;
            }
        }

        private static void CleanupStaleBindings()
        {
            foreach (var pair in SellerToHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Value);
                if (desk != null && desk.IsAlive) continue;
                ForgetSeller(pair.Key, pair.Value);
            }

            foreach (var pair in ActiveSellerByHwnd.ToArray())
            {
                var desk = Desk.FindExistingByHwnd(pair.Key);
                if (desk != null && desk.IsAlive) continue;
                string ignored;
                ActiveSellerByHwnd.TryRemove(pair.Key, out ignored);
            }
        }

        private static void ForgetSeller(string seller, int hwnd)
        {
            int ignoredHwnd;
            SellerToHwnd.TryRemove(seller, out ignoredHwnd);

            string activeSeller;
            if (ActiveSellerByHwnd.TryGetValue(hwnd, out activeSeller)
                && string.Equals(NormalizeSeller(activeSeller), NormalizeSeller(seller), StringComparison.Ordinal))
            {
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
