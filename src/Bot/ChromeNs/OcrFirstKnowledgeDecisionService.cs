using Bot.Knowledge;
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

        public static async Task<VisionRequestResult> TryResolveAsync(
            VisionReplyTask task,
            CancellationToken cancellationToken)
        {
            if (task == null || task.Message == null) return null;
            if (!KnowledgeEngineV2Service.IsEnabled(task.SellerNick)
                || !KnowledgeEngineV2Service.IsSnapshotReady(task.SellerNick))
            {
                return null;
            }

            var priority = await VisionOcrPriorityService.ResolveAsync(task.SellerNick, cancellationToken).ConfigureAwait(false);
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
            VisionImageResult image;
            try
            {
                image = await new VisionImageResolver().ResolveAsync(task.Message, null, cancellationToken);
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
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("OCR-first服务端OCR失败，继续视觉模型链路: " + ex.Message, 20);
                return null;
            }
            if (!IsHighConfidenceUsefulText(ocr))
            {
                Log.Info("OCR-first未满足本地知识直答阈值，继续视觉模型链路: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", confidence=" + (ocr == null ? 0d : ocr.Confidence).ToString("0.00")
                    + ", chars=" + (ocr == null || ocr.Text == null ? 0 : ocr.Text.Trim().Length));
                return null;
            }

            var question = BuildKnowledgeQuestion(task.CombinedQuestion, ocr.Text);
            KnowledgeV2Decision decision;
            try
            {
                decision = KnowledgeEngineV2Service.Resolve(task.SellerNick, task.BuyerNick, question);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("OCR-first知识库检索失败，继续视觉模型链路: " + ex.Message, 20);
                return null;
            }
            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                Log.Info("OCR-first知识库未达到安全直答条件，继续视觉模型链路: seller=" + task.SellerNick
                    + ", buyer=" + task.BuyerNick
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                return null;
            }

            var answer = BotMessageSuffixService.Apply(task.SellerNick, decision.Answer);
            KnowledgeLearningService.RegisterAnswerSource(
                task.SellerNick,
                task.BuyerNick,
                task.CombinedQuestion,
                answer,
                "本地OCR+知识库V2");

            sw.Stop();
            Log.Info("OCR-first本地知识直答命中，未调用视觉API: seller=" + task.SellerNick
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
                VisualSummary = string.Empty,
                VisualTags = "server-ocr,knowledge-v2",
                MatchedVisualKnowledgeId = "ocr-direct-knowledge-v2",
                VisualKnowledgeScore = decision.Matches == null || decision.Matches.Count < 1 ? 0d : decision.Matches.Max(x => x.Score),
                LocalOcrText = ocr.Text,
                LocalOcrConfidence = ocr.Confidence,
                LocalOcrLatencyMs = ocr.ElapsedMs,
                LocalOcrCacheHit = ocr.CacheHit,
                LocalOcrEngine = ocr.Engine
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

        private static string BuildKnowledgeQuestion(string combinedQuestion, string ocrText)
        {
            var sb = new StringBuilder();
            var question = (combinedQuestion ?? string.Empty).Trim();
            if (question.Length > 0 && !string.Equals(question, "[图片]", StringComparison.Ordinal))
            {
                sb.AppendLine(question);
            }
            sb.Append((ocrText ?? string.Empty).Trim());
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
