using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class VisualImageKnowledgeEntry
    {
        [PrimaryKey] public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string ImageSha256 { get; set; }
        public string ImagePath { get; set; }
        public string ImageName { get; set; }
        public string OcrText { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
        public bool SyncDirty { get; set; }
        public long CloudRevision { get; set; }
    }

    internal sealed class VisualImageKnowledgeSyncTombstone
    {
        [PrimaryKey] public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public long DeletedAtTicks { get; set; }
        public bool SyncDirty { get; set; }
        public long CloudRevision { get; set; }
    }

    internal sealed class VisualImageKnowledgeSyncState
    {
        [PrimaryKey] public string ShopKey { get; set; }
        public string Seller { get; set; }
        public long LastCloudRevision { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualImageKnowledgeMatch
    {
        public string KnowledgeId { get; set; }
        public string Description { get; set; }
        public string OcrText { get; set; }
        public string ImageSha256 { get; set; }
        public double Score { get; set; }
        public string ContextText { get; set; }
    }

    /// <summary>
    /// Shop-scoped image knowledge curated by the operator. OCR text is editable and
    /// used as a matching fingerprint; the description is contextual evidence only,
    /// never a standalone reply or proof that the buyer fulfilled a prior instruction.
    /// </summary>
    internal static class VisualImageKnowledgeService
    {
        public const int MaxImageBytes = 8 * 1024 * 1024;
        public const int MaxOcrChars = 6000;
        public const int MaxDescriptionChars = 1200;
        public const int MaxEntriesPerShop = 500;
        public const double MatchThreshold = 0.78d;

        private static readonly object Sync = new object();
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static int _schemaReady;

        public static List<VisualImageKnowledgeEntry> Load(string shopKey, string seller)
        {
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            EnsureSchema();
            var imageRoot = GetImageDirectory(shop);
            lock (Sync)
            {
                return Select("where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc limit "
                    + MaxEntriesPerShop, shop.ShopKey, seller)
                    .Select(item =>
                    {
                        if (!IsManagedImagePath(item.ImagePath, imageRoot))
                            item.ImagePath = string.Empty;
                        return item;
                    })
                    .ToList();
            }
        }

        public static VisualImageKnowledgeEntry Save(
            string shopKey,
            string seller,
            VisualImageKnowledgeEntry entry,
            string sourceImagePath)
        {
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            if (entry == null) throw new ArgumentNullException("entry");
            sourceImagePath = (sourceImagePath ?? string.Empty).Trim();
            if (sourceImagePath.Length == 0 || !File.Exists(sourceImagePath))
                throw new InvalidOperationException("请先选择一张有效图片。");

            var info = new FileInfo(sourceImagePath);
            if (info.Length <= 0 || info.Length > MaxImageBytes)
                throw new InvalidOperationException("图片大小必须在 1 字节至 8 MB 之间。");

            var extension = SafeExtension(sourceImagePath);
            var sha256 = ComputeSha256(sourceImagePath);
            if (!string.IsNullOrWhiteSpace(entry.ImageSha256)
                && !string.Equals(entry.ImageSha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("图片文件已变化，请重新识别后再保存，避免 OCR 与图片不一致。");

            entry.EntityId = Clean(entry.EntityId, 80);
            entry.EntityId = entry.EntityId.Length == 0 ? Guid.NewGuid().ToString("N") : entry.EntityId;
            entry.ShopKey = shop.ShopKey;
            entry.Seller = seller;
            entry.ImageSha256 = sha256;
            entry.ImageName = Clean(entry.ImageName, 180);
            if (entry.ImageName.Length == 0) entry.ImageName = Path.GetFileName(sourceImagePath);
            entry.OcrText = Clean(entry.OcrText, MaxOcrChars);
            entry.Description = Clean(entry.Description, MaxDescriptionChars);
            entry.Enabled = true;
            if (entry.Description.Length == 0)
                throw new InvalidOperationException("请填写图片说明后再保存。");

            EnsureSchema();
            var imageRoot = GetImageDirectory(shop);
            Directory.CreateDirectory(imageRoot);
            var targetPath = Path.Combine(imageRoot, entry.EntityId + "-" + sha256.Substring(0, 12) + extension);
            VisualImageKnowledgeEntry old;
            lock (Sync)
            {
                old = Select("where EntityId = ? and ShopKey = ? and Seller = ? limit 1",
                    entry.EntityId, shop.ShopKey, seller).FirstOrDefault();
                var duplicate = Select("where ShopKey = ? and Seller = ? and ImageSha256 = ? and EntityId <> ? limit 1",
                    shop.ShopKey, seller, sha256, entry.EntityId).FirstOrDefault();
                if (duplicate != null)
                    throw new InvalidOperationException("当前店铺已存在这张图片，请在左侧列表中编辑已有图片知识。");
            }

            var createdImage = false;
            if (!SamePath(sourceImagePath, targetPath) && !File.Exists(targetPath))
            {
                var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(sourceImagePath, tempPath, false);
                    File.Move(tempPath, targetPath);
                    createdImage = true;
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
            else if (!File.Exists(targetPath))
            {
                throw new IOException("图片文件无法保存到当前店铺的知识目录。");
            }

            var now = DateTime.Now.Ticks;
            entry.ImagePath = targetPath;
            entry.CreatedAtTicks = old == null ? now : old.CreatedAtTicks;
            entry.UpdatedAtTicks = now;
            entry.CloudRevision = old == null ? entry.CloudRevision : old.CloudRevision;
            entry.SyncDirty = true;
            try
            {
                lock (Sync)
                {
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { entry });
                    DbHelper.Db.Execute("delete from VisualImageKnowledgeSyncTombstone where EntityId = ? and ShopKey = ? and Seller = ?",
                        entry.EntityId, shop.ShopKey, seller);
                }
            }
            catch
            {
                if (createdImage) TryDeleteManagedImage(targetPath, imageRoot);
                throw;
            }

            if (old != null && !SamePath(old.ImagePath, targetPath))
                TryDeleteManagedImage(old.ImagePath, imageRoot);

            Log.Info("图片知识已保存: shopRef="
                + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                + ", knowledgeId=" + entry.EntityId
                + ", ocrChars=" + entry.OcrText.Length);
            return entry;
        }

        public static bool Delete(string shopKey, string seller, string id)
        {
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            id = Clean(id, 80);
            if (id.Length == 0) return false;
            EnsureSchema();
            var imageRoot = GetImageDirectory(shop);
            VisualImageKnowledgeEntry item;
            lock (Sync)
            {
                item = Select("where EntityId = ? and ShopKey = ? and Seller = ? limit 1",
                    id, shop.ShopKey, seller).FirstOrDefault();
                if (item == null) return false;
                DbHelper.Db.Execute(
                    "insert or replace into VisualImageKnowledgeSyncTombstone(EntityId,ShopKey,Seller,DeletedAtTicks,SyncDirty,CloudRevision) values(?,?,?,?,1,?)",
                    item.EntityId, shop.ShopKey, seller, DateTime.Now.Ticks, item.CloudRevision);
                DbHelper.Db.Execute(
                    "delete from VisualImageKnowledgeEntry where EntityId = ? and ShopKey = ? and Seller = ?",
                    id, shop.ShopKey, seller);
            }
            TryDeleteManagedImage(item.ImagePath, imageRoot);
            Log.Info("图片知识已删除: shopRef="
                + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey) + ", knowledgeId=" + id);
            return true;
        }

        /// <summary>
        /// Build one bounded delta request. Images are sent one at a time so the normal
        /// three-second status/message sync cannot accidentally create multi-megabyte bursts.
        /// </summary>
        public static JObject BuildCloudSyncRequest(string shopKey, string seller)
        {
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            EnsureSchema();
            lock (Sync)
            {
                var state = SelectSyncState(shop.ShopKey, seller);
                var pending = Select("where ShopKey = ? and Seller = ? and SyncDirty = 1 order by UpdatedAtTicks asc limit 1",
                    shop.ShopKey, seller).FirstOrDefault();
                var tombstones = SelectTombstones("where ShopKey = ? and Seller = ? and SyncDirty = 1 order by DeletedAtTicks asc limit 50",
                    shop.ShopKey, seller);
                var upserts = new JArray();
                if (pending != null)
                {
                    if (!IsManagedImagePath(pending.ImagePath, GetImageDirectory(shop)) || !File.Exists(pending.ImagePath))
                        throw new IOException("图片知识原图缺失，已保留待同步状态：" + pending.EntityId);
                    var bytes = File.ReadAllBytes(pending.ImagePath);
                    if (bytes.Length <= 0 || bytes.Length > MaxImageBytes)
                        throw new InvalidOperationException("图片知识原图超过同步大小限制。");
                    var digest = ComputeSha256(bytes);
                    if (!string.Equals(digest, pending.ImageSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("图片知识原图哈希不匹配，拒绝同步损坏数据。");
                    upserts.Add(new JObject
                    {
                        ["id"] = pending.EntityId,
                        ["base_revision"] = pending.CloudRevision,
                        ["image_name"] = pending.ImageName ?? Path.GetFileName(pending.ImagePath),
                        ["mime_type"] = VisionImageResolver.DetectMime(bytes),
                        ["image_sha256"] = digest,
                        ["image_base64"] = Convert.ToBase64String(bytes),
                        ["ocr_text"] = pending.OcrText ?? string.Empty,
                        ["description"] = pending.Description ?? string.Empty,
                        ["enabled"] = pending.Enabled,
                        ["updated_at"] = ToUtcIso(pending.UpdatedAtTicks)
                    });
                }

                var deletes = new JArray(tombstones.Select(x => new JObject
                {
                    ["id"] = x.EntityId,
                    ["base_revision"] = x.CloudRevision,
                    ["deleted_at"] = ToUtcIso(x.DeletedAtTicks)
                }));
                return new JObject
                {
                    ["shop_key"] = shop.ShopKey,
                    ["seller"] = seller,
                    ["since_revision"] = state == null ? 0 : state.LastCloudRevision,
                    ["upserts"] = upserts,
                    ["deletes"] = deletes
                };
            }
        }

        /// <summary>
        /// Apply a server delta/conflict snapshot only after all files have been verified.
        /// Each image is written through a temporary file inside the current shop's managed
        /// directory; Save() then applies the same validation and atomic file ownership rules
        /// as a locally uploaded image.
        /// </summary>
        public static void ApplyCloudSyncResponse(string shopKey, string seller, JObject response)
        {
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (!string.Equals((string)response["shop_key"], shop.ShopKey, StringComparison.Ordinal))
                throw new InvalidOperationException("云端图片知识响应的ShopKey不匹配。");
            EnsureSchema();

            var upserts = response["upserts"] as JArray ?? new JArray();
            var conflicts = response["conflicts"] as JArray ?? new JArray();
            foreach (var item in upserts.Concat(conflicts).OfType<JObject>())
            {
                var kind = (string)item["kind"] ?? "upsert";
                var data = item["item"] as JObject ?? item;
                if (string.Equals(kind, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyCloudDelete(shop, seller, data);
                    continue;
                }
                ApplyCloudUpsert(shop, seller, data);
            }
            var deletes = response["deletes"] as JArray ?? new JArray();
            foreach (var item in deletes.OfType<JObject>())
                ApplyCloudDelete(shop, seller, item);

            var revision = response.Value<long?>("revision") ?? 0;
            lock (Sync)
            {
                DbHelper.Db.Execute(
                    "insert or replace into VisualImageKnowledgeSyncState(ShopKey,Seller,LastCloudRevision,UpdatedAtTicks) values(?,?,?,?)",
                    shop.ShopKey, seller, revision, DateTime.Now.Ticks);
            }
        }

        private static void ApplyCloudUpsert(ShopContext shop, string seller, JObject data)
        {
            var id = Clean((string)data["id"], 80);
            var base64 = (string)data["image_base64"] ?? string.Empty;
            if (id.Length == 0 || base64.Length == 0) throw new InvalidOperationException("云端图片知识缺少图片数据。");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch { throw new InvalidOperationException("云端图片知识Base64无效。"); }
            if (bytes.Length <= 0 || bytes.Length > MaxImageBytes)
                throw new InvalidOperationException("云端图片知识大小无效。");
            var sha = ComputeSha256(bytes);
            if (!string.Equals(sha, Clean((string)data["image_sha256"], 64), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("云端图片知识SHA-256校验失败。");
            var mime = VisionImageResolver.DetectMime(bytes);
            if (!string.Equals(mime, Clean((string)data["mime_type"], 40), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("云端图片知识MIME与文件内容不匹配。");

            var root = GetImageDirectory(shop);
            Directory.CreateDirectory(root);
            var temp = Path.Combine(root, ".sync-" + Guid.NewGuid().ToString("N") + ExtensionForMime(mime));
            try
            {
                File.WriteAllBytes(temp, bytes);
                var entry = new VisualImageKnowledgeEntry
                {
                    EntityId = id,
                    ShopKey = shop.ShopKey,
                    Seller = seller,
                    ImageName = Clean((string)data["image_name"], 180),
                    ImageSha256 = sha,
                    OcrText = Clean((string)data["ocr_text"], MaxOcrChars),
                    Description = Clean((string)data["description"], MaxDescriptionChars),
                    Enabled = data.Value<bool?>("enabled") ?? true
                };
                Save(shop.ShopKey, seller, entry, temp);
                var revision = data.Value<long?>("revision") ?? 0;
                lock (Sync)
                {
                    DbHelper.Db.Execute(
                        "update VisualImageKnowledgeEntry set SyncDirty=0,CloudRevision=? where EntityId=? and ShopKey=? and Seller=?",
                        revision, id, shop.ShopKey, seller);
                    DbHelper.Db.Execute(
                        "delete from VisualImageKnowledgeSyncTombstone where EntityId=? and ShopKey=? and Seller=?",
                        id, shop.ShopKey, seller);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void ApplyCloudDelete(ShopContext shop, string seller, JObject data)
        {
            var id = Clean((string)data["id"], 80);
            if (id.Length == 0) return;
            var revision = data.Value<long?>("revision") ?? 0;
            var deletedAt = ParseUtcTicks((string)data["deleted_at"]);
            VisualImageKnowledgeEntry old;
            lock (Sync)
            {
                old = Select("where EntityId=? and ShopKey=? and Seller=? limit 1",
                    id, shop.ShopKey, seller).FirstOrDefault();
                if (old != null)
                {
                    DbHelper.Db.Execute("delete from VisualImageKnowledgeEntry where EntityId=? and ShopKey=? and Seller=?",
                        id, shop.ShopKey, seller);
                    TryDeleteManagedImage(old.ImagePath, GetImageDirectory(shop));
                }
                DbHelper.Db.Execute(
                    "insert or replace into VisualImageKnowledgeSyncTombstone(EntityId,ShopKey,Seller,DeletedAtTicks,SyncDirty,CloudRevision) values(?,?,?,?,0,?)",
                    id, shop.ShopKey, seller, deletedAt > 0 ? deletedAt : DateTime.Now.Ticks, revision);
            }
        }

        private static VisualImageKnowledgeSyncState SelectSyncState(string shopKey, string seller)
        {
            return (DbHelper.Db.Select(typeof(VisualImageKnowledgeSyncState),
                "where ShopKey = ? and Seller = ? limit 1", shopKey, seller) ?? new List<object>())
                .OfType<VisualImageKnowledgeSyncState>().FirstOrDefault();
        }

        private static List<VisualImageKnowledgeSyncTombstone> SelectTombstones(string clause, params object[] args)
        {
            return (DbHelper.Db.Select(typeof(VisualImageKnowledgeSyncTombstone), clause, args) ?? new List<object>())
                .OfType<VisualImageKnowledgeSyncTombstone>().ToList();
        }

        private static string ToUtcIso(long ticks)
        {
            try { return new DateTime(ticks, DateTimeKind.Local).ToUniversalTime().ToString("o"); }
            catch { return DateTime.UtcNow.ToString("o"); }
        }

        private static long ParseUtcTicks(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(value ?? string.Empty, out parsed)
                ? parsed.LocalDateTime.Ticks : DateTime.Now.Ticks;
        }

        private static string ExtensionForMime(string mime)
        {
            switch ((mime ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/webp": return ".webp";
                case "image/gif": return ".gif";
                default: throw new InvalidOperationException("不支持的图片MIME类型。");
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }

        public static bool HasEntries(string shopKey)
        {
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            return Load(shop.ShopKey, shop.SellerNick).Count > 0;
        }

        public static bool TryMatch(
            string shopKey,
            string seller,
            string ocrText,
            string imageSha256,
            double ocrConfidence,
            out VisualImageKnowledgeMatch match)
        {
            match = null;
            var shop = ResolveScope(shopKey, seller);
            seller = (shop.SellerNick ?? string.Empty).Trim();
            ocrText = Clean(ocrText, MaxOcrChars);
            imageSha256 = Clean(imageSha256, 64);
            if (ocrText.Length < 4 && imageSha256.Length == 0) return false;

            var entries = Load(shop.ShopKey, seller).Where(x => x != null && x.Enabled).ToList();
            VisualImageKnowledgeEntry best = null;
            var bestScore = 0d;
            foreach (var item in entries)
            {
                var score = !string.IsNullOrWhiteSpace(imageSha256)
                    && string.Equals(item.ImageSha256, imageSha256, StringComparison.OrdinalIgnoreCase)
                    ? 1d
                    : TextSimilarity(ocrText, item.OcrText);
                if (score > bestScore)
                {
                    best = item;
                    bestScore = score;
                }
            }

            if (best == null || bestScore < MatchThreshold
                || (ocrConfidence < 0.55d && bestScore < 0.98d))
                return false;

            var description = Clean(best.Description, MaxDescriptionChars);
            var referenceOcr = Clean(best.OcrText, MaxOcrChars);
            match = new VisualImageKnowledgeMatch
            {
                KnowledgeId = best.EntityId,
                Description = description,
                OcrText = referenceOcr,
                ImageSha256 = best.ImageSha256,
                Score = bestScore,
                ContextText = BuildContext(description, bestScore)
            };
            Log.Info("买家图片命中本店图片知识: shopRef="
                + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                + ", knowledgeId=" + best.EntityId
                + ", score=" + bestScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        private static string BuildContext(string description, double score)
        {
            return "【已命中本店人工维护的图片知识】"
                + "\n图片说明：" + description
                + "\n匹配分：" + score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + "\n这条说明只用于识别图片所属的界面/设备/场景，不是客服回复，也不代表买家已完成此前要求、账号状态或业务结果。"
                + "必须结合当前买家文字以及同一客服、同一买家的前序对话判断图片是否符合要求。"
                + "如果此前客服要求的是某种设备或页面，而图片说明表明买家发来的是另一种设备或页面，应明确指出不一致并请买家重新发送正确界面；不要只复述图片说明。";
        }

        private static double TextSimilarity(string left, string right)
        {
            var aText = Normalize(left);
            var bText = Normalize(right);
            if (aText.Length < 8 || bText.Length < 8) return 0d;
            if (string.Equals(aText, bText, StringComparison.Ordinal)) return 1d;

            var a = Bigrams(aText);
            var b = Bigrams(bText);
            if (a.Count < 5 || b.Count < 5) return 0d;
            var common = a.Intersect(b, StringComparer.Ordinal).Count();
            if (common < 4) return 0d;
            var union = a.Union(b, StringComparer.Ordinal).Count();
            var containment = (double)common / Math.Min(a.Count, b.Count);
            var jaccard = union == 0 ? 0d : (double)common / union;
            return Math.Max(0d, Math.Min(1d, containment * 0.68d + jaccard * 0.32d));
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < value.Length; i++)
                result.Add(value.Substring(i, 2));
            return result;
        }

        private static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            // Account IDs, phone numbers, dates and other per-buyer values vary between
            // screenshots. Ignore numeric/mixed identifiers while retaining their OCR labels.
            value = Regex.Replace(value,
                @"(?<![a-z0-9])(?=[a-z0-9_-]{5,}(?![a-z0-9]))(?=[a-z0-9_-]*\d)[a-z0-9_-]+", " ");
            value = Regex.Replace(value, @"(?<!\d)\d{4,}(?!\d)", " ");
            return Regex.Replace(value, @"[\s\p{P}\p{S}]+", string.Empty);
        }

        private static ShopContext ResolveScope(string shopKey, string seller)
        {
            shopKey = Clean(shopKey, 64);
            seller = Clean(seller, 160);
            if (shopKey.Length == 0)
                throw new InvalidOperationException("图片知识必须绑定明确的ShopKey。");

            // ShopKey is the authoritative identity. Seller may be a UI/display alias;
            // every database row is written with the canonical seller from this profile.
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            if (shop == null
                || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(shop.SellerNick))
                throw new InvalidOperationException("图片知识无法解析权威ShopKey店铺身份，已阻止跨店访问。");
            if (seller.Length > 0
                && !string.Equals(seller, (shop.SellerNick ?? string.Empty).Trim(), StringComparison.Ordinal)
                && !string.Equals(seller, (shop.DisplayName ?? string.Empty).Trim(), StringComparison.Ordinal))
                Log.Info("图片知识请求使用了店铺显示昵称别名，数据仍按权威ShopKey和canonical Seller隔离。");
            return shop;
        }

        private static string GetImageDirectory(ShopContext shop)
        {
            return Path.Combine(Paths.GetKnowledgeRoot(shop), "visual-images");
        }

        private static bool IsManagedImagePath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(path);
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void TryDeleteManagedImage(string path, string root)
        {
            try
            {
                if (IsManagedImagePath(path, root) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理图片知识文件失败: " + ex.Message, 5);
            }
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left ?? string.Empty),
                    Path.GetFullPath(right ?? string.Empty), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SafeExtension(string path)
        {
            var extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (extension == ".jpeg" || extension == ".jpg" || extension == ".png"
                || extension == ".webp" || extension == ".gif")
                return extension;
            throw new InvalidOperationException("仅支持 JPG、PNG、WEBP、GIF 图片。");
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(stream);
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes) result.Append(item.ToString("x2"));
                return result.ToString();
            }
        }

        private static List<VisualImageKnowledgeEntry> Select(string clause, params object[] args)
        {
            return (DbHelper.Db.Select(typeof(VisualImageKnowledgeEntry), clause, args) ?? new List<object>())
                .OfType<VisualImageKnowledgeEntry>().ToList();
        }

        private static void EnsureSchema()
        {
            if (System.Threading.Volatile.Read(ref _schemaReady) != 0) return;
            lock (Sync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute("create table if not exists VisualImageKnowledgeEntry (EntityId text primary key not null,ShopKey text not null,Seller text not null,ImageSha256 text not null,ImagePath text not null,ImageName text not null default '',OcrText text,Description text not null,Enabled integer not null default 1,CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0,SyncDirty integer not null default 1,CloudRevision integer not null default 0)");
                try { DbHelper.Db.Execute("alter table VisualImageKnowledgeEntry add column ImageName text not null default ''"); } catch { }
                try { DbHelper.Db.Execute("alter table VisualImageKnowledgeEntry add column SyncDirty integer not null default 1"); } catch { }
                try { DbHelper.Db.Execute("alter table VisualImageKnowledgeEntry add column CloudRevision integer not null default 0"); } catch { }
                DbHelper.Db.Execute("create table if not exists VisualImageKnowledgeSyncTombstone (EntityId text primary key not null,ShopKey text not null,Seller text not null,DeletedAtTicks integer not null default 0,SyncDirty integer not null default 1,CloudRevision integer not null default 0)");
                DbHelper.Db.Execute("create table if not exists VisualImageKnowledgeSyncState (ShopKey text primary key not null,Seller text not null,LastCloudRevision integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_VisualImageKnowledge_ShopSeller on VisualImageKnowledgeEntry(ShopKey,Seller,Enabled,UpdatedAtTicks)");
                DbHelper.Db.Execute("create unique index if not exists UX_VisualImageKnowledge_ShopSellerImage on VisualImageKnowledgeEntry(ShopKey,Seller,ImageSha256)");
                System.Threading.Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
