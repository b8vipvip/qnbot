using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

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
