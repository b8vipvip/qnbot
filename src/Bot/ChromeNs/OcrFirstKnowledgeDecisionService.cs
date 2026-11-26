using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// OCR-first local decision layer for image messages. It resolves the already-cached image,
    /// calls the authenticated server OCR control-plane endpoint. A saved image-knowledge match
    /// or legacy visual rule may identify the image context, but direct replies still require
    /// Knowledge V2 to pass its existing CanDirectReply safety gate. A description match alone
    /// is context-only and yields to AI for the final answer.
    /// When the server selects AI-first and a usable vision endpoint exists, this early layer yields
    /// immediately so visual AI remains primary; OCR is attached later as auxiliary evidence.
    /// </summary>
    internal static class OcrFirstKnowledgeDecisionService
    {
        private const double DirectKnowledgeMinOcrConfidence = 0.88d;
        private const int MinUsefulTextChars = 4;
        private const int SnapshotWarmWaitMs = 12000;

        public static async Task<VisionRequestResult> TryResolveAsync(
            VisionReplyTask task,
            CancellationToken cancellationToken)
        {
            if (task == null || task.Message == null) return null;

            // OCR is independent of Knowledge V2 readiness. Keep OCR available for AI-first
            // auxiliary evidence and for text fallback while the knowledge snapshot is cold.
            var priority = await VisionOcrPriorityService.ResolveAsync(
                task.SellerNick, task.ShopKey, cancellationToken).ConfigureAwait(false);
            if (VisionOcrPriorityService.IsAiFirst(priority))
            {
                var visionEndpoints = AiEndpointStore.GetVisionEnabledEndpoints();
                if (visionEndpoints != null && visionEndpoints.Count > 0)
                {
                    var hasImageKnowledge = false;
                    try { hasImageKnowledge = VisualImageKnowledgeService.HasEntries(task.ShopKey); }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("检查本店图片知识失败，保留原AI优先路由: " + ex.Message, 5);
                    }
                    if (!hasImageKnowledge)
                    {
                        Log.Info("视觉理解优先级=AI接口优先，且本店没有图片知识，跳过OCR+知识库提前直答: seller=" + task.SellerNick
                            + ", buyer=" + task.BuyerNick
                            + ", visionEndpoints=" + visionEndpoints.Count);
                        return null;
                    }

                    // Image knowledge must be matched even when the configured answer route is AI-first.
                    // OCR supplies the stable fingerprint and its description is then injected into AI context.
                    Log.Info("视觉理解优先级=AI接口优先，但本店存在图片知识，先执行服务端OCR匹配再交给视觉AI: seller="
                        + task.SellerNick + ", buyer=" + task.BuyerNick
                        + ", imageKnowledgeEnabled=true");
                }
                else
                {
                    Log.Info("视觉理解优先级=AI接口优先，但未配置可用视觉模型，允许OCR+知识库兜底: seller="
                        + task.SellerNick + ", buyer=" + task.BuyerNick);
                }
            }

            var sw = Stopwatch.StartNew();
            ShopContext shop = null;
            string knowledgeSeller = string.Empty;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeByShopKey(task.ShopKey);
                knowledgeSeller = shop == null ? string.Empty : (shop.SellerNick ?? string.Empty).Trim();
                if (shop != null && knowledgeSeller.Length > 0)
                {
                    using (ShopSettingsScope.Enter(shop, false))
                    {
                        if (KnowledgeEngineV2Service.IsEnabled(knowledgeSeller))
                        {
                            KnowledgeEngineV2RuntimeBridge.PrepareForVision(task.ShopKey, knowledgeSeller);
                            Log.Info("图片OCR已提前触发本店Knowledge V2索引预热: shopRef="
                                + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("图片OCR提前预热Knowledge V2失败，仍继续OCR和视觉兜底: " + ex.Message, 10);
            }

            VisionImageResult image;
            try
            {
                image = await new VisionImageResolver().ResolveAsync(
                    task.Message, null, cancellationToken, task.ShopKey, task.BrowserClient);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("OCR-first本地图片解析失败，继续视觉模型链路: " + ex.Message, 20);
                return null;
            }
            if (image == null || !image.Success || string.IsNullOrWhiteSpace(image.LocalCachePath)) return null;

            LocalOcrResult ocr;
            try
            {
                ocr = await LocalOcrService.TryRecognizeAsync(
                    image.LocalCachePath,
                    task.SellerNick,
                    task.ShopKey,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("OCR-first服务端OCR失败，继续视觉模型链路: " + ex.Message, 20);
                return null;
            }
            if (ocr == null || !ocr.Success)
            {
                Log.Info("OCR-first服务端OCR未成功，继续视觉/文字兜底: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                    + ", resultAvailable=false");
                return null;
            }

            if (shop == null || knowledgeSeller.Length == 0)
            {
                Log.ErrorWithMaxCount("OCR已完成，但无法通过权威ShopKey解析图片知识/Knowledge V2店铺，禁止跨店检索。", 20);
                return null;
            }

            VisualImageKnowledgeMatch imageKnowledgeMatch = null;
            try
            {
                VisualImageKnowledgeService.TryMatch(
                    task.ShopKey, knowledgeSeller, ocr.Text, ocr.ImageSha256, ocr.Confidence,
                    out imageKnowledgeMatch);
            }
            catch (Exception ex)
            {
                imageKnowledgeMatch = null;
                Log.ErrorWithMaxCount("本店图片知识匹配失败，仍继续OCR证据检索和视觉AI兜底: " + ex.Message, 10);
            }

            try
            {
                VisualKnowledgeLearningService.RecordOcrEvidence(
                    task.ShopKey, knowledgeSeller, task.BuyerNick, task.Message, task.MessageKey,
                    ocr,
                    imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.KnowledgeId,
                    imageKnowledgeMatch == null ? 0d : imageKnowledgeMatch.Score,
                    imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.Description);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("图片OCR与买家消息/图片知识关联持久化失败；本轮仍继续安全检索: " + ex.Message, 10);
            }

            if (!IsHighConfidenceUsefulText(ocr))
            {
                Log.Info("OCR-first未满足本地知识直答阈值: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", confidence=" + ocr.Confidence.ToString("0.00")
                    + ", chars=" + (ocr.Text == null ? 0 : ocr.Text.Trim().Length)
                    + ", imageKnowledgeMatched=" + (imageKnowledgeMatch != null));
                // A saved image description can still guide multimodal AI at lower OCR
                // confidence, but must not authorize a Knowledge V2 direct reply.
                return imageKnowledgeMatch == null
                    ? null
                    : BuildVisualFallback(null, ocr, "OCR置信度不足以进行Knowledge V2直答", imageKnowledgeMatch);
            }

            VisualUnderstandingMatch visualMatch = null;
            if (imageKnowledgeMatch == null)
            {
                // Keep existing operator rules as a compatibility fallback only. A curated
                // image-knowledge match is authoritative and must not be overridden by legacy fields.
                try
                {
                    if (!VisualUnderstandingRuleService.TryMatch(task.ShopKey, knowledgeSeller, ocr.Text, out visualMatch))
                    {
                        visualMatch = null;
                        Log.Info("OCR 未命中本店视觉理解预设，仍独立执行Knowledge V2证据检索；不允许基于未确认应用身份提前直答: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                    }
                }
                catch (Exception ex)
                {
                    visualMatch = null;
                    Log.ErrorWithMaxCount("旧版视觉理解规则匹配失败，仍继续OCR证据检索；不允许基于未确认应用身份提前直答: " + ex.Message, 10);
                }
            }

            KnowledgeV2Decision decision;
            CommerceReplyAuthorityDecision commerceAuthority = null;
            ConditionalBusinessAdviceEvidence conditionalAdviceEvidence = null;
            try
            {
                // Message display nicknames can be aliases. Knowledge V2 must use the seller
                // identity owned by the ShopKey, never the untrusted message.toid.nick value.
                using (ShopSettingsScope.Enter(shop, false))
                {
                    var question = (task.CombinedQuestion ?? string.Empty).Trim();
                    commerceAuthority = CommerceReplyAuthorityService.Evaluate(
                        knowledgeSeller, task.BuyerNick, question);
                    if (!KnowledgeEngineV2Service.IsEnabled(knowledgeSeller))
                    {
                        Log.Info("OCR已完成，但本店Knowledge V2未启用，继续视觉/文字兜底: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                        return BuildVisualFallback(visualMatch, ocr, "Knowledge V2未启用", imageKnowledgeMatch);
                    }

                    if (!KnowledgeEngineV2Service.IsSnapshotReady(knowledgeSeller))
                    {
                        var warmWait = Stopwatch.StartNew();
                        Log.Info("图片OCR等待Knowledge V2索引就绪: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                            + ", timeoutMs=" + SnapshotWarmWaitMs);
                        while (!KnowledgeEngineV2Service.IsSnapshotReady(knowledgeSeller)
                            && warmWait.ElapsedMilliseconds < SnapshotWarmWaitMs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            KnowledgeEngineV2RuntimeBridge.PrepareForVision(task.ShopKey, knowledgeSeller);
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                        warmWait.Stop();
                        Log.Info("图片OCR Knowledge V2索引预热等待结束: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                            + ", ready=" + KnowledgeEngineV2Service.IsSnapshotReady(knowledgeSeller)
                            + ", waitMs=" + warmWait.ElapsedMilliseconds);
                    }

                    if (!KnowledgeEngineV2Service.IsSnapshotReady(knowledgeSeller))
                    {
                        Log.Info("服务端OCR已完成，但Knowledge V2索引在等待窗口内仍未就绪；保留OCR缓存并继续视觉/文字兜底: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                            + ", buyer=" + task.BuyerNick);
                        return BuildVisualFallback(visualMatch, ocr, "Knowledge V2索引未就绪", imageKnowledgeMatch);
                    }

                    var evidenceText = ocr.Text;
                    if (imageKnowledgeMatch != null && !string.IsNullOrWhiteSpace(imageKnowledgeMatch.ContextText))
                        evidenceText += Environment.NewLine + imageKnowledgeMatch.ContextText;
                    if (visualMatch != null && !string.IsNullOrWhiteSpace(visualMatch.ContextText))
                        evidenceText += Environment.NewLine + visualMatch.ContextText;
                    var evidenceSubject = imageKnowledgeMatch != null
                        ? imageKnowledgeMatch.Description
                        : (visualMatch == null ? string.Empty : visualMatch.AppName);
                    decision = imageKnowledgeMatch == null
                        ? KnowledgeEngineV2Service.ResolveWithEvidence(
                            knowledgeSeller,
                            task.BuyerNick,
                            question,
                            evidenceText,
                            evidenceSubject)
                        : KnowledgeEngineV2Service.ResolveWithCuratedImageKnowledgeEvidence(
                            knowledgeSeller,
                            task.BuyerNick,
                            question,
                            evidenceText,
                            evidenceSubject);

                    // Structured orders always require the shared commerce authority.
                    // The only local exception is a knowledge record that explicitly opts
                    // into generic image+order evidence requirements and safe refund advice.
                    if (commerceAuthority != null && !commerceAuthority.AllowLocalTerminalReply)
                    {
                        if (imageKnowledgeMatch != null)
                            ConditionalBusinessAdviceService.TryBuild(
                                task.ShopKey, knowledgeSeller, task.BuyerNick,
                                commerceAuthority.CommerceContext, task.MessageKey,
                                imageKnowledgeMatch.KnowledgeId, out conditionalAdviceEvidence);

                        string conditionalReason;
                        if (conditionalAdviceEvidence == null
                            || !ConditionalBusinessAdviceService.TryAuthorizeRefundSuggestion(
                                decision, conditionalAdviceEvidence, out conditionalReason))
                        {
                            if (decision != null)
                            {
                                decision.CanDirectReply = false;
                                decision.Answer = string.Empty;
                            }
                            Log.Info("OCR图片命中后仍需Commerce上下文处理；条件性售后证据未通过，禁止V2提前直答: buyer="
                                + task.BuyerNick + ", reason="
                                + (conditionalAdviceEvidence == null
                                    ? "缺少同店同买家有效图片/已付款订单证据"
                                    : conditionalReason));
                        }
                        else
                        {
                            Log.Info("OCR图片命中并通过条件性售后建议门禁: buyer=" + task.BuyerNick
                                + ", orderRef=" + MyWebSocketServer.DiagnosticRef("order", conditionalAdviceEvidence.OrderId)
                                + ", adviceKey=" + decision.ConditionalAdviceKey);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("OCR-first知识库检索失败，继续视觉模型链路: " + ex.Message, 20);
                return BuildVisualFallback(visualMatch, ocr, "Knowledge V2检索异常", imageKnowledgeMatch);
            }
            // A saved image knowledge description is an operator-confirmed image-class
            // hint. It may authorize only an already-approved Knowledge V2 answer; the
            // description itself is never sent as a reply.
            if ((visualMatch == null && imageKnowledgeMatch == null) || decision == null || !decision.CanDirectReply
                || string.IsNullOrWhiteSpace(decision.Answer))
            {
                var contextAddon = CombineContext(
                    imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.ContextText,
                    visualMatch == null ? string.Empty : visualMatch.ContextText,
                    KnowledgeEngineV2RuntimeBridge.BuildContextPromptAddon(decision),
                    commerceAuthority == null ? string.Empty
                        : CommerceContextService.BuildPromptAddon(commerceAuthority.CommerceContext),
                    ConditionalBusinessAdviceService.BuildPromptAddon(conditionalAdviceEvidence, false));
                Log.Info((visualMatch == null && imageKnowledgeMatch == null
                    ? "OCR未命中图片知识/视觉预设，Knowledge V2仅作为检索证据，禁止提前直答"
                    : "图片知识/视觉理解已命中，但Knowledge V2未达到安全直答条件")
                    + "，携带OCR证据及可用知识上下文交给上游AI: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", contextAvailable=" + !string.IsNullOrWhiteSpace(contextAddon)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                if (string.IsNullOrWhiteSpace(contextAddon)) contextAddon = string.Empty;
                return new VisionRequestResult
                {
                    Success = false,
                    KnowledgeV2ContextPromptAddon = contextAddon,
                    VisualQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion) ? "[图片OCR]" : task.CombinedQuestion,
                    VisualSummary = imageKnowledgeMatch != null
                        ? imageKnowledgeMatch.Description
                        : (visualMatch == null
                            ? "未匹配到图片知识或视觉预设；OCR仅用于知识检索辅助证据"
                            : visualMatch.AppName + "/" + visualMatch.ScreenName),
                    VisualTags = imageKnowledgeMatch != null
                        ? "server-ocr,image-knowledge,knowledge-v2-evidence"
                        : (visualMatch == null
                            ? "server-ocr,knowledge-v2-evidence"
                            : "server-ocr,visual-understanding,knowledge-v2"),
                    MatchedVisualKnowledgeId = visualMatch == null ? string.Empty : visualMatch.RuleId,
                    MatchedImageKnowledgeId = imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.KnowledgeId,
                    ImageKnowledgeScore = imageKnowledgeMatch == null ? 0d : imageKnowledgeMatch.Score,
                    LocalOcrText = ocr.Text,
                    LocalOcrConfidence = ocr.Confidence,
                    LocalOcrLatencyMs = ocr.ElapsedMs,
                    LocalOcrCacheHit = ocr.CacheHit,
                    LocalOcrEngine = ocr.Engine
                };
            }

            var answer = BotMessageSuffixService.Apply(knowledgeSeller, decision.Answer);
            KnowledgeLearningService.RegisterAnswerSource(
                knowledgeSeller,
                task.BuyerNick,
                task.CombinedQuestion,
                answer,
                "服务端OCR+知识库V2");

            sw.Stop();
            Log.Info("OCR-first服务端OCR知识直答命中，未调用视觉API: seller=" + task.SellerNick
                + ", buyer=" + task.BuyerNick
                + ", confidence=" + ocr.Confidence.ToString("0.00")
                + ", cacheHit=" + ocr.CacheHit
                + ", lookupMs=" + decision.TotalMs
                + ", totalMs=" + sw.ElapsedMilliseconds);

            return new VisionRequestResult
            {
                Success = true,
                Answer = answer,
                Error = string.Empty,
                EndpointName = "server-ocr+knowledge-v2",
                VisionModel = "none",
                LatencyMs = sw.ElapsedMilliseconds,
                VisualQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion) ? "[图片OCR]" : task.CombinedQuestion,
                VisualSummary = imageKnowledgeMatch != null
                    ? imageKnowledgeMatch.Description
                    : visualMatch.AppName + "/" + visualMatch.ScreenName,
                VisualTags = imageKnowledgeMatch != null
                    ? "server-ocr,image-knowledge,knowledge-v2"
                    : "server-ocr,visual-understanding,knowledge-v2",
                MatchedVisualKnowledgeId = visualMatch == null ? string.Empty : "ocr-direct-knowledge-v2:" + visualMatch.RuleId,
                MatchedImageKnowledgeId = imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.KnowledgeId,
                ImageKnowledgeScore = imageKnowledgeMatch == null ? 0d : imageKnowledgeMatch.Score,
                VisualKnowledgeScore = decision.Matches == null || decision.Matches.Count < 1 ? 0d : decision.Matches.Max(x => x.Score),
                LocalOcrText = ocr.Text,
                LocalOcrConfidence = ocr.Confidence,
                LocalOcrLatencyMs = ocr.ElapsedMs,
                LocalOcrCacheHit = ocr.CacheHit,
                LocalOcrEngine = ocr.Engine,
                ConditionalAdviceKey = decision.ConditionalAdviceKey,
                ConditionalAdviceOrderId = decision.ConditionalAdviceOrderId,
                ConditionalAdviceItemId = decision.ConditionalAdviceItemId,
                ConditionalAdviceItemTitle = decision.ConditionalAdviceItemTitle,
                ConditionalAdviceSkuText = decision.ConditionalAdviceSkuText,
                ConditionalAdviceImageMessageKey = decision.ConditionalAdviceImageMessageKey,
                ConditionalAdviceImageKnowledgeId = decision.ConditionalAdviceImageKnowledgeId
            };
        }

        private static VisionRequestResult BuildVisualFallback(
            VisualUnderstandingMatch match, LocalOcrResult ocr, string reason,
            VisualImageKnowledgeMatch imageKnowledgeMatch = null)
        {
            return new VisionRequestResult
            {
                Success = false,
                Error = reason ?? string.Empty,
                KnowledgeV2ContextPromptAddon = CombineContext(
                    imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.ContextText,
                    match == null ? string.Empty : match.ContextText),
                VisualQuestion = match == null ? string.Empty : match.AppName + "/" + match.ScreenName,
                VisualSummary = imageKnowledgeMatch != null
                    ? imageKnowledgeMatch.Description : (match == null ? string.Empty : match.Description),
                VisualTags = imageKnowledgeMatch != null
                    ? "server-ocr,image-knowledge"
                    : (match == null ? "server-ocr" : "server-ocr,visual-understanding"),
                MatchedVisualKnowledgeId = match == null ? string.Empty : match.RuleId,
                MatchedImageKnowledgeId = imageKnowledgeMatch == null ? string.Empty : imageKnowledgeMatch.KnowledgeId,
                ImageKnowledgeScore = imageKnowledgeMatch == null ? 0d : imageKnowledgeMatch.Score,
                LocalOcrText = ocr == null ? string.Empty : ocr.Text,
                LocalOcrConfidence = ocr == null ? 0d : ocr.Confidence,
                LocalOcrLatencyMs = ocr == null ? 0L : ocr.ElapsedMs,
                LocalOcrCacheHit = ocr != null && ocr.CacheHit,
                LocalOcrEngine = ocr == null ? string.Empty : ocr.Engine
            };
        }

        private static string CombineContext(params string[] values)
        {
            return string.Join(Environment.NewLine,
                (values ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
        }

        private static bool IsHighConfidenceUsefulText(LocalOcrResult ocr)
        {
            if (ocr == null || !ocr.Success || ocr.Confidence < DirectKnowledgeMinOcrConfidence) return false;
            var text = (ocr.Text ?? string.Empty).Trim();
            if (text.Length < MinUsefulTextChars) return false;
            var semanticChars = text.Count(ch => IsCjk(ch) || char.IsLetter(ch));
            return semanticChars >= 2;
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= '\u3400' && ch <= '\u4DBF')
                || (ch >= '\u4E00' && ch <= '\u9FFF')
                || (ch >= '\uF900' && ch <= '\uFAFF');
        }
    }
}
