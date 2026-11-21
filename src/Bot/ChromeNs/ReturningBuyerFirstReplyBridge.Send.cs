using BotLib;
using Bot.ShopScope;
using System;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static partial class ReturningBuyerFirstReplyBridge
    {
        private static async Task SendAsync(QN qn, string seller, string buyer, string question, string key)
        {
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), (seller ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    Release(key);
                    Log.ErrorWithMaxCount("回访首答缺少唯一店铺身份，已阻止未作用域发送。", 20);
                    return;
                }

                using (ShopSettingsScope.Enter(shop))
                {
                    await Task.Delay(120);
                    if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply() || FirstInquiryFixedReplyService.HasPending(seller, buyer))
                    {
                        Release(key);
                        return;
                    }
                    var cfg = FirstInquiryFixedReplyService.Load(seller);
                    if (cfg == null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Answer)) { Release(key); return; }
                    var answer = BotFeatureStore.ApplyOutputPolicy(cfg.Answer.Trim()) ?? "";
                    if (answer.Length == 0) { Release(key); return; }
                    answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, answer, "首条咨询固定回复-10分钟回访");
                    var ok = await qn.SendTextWithRetryAsync(buyer, answer, 1);
                    if (ok)
                    {
                        FirstInquiryFixedReplyService.MarkDelivered(seller, buyer);
                        ReplyDeduplicationService.RememberDelivered(seller, buyer, answer);
                        Log.Info("回访买家超过10分钟无互动，首次固定回复已发送: seller=" + seller + ", buyer=" + buyer);
                    }
                    else Release(key);
                }
            }
            catch (Exception ex)
            {
                Release(key);
                Log.ErrorWithMaxCount("回访首答发送失败：" + ex.Message, 10);
            }
        }

        private static void Release(string key)
        {
            DateTime ignored;
            Reservations.TryRemove(key, out ignored);
        }
    }
}
