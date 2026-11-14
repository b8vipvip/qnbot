using System;

namespace Bot.ShopScope
{
    /// <summary>
    /// Immutable identity carried by shop-scoped operations.
    /// DisplayName is informational only and must never be used as a storage or authorization key.
    /// </summary>
    internal sealed class ShopContext : IEquatable<ShopContext>
    {
        public const string QianniuPlatform = "qianniu";

        public ShopContext(
            string shopKey,
            string platform,
            string sellerId,
            string displayName,
            bool hasStableSellerId,
            string sellerNick = null)
        {
            ShopKey = Require(shopKey, nameof(shopKey));
            Platform = Require(platform, nameof(platform)).ToLowerInvariant();
            SellerId = Require(sellerId, nameof(sellerId));
            DisplayName = (displayName ?? string.Empty).Trim();
            SellerNick = (sellerNick ?? string.Empty).Trim();
            HasStableSellerId = hasStableSellerId;
        }

        public string ShopKey { get; private set; }
        public string Platform { get; private set; }
        public string SellerId { get; private set; }
        public string DisplayName { get; private set; }
        public string SellerNick { get; private set; }
        public bool HasStableSellerId { get; private set; }

        public bool Equals(ShopContext other)
        {
            return other != null
                && string.Equals(ShopKey, other.ShopKey, StringComparison.Ordinal)
                && string.Equals(Platform, other.Platform, StringComparison.Ordinal)
                && string.Equals(SellerId, other.SellerId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ShopContext);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(ShopKey);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Platform);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(SellerId);
                return hash;
            }
        }

        public override string ToString()
        {
            return ShopKey + " (" + (HasStableSellerId ? "stable" : "fallback") + ")";
        }

        private static string Require(string value, string name)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new ArgumentException("Value is required.", name);
            return value;
        }
    }
}
