using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// OCR-first local decision layer for image messages. It resolves the already-cached image,
    /// calls the authenticated server OCR control-plane endpoint and only allows a direct reply
    /// when OCR confidence is high AND Knowledge Engine V2 independently passes its existing
    /// CanDirectReply safety gate. When the server selects AI-first and a usable vision endpoint
    /// exists, this early direct-reply layer yields immediately so visual AI remains the primary
    /// decision source; OCR can still be attached later as auxiliary evidence by the vision path.
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
                // The configured AI-first preference remains the fallback route, but it must
                // not bypass an explicitly configured deterministic visual-understanding rule.
                // OCR + preset matching runs first; only a preset miss proceeds to upstream AI.
                Log.Info("视觉理解优先级=AI优先；先执行本店视觉理解预设，未命中再调用上游AI: seller="
                    + task.SellerNick + ", buyer=" + task.BuyerNick);
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
                    task.BuyerNick,
                    task.MessageKey,
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
            }
            if (!IsHighConfidenceUsefulText(ocr))
            {
                Log.Info("OCR-first未满足本地知识直答阈值，继续视觉模型链路: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", confidence=" + (ocr == null ? 0d : ocr.Confidence).ToString("0.00")
                    + ", chars=" + (ocr == null || ocr.Text == null ? 0 : ocr.Text.Trim().Length));
                return null;
            }

            if (shop == null || knowledgeSeller.Length == 0)
            {
                Log.ErrorWithMaxCount("OCR已完成，但无法通过权威ShopKey解析视觉理解/Knowledge V2店铺，禁止跨店检索。", 20);
                return null;
            }

            VisualUnderstandingMatch visualMatch;
            string visualMatchReason;
            if (!VisualUnderstandingRuleService.TryMatch(
                task.ShopKey, knowledgeSeller, ocr.Text, out visualMatch, out visualMatchReason))
            {
                Log.Info("图片未命中本店视觉理解预设，交给上游AI: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                    + ", reason=" + visualMatchReason);
                return null;
            }
            var visualContext = visualMatch.BuildContextText();
            Log.Info("图片已命中本店视觉理解预设，开始结合当前买家上下文检索知识: ruleId="
                + visualMatch.RuleId + ", fields=" + (visualMatch.Fields == null ? 0 : visualMatch.Fields.Count));

            KnowledgeV2Decision decision;
            try
            {
                // Message display nicknames can be aliases. Knowledge V2 must use the seller
                // identity owned by the ShopKey, never the untrusted message.toid.nick value.
                using (ShopSettingsScope.Enter(shop, false))
                {
                    var timeline = ConversationContextStore.BuildTimelineText(
                        knowledgeSeller, task.BuyerNick, task.CombinedQuestion, 12);
                    var question = BuildKnowledgeQuestion(task.CombinedQuestion, timeline, ocr.Text, visualContext);
                    if (!KnowledgeEngineV2Service.IsEnabled(knowledgeSeller))
                    {
                        Log.Info("视觉理解预设已命中，但本店Knowledge V2未启用；将结构化字段交给上游AI: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                        return BuildVisualFallback(task, ocr, visualMatch, visualContext, string.Empty);
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
                        Log.Info("视觉理解预设已命中，但Knowledge V2索引等待超时；保留结构化字段并交给上游AI: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey)
                            + ", buyer=" + task.BuyerNick);
                        return BuildVisualFallback(task, ocr, visualMatch, visualContext, string.Empty);
                    }

                    decision = KnowledgeEngineV2Service.Resolve(knowledgeSeller, task.BuyerNick, question);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("视觉理解已命中，但Knowledge V2检索失败；保留本轮结构化字段并继续上游AI: " + ex.Message, 20);
                return BuildVisualFallback(task, ocr, visualMatch, visualContext, string.Empty);
            }
            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                var contextAddon = KnowledgeEngineV2RuntimeBridge.BuildContextPromptAddon(decision);
                Log.Info("视觉理解规则已命中，但Knowledge V2未达到安全直答条件，继续上游AI: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", knowledgeContextAvailable=" + !string.IsNullOrWhiteSpace(contextAddon)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                return BuildVisualFallback(task, ocr, visualMatch, visualContext, contextAddon);
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
                VisualSummary = visualMatch.VisualSummary,
                VisualTags = visualMatch.VisualTags,
                MatchedVisualKnowledgeId = "visual-rule:" + visualMatch.RuleId,
                VisualKnowledgeScore = decision.Matches == null || decision.Matches.Count < 1 ? 0d : decision.Matches.Max(x => x.Score),
                LocalOcrText = ocr.Text,
                LocalOcrConfidence = ocr.Confidence,
                LocalOcrLatencyMs = ocr.ElapsedMs,
                LocalOcrCacheHit = ocr.CacheHit,
                LocalOcrEngine = ocr.Engine
            };
        }

        private static VisionRequestResult BuildVisualFallback(
            VisionReplyTask task,
            LocalOcrResult ocr,
            VisualUnderstandingMatch visualMatch,
            string visualContext,
            string knowledgeContext)
        {
            var addon = (visualContext ?? string.Empty).Trim();
            knowledgeContext = (knowledgeContext ?? string.Empty).Trim();
            if (knowledgeContext.Length > 0)
                addon = addon + Environment.NewLine + Environment.NewLine + knowledgeContext;
            return new VisionRequestResult
            {
                Success = false,
                KnowledgeV2ContextPromptAddon = addon,
                VisualQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion) ? "[图片]" : task.CombinedQuestion,
                VisualSummary = visualMatch == null ? string.Empty : visualMatch.VisualSummary,
                VisualTags = visualMatch == null ? string.Empty : visualMatch.VisualTags,
                MatchedVisualKnowledgeId = visualMatch == null ? string.Empty : "visual-rule:" + visualMatch.RuleId,
                VisualKnowledgeScore = visualMatch == null ? 0d : visualMatch.Score,
                LocalOcrText = ocr == null ? string.Empty : ocr.Text,
                LocalOcrConfidence = ocr == null ? 0d : ocr.Confidence,
                LocalOcrLatencyMs = ocr == null ? 0L : ocr.ElapsedMs,
                LocalOcrCacheHit = ocr != null && ocr.CacheHit,
                LocalOcrEngine = ocr == null ? string.Empty : ocr.Engine
            };
        }

        private static bool IsHighConfidenceUsefulText(LocalOcrResult ocr)
        {
            if (ocr == null || !ocr.Success || ocr.Confidence < DirectKnowledgeMinOcrConfidence) return false;
            var text = (ocr.Text ?? string.Empty).Trim();
            if (text.Length < MinUsefulTextChars) return false;
            var semanticChars = text.Count(ch => IsCjk(ch) || char.IsLetter(ch));
            return semanticChars >= 2;
        }

        private static string BuildKnowledgeQuestion(string combinedQuestion, string timeline, string ocrText, string visualContext)
        {
            var sb = new StringBuilder();
            var question = (combinedQuestion ?? string.Empty).Trim();
            if (question.Length > 0 && !string.Equals(question, "[图片]", StringComparison.Ordinal))
            {
                sb.AppendLine("买家本轮文字：").AppendLine(question);
            }

            timeline = (timeline ?? string.Empty).Trim();
            if (timeline.Length > 3000) timeline = timeline.Substring(timeline.Length - 3000);
            if (timeline.Length > 0)
            {
                sb.AppendLine("最近客服与买家对话（用于理解图片提交原因）：")
                    .AppendLine(timeline);
            }

            if (!string.IsNullOrWhiteSpace(visualContext))
            {
                sb.AppendLine("本店视觉理解预设提取的结构化结果（当前买家专属）：")
                    .AppendLine(visualContext);
            }

            sb.AppendLine("图片OCR文字（仅为文字证据）：")
                .Append((ocrText ?? string.Empty).Trim());
            return sb.ToString().Trim();
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= '\u3400' && ch <= '\u4DBF')
                || (ch >= '\u4E00' && ch <= '\u9FFF')
                || (ch >= '\uF900' && ch <= '\uFAFF');
        }
    }
}
