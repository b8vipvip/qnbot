using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal enum RemoteSellerEchoVerification
    {
        Delivered = 1,
        Absent = 2,
        Unavailable = 3
    }

    public partial class QN
    {
        internal async Task<RemoteSellerEchoVerification> VerifySellerEchoInRemoteHistoryAsync(
            string seller,
            string buyer,
            string expectedText,
            DateTime notBefore)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            expectedText = NormalizeDeliveryText(expectedText);
            if (seller.Length == 0 || buyer.Length == 0 || expectedText.Length == 0 || cdp == null)
                return RemoteSellerEchoVerification.Unavailable;

            try
            {
                var response = await GetCurrentConversationID().ConfigureAwait(false);
                var current = response == null ? null : response.Result;
                if (current == null
                    || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer)
                    || string.IsNullOrWhiteSpace(current.Ccode))
                {
                    Log.Info("订单送达远端核验不可用：当前会话不是目标买家。seller=" + seller
                        + ", buyer=" + buyer);
                    return RemoteSellerEchoVerification.Unavailable;
                }

                var ccode = current.Ccode.Trim();
                var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = ccode, type = 1 },
                    // A 40-message buyer burst can push the just-submitted seller echo
                    // out of a 30-row window before order delivery verification reads it.
                    count = 100,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                }).ConfigureAwait(false);
                if (history == null)
                    return RemoteSellerEchoVerification.Unavailable;

                var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>()
                    ?? new List<QNChatMessage>();
                var threshold = notBefore.AddSeconds(-4).Ticks;
                foreach (var message in messages.Where(x => x != null))
                {
                    if (message.fromid == null || !EquivalentSellerNick(message.fromid.nick, seller)) continue;
                    var sort = IncomingMessageSafety.GetSortValue(message);
                    if (sort > 0 && sort < threshold) continue;
                    var actual = NormalizeDeliveryText(ExtractDeliveryText(message));
                    if (actual.Length > 0 && string.Equals(actual, expectedText, StringComparison.Ordinal))
                        return RemoteSellerEchoVerification.Delivered;
                }
                return RemoteSellerEchoVerification.Absent;
            }
            catch (Exception ex)
            {
                Log.Info("订单送达远端核验失败，禁止盲目重发: seller=" + seller
                    + ", buyer=" + buyer + ", error=" + SafeDeliveryError(ex.Message));
                return RemoteSellerEchoVerification.Unavailable;
            }
        }

        private static string ExtractDeliveryText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text))
                return message.originalData.text;
            return message.summary ?? string.Empty;
        }

        private static bool EquivalentSellerNick(string candidate, string seller)
        {
            return string.Equals(NormalizeTaobaoIdentity(candidate), NormalizeTaobaoIdentity(seller), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTaobaoIdentity(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("cntaobao", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("cntaobao".Length);
            return value;
        }

        private static string NormalizeDeliveryText(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            value = Regex.Replace(value, @"\s+", " ");
            value = Regex.Replace(value, @"\s*\[A\]\s*$", string.Empty, RegexOptions.IgnoreCase);
            return value.Trim();
        }

        private static string SafeDeliveryError(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 180 ? value : value.Substring(0, 180) + "...";
        }
    }
}
