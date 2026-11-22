using Bot.ChatRecord;
using Bot.Common;
using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Learns the final human-agent answer for a recently received buyer image. Human intervention
    /// still cancels the outgoing Bot reply, but it must not cancel vision understanding or waste the
    /// correction supplied by the agent. Consecutive short fragments are combined into one reusable
    /// visual answer and attached to the privacy-safe visual semantics, never to raw image bytes.
    /// </summary>
    internal static class ManualVisualReplyLearningService
    {
        private sealed class ReplyFragment
        {
            public string Text;
            public DateTime At;
        }

        private sealed class PendingReply
        {
            public readonly object Sync = new object();
            public string ShopKey;
            public string Seller;
            public string Buyer;
            public string MessageKey;
            public DateTime ImageObservedAt;
            public readonly List<ReplyFragment> Fragments = new List<ReplyFragment>();
            public int Version;
            public DateTime LastReplyAt;
        }

        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, PendingReply> Pending =
            new ConcurrentDictionary<string, PendingReply>(StringComparer.Ordinal);
        private static Timer _attachTimer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            AttachExisting();
            _attachTimer = new Timer(_ => AttachExisting(), null, 250, 500);
            Log.Info("人工视觉回复即时学习已启动：客服接管后图片继续识别，并把最终人工判断用于相似图片。");
        }

        private static void AttachExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    lock (AttachSync)
                    {
                        object marker;
                        if (Attached.TryGetValue(qn, out marker)) continue;
                        var captured = qn;
                        captured.EvRecieveNewMessage += (s, e) => OnRawMessages(captured, e);
                        Attached.Add(qn, new object());
                    }
                }
                CleanupPending();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装人工视觉回复学习监听失败：" + ex.Message, 10);
            }
        }

        private static void OnRawMessages(QN qn, RecieveNewMessageEventArgs e)
        {
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                foreach (var message in response.result
                    .Where(x => x != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue))
                {
                    ObserveSellerReply(qn, message);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("解析人工视觉回复学习消息失败：" + ex.Message, 10);
            }
        }

        private static void ObserveSellerReply(QN qn, QNChatMessage message)
        {
            if (qn == null || message == null) return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch { return; }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            using (ShopSettingsScope.Enter(shop))
                ObserveSellerReplyScoped(qn, message, shop.ShopKey);
        }

        private static void ObserveSellerReplyScoped(QN qn, QNChatMessage message, string shopKey)
        {
            if (message == null || message.fromid == null || message.toid == null) return;
            var shop = ShopSettingsScope.Current;
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(shop.SellerNick)
                || !DirectOrderIdentityResolver.IsMessageFromShop(message, shop)) return;
            var seller = shop.SellerNick.Trim();
            var from = (message.fromid.nick ?? string.Empty).Trim();
            var buyer = (message.toid.nick ?? string.Empty).Trim();
            if (!DirectOrderIdentityResolver.IdentityEquals(from, seller)
                || DirectOrderIdentityResolver.IdentityEquals(buyer, seller)) return;
            BuyerIdentityAliasService.ObserveMessage(seller, message);
            var canonicalBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (!string.IsNullOrWhiteSpace(canonicalBuyer)) buyer = canonicalBuyer;

            var text = ExtractMessageText(message);
            if (string.IsNullOrWhiteSpace(text)
                || ConversationContextStore.IsWithdrawalNotice(message, text)
                || ConversationContextStore.IsPlatformSystemTip(message, text)
                || IsBotReply(seller, buyer, text))
            {
                return;
            }

            VisionCachedImageReference recent;
            if (!VisionImageCacheService.TryGetRecentReference(
                seller,
                buyer,
                TimeSpan.FromMinutes(5),
                out recent,
                shopKey)
                || recent == null
                || recent.Message == null
                || string.IsNullOrWhiteSpace(recent.MessageKey))
            {
                // No recent image: the existing ConversationSessionLearningService will learn this
                // as an ordinary text/manual-reply session instead.
                return;
            }

            var replyAt = GetMessageTime(message);
            if (replyAt == DateTime.MinValue) replyAt = DateTime.Now;
            if (replyAt < recent.ObservedAt.AddSeconds(-2) || replyAt > recent.ObservedAt.AddMinutes(10)) return;

            var stateKey = Key(shopKey, seller, buyer, recent.MessageKey);
            var pending = Pending.GetOrAdd(stateKey, _ => new PendingReply
            {
                ShopKey = shopKey,
                Seller = seller,
                Buyer = buyer,
                MessageKey = recent.MessageKey,
                ImageObservedAt = recent.ObservedAt
            });

            int version;
            lock (pending.Sync)
            {
                var clean = CleanHumanReply(text, 500);
                if (clean.Length == 0) return;
                if (!pending.Fragments.Any(x => Normalize(x.Text) == Normalize(clean)))
                {
                    pending.Fragments.Add(new ReplyFragment { Text = clean, At = replyAt });
                    if (pending.Fragments.Count > 8) pending.Fragments.RemoveRange(0, pending.Fragments.Count - 8);
                }
                pending.LastReplyAt = DateTime.Now;
                pending.Version++;
                version = pending.Version;
            }

            Task.Run(() => LearnWhenReadyAsync(stateKey, pending, version));
        }

        private static async Task LearnWhenReadyAsync(string stateKey, PendingReply pending, int capturedVersion)
        {
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(pending.ShopKey);
                if (shop == null || !string.Equals(shop.ShopKey, pending.ShopKey, StringComparison.Ordinal)) return;
                using (ShopSettingsScope.Enter(shop))
                {
                    await Task.Delay(3500);
                    if (!IsCurrent(pending, capturedVersion)) return;

                // Human replies often arrive before the visual API has finished. Retry the semantic
                // observation lookup without restarting or cancelling the existing vision request.
                VisualKnowledgeObservationEntity observation = null;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (!IsCurrent(pending, capturedVersion)) return;
                    observation = LoadObservation(pending);
                    if (observation != null && !string.IsNullOrWhiteSpace(observation.VisualSummary)) break;
                    await Task.Delay(2000);
                }

                if (observation == null || string.IsNullOrWhiteSpace(observation.VisualSummary))
                {
                    Log.Info("人工视觉回复等待两分钟仍未取得图片语义，保留普通文本接待学习: seller="
                        + pending.Seller + ", buyer=" + pending.Buyer);
                    return;
                }

                var answer = BuildCombinedAnswer(pending);
                if (!IsReusableHumanAnswer(answer))
                {
                    Log.Info("人工视觉回复仅为等待/寒暄，不写入视觉知识: seller="
                        + pending.Seller + ", buyer=" + pending.Buyer + ", answer=" + Short(answer, 120));
                    return;
                }

                var knowledge = UpsertVisualKnowledge(observation, answer);
                if (knowledge == null) return;
                Log.Info("已从人工客服回复即时学习视觉判断: seller=" + pending.Seller
                    + ", buyer=" + pending.Buyer
                    + ", knowledgeId=" + knowledge.EntityId
                    + ", answer=" + Short(answer, 180));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("人工视觉回复即时学习失败：" + ex.Message, 20);
            }
            finally
            {
                PendingReply current;
                if (Pending.TryGetValue(stateKey, out current)
                    && ReferenceEquals(current, pending)
                    && IsCurrent(pending, capturedVersion))
                {
                    Pending.TryRemove(stateKey, out current);
                }
            }
        }

        private static VisualKnowledgeObservationEntity LoadObservation(PendingReply pending)
        {
            try
            {
                var byMessage = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where ShopKey = ? and Seller = ? and MessageKey = ? order by UpdatedAtTicks desc limit 1",
                    pending.ShopKey,
                    pending.Seller,
                    pending.MessageKey) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .FirstOrDefault();
                if (byMessage != null && SameBuyer(pending.Seller, byMessage.Buyer, pending.Buyer)) return byMessage;

                var since = pending.ImageObservedAt.AddMinutes(-1).Ticks;
                var until = pending.ImageObservedAt.AddMinutes(5).Ticks;
                return (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where ShopKey = ? and Seller = ? and ObservedAtTicks >= ? and ObservedAtTicks <= ? order by ObservedAtTicks desc limit 12",
                    pending.ShopKey,
                    pending.Seller,
                    since,
                    until) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .FirstOrDefault(x => x != null && SameBuyer(pending.Seller, x.Buyer, pending.Buyer));
            }
            catch
            {
                return null;
            }
        }

        private static VisualKnowledgeEntryEntity UpsertVisualKnowledge(
            VisualKnowledgeObservationEntity observation,
            string answer)
        {
            try
            {
                VisualKnowledgeEntryEntity target = null;
                var entries = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeEntryEntity),
                    "where ShopKey = ? and Seller = ? and Enabled = 1 order by UpdatedAtTicks desc limit 500",
                    observation.ShopKey,
                    observation.Seller) ?? new List<object>())
                    .OfType<VisualKnowledgeEntryEntity>()
                    .ToList();

                if (!string.IsNullOrWhiteSpace(observation.LearnedKnowledgeId))
                {
                    target = entries.FirstOrDefault(x => x.EntityId == observation.LearnedKnowledgeId);
                }
                if (target == null)
                {
                    target = entries
                        .Select(x => new { Entry = x, Score = Similarity(observation, x) })
                        .Where(x => x.Score >= 0.70)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Entry)
                        .FirstOrDefault();
                }

                var now = DateTime.Now.Ticks;
                if (target == null)
                {
                    target = new VisualKnowledgeEntryEntity
                    {
                        EntityId = Guid.NewGuid().ToString("N"),
                        ShopKey = observation.ShopKey,
                        Seller = observation.Seller,
                        VisualQuestion = observation.VisualQuestion,
                        VisualSummary = observation.VisualSummary,
                        VisualTags = MergeTags(observation.VisualTags, ExtractManualTags(answer)),
                        Answer = answer,
                        SourceType = "视觉人工即时学习",
                        Confirmations = 1,
                        Enabled = true,
                        CreatedAtTicks = now,
                        UpdatedAtTicks = now
                    };
                }
                else
                {
                    target.VisualQuestion = PreferLonger(target.VisualQuestion, observation.VisualQuestion);
                    target.VisualSummary = PreferLonger(target.VisualSummary, observation.VisualSummary);
                    target.VisualTags = MergeTags(target.VisualTags,
                        MergeTags(observation.VisualTags, ExtractManualTags(answer)));
                    target.Answer = answer;
                    target.SourceType = "视觉人工即时学习";
                    target.Confirmations = Math.Max(1, target.Confirmations) + 1;
                    target.Enabled = true;
                    target.UpdatedAtTicks = now;
                }

                observation.Status = "已通过人工即时回复学习";
                observation.LearnedKnowledgeId = target.EntityId;
                observation.UpdatedAtTicks = now;
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { target, observation });
                DbHelper.Db.Execute(
                    "delete from VisualKnowledgeEntryEntity where ShopKey = ? and Seller = ? and EntityId not in "
                    + "(select EntityId from VisualKnowledgeEntryEntity where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc limit 500)",
                    observation.ShopKey,
                    observation.Seller,
                    observation.ShopKey,
                    observation.Seller);
                return target;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("写入人工视觉知识失败：" + ex.Message, 20);
                return null;
            }
        }

        private static string BuildCombinedAnswer(PendingReply pending)
        {
            List<string> parts;
            lock (pending.Sync)
            {
                parts = pending.Fragments
                    .OrderBy(x => x.At)
                    .Select(x => CleanHumanReply(x.Text, 500).TrimEnd('。', '！', '？', '；', ';'))
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            var answer = string.Join("；", parts);
            if (answer.Length > 0 && !answer.EndsWith("。", StringComparison.Ordinal)) answer += "。";
            return answer;
        }

        private static bool IsReusableHumanAnswer(string answer)
        {
            answer = Normalize(answer);
            if (answer.Length < 2 || ContainsHighRisk(answer)) return false;
            return answer != "稍等"
                && answer != "稍等一下"
                && answer != "等一下"
                && answer != "我看看"
                && answer != "我看一下"
                && answer != "好的"
                && answer != "好"
                && answer != "嗯"
                && answer != "在的";
        }

        private static bool IsBotReply(string seller, string buyer, string text)
        {
            var compact = Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
            if (compact.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase)) return true;
            try { return SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, text); }
            catch { return false; }
        }

        private static double Similarity(
            VisualKnowledgeObservationEntity observation,
            VisualKnowledgeEntryEntity entry)
        {
            var summary = BigramSimilarity(Normalize(observation.VisualSummary), Normalize(entry.VisualSummary));
            var leftTags = SplitTags(observation.VisualTags);
            var rightTags = SplitTags(entry.VisualTags);
            var union = leftTags.Union(rightTags).Count();
            var tagScore = union == 0 ? 0 : (double)leftTags.Intersect(rightTags).Count() / union;
            return Math.Max(0, Math.Min(1, summary * 0.75 + tagScore * 0.25));
        }

        private static double BigramSimilarity(string left, string right)
        {
            if (left.Length == 0 || right.Length == 0) return 0;
            if (left == right) return 1;
            var a = Bigrams(left);
            var b = Bigrams(right);
            if (a.Count == 0 || b.Count == 0) return 0;
            return (2.0 * a.Intersect(b).Count()) / (a.Count + b.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < value.Length; i++) result.Add(value.Substring(i, 2));
            return result;
        }

        private static HashSet<string> SplitTags(string value)
        {
            return new HashSet<string>((value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize)
                .Where(x => x.Length >= 2), StringComparer.Ordinal);
        }

        private static string ExtractManualTags(string answer)
        {
            return string.Join(",", (answer ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '。', '！', '？', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Regex.Replace(x.Trim(), @"^(这个|这是|属于|就是)", string.Empty))
                .Where(x => x.Length >= 2 && x.Length <= 40)
                .Take(8));
        }

        private static string MergeTags(string left, string right)
        {
            return string.Join(",", SplitTags(left).Union(SplitTags(right)).Take(20));
        }

        private static bool IsCurrent(PendingReply pending, int version)
        {
            lock (pending.Sync) return pending.Version == version;
        }

        private static void CleanupPending()
        {
            var cutoff = DateTime.Now.AddMinutes(-15);
            foreach (var pair in Pending.Where(x => x.Value == null || x.Value.LastReplyAt < cutoff).ToList())
            {
                PendingReply ignored;
                Pending.TryRemove(pair.Key, out ignored);
            }
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                var candidates = new List<string>();
                if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text))
                    candidates.Add(message.originalData.text.Trim());
                if (!string.IsNullOrWhiteSpace(message.summary)) candidates.Add(message.summary.Trim());
                return candidates.OrderByDescending(x => x.Length).FirstOrDefault() ?? string.Empty;
            }
            catch { return (message.summary ?? string.Empty).Trim(); }
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(message.sendTime, out dto)) return dto.LocalDateTime;
            long raw;
            if (long.TryParse(message.sendTime, out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    if (raw > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    if (raw > 1000000000L) return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                }
                catch { }
            }
            return DateTime.MinValue;
        }

        private static bool SameBuyer(string seller, string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            try { return BuyerIdentityAliasService.AreEquivalent(seller, left, right); }
            catch { return false; }
        }

        private static bool ContainsHighRisk(string value)
        {
            var terms = new[] { "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡", "验证码", "密码", "订单号", "手机号", "账号安全", "封号", "解封", "法律", "报警" };
            return terms.Any(x => (value ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string CleanHumanReply(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            value = Regex.Replace(value, @"\s+", " ");
            while (value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 4).TrimEnd();
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty);
        }

        private static string PreferLonger(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            return right.Length > left.Length ? right : left;
        }

        private static string Key(string shopKey, string seller, string buyer, string messageKey)
        {
            return (shopKey ?? string.Empty).Trim() + "#"
                + (seller ?? string.Empty).Trim() + "#"
                + (buyer ?? string.Empty).Trim() + "#"
                + (messageKey ?? string.Empty).Trim();
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
