using Bot.ShopScope;
using BotLib;
using DbEntity;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        public bool ManuallyAdded { get; set; }
        [JsonIgnore]
        public bool LocalManaged { get; set; }
        [JsonIgnore]
        public string SourceLabel
        {
            get { return ManuallyAdded ? "本地新增" : (LocalManaged ? "本地修改" : "千牛同步"); }
        }
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
        public string Source { get; set; }
        public bool Partial { get; set; }
        public string LastFullCatalogError { get; set; }
        public DateTime LastFullCatalogAttemptUtc { get; set; }
    }

    internal sealed class ShopCatalogManagedEdits
    {
        public List<ShopCatalogProduct> Overrides { get; set; }
        public List<string> HiddenItemIds { get; set; }
    }

    /// <summary>
    /// Seller-scoped product catalog. Only real, authorized QN MTop results may replace
    /// the last good cache. An empty/failed query never erases goods or custom replies.
    /// All order replies are still delivered exclusively by OrderPlacedAutoReplyService.
    /// </summary>
    internal static class ShopProductCatalogService
    {
        private const string RulesKey = "shop.order.product-replies.v1";
        private const string ManualProductsKey = "shop.catalog.manual-products.v1";
        private const string ManagedProductsKey = "shop.catalog.managed-products.v1";
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
            var data = File.Exists(path)
                ? JsonConvert.DeserializeObject<ShopCatalogSnapshot>(File.ReadAllText(path))
                : new ShopCatalogSnapshot { Products = new List<ShopCatalogProduct>() };
            if (data == null) throw new InvalidDataException("店铺商品缓存结构无效");
            data.Products = data.Products ?? new List<ShopCatalogProduct>();
            // A merchant may add a missing item by its original item URL.
            // These remain clearly marked as manual and are never proof of a
            // successful full seller catalog sync.
            var products = data.Products.Where(x => x != null && !x.ManuallyAdded
                && !string.IsNullOrWhiteSpace(x.ItemId))
                .ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
            foreach (var manual in LoadManualProducts(seller))
            {
                ShopCatalogProduct remote;
                if (!products.TryGetValue(manual.ItemId, out remote))
                    products[manual.ItemId] = manual;
            }
            // The local management overlay is applied AFTER every automatic
            // fetch. It changes only Bot's own catalog, not Taobao listings.
            var managed = LoadManagedEdits(seller);
            foreach (var local in managed.Overrides.Where(x =>
                x != null && !string.IsNullOrWhiteSpace(x.ItemId)))
            {
                local.LocalManaged = true;
                ShopCatalogProduct currentRemote;
                if (!local.ManuallyAdded && products.TryGetValue(local.ItemId, out currentRemote))
                    local.Status = currentRemote.Status; // stay current with QN status
                products[local.ItemId] = local;
            }
            foreach (var hidden in managed.HiddenItemIds.Where(x =>
                !string.IsNullOrWhiteSpace(x)))
                products.Remove(hidden);
            data.Products = products.Values.OrderBy(x => x.Title, StringComparer.Ordinal)
                .ThenBy(x => x.ItemId, StringComparer.Ordinal).ToList();
            if (data.UpdatedAtUtc == DateTime.MinValue && data.Products.Count > 0)
            {
                data.Partial = true;
                data.Source = "manual";
            }
            return data;
        }

        private static ShopCatalogManagedEdits LoadManagedEdits(string seller)
        {
            string json;
            var value = RuleStore(seller).TryGetString(ManagedProductsKey, out json)
                && !string.IsNullOrWhiteSpace(json)
                ? JsonConvert.DeserializeObject<ShopCatalogManagedEdits>(json)
                : null;
            value = value ?? new ShopCatalogManagedEdits();
            value.Overrides = value.Overrides ?? new List<ShopCatalogProduct>();
            value.HiddenItemIds = value.HiddenItemIds ?? new List<string>();
            return value;
        }

        private static ShopCatalogProduct CloneForManagement(ShopCatalogProduct product)
        {
            return new ShopCatalogProduct
            {
                ItemId = (product.ItemId ?? "").Trim(),
                Title = (product.Title ?? "").Trim(),
                Url = (product.Url ?? "").Trim(),
                Status = product.Status ?? "",
                ManuallyAdded = product.ManuallyAdded
            };
        }

        // Save a single atomic local overlay under the current shop's settings
        // scope. A full QN refresh still writes its own raw snapshot; edits and
        // hidden entries are restored on every Load without touching replies.
        internal static void SaveManagedCatalog(string seller,
            IEnumerable<ShopCatalogProduct> requested,
            IEnumerable<ShopCatalogProduct> originalVisible)
        {
            if (string.IsNullOrWhiteSpace(seller))
                throw new ArgumentException("当前店铺身份为空，不能保存商品列表");
            var submitted = (requested ?? Enumerable.Empty<ShopCatalogProduct>())
                .Select(CloneForManagement).ToList();
            var originals = (originalVisible ?? Enumerable.Empty<ShopCatalogProduct>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ItemId))
                .ToDictionary(x => x.ItemId, CloneForManagement, StringComparer.Ordinal);
            if (submitted.Count > 1000)
                throw new InvalidOperationException("最多允许管理1000件商品");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in submitted)
            {
                if (row.ItemId.Length < 6 || row.ItemId.Length > 20 ||
                    !row.ItemId.All(char.IsDigit) || !ids.Add(row.ItemId))
                    throw new InvalidOperationException("商品ID必须为唯一的6~20位数字：" + row.ItemId);
                if (row.Title.Length == 0 || row.Title.Length > 240)
                    throw new InvalidOperationException("请输入商品ID " + row.ItemId + " 的正确名称（最多240字）");
                ShopCatalogProduct baseline;
                bool known = originals.TryGetValue(row.ItemId, out baseline);
                if (!known || !string.Equals(row.Url, baseline.Url, StringComparison.Ordinal))
                    ValidateItemUrl(row.ItemId, row.Url);
                // The item identity never changes in-place; an old row whose
                // ItemId was changed would otherwise orphan its order rules.
                if (!known && originals.Values.Any(x =>
                    string.Equals(x.Title, row.Title, StringComparison.Ordinal)
                    && string.Equals(x.Url, row.Url, StringComparison.Ordinal)))
                    throw new InvalidOperationException("不能修改已有商品ID，请恢复原ID");
            }
            var gate = Gates.GetOrAdd(seller, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
                throw new InvalidOperationException("本店商品同步正在执行，请稍后保存");
            try
            {
                var edits = LoadManagedEdits(seller);
                var overrides = edits.Overrides.Where(x => x != null
                    && !string.IsNullOrWhiteSpace(x.ItemId))
                    .ToDictionary(x => x.ItemId, CloneForManagement, StringComparer.Ordinal);
                var hidden = new HashSet<string>(edits.HiddenItemIds, StringComparer.Ordinal);
                foreach (var id in originals.Keys.Where(id => !ids.Contains(id)))
                {
                    hidden.Add(id);  // hide from Bot, never delete from Taobao
                    overrides.Remove(id);
                }
                foreach (var row in submitted)
                {
                    hidden.Remove(row.ItemId);
                    ShopCatalogProduct baseline;
                    var existing = originals.TryGetValue(row.ItemId, out baseline);
                    if (existing &&
                        string.Equals(baseline.Title, row.Title, StringComparison.Ordinal) &&
                        string.Equals(baseline.Url, row.Url, StringComparison.Ordinal))
                        continue; // automatic fields continue following QN
                    row.ManuallyAdded = !existing || baseline.ManuallyAdded;
                    row.Status = existing ? baseline.Status : "本地新增";
                    overrides[row.ItemId] = row;
                }
                RuleStore(seller).SetString(ManagedProductsKey,
                    JsonConvert.SerializeObject(new ShopCatalogManagedEdits
                    {
                        Overrides = overrides.Values.OrderBy(x => x.ItemId).ToList(),
                        HiddenItemIds = hidden.OrderBy(x => x).ToList()
                    }));
                Log.Info("本店商品列表管理已保存: seller=" + seller
                    + ", visible=" + submitted.Count
                    + ", overrides=" + overrides.Count
                    + ", hidden=" + hidden.Count
                    + ", productReplyRulesUntouched=true");
            }
            finally { gate.Release(); }
        }

        private static void ValidateItemUrl(string itemId, string input)
        {
            Uri uri;
            if (!Uri.TryCreate(input, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !IsTaobaoItemHost(uri.Host))
                throw new InvalidOperationException("商品ID " + itemId + " 的链接必须是淘宝/天猫商品详情页");
            var query = Regex.Match(uri.Query ?? "",
                @"(?:^|[?&])(?:id|item_id|num_iid)=(\d{6,20})(?:&|$)",
                RegexOptions.IgnoreCase);
            if (!query.Success || !string.Equals(query.Groups[1].Value, itemId, StringComparison.Ordinal))
                throw new InvalidOperationException("商品链接中的id必须与商品ID一致：" + itemId);
        }

        private static List<ShopCatalogProduct> LoadManualProducts(string seller)
        {
            string json;
            if (!RuleStore(seller).TryGetString(ManualProductsKey, out json)
                || string.IsNullOrWhiteSpace(json)) return new List<ShopCatalogProduct>();
            return (JsonConvert.DeserializeObject<List<ShopCatalogProduct>>(json)
                ?? new List<ShopCatalogProduct>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ItemId)
                    && x.ItemId.All(char.IsDigit)).ToList();
        }

        internal static string AddManualProduct(string seller, string productUrl, string productTitle)
        {
            productUrl = (productUrl ?? string.Empty).Trim();
            productTitle = (productTitle ?? string.Empty).Trim();
            if (productTitle.Length == 0 || productTitle.Length > 240)
                throw new ArgumentException("请输入长度不超过240字的本店商品名称");
            Uri uri;
            if (!Uri.TryCreate(productUrl, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !IsTaobaoItemHost(uri.Host))
                throw new ArgumentException("请填写淘宝或天猫的商品详情链接");
            var query = Regex.Match(uri.Query ?? string.Empty,
                @"(?:^|[?&])(?:id|item_id|num_iid)=(\d{6,20})(?:&|$)",
                RegexOptions.IgnoreCase);
            if (!query.Success)
                throw new ArgumentException("商品链接中没有有效商品 ID（id 参数）");
            var itemId = query.Groups[1].Value;
            var gate = Gates.GetOrAdd(seller, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0)) throw new InvalidOperationException("正在同步本店商品，请稍后补录");
            try
            {
                var all = LoadManualProducts(seller)
                    .ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
                if (all.Count >= 1000 && !all.ContainsKey(itemId))
                    throw new InvalidOperationException("手动补录商品数量已达到上限");
                all[itemId] = new ShopCatalogProduct
                {
                    ItemId = itemId, Title = productTitle,
                    Url = uri.GetLeftPart(UriPartial.Path) + "?id=" + itemId,
                    Status = "手动补录", ManuallyAdded = true
                };
                RuleStore(seller).SetString(ManualProductsKey,
                    JsonConvert.SerializeObject(all.Values.OrderBy(x => x.ItemId).ToList()));
                Log.Info("本店商品已人工补录: seller=" + seller + ", itemId=" + itemId);
                return itemId;
            }
            finally { gate.Release(); }
        }

        internal static bool RemoveManualProduct(string seller, string itemId)
        {
            itemId = (itemId ?? string.Empty).Trim();
            var gate = Gates.GetOrAdd(seller, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0)) throw new InvalidOperationException("正在同步本店商品，请稍后删除");
            try
            {
                var all = LoadManualProducts(seller);
                var removed = all.RemoveAll(x => x.ItemId == itemId) > 0;
                if (!removed) return false;
                RuleStore(seller).SetString(ManualProductsKey,
                    JsonConvert.SerializeObject(all));
                Log.Info("已删除本店手动补录商品: seller=" + seller
                    + ", itemId=" + itemId);
                return true;
            }
            finally { gate.Release(); }
        }

        private static bool IsTaobaoItemHost(string host)
        {
            host = (host ?? string.Empty).Trim().ToLowerInvariant();
            return host == "item.taobao.com" || host == "detail.tmall.com"
                || host.EndsWith(".detail.tmall.com", StringComparison.Ordinal);
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
                if (!force && !snapshot.Partial && snapshot.UpdatedAtUtc != DateTime.MinValue &&
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
                // Query the real seller merchandise management API and normalize its
                // nested response envelope. The historical cs.item.search endpoint
                // is not a seller-wide item list (and returned no data.data list).
                var fetched = await ShopCatalogMtopReader.FetchAsync(qn.CDP)
                    .ConfigureAwait(false);
                if (fetched.Products == null || fetched.Products.Count == 0)
                    throw new InvalidOperationException("千牛商品接口未返回有效商品，保留旧数据");
                var nowUtc = DateTime.UtcNow;
                var mergedProducts = fetched.Products
                    .ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
                if (fetched.Partial)
                {
                    // Recommendations are not the full seller inventory. Never drop
                    // items that were already in this seller's product cache.
                    foreach (var cached in snapshot.Products.Where(x =>
                        x != null && !string.IsNullOrWhiteSpace(x.ItemId)))
                    {
                        if (!mergedProducts.ContainsKey(cached.ItemId))
                            mergedProducts[cached.ItemId] = cached;
                    }
                }
                var keptFullCatalog = fetched.Partial && !snapshot.Partial
                    && snapshot.Products.Count > 0;
                var updated = new ShopCatalogSnapshot
                {
                    UpdatedAtUtc = keptFullCatalog ? snapshot.UpdatedAtUtc : nowUtc,
                    Products = (keptFullCatalog ? snapshot.Products : mergedProducts.Values.ToList())
                        .OrderBy(x => x.Title, StringComparer.Ordinal)
                        .ThenBy(x => x.ItemId, StringComparer.Ordinal).ToList(),
                    Source = keptFullCatalog ? snapshot.Source : fetched.Source,
                    Partial = keptFullCatalog ? false : fetched.Partial,
                    LastFullCatalogError = fetched.Partial ? fetched.FullCatalogError : string.Empty,
                    LastFullCatalogAttemptUtc = nowUtc
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
                    + ", updatedAtUtc=" + updated.UpdatedAtUtc.ToString("o")
                    + ", source=" + (updated.Source ?? "unknown")
                    + ", partial=" + updated.Partial
                    + ", fullSourceSucceeded=" + (!fetched.Partial)
                    + ", fullSourceError=" + (updated.LastFullCatalogError ?? ""));
                if (fetched.Partial)
                    Log.Info("shop_catalog_partial_only; seller=" + seller
                        + "; returnedByRecommend=" + fetched.Products.Count
                        + "; cachedVisible=" + updated.Products.Count
                        + "; manualReviewRequired=true");
                return updated.Products.Count;
            }
            finally { gate.Release(); }
        }
    }
}
