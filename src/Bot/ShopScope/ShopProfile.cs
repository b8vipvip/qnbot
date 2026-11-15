using Newtonsoft.Json;
using System;

namespace Bot.ShopScope
{
    internal sealed class ShopProfile
    {
        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonProperty("shop_key")]
        public string ShopKey { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("seller_id")]
        public string SellerId { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }

        [JsonProperty("seller_nick")]
        public string SellerNick { get; set; }

        [JsonProperty("has_stable_seller_id")]
        public bool HasStableSellerId { get; set; }

        [JsonProperty("created_at_utc")]
        public DateTime CreatedAtUtc { get; set; }

        [JsonProperty("last_seen_at_utc")]
        public DateTime LastSeenAtUtc { get; set; }

        public ShopContext ToContext()
        {
            var sellerNick = (SellerNick ?? string.Empty).Trim();
            const string fallbackPrefix = "nick:";
            if (sellerNick.Length == 0
                && !HasStableSellerId
                && (SellerId ?? string.Empty).StartsWith(
                    fallbackPrefix, StringComparison.OrdinalIgnoreCase))
                sellerNick = (SellerId ?? string.Empty).Substring(fallbackPrefix.Length).Trim();

            return new ShopContext(
                ShopKey,
                Platform,
                SellerId,
                DisplayName,
                HasStableSellerId,
                sellerNick);
        }
    }
}
