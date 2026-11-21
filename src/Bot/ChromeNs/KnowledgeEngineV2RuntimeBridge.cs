using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2RuntimeBridge
    {
        private static readonly ConcurrentDictionary<string, byte> PreparedSellers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> WarmingSellers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                try { KnowledgeCenterV2UiBridge.Initialize(); } catch { }
                try { KnowledgeV2QualityUiBridge.Initialize(); } catch { }
                try { KnowledgeEngineV2FeedbackBridge.Initialize(); } catch { }
                StopLegacyMemoryTimer();
                Log.Info("Knowledge Engine V2已启动：回复主流程显式调用V2决策，不再与Smart Reply争抢反射包装顺序；SQLite知识库、结构化索引、Working Memory和反馈质量闭环已启用。");
            }
            return new object();
        }

        private static void StopLegacyMemoryTimer()
        {
            try
            {
                var type = typeof(KnowledgeMemoryRuntimeBridge);
                var timerField = type.GetField("_timer", BindingFlags.Static | BindingFlags.NonPublic);
                var timer = timerField == null ? null : timerField.GetValue(null) as Timer;
                if (timer == null) return;
                try { timer.Dispose(); } catch { }
                timerField.SetValue(null, null);
            }
            catch { }
        }

        private static void PrepareSeller(string seller)
        {
            if (!PreparedSellers.TryAdd(seller, 1)) return;
            try
            {
                KnowledgeMemoryEngine.SetEnabled(seller, false);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("关闭Knowledge Memory v1店铺开关失败: seller=" + seller + ", error=" + ex.Message, 10);
            }
            QueueWarm(seller);
        }

        private static void QueueWarm(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0 || KnowledgeEngineV2Service.IsSnapshotReady(seller)) return;
            if (!WarmingSellers.TryAdd(seller, 1)) return;
            Task.Run(() =>
            {
                try
                {
                    KnowledgeEngineV2Service.Warm(seller);
                    var stats = KnowledgeEngineV2Service.GetStats(seller);
                    Log.Info("Knowledge Engine V2预热完成: seller=" + seller
                        + ", records=" + stats.Total
                        + ", conflicts=" + stats.Conflicts
                        + ", snapshot=" + stats.SnapshotBuiltAt.ToString("HH:mm:ss.fff"));
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Knowledge Engine V2后台预热失败: seller=" + seller + ", error=" + ex.Message, 10);
                }
                finally
                {
                    byte ignored;
                    WarmingSellers.TryRemove(seller, out ignored);
                }
            });
        }

        internal static async Task<bool> TryHandleAsync(QN qn, BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null || burst == null || burst.Items.Count < 1
                || burst.LatestVisionItem != null || !burst.HasReplyableItem)
                return false;
            if (!KnowledgeEngineV2Service.IsEnabled(burst.SellerNick))
                return false;

            // Prepare each seller once: disable the retired v1 runtime flag and warm
            // this exact shop's V2 snapshot before any local direct decision.
            PrepareSeller(burst.SellerNick);

            // V2 is only a fast terminal path when the shared commerce authority allows local
            // termination. The same authority is consumed by Smart Reply, so verified orders and
            // strongly post-order conversations cannot be decided differently by parallel layers.
            var commerceAuthority = CommerceReplyAuthorityService.Evaluate(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion);
            if (!commerceAuthority.AllowLocalTerminalReply)
            {
                var commerce = commerceAuthority.CommerceContext;
                Log.Info("Knowledge Engine V2已让出终态直答权，交给Commerce Smart Reply: buyer="
                    + burst.BuyerNick
                    + ", phase=" + (commerce == null ? string.Empty : commerce.PurchasePhase)
                    + ", structured=" + (commerce != null && commerce.HasStructuredOrder)
                    + ", reason=" + commerceAuthority.Reason);
                return false;
            }

            if (!KnowledgeEngineV2Service.IsSnapshotReady(burst.SellerNick))
            {
                QueueWarm(burst.SellerNick);
                Log.Info("Knowledge Engine V2索引尚未就绪，本轮不阻塞买家消息，直接继续兼容回复链路: buyer="
                    + burst.BuyerNick);
                return false;
            }

            KnowledgeV2Decision decision;
            try
            {
                decision = KnowledgeEngineV2Service.Resolve(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge Engine V2检索失败，回退兼容链路: buyer="
                    + burst.BuyerNick + ", error=" + ex.Message, 20);
                return false;
            }

            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                Log.Info("Knowledge Engine V2未直答: buyer=" + burst.BuyerNick
                    + ", totalMs=" + (decision == null ? 0 : decision.TotalMs)
                    + ", candidates=" + (decision == null ? 0 : decision.CandidateCount)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                return false;
            }

            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();

            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                Log.Info("Knowledge Engine V2迟到结果已丢弃，generation已失效，未进入答案就绪/发送: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return true;
            }
            if (autoSend && !await lease.ConfirmStableAsync(80))
            {
                Log.Info("Knowledge Engine V2发送前稳定性确认失败，未发布迟到答案: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return true;
            }
            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                Log.Info("Knowledge Engine V2稳定性确认后generation失效，未发布答案: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return true;
            }

            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt);
            var answer = BotMessageSuffixService.Apply(burst.SellerNick, decision.Answer);
            ReplyDeduplicationResult dedup;
            try
            {
                dedup = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    lease.CancellationToken,
                    true);
            }
            catch (OperationCanceledException)
            {
                Log.Info("Knowledge Engine V2答案发布前generation已终止，权威本地答案未进入UI/发送: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return true;
            }
            answer = dedup.Answer;
            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                || !lease.MarkReady("knowledge_v2_answer_materialized"))
            {
                Log.Info("Knowledge Engine V2答案完成后generation已终止，迟到本地答案已丢弃: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return true;
            }
            var best = decision.Matches.FirstOrDefault(IsApprovedMatchForLogging);
            // Preserve the actual knowledge record selected for the direct reply.
            // A generic "本地知识V2" label cannot open the right editor later.
            var sourceLabel = best != null && best.Record != null
                && !string.IsNullOrWhiteSpace(best.Record.Id)
                    ? "本地知识V2（记录ID：" + best.Record.Id + "）"
                    : "本地知识V2（未记录知识ID）";
            var readyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                sourceLabel,
                detectedAt,
                readyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);

            Log.Info("Knowledge Engine V2本地答案已就绪: buyer=" + burst.BuyerNick
                + ", score=" + (best == null ? "-" : best.Score.ToString("0.00"))
                + ", predicate=" + (decision.Query == null ? "" : decision.Query.Predicate)
                + ", parseMs=" + decision.ParseMs
                + ", recallMs=" + decision.RecallMs
                + ", rankMs=" + decision.RankMs
                + ", totalLookupMs=" + decision.TotalMs
                + ", totalToAnswerMs=" + Math.Max(0, (long)(readyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                lease.MarkCompleted("knowledge_v2_answer_generated_only");
                if (ctl != null) ctl.SetStatus("仅生成答案（Knowledge Engine V2本地命中）", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return true;
            }

            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：任务已被人工接管或显式取消");
                return true;
            }

            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, out relevanceReason))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：" + relevanceReason);
                Log.Info("Knowledge Engine V2发送前被并发相关性门控抑制: buyer="
                    + burst.BuyerNick + ", reason=" + relevanceReason);
                return true;
            }

            if (!lease.MarkSending("knowledge_v2_send_started"))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在V2发送前已失效");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge V2发送前generation已失效");
                return true;
            }

            bool sendOk;
            try
            {
                sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lease.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在等待发送资源/会话确认期间已失效");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge V2发送期间generation已失效");
                Log.Info("Knowledge Engine V2发送期间generation硬失效，已停止后续UI发送副作用: buyer="
                    + burst.BuyerNick);
                return true;
            }
            var failureReason = sendOk || qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason();
            if (sendOk)
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
            try
            {
                if (best != null && best.Record != null && !string.IsNullOrWhiteSpace(best.Record.Id))
                {
                    KnowledgeEngineV2FeedbackService.RecordDirectSend(
                        burst.SellerNick,
                        burst.BuyerNick,
                        best.Record.Id,
                        burst.CombinedQuestion,
                        answer,
                        sendOk,
                        failureReason);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("记录Knowledge V2发送反馈失败: " + ex.Message, 10);
            }
            if (ctl != null)
            {
                ctl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（Knowledge Engine V2本地直答，无AI调用）"
                        : "发送失败：" + failureReason);
            }
            if (sendOk)
                lease.MarkCompleted("knowledge_v2_send_completed");
            else
                lease.MarkFailed("knowledge_v2_send_failed");
            Log.Info("Knowledge Engine V2本地直答完成: buyer=" + burst.BuyerNick
                + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
            return true;
        }

        private static bool IsApprovedMatchForLogging(KnowledgeV2Match match)
        {
            return match != null && match.Record != null
                && match.Record.Enabled
                && !string.Equals(match.Record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(match.Record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeEngineV2Bootstrap = ChromeNs.KnowledgeEngineV2RuntimeBridge.InitializeForApp();
    }
}
