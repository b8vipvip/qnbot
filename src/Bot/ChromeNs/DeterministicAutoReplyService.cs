using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Executes deterministic buyer replies before the burst/context merge window.
    /// Fixed replies never inspect or wait for AI endpoints. Off-hours is an exclusive policy:
    /// off-hours reply -> first-inquiry greeting -> configurable local short reply -> ordinary burst/context/AI handling.
    /// </summary>
    internal static class DeterministicAutoReplyService
    {
        private const string DefaultOffHoursReply =
            "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。";
        private const int OffHoursRepeatMinutes = 5;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> BuyerGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> OffHoursGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> OffHoursDeliveredUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>
        /// Returns true when the original buyer message should continue into the burst/context
        /// merge pipeline. Returns false when a higher-priority fixed rule owns this message.
        /// </summary>
        public static Task<bool> HandleBeforeMergeAsync(BuyerMessageBurstItem item)
        {
            return HandleBeforeMergeAsync(item, true);
        }

        /// <summary>
        /// allowLocalShortReply is false while another buyer message is still waiting in the same
        /// merge window. In that case a word such as “好的” must join the pending burst instead of
        /// prematurely consuming the buyer's previous unresolved question.
        /// </summary>
        public static async Task<bool> HandleBeforeMergeAsync(
            BuyerMessageBurstItem item,
            bool allowLocalShortReply)
        {
            try { Bot.Knowledge.LocalShortReplyUi.Initialize(); } catch { }

            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick)
                || string.IsNullOrWhiteSpace(item.DisplayText)
                || !Params.Robot.CanUseRobotReal)
            {
                return true;
            }

            if (item.SafetyDecision != null && !item.SafetyDecision.ShouldCallAi)
            {
                return true;
            }
            if (item.VisionDecision != null && item.VisionDecision.Kind == VisionDecisionKind.Skip)
            {
                return true;
            }

            var key = Key(item.SellerNick, item.BuyerNick);
            ShopContext shop = null;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(item.SellerNick); }
            catch { shop = null; }

            // Off-hours must be decided before the ordinary deterministic-rule gate. A slow or
            // failed mandatory send must never make a later message fail-open into Knowledge/AI.
            if (shop != null)
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    string offHoursReply;
                    if (TryResolveOffHours(out offHoursReply))
                    {
                        return await HandleOffHoursExclusiveAsync(item, key, offHoursReply).ConfigureAwait(false);
                    }
                }
            }

            var gate = BuyerGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            var gateAcquired = false;
            var sessionAgent = new BuyerSessionAgent();
            var generationToken = item.SessionGeneration > 0
                ? sessionAgent.GetCancellationToken(item.SellerNick, item.BuyerNick, item.SessionGeneration)
                : CancellationToken.None;
            try
            {
                await gate.WaitAsync(generationToken).ConfigureAwait(false);
                gateAcquired = true;
            }
            catch (OperationCanceledException)
            {
                Log.Info("固定规则串行等待期间generation已失效，禁止超时后放行AI链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick
                    + ", generation=" + item.SessionGeneration);
                return false;
            }

            try
            {
                if (item.SessionGeneration > 0
                    && !sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
                {
                    Log.Info("固定规则获得串行门时generation已失效，已消费该旧任务且不进入AI链路: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick
                        + ", generation=" + item.SessionGeneration);
                    return false;
                }

                if (shop != null)
                {
                    using (ShopSettingsScope.Enter(shop))
                    {
                        return await HandleScopedBeforeMergeAsync(item, key, allowLocalShortReply).ConfigureAwait(false);
                    }
                }

                Log.ErrorWithMaxCount(
                    "固定规则前置缺少店铺作用域，已停止固定规则发送并继续普通消息链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick,
                    20);
                return true;
            }
            catch (OperationCanceledException)
            {
                if (generationToken.IsCancellationRequested)
                {
                    Log.Info("固定规则执行期间generation已失效，禁止继续发送或进入AI链路: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick
                        + ", generation=" + item.SessionGeneration);
                    return false;
                }
                throw;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "固定规则前置处理异常，继续普通消息链路: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick
                    + ", error=" + ex.Message,
                    20);
                return true;
            }
            finally
            {
                if (gateAcquired) gate.Release();
            }
        }

        private static async Task<bool> HandleOffHoursExclusiveAsync(
            BuyerMessageBurstItem item,
            string buyerKey,
            string resolvedReply)
        {
            var gate = OffHoursGates.GetOrAdd(buyerKey, _ => new SemaphoreSlim(1, 1));
            var gateAcquired = await gate.WaitAsync(1800).ConfigureAwait(false);
            if (!gateAcquired)
            {
                Log.ErrorWithMaxCount(
                    "下班独占串行门等待超时，已fail-closed阻止Knowledge/AI链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick + ", waitMs=1800",
                    50);
                return false;
            }

            try
            {
                // Re-read the shop-scoped clock after serialization; a message waiting across the
                // work-start boundary should resume normal handling rather than send a stale notice.
                string currentReply;
                if (!TryResolveOffHours(out currentReply)) return true;
                if (string.IsNullOrWhiteSpace(currentReply)) currentReply = resolvedReply;

                DateTime until;
                if (OffHoursDeliveredUntil.TryGetValue(buyerKey, out until) && until > DateTime.Now)
                {
                    Log.Info("下班独占策略已消费买家消息，距离下一次下班提示不足5分钟，不进入其它回复链: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick
                        + ", next=" + until.ToString("HH:mm:ss"));
                    return false;
                }

                var qn = QN.FindExistingBySellerNick(item.SellerNick);
                if (qn == null)
                {
                    Log.ErrorWithMaxCount(
                        "下班独占策略找不到客服运行实例，已fail-closed阻止Knowledge/AI链路: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick,
                        20);
                    return false;
                }

                var ok = await SendFixedAsync(qn, item, currentReply, "下班自动回复").ConfigureAwait(false);
                if (ok)
                {
                    OffHoursDeliveredUntil[buyerKey] = DateTime.Now.AddMinutes(OffHoursRepeatMinutes);
                }
                else
                {
                    // Keep the policy exclusive even if transport fails. A short retry guard avoids
                    // a message storm repeatedly occupying the sender while still allowing recovery.
                    OffHoursDeliveredUntil[buyerKey] = DateTime.Now.AddSeconds(15);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "下班独占策略异常，已fail-closed阻止Knowledge/AI链路: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick + ", error=" + ex.Message,
                    20);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<bool> HandleScopedBeforeMergeAsync(
            BuyerMessageBurstItem item,
            string buyerKey,
            bool allowLocalShortReply)
        {
            if (!Params.Robot.GetIsAutoReply()) return true;

            var qn = QN.FindExistingBySellerNick(item.SellerNick);
            if (qn == null)
            {
                Log.ErrorWithMaxCount(
                    "固定规则前置未发送：找不到客服运行实例。seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick,
                    20);
                return true;
            }

            var question = (item.DisplayText ?? string.Empty).Trim();

            // Defense in depth for a work-hours boundary crossed after the pre-check: off-hours
            // still outranks first inquiry/local rules and consumes the message exclusively.
            string offHoursReply;
            if (TryResolveOffHours(out offHoursReply))
            {
                DateTime until;
                if (!OffHoursDeliveredUntil.TryGetValue(buyerKey, out until) || until <= DateTime.Now)
                {
                    var offHoursOk = await SendFixedAsync(qn, item, offHoursReply, "下班自动回复").ConfigureAwait(false);
                    OffHoursDeliveredUntil[buyerKey] = offHoursOk
                        ? DateTime.Now.AddMinutes(OffHoursRepeatMinutes)
                        : DateTime.Now.AddSeconds(15);
                }
                return false;
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
                    qn,
                    item,
                    firstReply,
                    "首条咨询固定回复").ConfigureAwait(false);
                if (firstOk)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(
                        item.SellerNick,
                        item.BuyerNick);
                }
                else
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        item.SellerNick,
                        item.BuyerNick,
                        qn.Rpa == null
                            ? "首条咨询固定回复发送失败"
                            : qn.Rpa.GetSendFailureReason());
                    return false;
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
                                + item.SellerNick + ", buyer=" + item.BuyerNick
                                + ", phrase=" + matchedPhrase);
                            return false;
                        }

                        var localOk = await SendFixedAsync(
                            qn,
                            item,
                            localAnswer,
                            "本地短消息回复").ConfigureAwait(false);
                        Log.Info("本地短消息精确命中: seller=" + item.SellerNick
                            + ", buyer=" + item.BuyerNick
                            + ", phrase=" + matchedPhrase
                            + ", success=" + localOk
                            + ", aiCalled=false");
                        return false;
                    }
                }
            }

            return true;
        }

        private static async Task<bool> SendFixedAsync(
            QN qn,
            BuyerMessageBurstItem item,
            string answer,
            string source)
        {
            answer = BotOutboundMessageFormatter.EnsureAiMarker((answer ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(answer))
            {
                Log.ErrorWithMaxCount(source + "内容为空，未发送。", 20);
                return false;
            }

            var sessionAgent = new BuyerSessionAgent();
            var generationToken = item.SessionGeneration > 0
                ? sessionAgent.GetCancellationToken(item.SellerNick, item.BuyerNick, item.SessionGeneration)
                : CancellationToken.None;
            if (item.SessionGeneration > 0
                && !sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
            {
                Log.Info(source + "生成结果到达时generation已失效，禁止记录Ready和真实发送: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick
                    + ", generation=" + item.SessionGeneration);
                return false;
            }

            var detectedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt;
            var ctl = ResponseProgressTracker.BeginAnswer(
                item.SellerNick,
                item.BuyerNick,
                item.DisplayText,
                detectedAt);
            try
            {
                KnowledgeLearningService.RegisterAnswerSource(
                    item.SellerNick,
                    item.BuyerNick,
                    item.DisplayText,
                    answer,
                    source);
                ctl = ResponseProgressTracker.SetAnswerReady(
                    item.SellerNick,
                    item.BuyerNick,
                    item.DisplayText,
                    answer,
                    source,
                    detectedAt,
                    DateTime.Now);
                BotRuntimeStats.RecordDisplayedAnswer(true);
                Log.Info(
                    source + "在消息合并前命中，不等待合并窗口、不检查AI接口: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick);

                if (item.SessionGeneration > 0
                    && !sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration))
                {
                    if (ctl != null) ctl.SetSendResult(false, "generation已失效，禁止发送迟到固定回复");
                    Log.Info(source + "进入真实发送前generation已失效，已阻止迟到回复: seller="
                        + item.SellerNick + ", buyer=" + item.BuyerNick
                        + ", generation=" + item.SessionGeneration);
                    return false;
                }

                var ok = await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken).ConfigureAwait(false);
                if (ok)
                {
                    ReplyDeduplicationService.RememberDelivered(
                        item.SellerNick,
                        item.BuyerNick,
                        answer);
                }
                else if (item.SessionGeneration > 0)
                {
                    sessionAgent.TryTransition(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        BuyerSessionAgentState.Failed,
                        "fixed_reply_send_failed");
                }
                if (ctl != null)
                {
                    ctl.SetSendResult(
                        ok,
                        ok
                            ? "已发送（" + source + "，先于消息合并，未调用AI）"
                            : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
                }
                Log.Info(
                    source + "前置真实发送完成: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", success=" + ok);
                return ok;
            }
            catch (OperationCanceledException)
            {
                if (ctl != null) ctl.SetSendResult(false, "generation已失效，固定回复发送已取消");
                Log.Info(source + "等待发送资源/会话确认期间generation失效，已在后续UI副作用前取消: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick
                    + ", generation=" + item.SessionGeneration);
                return false;
            }
            catch (Exception ex)
            {
                if (item.SessionGeneration > 0)
                {
                    sessionAgent.TryTransition(
                        item.SellerNick,
                        item.BuyerNick,
                        item.SessionGeneration,
                        BuyerSessionAgentState.Failed,
                        "fixed_reply_send_exception");
                }
                if (ctl != null) ctl.SetSendResult(false, "发送失败：" + ex.Message);
                Log.ErrorWithMaxCount(
                    source + "前置发送异常: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", error=" + ex.Message,
                    20);
                return false;
            }
            finally
            {
                ResponseProgressTracker.Complete(item.SellerNick, item.BuyerNick);
            }
        }

        public static async Task<bool> TryHandleAsync(
            BuyerMessageBurst burst,
            BuyerMessageBurstLease lease)
        {
            if (burst == null || burst.Items == null || burst.Items.Count < 1) return false;
            var item = burst.Items[0];
            return !await HandleBeforeMergeAsync(item).ConfigureAwait(false);
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
                    "下班自动回复工作时间配置无效，已停止固定回复以避免伪造09:00-18:00。 workStart="
                    + (cfg.WorkStartTime ?? string.Empty)
                    + ", workEnd=" + (cfg.WorkEndTime ?? string.Empty),
                    20);
                return false;
            }
            if (IsInsideWorkHours(DateTime.Now.TimeOfDay, start, end)) return false;

            var template = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? DefaultOffHoursReply
                : cfg.OffHoursFixedText.Trim();
            var workHours = FormatClock(start) + "-" + FormatClock(end);
            answer = template.Replace("{工作时间}", workHours);
            return !string.IsNullOrWhiteSpace(answer);
        }

        private static bool TryParseClock(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            value = (value ?? string.Empty).Trim();
            DateTime parsed;
            if (!DateTime.TryParseExact(
                value,
                new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsed))
            {
                return false;
            }
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

    internal sealed class LocalShortReplyEntry
    {
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public string Category { get; set; }
        public string Phrases { get; set; }
        public string Reply { get; set; }
        public string UpdatedAt { get; set; }
    }

    internal static class LocalShortReplyService
    {
        internal const string ConfigFileName = "local-short-replies.json";
        private const int MaxNormalizedPhraseLength = 24;
        private static readonly object Sync = new object();
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly Dictionary<string, CacheState> Cache =
            new Dictionary<string, CacheState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex CollapsedWhitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TrailingSafePunctuation =
            new Regex(@"[。！!，,、~～\.]+$", RegexOptions.Compiled);

        private sealed class CacheState
        {
            public long LastWriteTicks;
            public List<LocalShortReplyEntry> Entries;
        }

        public static bool TryResolve(
            string seller,
            string buyerText,
            out string reply,
            out string matchedPhrase)
        {
            reply = string.Empty;
            matchedPhrase = string.Empty;
            var normalized = NormalizePhrase(buyerText);
            if (normalized.Length == 0 || normalized.Length > MaxNormalizedPhraseLength) return false;

            try
            {
                var shop = ShopSettingsScope.Current;
                if (shop == null)
                {
                    shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                }
                var entries = Load(shop);
                foreach (var entry in entries)
                {
                    if (entry == null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Reply)) continue;
                    foreach (var phrase in SplitPhrases(entry.Phrases))
                    {
                        if (!string.Equals(NormalizePhrase(phrase), normalized, StringComparison.Ordinal)) continue;
                        reply = BotFeatureStore.ApplyOutputPolicy(entry.Reply.Trim());
                        matchedPhrase = phrase.Trim();
                        return !string.IsNullOrWhiteSpace(reply);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取本地短消息回复失败，继续普通消息链路: seller="
                    + (seller ?? string.Empty) + ", error=" + ex.Message, 20);
            }
            return false;
        }

        public static List<LocalShortReplyEntry> LoadForCurrentUi()
        {
            var shop = ShopSettingsScope.Current ?? ShopContextLocator.ResolveCurrentForUi();
            return Load(shop);
        }

        public static void SaveForCurrentUi(IEnumerable<LocalShortReplyEntry> entries)
        {
            var shop = ShopSettingsScope.Current ?? ShopContextLocator.ResolveCurrentForUi();
            Save(shop, entries, true);
        }

        public static void RestoreDefaultsForCurrentUi()
        {
            SaveForCurrentUi(CreateDefaults());
        }

        public static List<LocalShortReplyEntry> CreateDefaults()
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return new List<LocalShortReplyEntry>
            {
                Make("default-confirm", "确认/接受",
                    "好|好的|好呀|好啊|好哒|好嘞|好呢|好哦|行|行的|行吧|可以|可以的|没问题|没问题的|成|成的|OK|ok|Okay|okay|okk|嗯|嗯嗯|恩|恩恩|哦|哦哦|噢|嗯好|嗯好的|哦好|哦好的",
                    "好的。", now),
                Make("default-received", "收到/知悉",
                    "收到|收到了|收到啦|收到哈|知道了|知道啦|晓得了|明白|明白了|了解|了解了|清楚了|记住了|我知道了|我明白了",
                    "好的。", now),
                Make("default-affirmative", "肯定确认",
                    "是|是的|对|对的|对啊|对哦|没错|没错的|就是|就是的",
                    "好的。", now),
                Make("default-thanks", "感谢",
                    "谢谢|谢谢你|谢谢亲|谢啦|谢了|多谢|感谢|感谢你|辛苦了|辛苦啦|麻烦你了|麻烦了|有劳了",
                    "不客气。", now),
                Make("default-confirm-thanks", "确认并感谢",
                    "好的谢谢|好的谢谢你|好谢谢|行谢谢|可以谢谢|知道了谢谢|明白了谢谢|收到谢谢|谢谢啦|谢谢哈|谢谢啊",
                    "不客气。", now),
                Make("default-solved", "已解决/完成",
                    "好了|已经好了|弄好了|搞定了|解决了|已解决|可以了|能用了|正常了|恢复了|没事了|成功了|已经成功了",
                    "好的，解决了就行。", now),
                Make("default-wait", "稍等/正在操作",
                    "稍等|稍等一下|等一下|等下|等会|等一会|等一会儿|我看看|我看下|我看一下|我试试|我试一下|我操作一下|我操作下|我去试试|我先试试|我弄一下|我弄下|我处理一下",
                    "好的，您先操作，有问题再告诉我。", now),
                Make("default-later", "稍后处理",
                    "晚点弄|晚点试|一会儿试|我晚点试试|等会试|等会儿试|过会试|稍后试试|我稍后试试|晚点联系|稍后联系",
                    "好的，您先忙，有需要再联系我们。", now),
                Make("default-no-need", "暂不需要",
                    "不用了|不用啦|不需要了|暂时不用|暂时不用了|先不用|先不用了|算了|不麻烦了|先这样|这样就行|这样可以了",
                    "好的。", now),
                Make("default-apology", "道歉回应",
                    "不好意思|不好意思啊|不好意思哈|抱歉|抱歉啊|对不起|对不住",
                    "没关系。", now),
                Make("default-praise", "表扬/认可",
                    "很好|挺好的|不错|真不错|可以的很|服务很好|服务不错|你真好|太好了|太棒了",
                    "谢谢认可。", now),
                Make("default-bye", "告别",
                    "再见|拜拜|拜|回见|下次再聊|下次联系|回头联系|有需要再找你|有需要再联系|先这样吧",
                    "好的，有需要再联系我们。", now),
                Make("default-night", "晚安",
                    "晚安|晚安啦|早点休息|休息吧",
                    "晚安，有需要再联系我们。", now),
                Make("default-laugh", "轻松回应",
                    "哈哈|哈哈哈|嘿嘿|嘻嘻",
                    "哈哈，好的。", now)
            };
        }

        internal static List<string> SplitPhrases(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static string NormalizePhrase(string value)
        {
            value = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
            value = CollapsedWhitespace.Replace(value, string.Empty);
            value = TrailingSafePunctuation.Replace(value, string.Empty).Trim();
            return value;
        }

        private static LocalShortReplyEntry Make(
            string id,
            string category,
            string phrases,
            string reply,
            string updatedAt)
        {
            return new LocalShortReplyEntry
            {
                Id = id,
                Enabled = true,
                Category = category,
                Phrases = phrases,
                Reply = reply,
                UpdatedAt = updatedAt
            };
        }

        private static List<LocalShortReplyEntry> Load(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException("shop");
            var path = Paths.GetConfigPath(shop, ConfigFileName);
            lock (Sync)
            {
                if (!File.Exists(path))
                {
                    var defaults = CreateDefaults();
                    SaveLocked(path, defaults, false);
                    return Clone(defaults);
                }

                var ticks = File.GetLastWriteTimeUtc(path).Ticks;
                CacheState cached;
                if (Cache.TryGetValue(path, out cached)
                    && cached != null
                    && cached.LastWriteTicks == ticks
                    && cached.Entries != null)
                {
                    return Clone(cached.Entries);
                }

                List<LocalShortReplyEntry> entries;
                try
                {
                    entries = JsonConvert.DeserializeObject<List<LocalShortReplyEntry>>(
                        File.ReadAllText(path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("短消息回复配置损坏，已停止使用该配置：" + ex.Message, ex);
                }
                entries = NormalizeEntries(entries, false);
                Cache[path] = new CacheState { LastWriteTicks = ticks, Entries = Clone(entries) };
                return Clone(entries);
            }
        }

        private static void Save(
            ShopContext shop,
            IEnumerable<LocalShortReplyEntry> entries,
            bool failOnDuplicate)
        {
            if (shop == null) throw new ArgumentNullException("shop");
            var path = Paths.GetConfigPath(shop, ConfigFileName);
            var normalized = NormalizeEntries(entries, failOnDuplicate);
            lock (Sync)
            {
                SaveLocked(path, normalized, failOnDuplicate);
            }
        }

        private static void SaveLocked(
            string path,
            IEnumerable<LocalShortReplyEntry> entries,
            bool failOnDuplicate)
        {
            var normalized = NormalizeEntries(entries, failOnDuplicate);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(normalized, Formatting.Indented);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    var previous = path + ".previous";
                    try
                    {
                        File.Replace(temp, path, previous, true);
                        if (File.Exists(previous)) File.Delete(previous);
                    }
                    catch
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }

            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            Cache[path] = new CacheState { LastWriteTicks = ticks, Entries = Clone(normalized) };
        }

        private static List<LocalShortReplyEntry> NormalizeEntries(
            IEnumerable<LocalShortReplyEntry> entries,
            bool failOnDuplicate)
        {
            var result = new List<LocalShortReplyEntry>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in entries ?? new LocalShortReplyEntry[0])
            {
                if (raw == null) continue;
                var phrases = new List<string>();
                foreach (var phrase in SplitPhrases(raw.Phrases))
                {
                    var normalized = NormalizePhrase(phrase);
                    if (normalized.Length == 0 || normalized.Length > MaxNormalizedPhraseLength) continue;
                    string owner;
                    if (seen.TryGetValue(normalized, out owner))
                    {
                        if (failOnDuplicate)
                            throw new InvalidOperationException("短消息触发词重复：“" + phrase + "”同时存在于“" + owner + "”等规则中。请保留一处。 ");
                        continue;
                    }
                    seen[normalized] = string.IsNullOrWhiteSpace(raw.Category) ? "未分类" : raw.Category.Trim();
                    phrases.Add(phrase.Trim());
                }
                if (phrases.Count == 0 || string.IsNullOrWhiteSpace(raw.Reply)) continue;
                result.Add(new LocalShortReplyEntry
                {
                    Id = string.IsNullOrWhiteSpace(raw.Id) ? Guid.NewGuid().ToString("N") : raw.Id.Trim(),
                    Enabled = raw.Enabled,
                    Category = string.IsNullOrWhiteSpace(raw.Category) ? "通用" : raw.Category.Trim(),
                    Phrases = string.Join("|", phrases),
                    Reply = raw.Reply.Trim(),
                    UpdatedAt = string.IsNullOrWhiteSpace(raw.UpdatedAt)
                        ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        : raw.UpdatedAt.Trim()
                });
            }
            return result;
        }

        internal static LocalShortReplyEntry Clone(LocalShortReplyEntry entry)
        {
            if (entry == null) return null;
            return new LocalShortReplyEntry
            {
                Id = entry.Id,
                Enabled = entry.Enabled,
                Category = entry.Category,
                Phrases = entry.Phrases,
                Reply = entry.Reply,
                UpdatedAt = entry.UpdatedAt
            };
        }

        private static List<LocalShortReplyEntry> Clone(IEnumerable<LocalShortReplyEntry> entries)
        {
            return (entries ?? new LocalShortReplyEntry[0]).Select(Clone).Where(x => x != null).ToList();
        }
    }
}

namespace Bot.Knowledge
{
    internal static class LocalShortReplyUi
    {
        private static readonly ConditionalWeakTable<KnowledgeCenterWindow, object> Enhanced =
            new ConditionalWeakTable<KnowledgeCenterWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeCenterWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnKnowledgeCenterLoaded),
                true);
        }

        private static void OnKnowledgeCenterLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow;
            if (window == null) return;
            object marker;
            if (Enhanced.TryGetValue(window, out marker)) return;

            try
            {
                var field = typeof(KnowledgeCenterWindow).GetField(
                    "_tabs",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tabs = field == null ? null : field.GetValue(window) as TabControl;
                if (tabs == null) return;
                if (tabs.Items.OfType<TabItem>().Any(x => string.Equals(
                    Convert.ToString(x.Header), "短消息回复", StringComparison.Ordinal)))
                {
                    Enhanced.Add(window, new object());
                    return;
                }

                var managerIndex = -1;
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    var tab = tabs.Items[i] as TabItem;
                    if (tab != null && string.Equals(Convert.ToString(tab.Header), "问答管理", StringComparison.Ordinal))
                    {
                        managerIndex = i;
                        break;
                    }
                }
                var insertAt = managerIndex >= 0 ? managerIndex + 1 : Math.Min(2, tabs.Items.Count);
                tabs.Items.Insert(insertAt, new TabItem
                {
                    Header = "短消息回复",
                    Content = new LocalShortReplyManagerControl()
                });
                Enhanced.Add(window, new object());
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化知识库短消息回复页面失败：" + ex.Message, 10);
            }
        }
    }

    internal sealed class LocalShortReplyManagerControl : UserControl
    {
        private readonly ObservableCollection<Bot.ChromeNs.LocalShortReplyEntry> _view =
            new ObservableCollection<Bot.ChromeNs.LocalShortReplyEntry>();
        private List<Bot.ChromeNs.LocalShortReplyEntry> _all =
            new List<Bot.ChromeNs.LocalShortReplyEntry>();
        private readonly TextBox _search = new TextBox();
        private readonly DataGrid _grid = new DataGrid();
        private readonly TextBlock _status = new TextBlock();

        public LocalShortReplyManagerControl()
        {
            Build();
            Loaded += delegate { RefreshData(); };
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var intro = new TextBlock
            {
                Text = "管理无需调用 AI 的简短确认/感谢/收尾回复。仅做完整短语精确匹配；例如“好的”会命中，“好的怎么充值”不会命中。规则按当前 ShopKey 独立保存。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(91, 102, 121)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            _search.Width = 210;
            _search.Height = 28;
            _search.ToolTip = "搜索分类、触发短语或回复内容";
            _search.TextChanged += delegate { ApplyFilter(); };
            tools.Children.Add(_search);
            AddButton(tools, "新增", 70, delegate { AddNew(); });
            AddButton(tools, "编辑所选", 82, delegate { EditSelected(); });
            AddButton(tools, "启用/停用", 82, delegate { ToggleSelected(); });
            AddButton(tools, "删除所选", 82, delegate { DeleteSelected(); });
            AddButton(tools, "恢复默认模板", 100, delegate { RestoreDefaults(); });
            AddButton(tools, "导入JSON", 82, delegate { ImportJson(); });
            AddButton(tools, "导出JSON", 82, delegate { ExportJson(); });

            _status.Margin = new Thickness(0, 4, 0, 0);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(91, 102, 121));
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.IsReadOnly = true;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.ItemsSource = _view;
            _grid.MouseDoubleClick += delegate { EditSelected(); };
            _grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "启用",
                Binding = new Binding("Enabled"),
                Width = 55,
                IsReadOnly = true
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "分类",
                Binding = new Binding("Category"),
                Width = 110,
                IsReadOnly = true
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "精确触发短语（| 分隔）",
                Binding = new Binding("Phrases"),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                IsReadOnly = true
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "直接回复",
                Binding = new Binding("Reply"),
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                IsReadOnly = true
            });
            root.Children.Add(_grid);
        }

        private static void AddButton(Panel panel, string text, double width, Action handler)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 5)
            };
            button.Click += delegate { handler(); };
            panel.Children.Add(button);
        }

        private void RefreshData()
        {
            try
            {
                _all = Bot.ChromeNs.LocalShortReplyService.LoadForCurrentUi();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                _status.Text = "读取短消息回复失败：" + ex.Message;
                _all = new List<Bot.ChromeNs.LocalShortReplyEntry>();
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            var query = (_search.Text ?? string.Empty).Trim();
            _view.Clear();
            foreach (var entry in _all.Where(x => Match(x, query))) _view.Add(entry);
            var phraseCount = _all.Sum(x => Bot.ChromeNs.LocalShortReplyService.SplitPhrases(x.Phrases).Count);
            _status.Text = "共 " + _all.Count + " 组、" + phraseCount + " 个精确触发短语；当前显示 " + _view.Count
                + " 组。命中后直接走现有可靠发送链，不调用 AI，也不会进入知识学习。";
        }

        private static bool Match(Bot.ChromeNs.LocalShortReplyEntry entry, string query)
        {
            if (entry == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return ((entry.Category ?? string.Empty) + " " + (entry.Phrases ?? string.Empty) + " " + (entry.Reply ?? string.Empty))
                .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddNew()
        {
            var item = new Bot.ChromeNs.LocalShortReplyEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Enabled = true,
                Category = "通用",
                Phrases = string.Empty,
                Reply = "好的。",
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            if (!OpenEditor(item)) return;
            _all.Add(item);
            SaveAndRefresh();
        }

        private void EditSelected()
        {
            var selected = _grid.SelectedItem as Bot.ChromeNs.LocalShortReplyEntry;
            if (selected == null) return;
            var edited = Bot.ChromeNs.LocalShortReplyService.Clone(selected);
            if (!OpenEditor(edited)) return;
            var index = _all.IndexOf(selected);
            if (index < 0) return;
            _all[index] = edited;
            SaveAndRefresh();
        }

        private bool OpenEditor(Bot.ChromeNs.LocalShortReplyEntry item)
        {
            var window = new LocalShortReplyEditWindow(item) { Owner = Window.GetWindow(this) };
            return window.ShowDialog() == true;
        }

        private void ToggleSelected()
        {
            var selected = _grid.SelectedItem as Bot.ChromeNs.LocalShortReplyEntry;
            if (selected == null) return;
            selected.Enabled = !selected.Enabled;
            selected.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveAndRefresh();
        }

        private void DeleteSelected()
        {
            var selected = _grid.SelectedItem as Bot.ChromeNs.LocalShortReplyEntry;
            if (selected == null) return;
            if (MessageBox.Show(
                Window.GetWindow(this),
                "确定删除所选短消息回复规则吗？",
                "短消息回复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _all.Remove(selected);
            SaveAndRefresh();
        }

        private void RestoreDefaults()
        {
            if (MessageBox.Show(
                Window.GetWindow(this),
                "恢复默认模板会覆盖当前店铺的全部短消息回复规则，但不会修改普通知识库。是否继续？",
                "恢复默认短消息模板",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                Bot.ChromeNs.LocalShortReplyService.RestoreDefaultsForCurrentUi();
                RefreshData();
            }
            catch (Exception ex)
            {
                MessageBox.Show("恢复默认模板失败：" + ex.Message, "短消息回复", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportJson()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入短消息回复",
                Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<Bot.ChromeNs.LocalShortReplyEntry>>(
                    File.ReadAllText(dialog.FileName, Encoding.UTF8));
                if (list == null) throw new InvalidOperationException("JSON中没有短消息回复数据。 ");
                if (MessageBox.Show(
                    Window.GetWindow(this),
                    "导入将覆盖当前店铺的短消息回复规则。是否继续？",
                    "导入短消息回复",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Bot.ChromeNs.LocalShortReplyService.SaveForCurrentUi(list);
                RefreshData();
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "短消息回复", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportJson()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出短消息回复",
                FileName = "local-short-replies.json",
                Filter = "JSON文件 (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(
                    dialog.FileName,
                    JsonConvert.SerializeObject(_all, Formatting.Indented),
                    new UTF8Encoding(false));
                _status.Text = "已导出：" + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "短消息回复", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAndRefresh()
        {
            try
            {
                Bot.ChromeNs.LocalShortReplyService.SaveForCurrentUi(_all);
                RefreshData();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存短消息回复失败：" + ex.Message, "短消息回复", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshData();
            }
        }
    }

    internal sealed class LocalShortReplyEditWindow : Window
    {
        private readonly Bot.ChromeNs.LocalShortReplyEntry _entry;
        private readonly CheckBox _enabled = new CheckBox();
        private readonly TextBox _category = new TextBox();
        private readonly TextBox _phrases = new TextBox();
        private readonly TextBox _reply = new TextBox();

        public LocalShortReplyEditWindow(Bot.ChromeNs.LocalShortReplyEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            _entry = entry;
            Title = "编辑短消息回复";
            Width = 650;
            Height = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Build();
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "取消", Width = 80, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += delegate { DialogResult = false; };
            var save = new Button { Content = "保存", Width = 80, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            save.Click += delegate { Save(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);

            var body = new StackPanel();
            root.Children.Add(body);

            _enabled.Content = "启用这组短消息回复";
            _enabled.IsChecked = _entry.Enabled;
            _enabled.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(_enabled);

            body.Children.Add(Label("分类"));
            _category.Text = _entry.Category ?? string.Empty;
            _category.Height = 28;
            body.Children.Add(_category);

            body.Children.Add(Label("精确触发短语（使用 | 分隔多个说法）"));
            _phrases.Text = _entry.Phrases ?? string.Empty;
            _phrases.AcceptsReturn = true;
            _phrases.TextWrapping = TextWrapping.Wrap;
            _phrases.MinHeight = 90;
            _phrases.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            body.Children.Add(_phrases);
            body.Children.Add(new TextBlock
            {
                Text = "仅完整短语匹配；会忽略大小写、空格以及末尾的句号/感叹号等安全标点，但不会去掉问号，也不会做包含或模糊匹配。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 10)
            });

            body.Children.Add(Label("直接回复内容"));
            _reply.Text = _entry.Reply ?? string.Empty;
            _reply.AcceptsReturn = true;
            _reply.TextWrapping = TextWrapping.Wrap;
            _reply.MinHeight = 70;
            _reply.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            body.Children.Add(_reply);
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 5)
            };
        }

        private void Save()
        {
            if (Bot.ChromeNs.LocalShortReplyService.SplitPhrases(_phrases.Text).Count == 0)
            {
                MessageBox.Show(this, "至少填写一个触发短语。", "短消息回复", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_reply.Text))
            {
                MessageBox.Show(this, "直接回复内容不能为空。", "短消息回复", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _entry.Enabled = _enabled.IsChecked == true;
            _entry.Category = string.IsNullOrWhiteSpace(_category.Text) ? "通用" : _category.Text.Trim();
            _entry.Phrases = string.Join("|", Bot.ChromeNs.LocalShortReplyService.SplitPhrases(_phrases.Text));
            _entry.Reply = _reply.Text.Trim();
            _entry.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            DialogResult = true;
        }
    }
}