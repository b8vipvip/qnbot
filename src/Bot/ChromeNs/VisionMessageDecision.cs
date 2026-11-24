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
            string shopKey)
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
                out fixedAnswer,
                shopKey);

            var isImage = string.Equals(safetyDecision.MessageLabel, "[图片]", StringComparison.Ordinal);
            if (isImage)
            {
                // Local OCR + Knowledge V2 is a real image-understanding route and must be considered
                // before deciding that the shop has no image capability. VisionRequestService executes
                // this local route before selecting any external provider, so pre-routing must not skip
                // the image merely because no billable vision endpoint is configured.
                // ShopKey is supplied by the authoritative QN session. Resolve configuration from
                // that scope, not from message.toid.nick, which can be an alias/display nickname.
                // A failed preflight must not silently discard the image: the OCR attempt records
                // the concrete endpoint/auth failure locally; requests reaching the server enter its task ledger.
                var shopScopeUsable = HasAuthoritativeShopScope(shopKey);
                var localUsable = CanUseServerOcr(shopKey);
                var selectedEndpoints = (ResolveShopVisionEndpoints(shopKey) ?? new AiEndpointConfig[0]).ToList();
                var externalUsable = selectedEndpoints.Any(
                    e => e != null
                        && e.Enabled
                        && e.SupportsVision
                        && !string.IsNullOrWhiteSpace(e.VisionModel)
                        && !string.IsNullOrWhiteSpace(e.ApiKey)
                        && !string.IsNullOrWhiteSpace(e.BaseUrl));
                Log.Info("图片消息路由探测: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                    + ", shopScope=" + shopScopeUsable
                    + ", serverOcrConfigured=" + localUsable
                    + ", visionEndpoints=" + selectedEndpoints.Count
                    + ", policy=attempt_and_record");

                // BuyerMessageBurstCoordinator runs deterministic pre-merge rules before merge.
                // A valid ShopKey keeps this image replyable even if preflight cannot read the OCR
                // token; the actual OCR/vision request records the failure instead of dropping it.
                if (!shopScopeUsable)
                {
                    if (firstPrepared)
                        FirstInquiryFixedReplyService.ReleaseReservation(
                            seller, buyer, "图片消息缺少可验证的ShopKey店铺身份", shopKey);
                    return Skip("[图片]", "已跳过：无法确认图片所属店铺身份，已阻止跨店处理。");
                }

                if (firstPrepared)
                {
                    safetyDecision.ShouldCallAi = true;
                    safetyDecision.Note = "首条咨询固定回复先发送，随后继续图片视觉理解；OCR/视觉连接状态将在实际处理阶段记录。";
                    return new VisionMessageDecision
                    {
                        Kind = VisionDecisionKind.Vision,
                        QuestionLabel = "[图片]",
                        Note = safetyDecision.Note
                    };
                }

                if (!localUsable && !externalUsable)
                {
                    Log.Info("图片消息没有可用的OCR/视觉连接预检结果，但仍进入视觉处理链路；"
                        + "由实际OCR处理阶段记录连接失败，避免静默丢弃图片: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shopKey));
                }

                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Vision,
                    QuestionLabel = "[图片]",
                    Note = localUsable && !externalUsable
                        ? "使用服务端OCR，并在Knowledge V2就绪时进行知识直答；无需外部视觉模型。"
                        : (!localUsable && !externalUsable
                            ? "已进入视觉处理链路；OCR连接状态由实际处理阶段记录。"
                            : string.Empty)
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

        private static bool HasAuthoritativeShopScope(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                return shop != null && !string.IsNullOrWhiteSpace(shop.SellerNick);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanUseServerOcr(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                if (shop == null || string.IsNullOrWhiteSpace(shop.SellerNick)) return false;
                using (ShopSettingsScope.Enter(shop, false))
                {
                    // The stable ShopKey resolves the authoritative seller. Do not compare it with
                    // message.toid.nick because Qianniu may deliver an alias/display nickname here.
                    return LocalOcrService.IsServerOcrConfigured(shop.SellerNick, shopKey);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "图片服务端OCR能力探测失败，仍允许进入实际OCR链路记录故障：" + Safe(ex.Message, 220),
                    20);
                return false;
            }
        }

        private static IEnumerable<AiEndpointConfig> ResolveShopVisionEndpoints(string shopKey)
        {
            try
            {
                shopKey = (shopKey ?? string.Empty).Trim();
                if (shopKey.Length == 0) return new AiEndpointConfig[0];

                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                if (shop == null || string.IsNullOrWhiteSpace(shop.SellerNick))
                    return new AiEndpointConfig[0];
                using (ShopSettingsScope.Enter(shop))
                {
                    return AiEndpointStore.GetVisionEnabledEndpoints();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "视觉消息未能解析唯一ShopKey的AI配置，已fail-closed且不回退全局配置：" + Safe(ex.Message, 220),
                    20);
                return new AiEndpointConfig[0];
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