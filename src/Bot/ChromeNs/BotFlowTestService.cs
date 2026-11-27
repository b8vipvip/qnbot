using Bot.AssistWindow.Widget.Robot;
using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BotFlowTestCandidate
    {
        public string Seller { get; set; }
        public string ShopKey { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; }
    }

    internal sealed class BotFlowTestResult
    {
        public bool Success { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string Detail { get; set; }
        public long AiLatencyMs { get; set; }
        public long SendLatencyMs { get; set; }
        public long TotalLatencyMs { get; set; }
    }

    internal static class BotFlowTestService
    {
        private static readonly object Sync = new object();
        private static readonly List<BotFlowTestCandidate> Recent = new List<BotFlowTestCandidate>();
        private static readonly Random Random = new Random();

        public static void RecordCandidate(
            string seller,
            string buyer,
            string question,
            DateTime capturedAt,
            string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0 || !IsEligible(question)) return;
            if (BotFeatureStore.EvaluateAutoReplyRule(question).Matched) return;
            var candidate = new BotFlowTestCandidate
            {
                Seller = (seller ?? string.Empty).Trim(),
                ShopKey = shopKey,
                Buyer = (buyer ?? string.Empty).Trim(),
                Question = question.Trim(),
                CapturedAt = capturedAt == DateTime.MinValue ? DateTime.Now : capturedAt,
                Source = "本次运行已识别消息"
            };
            if (candidate.Seller.Length == 0 || candidate.Buyer.Length == 0) return;
            lock (Sync)
            {
                Recent.RemoveAll(x => x == null || x.CapturedAt < DateTime.Now.AddHours(-72));
                if (!Recent.Any(x => x.ShopKey == candidate.ShopKey
                    && x.Seller == candidate.Seller
                    && x.Buyer == candidate.Buyer
                    && x.Question == candidate.Question))
                {
                    Recent.Add(candidate);
                }
                if (Recent.Count > 200) Recent.RemoveRange(0, Recent.Count - 200);
            }
        }

        public static async Task<BotFlowTestCandidate> PickRandomCandidateAsync()
        {
            lock (Sync)
            {
                var available = Recent
                    .Where(x => x != null
                        && x.CapturedAt >= DateTime.Now.AddHours(-72)
                        && !string.IsNullOrWhiteSpace(x.ShopKey)
                        && QN.FindExistingByShopKey(x.ShopKey) != null
                        && IsEligible(x.Question)
                        && !BotFeatureStore.EvaluateAutoReplyRule(x.Question).Matched)
                    .ToList();
                if (available.Count > 0)
                {
                    return available[Random.Next(available.Count)];
                }
            }
            return await LoadFromCurrentConversationAsync();
        }

        public static async Task<BotFlowTestResult> RunAsync(BotFlowTestCandidate candidate)
        {
            var total = Stopwatch.StartNew();
            if (candidate == null) return Failure("没有可用的测试问题。", total);
            if (!Params.Robot.CanUseRobotReal) return Failure("Bot 总开关已停用。", total, candidate);
            var shopKey = (candidate.ShopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                return Failure("测试问题缺少可信ShopKey，已阻止按客服昵称猜测店铺。", total, candidate);
            var qn = QN.FindExistingByShopKey(shopKey);
            if (qn == null || qn.CDP == null)
            {
                return Failure("目标店铺当前没有可用的千牛消息连接，已阻止回退到其他店铺。", total, candidate);
            }
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
            catch
            {
                return Failure("无法确认目标QN的唯一ShopKey，已阻止跨店铺执行测试。", total, candidate);
            }
            if (shop == null
                || !string.Equals((shop.ShopKey ?? string.Empty).Trim(), shopKey, StringComparison.Ordinal)
                || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), (candidate.Seller ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return Failure("目标店铺身份与测试问题来源不一致，已阻止跨店铺执行测试。", total, candidate);
            }
            if (qn.Seller == null
                || !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), (candidate.Seller ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return Failure("目标客服账号校验失败，已阻止跨店铺执行测试。", total, candidate);
            }

            var detectedAt = DateTime.Now;
            var label = "[Bot真实流程测试] " + candidate.Question;
            var ctl = ResponseProgressTracker.ObserveQuestion(
                candidate.Seller,
                candidate.Buyer,
                label,
                detectedAt,
                shopKey);
            if (ctl != null) ctl.SetProcessing("测试中：正在按真实规则生成答案...");

            var ai = Stopwatch.StartNew();
            string generated;
            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    generated = await Task.Run(() => MyOpenAI.GetAnswer(
                        candidate.Seller,
                        candidate.Buyer,
                        candidate.Question,
                        true));
                }
            }
            catch (Exception ex)
            {
                ai.Stop();
                var error = "AI答案生成异常：" + ex.Message;
                if (ctl != null) ctl.SetSendResult(false, "测试失败：答案生成阶段");
                ResponseProgressTracker.Fail(
                    candidate.Seller, candidate.Buyer,
                    "Bot真实流程测试答案生成异常：" + ex.Message, shopKey);
                return Failure(error, total, candidate, ai.ElapsedMilliseconds);
            }
            ai.Stop();
            if (string.IsNullOrWhiteSpace(generated) || generated.StartsWith("错误：", StringComparison.Ordinal))
            {
                var error = string.IsNullOrWhiteSpace(generated) ? "AI 未返回答案。" : generated;
                ctl = ResponseProgressTracker.SetAnswerReady(
                    candidate.Seller,
                    candidate.Buyer,
                    label,
                    error,
                    "Bot真实流程测试",
                    detectedAt,
                    DateTime.Now,
                    shopKey);
                if (ctl != null) ctl.SetSendResult(false, "测试失败：答案生成阶段");
                ResponseProgressTracker.Fail(
                    candidate.Seller, candidate.Buyer,
                    "Bot真实流程测试答案生成失败：" + error, shopKey);
                return Failure(error, total, candidate, ai.ElapsedMilliseconds);
            }

            var outgoing = BotOutboundMessageFormatter.EnsureAiMarker(
                "【Bot测试】" + BotOutboundMessageFormatter.StripAiMarker(
                    BotFeatureStore.ApplyOutputPolicy(generated)));
            var answerReadyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                candidate.Seller,
                candidate.Buyer,
                label,
                outgoing,
                "Bot真实流程测试",
                detectedAt,
                answerReadyAt,
                shopKey);
            if (ctl != null) ctl.SetSendPending("测试中：正在定位买家、写入输入框并真实发送...");

            var send = Stopwatch.StartNew();
            var sent = false;
            Exception sendException = null;
            using (ShopSettingsScope.Enter(shop))
            {
                try
                {
                    sent = await qn.SendTextWithRetryAsync(candidate.Buyer, outgoing, 1);
                }
                catch (Exception ex)
                {
                    sendException = ex;
                }
            }
            send.Stop();
            total.Stop();
            var sendCancelled = !sent && (sendException is OperationCanceledException
                || (qn.Rpa != null && qn.Rpa.LastSendWasCancelled));
            var sendFailureReason = sendException != null
                ? sendException.Message
                : qn.Rpa == null
                    ? "发送器不可用，未能确认实际送达状态"
                    : qn.Rpa.GetSendFailureReason();
            var detail = sent
                ? "测试成功：真实消息已发送，请在千牛中手动撤回这条【Bot测试】消息。"
                : sendCancelled
                    ? "测试取消：发送前会话门禁或人工接管已阻止发送。"
                    : "测试失败：" + sendFailureReason;
            if (ctl != null) ctl.SetSendResult(sent, detail);
            if (sendCancelled)
                ResponseProgressTracker.Cancel(
                    candidate.Seller, candidate.Buyer, "Bot真实流程测试发送前门禁取消", shopKey);
            else if (sent)
                ResponseProgressTracker.Complete(candidate.Seller, candidate.Buyer, shopKey);
            else
                ResponseProgressTracker.Fail(
                    candidate.Seller, candidate.Buyer,
                    "Bot真实流程测试答案已生成，但实际发送未确认：" + sendFailureReason, shopKey);
            Log.Info("Bot真实流程测试完成: seller=" + candidate.Seller
                + ", buyer=" + candidate.Buyer
                + ", success=" + sent
                + ", aiMs=" + ai.ElapsedMilliseconds
                + ", sendMs=" + send.ElapsedMilliseconds
                + ", totalMs=" + total.ElapsedMilliseconds
                + ", detail=" + detail);
            return new BotFlowTestResult
            {
                Success = sent,
                Seller = candidate.Seller,
                Buyer = candidate.Buyer,
                Question = candidate.Question,
                Answer = outgoing,
                Detail = detail,
                AiLatencyMs = ai.ElapsedMilliseconds,
                SendLatencyMs = send.ElapsedMilliseconds,
                TotalLatencyMs = total.ElapsedMilliseconds
            };
        }

        private static async Task<BotFlowTestCandidate> LoadFromCurrentConversationAsync()
        {
            try
            {
                var qn = QN.CurQN;
                if (qn == null || qn.CDP == null || qn.Seller == null) return null;
                var current = await qn.GetCurrentConversationID();
                var conversation = current == null ? null : current.Result;
                if (conversation == null
                    || string.IsNullOrWhiteSpace(conversation.Nick)
                    || string.IsNullOrWhiteSpace(conversation.Ccode))
                {
                    return null;
                }

                ShopContext shop;
                try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
                catch { shop = null; }
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), qn.Seller.Nick.Trim(), StringComparison.Ordinal))
                    return null;

                var history = await qn.CDP.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = conversation.Ccode, type = 1 },
                    count = 50,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                });
                var messages = history == null
                    ? null
                    : history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
                var candidates = (messages ?? new List<QNChatMessage>())
                    .Where(x => x != null
                        && x.fromid != null
                        && x.toid != null
                        && string.Equals((x.fromid.nick ?? string.Empty).Trim(), conversation.Nick.Trim(), StringComparison.Ordinal)
                        && string.Equals((x.toid.nick ?? string.Empty).Trim(), qn.Seller.Nick.Trim(), StringComparison.Ordinal))
                    .Select(x => new
                    {
                        Message = x,
                        Text = IncomingMessageSafety.GetDisplayText(x, ExtractText(x))
                    })
                    .Where(x => IsEligible(x.Text) && !BotFeatureStore.EvaluateAutoReplyRule(x.Text).Matched)
                    .OrderByDescending(x => IncomingMessageSafety.GetSortValue(x.Message))
                    .Take(20)
                    .ToList();
                if (candidates.Count == 0) return null;
                int pickedIndex;
                lock (Sync)
                {
                    pickedIndex = Random.Next(candidates.Count);
                }
                var picked = candidates[pickedIndex];
                return new BotFlowTestCandidate
                {
                    Seller = qn.Seller.Nick,
                    ShopKey = shop.ShopKey,
                    Buyer = conversation.Nick,
                    Question = picked.Text,
                    CapturedAt = DateTime.Now,
                    Source = "当前会话最近消息"
                };
            }
            catch (Exception ex)
            {
                Log.Info("选择Bot真实流程测试问题失败：" + ex.Message);
                return null;
            }
        }

        private static string ExtractText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var text = message.originalData == null ? string.Empty : (message.originalData.text ?? string.Empty);
            if (message.originalData != null && message.originalData.header != null)
            {
                text += message.originalData.header.summary ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(text)) text = message.summary ?? string.Empty;
            return text.Trim();
        }

        private static bool IsEligible(string question)
        {
            question = (question ?? string.Empty).Trim();
            if (question.Length < 1 || question.Length > 500) return false;
            if (question.StartsWith("[", StringComparison.Ordinal) && question.EndsWith("]", StringComparison.Ordinal)) return false;
            if (question.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
                || question.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static BotFlowTestResult Failure(
            string detail,
            Stopwatch total,
            BotFlowTestCandidate candidate = null,
            long aiMs = 0)
        {
            if (total.IsRunning) total.Stop();
            Log.Info("Bot真实流程测试未完成：" + detail);
            return new BotFlowTestResult
            {
                Success = false,
                Seller = candidate == null ? string.Empty : candidate.Seller,
                Buyer = candidate == null ? string.Empty : candidate.Buyer,
                Question = candidate == null ? string.Empty : candidate.Question,
                Detail = detail,
                AiLatencyMs = aiMs,
                TotalLatencyMs = total.ElapsedMilliseconds
            };
        }
    }
}
