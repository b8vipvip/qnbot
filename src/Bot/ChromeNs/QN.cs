using Bot.ChromeNs;
using DbEntity.Response;
using DbEntity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Newtonsoft.Json;
using BotLib;

namespace Bot.ChromeNs
{
    public partial class QN
    {
        public event EventHandler<BuyerSwitchedEventArgs> EvBuyerSwitched;
        public event EventHandler<SellerSwitchedEventArgs> EvSellerSwitched;
        public event EventHandler<MessageNotifyEventArgs> EvMessageNotity;
        public event EventHandler<RecieveNewMessageEventArgs> EvRecieveNewMessage;
        public event EventHandler<ShopRobotReceriveNewMessageEventArgs> EvShopRobotReceriveNewMessage;
        public static HashSet<QN> QNSet { get; set; }
        private static readonly object QNSetLock = new object();
        public string QnVersion { get; set; }

        private CDPClient cdp;
        public CDPClient CDP
        {
            get { return cdp; }
            set
            {
                // 千牛新版会同时打开多个 recent.html / iframe，多个 WebSocket session 会重复初始化。
                // 旧代码先把 cdp 字段替换成新对象，再 -= 事件，导致旧 CDP 事件没有真正解绑，
                // 后续同一个买家第二条消息可能由旧 session 触发、却用新 session 发送，表现为 Bot 右侧有答案但千牛未发送。
                if (cdp != null)
                {
                    cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                    cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                    cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                    cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                    cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;
                }

                cdp = value;
                if (cdp == null) return;

                cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;

                cdp.EvBuyerSwitched += Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity += Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage += Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched += Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage += Cdp_EvShopRobotReceriveNewMessage;
            }
        }

        private LocalUser _seller;
        public LocalUser Seller
        {
            get { return _seller; }
            set { _seller = value; }
        }

        public QNRpa rpa;
        public QNRpa Rpa { get { return rpa; } }
        public Conversation Buyer { get; set; }

        private readonly object _sellerEchoLock = new object();
        private string _lastSellerEchoBuyer = string.Empty;
        private string _lastSellerEchoText = string.Empty;
        private DateTime _lastSellerEchoTime = DateTime.MinValue;
        private readonly SemaphoreSlim _incomingMessageGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private const int ActiveBuyerConfirmDeadlineMs = 9000;
        private const int ActiveBuyerConfirmPollMs = 250;
        // Seller echoes may be repeated by several injected pages. Keep their transport-level
        // dedupe separate from buyer business ownership so a malformed/non-business duplicate page
        // can never consume a buyer key before the authoritative processing path claims it.
        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);
        // This is the single side-effect claim for buyer messages. Duplicate CDP pages are forwarded
        // into the authoritative QN instance, and only the path that reaches this ledger first may
        // enqueue/order-route the buyer event. Background recovery consults the same ledger.
        private readonly IncomingMessageDeduplicator _handledBuyerMessageDeduplicator =
            new IncomingMessageDeduplicator(4000);
        private readonly DateTime _messageSafetyStartedAt = DateTime.Now;
        private readonly VisionRequestService _visionRequestService = new VisionRequestService();
        private readonly BuyerMessageBurstCoordinator _buyerMessageBurstCoordinator;

        public static QN CurQN = null;

        static QN()
        {
            QNSet = new HashSet<QN>();
        }

        public QN(LocalUser seller)
        {
            this._seller = seller;
            this.rpa = new QNRpa(this);
            this._buyerMessageBurstCoordinator = new BuyerMessageBurstCoordinator(ProcessBuyerBurstAsync);
        }

        public async Task<bool> SendTextAsync(string buyer, string text)
        {
            var comparison = String.Compare(QnVersion, "9.19.06N", StringComparison.OrdinalIgnoreCase);
            if (comparison < 0)
            {
                SendTimiMsg(buyer, text);
                BotConnectionDiagnostics.RecordSendAttempt(true, "旧版接口发送");
                return true;
            }
            else
            {
                return await rpa.SendTextAsync(buyer, text);
            }
        }


