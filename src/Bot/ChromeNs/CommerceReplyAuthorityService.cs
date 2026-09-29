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

            if (commerce.HasStructuredOrder)
            {
                decision.RequiresContextualAgent = true;
                decision.Reason = "存在结构化订单事实，回复必须结合当前订单阶段/商品/SKU/履约状态";
                return decision;
            }

            if (string.Equals(
                commerce.PurchasePhase,
                "post_order_unverified",
                StringComparison.OrdinalIgnoreCase))
            {
                decision.RequiresContextualAgent = true;
                decision.Reason = "会话强烈指向下单后但缺少结构化订单，禁止固定FAQ直接结束回复";
                return decision;
            }

            decision.RequiresContextualAgent = false;
            decision.Reason = "未进入订单相关阶段，可继续普通售前知识路由";
            return decision;
        }
    }
}
