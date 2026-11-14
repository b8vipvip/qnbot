using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ShopScope
{
    internal static class ShopContextLocator
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);

        public static ShopContext ResolveBySellerNick(string sellerNick)
        {
            return Profiles.GetOrCreate(ResolveBySellerNickCore(sellerNick)).ToContext();
        }

        public static ShopContext ResolveRuntimeBySellerNick(string sellerNick)
        {
            return ResolveBySellerNickCore(sellerNick);
        }

        // Resolve from the exact QN instance that raised an event. Seller nick is
        // display data and may be shared by multiple accounts; never infer identity
        // from the nick when the source QN object is available.
        public static ShopContext ResolveRuntimeBySeller(QN qn)
        {
            if (qn == null || qn.Seller == null)
                throw new InvalidOperationException("千牛会话缺少卖家身份，无法建立店铺作用域。");
            return Profiles.GetOrCreate(ShopIdentityResolver.Resolve(qn.Seller)).ToContext();
        }

        // Resolve a persisted session by its stable ShopKey after restart. Prefer a
        // currently connected QN instance, but the profile registry also lets us
        // restore the scope before that account reconnects.
        public static ShopContext ResolveRuntimeByShopKey(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                throw new ArgumentException("ShopKey不能为空。", nameof(shopKey));

            var match = SnapshotQns()
                .Where(x => x != null && x.Seller != null)
                .Select(x => ShopIdentityResolver.Resolve(x.Seller))
                .FirstOrDefault(x => string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal));
            if (match != null) return Profiles.GetOrCreate(match).ToContext();

            var profile = Profiles.GetAll().FirstOrDefault(x =>
                string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal));
            if (profile != null) return profile.ToContext();

            throw new InvalidOperationException("未找到ShopKey对应的店铺身份，已阻止跨店回退。");
        }

        public static ShopContext ResolveCurrentForUi()
        {
            var current = QN.CurQN;
            if (current == null || current.Seller == null)
                throw new InvalidOperationException("当前没有可用的千牛卖家会话。" );
            var context = ShopIdentityResolver.Resolve(current.Seller);
            return Profiles.GetOrCreate(context).ToContext();
        }

        private static ShopContext ResolveBySellerNickCore(string sellerNick)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            if (sellerNick.Length == 0)
                throw new ArgumentException("卖家昵称不能为空。", nameof(sellerNick));

            var matches = SnapshotQns()
                .Where(x => x != null
                    && x.Seller != null
                    && string.Equals(
                        (x.Seller.Nick ?? string.Empty).Trim(),
                        sellerNick,
                        StringComparison.Ordinal))
                .Select(x => ShopIdentityResolver.Resolve(x.Seller))
                .GroupBy(x => x.ShopKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();

            if (matches.Count == 0)
            {
                var current = QN.CurQN;
                if (current != null
                    && current.Seller != null
                    && string.Equals(
                        (current.Seller.Nick ?? string.Empty).Trim(),
                        sellerNick,
                        StringComparison.Ordinal))
                {
                    matches.Add(ShopIdentityResolver.Resolve(current.Seller));
                }
            }

            if (matches.Count == 0)
                throw new InvalidOperationException("未找到卖家“" + sellerNick + "”对应的千牛登录身份。请先保持该店铺在线。" );
            if (matches.Count > 1)
                throw new InvalidOperationException("同一卖家昵称匹配到多个不同店铺身份，已阻止自动绑定。" );

            return matches[0];
        }

        private static IList<QN> SnapshotQns()
        {
            try
            {
                if (QN.QNSet == null) return new List<QN>();
                return QN.QNSet.ToArray();
            }
            catch
            {
                return new List<QN>();
            }
        }
    }
}
