using DbEntity;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class ShopCatalogFetchResult
    {
        public List<ShopCatalogProduct> Products { get; set; }
        public string Source { get; set; }
        public bool Partial { get; set; }
        public int Pages { get; set; }
        // Sanitized reason why the authoritative full-catalog source failed.
        // Keep this even when the recommendation fallback returned products.
        public string FullCatalogError { get; set; }
    }

    /// <summary>
    /// PC seller goods management has a different response contract from the
    /// buyer-conversation item-search endpoint. Parse its JSON envelope before
    /// assuming the response contains a data.data array.
    /// </summary>
    internal static class ShopCatalogMtopReader
    {
        private const int PageSize = 20;
        private const int MaxPages = 50;
        private const string SellerManageApi = "mtop.tmall.sell.pc.manage.async";
        private const string SellerRecommendApi = "mtop.taobao.qianniu.cs.item.recommend.query";

        internal static async Task<ShopCatalogFetchResult> FetchAsync(CDPClient cdp)
        {
            var errors = new List<string>();
            if (cdp == null || cdp.IsInvalidated)
                throw new InvalidOperationException("千牛商品读取会话已失效");

            try
            {
                return await ReadSellerManageAsync(cdp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var fullError = "卖家商品管理:" + Short(ex.Message, 200);
                errors.Add(fullError);
                Log.Info("shop_catalog_full_source_failed; api=" + SellerManageApi
                    + "; reason=" + fullError
                    + "; fallback=partial_recommendations_only");
                if (cdp.IsInvalidated)
                    throw new InvalidOperationException("千牛会话已失效，停止商品查询；" + string.Join("; ", errors));
            }

            // Fallback is a seller-side recommendation list, NOT an authoritative
            // full inventory. Clearly mark it as partial, never silently claim
            // a complete seven-day store catalog from these items.
            try
            {
                var response = await cdp.InvokeMTop<JToken>(
                    SellerRecommendApi, new { }).ConfigureAwait(false);
                var page = ParsePage(response);
                if (page.Products.Count > 0)
                {
                    return new ShopCatalogFetchResult
                    {
                        Products = page.Products,
                        Source = SellerRecommendApi,
                        Partial = true,
                        Pages = 1,
                        FullCatalogError = string.Join("; ", errors)
                    };
                }
                errors.Add("推荐商品:" + (page.Error ?? "未返回商品"));
            }
            catch (Exception ex)
            {
                errors.Add("推荐商品:" + Short(ex.Message, 160));
            }
            throw new InvalidOperationException(
                "千牛未返回有效本店商品列表（已尝试卖家商品管理与推荐商品接口）："
                + string.Join("; ", errors));
        }

        private static async Task<ShopCatalogFetchResult> ReadSellerManageAsync(CDPClient cdp)
        {
            var products = new Dictionary<string, ShopCatalogProduct>(StringComparer.Ordinal);
            var pages = 0;
            for (var pageNo = 1; pageNo <= MaxPages; pageNo++)
            {
                if (cdp.IsInvalidated) throw new InvalidOperationException("千牛会话已断开");
                var payload = new
                {
                    url = "/tmall/manager/table.htm?tab=all",
                    jsonBody = JsonConvert.SerializeObject(new
                    {
                        tab = "all",
                        pagination = new { current = pageNo, pageSize = PageSize },
                        filter = new { },
                        table = new { }
                    })
                };
                var raw = await cdp.InvokeMTop<JToken>(SellerManageApi, payload).ConfigureAwait(false);
                var parsed = ParsePage(raw);
                if (!string.IsNullOrWhiteSpace(parsed.Error))
                    throw new InvalidOperationException("第" + pageNo + "页 " + parsed.Error);
                pages++;
                if (pageNo == 1 && parsed.Products.Count == 0)
                    throw new InvalidOperationException("第1页无有效商品；响应结构=" + parsed.Shape
                        + "; rootItems=" + parsed.ProductCandidateCount
                        + "; unreadableItems=" + parsed.UnreadableProductCount
                        + "; collectionKeys=" + parsed.CollectionKeys);
                if (pageNo > 1 && parsed.Products.Count == 0) break;

                var newIds = 0;
                foreach (var item in parsed.Products)
                {
                    if (!products.ContainsKey(item.ItemId)) newIds++;
                    products[item.ItemId] = item;
                }
                if (pageNo > 1 && newIds == 0)
                    throw new InvalidOperationException("第" + pageNo + "页与前页重复，千牛未按分页返回数据");
                if (parsed.HasMore == false || (parsed.Total > 0 && products.Count >= parsed.Total))
                    break;
                if (pageNo == MaxPages)
                    throw new InvalidOperationException("商品超过同步分页上限，拒绝将不完整结果标记为全店商品");
            }
            if (products.Count == 0)
                throw new InvalidOperationException("卖家商品管理接口返回的商品为空");
            return new ShopCatalogFetchResult
            {
                Products = products.Values.ToList(),
                Source = SellerManageApi,
                Partial = false,
                Pages = pages
            };
        }

        private sealed class CatalogPage
        {
            public List<ShopCatalogProduct> Products = new List<ShopCatalogProduct>();
            public int Total;
            public bool? HasMore;
            public string Error;
            public string Shape;
            public int ProductCandidateCount;
            public int UnreadableProductCount;
            public string CollectionKeys;
        }

        internal static ShopCatalogFetchResult ParseSampleForTest(string raw)
        {
            var page = ParsePage(JToken.Parse(raw));
            return new ShopCatalogFetchResult
            {
                Products = page.Products,
                Source = page.Shape,
                Partial = false,
                Pages = 1
            };
        }

        private static CatalogPage ParsePage(JToken response)
        {
            var page = new CatalogPage();
            var root = Expand(response);
            if (root == null)
            {
                page.Error = "响应为空";
                page.Shape = "null";
                return page;
            }
            page.Shape = RootShape(root);
            var ret = Read(root, "ret");
            if (ret is JArray && ((JArray)ret).Any(x =>
                x != null && x.ToString().StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)))
            {
                page.Error = "接口拒绝:" + Short(string.Join(",", ((JArray)ret)
                    .Select(x => x == null ? "" : x.ToString())), 110);
                return page;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Walk(root))
            {
                var obj = node as JObject;
                if (obj == null) continue;
                var itemIdCandidate = Read(obj, "itemId", "item_id", "numIid", "num_iid",
                    "auctionId", "itemNumId");
                if (itemIdCandidate != null)
                {
                    page.ProductCandidateCount++;
                    var productCandidate = ToProduct(obj);
                    if (productCandidate == null) page.UnreadableProductCount++;
                    else if (seen.Add(productCandidate.ItemId)) page.Products.Add(productCandidate);
                }
                // If the API structure changed, capture keys, not product data,
                // buyer identifiers, response bodies or credentials.
                foreach (var prop in obj.Properties())
                {
                    if ((prop.Value is JArray || prop.Value is JObject)
                        && candidateKeys.Count < 20
                        && new[] { "items", "list", "records", "result", "data", "rows",
                            "products", "auctions", "table", "pagination", "page" }
                            .Any(k => prop.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                        candidateKeys.Add(prop.Name);
                }

                var pagination = node as JObject;
                if (pagination == null) continue;
                var nestedPagination = Read(pagination, "pagination", "page");
                if (nestedPagination is JObject)
                {
                    int count;
                    if (TryInt(Read(nestedPagination, "total", "totalCount", "totalElements"), out count)
                        && count > page.Total) page.Total = count;
                    bool more;
                    if (TryBool(Read(nestedPagination, "hasMore", "hasNext", "hasNextPage"), out more))
                        page.HasMore = more;
                }
                else if (node == root || Read(pagination, "data") != null)
                {
                    int count;
                    if (TryInt(Read(pagination, "totalCount", "totalElements", "total"), out count)
                        && count > page.Total) page.Total = count;
                    bool more;
                    if (TryBool(Read(pagination, "hasMore", "hasNext", "hasNextPage"), out more))
                        page.HasMore = more;
                }
            }
            page.CollectionKeys = string.Join(",", candidateKeys.OrderBy(x => x, StringComparer.Ordinal).Take(20));
            return page;
        }

        private static string RootShape(JToken root)
        {
            var obj = root as JObject;
            if (obj == null) return root.Type.ToString();
            return string.Join(",", obj.Properties().Take(12).Select(x => x.Name));
        }

        private static IEnumerable<JToken> Walk(JToken root)
        {
            var queue = new Queue<Tuple<JToken, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            var count = 0;
            while (queue.Count > 0 && count++ < 8000)
            {
                var next = queue.Dequeue();
                var token = Expand(next.Item1);
                if (token == null || next.Item2 > 14) continue;
                yield return token;
                if (token is JObject)
                {
                    foreach (var prop in ((JObject)token).Properties())
                        queue.Enqueue(Tuple.Create(prop.Value, next.Item2 + 1));
                }
                else if (token is JArray)
                {
                    foreach (var value in ((JArray)token).Take(2000))
                        queue.Enqueue(Tuple.Create(value, next.Item2 + 1));
                }
            }
        }

        private static JToken Expand(JToken token)
        {
            for (var i = 0; i < 3 && token is JValue && token.Type == JTokenType.String; i++)
            {
                var text = (token.ToString() ?? "").Trim();
                if (!(text.StartsWith("{", StringComparison.Ordinal) ||
                    text.StartsWith("[", StringComparison.Ordinal))) break;
                try { token = JToken.Parse(text); }
                catch { break; }
            }
            return token;
        }

        private static JToken Read(JToken value, params string[] names)
        {
            var obj = value as JObject;
            if (obj == null) return null;
            foreach (var name in names)
            {
                var prop = obj.Properties().FirstOrDefault(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (prop != null) return prop.Value;
            }
            return null;
        }

        private static ShopCatalogProduct ToProduct(JObject value)
        {
            if (value == null) return null;
            var id = Value(Read(value, "itemId", "item_id", "numIid", "num_iid",
                "auctionId", "itemNumId", "id"));
            var title = Value(Read(value, "title", "itemTitle", "auctionTitle",
                "itemName", "item_name"));
            // Never mistake category, buyer, or order IDs for catalog product IDs.
            if (id.Length < 6 || id.Length > 20 || !id.All(char.IsDigit) ||
                string.IsNullOrWhiteSpace(title)) return null;
            return new ShopCatalogProduct
            {
                ItemId = id,
                Title = Short(title, 260),
                Url = Short(Value(Read(value, "itemUrl", "auctionUrl", "url", "link")), 800),
                Status = Short(Value(Read(value, "approveStatus", "itemStatus", "status")), 80)
            };
        }

        private static string Value(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ||
                token.Type == JTokenType.Object || token.Type == JTokenType.Array
                ? "" : token.ToString().Trim();
        }

        private static bool TryInt(JToken token, out int number)
        {
            return int.TryParse(Value(token), out number) && number >= 0;
        }

        private static bool TryBool(JToken token, out bool value)
        {
            return bool.TryParse(Value(token), out value);
        }

        private static string Short(string text, int max)
        {
            text = (text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length > max ? text.Substring(0, max) : text;
        }
    }
}
