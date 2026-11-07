using Bot.ShopScope;
using BotLib;
using DbEntity;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class ShopCatalogProduct
    {
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Status { get; set; }
        public override string ToString() { return (Title ?? "未命名商品") + "（" + ItemId + "）"; }
    }

    internal sealed class ShopOrderProductReply
    {
        public string ItemId { get; set; }
        public string Reply { get; set; }
    }

    internal sealed class ShopCatalogSnapshot
    {
        public DateTime UpdatedAtUtc { get; set; }
        public List<ShopCatalogProduct> Products { get; set; }
    }

    /// <summary>
    /// Seller-scoped product catalog. Only real, authorized QN MTop results may replace
    /// the last good cache. An empty/failed query never erases goods or custom replies.
    /// All order replies are still delivered exclusively by OrderPlacedAutoReplyService.
    /// </summary>
    internal static class ShopProductCatalogService
    {
        private const string RulesKey = "shop.order.product-replies.v1";
        private const int PageSize = 50;
        private const int MaxPages = 20;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> LastAttempts =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Timer> Pollers =
            new ConcurrentDictionary<string, Timer>(StringComparer.Ordinal);

        private static string CachePath(string seller)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            return Path.Combine(Paths.GetCacheRoot(shop), "shop-products-v1.json");
        }

        private static ShopScopedSettingsStore RuleStore(string seller)
        {
            return new ShopScopedSettingsStore(
                ShopContextLocator.ResolveRuntimeBySellerNick(seller), Paths);
        }

        internal static ShopCatalogSnapshot Load(string seller)
        {
            var path = CachePath(seller);
            if (!File.Exists(path)) return new ShopCatalogSnapshot { Products = new List<ShopCatalogProduct>() };
            var data = JsonConvert.DeserializeObject<ShopCatalogSnapshot>(File.ReadAllText(path));
            if (data == null) throw new InvalidDataException("店铺商品缓存结构无效");
            data.Products = data.Products ?? new List<ShopCatalogProduct>();
            return data;
        }

        internal static List<ShopOrderProductReply> GetOrderRules(string seller)
        {
            string json;
            if (!RuleStore(seller).TryGetString(RulesKey, out json) || string.IsNullOrWhiteSpace(json))
                return new List<ShopOrderProductReply>();
            return JsonConvert.DeserializeObject<List<ShopOrderProductReply>>(json)
                ?? new List<ShopOrderProductReply>();
        }

        internal static void SaveOrderRules(string seller, IEnumerable<ShopOrderProductReply> rules)
        {
            var entries = (rules ?? Enumerable.Empty<ShopOrderProductReply>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Reply))
                .Select(x => new ShopOrderProductReply
                {
                    ItemId = (x.ItemId ?? string.Empty).Trim(),
                    Reply = (x.Reply ?? string.Empty).Trim()
                }).ToList();
            if (entries.Count > 1000 || entries.Any(x =>
                x.ItemId.Length < 6 || x.ItemId.Length > 20 ||
                !x.ItemId.All(char.IsDigit) || x.Reply.Length > 4000))
                throw new InvalidOperationException("商品回复规则格式或数量不合法");
            if (entries.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                throw new InvalidOperationException("存在重复的商品回复规则");
            RuleStore(seller).SetString(RulesKey, JsonConvert.SerializeObject(entries));
        }

        internal static string MatchOrderReply(string seller, string itemId)
        {
            itemId = (itemId ?? string.Empty).Trim();
            if (itemId.Length < 6 || itemId.Length > 20 || !itemId.All(char.IsDigit))
                return string.Empty;
            // The rule is a stable item-ID match; product name and cache freshness
            // never control which buyer receives this answer.
            var rule = GetOrderRules(seller).FirstOrDefault(x => x != null &&
                string.Equals(x.ItemId, itemId, StringComparison.Ordinal));
            return rule == null ? string.Empty : (rule.Reply ?? string.Empty).Trim();
        }

        internal static void EnsureScheduled(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            Pollers.GetOrAdd(seller, s => new Timer(_ =>
            {
                Task.Run(async () =>
                {
                    try { await RefreshAsync(s, false).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Info("店铺商品周期更新失败: seller=" + s + ", error=" + ex.Message); }
                });
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(1)));
        }

        internal static async Task<int> RefreshAsync(string seller, bool force)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) throw new InvalidOperationException("必须选择当前店铺");
            var gate = Gates.GetOrAdd(seller, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0).ConfigureAwait(false))
                throw new InvalidOperationException("本店商品信息正在更新，请稍后查看");
            try
            {
                var snapshot = Load(seller);
                if (!force && snapshot.UpdatedAtUtc != DateTime.MinValue &&
                    DateTime.UtcNow - snapshot.UpdatedAtUtc < RefreshInterval)
                    return snapshot.Products.Count;
                DateTime last;
                if (!force && LastAttempts.TryGetValue(seller, out last) &&
                    DateTime.UtcNow - last < FailureBackoff)
                    return snapshot.Products.Count;
                LastAttempts[seller] = DateTime.UtcNow;

                var qn = QN.FindExistingBySellerNick(seller);
                if (qn == null || qn.CDP == null || qn.CDP.IsInvalidated ||
                    string.IsNullOrWhiteSpace(qn.CDP.SessionId) || qn.Seller == null ||
                    !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal))
                    throw new InvalidOperationException("当前店铺千牛未连接，无法获取商品");
                var products = new Dictionary<string, ShopCatalogProduct>(StringComparer.Ordinal);
                for (var page = 1; page <= MaxPages; page++)
                {
                    // Reuse the QN authorized MTop channel and the repository's
                    // existing ZnkfItemSearchResponse DTO; never scrape another shop.
                    var response = await qn.CDP.InvokeMTop<ZnkfItemSearchResponse>(
                        "mtop.taobao.qianniu.cs.item.search",
                        new { keyword = "", pageNo = page, pageSize = PageSize })
                        .ConfigureAwait(false);
                    if (response == null || response.data == null || response.data.data == null)
                        throw new InvalidOperationException("千牛商品接口没有返回有效列表，第 " + page + " 页");
                    var entries = response.data.data;
                    foreach (var item in entries.Where(x => x != null && x.itemId > 0))
                    {
                        var id = item.itemId.ToString();
                        products[id] = new ShopCatalogProduct
                        {
                            ItemId = id,
                            Title = (item.title ?? string.Empty).Trim(),
                            Url = (item.itemUrl ?? string.Empty).Trim(),
                            Status = (item.approveStatus ?? string.Empty).Trim()
                        };
                    }
                    if (entries.Count < PageSize) break;
                    if (page == MaxPages)
                        throw new InvalidOperationException("本店商品已超过单次同步上限，缓存未覆盖，请检查分页");
                }
                if (products.Count == 0)
                    throw new InvalidOperationException("千牛接口没有返回商品；保留原缓存和全部商品专属答案");

                var updated = new ShopCatalogSnapshot
                {
                    UpdatedAtUtc = DateTime.UtcNow,
                    Products = products.Values.OrderBy(x => x.Title, StringComparer.Ordinal)
                        .ThenBy(x => x.ItemId, StringComparer.Ordinal).ToList()
                };
                var path = CachePath(seller);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonConvert.SerializeObject(updated));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                Log.Info("店铺商品缓存已更新: seller=" + seller + ", count=" + updated.Products.Count
                    + ", updatedAtUtc=" + updated.UpdatedAtUtc.ToString("o"));
                return updated.Products.Count;
            }
            finally { gate.Release(); }
        }
    }
}
