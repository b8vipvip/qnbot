using Bot.AssistWindow.Widget.Robot;
using BotLib;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Owns the real vision reply path so a buyer withdrawing an image affects only whether an
    /// answer is sent. The local image download and vision analysis continue with no cancellation,
    /// and the privacy-safe visual semantics are still persisted for later turns and learning.
    /// </summary>
    internal static class VisionWithdrawalAwarePipeline
    {
        private static readonly ConcurrentDictionary<int, bool> PatchedCoordinators =
            new ConcurrentDictionary<int, bool>();
        private static readonly VisionRequestService Vision = new VisionRequestService();
        private static Timer _patchTimer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            PatchExisting();
            // Run before the existing streaming/follow-up patch timers whenever possible. The
            // handler is also able to rebind a recent cached image itself, so wrapper order is safe.
            _patchTimer = new Timer(_ => PatchExisting(), null, 50, 200);
            Log.Info("买家图片撤回保护管线已启动：图片先完整缓存，撤回后继续识别但不发送旧回复。");
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try
                {
                    qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                }
                catch
                {
                    return;
                }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler", BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    if (PatchedCoordinators.ContainsKey(key)) continue;

                    var next = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (next == null) continue;
                    Func<BuyerMessageBurstLease, Task> wrapped = lease => HandleAsync(qn, next, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    PatchedCoordinators[key] = true;
                    Log.Info("已为客服实例启用图片撤回保护: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装图片撤回保护管线失败，将继续使用原视觉流程：" + ex.Message, 10);
            }
        }

        private static async Task HandleAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> next,
            BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null || burst == null || burst.Items == null || burst.Items.Count < 1)
            {
                await next(lease);
                return;
            }

            // Keep the authoritative generation-owning lease even when a synthetic cached-image
            // item is added below. The rebound lease is only a view over the augmented burst; it
            // must never detach Ready/Sending/Completed transitions from BuyerSessionAgent.
            var lifecycleLease = lease;
            var visionItem = burst.LatestVisionItem;
            if (visionItem == null)
            {
                BuyerMessageBurst rebound;
                if (!TryRebindRecentCachedImage(burst, out rebound))
                {
                    await next(lease);
                    return;
                }

                // Capture the original lease before replacing the local variable. Referencing the
                // reassigned local from the delegate would make IsCurrent call itself recursively.
                var sourceLease = lease;
                burst = rebound;
                lease = new BuyerMessageBurstLease(burst, () => sourceLease.IsCurrent);
                visionItem = burst.LatestVisionItem;
            }
            if (visionItem == null)
            {
                await next(lease);
                return;
            }

            await ProcessVisionAsync(qn, lease, lifecycleLease, visionItem);
        }

        private static async Task ProcessVisionAsync(
            QN qn,
            BuyerMessageBurstLease burstLease,
            BuyerMessageBurstLease lifecycleLease,
            BuyerMessageBurstItem visionItem)
        {
            var burst = burstLease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt,
                burst.ShopKey);

            // Calling Prime again is idempotent. It guarantees a cache task exists even for a
            // recovered/background image that bypassed the normal incoming-message hook.
            VisionImageCacheService.Prime(visionItem.Message, visionItem.DisplayText, burst.ShopKey, qn.CDP);
            var task = new VisionReplyTask
            {
                ShopKey = burst.ShopKey,
                SellerNick = burst.SellerNick,
                BuyerNick = burst.BuyerNick,
                MessageKey = visionItem.MessageKey,
                Message = visionItem.Message,
                BrowserClient = qn.CDP,
                CombinedQuestion = burst.CombinedQuestion,
                DeferLearningUntilDelivered = true
            };

            // Intentionally never link the analysis itself to the reply lease. New buyer messages,
            // withdrawal, or a human reply may make the outgoing answer stale, but must not cancel
            // image download/analysis or prevent visual_summary from being persisted.
            var result = await Vision.ExecuteAsync(task, CancellationToken.None);
            ApplyOcrTextToQuestionCard(ctl, burst.CombinedQuestion, result, detectedAt);
            var withdrawn = VisionImageCacheService.IsWithdrawn(
                burst.SellerNick,
                burst.BuyerNick,
                visionItem.MessageKey,
                visionItem.Message,
                burst.ShopKey);
            var hasFollowUpText = HasSubstantiveFollowUpText(burst, visionItem);
            var cacheComplete = VisionImageCacheService.HasCompleteCache(
                visionItem.MessageKey,
                visionItem.Message,
                burst.ShopKey);

            ReceptionWorkflowDecision workflowDecision = null;
            if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                if (withdrawn && hasFollowUpText && !cacheComplete && lifecycleLease.IsCurrent)
                {
                    await SendCacheMissFallbackAsync(qn, burstLease, lifecycleLease, ctl, detectedAt);
                    return;
                }

                // OCR failure is not a reason to bypass the active workflow. A failed image
                // may still satisfy image_message, while vision_observation/account_evidence
                // remain missing and can trigger the configured request for a clearer image.
                if (!withdrawn || hasFollowUpText)
                {
                    workflowDecision = ReceptionWorkflowRuntimeService.EvaluateVision(
                        burst, visionItem.MessageKey, string.Empty, withdrawn);
                    if (workflowDecision != null && workflowDecision.SuppressReply)
                    {
                        lifecycleLease.MarkCompleted("reception_workflow_" + (workflowDecision.Action ?? "wait").ToLowerInvariant());
                        if (ctl != null) ctl.SetSkipped(workflowDecision.Reason);
                        ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                        Log.Info("视觉识别失败后由Reception Agent状态机处理: seller=" + burst.SellerNick
                            + ", buyer=" + burst.BuyerNick + ", action=" + workflowDecision.Action);
                        return;
                    }
                    if (workflowDecision != null && workflowDecision.RequiresSend)
                    {
                        result.Answer = workflowDecision.ReplyText;
                        Log.Info("视觉识别失败，改用已发布流程的索证话术: seller="
                            + burst.SellerNick + ", buyer=" + burst.BuyerNick);
                    }
                    else
                    {
                        workflowDecision = null;
                    }
                }

                if (workflowDecision == null)
                {
                    var note = withdrawn && cacheComplete
                        ? "图片已撤回；后台识别未取得可用结果，但完整本地缓存仍保留供后续重试。"
                        : "已跳过：" + (string.IsNullOrWhiteSpace(result.Error) ? "视觉识别失败" : result.Error) + "，未向买家发送消息。";
                    ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, note, burst.ShopKey);
                    if (lifecycleLease.IsCurrent) lifecycleLease.MarkFailed("vision_processing_failed");
                    Log.Info("视觉消息后台处理完成但未回复: seller=" + burst.SellerNick
                        + ", buyer=" + burst.BuyerNick
                        + ", withdrawn=" + withdrawn
                        + ", cacheComplete=" + cacheComplete
                        + ", messageId=" + visionItem.MessageKey
                        + ", reason=" + result.Error);
                    return;
                }
            }

            result.Answer = ReplyTranscriptSanitizer.Sanitize(result.Answer);
            if (withdrawn && !hasFollowUpText)
            {
                if (ctl != null)
                {
                    ctl.SetProcessing("图片已撤回，但后台视觉分析已完成并缓存");
                    ctl.SetStatus("未发送：撤回只取消旧回复，图片语义已保存供后续对话使用", false);
                }
                Log.Info("撤回图片分析完成，结果已缓存且未主动回复: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick
                    + ", messageId=" + visionItem.MessageKey
                    + ", cacheComplete=" + cacheComplete
                    + ", summary=" + Short(result.VisualSummary, 140));
                if (lifecycleLease.IsCurrent) lifecycleLease.MarkCompleted("vision_withdrawn_analysis_completed");
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            if (!lifecycleLease.IsCurrent)
            {
                if (ctl != null)
                {
                    ctl.SetProcessing("图片分析已完成并缓存，最新消息或人工客服已接管");
                    ctl.SetStatus("未发送旧视觉答案；后续回复可继续使用本次图片分析结果", false);
                }
                Log.Info("视觉分析完成但本轮已失效，语义已缓存不发送旧答案: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick
                    + ", withdrawn=" + withdrawn
                    + ", summary=" + Short(result.VisualSummary, 140));
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }

            if (workflowDecision == null)
                workflowDecision = ReceptionWorkflowRuntimeService.EvaluateVision(
                    burst, visionItem.MessageKey, result.VisualSummary, withdrawn);
            if (workflowDecision != null && workflowDecision.SuppressReply)
            {
                lifecycleLease.MarkCompleted("reception_workflow_" + (workflowDecision.Action ?? "wait").ToLowerInvariant());
                if (ctl != null) ctl.SetSkipped(workflowDecision.Reason);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                Log.Info("Reception Agent视觉状态机抑制普通视觉回复: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", flow=" + workflowDecision.FlowId
                    + ", action=" + workflowDecision.Action);
                return;
            }
            if (workflowDecision != null && workflowDecision.RequiresSend)
                result.Answer = workflowDecision.ReplyText;

            // OCR-first direct knowledge has already passed Knowledge Engine V2's production
            // authority gate. Running the generic dedupe/validator without preserveTrustedAnswer
            // can launch a hidden structured-AI repair and turn a ~250ms local answer into a 20s+
            // generation. Preserve that authoritative answer while still applying the common AI
            // marker and generation cancellation contract. A workflow reply is separately grounded
            // in a published human-reply template and must not be treated as an OCR direct answer.
            var trustedOcrKnowledge = workflowDecision == null && string.Equals(
                result.MatchedVisualKnowledgeId,
                "ocr-direct-knowledge-v2",
                StringComparison.Ordinal);
            var deduplication = workflowDecision != null && workflowDecision.RequiresSend
                ? ReplyDeduplicationService.EnsureWorkflowReplyDistinct(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion,
                    result.Answer, burst.ShopKey, lifecycleLease.CancellationToken)
                : (trustedOcrKnowledge
                    ? ReplyDeduplicationService.EnsureDistinct(
                        burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion,
                        result.Answer, lifecycleLease.CancellationToken, true)
                    : ReplyDeduplicationService.EnsureDistinct(
                        burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, result.Answer));
            if (trustedOcrKnowledge)
            {
                Log.Info("OCR-first权威知识直答已跳过隐藏AI校验/重答，保持本地快速路径: seller="
                    + burst.SellerNick + ", buyer=" + burst.BuyerNick);
            }
            var answer = deduplication.Answer;
            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkFailed("vision_pre_send_validation_failed");
                if (ctl != null) ctl.SetStatus("发送前答案校验未通过，已阻止自动发送", false);
                ResponseProgressTracker.Fail(
                    burst.SellerNick, burst.BuyerNick,
                    "发送前答案校验未通过，已阻止自动发送", burst.ShopKey);
                return;
            }
            ShopContext visionShop = null;
            KugouReceptionConfig visionConfig = null;
            try
            {
                visionShop = ShopContextLocator.ResolveRuntimeByShopKey(burst.ShopKey);
                if (visionShop != null
                    && string.Equals(visionShop.SellerNick, burst.SellerNick, StringComparison.OrdinalIgnoreCase))
                    visionConfig = KugouReceptionConfigService.GetConfig(visionShop);
            }
            catch { }
            if (visionConfig != null && visionConfig.Enabled && !visionConfig.EnableAutoGuidance)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkCompleted("vision_auto_guidance_disabled");
                if (ctl != null) ctl.SetSkipped("本店已关闭自动视觉设备引导，图片仍可供人工核验");
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            string receptionBlock;
            var receptionBlocked = KugouReceptionPolicy.BlockOutgoing(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer,
                out receptionBlock, burst.ShopKey,
                workflowDecision != null && workflowDecision.RequiresSend);
            if (receptionBlocked)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkCompleted("vision_business_guard_suppressed");
                if (ctl != null) ctl.SetSkipped(receptionBlock);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            string humanReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                out humanReason, trustedOcrKnowledge, burst.ShopKey))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                Log.Info("图片OCR知识回复抑制: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", reviewedOcr=" + trustedOcrKnowledge
                    + ", reason=" + humanReason);
                lifecycleLease.MarkCompleted("vision_human_relevance_suppressed");
                if (ctl != null) ctl.SetSkipped(humanReason);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            if (!await lifecycleLease.ConfirmStableAsync(220))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                if (ctl != null) ctl.SetStatus("未发送：分析结果已缓存，但买家刚刚补充了新消息", false);
                return;
            }

            if (!lifecycleLease.MarkReady("vision_answer_materialized"))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                Log.Info("视觉答案已生成但generation无法进入Ready，已阻止迟到视觉答案发布: buyer=" + burst.BuyerNick);
                return;
            }

            var source = workflowDecision != null && workflowDecision.RequiresSend
                ? "Reception Agent"
                : (!string.IsNullOrWhiteSpace(result.MatchedVisualKnowledgeId)
                ? "视觉知识"
                : (deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                    ? deduplication.Source
                    : "AI生成"));
            var answerReadyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                source,
                detectedAt,
                answerReadyAt,
                burst.ShopKey);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);

            if (!autoSend)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkCompleted("vision_answer_generated_only");
                if (ctl != null) ctl.SetStatus("仅生成答案；图片语义已缓存", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkFailed("vision_answer_invalid");
                if (ctl != null) ctl.SetSendResult(false, "未发送：AI错误");
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            if (!lifecycleLease.IsCurrent)
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                if (ctl != null) ctl.SetSendResult(false, "未发送：最新买家消息或人工回复已接管");
                return;
            }
            if (workflowDecision != null && workflowDecision.RequiresSend
                && !ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                lifecycleLease.MarkCompleted("reception_workflow_revision_changed_before_vision_send");
                if (ctl != null) ctl.SetSendResult(false, "未发送：接待流程在发送前已更新或回滚，旧版本图片回复已取消");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick,
                    "未发送：接待流程在发送前已更新或回滚，旧版本图片回复已取消", burst.ShopKey);
                return;
            }

            if (!lifecycleLease.MarkSending("vision_send_started"))
            {
                ReceptionWorkflowRuntimeService.CancelDecision(workflowDecision);
                if (ctl != null) ctl.SetSendResult(false, "未发送：视觉generation在发送前已失效");
                return;
            }

            Func<bool> preSendGuard = () =>
            {
                if (!lifecycleLease.IsCurrent || lifecycleLease.CancellationToken.IsCancellationRequested)
                    return false;
                var latestWithdrawn = VisionImageCacheService.IsWithdrawn(
                    burst.SellerNick, burst.BuyerNick,
                    visionItem.MessageKey, visionItem.Message, burst.ShopKey);
                if (latestWithdrawn && !hasFollowUpText)
                {
                    Log.Info("图片回复等待发送资源期间收到撤回，已阻止仅基于撤回图片主动回复: messageId="
                        + visionItem.MessageKey);
                    return false;
                }
                string latestRelevanceReason;
                if (!ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestRelevanceReason, trustedOcrKnowledge, burst.ShopKey))
                {
                    Log.Info("图片回复等待发送资源期间会话相关性发生变化，已阻止发送: reason="
                        + latestRelevanceReason);
                    return false;
                }
                return workflowDecision == null || !workflowDecision.RequiresSend
                    || ReceptionWorkflowRuntimeService.IsDecisionCurrent(workflowDecision);
            };

            bool sendOk;
            try
            {
                sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken, preSendGuard);
            }
            catch (OperationCanceledException)
            {
                ReceptionWorkflowRuntimeService.CompleteDecision(workflowDecision, false);
                if (lifecycleLease.IsCurrent) lifecycleLease.MarkFailed("vision_send_cancelled");
                if (ctl != null) ctl.SetSendResult(false, "未发送：视觉generation已取消");
                return;
            }
            ReceptionWorkflowRuntimeService.CompleteDecision(workflowDecision, sendOk);
            if (sendOk)
            {
                if (workflowDecision != null && workflowDecision.RequiresSend)
                    ReplyDeduplicationService.RememberDelivered(
                        burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
                else
                    ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                if (workflowDecision == null && string.IsNullOrWhiteSpace(result.MatchedVisualKnowledgeId))
                {
                    KnowledgeLearningService.QueueLearn(
                        burst.CombinedQuestion,
                        answer,
                        "视觉AI",
                        burst.SellerNick,
                        burst.BuyerNick);
                }
                lifecycleLease.MarkCompleted("vision_send_completed");
            }
            else
            {
                lifecycleLease.MarkFailed("vision_send_failed");
            }
            if (ctl != null)
            {
                ctl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（使用本地完整图片缓存，合并图片与最新提问）"
                        : "识别完成，但目标买家会话未确认，未发送。原因："
                            + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
            }
            Log.Info("图片撤回保护视觉流程完成: buyer=" + burst.BuyerNick
                + ", withdrawn=" + withdrawn
                + ", followUp=" + hasFollowUpText
                + ", cacheComplete=" + cacheComplete
                + ", success=" + sendOk);
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
        }

        private static async Task SendCacheMissFallbackAsync(
            QN qn,
            BuyerMessageBurstLease burstLease,
            BuyerMessageBurstLease lifecycleLease,
            CtlConversation ctl,
            DateTime detectedAt)
        {
            var burst = burstLease.Burst;
            var answer = BotFeatureStore.ApplyOutputPolicy(
                "刚才图片已撤回且未能完整保存，请重新发送清晰图片后我再确认。");
            if (!await lifecycleLease.ConfirmStableAsync(180)) return;
            if (!lifecycleLease.MarkReady("vision_cache_miss_fallback_ready")) return;

            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                "本地图片缓存兜底",
                detectedAt,
                DateTime.Now,
                burst.ShopKey);
            if (!Params.Robot.GetIsAutoReply())
            {
                lifecycleLease.MarkCompleted("vision_cache_miss_fallback_generated_only");
                if (ctl != null) ctl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
                return;
            }
            if (!lifecycleLease.MarkSending("vision_cache_miss_fallback_send_started")) return;

            // The fallback is still an automatic buyer-facing send. Recheck the exact
            // conversation immediately before every transport attempt so a human reply,
            // newer buyer message, or expired generation cannot be overwritten.
            Func<bool> preSendGuard = () =>
            {
                if (!lifecycleLease.IsCurrent || lifecycleLease.CancellationToken.IsCancellationRequested)
                    return false;
                string latestRelevanceReason;
                var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestRelevanceReason, false, burst.ShopKey);
                if (!shouldSend)
                    Log.Info("图片缓存兜底发送前会话相关性已变化，阻止旧兜底回复: reason="
                        + latestRelevanceReason + ", buyer=" + burst.BuyerNick);
                return shouldSend;
            };

            bool sent;
            try
            {
                sent = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken, preSendGuard);
            }
            catch (OperationCanceledException)
            {
                if (lifecycleLease.IsCurrent) lifecycleLease.MarkFailed("vision_cache_miss_fallback_cancelled");
                return;
            }
            if (sent)
                lifecycleLease.MarkCompleted("vision_cache_miss_fallback_sent");
            else
                lifecycleLease.MarkFailed("vision_cache_miss_fallback_failed");
            if (ctl != null) ctl.SetSendResult(sent, sent ? "已发送：图片未完整缓存，需要买家重发" : "发送失败");
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
        }

        private static bool TryRebindRecentCachedImage(
            BuyerMessageBurst burst,
            out BuyerMessageBurst rebound)
        {
            rebound = null;
            if (burst == null || burst.Items == null || burst.Items.Count < 1) return false;

            VisionCachedImageReference recent;
            if (!VisionImageCacheService.TryGetRecentReference(
                burst.SellerNick,
                burst.BuyerNick,
                TimeSpan.FromSeconds(VisionImageCacheService.RecentReferenceWindowSeconds),
                out recent,
                burst.ShopKey))
            {
                return false;
            }

            var latestAt = burst.Items
                .Where(x => x != null)
                .Select(x => x.ReceivedAt == DateTime.MinValue ? DateTime.Now : x.ReceivedAt)
                .DefaultIfEmpty(DateTime.Now)
                .Max();
            var elapsed = latestAt - recent.ObservedAt;
            if (elapsed < TimeSpan.Zero
                || elapsed > TimeSpan.FromSeconds(VisionImageCacheService.RecentReferenceWindowSeconds))
            {
                return false;
            }

            var question = burst.CombinedQuestion ?? string.Empty;
            if (!ShouldBindToRecentImage(question, elapsed)) return false;

            // The cached image contributes semantics, not a fresh buyer receive timestamp. Keep its
            // source SortValue for visual ordering, but anchor the synthetic clone to the first
            // message of the current follow-up burst so progress/slow-response metrics are measured
            // from the actual follow-up instead of the historical image.
            var responseAnchorAt = burst.Items
                .Where(x => x != null)
                .Select(x => x.ReceivedAt == DateTime.MinValue ? DateTime.Now : x.ReceivedAt)
                .DefaultIfEmpty(DateTime.Now)
                .Min();
            var synthetic = new BuyerMessageBurstItem
            {
                ShopKey = burst.ShopKey,
                SellerNick = burst.SellerNick,
                BuyerNick = burst.BuyerNick,
                MessageKey = recent.MessageKey,
                DisplayText = "[图片]",
                Message = recent.Message,
                VisionDecision = new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Vision,
                    QuestionLabel = "[图片]",
                    Note = string.Empty
                },
                SortValue = recent.ObservedAt.Ticks,
                ReceivedAt = responseAnchorAt
            };
            var items = new List<BuyerMessageBurstItem> { synthetic };
            items.AddRange(burst.Items.Where(x => x != null));
            rebound = new BuyerMessageBurst(
                burst.SellerNick,
                burst.BuyerNick,
                items,
                burst.Version);
            Log.Info("最新文字已绑定本地图片缓存: seller=" + burst.SellerNick
                + ", buyer=" + burst.BuyerNick
                + ", elapsedMs=" + Math.Max(0, (long)elapsed.TotalMilliseconds)
                + ", responseAnchorAt=" + responseAnchorAt.ToString("HH:mm:ss.fff")
                + ", withdrawn=" + recent.Withdrawn
                + ", cacheComplete=" + recent.CacheComplete
                + ", question=" + Short(question, 100));
            return true;
        }

        private static void ApplyOcrTextToQuestionCard(
            CtlConversation control,
            string originalQuestion,
            VisionRequestResult result,
            DateTime detectedAt)
        {
            if (control == null || result == null || string.IsNullOrWhiteSpace(result.LocalOcrText))
                return;

            var recognizedText = Short(result.LocalOcrText, 800);
            if (string.IsNullOrWhiteSpace(recognizedText)) return;

            var label = result.LocalOcrCacheHit ? "服务端OCR缓存命中" : "服务端OCR识别";
            if (!string.IsNullOrWhiteSpace(result.LocalOcrEngine))
                label += "（" + Short(result.LocalOcrEngine, 32) + "）";
            if (result.LocalOcrConfidence > 0d)
                label += "，置信度=" + result.LocalOcrConfidence.ToString("0.00");

            var question = string.IsNullOrWhiteSpace(originalQuestion)
                ? "[图片]"
                : originalQuestion.Trim();
            // This is a UI-only annotation. The canonical buyer question, message IDs,
            // learning evidence and dedupe keys remain the original native message values.
            if (question.IndexOf("服务端OCR识别", StringComparison.Ordinal) >= 0
                || question.IndexOf("服务端OCR缓存命中", StringComparison.Ordinal) >= 0)
                return;
            control.SetQuestion(question + Environment.NewLine + label + "：" + recognizedText, detectedAt);
        }

        private static bool ShouldBindToRecentImage(string text, TimeSpan elapsed)
        {
            var compact = Normalize(text);
            if (string.IsNullOrWhiteSpace(compact) || compact.Length > 90) return false;
            if (IsAcknowledgement(compact)) return false;

            // One authority only: recent-image rebinding is opt-in and must be explicitly deictic.
            // The old fallback treated generic words such as “充值/能充/可以” within 20 seconds as
            // image captions, which could silently drag phone numbers or recharge-status turns back
            // onto the previous screenshot. Keep the elapsed parameter for API compatibility, but
            // never infer image reference from timing alone.
            return VisionFollowUpContextPipeline.IsVisionReferentialFollowUp(text);
        }

        private static bool HasSubstantiveFollowUpText(
            BuyerMessageBurst burst,
            BuyerMessageBurstItem visionItem)
        {
            return burst.Items.Any(x => x != null
                && !ReferenceEquals(x, visionItem)
                && x.VisionDecision != null
                && x.VisionDecision.Kind == VisionDecisionKind.Text
                && !IncomingMessageSafety.IsMediaPlaceholder(x.DisplayText)
                && !string.IsNullOrWhiteSpace(x.DisplayText));
        }

        private static bool IsAcknowledgement(string text)
        {
            return text == "好"
                || text == "好的"
                || text == "嗯"
                || text == "哦"
                || text == "知道了"
                || text == "明白了"
                || text == "谢谢"
                || text == "ok"
                || text == "收到";
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+",
                string.Empty);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
