using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ShopScope
{
    internal sealed class AmbiguousShopIdentityException : InvalidOperationException
    {
        public AmbiguousShopIdentityException(string message) : base(message) { }
    }

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
            var context = ShopIdentityResolver.Resolve(qn.Seller);
            EnsureUnstableIdentityIsUnique(qn, context);
            return Profiles.GetOrCreate(context).ToContext();
        }

        // Resolve a persisted session by its stable ShopKey after restart. Prefer a
        // currently connected QN instance, but the profile registry also lets us
        // restore the scope before that account reconnects.
        public static ShopContext ResolveRuntimeByShopKey(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                throw new ArgumentException("ShopKey不能为空。", nameof(shopKey));

            var qnMatches = SnapshotQns()
                .Where(x => x != null && x.Seller != null)
                .Select(x => new { Qn = x, Context = TryResolveContext(x) })
                .Where(x => x.Context != null
                    && string.Equals(x.Context.ShopKey, shopKey, StringComparison.Ordinal))
                .ToList();
            if (qnMatches.Count > 1 && qnMatches.Any(x => !x.Context.HasStableSellerId))
                throw new AmbiguousShopIdentityException(
                    "昵称回退身份对应多个在线千牛实例，已阻止按ShopKey恢复。");
            if (qnMatches.Count > 0)
            {
                var match = qnMatches[0];
                EnsureUnstableIdentityIsUnique(match.Qn, match.Context);
                return Profiles.GetOrCreate(match.Context).ToContext();
            }

            var profiles = Profiles.GetAll();
            var profile = profiles.FirstOrDefault(x =>
                string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal));
            if (profile != null)
            {
                if (!profile.HasStableSellerId && !string.IsNullOrWhiteSpace(profile.SellerNick))
                {
                    var normalizedNick = ShopKeyGenerator.NormalizeFallbackNickname(profile.SellerNick);
                    if (profiles.Any(x => x != null
                        && !string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(
                            string.IsNullOrWhiteSpace(x.SellerNick) ? x.DisplayName : x.SellerNick)
                        && string.Equals(
                            ShopKeyGenerator.NormalizeFallbackNickname(
                                string.IsNullOrWhiteSpace(x.SellerNick) ? x.DisplayName : x.SellerNick),
                            normalizedNick,
                            StringComparison.Ordinal)))
                        throw new AmbiguousShopIdentityException(
                            "昵称回退ShopKey已对应多个注册店铺，已阻止恢复。");
                }
                return profile.ToContext();
            }

            throw new InvalidOperationException("未找到ShopKey对应的店铺身份，已阻止跨店回退。");
        }

        public static QN FindConnectedQnByShopKey(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return null;
            var matches = SnapshotQns()
                .Where(x => x != null && x.Seller != null)
                .Select(x => new { Qn = x, Context = TryResolveContext(x) })
                .Where(x => x.Context != null
                    && string.Equals(x.Context.ShopKey, shopKey, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0) return null;
            if (matches.Count > 1 && matches.Any(x => !x.Context.HasStableSellerId))
                return null;
            var match = matches[0];
            try
            {
                EnsureUnstableIdentityIsUnique(match.Qn, match.Context);
                return match.Qn;
            }
            catch
            {
                return null;
            }
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

            var qnMatches = SnapshotQns()
                .Where(x => x != null
                    && x.Seller != null
                    && string.Equals(
                        (x.Seller.Nick ?? string.Empty).Trim(),
                        sellerNick,
                        StringComparison.Ordinal))
                .Select(x => new { Qn = x, Context = TryResolveContext(x) })
                .Where(x => x.Context != null)
                .ToList();
            if (qnMatches.Count > 1 && qnMatches.Any(x => !x.Context.HasStableSellerId))
                throw new AmbiguousShopIdentityException(
                    "多个千牛实例只有相同昵称、没有稳定TargetId，已阻止合并为同一店铺。");

            var matches = qnMatches.Select(x => x.Context)
                .Concat(Profiles.GetAll()
                    .Where(x => x != null
                        && (string.Equals((x.SellerNick ?? string.Empty).Trim(),
                                sellerNick, StringComparison.Ordinal)
                            || (string.IsNullOrWhiteSpace(x.SellerNick)
                                && string.Equals((x.DisplayName ?? string.Empty).Trim(),
                                    sellerNick, StringComparison.Ordinal))))
                    .Select(x => x.ToContext()))
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
                    var currentContext = ShopIdentityResolver.Resolve(current.Seller);
                    EnsureUnstableIdentityIsUnique(current, currentContext);
                    matches.Add(currentContext);
                }
            }

            if (matches.Count == 0)
                throw new InvalidOperationException("未找到卖家“" + sellerNick + "”对应的千牛登录身份。请先保持该店铺在线。");
            if (matches.Count > 1)
                throw new AmbiguousShopIdentityException(
                    "同一卖家昵称匹配到多个不同店铺身份，已阻止自动绑定。");

            if (!matches[0].HasStableSellerId)
            {
                var normalizedNick = ShopKeyGenerator.NormalizeFallbackNickname(sellerNick);
                var profiles = Profiles.GetAll();
                if (profiles.Any(x => x != null
                    && !string.Equals(x.ShopKey, matches[0].ShopKey, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(x.SellerNick)
                    && string.Equals(
                        ShopKeyGenerator.NormalizeFallbackNickname(x.SellerNick),
                        normalizedNick,
                        StringComparison.Ordinal)))
                    throw new AmbiguousShopIdentityException(
                        "昵称回退身份与其他注册店铺昵称冲突，已阻止自动绑定。");
            }

            return matches[0];
        }

        private static void EnsureUnstableIdentityIsUnique(QN source, ShopContext context)
        {
            if (context == null || context.HasStableSellerId) return;
            var nick = source == null || source.Seller == null
                ? context.SellerNick
                : (source.Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(nick))
                throw new AmbiguousShopIdentityException("昵称回退身份缺少可验证的卖家昵称。");
            var normalized = ShopKeyGenerator.NormalizeFallbackNickname(nick);
            var matchingQns = SnapshotQns().Where(x => x != null && x.Seller != null
                && !string.IsNullOrWhiteSpace(x.Seller.Nick)
                && string.Equals(
                    ShopKeyGenerator.NormalizeFallbackNickname(x.Seller.Nick),
                    normalized,
                    StringComparison.Ordinal))
                .ToList();
            if (matchingQns.Count > 1)
                throw new AmbiguousShopIdentityException(
                    "多个千牛实例只有相同昵称、没有稳定TargetId，已阻止合并为同一店铺。");

            var conflictingProfiles = Profiles.GetAll().Where(x => x != null
                && !string.Equals(x.ShopKey, context.ShopKey, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(
                    string.IsNullOrWhiteSpace(x.SellerNick) ? x.DisplayName : x.SellerNick)
                && string.Equals(
                    ShopKeyGenerator.NormalizeFallbackNickname(
                        string.IsNullOrWhiteSpace(x.SellerNick) ? x.DisplayName : x.SellerNick),
                    normalized,
                    StringComparison.Ordinal))
                .ToList();
            if (conflictingProfiles.Count > 0)
                throw new AmbiguousShopIdentityException(
                    "昵称回退身份与其他注册店铺冲突，已阻止自动绑定。");
        }

        private static ShopContext TryResolveContext(QN qn)
        {
            try
            {
                return qn == null || qn.Seller == null
                    ? null : ShopIdentityResolver.Resolve(qn.Seller);
            }
            catch
            {
                return null;
            }
        }

        private static IList<QN> SnapshotQns()
        {
            try
            {
                var result = QN.QNSet == null ? new List<QN>() : QN.QNSet.ToList();
                var current = QN.CurQN;
                if (current != null && !result.Any(x => ReferenceEquals(x, current)))
                    result.Add(current);
                return result;
            }
            catch
            {
                return new List<QN>();
            }
        }
    }
}
