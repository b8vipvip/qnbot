using Bot.ChromeNs;
using Bot.ChatRecord;
using Bot.Automation.ChatDeskNs;
using System;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    // Only actual buyer-authored utterances should enter Buyer Questions V2.
    // Preserve buyer question phrases about orders, refunds and shipment; do
    // not broadly blacklist those keywords. Classify structured cards and
    // automatic notifications by metadata plus high-specificity patterns.
    internal static class BuyerHistoryMessageFilter
    {
        private static readonly Regex LinkOnly = new Regex(
            @"^\s*(?:\[链接\]|\[商品链接\]|(?:https?://|www\.|m\.tb\.cn/|item\.taobao\.com/|detail\.tmall\.com/)\S*)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CardHeaders = new Regex(
            @"^(?:系统(?:消息|通知|提示)|平台(?:消息|通知|提示)|官方(?:消息|通知)|风险(?:提示|提醒|通知)|安全(?:提示|提醒|通知)|交易风险|订单(?:卡片|信息|详情|通知)|发货(?:卡片|信息|通知)|物流(?:卡片|信息|通知)|售后(?:卡片|信息|通知)|退款(?:卡片|信息|通知)|商品(?:卡片|信息|详情))\s*(?:[:：]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AutomaticNotices = new Regex(
            @"^\s*(?:(?:\[?(?:系统|平台|官方|千牛|淘宝|天猫)(?:消息|通知|提示|提醒)\]?|\[?(?:风险提醒|风险提示|安全提醒|安全提示|交易风险提示)\]?)\s*[:：]|温馨提示[:：]|系统自动(?:发送|回复|通知)[:：]?|平台自动(?:发送|回复|通知)[:：]?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StructuredOrderFields = new Regex(
            @"(?:订单(?:号|编号)|交易时间|订单金额|订单状态|交易状态|物流单号|运单号|售后单号|共\s*\d+\s*件商品|合计\s*[￥¥]?\s*\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StructuredCardMarker = new Regex(
            @"(?:^|[\r\n])\s*(?:订单(?:号|编号|信息)|交易时间|共\s*\d+\s*件商品|物流(?:单号|详情)|发货(?:时间|信息)|售后(?:单号|信息)|退款(?:金额|详情))\s*[:：]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CardType = new Regex(
            @"(?:订单|发货|物流|售后|退款|商品|系统|平台|风险|安全)(?:卡片|通知|提示|详情|信息|提醒)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool ShouldExcludeRaw(QNChatMessage message, string seller,
            string text, out string reason)
        {
            reason = null;
            if (message == null) return false;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text,
                out reason))
            {
                reason = "non_buyer_or_system_sender";
                return true;
            }
            if (ConversationContextStore.IsPlatformSystemTip(message, text)
                || ConversationContextStore.IsWithdrawalNotice(message, text))
            {
                reason = "platform_notice";
                return true;
            }
            if (message.originalData != null)
            {
                var header = message.originalData.header;
                if (header != null)
                {
                    var title = (header.title ?? "").Trim();
                    // Structured card fields are strong evidence, not just
                    // a buyer asking a natural-language order question.
                    if (CardHeaders.IsMatch(title) || CardType.IsMatch(title))
                    {
                        reason = "structured_card_header";
                        return true;
                    }
                }
                if (!string.IsNullOrWhiteSpace(message.originalData.itemId)
                    || !string.IsNullOrWhiteSpace(message.originalData.actionUrl))
                {
                    reason = "product_link_or_card";
                    return true;
                }
            }
            return ShouldExcludeText(text, out reason);
        }

        internal static bool ShouldExcludeText(string text, out string reason)
        {
            reason = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            if (LinkOnly.IsMatch(text))
            {
                reason = "standalone_link";
                return true;
            }
            if (AutomaticNotices.IsMatch(text))
            {
                reason = "automatic_notice";
                return true;
            }
            // Require both a field-like marker and at least two structured
            // order fields. Do not discard “请问订单状态如何” or “发货了吗”.
            if (StructuredCardMarker.IsMatch(text)
                && StructuredOrderFields.Matches(text).Count >= 2)
            {
                reason = "structured_order_logistics_aftersales";
                return true;
            }
            // A saved row may be truncated to a card summary with an explicit
            // type prefix, but plain buyer questions must remain eligible.
            if (CardHeaders.IsMatch(text) && text.Contains("："))
            {
                reason = "structured_card_text";
                return true;
            }
            return false;
        }

        internal static bool IsExcludedArchivedText(string text)
        {
            string reason;
            return ShouldExcludeText(text, out reason);
        }
    }
}
