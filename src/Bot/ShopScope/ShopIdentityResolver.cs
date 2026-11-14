using DbEntity;
using System;

namespace Bot.ShopScope
{
    /// <summary>
    /// Resolves the identity already returned by Qianniu's GetCurrentLoginID/loginID payloads.
    /// TargetId is preferred. Nick is an explicit compatibility fallback and is marked unstable.
    /// </summary>
    internal static class ShopIdentityResolver
    {
        public static ShopContext Resolve(LocalUser seller)
        {
            if (seller == null) throw new ArgumentNullException(nameof(seller));

            var displayName = FirstNonEmpty(seller.Display, seller.Nick);
            var targetId = (seller.TargetId ?? string.Empty).Trim();
            string sellerIdentity;
            bool hasStableSellerId;

            if (targetId.Length > 0)
            {
                sellerIdentity = targetId;
                hasStableSellerId = true;
            }
            else
            {
                var nickname = (seller.Nick ?? string.Empty).Trim();
                if (nickname.Length == 0)
                    throw new InvalidOperationException("Qianniu seller identity is missing TargetId and Nick.");
                sellerIdentity = "nick:" + ShopKeyGenerator.NormalizeFallbackNickname(nickname);
                hasStableSellerId = false;
            }

            return new ShopContext(
                ShopKeyGenerator.Create(ShopContext.QianniuPlatform, sellerIdentity),
                ShopContext.QianniuPlatform,
                sellerIdentity,
                displayName,
                hasStableSellerId,
                (seller.Nick ?? string.Empty).Trim());
        }

        private static string FirstNonEmpty(string first, string second)
        {
            first = (first ?? string.Empty).Trim();
            if (first.Length > 0) return first;
            return (second ?? string.Empty).Trim();
        }
    }
}
