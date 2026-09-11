using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal enum CanonicalPreMergeOutcome
    {
        Continue = 0,
        Consumed = 1,
        Failed = 2,
        Cancelled = 3
    }

    /// <summary>
    /// Stateless deterministic policy helper for BuyerMessageBurstCoordinator.
    /// The coordinator is the sole per-buyer serializer and lifecycle owner. This helper never
    /// creates a same-buyer gate and never writes BuyerSessionAgent terminal state.
    /// </summary>
    internal static class CanonicalPreMergeDecisionService
    {
        private const string DefaultOffHoursReply =
            "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。";
        private const int OffHoursRepeatMinutes = 2;
        private static readonly ConcurrentDictionary<string, DateTime> OffHoursDeliveredUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public static async Task<CanonicalPreMergeOutcome> HandleAsync(
            BuyerMessageBurstItem item,
            bool allowLocalShortReply,
            CancellationToken cancellationToken)
        {
            try { Bot.Knowledge.LocalShortReplyUi.Initialize(); } catch { }

            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick)
                || string.IsNullOrWhiteSpace(item.DisplayText)
                || !Params.Robot.CanUseRobotReal)
            {
                return CanonicalPreMergeOutcome.Continue;
            }

            if (item.SafetyDecision != null && !item.SafetyDecision.ShouldCallAi)
                return CanonicalPreMergeOutcome.Continue;
            if (item.VisionDecision != null && item.VisionDecision.Kind == VisionDecisionKind.Skip)
                return CanonicalPreMergeOutcome.Continue;

            cancellationToken.ThrowIfCancellationRequested();

            ShopContext shop = null;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(item.SellerNick); }
            catch { shop = null; }
            if (shop == null)
            {
                Log.ErrorWithMaxCount(
                    "统一买家生命周期缺少店铺作用域，固定规则跳过并继续普通消息链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick,
                    20);
                return CanonicalPreMergeOutcome.Continue;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await HandleScopedAsync(item, allowLocalShortReply, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<CanonicalPreMergeOutcome> HandleScopedAsync(
            BuyerMessageBurstItem item,
            bool allowLocalShortReply,
            CancellationToken cancellationToken)
        {
            if (!Params.Robot.GetIsAutoReply()) return CanonicalPreMergeOutcome.Continue;

            var qn = QN.FindExistingBySellerNick(item.SellerNick);
            if (qn == null)
            {
                Log.ErrorWithMaxCount(
                    "统一买家生命周期固定规则未发送：找不到客服运行实例。seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick,
                    20);
                return CanonicalPreMergeOutcome.Continue;
            }

            var question = (item.DisplayText ?? string.Empty).Trim();
            var buyerKey = Key(item.SellerNick, item.BuyerNick);

            string offHoursReply;
            if (TryResolveOffHours(out offHoursReply))
            {
                DateTime until;
                if (!OffHoursDeliveredUntil.TryGetValue(buyerKey, out until) || until <= DateTime.Now)
                {
                    var offHoursOk = await SendFixedAsync(
                        qn, item, offHoursReply, "下班自动回复", cancellationToken).ConfigureAwait(false);
                    OffHoursDeliveredUntil[buyerKey] = offHoursOk
                        ? DateTime.Now.AddMinutes(OffHoursRepeatMinutes)
                        : DateTime.Now.AddSeconds(15);
                    return offHoursOk ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;
                }
                Log.Info("下班独占策略已由统一买家生命周期消费，距离下一次下班提示不足2分钟: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick + ", next=" + until.ToString("HH:mm:ss"));
                return CanonicalPreMergeOutcome.Consumed;
            }

            string firstReply;
            var firstReserved = FirstInquiryFixedReplyService.TryResolve(
                item.SellerNick,
                item.BuyerNick,
                question,
                out firstReply);
            if (firstReserved)
            {
                var firstOk = await SendFixedAsync(
                    qn, item, firstReply, "首条咨询固定回复", cancellationToken).ConfigureAwait(false);
                if (firstOk)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(item.SellerNick, item.BuyerNick);
                }
                else
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        item.SellerNick,
                        item.BuyerNick,
                        qn.Rpa == null ? "首条咨询固定回复发送失败" : qn.Rpa.GetSendFailureReason());
                    return cancellationToken.IsCancellationRequested
                        ? CanonicalPreMergeOutcome.Cancelled
                        : CanonicalPreMergeOutcome.Failed;
                }
            }

            if (allowLocalShortReply)
            {
                var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
                if (manualDecision == null || !manualDecision.Matched)
                {
                    string localAnswer;
                    string matchedPhrase;
                    if (LocalShortReplyService.TryResolve(
                        item.SellerNick,
                        question,
                        out localAnswer,
                        out matchedPhrase))
                    {
                        if (firstReserved)
                        {
                            Log.Info("本地短消息已由首条咨询固定回复覆盖，避免重复发送: seller="
                                + item.SellerNick + ", buyer=" + item.BuyerNick + ", phrase=" + matchedPhrase);
                            return CanonicalPreMergeOutcome.Continue;
                        }

                        var localOk = await SendFixedAsync(
                            qn, item, localAnswer, "本地短消息回复", cancellationToken).ConfigureAwait(false);
                        Log.Info("本地短消息精确命中: seller=" + item.SellerNick
                            + ", buyer=" + item.BuyerNick + ", phrase=" + matchedPhrase
                            + ", success=" + localOk + ", aiCalled=false");
                        return localOk ? CanonicalPreMergeOutcome.Consumed : CanonicalPreMergeOutcome.Failed;
                    }
                }
            }

            // A successful first-inquiry greeting is a prelude; the same substantive problem must
            // continue through the ordinary Knowledge/AI path.
            return CanonicalPreMergeOutcome.Continue;
        }

        private static async Task<bool> SendFixedAsync(
            QN qn,
            BuyerMessageBurstItem item,
            string answer,
            string source,
            CancellationToken cancellationToken)
        {
            answer = BotOutboundMessageFormatter.EnsureAiMarker((answer ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(answer)) return false;
            cancellationToken.ThrowIfCancellationRequested();

            var detectedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt;
            var ctl = ResponseProgressTracker.BeginAnswer(
                item.SellerNick, item.BuyerNick, item.DisplayText, detectedAt);
            try
            {
                KnowledgeLearningService.RegisterAnswerSource(
                    item.SellerNick, item.BuyerNick, item.DisplayText, answer, source);
                ctl = ResponseProgressTracker.SetAnswerReady(
                    item.SellerNick, item.BuyerNick, item.DisplayText, answer, source, detectedAt, DateTime.Now);
                BotRuntimeStats.RecordDisplayedAnswer(true);
                Log.Info(source + "由统一买家生命周期在消息合并前命中，不等待AI接口: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick);

                cancellationToken.ThrowIfCancellationRequested();
                var ok = await qn.SendTextWithRetryAsync(
                    item.BuyerNick, answer, 3, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    ReplyDeduplicationService.RememberDelivered(item.SellerNick, item.BuyerNick, answer);
                }
                if (ctl != null)
                {
                    ctl.SetSendResult(ok, ok
                        ? "已发送（" + source + "，由统一买家生命周期前置处理）"
                        : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
                }
                Log.Info(source + "前置真实发送完成: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", success=" + ok);
                return ok;
            }
            catch (OperationCanceledException)
            {
                if (ctl != null) ctl.SetSendResult(false, "generation已失效，固定回复发送已取消");
                return false;
            }
            catch (Exception ex)
            {
                if (ctl != null) ctl.SetSendResult(false, "发送失败：" + ex.Message);
                Log.ErrorWithMaxCount(source + "前置发送异常: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", error=" + ex.Message, 20);
                return false;
            }
            finally
            {
                ResponseProgressTracker.Complete(item.SellerNick, item.BuyerNick);
            }
        }

        private static bool TryResolveOffHours(out string answer)
        {
            answer = string.Empty;
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableWorkHours) return false;

            TimeSpan start;
            TimeSpan end;
            if (!TryParseClock(cfg.WorkStartTime, out start)
                || !TryParseClock(cfg.WorkEndTime, out end))
            {
                Log.ErrorWithMaxCount(
                    "下班自动回复工作时间配置无效，已停止固定回复。 workStart="
                    + (cfg.WorkStartTime ?? string.Empty) + ", workEnd=" + (cfg.WorkEndTime ?? string.Empty),
                    20);
                return false;
            }
            if (IsInsideWorkHours(DateTime.Now.TimeOfDay, start, end)) return false;

            var template = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? DefaultOffHoursReply
                : cfg.OffHoursFixedText.Trim();
            answer = template.Replace("{工作时间}", FormatClock(start) + "-" + FormatClock(end));
            return !string.IsNullOrWhiteSpace(answer);
        }

        private static bool TryParseClock(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            DateTime parsed;
            if (!DateTime.TryParseExact(
                (value ?? string.Empty).Trim(),
                new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsed)) return false;
            time = parsed.TimeOfDay;
            return true;
        }

        private static bool IsInsideWorkHours(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            if (start == end) return true;
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;
        }

        private static string FormatClock(TimeSpan value)
        {
            return ((int)value.TotalHours).ToString("00") + ":" + value.Minutes.ToString("00");
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
