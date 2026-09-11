using Bot.ChatRecord;
using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    internal enum VisionDecisionKind { Text, Vision, Skip }

    internal sealed class VisionMessageDecision
    {
        public VisionDecisionKind Kind { get; set; }
        public string QuestionLabel { get; set; }
        public string Note { get; set; }

        public static VisionMessageDecision Decide(
            QNChatMessage message,
            string text,
            IncomingMessageDecision safetyDecision,
            IEnumerable<AiEndpointConfig> endpoints)
        {
            if (safetyDecision == null)
                return Skip("[未知消息]", "已跳过：消息安全检查失败，未调用AI，也未发送给买家。");

            // First-inquiry is a prelude delivery policy, not a replacement answer. Reserve it only
            // when the complete incoming message can be classified as a real buyer problem. The
            // deterministic pre-merge sender delivers the configured greeting first; this decision
            // then keeps the same problem on its ordinary text/vision path for the substantive reply.
            var seller = message == null || message.toid == null
                ? string.Empty
                : (message.toid.nick ?? string.Empty).Trim();
            var buyer = message == null || message.fromid == null
                ? string.Empty
                : (message.fromid.nick ?? string.Empty).Trim();
            var firstQuestion = IncomingMessageSafety.GetDisplayText(message, text);
            string fixedAnswer;
            var firstPrepared = FirstInquiryFixedReplyService.TryPrepare(
                seller,
                buyer,
                message,
                firstQuestion,
                safetyDecision,
                out fixedAnswer);

            var isImage = string.Equals(safetyDecision.MessageLabel, "[图片]", StringComparison.Ordinal);
            if (isImage)
            {
                // Local OCR + Knowledge V2 is a real image-understanding route and must be considered
                // before deciding that the shop has no image capability. VisionRequestService executes
                // this local route before selecting any external provider, so pre-routing must not skip
                // the image merely because no billable vision endpoint is configured.
                var localUsable = CanUseLocalOcrKnowledge(seller);
                var selectedEndpoints = ResolveShopVisionEndpoints(message, endpoints);
                var externalUsable = (selectedEndpoints ?? new AiEndpointConfig[0]).Any(
                    e => e != null
                        && e.Enabled
                        && e.SupportsVision
                        && !string.IsNullOrWhiteSpace(e.VisionModel)
                        && !string.IsNullOrWhiteSpace(e.ApiKey)
                        && !string.IsNullOrWhiteSpace(e.BaseUrl));
                var usable = localUsable || externalUsable;

                // BuyerMessageBurstCoordinator runs DeterministicAutoReplyService before merge.
                // Images are intentionally marked replyable here only when a first greeting was
                // reserved, so that sender can deliver the greeting first; VisionDecisionKind stays
                // Vision and the same image is analysed immediately afterwards.
                if (firstPrepared)
                {
                    safetyDecision.ShouldCallAi = true;
                    safetyDecision.Note = usable
                        ? "首条咨询固定回复先发送，随后继续图片视觉理解。"
                        : "首条咨询固定回复先发送；当前既无可用本地OCR知识直答，也无外部视觉模型，后续视觉任务将安全失败而不发送伪答案。";
                    return new VisionMessageDecision
                    {
                        Kind = VisionDecisionKind.Vision,
                        QuestionLabel = "[图片]",
                        Note = safetyDecision.Note
                    };
                }

                if (!usable)
                    return Skip("[图片]", "已跳过：本店未配置可用的视觉模型，且本地OCR+Knowledge V2不可用，未向买家发送消息。");

                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Vision,
                    QuestionLabel = "[图片]",
                    Note = localUsable && !externalUsable
                        ? "使用本地OCR+Knowledge V2图片理解；无需外部视觉模型。"
                        : string.Empty
                };
            }

            if (firstPrepared)
            {
                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Text,
                    QuestionLabel = firstQuestion,
                    Note = "首条咨询固定回复先发送，随后同一实际问题继续进入正常文本回复处理。"
                };
            }

            if (safetyDecision.ShouldCallAi)
                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Text,
                    QuestionLabel = text,
                    Note = string.Empty
                };

            // Keep the explicit non-image skip guard for compatibility with existing safety
            // invariants. Real images have already returned through the image branch above.
            if (!string.Equals(safetyDecision.MessageLabel, "[图片]", StringComparison.Ordinal))
                return Skip(safetyDecision.MessageLabel, safetyDecision.Note);
            return Skip("[图片]", safetyDecision.Note);
        }

        private static bool CanUseLocalOcrKnowledge(string sellerNick)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            if (sellerNick.Length == 0) return false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick);
                if (shop == null) return false;
                using (ShopSettingsScope.Enter(shop))
                {
                    return ReplyModeService.IsLocalFirst(sellerNick)
                        && KnowledgeEngineV2Service.IsEnabled(sellerNick)
                        && KnowledgeEngineV2Service.IsSnapshotReady(sellerNick);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "图片本地OCR能力探测失败，继续检查外部视觉模型：" + Safe(ex.Message, 220),
                    20);
                return false;
            }
        }

        private static IEnumerable<AiEndpointConfig> ResolveShopVisionEndpoints(
            QNChatMessage message,
            IEnumerable<AiEndpointConfig> fallback)
        {
            try
            {
                var sellerNick = message == null || message.toid == null
                    ? string.Empty
                    : (message.toid.nick ?? string.Empty).Trim();
                if (sellerNick.Length == 0) return fallback;

                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick);
                using (ShopSettingsScope.Enter(shop))
                {
                    return AiEndpointStore.GetVisionEnabledEndpoints();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "视觉消息未能解析店铺 AI 配置，使用旧全局配置兼容模式：" + Safe(ex.Message, 220),
                    20);
                return fallback;
            }
        }

        private static VisionMessageDecision Skip(string label, string note)
        {
            return new VisionMessageDecision
            {
                Kind = VisionDecisionKind.Skip,
                QuestionLabel = label,
                Note = note
            };
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}