        public Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)
        {
            return SendTextWithRetryAsync(buyer, text, retryCount, CancellationToken.None);
        }

        public async Task<bool> SendTextWithRetryAsync(
            string buyer,
            string text,
            int retryCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string segmentToken = "{分段符}";
            if (!string.IsNullOrEmpty(text) && text.IndexOf(segmentToken, StringComparison.Ordinal) >= 0)
            {
                var segments = text.Split(new[] { segmentToken }, StringSplitOptions.None)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (segments.Count == 0) return false;
                for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    var segment = segments[segmentIndex];
                    Log.Info("分段自动发送: buyer=" + buyer + ", segment=" + (segmentIndex + 1) + "/" + segments.Count);
                    if (!await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken)) return false;
                    if (segmentIndex + 1 < segments.Count) await Task.Delay(220, cancellationToken);
                }
                return true;
            }

            text = BotMessageSuffixService.Apply(
                Seller == null ? string.Empty : Seller.Nick,
                text);

            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                rpa.ResetSendFailure();
                if (!await EnsureActiveBuyerForSendAsync(buyer, cancellationToken))
                {
                    rpa.SetSendFailure("会话确认", "无法确认目标买家会话");
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var sendStartedAt = DateTime.Now;
                var ok = await SendTextAsync(buyer, text);
                if (!ok && rpa.LastSendWasCancelled)
                {
                    Log.Info("自动发送因人工接管取消，禁止重试: buyer=" + buyer
                        + ", reason=" + rpa.GetSendFailureReason());
                    return false;
                }
                if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))
                {
                    rpa.ResetSendFailure();
                    ok = true;
                    Log.Info("自动发送动作结果未确认，但已收到同买家同文本卖家回显，按真实送达处理并取消重试: buyer="
                        + buyer + ", text=" + text);
                }

                var retry = Math.Max(0, retryCount);
                for (var i = 0; !ok && i < retry; i++)
                {
                    Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer
                        + ", reason=" + rpa.GetSendFailureReason() + ", text=" + text);
                    cancellationToken.ThrowIfCancellationRequested();
                    rpa.InvalidateChatControls();
                    await Task.Delay(1800, cancellationToken);

                    // Seller echo is the source of truth. It can arrive after UIA timed out but
                    // before this retry window ends; sending again would create a duplicate.
                    if (HasRecentSellerEcho(buyer, text, sendStartedAt))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("重试前已收到同买家同文本卖家回显，按真实送达处理并取消重复发送: buyer="
                            + buyer + ", text=" + text);
                        break;
                    }

                    if (!await EnsureActiveBuyerForSendAsync(buyer, cancellationToken))
                    {
                        rpa.SetSendFailure("重试会话确认", "无法确认目标买家会话");
                        return false;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    ok = await SendTextAsync(buyer, text);
                    if (!ok && rpa.LastSendWasCancelled)
                    {
                        Log.Info("自动发送重试期间检测到人工接管，立即停止后续重试: buyer=" + buyer
                            + ", reason=" + rpa.GetSendFailureReason());
                        return false;
                    }
                    if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 900, cancellationToken))
                    {
                        rpa.ResetSendFailure();
                        ok = true;
                        Log.Info("重试动作结果未确认，但卖家回显已证明真实送达: buyer=" + buyer
                            + ", text=" + text);
                    }
                }

                if (!ok && await WaitForSellerEchoGraceAsync(buyer, text, sendStartedAt, 1400, cancellationToken))
                {
                    rpa.ResetSendFailure();
                    ok = true;
                    Log.Info("最终失败判定前收到延迟卖家回显，改判真实送达: buyer=" + buyer
                        + ", text=" + text);
                }
                if (!ok)
                {
                    Log.Error("自动发送最终失败: buyer=" + buyer + ", reason=" + rpa.GetSendFailureReason());
                }
                return ok;
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task<bool> WaitForSellerEchoGraceAsync(
            string buyer,
            string text,
            DateTime since,
            int milliseconds,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.Now.AddMilliseconds(Math.Max(0, milliseconds));
            while (DateTime.Now <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasRecentSellerEcho(buyer, text, since)) return true;
                await Task.Delay(120, cancellationToken);
            }
            return HasRecentSellerEcho(buyer, text, since);
        }

        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer, CancellationToken cancellationToken)
        {
            buyer = (buyer ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer) || cdp == null) return false;
            var sellerNick = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ActiveBuyerConfirmDeadlineMs);

            for (var attempt = 0; attempt < 22 && DateTime.UtcNow < deadlineUtc; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var current = await GetCurrentConversationID();
                    var currentConversation = current == null ? null : current.Result;
                    var currentNick = currentConversation == null ? string.Empty : (currentConversation.Nick ?? string.Empty).Trim();
                    if (currentConversation != null)
                    {
                        BuyerIdentityAliasService.Observe(
                            sellerNick,
                            currentConversation.Nick,
                            currentConversation.Display,
                            currentConversation.TargetId);
                    }
                    if (BuyerIdentityAliasService.AreEquivalent(sellerNick, currentNick, buyer))
                    {
                        SetActiveConversationByNick(sellerNick, currentNick, "sendVerified");
                        return true;
                    }
                    if (attempt == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);
                        OpenChat(buyer);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("发送前确认买家会话失败: " + ex.Message);
                    if (attempt == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        OpenChat(buyer);
                    }
                }

                var remainingMs = (int)Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                if (remainingMs <= 0) break;
                await Task.Delay(Math.Min(ActiveBuyerConfirmPollMs, remainingMs), cancellationToken);
            }

            Log.Error("发送已阻止：无法在会话确认总预算内确认当前会话为目标买家。target=" + buyer
                + ", deadlineMs=" + ActiveBuyerConfirmDeadlineMs);
            return false;
        }

        public async void SendImageAsync(string buyer, string imagePath)
        {
            await rpa.SendImageAsync(buyer, imagePath);
        }

        private static string GetMessageText(QNChatMessage m)
        {
            if (m == null) return string.Empty;
            try
            {
                if (m.originalData != null)
                {
                    var text = m.originalData.text ?? string.Empty;
                    if (m.originalData.header != null)
                    {
                        text += m.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (m.summary ?? string.Empty).Trim();
        }

        private static string NormalizeMessageText(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        }

        private void RecordSellerEcho(string buyerNick, string text)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return;

            lock (_sellerEchoLock)
            {
                _lastSellerEchoBuyer = buyerNick;
                _lastSellerEchoText = text;
                _lastSellerEchoTime = DateTime.Now;
            }
            Log.Info("已记录卖家消息回显: buyer=" + buyerNick + ", text=" + text);
        }

        public bool HasRecentSellerEcho(string buyerNick, string text, DateTime since)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return false;

            lock (_sellerEchoLock)
            {
                if (_lastSellerEchoTime < since.AddMilliseconds(-500)) return false;
                var sellerNick = Seller == null ? string.Empty : (Seller.Nick ?? string.Empty).Trim();
                if (!BuyerIdentityAliasService.AreEquivalent(sellerNick, _lastSellerEchoBuyer, buyerNick)) return false;
                return _lastSellerEchoText == text;
            }
        }

        private bool IsBuyerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && m.fromid.nick != _seller.Nick && m.toid.nick == _seller.Nick;
        }

        private bool IsSellerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && m.fromid.nick == _seller.Nick && m.toid.nick != _seller.Nick;
        }

        public void SetActiveConversationByNick(string sellerNick, string buyerNick, string source)
        {
            try
            {
                sellerNick = (sellerNick ?? string.Empty).Trim();
                buyerNick = (buyerNick ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sellerNick) && string.IsNullOrWhiteSpace(buyerNick)) return;

                string nonBuyerReason;
                if (NonBuyerConversationGuard.ShouldBlockIdentity(sellerNick, buyerNick, out nonBuyerReason))
                {
                    Log.Info("非买家会话身份已拒绝，不更新当前buyer: source=" + source + ", reason=" + nonBuyerReason);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(sellerNick) && (_seller == null || _seller.Nick != sellerNick))
                {
                    _seller = new LocalUser { Nick = sellerNick, Display = sellerNick };
                }

                if (!string.IsNullOrWhiteSpace(buyerNick) && (Buyer == null || Buyer.Nick != buyerNick))
                {
                    Buyer = new Conversation { Nick = buyerNick, Display = buyerNick };
                }

                CurQN = this;
                BotConnectionDiagnostics.RecordBuyerSeller(_seller == null ? sellerNick : _seller.Nick, Buyer == null ? buyerNick : Buyer.Nick);

                try
                {
                    // Desk.Inst is replaced/disposed during startup reconciliation. Snapshot it once;
                    // checking Desk.Inst and then dereferencing it again creates a classic TOCTOU null
                    // race that was observed in the 1.1.1406 field log.
                    var desk = Desk.Inst;
                    if (desk != null)
                    {
                        if (_seller != null && !string.IsNullOrWhiteSpace(_seller.Nick)) desk.ChangeSeller(_seller.Nick);
                        if (Buyer != null && !string.IsNullOrWhiteSpace(Buyer.Nick)) desk.ChangeBuyer(Buyer.Nick);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }

                Log.Info("当前会话已更新: source=" + source + ", seller=" + (_seller == null ? string.Empty : _seller.Nick) + ", buyer=" + (Buyer == null ? string.Empty : Buyer.Nick));
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            string nonBuyerReason;
            if (e != null && e.Seller != null && e.Buyer != null
                && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家后台消息通知已丢弃，不触发首问/补偿/学习: reason=" + nonBuyerReason);
                return;
            }

            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
            // 绝不能在这里修改 QN.Buyer 或提前打开聊天，否则后台买家的答案可能发到当前可见买家。
            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
                ScheduleBackgroundMessageRecovery(e);
            }
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, e);
            }
        }

        private void Cdp_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            CurQN = this;
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("卖家切换事件携带非买家会话，保留当前真实buyer: reason=" + nonBuyerReason);
                return;
            }
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "sellerSwitched");

            if (EvSellerSwitched != null)
            {
                EvSellerSwitched(this, e);
            }
        }

        private Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(
                message,
                _seller == null ? string.Empty : _seller.Nick,
                messageText,
                out nonBuyerReason))
            {
                Log.Info("非买家普通入站消息已丢弃，未进入订单/首问/商品链接/AI链: reason=" + nonBuyerReason);
                return Task.CompletedTask;
            }
            BuyerIdentityAliasService.ObserveMessage(_seller == null ? string.Empty : _seller.Nick, message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);

            // Classify before consuming any transport dedupe key. A duplicate/partially hydrated CDP
            // page can emit a frame that is not yet a valid buyer business event; allowing that frame
            // to reserve the key would make the later authoritative copy disappear. Seller echoes use
            // the transport ledger, while valid buyer messages use the handled-business ledger below.
            if (IsSellerMessage(message))
            {
                if (!_incomingMessageDeduplicator.TryAccept(messageKey))
                {
                    Log.Info("重复卖家回显已跳过: key=" + messageKey);
                    return Task.CompletedTask;
                }
                ConversationContextStore.RefreshAndRecord(message, messageText);
                RecordSellerEcho(message.toid.nick, messageText);
                return Task.CompletedTask;
            }
            if (!IsBuyerMessage(message)) return Task.CompletedTask;

            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var detectedAt = DateTime.Now;

            // This is the authoritative business claim. It is intentionally the first dedupe write
            // on the buyer path and is shared with background recovery.
            if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("已实际处理的买家消息不再重复入队: key=" + messageKey);
                return Task.CompletedTask;
            }
            MarkBuyerMessageObserved(sellerNick, buyerNick);

            OrderPlacedReplyPlan orderPlan;
            if (OrderPlacedAutoReplyService.TryCreatePlan(
                message,
                messageText,
                sellerNick,
                buyerNick,
                _messageSafetyStartedAt,
                out orderPlan))
            {
                if (orderPlan != null && orderPlan.IsBuyerFollowUp)
                    ResponseProgressTracker.ObserveNewBuyerTurn(sellerNick, buyerNick);
                return orderPlan == null
                    ? Task.CompletedTask
                    : ProcessOrderPlacedReplyAsync(orderPlan);
            }

            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
            var displayQuestion = IncomingMessageSafety.GetDisplayText(message, messageText);
            var visionDecision = VisionMessageDecision.Decide(
                message,
                messageText,
                decision,
                AiEndpointStore.GetVisionEnabledEndpoints());

            if (!Params.Robot.CanUseRobotReal)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                return Task.CompletedTask;
            }

            if (visionDecision.Kind == VisionDecisionKind.Skip
                && !IncomingMessageSafety.IsMediaPlaceholder(displayQuestion))
            {
                AddSkippedConversation(sellerNick, buyerNick, visionDecision.QuestionLabel, visionDecision.Note);
                Log.Info("买家消息安全跳过: buyer=" + buyerNick + ", reason=" + visionDecision.Note);
                return Task.CompletedTask;
            }

            ResponseProgressTracker.ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt);
            if (visionDecision.Kind == VisionDecisionKind.Text)
            {
                BotFlowTestService.RecordCandidate(sellerNick, buyerNick, displayQuestion, detectedAt);
            }
            Log.Info("买家消息已识别并展示: seller=" + sellerNick + ", buyer=" + buyerNick
                + ", detectedAt=" + detectedAt.ToString("HH:mm:ss.fff") + ", question=" + displayQuestion);

            _buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem
            {
                SellerNick = sellerNick,
                BuyerNick = buyerNick,
                MessageKey = messageKey,
                DisplayText = displayQuestion,
                Message = message,
                SafetyDecision = decision,
                VisionDecision = visionDecision,
                SortValue = IncomingMessageSafety.GetSortValue(message),
                ReceivedAt = detectedAt
            });
            return Task.CompletedTask;
        }

        private async Task ProcessBuyerBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (burst == null || burst.Items.Count < 1 || string.IsNullOrWhiteSpace(burst.CombinedQuestion)) return;

            if (!burst.HasReplyableItem)
            {
                if (!lease.IsCurrent) return;
                var note = "已合并收到买家的媒体消息，但当前未配置对应内容理解能力，未自动回复。";
                AddSkippedConversation(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, note);
                Log.Info("买家媒体消息合并跳过: buyer=" + burst.BuyerNick + ", messages=" + burst.CombinedQuestion.Replace("\n", " | "));
                return;
            }

            var visionItem = burst.LatestVisionItem;
            if (visionItem != null)
            {
                await ProcessVisionBurstAsync(lease, visionItem);
                return;
            }
            await ProcessTextBurstAsync(lease);
        }

        private async Task ProcessTextBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            var aiStartedAt = DateTime.Now;
            Log.Info("文本回复开始: buyer=" + burst.BuyerNick + ", queueMs="
                + Math.Max(0, (long)(aiStartedAt - detectedAt).TotalMilliseconds));

            string answer;
            var usedFirstInquiryFixedReply = FirstInquiryFixedReplyService.TryResolve(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                out answer);
            if (usedFirstInquiryFixedReply)
            {
                answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);
                KnowledgeLearningService.RegisterAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    "首条咨询固定回复");
                Log.Info("首条咨询固定回复已命中，跳过AI调用: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick);
            }
            else
            {
                answer = await Task.Run(() => MyOpenAI.GetAnswer(
                    burst.SellerNick,
                    burst.BuyerNick,
                    string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion,
                    true));
            }

            if (!lease.IsCurrent)
            {
                Log.Info("买家在AI生成期间发送了新消息，旧文本草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            if (!usedFirstInquiryFixedReply)
            {
                var deduplication = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer);
                answer = deduplication.Answer;
            }

            if (!await lease.ConfirmStableAsync(220))
            {
                Log.Info("发送前发现买家补充了新消息，旧文本答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            // Defensive legacy path: never publish an error/empty model result as AnswerReady.
            // BuyerStreamingReplyPipeline normally replaces this handler, but if that patch is ever
            // unavailable the fallback must preserve the same terminal failure semantics.
            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                var failure = string.IsNullOrWhiteSpace(answer) ? "错误：AI未返回有效答案。" : answer;
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing("AI未生成可用答案");
                    conversationCtl.SetStatus(failure, false);
                }
                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, failure);
                Log.Info("旧文本回复路径AI失败，保持失败态且不进入答案就绪/完成: buyer="
                    + burst.BuyerNick);
                return;
            }

            var answerReadyAt = DateTime.Now;
            var answerSource = KnowledgeLearningService.ResolveAnswerSource(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            conversationCtl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                answerSource,
                detectedAt,
                answerReadyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("文本答案已生成: buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds)
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.IsCurrent)
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))
                {
                    KnowledgeLearningService.QueueLearn(
                        burst.CombinedQuestion,
                        answer,
                        "AI生成",
                        burst.SellerNick,
                        burst.BuyerNick);
                }
            }
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(sendOk, sendOk ? "已发送（合并本轮买家消息）" : "发送失败：" + rpa.GetSendFailureReason());
            }
            Log.Info("文本真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
        }

        private async Task ProcessVisionBurstAsync(
            BuyerMessageBurstLease lease,
            BuyerMessageBurstItem visionItem)
        {
            var burst = lease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            var task = new VisionReplyTask
            {
                SellerNick = burst.SellerNick,
                BuyerNick = burst.BuyerNick,
                MessageKey = visionItem.MessageKey,
                Message = visionItem.Message,
                CombinedQuestion = burst.CombinedQuestion,
                DeferLearningUntilDelivered = true
            };
            var result = await _visionRequestService.ExecuteAsync(task, CancellationToken.None);
            if (!lease.IsCurrent)
            {
                Log.Info("买家在视觉AI生成期间发送了新消息，旧视觉草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                var note = "已跳过：" + (string.IsNullOrWhiteSpace(result.Error) ? "视觉识别失败" : result.Error) + "，未向买家发送消息。";
                ResponseProgressTracker.Fail(burst.SellerNick, burst.BuyerNick, note);
                Log.Info("视觉消息跳过: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", messageId=" + visionItem.MessageKey + ", endpoint=" + result.EndpointName + ", model=" + result.VisionModel + ", latencyMs=" + result.LatencyMs + ", reason=" + result.Error);
                return;
            }

            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                result.Answer);
            var answer = deduplication.Answer;
            if (!await lease.ConfirmStableAsync(220))
            {
                Log.Info("发送前发现买家补充了新消息，旧视觉答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var source = deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                ? deduplication.Source
                : "AI生成";
            var answerReadyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                source,
                detectedAt,
                answerReadyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("视觉答案已生成: buyer=" + burst.BuyerNick
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds)
                + ", visionApiMs=" + result.LatencyMs);
            if (!autoSend)
            {
                if (ctl != null) ctl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }
            if (!lease.IsCurrent)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                KnowledgeLearningService.QueueLearn(
                    burst.CombinedQuestion,
                    answer,
                    "视觉AI",
                    burst.SellerNick,
                    burst.BuyerNick);
            }
            if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "识别完成，但目标买家会话未确认，未发送。原因：" + rpa.GetSendFailureReason());
            Log.Info("视觉真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
        }

        private void AddSkippedConversation(string seller, string buyer, string question, string note)
        {
            if (Desk.Inst == null) return;
            var ctl = Desk.Inst.AddConversation(seller, buyer, question, note, false);
            if (ctl != null) ctl.SetSkipped(note);
        }

        private async void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            await _incomingMessageGate.WaitAsync();
            try
            {
                if (EvRecieveNewMessage != null)
                {
                    EvRecieveNewMessage(this, e);
                }

                Log.Info("收到千牛新消息事件: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));
                var chatRes = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (chatRes == null || chatRes.result == null)
                {
                    Log.Error("收到新消息但无法解析: payloadLength=" + ((e == null || e.Message == null) ? 0 : e.Message.Length));
                    return;
                }

                var messages = chatRes.result
                    .Where(m => m != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();

                // 同一批次和随后几秒到达的消息全部进入按买家隔离的聚合器。
                // 聚合器只在买家停止输入后生成一次答案，不再丢弃较早的短片段。
                foreach (var message in messages)
                {
                    await ProcessIncomingMessageAsync(message);
                    await Task.Delay(30);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {
                _incomingMessageGate.Release();
            }
        }

        private void Cdp_EvMessageNotity(object sender, MessageNotifyEventArgs e)
        {
            if (EvMessageNotity != null)
            {
                EvMessageNotity(this, e);
            }
        }

        private void Cdp_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            CurQN = this;
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家会话切换已拒绝，不污染当前buyer也不触发买家切换订阅: reason=" + nonBuyerReason);
                return;
            }
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "buyerSwitched");
            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, e);
            }
        }

        public static QN GetByNick(LocalUser seller)
        {
            if (seller == null) throw new ArgumentNullException("seller");
            lock (QNSetLock)
            {
                var qn = QNSet.FirstOrDefault(q => q._seller != null && (q._seller.Nick == seller.Nick || q._seller.Display == seller.Display));
                if (qn == null)
                {
                    qn = new QN(seller);
                    QNSet.Add(qn);
                }
                return qn;
            }
        }

        public static QN FindExistingBySellerNick(string sellerNick)
        {
            if (string.IsNullOrWhiteSpace(sellerNick)) return null;
            lock (QNSetLock)
            {
                return QNSet.FirstOrDefault(q => q._seller != null && (q._seller.Nick == sellerNick || q._seller.Display == sellerNick));
            }
        }

        public void SendTimiMsg(string userId, string smartTip)
        {
            cdp.SendTimiMsg(userId, smartTip);
        }

        public void TransferContact(string contactID, string targetID, string reason = "")
        {
            cdp.TransferContact(contactID, targetID, reason);
        }

        public void LightOff(string ccode)
        {
            cdp.LightOff(ccode);
        }

        public void MarkRead(string ccode, string clientId, string messageId)
        {
            cdp.MarkRead(ccode, clientId, messageId);
        }

        public async Task<LocalUserResponse> GetCurrentUser()
        {
            return await cdp.GetCurrentUser();
        }

        public void InsertText2Inputbox(string uid, string text)
        {
            cdp.InsertText2Inputbox(uid, text);
        }

        public async Task<bool> IsInputboxEmpty()
        {
            return await cdp.IsInputboxEmpty();
        }

        public void BrowserUrl(string url)
        {
            cdp.BrowserUrl(url);
        }

        public void SendRemindPayCard(string encryptedBuyerId, string orderId)
        {
            cdp.SendRemindPayCard(encryptedBuyerId, orderId);
        }

        public void RecallMessage(string ccode, string clientId, string messageId)
        {
            cdp.RecallMessage(ccode, clientId, messageId);
        }

        public void OpenChat(string nick)
        {
            cdp.OpenChat(nick);
        }

        public void SendCoupon(string buyerNick, string activityId)
        {
            cdp.SendCoupon(buyerNick, activityId);
        }

        public void CloseChat(string contactID)
        {
            cdp.CloseChat(contactID);
        }

        public void GetRemoteHisMsg(string ccode)
        {
            cdp.GetRemoteHisMsg(ccode);
        }

        public async Task<AccountStatusResponse> GetAccountStatus()
        {
            return await cdp.GetAccountStatus();
        }

        public async Task<ItemRecordResponse> GetItemRecords(string encryptId)
        {
            return await cdp.GetItemRecords(encryptId);
        }

        public async Task<SearchUserResponse> SearchBuyerUser(string searchQuery)
        {
            return await cdp.SearchBuyerUser(searchQuery);
        }

        public async Task<BuyerInfoResponse> GetBuyerInfo(string encryptId)
        {
            return await cdp.GetBuyerInfo(encryptId);
        }

        public async Task<ZnkfTradeQueryResponse> GetBuyerTrades(string securityBuyerUid, string bizOrderId)
        {
            return await cdp.GetBuyerTrades(securityBuyerUid, bizOrderId);
        }

        public async Task<ConversationResponse> GetCurrentConversationID()
        {
            return await cdp.GetCurrentConversationID();
        }
    }
}