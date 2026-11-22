using Bot.ChatRecord;
using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2FeedbackBridge
    {
        private static readonly ConcurrentDictionary<int, bool> Attached =
            new ConcurrentDictionary<int, bool>();
        private static readonly ConcurrentDictionary<string, DateTime> Seen =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly DateTime StartedAt = DateTime.Now;
        private static Timer _timer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            AttachExisting();
            _timer = new Timer(_ => AttachExisting(), null, 400, 700);
            Log.Info("Knowledge Engine V2反馈观察器已启动：发送成功、明确认可、人工纠正和撤回将进入本地质量闭环；发送失败仅作为传输指标。" );
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
                    var key = RuntimeHelpers.GetHashCode(qn);
                    if (!Attached.TryAdd(key, true)) continue;
                    var captured = qn;
                    captured.EvRecieveNewMessage += (s, e) => OnRawMessages(captured, e);
                    var seller = captured.Seller == null ? string.Empty : (captured.Seller.Nick ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(seller))
                    {
                        var capturedSeller = seller;
                        Task.Run(() =>
                        {
                            try { KnowledgeEngineV2FeedbackService.Warm(capturedSeller); }
                            catch (Exception ex) { Log.ErrorWithMaxCount("Knowledge V2反馈缓存预热失败: " + ex.Message, 10); }
                        });
                    }
                }
                CleanupSeen();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装Knowledge V2反馈观察器失败: " + ex.Message, 10);
            }
        }

        private static void OnRawMessages(QN qn, RecieveNewMessageEventArgs e)
        {
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                foreach (var message in response.result.Where(x => x != null).OrderBy(IncomingMessageSafety.GetSortValue))
                    ObserveMessage(qn, message);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("解析Knowledge V2反馈消息失败: " + ex.Message, 10);
            }
        }

        private static void ObserveMessage(QN qn, QNChatMessage message)
        {
            if (message == null) return;
            var messageTime = GetMessageTime(message);
            if (messageTime != DateTime.MinValue && messageTime < StartedAt.AddSeconds(-8)) return;

            ShopContext shop;
            try
            {
                // The event's QN wrapper is the authoritative source of ShopKey.
                // Never let identical native message IDs in two shops share a dedup key.
                shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge V2反馈观察拒绝无法唯一解析消息来源店铺: " + ex.Message, 10);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || string.IsNullOrWhiteSpace(shop.SellerNick)) return;

            using (ShopSettingsScope.Enter(shop))
            {
                var seller = shop.SellerNick.Trim();
                var qnSeller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                if (qnSeller.Length > 0
                    && !DirectOrderIdentityResolver.IdentityEquals(qnSeller, seller)) return;
                var loginNick = message.loginid == null ? string.Empty
                    : (message.loginid.nick ?? string.Empty).Trim();
                if (loginNick.Length > 0
                    && !DirectOrderIdentityResolver.IdentityEquals(loginNick, seller)) return;

                var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
                var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
                var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(from, seller);
                var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(to, seller);
                if (fromIsSeller == toIsSeller) return;
                var buyer = fromIsSeller ? to : from;
                if (string.IsNullOrWhiteSpace(buyer)
                    || DirectOrderIdentityResolver.IdentityEquals(buyer, seller)) return;

                if (shop.HasStableSellerId)
                {
                    var expectedTargetId = (shop.SellerId ?? string.Empty).Trim();
                    var sellerEndpointTargetId = fromIsSeller
                        ? (message.fromid == null ? string.Empty : (message.fromid.targetId ?? string.Empty).Trim())
                        : (message.toid == null ? string.Empty : (message.toid.targetId ?? string.Empty).Trim());
                    var loginTargetId = message.loginid == null ? string.Empty
                        : (message.loginid.targetId ?? string.Empty).Trim();
                    if (sellerEndpointTargetId.Length > 0
                        && !string.Equals(sellerEndpointTargetId, expectedTargetId, StringComparison.Ordinal)) return;
                    if (loginTargetId.Length > 0
                        && !string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal)) return;
                }

                // Observe the exact native identity pair before alias-aware role classification.
                BuyerIdentityAliasService.ObserveMessage(seller, message);
                var canonicalBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
                if (!string.IsNullOrWhiteSpace(canonicalBuyer)) buyer = canonicalBuyer;

                var text = ExtractMessageText(message);
                var messageKey = IncomingMessageSafety.BuildMessageKey(message, text);
                var scopedKey = shop.ShopKey + "|v2-feedback:" + messageKey;
                if (!string.IsNullOrWhiteSpace(messageKey)
                    && !Seen.TryAdd(scopedKey, DateTime.Now)) return;

                var fromIsBuyer = BuyerIdentityAliasService.AreEquivalent(seller, from, buyer);
                var toIsBuyer = BuyerIdentityAliasService.AreEquivalent(seller, to, buyer);
                var isBuyer = fromIsBuyer && toIsSeller;
                var isSeller = fromIsSeller && toIsBuyer;
                var isWithdrawal = ConversationContextStore.IsWithdrawalNotice(message, text);
                if (isWithdrawal)
                {
                    KnowledgeEngineV2FeedbackService.ObserveWithdrawal(seller, buyer, text);
                    return;
                }
                if (ConversationContextStore.IsPlatformSystemTip(message, text)) return;

                if (isBuyer)
                    KnowledgeEngineV2FeedbackService.ObserveBuyerMessage(seller, buyer, text);
                else if (isSeller)
                    KnowledgeEngineV2FeedbackService.ObserveSellerMessage(seller, buyer, text);
            }
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                        text += message.originalData.header.summary ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch { }
            return (message.summary ?? string.Empty).Trim();
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime result;
            if (TryParseTime(message.sendTime, out result)) return result;
            if (TryParseTime(message.sortTimeMicrosecond, out result)) return result;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) result = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (result != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                result = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static void CleanupSeen()
        {
            if (Seen.Count < 5000) return;
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var key in Seen.Where(x => x.Value < cutoff).Select(x => x.Key).Take(2500).ToList())
            {
                DateTime ignored;
                Seen.TryRemove(key, out ignored);
            }
        }
    }
}
