using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class BotReplyPreset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public string Trigger { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Shop-scoped, persisted catalog for built-in Bot replies. Defaults are seeded once;
    /// subsequent reads never overwrite an operator's edits.
    /// </summary>
    internal static class BotReplyPresetService
    {
        private const string SettingsKey = "bot.reply_presets.product_link.v1";
        private static readonly object Sync = new object();
        // Explicit item-level rule: 商品ID:1234567890123 (or full-width colon).
        // Unlike the legacy free-text Trigger, this form is an executable condition.
        private static readonly Regex ExactItemTrigger = new Regex(
            @"^商品ID\s*[:：]\s*(\d{6,20})\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ExactProductUrlTrigger = new Regex(
            @"^https?://(?:item\.taobao\.com|detail\.tmall\.com|h5\.m\.taobao\.com)/[^\s]*?[?&](?:id|item_id)=(\d{6,20})(?:[&#]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static List<BotReplyPreset> GetPresets(string seller)
        {
            lock (Sync)
            {
                try
                {
                    var store = CreateStore(seller);
                    string json;
                    if (!store.TryGetString(SettingsKey, out json) || string.IsNullOrWhiteSpace(json))
                    {
                        var defaults = CreateDefaults();
                        store.SetString(SettingsKey, JsonConvert.SerializeObject(defaults));
                        return Clone(defaults);
                    }

                    var parsed = JsonConvert.DeserializeObject<List<BotReplyPreset>>(json);
                    if (parsed == null) throw new InvalidOperationException("内置话术配置格式无效。");
                    return Normalize(parsed);
                }
                catch (Exception ex)
                {
                    // Fail closed: damaged settings must never re-enable replies
                    // that an operator may have deliberately disabled.
                    Log.ErrorWithMaxCount("读取店铺内置Bot话术失败，已禁止预设自动回复：" + ex.Message, 5);
                    return new List<BotReplyPreset>();
                }
            }
        }

        // Sandbox-only read: do not call GetPresets here, because GetPresets
        // initializes missing default settings on disk as a side effect.
        internal static List<BotReplyPreset> GetEnabledPresetsForPreview(string seller)
        {
            lock (Sync)
            {
                try
                {
                    var store = CreateStore(seller);
                    string json;
                    var presets = !store.TryGetString(SettingsKey, out json)
                        || string.IsNullOrWhiteSpace(json)
                        ? CreateDefaults()
                        : Normalize(JsonConvert.DeserializeObject<List<BotReplyPreset>>(json));
                    return (presets ?? new List<BotReplyPreset>())
                        .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Text))
                        .ToList();
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("读取模拟测试台内置话术失败：" + ex.Message, 5);
                    return new List<BotReplyPreset>();
                }
            }
        }

        public static List<BotReplyPreset> GetEnabledPresets(string seller)
        {
            return GetPresets(seller).Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Text)).ToList();
        }

        public static void SavePresets(string seller, IEnumerable<BotReplyPreset> presets)
        {
            var normalized = Normalize((presets ?? Enumerable.Empty<BotReplyPreset>()).ToList());
            var enabledExactItemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var preset in normalized)
            {
                if (!IsItemScopedTrigger(preset.Trigger))
                {
                    // Unknown free-text triggers have never been executable: fail
                    // rather than silently applying a product-specific reply to all links.
                    if (!string.Equals(preset.Trigger, "买家消息包含商品链接", StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "触发条件只能是“买家消息包含商品链接”、商品ID:数字或含商品ID的淘宝/天猫完整商品链接。");
                    continue;
                }
                string itemId;
                if (!TryParseExactItemTrigger(preset.Trigger, out itemId))
                    throw new InvalidOperationException(
                        "商品专属话术触发条件无有效商品ID。请输入 商品ID:1234567890123 或含 id 参数的淘宝/天猫完整商品链接。");
                if (preset.Enabled && !enabledExactItemIds.Add(itemId))
                    throw new InvalidOperationException(
                        "商品ID " + itemId + " 已有一条启用的专属话术，请先修改或停用原条目，避免冲突。");
            }
            if (normalized.Count > 100) throw new InvalidOperationException("内置话术最多保存 100 条。");
            if (normalized.Any(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Text)))
                throw new InvalidOperationException("话术名称和内容不能为空。");
            if (normalized.Any(x => x.Name.Length > 100 || x.Text.Length > 4000 || x.Trigger.Length > 300 || x.Description.Length > 1000))
                throw new InvalidOperationException("话术字段过长，请缩短后再保存。");
            if (normalized.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
                throw new InvalidOperationException("话术 ID 重复，请刷新后重试。");

            lock (Sync)
            {
                CreateStore(seller).SetString(SettingsKey, JsonConvert.SerializeObject(normalized));
            }
        }

        /// <summary>
        /// Exact item rules always win over generic product-link acknowledgements.
        /// The existing generic replies remain available for other items; no AI or
        /// knowledge-index thresholds are bypassed for non-product messages.
        /// </summary>
        public static List<BotReplyPreset> GetPresetsForProductLink(string seller, string itemId)
        {
            return SelectForProductItem(GetEnabledPresets(seller), itemId);
        }

        internal static List<BotReplyPreset> GetEnabledPresetsForProductLinkPreview(
            string seller, string itemId)
        {
            // Must not initialize missing shop settings from a read-only sandbox.
            return SelectForProductItem(GetEnabledPresetsForPreview(seller), itemId);
        }

        private static List<BotReplyPreset> SelectForProductItem(
            IEnumerable<BotReplyPreset> presets, string itemId)
        {
            var enabled = (presets ?? Enumerable.Empty<BotReplyPreset>())
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Text))
                .ToList();
            itemId = (itemId ?? string.Empty).Trim();
            if (itemId.Length > 0)
            {
                var exact = enabled.Where(x =>
                {
                    string configured;
                    return TryParseExactItemTrigger(x.Trigger, out configured)
                        && string.Equals(configured, itemId, StringComparison.Ordinal);
                }).OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
                if (exact.Count > 0) return exact;
            }
            // Never send a different item's answer for an unknown or unmatched item.
            return enabled.Where(x => !IsItemScopedTrigger(x.Trigger)).ToList();
        }

        internal static bool IsItemScopedTrigger(string trigger)
        {
            var normalized = (trigger ?? string.Empty).TrimStart();
            return normalized.StartsWith("商品ID", StringComparison.Ordinal)
                || normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryParseExactItemTrigger(string trigger, out string itemId)
        {
            itemId = string.Empty;
            var value = (trigger ?? string.Empty).Trim();
            var match = ExactItemTrigger.Match(value);
            if (!match.Success) match = ExactProductUrlTrigger.Match(value);
            if (!match.Success) return false;
            itemId = match.Groups[1].Value;
            return true;
        }

        public static bool IsPresetReply(string seller, string answer)
        {
            BotReplyPreset ignored;
            return TryFindPreset(seller, answer, out ignored);
        }

        public static bool TryFindPreset(string seller, string answer, out BotReplyPreset preset)
        {
            preset = null;
            var normalized = NormalizeReply(answer);
            if (normalized.Length == 0) return false;
            preset = GetPresets(seller).FirstOrDefault(x =>
                string.Equals(NormalizeReply(x.Text), normalized, StringComparison.Ordinal));
            return preset != null;
        }

        public static string ResolveSourceLabel(string seller, string answer)
        {
            BotReplyPreset preset;
            if (!TryFindPreset(seller, answer, out preset)) return "内置话术（来源待核实）";
            return BuildSourceLabel(preset);
        }

        public static string BuildSourceLabel(BotReplyPreset preset)
        {
            if (preset == null) return "内置话术（来源待核实）";
            return "内置话术：" + (preset.Name ?? "未命名") + "（" + (preset.Id ?? string.Empty) + "）";
        }

        public static string NormalizeReply(string answer)
        {
            var text = (answer ?? string.Empty).Trim();
            if (text.EndsWith("[A]", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 3).TrimEnd();
            return text;
        }

        private static ShopScopedSettingsStore CreateStore(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0)
                throw new InvalidOperationException("无法确定当前店铺，不能读取或修改店铺话术。");
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            return new ShopScopedSettingsStore(shop, Paths);
        }

        private static List<BotReplyPreset> Normalize(IEnumerable<BotReplyPreset> presets)
        {
            return (presets ?? Enumerable.Empty<BotReplyPreset>())
                .Where(x => x != null)
                .Select(x => new BotReplyPreset
                {
                    Id = string.IsNullOrWhiteSpace(x.Id) ? Guid.NewGuid().ToString("N") : x.Id.Trim(),
                    Name = (x.Name ?? string.Empty).Trim(),
                    Text = (x.Text ?? string.Empty).Trim(),
                    Trigger = string.IsNullOrWhiteSpace(x.Trigger) ? "买家消息包含商品链接" : x.Trigger.Trim(),
                    Description = (x.Description ?? string.Empty).Trim(),
                    Enabled = x.Enabled,
                    UpdatedAt = x.UpdatedAt == DateTime.MinValue ? DateTime.Now : x.UpdatedAt
                })
                .ToList();
        }

        private static List<BotReplyPreset> Clone(IEnumerable<BotReplyPreset> presets)
        {
            return Normalize(presets);
        }

        private static List<BotReplyPreset> CreateDefaults()
        {
            return new List<BotReplyPreset>
            {
                new BotReplyPreset { Id = "product-link-ack-01", Name = "商品链接确认 1", Text = "我看到您发来的商品链接了，想咨询哪方面呢？", Trigger = "买家消息包含商品链接", Description = "商品链接的首轮确认话术。", Enabled = true, UpdatedAt = DateTime.Now },
                new BotReplyPreset { Id = "product-link-ack-02", Name = "商品链接确认 2", Text = "商品链接已收到，您想了解规格、使用还是售后呢？", Trigger = "买家消息包含商品链接", Description = "商品链接的首轮确认话术。", Enabled = true, UpdatedAt = DateTime.Now },
                new BotReplyPreset { Id = "product-link-ack-03", Name = "商品链接确认 3", Text = "我看到了这款商品，请问您主要想确认哪项信息？", Trigger = "买家消息包含商品链接", Description = "商品链接的首轮确认话术。", Enabled = true, UpdatedAt = DateTime.Now },
                new BotReplyPreset { Id = "product-link-ack-04", Name = "商品链接确认 4", Text = "这款商品链接我收到了，您想重点了解什么呢？", Trigger = "买家消息包含商品链接", Description = "商品链接的首轮确认话术。", Enabled = true, UpdatedAt = DateTime.Now }
            };
        }
    }
}
