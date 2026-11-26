using System;
using System.Collections.Generic;

namespace Bot.ChromeNs
{
    internal sealed class CommerceReplyAuthorityDecision
    {
        public ConversationStateSnapshot ConversationState { get; set; }
        public CommerceContextSnapshot CommerceContext { get; set; }
        public bool RequiresContextualAgent { get; set; }
        public bool AllowLocalTerminalReply { get { return !RequiresContextualAgent; } }
        public string Reason { get; set; }

        public CommerceReplyAuthorityDecision()
        {
            Reason = string.Empty;
        }
    }

    /// <summary>
    /// Single authority for deciding whether a buyer-message reply may terminate in a local fixed
    /// answer or must continue through the commerce-aware contextual agent. It deliberately does not
    /// generate text and does not send anything. Smart Reply, V2 and future retrieval layers consume
    /// the same decision instead of each owning overlapping order-state conditions.
    /// </summary>
    internal static class CommerceReplyAuthorityService
    {
        public static CommerceReplyAuthorityDecision Evaluate(
            string seller,
            string buyer,
            string question)
        {
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 16);
            var state = ConversationStateService.Build(seller, buyer, question, turns);
            return Evaluate(state);
        }

        private static readonly System.Text.RegularExpressions.Regex OrderDependentQuestionRegex =
            new System.Text.RegularExpressions.Regex(
                "订单|订单号|订单状态|我的订单|这笔订单|这单|交易状态|交易号|下单后|已下单|刚买|买的东西|支付|付款|付钱|实付款|付款金额|未付款|已付款|支付成功|发货|物流|快递|运单|收货|签收|退款|退货|售后|退换|取消订单|关闭订单",
                System.Text.RegularExpressions.RegexOptions.Compiled
                    | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static bool IsOrderDependentQuestion(string question)
        {
            return !string.IsNullOrWhiteSpace(question)
                && OrderDependentQuestionRegex.IsMatch(question);
        }

        public static CommerceReplyAuthorityDecision Evaluate(ConversationStateSnapshot state)
        {
            var commerce = state == null ? null : state.CommerceContext;
            var decision = new CommerceReplyAuthorityDecision
            {
                ConversationState = state,
                CommerceContext = commerce
            };

            if (commerce == null)
            {
                decision.RequiresContextualAgent = false;
                decision.Reason = "没有CommerceContext，沿用普通售前知识路由";
                return decision;
            }

            var currentQuestion = state.CurrentQuestion ?? string.Empty;
            var orderDependent = IsOrderDependentQuestion(currentQuestion);
            if (orderDependent && commerce.HasStructuredOrder)
            {
                decision.RequiresContextualAgent = true;
                decision.Reason = "当前问题明确涉及订单/支付/履约/售后，需按知识规则核验对应订单证据";
                return decision;
            }

            if (orderDependent
                && string.Equals(
                    commerce.PurchasePhase,
                    "post_order_unverified",
                    StringComparison.OrdinalIgnoreCase))
            {
                decision.RequiresContextualAgent = true;
                decision.Reason = "当前问题涉及下单后事项，但没有可核验的结构化订单";
                return decision;
            }

            decision.RequiresContextualAgent = false;
            decision.Reason = commerce.HasStructuredOrder
                ? "当前问题不依赖订单状态，不注入订单证据，沿用普通知识路由"
                : "当前问题不需要订单证据，可继续普通知识路由";
            return decision;
        }
    }
}
