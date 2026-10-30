using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class BotReplySourceRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string BuyerHash { get; set; }
        public string ReplyHash { get; set; }
        public string ReplyPreview { get; set; }
        public string Question { get; set; }
        public string Origin { get; set; }
        public string OriginId { get; set; }
        public string CorrectedOrigin { get; set; }
        public string CorrectionReason { get; set; }
        public long PreparedAtTicks { get; set; }
        public long ConfirmedAtTicks { get; set; }
        public long CorrectedAtTicks { get; set; }
        public string ConfirmationMessageKey { get; set; }

        public string EffectiveSource
        {
            get { return string.IsNullOrWhiteSpace(CorrectedOrigin) ? Origin : CorrectedOrigin + "（人工更正，原始：" + (Origin ?? "未记录") + "）"; }
        }
    }

    /// <summary>
    /// Per-shop persistent provenance for Bot replies, separate from user-editable knowledge.
    /// The message echo confirms delivery; prepared-only sources never claim delivery.
    /// Exact answer + shop + buyer matching prevents using another buyer's source.
    /// </summary>
    internal static class BotReplySourceLedger
    {
        private sealed class State
        {
            public readonly object Sync = new object();
            public SQLiteHelper Db;
        }

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, State> States =
            new ConcurrentDictionary<string, State>(StringComparer.Ordinal);

        public static void RecordPrepared(string seller, string buyer, string question, string answer, string origin, string originId = "")
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer) ||
                string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(origin)) return;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                var state = GetState(shop);
                var buyerKey = Hash(shop.ShopKey + "|" + buyer.Trim());
                var replyKey = Hash(BotReplyPresetService.NormalizeReply(answer));
                var now = DateTime.Now;
                lock (state.Sync)
                {
                    var recent = state.Db.ReadRecords<BotReplySourceRow>(null)
                        .Where(x => x.BuyerHash == buyerKey && x.ReplyHash == replyKey
                            && x.PreparedAtTicks >= now.AddMinutes(-2).Ticks
                            && x.ConfirmedAtTicks == 0)
                        .OrderByDescending(x => x.PreparedAtTicks).FirstOrDefault();
                    if (recent == null)
                    {
                        recent = new BotReplySourceRow
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            BuyerHash = buyerKey,
                            ReplyHash = replyKey,
                            PreparedAtTicks = now.Ticks
                        };
                    }
                    recent.ReplyPreview = Redact(answer, 280);
                    recent.Question = Redact(question, 600);
                    recent.Origin = origin.Trim();
                    recent.OriginId = (originId ?? string.Empty).Trim();
                    state.Db.SaveOneRecord(recent);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("持久化 Bot 回复来源失败：" + ex.Message, 10);
            }
        }

        public static BotReplySourceRow ObserveConfirmedEcho(
            string seller, string buyer, string answer, DateTime sentAt, string messageKey, string knownSource)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer) ||
                string.IsNullOrWhiteSpace(answer)) return null;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                var state = GetState(shop);
                var buyerKey = Hash(shop.ShopKey + "|" + buyer.Trim());
                var replyKey = Hash(BotReplyPresetService.NormalizeReply(answer));
                var at = sentAt == DateTime.MinValue ? DateTime.Now : sentAt;
                lock (state.Sync)
                {
                    var rows = state.Db.ReadRecords<BotReplySourceRow>(null)
                        .Where(x => x.BuyerHash == buyerKey && x.ReplyHash == replyKey)
                        .ToList();
                    var existingByEcho = rows.FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(messageKey) &&
                        x.ConfirmationMessageKey == messageKey);
                    if (existingByEcho != null) return existingByEcho;
                    var source = rows.Where(x => x.PreparedAtTicks > 0
                        && Math.Abs(x.PreparedAtTicks - at.Ticks) <= TimeSpan.FromHours(3).Ticks)
                        .OrderBy(x => x.ConfirmedAtTicks > 0 ? 1 : 0)
                        .ThenBy(x => Math.Abs(x.PreparedAtTicks - at.Ticks))
                        .FirstOrDefault();
                    if (source == null)
                    {
                        source = new BotReplySourceRow
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            BuyerHash = buyerKey,
                            ReplyHash = replyKey,
                            PreparedAtTicks = at.Ticks,
                            ReplyPreview = Redact(answer, 280),
                            Origin = string.IsNullOrWhiteSpace(knownSource)
                                ? "来源记录未留存（仅确认千牛发送回显）"
                                : knownSource,
                        };
                    }
                    // Do not rewrite an existing source with a guessed historical
                    // origin. The immutable original and any operator correction
                    // are deliberately distinct.
                    source.ConfirmedAtTicks = at.Ticks;
                    source.ConfirmationMessageKey = (messageKey ?? string.Empty).Trim();
                    state.Db.SaveOneRecord(source);
                    return source;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存 Bot 发送回显来源失败：" + ex.Message, 10);
                return null;
            }
        }

        public static BotReplySourceRow Find(
            string seller, string buyer, string answer, DateTime at)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer) ||
                string.IsNullOrWhiteSpace(answer)) return null;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                var state = GetState(shop);
                var buyerKey = Hash(shop.ShopKey + "|" + buyer.Trim());
                var replyKey = Hash(BotReplyPresetService.NormalizeReply(answer));
                lock (state.Sync)
                {
                    var all = state.Db.ReadRecords<BotReplySourceRow>(null)
                        .Where(x => x.BuyerHash == buyerKey && x.ReplyHash == replyKey)
                        .ToList();
                    if (at != DateTime.MinValue)
                    {
                        // Prevent a later identical reply from lending its origin
                        // to an old message with the same body.
                        all = all.Where(x => Math.Abs(
                            (x.ConfirmedAtTicks > 0 ? x.ConfirmedAtTicks : x.PreparedAtTicks) - at.Ticks)
                            <= TimeSpan.FromHours(3).Ticks).ToList();
                    }
                    return all.OrderByDescending(x => x.ConfirmedAtTicks > 0)
                        .ThenByDescending(x => x.ConfirmedAtTicks > 0 ? x.ConfirmedAtTicks : x.PreparedAtTicks)
                        .FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取 Bot 回复来源失败：" + ex.Message, 10);
                return null;
            }
        }

        public static List<BotReplySourceRow> Search(string seller, string query)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            var state = GetState(shop);
            var term = (query ?? string.Empty).Trim();
            lock (state.Sync)
            {
                var rows = state.Db.ReadRecords<BotReplySourceRow>(null)
                    .OrderByDescending(x => x.ConfirmedAtTicks > 0 ? x.ConfirmedAtTicks : x.PreparedAtTicks);
                if (term.Length > 0)
                    rows = rows.Where(x => (x.ReplyPreview ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || (x.Question ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || (x.Origin ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || (x.CorrectedOrigin ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(x => x.ConfirmedAtTicks > 0 ? x.ConfirmedAtTicks : x.PreparedAtTicks);
                return rows.Take(500).ToList();
            }
        }

        public static void CorrectSource(
            string seller, string rowId, string corrected, string reason)
        {
            if (string.IsNullOrWhiteSpace(rowId)) throw new ArgumentException("必须选择消息记录。");
            corrected = (corrected ?? string.Empty).Trim();
            reason = (reason ?? string.Empty).Trim();
            if (corrected.Length == 0 || corrected.Length > 200 ||
                reason.Length < 4 || reason.Length > 500)
                throw new InvalidOperationException("更正后的来源不能为空，且必须填写至少 4 字的修改原因。");
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            var state = GetState(shop);
            lock (state.Sync)
            {
                var row = state.Db.ReadOneRecord<BotReplySourceRow>(x => x.Id == rowId);
                if (row == null) throw new InvalidOperationException("没有找到该店铺的消息来源记录。");
                row.CorrectedOrigin = corrected;
                row.CorrectionReason = reason;
                row.CorrectedAtTicks = DateTime.Now.Ticks;
                state.Db.SaveOneRecord(row);
            }
        }

        public static string Redact(string value, int maxLength)
        {
            var text = (value ?? string.Empty).Trim();
            text = Regex.Replace(text, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            text = Regex.Replace(text, @"(?<!\d)\d{15,19}(?!\d)", "[敏感编号]");
            text = Regex.Replace(text, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            text = Regex.Replace(text, @"https?://\S+", "[链接]");
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
        }

        private static State GetState(ShopContext shop)
        {
            return States.GetOrAdd(shop.ShopKey, key =>
            {
                var root = Paths.GetStateRoot(shop);
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "bot-reply-sources.db");
                return new State
                {
                    Db = new SQLiteHelper(path, new List<Type> { typeof(BotReplySourceRow) })
                };
            });
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
