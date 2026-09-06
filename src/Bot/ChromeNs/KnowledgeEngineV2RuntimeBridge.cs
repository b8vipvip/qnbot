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
        private sealed class V2HandlerWrapper
        {
            private readonly QN _qn;
            private readonly Func<BuyerMessageBurstLease, Task> _inner;

            public V2HandlerWrapper(QN qn, Func<BuyerMessageBurstLease, Task> inner)
            {
                _qn = qn;
                _inner = inner;
            }

            public Task HandleAsync(BuyerMessageBurstLease lease)
            {
                return KnowledgeEngineV2RuntimeBridge.HandleAsync(_qn, _inner, lease);
            }
        }

        private static readonly ConcurrentDictionary<string, byte> PreparedSellers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> WarmingSellers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<BuyerMessageBurstCoordinator, byte> PatchedCoordinators =
            new ConcurrentDictionary<BuyerMessageBurstCoordinator, byte>();
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                try { KnowledgeCenterV2UiBridge.Initialize(); } catch { }
                try { KnowledgeV2QualityUiBridge.Initialize(); } catch { }
                try { KnowledgeEngineV2FeedbackBridge.Initialize(); } catch { }
                StopLegacyMemoryTimer();
                _timer = new Timer(_ =>
                {
                    StopLegacyMemoryTimer();
                    PatchExisting();
                }, null, 850, 850);
                Log.Info("Knowledge Engine V2已启动：SQLite Knowledge Repository + 结构化倒排索引 + Working Memory补全 + 真实反馈质量闭环；Memory Engine v1运行时已进入兼容退场模式。");
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

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }

                var coordinatorField = typeof(QN).GetField("_buyerMessageBurstCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick)) continue;
                    var seller = qn.Seller.Nick.Trim();
                    PrepareSeller(seller);
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    if (!PatchedCoordinators.TryAdd(coordinator, 0)) continue;
                    var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (current == null)
                    {
                        byte ignored;
                        PatchedCoordinators.TryRemove(coordinator, out ignored);
                        continue;
                    }
                    current = StripLegacyMemoryWrapper(current);
                    if (current == null)
                    {
                        byte ignored;
                        PatchedCoordinators.TryRemove(coordinator, out ignored);
                        continue;
                    }
                    if (current.Target is V2HandlerWrapper)
                    {
                        handlerField.SetValue(coordinator, current);
                        continue;
                    }
                    var wrapper = new V2HandlerWrapper(qn, current);
                    try
                    {
                        handlerField.SetValue(coordinator, new Func<BuyerMessageBurstLease, Task>(wrapper.HandleAsync));
                    }
                    catch
                    {
                        byte ignored;
                        PatchedCoordinators.TryRemove(coordinator, out ignored);
                        throw;
                    }
                    Log.Info("已为客服实例挂载Knowledge Engine V2本地决策层: seller=" + seller);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装Knowledge Engine V2运行时桥失败，继续原回复链路: " + ex.Message, 10);
            }
        }

        private static Func<BuyerMessageBurstLease, Task> StripLegacyMemoryWrapper(
            Func<BuyerMessageBurstLease, Task> current)
        {
            for (var depth = 0; depth < 4 && current != null; depth++)
            {
                var target = current.Target;
                if (target == null) return current;
                var type = target.GetType();
                if (type.Name.IndexOf("MemoryHandlerWrapper", StringComparison.Ordinal) < 0
                    || type.DeclaringType != typeof(KnowledgeMemoryRuntimeBridge)) return current;
                var innerField = type.GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic);
                current = innerField == null ? null : innerField.GetValue(target) as Func<BuyerMessageBurstLease, Task>;
            }
            return current;
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

        private static async Task HandleAsync(QN qn, Func<BuyerMessageBurstLease, Task> inner, BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null || inner == null || burst == null || burst.Items.Count < 1
                || burst.LatestVisionItem != null || !burst.HasReplyableItem)
            {
                if (inner != null) await inner(lease);
                return;
            }
            if (!ReplyModeService.IsLocalFirst(burst.SellerNick) || !KnowledgeEngineV2Service.IsEnabled(burst.SellerNick))
            {
                await inner(lease);
                return;
            }

            if (!KnowledgeEngineV2Service.IsSnapshotReady(burst.SellerNick))
            {
                QueueWarm(burst.SellerNick);
                Log.Info("Knowledge Engine V2索引尚未就绪，本轮不阻塞买家消息，直接继续兼容回复链路: buyer="
                    + burst.BuyerNick);
                await inner(lease);
                return;
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
                await inner(lease);
                return;
            }

            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                Log.Info("Knowledge Engine V2未直答: buyer=" + burst.BuyerNick
                    + ", totalMs=" + (decision == null ? 0 : decision.TotalMs)
                    + ", candidates=" + (decision == null ? 0 : decision.CandidateCount)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                await inner(lease);
                return;
            }

            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();

            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                Log.Info("Knowledge Engine V2迟到结果已丢弃，generation已失效，未进入答案就绪/发送: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
            }
            if (autoSend && !await lease.ConfirmStableAsync(80))
            {
                Log.Info("Knowledge Engine V2发送前稳定性确认失败，未发布迟到答案: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
            }
            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                Log.Info("Knowledge Engine V2稳定性确认后generation失效，未发布答案: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
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
                return;
            }
            answer = dedup.Answer;
            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                || !lease.MarkReady("knowledge_v2_answer_materialized"))
            {
                Log.Info("Knowledge Engine V2答案完成后generation已终止，迟到本地答案已丢弃: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
            }
            var readyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                "本地知识V2",
                detectedAt,
                readyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);

            var best = decision.Matches.FirstOrDefault(IsApprovedMatchForLogging);
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
                return;
            }

            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：任务已被人工接管或显式取消");
                return;
            }

            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, out relevanceReason))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：" + relevanceReason);
                Log.Info("Knowledge Engine V2发送前被并发相关性门控抑制: buyer="
                    + burst.BuyerNick + ", reason=" + relevanceReason);
                return;
            }

            if (!lease.MarkSending("knowledge_v2_send_started"))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在V2发送前已失效");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge V2发送前generation已失效");
                return;
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
                return;
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
