using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static partial class ReturningBuyerFirstReplyBridge
    {
        private static void OnMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                var seller = qn.Seller == null ? "" : (qn.Seller.Nick ?? "").Trim();
                if (data == null || data.result == null || seller.Length == 0) return;
                foreach (var m in data.result.Where(x => x != null && x.fromid != null && x.toid != null))
                {
                    var buyer = (m.fromid.nick ?? "").Trim();
                    var to = (m.toid.nick ?? "").Trim();
                    if (buyer.Length == 0 || to != seller || buyer == seller) continue;
                    var question = MessageText(m);
                    string nonBuyerReason;
                    if (NonBuyerConversationGuard.ShouldBlockMessage(m, seller, question, out nonBuyerReason))
                    {
                        Log.Info("回访首答已跳过非买家消息: reason=" + nonBuyerReason);
                        continue;
                    }
                    ShopContext shop;
                    try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
                    catch { shop = null; }
                    if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                        || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), seller, StringComparison.Ordinal))
                        continue;

                    DateTime detectedAt;
                    if (!OrderCardParser.TryGetMessageTime(m, out detectedAt) || detectedAt == DateTime.MinValue)
                        detectedAt = DateTime.Now;
                    List<ConversationContextTurn> priorTurns;
                    using (ShopSettingsScope.Enter(shop))
                    {
                        priorTurns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 24);
                    }
                    var prior = (priorTurns ?? new List<ConversationContextTurn>())
                        .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                        .OrderByDescending(x => x.Timestamp).FirstOrDefault();
                    if (prior == null || prior.Timestamp == DateTime.MinValue) continue;
                    var idle = detectedAt - prior.Timestamp;
                    if (idle.TotalMinutes <= ReturningBuyerIdleMinutes || idle.TotalMinutes >= ExistingSessionResetMinutes) continue;
                    var key = shop.ShopKey + "#" + seller + "#" + buyer;
                    if (!Reservations.TryAdd(key, DateTime.Now)) continue;
                    Task.Run(async () => await SendAsync(qn, shop.ShopKey, seller, buyer, question, detectedAt, key));
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("回访首答事件处理失败：" + ex.Message, 10); }
        }

        private static string MessageText(QNChatMessage m)
        {
            try
            {
                var text = m.originalData == null ? "" : (m.originalData.text ?? "");
                if (m.originalData != null && m.originalData.header != null) text += m.originalData.header.summary ?? "";
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch { }
            return (m.summary ?? "").Trim();
        }
    }
}
