using BotLib;
using Bot.ShopScope;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static partial class ReturningBuyerFirstReplyBridge
    {
        private static async Task SendAsync(QN qn, string shopKey, string seller, string buyer, string question, DateTime detectedAt, string key)
        {
            var sendConfirmed = false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                    || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), (seller ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    Release(key);
                    Log.ErrorWithMaxCount("回访首答缺少唯一店铺身份，已阻止未作用域发送。", 20);
                    return;
                }

                using (ShopSettingsScope.Enter(shop))
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()
                        || FirstInquiryFixedReplyService.HasPending(seller, buyer, shop.ShopKey))
                    {
                        Release(key);
                        return;
                    }
                    var cfg = FirstInquiryFixedReplyService.Load(seller);
                    if (cfg == null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Answer)) { Release(key); return; }
                    var answer = BotFeatureStore.ApplyOutputPolicy(cfg.Answer.Trim()) ?? "";
                    if (answer.Length == 0) { Release(key); return; }
                    answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);

                    string initialRelevanceReason;
                    if (!ParallelReplyRelevanceGate.ShouldSend(
                        seller, buyer, question, detectedAt, out initialRelevanceReason, false, shop.ShopKey))
                    {
                        Log.Info("回访首答发送前已存在更新消息或人工接管，阻止旧固定回复: reason=" + initialRelevanceReason);
                        Release(key);
                        return;
                    }

                    Func<bool> preSendGuard = () =>
                    {
                        if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()
                            || FirstInquiryFixedReplyService.HasPending(seller, buyer, shop.ShopKey))
                            return false;
                        string latestRelevanceReason;
                        var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                            seller, buyer, question, detectedAt, out latestRelevanceReason, false, shop.ShopKey);
                        if (!shouldSend)
                            Log.Info("回访首答等待发送资源期间会话已变化，阻止迟到固定回复: reason=" + latestRelevanceReason);
                        return shouldSend;
                    };

                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, answer, "首条咨询固定回复-10分钟回访");
                    var ok = await qn.SendTextWithRetryAsync(
                        buyer, answer, 1, CancellationToken.None, preSendGuard).ConfigureAwait(false);
                    sendConfirmed = ok;
                    if (ok)
                    {
                        DateTime ignored;
                        IndeterminateReservations.TryRemove(key, out ignored);
                        FirstInquiryFixedReplyService.MarkDelivered(seller, buyer, shop.ShopKey);
                        ReplyDeduplicationService.RememberDelivered(seller, buyer, answer, shop.ShopKey);
                        Log.Info("回访买家超过10分钟无互动，首次固定回复已发送: seller=" + seller + ", buyer=" + buyer);
                    }
                    else if (qn.Rpa != null && qn.Rpa.LastSendWasIndeterminate)
                    {
                        RetainIndeterminateReservation(key);
                        Log.ErrorWithMaxCount("回访首答发送结果不确定，已保留本店买家资格占位，禁止重复触发: seller="
                            + seller + ", buyer=" + buyer, 20);
                    }
                    else Release(key);
                }
            }
            catch (Exception ex)
            {
                if (sendConfirmed)
                {
                    // The physical send already returned success; a later bookkeeping/logging
                    // exception must not reopen this message's reservation and allow a duplicate.
                    DateTime ignored;
                    IndeterminateReservations.TryRemove(key, out ignored);
                    Reservations[key] = DateTime.Now;
                }
                else if (qn != null && qn.Rpa != null && qn.Rpa.LastSendWasIndeterminate)
                    RetainIndeterminateReservation(key);
                else
                    Release(key);
                Log.ErrorWithMaxCount("回访首答发送失败：" + ex.Message, 10);
            }
        }

        private static void RetainIndeterminateReservation(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var now = DateTime.Now;
            Reservations[key] = now;
            IndeterminateReservations[key] = now;
        }

        private static void Release(string key)
        {
            DateTime ignored;
            Reservations.TryRemove(key, out ignored);
            IndeterminateReservations.TryRemove(key, out ignored);
        }
    }
}
