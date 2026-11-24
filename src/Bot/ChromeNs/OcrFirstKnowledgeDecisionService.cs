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
                var visionEndpoints = AiEndpointStore.GetVisionEnabledEndpoints();
                if (visionEndpoints != null && visionEndpoints.Count > 0)
                {
                    Log.Info("视觉理解优先级=AI接口优先，跳过OCR+知识库提前直答: seller=" + task.SellerNick
                        + ", buyer=" + task.BuyerNick
                        + ", visionEndpoints=" + visionEndpoints.Count);
                    return null;
                }

                Log.Info("视觉理解优先级=AI接口优先，但未配置可用视觉模型，允许OCR+知识库兜底: seller="
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

            VisualUnderstandingMatch visualMatch;
            try
            {
                if (!VisualUnderstandingRuleService.TryMatch(task.ShopKey, knowledgeSeller, ocr.Text, out visualMatch))
                {
                    Log.Info("OCR 未命中本店视觉理解预设，转交上游视觉 AI: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("视觉理解规则匹配失败，转交上游视觉 AI: " + ex.Message, 10);
                return null;
            }

            if (shop == null || knowledgeSeller.Length == 0)
            {
                Log.ErrorWithMaxCount("OCR已完成，但无法通过权威ShopKey解析Knowledge V2店铺，禁止跨店检索。", 20);
                return null;
            }

            KnowledgeV2Decision decision;
            try
            {
                // Message display nicknames can be aliases. Knowledge V2 must use the seller
                // identity owned by the ShopKey, never the untrusted message.toid.nick value.
                using (ShopSettingsScope.Enter(shop, false))
                {
                    var timeline = ConversationContextStore.BuildTimelineText(
                        knowledgeSeller, task.BuyerNick, task.CombinedQuestion, 12);
                    var question = BuildKnowledgeQuestion(task.CombinedQuestion, timeline, ocr.Text + Environment.NewLine + visualMatch.ContextText);
                    if (!KnowledgeEngineV2Service.IsEnabled(knowledgeSeller))
                    {
                        Log.Info("OCR已完成，但本店Knowledge V2未启用，继续视觉/文字兜底: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", task.ShopKey));
                        return BuildVisualFallback(visualMatch, ocr, "Knowledge V2未启用");
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
                        return BuildVisualFallback(visualMatch, ocr, "Knowledge V2索引未就绪");
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
                Log.ErrorWithMaxCount("OCR-first知识库检索失败，继续视觉模型链路: " + ex.Message, 20);
                return BuildVisualFallback(visualMatch, ocr, "Knowledge V2检索异常");
            }
            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                var contextAddon = KnowledgeEngineV2RuntimeBridge.BuildContextPromptAddon(decision);
                Log.Info("视觉理解已命中，但Knowledge V2未达到安全直答条件，携带结构化图片事实交给上游AI: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", contextAvailable=" + !string.IsNullOrWhiteSpace(contextAddon)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                if (string.IsNullOrWhiteSpace(contextAddon)) contextAddon = string.Empty;
                contextAddon = visualMatch.ContextText
                    + (string.IsNullOrWhiteSpace(contextAddon) ? string.Empty : Environment.NewLine + contextAddon);
                return new VisionRequestResult
                {
                    Success = false,
                    KnowledgeV2ContextPromptAddon = contextAddon,
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
                VisualSummary = visualMatch.AppName + "/" + visualMatch.ScreenName,
                VisualTags = "server-ocr,visual-understanding,knowledge-v2",
                MatchedVisualKnowledgeId = "ocr-direct-knowledge-v2:" + visualMatch.RuleId,
                VisualKnowledgeScore = decision.Matches == null || decision.Matches.Count < 1 ? 0d : decision.Matches.Max(x => x.Score),
                LocalOcrText = ocr.Text,
                LocalOcrConfidence = ocr.Confidence,
                LocalOcrLatencyMs = ocr.ElapsedMs,
                LocalOcrCacheHit = ocr.CacheHit,
                LocalOcrEngine = ocr.Engine
            };
        }

        private static VisionRequestResult BuildVisualFallback(
            VisualUnderstandingMatch match, LocalOcrResult ocr, string reason)
        {
            return new VisionRequestResult
            {
                Success = false,
                Error = reason ?? string.Empty,
                KnowledgeV2ContextPromptAddon = match == null ? string.Empty : match.ContextText,
                VisualQuestion = match == null ? string.Empty : match.AppName + "/" + match.ScreenName,
                VisualSummary = match == null ? string.Empty : match.Description,
                VisualTags = "server-ocr,visual-understanding",
                MatchedVisualKnowledgeId = match == null ? string.Empty : match.RuleId,
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

        private static string BuildKnowledgeQuestion(string combinedQuestion, string timeline, string ocrText)
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
