using Bot.ShopScope;
using BotLib;
using System;

namespace Bot.ChromeNs
{
    internal enum BotReplyMode
    {
        AiFirst = 0,
        LocalFirst = 1
    }

    /// <summary>
    /// Shop-scoped reply routing preference.
    /// AiFirst keeps approved Knowledge V2 matches as AI context and lets AI produce the final answer.
    /// LocalFirst permits only the Knowledge V2 runtime bridge's high-confidence direct-answer
    /// decision to return without an AI call; all other questions continue through the normal AI pipeline.
    /// </summary>
    internal static class ReplyModeService
    {
        internal const string SettingsKey = "message.reply_mode";
        internal const string AiFirstValue = "ai_first";
        internal const string LocalFirstValue = "local_first";

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static BotReplyMode GetMode(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0 && ShopSettingsScope.Current == null) return BotReplyMode.AiFirst;

            try
            {
                var shop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
                var store = new ShopScopedSettingsStore(shop, Paths);
                string value;
                if (!store.TryGetString(SettingsKey, out value)) return BotReplyMode.AiFirst;
                return Parse(value);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取回复模式失败，已按AI优先运行: seller=" + seller + ", error=" + ex.Message, 10);
                return BotReplyMode.AiFirst;
            }
        }

        public static bool IsLocalFirst(string seller)
        {
            return GetMode(seller) == BotReplyMode.LocalFirst;
        }

        public static void Save(string seller, BotReplyMode mode)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0 && ShopSettingsScope.Current == null)
                throw new ArgumentException("保存回复模式需要卖家身份或显式店铺作用域。", nameof(seller));

            var shop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(SettingsKey, Serialize(mode));
            Log.Info("回复模式已保存: seller=" + seller + ", mode=" + GetDisplayName(mode));
        }

        public static BotReplyMode Parse(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Equals(LocalFirstValue, StringComparison.OrdinalIgnoreCase)
                || value.Equals("本地优先", StringComparison.Ordinal))
            {
                return BotReplyMode.LocalFirst;
            }
            return BotReplyMode.AiFirst;
        }

        public static string Serialize(BotReplyMode mode)
        {
            return mode == BotReplyMode.LocalFirst ? LocalFirstValue : AiFirstValue;
        }

        public static string GetDisplayName(BotReplyMode mode)
        {
            return mode == BotReplyMode.LocalFirst ? "本地优先" : "AI优先";
        }
    }

    internal static class BotMessageSuffixService
    {
        internal const string SettingsKey = "message.bot_message_suffix";
        internal const string DefaultSuffix = "[AI]";
        internal const int MaxSuffixLength = 32;

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static string GetSuffix(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0 && ShopSettingsScope.Current == null) return DefaultSuffix;

            try
            {
                var shop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
                return GetSuffix(new ShopScopedSettingsStore(shop, Paths));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot消息后缀失败，已使用默认[AI]: seller=" + seller + ", error=" + ex.Message, 10);
                return DefaultSuffix;
            }
        }

        public static string GetCurrentSuffix()
        {
            try
            {
                var shop = ShopSettingsScope.Current;
                if (shop == null) return DefaultSuffix;
                return GetSuffix(new ShopScopedSettingsStore(shop, Paths));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取当前店铺Bot消息后缀失败，已使用默认[AI]: " + ex.Message, 10);
                return DefaultSuffix;
            }
        }

        public static void Save(string seller, string suffix)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0 && ShopSettingsScope.Current == null)
                throw new ArgumentException("保存Bot消息后缀需要卖家身份或显式店铺作用域。", nameof(seller));

            var normalized = Normalize(suffix);
            var shop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(SettingsKey, normalized);
            Log.Info("Bot消息后缀已保存: seller=" + seller + ", suffix=" + normalized);
        }

        public static string Apply(string seller, string value)
        {
            value = BotOutboundMessageFormatter.StripAiMarker((value ?? string.Empty).Trim());
            if (value.Length == 0 || value.StartsWith("错误：", StringComparison.Ordinal)) return value;

            var suffix = GetSuffix(seller);
            if (suffix.Length == 0) return value;
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
            return value + " " + suffix;
        }

        public static string Normalize(string suffix)
        {
            suffix = (suffix ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (suffix.Length > MaxSuffixLength)
                suffix = suffix.Substring(0, MaxSuffixLength).Trim();
            return suffix;
        }

        private static string GetSuffix(ShopScopedSettingsStore store)
        {
            string value;
            if (store == null || !store.TryGetString(SettingsKey, out value)) return DefaultSuffix;
            return Normalize(value);
        }
    }

}
