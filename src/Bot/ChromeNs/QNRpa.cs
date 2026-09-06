using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Bot.Automation.ChatDeskNs;
using System.Windows;
using Bot.Automation;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const int CdpQuickProbeTimeoutMs = 900;
        private const int CdpActionTimeoutMs = 4500;
        private const int UiActionTimeoutMs = 1800;
        private const int UiMutationTimeoutMs = 4500;

        private sealed class CdpInputboxProbe
        {
            public bool Completed;
            public bool IsEmpty;
        }

        private DateTime _preUpdateChatBrowserRectTime;
        private DateTime _preSendPlainTextAndImageTime;
        private BitmapImage _preSendPlainTextAndImageImage;
        public DateTime LatestSetTextTime;

        private AutomationElement _sendMessageButton;
        private System.Drawing.Rectangle _sendMessageButtonRect;
        private bool _lastSendButtonCoordinateClickRejected;
        private AutomationElement _closeContactButton;
        private TextBox _messageInputTextArea;

        private FlaUI.Core.Application automationApplication;
        private UIA3Automation uia3Automation;

        private static readonly ConcurrentDictionary<string, DateTime> AnswerAttemptStartedAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan OwnedDraftRetention = TimeSpan.FromMinutes(30);

        // A timed-out UI mutation must never be followed by a second concurrent mutation.
        // Keep the original worker leased until it actually exits; retries fail fast meanwhile.
        private readonly object _uiMutationLock = new object();
        private Task<bool> _activeUiMutationTask;

        private string _lastOwnedDraftBuyer = string.Empty;
        private string _lastOwnedDraftText = string.Empty;
        private DateTime _lastOwnedDraftAt = DateTime.MinValue;

        public string LastSetPlainText { get; private set; }

        private readonly QN _qn;

        public QNRpa(QN qn)
        {
            _qn = qn ?? throw new ArgumentNullException("qn");
            uia3Automation = new UIA3Automation();
            if (!EnsureSellerDeskBinding(false))
            {
                Log.Info("RPA初始化时卖家窗口尚未就绪，延后绑定: seller=" + SellerNick);
            }
            UpdateChatBrowserRect(true);
        }

        private bool IsSendButtonName(string name)
        {
            name = (name ?? string.Empty).Trim();
            return name == "发送" || name == "發送" || name.Equals("Send", StringComparison.OrdinalIgnoreCase);
        }

        public async void UpdateChatBrowserRect(bool force = false)
        {
            await RefreshChatControlsAsync(force).ConfigureAwait(false);
        }

        private static void PressCtrlA()
        {
            WinApi.Api.keybd_event(0x11, 0, 0, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x41, 0, 0, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x41, 0, 2, 0);
            Thread.Sleep(30);
            WinApi.Api.keybd_event(0x11, 0, 2, 0);
        }

        private static void PressBackspace()
        {
            WinApi.Api.keybd_event(0x08, 0, 0, 0);
            Thread.Sleep(50);
            WinApi.Api.keybd_event(0x08, 0, 2, 0);
        }

        private string GetEditorTextSafe()
        {
            string text;
            return TryGetEditorText(out text) ? text : string.Empty;
        }

        private bool IsEditorEmptySafe()
        {
            string text;
            return TryGetEditorText(out text) && string.IsNullOrWhiteSpace(text);
        }

        private bool HasOwnedRecentDraft(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length > 0
                && string.Equals((LastSetPlainText ?? string.Empty).Trim(), text, StringComparison.Ordinal)
                && LatestSetTextTime != DateTime.MinValue
                && (DateTime.Now - LatestSetTextTime).TotalSeconds <= 20;
        }

        private void RememberOwnedDraft(string buyer, string text)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            text = (text ?? string.Empty).Trim();
            _lastOwnedDraftBuyer = buyer ?? string.Empty;
            _lastOwnedDraftText = text;
            _lastOwnedDraftAt = text.Length == 0 ? DateTime.MinValue : DateTime.Now;
            LastSetPlainText = text;
            LatestSetTextTime = _lastOwnedDraftAt;
        }

        private void ForgetOwnedDraft()
        {
            _lastOwnedDraftBuyer = string.Empty;
            _lastOwnedDraftText = string.Empty;
            _lastOwnedDraftAt = DateTime.MinValue;
            LastSetPlainText = string.Empty;
            LatestSetTextTime = DateTime.MinValue;
        }

        private bool IsOwnedDraftForBuyer(string buyer, string currentText)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            var ownedBuyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _lastOwnedDraftBuyer);
            var ownedText = (_lastOwnedDraftText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer)
                || string.IsNullOrWhiteSpace(ownedBuyer)
                || ownedText.Length == 0
                || _lastOwnedDraftAt == DateTime.MinValue
                || DateTime.Now - _lastOwnedDraftAt > OwnedDraftRetention)
            {
                return false;
            }
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, ownedBuyer, buyer)) return false;
            return EditorMatchesExpectedText(currentText, ownedText);
        }

        internal bool IsKnownBotOwnedDraftText(string currentText)
        {
            var expected = (LastSetPlainText ?? string.Empty).Trim();
            return expected.Length > 0
                && !string.IsNullOrWhiteSpace(LastSendFailureReason)
                && !LastSendWasCancelled
                && EditorMatchesExpectedText(currentText, expected);
        }

        internal async Task<bool> IsKnownBotOwnedDraftAsync()
        {
            if (string.IsNullOrWhiteSpace(LastSetPlainText)) return false;
            if (_messageInputTextArea == null)
            {
                await RefreshChatControlsAsync(false).ConfigureAwait(false);
            }
            string current;
            return TryGetEditorText(out current) && IsKnownBotOwnedDraftText(current);
        }

        private async Task<CdpInputboxProbe> ProbeInputboxEmptyAsync(string stage, int timeoutMs)
        {
            var result = new CdpInputboxProbe();
            if (_qn == null || _qn.CDP == null) return result;

            Task<bool> probeTask;
            try
            {
                probeTask = _qn.IsInputboxEmpty();
            }
            catch (Exception ex)
            {
                Log.Info("CDP检查输入框启动失败: stage=" + stage + ", " + ex.Message);
                return result;
            }

            var winner = await Task.WhenAny(probeTask, Task.Delay(Math.Max(250, timeoutMs))).ConfigureAwait(false);
            if (winner != probeTask)
            {
                Log.Info("CDP检查输入框超时，已放弃等待且不会阻塞UI线程: stage=" + stage
                    + ", timeoutMs=" + timeoutMs + ", seller=" + SellerNick);
                return result;
            }

            try
            {
                result.IsEmpty = await probeTask.ConfigureAwait(false);
                result.Completed = true;
            }
            catch (Exception ex)
            {
                Log.Info("CDP检查输入框失败: stage=" + stage + ", " + ex.Message);
            }
            return result;
        }

        private async Task<bool> RunCdpActionAsync(Action action, string stage, int timeoutMs)
        {
            if (action == null) return false;
            Task worker;
            try
            {
                worker = Task.Run(action);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }

            var winner = await Task.WhenAny(worker, Task.Delay(Math.Max(500, timeoutMs))).ConfigureAwait(false);
            if (winner != worker)
            {
                SetSendFailure(stage, "千牛CDP调用超时，已停止等待以保护Bot界面");
                Log.Info(stage + "超时，后台调用后续由CDP自身超时/重连机制回收。seller=" + SellerNick);
                return false;
            }

            try
            {
                await worker.ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private async Task<bool> RunUiActionAsync(Func<bool> action, string stage, int timeoutMs)
        {
            if (action == null) return false;
            Task<bool> worker;
            try
            {
                worker = Task.Run(action);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }

            var winner = await Task.WhenAny(worker, Task.Delay(Math.Max(250, timeoutMs))).ConfigureAwait(false);
            if (winner != worker)
            {
                SetSendFailure(stage, "千牛UIA操作超时，已停止等待以保护Bot界面");
                return false;
            }

            try
            {
                return await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> RunUiMutationAsync(Func<bool> action, string stage)
        {
            if (action == null) return false;

            Task<bool> worker;
            lock (_uiMutationLock)
            {
                if (_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted)
                {
                    SetSendFailure(stage, "上一条UI草稿修改仍在安全退出，禁止并发启动新的草稿修改");
                    Log.Info(stage + "已快速失败：上一条UI草稿修改仍在后台退出，避免两个任务并发清空/覆盖输入框。seller="
                        + SellerNick);
                    return false;
                }

                try
                {
                    worker = Task.Run(action);
                    _activeUiMutationTask = worker;
                }
                catch (Exception ex)
                {
                    SetSendFailure(stage, ex.Message);
                    return false;
                }
            }

            var winner = await Task.WhenAny(worker, Task.Delay(UiMutationTimeoutMs)).ConfigureAwait(false);
            if (winner != worker)
            {
                // COM/UIA cannot be safely aborted. Release the send wait after a bounded interval,
                // but retain the single mutation lease until the original worker actually exits.
                SetSendFailure(stage, "UI草稿修改超过" + UiMutationTimeoutMs
                    + "ms，已停止等待；原任务保持独占租约直到安全退出");
                Log.Info(stage + "等待超时，发送链路已释放等待但保留单一UI修改租约: seller="
                    + SellerNick + ", timeoutMs=" + UiMutationTimeoutMs);
                worker.ContinueWith(completed =>
                {
                    lock (_uiMutationLock)
                    {
                        if (ReferenceEquals(_activeUiMutationTask, worker)) _activeUiMutationTask = null;
                    }
                    Log.Info(stage + "超时后的UI草稿修改任务已退出，可接受后续草稿任务: seller=" + SellerNick);
                }, TaskScheduler.Default);
                return false;
            }

            try
            {
                return await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                Log.Info(stage + "失败: " + ex.Message);
                return false;
            }
            finally
            {
                lock (_uiMutationLock)
                {
                    if (ReferenceEquals(_activeUiMutationTask, worker)) _activeUiMutationTask = null;
                }
            }
        }

        private async Task<bool> HasExpectedDraftFastAsync(string text, int probeTimeoutMs)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            var probe = await ProbeInputboxEmptyAsync("草稿确认", probeTimeoutMs).ConfigureAwait(false);
            if (probe.Completed)
            {
                if (probe.IsEmpty) return false;
                if (HasOwnedRecentDraft(text)) return true;
            }
            else if (HasOwnedRecentDraft(text))
            {
                Log.Info("CDP草稿检查超时，使用本次Bot草稿租约继续发送: buyer="
                    + (_qn == null || _qn.Buyer == null ? string.Empty : _qn.Buyer.Nick));
                return true;
            }

            return await RunUiActionAsync(() => HasExpectedDraft(text), "UIA草稿确认", UiActionTimeoutMs).ConfigureAwait(false);
        }

        private async Task<bool> WaitForTextSendConfirmedAsync(string buyer, string text, DateTime sendStart, string method, int timeoutMs)
        {
            var end = DateTime.Now.AddMilliseconds(timeoutMs);
            var cdpAvailable = true;
            var draftClearedObserved = false;
            while (DateTime.Now < end)
            {
                try
                {
                    if (_qn != null && _qn.HasRecentSellerEcho(buyer, text, sendStart))
                    {
                        BotConnectionDiagnostics.RecordSendAttempt(true, method + "，卖家消息已回显");
                        Log.Info(method + "发送确认成功：已收到卖家消息回显。buyer=" + buyer);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("检查卖家消息回显失败: " + ex.Message);
                }

                if (cdpAvailable)
                {
                    var remaining = Math.Max(250, (int)(end - DateTime.Now).TotalMilliseconds);
                    var probe = await ProbeInputboxEmptyAsync(method + "发送确认", Math.Min(1000, remaining)).ConfigureAwait(false);
                    if (probe.Completed)
                    {
                        if (probe.IsEmpty && !draftClearedObserved)
                        {
                            draftClearedObserved = true;
                            var extendedEnd = DateTime.Now.AddMilliseconds(4500);
                            if (extendedEnd > end) end = extendedEnd;
                            Log.Info(method + "发送动作已触发：输入框已清空，继续等待同买家同文本卖家回显。buyer=" + buyer);
                        }
                    }
                    else
                    {
                        cdpAvailable = false;
                    }
                }

                await Task.Delay(150).ConfigureAwait(false);
            }

            SetSendFailure(
                "发送确认",
                method + "后未检测到卖家消息回显；draftClearedObserved=" + draftClearedObserved
                    + "；cdpAvailable=" + cdpAvailable);
            Log.Info(method + "发送未获真实回显确认，buyer=" + buyer
                + ", draftClearedObserved=" + draftClearedObserved);
            return false;
        }

        private bool TryClickCachedSendButtonNow()
        {
            _lastSendButtonCoordinateClickRejected = false;
            if (_sendMessageButton == null && _sendMessageButtonRect.IsEmpty) return false;
            try
            {
                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    Log.Info("发送主按钮点击失败：当前卖家千牛窗口未绑定。seller=" + SellerNick);
                    return false;
                }

                sellerDesk.BringTop();
                Thread.Sleep(120);

                var rect = _sendMessageButtonRect;
                if ((rect.Width <= 0 || rect.Height <= 0) && _sendMessageButton != null)
                {
                    rect = _sendMessageButton.BoundingRectangle;
                }
                if (rect.Width <= 0 || rect.Height <= 0) return false;

                var arrowGuard = Math.Max(18, Math.Min(30, rect.Width / 3));
                var mainWidth = rect.Width - arrowGuard;
                if (mainWidth < 16)
                {
                    Log.Info("发送按钮区域过窄，已阻止可能误点下拉箭头: rect="
                        + rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height);
                    return false;
                }
                var x = rect.Left + Math.Max(8, Math.Min(mainWidth / 2, mainWidth - 8));
                var y = rect.Top + rect.Height / 2;
                Log.Info("发送主按钮左侧区域坐标点击: seller=" + SellerNick
                    + ", rect=" + rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height
                    + ", click=" + x + "," + y + ", arrowGuard=" + arrowGuard);
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = x, Y = y });
                return true;
            }
            catch (Exception ex)
            {
                _lastSendButtonCoordinateClickRejected = true;
                Log.Info("发送主按钮坐标点击异常: " + ex.Message
                    + ", type=" + ex.GetType().FullName
                    + ", hresult=0x" + ex.HResult.ToString("X8"));
                return false;
            }
        }

        private bool TryInvokeCachedSendButtonNow()
        {
            if (_sendMessageButton == null || uia3Automation == null) return false;
            try
            {
                var splitRect = _sendMessageButtonRect;
                if ((splitRect.Width <= 0 || splitRect.Height <= 0) && _sendMessageButton != null)
                {
                    splitRect = _sendMessageButton.BoundingRectangle;
                }
                if (splitRect.Width <= 0 || splitRect.Height <= 0) return false;

                var arrowGuard = Math.Max(18, Math.Min(30, splitRect.Width / 3));
                var mainWidth = splitRect.Width - arrowGuard;
                if (mainWidth < 16) return false;
                var x = splitRect.Left + Math.Max(8, Math.Min(mainWidth / 2, mainWidth - 8));
                var y = splitRect.Top + splitRect.Height / 2;

                AutomationElement candidate = null;
                try
                {
                    candidate = uia3Automation.FromPoint(new System.Drawing.Point(x, y));
                }
                catch (Exception ex)
                {
                    Log.Info("发送主按钮左侧UIA命中测试失败: " + ex.Message);
                }

                for (var depth = 0; candidate != null && depth < 7; depth++)
                {
                    if (TryInvokeSafeMainSendCandidate(candidate, splitRect, x, y, arrowGuard))
                    {
                        return true;
                    }
                    try { candidate = candidate.Parent; }
                    catch { candidate = null; }
                }

                try
                {
                    foreach (var child in _sendMessageButton.FindAllDescendants())
                    {
                        if (TryInvokeSafeMainSendCandidate(child, splitRect, x, y, arrowGuard))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("发送主按钮左侧UIA子控件扫描失败: " + ex.Message);
                }

                Log.Info("未找到可证明属于左侧主发送区域的UIA子控件，禁止Invoke整块分裂按钮: seller="
                    + SellerNick + ", rect=" + splitRect.Left + "," + splitRect.Top + ","
                    + splitRect.Width + "x" + splitRect.Height + ", safePoint=" + x + "," + y);
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("发送按钮左侧UIA安全调用失败: " + ex.Message
                    + ", type=" + ex.GetType().FullName
                    + ", hresult=0x" + ex.HResult.ToString("X8"));
                return false;
            }
        }

        private bool TryInvokeSafeMainSendCandidate(
            AutomationElement candidate,
            System.Drawing.Rectangle splitRect,
            int safeX,
            int safeY,
            int arrowGuard)
        {
            if (!IsSafeMainSendInvokeCandidate(candidate, splitRect, safeX, safeY, arrowGuard))
            {
                return false;
            }

            try
            {
                var rect = candidate.BoundingRectangle;
                var name = candidate.Name ?? string.Empty;
                var automationId = candidate.AutomationId ?? string.Empty;
                candidate.AsButton().Invoke();
                Log.Info("已通过左侧主发送UIA子控件执行发送: seller=" + SellerNick
                    + ", name=" + name + ", automationId=" + automationId
                    + ", rect=" + rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("左侧主发送UIA候选不可Invoke，继续寻找父级/兄弟候选: " + ex.Message);
                return false;
            }
        }

        private static bool IsSafeMainSendInvokeCandidate(
            AutomationElement candidate,
            System.Drawing.Rectangle splitRect,
            int safeX,
            int safeY,
            int arrowGuard)
        {
            if (candidate == null) return false;
            try
            {
                var rect = candidate.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(safeX, safeY)) return false;

                var name = candidate.Name ?? string.Empty;
                var automationId = candidate.AutomationId ?? string.Empty;
                var identity = (name + " " + automationId).ToLowerInvariant();
                if (identity.Contains("arrow")
                    || identity.Contains("dropdown")
                    || identity.Contains("drop-down")
                    || identity.Contains("menu")
                    || identity.Contains("downbutton")
                    || identity.Contains("下拉")
                    || identity.Contains("展开"))
                {
                    return false;
                }

                var almostWholeSplit = Math.Abs(rect.Left - splitRect.Left) <= 2
                    && Math.Abs(rect.Top - splitRect.Top) <= 2
                    && rect.Width >= splitRect.Width - 3
                    && rect.Height >= splitRect.Height - 3;
                if (almostWholeSplit) return false;

                var protectedArrowStart = splitRect.Right - arrowGuard;
                if (rect.Left >= protectedArrowStart) return false;
                if (rect.Right > splitRect.Right - Math.Max(4, arrowGuard / 5)) return false;
                if (rect.Left < splitRect.Left - 3 || rect.Top < splitRect.Top - 3) return false;
                if (rect.Bottom > splitRect.Bottom + 3) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TrySendTextViaUiaAsync(string buyer, string text, DateTime sendStart)
        {
            try
            {
                if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                    && !await RefreshChatControlsAsync(true).ConfigureAwait(false))
                {
                    return false;
                }
                if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                {
                    SetSendFailure("UIA主发送", "当前卖家千牛窗口内未找到可点击的发送主按钮区域");
                    return false;
                }
                if (!await HasExpectedDraftFastAsync(text, 1000).ConfigureAwait(false))
                {
                    SetSendFailure("UIA主发送", "发送前无法确认输入框仍为本次目标文本");
                    return false;
                }

                Log.Info("UIA定位完成，开始点击发送主按钮左侧区域: seller=" + SellerNick
                    + ", buyer=" + buyer);
                var clicked = await RunUiActionAsync(
                    () => TryClickCachedSendButtonNow(),
                    "发送主按钮坐标点击",
                    UiActionTimeoutMs).ConfigureAwait(false);
                if (clicked)
                {
                    return await WaitForTextSendConfirmedAsync(
                        buyer, text, sendStart, "发送主按钮坐标", 3600).ConfigureAwait(false);
                }

                if (!_lastSendButtonCoordinateClickRejected)
                {
                    SetSendFailure("发送主按钮坐标点击", "未能点击已验证发送按钮的左侧主操作区域");
                    return false;
                }

                Log.Info("发送主按钮坐标输入被系统拒绝，准备定位左侧主发送UIA子控件；禁止Invoke整块分裂按钮: seller="
                    + SellerNick + ", buyer=" + buyer);

                if (!await HasExpectedDraftFastAsync(text, 900).ConfigureAwait(false))
                {
                    Log.Info("坐标点击异常后目标草稿已不存在或无法确认，禁止UIA二次动作: buyer=" + buyer);
                    return await WaitForTextSendConfirmedAsync(
                        buyer, text, sendStart, "坐标点击异常后确认", 1800).ConfigureAwait(false);
                }

                var invoked = await RunUiActionAsync(
                    () => TryInvokeCachedSendButtonNow(),
                    "发送按钮左侧UIA安全调用",
                    UiActionTimeoutMs).ConfigureAwait(false);
                if (!invoked)
                {
                    SetSendFailure("发送按钮UIA回退", "坐标输入被系统拒绝且未找到安全主发送UIA子控件（已禁止整块分裂按钮Invoke）");
                    return false;
                }

                return await WaitForTextSendConfirmedAsync(
                    buyer, text, sendStart, "发送按钮左侧UIA安全调用", 3600).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetSendFailure("UIA主发送异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        public async Task SendImageAsync(string buyer, string imagePath)
        {
            var image = await Task.Run(() => BitmapImageEx.CreateFromFile(imagePath)).ConfigureAwait(false);
            await OpenAndSendImageAsync(buyer, image).ConfigureAwait(false);
        }

        private async Task<bool> OpenAndSendImageAsync(string buyer, BitmapImage image)
        {
            ResetSendFailure();
            if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
            {
                if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "图片发送打开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;
                await Task.Delay(500).ConfigureAwait(false);
            }
            if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
            {
                SetSendFailure("图片发送会话确认", "当前会话不是目标买家");
                return false;
            }
            if (!await VerifyCurrentBuyerAsync(buyer, "图片发送前会话确认").ConfigureAwait(false)) return false;

            var sellerDesk = ResolveSellerDesk();
            if (sellerDesk == null || !EnsureSellerDeskBinding(false))
            {
                SetSendFailure("图片发送", "未找到当前卖家对应千牛窗口");
                return false;
            }
            if (!sellerDesk.IsVisible)
            {
                try { sellerDesk.Show(); } catch (Exception ex) { Log.Info("显示图片发送窗口失败: " + ex.Message); }
            }
            if (!await RefreshChatControlsAsync(true).ConfigureAwait(false)) return false;
            if (!await ClearStaleComposerBeforeNewDraftAsync(buyer, string.Empty).ConfigureAwait(false)) return false;

            var setOk = await RunUiActionAsync(() => SetImage(image), "图片草稿写入", 3500).ConfigureAwait(false);
            if (!setOk) return false;
            return await TrySendImageViaUiaAsync(buyer).ConfigureAwait(false);
        }

        private async Task<bool> TrySendImageViaUiaAsync(string buyer)
        {
            if ((_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
                && !await RefreshChatControlsAsync(true).ConfigureAwait(false))
            {
                return false;
            }
            if (_sendMessageButton == null || _sendMessageButtonRect.IsEmpty)
            {
                SetSendFailure("图片UIA发送", "当前卖家窗口内未找到可点击的发送主按钮区域");
                return false;
            }

            Log.Info("图片发送开始点击发送主按钮左侧区域: seller=" + SellerNick + ", buyer=" + buyer);
            var clicked = await RunUiActionAsync(
                () => TryClickCachedSendButtonNow(),
                "图片发送主按钮坐标点击",
                UiActionTimeoutMs).ConfigureAwait(false);
            if (!clicked) return false;
            await Task.Delay(500).ConfigureAwait(false);
            var empty = await RunUiActionAsync(() => IsEditorEmptySafe(), "图片坐标发送确认", UiActionTimeoutMs).ConfigureAwait(false);
            if (empty) BotConnectionDiagnostics.RecordSendAttempt(true, "图片发送主按钮坐标，输入框已清空");
            else SetSendFailure("图片发送", "发送主按钮坐标点击后未确认图片草稿已发送");
            return empty;
        }

        private bool SetImage(BitmapImage img)
        {
            var isok = false;
            if ((DateTime.Now - _preSendPlainTextAndImageTime).TotalSeconds < 1.1
                && _preSendPlainTextAndImageImage == img)
            {
                return false;
            }
            _preSendPlainTextAndImageTime = DateTime.Now;
            _preSendPlainTextAndImageImage = img;

            ClipboardEx.UseClipboardWithAutoRestoreInUiThread(() =>
            {
                if (!FocusEditor()) return;
                Clipboard.Clear();
                Clipboard.SetImage(img);
                WinApi.PressCtrlV();
                var started = DateTime.Now;
                do
                {
                    if (_messageInputTextArea != null && !string.IsNullOrEmpty(_messageInputTextArea.Text))
                    {
                        isok = true;
                        break;
                    }
                    DispatcherEx.DoEvents();
                } while ((DateTime.Now - started).TotalSeconds < 2.0);
                Util.WriteTimeElapsed(started, "等待时间");
            });
            return isok;
        }

        public bool FocusEditor()
        {
            var isok = false;
            DispatcherEx.xInvoke(() =>
            {
                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    SetSendFailure("聚焦输入框", "未找到当前卖家对应千牛窗口");
                    return;
                }
                sellerDesk.BringTop();
                try
                {
                    if (_messageInputTextArea == null)
                    {
                        SetSendFailure("聚焦输入框", "聊天输入框尚未异步刷新完成");
                        return;
                    }

                    try
                    {
                        _messageInputTextArea.Focus();
                        Thread.Sleep(120);
                        isok = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Info("输入框 Focus 失败，改用鼠标点击: " + ex.Message);
                    }

                    var point = _messageInputTextArea.GetClickablePoint();
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point { X = point.X + 5, Y = point.Y + 5 });
                    Thread.Sleep(120);
                    isok = true;
                }
                catch (Exception e)
                {
                    SetSendFailure("聚焦输入框", e.Message);
                    Log.Exception(e);
                }
            });
            return isok;
        }

        public async Task<bool> SendTextAsync(string buyer, string text)
        {
            await Task.Delay(180).ConfigureAwait(false);
            string manualQuestion;
            string manualAnswer;
            if (KnowledgeLearningService.TryBlockForManualReply(_qn, buyer, text, out manualQuestion, out manualAnswer))
            {
                SetSendCancellation("人工接管", "检测到客服已人工回复，本次Bot发送已停止");
                return false;
            }
            return await OpenAndSendText(buyer, text).ConfigureAwait(false);
        }

        private string SellerNick
        {
            get { return _qn == null || _qn.Seller == null ? string.Empty : (_qn.Seller.Nick ?? string.Empty).Trim(); }
        }

        private static string AttemptKey(string seller, string buyer, string text)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim() + "#" + (text ?? string.Empty).Trim();
        }

        private DateTime GetOrCreateAttemptStartedAt(string buyer, string text)
        {
            CleanupAttemptLeases();
            return AnswerAttemptStartedAt.GetOrAdd(AttemptKey(SellerNick, buyer, text), _ => DateTime.Now);
        }

        private void CompleteAttemptLease(string buyer, string text)
        {
            DateTime ignored;
            AnswerAttemptStartedAt.TryRemove(AttemptKey(SellerNick, buyer, text), out ignored);
        }

        private static void CleanupAttemptLeases()
        {
            var threshold = DateTime.Now.AddMinutes(-2);
            foreach (var pair in AnswerAttemptStartedAt)
            {
                if (pair.Value >= threshold) continue;
                DateTime ignored;
                AnswerAttemptStartedAt.TryRemove(pair.Key, out ignored);
            }
        }

        private bool VerifyAnswerFreshness(string buyer, string text, DateTime attemptStartedAt, string stage)
        {
            string authorityReason;
            if (ReliableSendAuthority.IsProtectedFromBuyerUpdate(
                SellerNick, buyer, text, out authorityReason))
            {
                Log.Info("业务可靠发送权限受保护，买家后续聊天不会取消已由业务动作ledger授权的本次发送: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage
                    + ", authority=" + authorityReason);
                return true;
            }
            if (_qn == null || !_qn.HasBuyerMessageAfter(SellerNick, buyer, attemptStartedAt)) return true;
            SetSendCancellation(stage, "买家已发送更新消息，旧答案不会发送");
            CompleteAttemptLease(buyer, text);
            Log.Info("旧答案发送/重试已取消: seller=" + SellerNick + ", buyer=" + buyer
                + ", stage=" + stage + ", reason=买家已发送更新消息");
            return false;
        }

        private bool IsExpectedBuyer(string expected, string current)
        {
            expected = (expected ?? string.Empty).Trim();
            current = (current ?? string.Empty).Trim();
            if (expected.Length == 0 || current.Length == 0) return false;
            if (string.Equals(expected, current, StringComparison.Ordinal)) return true;
            return BuyerIdentityAliasService.AreEquivalent(SellerNick, expected, current);
        }

        private async Task<string> ReadCurrentBuyerNickAsync()
        {
            var current = await _qn.GetCurrentConversationID().ConfigureAwait(false);
            return current == null || current.Result == null
                ? string.Empty
                : (current.Result.Nick ?? string.Empty).Trim();
        }

        private bool HasLiveOwnedDraft()
        {
            var expected = (LastSetPlainText ?? string.Empty).Trim();
            if (!HasOwnedRecentDraft(expected)) return false;
            string current;
            return TryGetEditorText(out current) && EditorMatchesExpectedText(current, expected);
        }

        private async Task<bool> VerifyCurrentBuyerWithoutNavigationAsync(string buyer, string stage)
        {
            buyer = (buyer ?? string.Empty).Trim();
            try
            {
                var currentNick = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (IsExpectedBuyer(buyer, currentNick))
                {
                    _qn.SetActiveConversationByNick(SellerNick,
                        BuyerIdentityAliasService.ResolveInternalNick(SellerNick, currentNick), stage + "-只读确认");
                    return true;
                }

                SetSendFailure(stage, "目标买家=" + buyer + "，当前买家="
                    + (string.IsNullOrWhiteSpace(currentNick) ? "<空>" : currentNick)
                    + "；Bot草稿已写入，禁止重开/切换会话");
                return false;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, "Bot草稿已写入，只读会话确认失败：" + ex.Message);
                return false;
            }
        }

        private async Task<bool> VerifyCurrentBuyerAsync(string buyer, string stage)
        {
            buyer = (buyer ?? string.Empty).Trim();
            try
            {
                if (_qn == null || _qn.CDP == null)
                {
                    SetSendFailure(stage, "千牛消息连接不可用");
                    return false;
                }

                if (HasLiveOwnedDraft())
                {
                    Log.Info("检测到本次Bot草稿已写入，发送前会话确认降级为只读快照，禁止重开/切换会话: stage="
                        + stage + ", buyer=" + buyer);
                    return await VerifyCurrentBuyerWithoutNavigationAsync(buyer, stage).ConfigureAwait(false);
                }

                for (var attempt = 0; attempt < 7; attempt++)
                {
                    var currentNick = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                    if (IsExpectedBuyer(buyer, currentNick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, currentNick), stage);
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(currentNick))
                    {
                        SetSendFailure(stage, "目标买家=" + buyer + "，当前买家=" + currentNick);
                        return false;
                    }
                    Log.Info("会话确认暂时为空，等待稳定: stage=" + stage + ", buyer=" + buyer
                        + ", attempt=" + (attempt + 1) + "/7");
                    await Task.Delay(180).ConfigureAwait(false);
                }

                Log.Info("会话持续为空，重新打开目标买家后再次确认: stage=" + stage + ", buyer=" + buyer);
                if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "重开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;
                await Task.Delay(500).ConfigureAwait(false);
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var currentNick = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                    if (IsExpectedBuyer(buyer, currentNick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, currentNick), stage + "-重开确认");
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(currentNick))
                    {
                        SetSendFailure(stage, "目标买家=" + buyer + "，重开后当前买家=" + currentNick);
                        return false;
                    }
                    await Task.Delay(200).ConfigureAwait(false);
                }

                SetSendFailure(stage, "目标买家=" + buyer + "，当前会话持续为空");
                return false;
            }
            catch (Exception ex)
            {
                SetSendFailure(stage, ex.Message);
                return false;
            }
        }

        private async Task ClearExpectedDraftIfSafeAsync(string buyer, string expected, string reason)
        {
            try
            {
                var currentBuyer = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, currentBuyer))
                {
                    Log.Info("发送失败草稿未清理：当前买家无法证明仍为目标买家。target=" + buyer
                        + ", current=" + currentBuyer + ", reason=" + reason);
                    return;
                }

                string currentText;
                if (!TryGetEditorText(out currentText)
                    || !EditorMatchesExpectedText(currentText, expected)
                    || !IsKnownBotOwnedDraftText(currentText))
                {
                    Log.Info("发送失败草稿未清理：输入框不是本次Bot精确草稿或所有权无法证明。buyer="
                        + buyer + ", reason=" + reason);
                    return;
                }

                var buyerBeforeClear = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, buyerBeforeClear))
                {
                    Log.Info("发送失败草稿未清理：清理前买家已变化。target=" + buyer
                        + ", current=" + buyerBeforeClear + ", reason=" + reason);
                    return;
                }

                DispatcherEx.xInvoke(() =>
                {
                    string latestText;
                    if (!TryGetEditorText(out latestText)
                        || !EditorMatchesExpectedText(latestText, expected)
                        || !IsKnownBotOwnedDraftText(latestText)
                        || !FocusEditor()) return;
                    PressCtrlA();
                    PressBackspace();
                    ForgetOwnedDraft();
                    Log.Info("已安全清除发送失败的Bot精确草稿: buyer=" + buyer + ", reason=" + reason);
                });
            }
            catch (Exception ex)
            {
                Log.Info("安全清理发送失败草稿异常，已保留输入框避免误删人工内容: " + ex.Message);
            }
        }

        private async Task<bool> ClearStaleComposerBeforeNewDraftAsync(string buyer, string expected)
        {
            try
            {
                var currentBuyer = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, currentBuyer))
                {
                    SetSendFailure("残留草稿清理", "清理前无法证明当前会话仍为目标买家；target=" + buyer
                        + ", current=" + currentBuyer);
                    return false;
                }

                if (_messageInputTextArea == null
                    && !await RefreshChatControlsAsync(false).ConfigureAwait(false))
                {
                    SetSendFailure("残留草稿清理", "无法定位当前目标买家的千牛输入框");
                    return false;
                }

                string observedText;
                if (!TryGetEditorText(out observedText))
                {
                    SetSendFailure("残留草稿清理", "无法读取当前输入框内容，禁止盲目清空");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(NormalizeEditorText(observedText))) return true;

                // A concurrent retry may already have placed this exact current answer. Adopt it;
                // never delete it and never append another copy.
                if (!string.IsNullOrEmpty(expected) && EditorMatchesExpectedText(observedText, expected))
                {
                    RememberOwnedDraft(buyer, expected);
                    Log.Info("输入框已存在本次任务精确草稿，已接管且不会重复写入: buyer=" + buyer);
                    return true;
                }

                // Only a draft previously recorded by this QNRpa instance for the same buyer may be
                // deleted. Unknown/manual text is preserved fail-closed.
                if (!IsOwnedDraftForBuyer(buyer, observedText))
                {
                    SetSendFailure("残留草稿清理",
                        "输入框存在所有权无法证明的内容，已保留并阻止覆盖/追加发送");
                    Log.Info("残留草稿未清理：无法证明属于同一买家的Bot历史草稿。buyer=" + buyer
                        + ", chars=" + NormalizeEditorText(observedText).Length);
                    return false;
                }

                var ownedText = observedText;
                Log.Info("检测到同一买家的Bot历史残留草稿，准备安全清空后执行新发送任务: buyer="
                    + buyer + ", chars=" + NormalizeEditorText(ownedText).Length);

                var cleared = await RunUiMutationAsync(() =>
                {
                    string latestText;
                    if (!TryGetEditorText(out latestText)
                        || !EditorMatchesExpectedText(latestText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, latestText)
                        || !FocusEditor())
                    {
                        return false;
                    }

                    // Focus/UIA may have been delayed. Revalidate the exact Bot-owned text
                    // immediately before the destructive key sequence so a late worker cannot
                    // erase a newer Bot draft or a human-authored draft.
                    string postFocusText;
                    if (!TryGetEditorText(out postFocusText)
                        || !EditorMatchesExpectedText(postFocusText, ownedText)
                        || !IsOwnedDraftForBuyer(buyer, postFocusText))
                    {
                        Log.Info("Bot历史残留草稿清理在聚焦后检测到内容已变化，已取消清空: buyer=" + buyer);
                        return false;
                    }
                    PressCtrlA();
                    PressBackspace();
                    Thread.Sleep(120);
                    string afterClear;
                    if (!TryGetEditorText(out afterClear)
                        || !string.IsNullOrWhiteSpace(NormalizeEditorText(afterClear)))
                    {
                        return false;
                    }
                    ForgetOwnedDraft();
                    return true;
                }, "Bot历史残留草稿清理").ConfigureAwait(false);

                if (!cleared)
                {
                    SetSendFailure("残留草稿清理", "Bot历史残留草稿清空失败，已阻止追加写入");
                    return false;
                }

                var buyerAfterClear = await ReadCurrentBuyerNickAsync().ConfigureAwait(false);
                if (!IsExpectedBuyer(buyer, buyerAfterClear))
                {
                    SetSendFailure("残留草稿清理", "清理后当前会话发生变化；target=" + buyer
                        + ", current=" + buyerAfterClear);
                    return false;
                }

                var after = await ProbeInputboxEmptyAsync("残留草稿清理后确认", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (!after.Completed || !after.IsEmpty)
                {
                    SetSendFailure("残留草稿清理", "清空后CDP未确认输入框为空，禁止盲目追加写入");
                    return false;
                }

                Log.Info("同一买家的Bot历史残留草稿已清空并二次确认为空，可继续写入新任务: buyer=" + buyer);
                return true;
            }
            catch (Exception ex)
            {
                SetSendFailure("残留草稿清理异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private async Task<bool> TrySetPlainTextByCdpAsync(string buyer, string text)
        {
            try
            {
                if (_qn == null) return false;

                var before = await ProbeInputboxEmptyAsync("写入前输入框检查", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (!before.Completed)
                {
                    SetSendFailure("CDP写入输入框", "写入前无法确认输入框是否为空，已停止发送");
                    return false;
                }

                if (!before.IsEmpty)
                {
                    if (HasOwnedRecentDraft(text))
                    {
                        Log.Info("检测到本次Bot草稿仍在输入框，重试直接复用且不再次追加: buyer=" + buyer);
                        return true;
                    }

                    if (_messageInputTextArea == null)
                    {
                        await RefreshChatControlsAsync(false).ConfigureAwait(false);
                    }
                    var exactExisting = await RunUiActionAsync(
                        () => HasExpectedDraft(text),
                        "已有草稿严格确认",
                        UiActionTimeoutMs).ConfigureAwait(false);
                    if (exactExisting)
                    {
                        RememberOwnedDraft(buyer, text);
                        Log.Info("输入框已存在与本次答案完全一致的草稿，直接接管发送且不追加: buyer=" + buyer);
                        return true;
                    }

                    if (!await ClearStaleComposerBeforeNewDraftAsync(buyer, text).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var afterClear = await ProbeInputboxEmptyAsync("新任务写入前清空确认", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                    if (!afterClear.Completed)
                    {
                        SetSendFailure("CDP写入输入框", "残留草稿清理后无法确认输入框状态，已停止发送");
                        return false;
                    }
                    if (!afterClear.IsEmpty)
                    {
                        var exactAfterClear = await RunUiActionAsync(
                            () => HasExpectedDraft(text),
                            "清理后同任务草稿确认",
                            UiActionTimeoutMs).ConfigureAwait(false);
                        if (exactAfterClear)
                        {
                            RememberOwnedDraft(buyer, text);
                            Log.Info("清理残留草稿期间同任务草稿已写入，直接接管发送且不追加: buyer=" + buyer);
                            return true;
                        }

                        SetSendFailure("CDP写入输入框", "残留草稿清理后二次检查仍非空，已阻止追加发送");
                        return false;
                    }
                }

                Log.Info("准备通过CDP写入输入框: buyer=" + buyer);
                if (!await RunCdpActionAsync(() => _qn.InsertText2Inputbox(buyer, text), "CDP写入输入框", CdpActionTimeoutMs).ConfigureAwait(false))
                    return false;

                RememberOwnedDraft(buyer, text);

                await Task.Delay(260).ConfigureAwait(false);
                var after = await ProbeInputboxEmptyAsync("写入后输入框检查", CdpQuickProbeTimeoutMs).ConfigureAwait(false);
                if (after.Completed && !after.IsEmpty)
                {
                    Log.Info("CDP写入输入框已由IMSDK确认，进入UIA定位发送主按钮动作: buyer=" + buyer);
                    return true;
                }

                await RefreshChatControlsAsync(true).ConfigureAwait(false);
                var uiVerified = await RunUiActionAsync(() => HasExpectedDraft(text), "UIA写入确认", UiActionTimeoutMs).ConfigureAwait(false);
                if (uiVerified)
                {
                    Log.Info("CDP写入由UIA严格确认: buyer=" + buyer);
                    return true;
                }

                SetSendFailure("CDP写入输入框", "写入后CDP/UIA均未确认本次目标草稿");
                return false;
            }
            catch (Exception ex)
            {
                SetSendFailure("CDP写入输入框异常", ex.Message);
                Log.Exception(ex);
                return false;
            }
        }

        private async Task<bool> OpenAndSendText(string buyer, string text)
        {
            var sendResult = false;
            ResetSendFailure();
            var attemptStartedAt = GetOrCreateAttemptStartedAt(buyer, text);
            try
            {
                Log.Info("自动发送开始: buyer=" + buyer + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));

                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "写入前答案时效检查")) return false;

                if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
                {
                    if (!await RunCdpActionAsync(() => _qn.OpenChat(buyer), "打开目标买家", CdpActionTimeoutMs).ConfigureAwait(false))
                        return false;
                    await Task.Delay(500).ConfigureAwait(false);
                    var conv = await _qn.GetCurrentConversationID().ConfigureAwait(false);
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick))
                    {
                        _qn.SetActiveConversationByNick(SellerNick,
                            BuyerIdentityAliasService.ResolveInternalNick(SellerNick, conv.Result.Nick), "beforeSend");
                    }
                }

                if (_qn.Buyer == null || !IsExpectedBuyer(buyer, _qn.Buyer.Nick))
                {
                    SetSendFailure("会话确认", "当前会话不是目标买家；target=" + buyer
                        + ", current=" + (_qn.Buyer == null ? "" : _qn.Buyer.Nick));
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                if (!await VerifyCurrentBuyerAsync(buyer, "写入前会话确认").ConfigureAwait(false))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "写入前答案时效检查"))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                var sellerDesk = ResolveSellerDesk();
                if (sellerDesk == null || !EnsureSellerDeskBinding(false))
                {
                    SetSendFailure("发送窗口", "未找到当前卖家对应千牛窗口");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!sellerDesk.IsVisible)
                {
                    try { sellerDesk.Show(); } catch (Exception ex) { Log.Info("显示文本发送窗口失败: " + ex.Message); }
                }

                if (!await RefreshChatControlsAsync(true).ConfigureAwait(false))
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                var setOk = await TrySetPlainTextByCdpAsync(buyer, text).ConfigureAwait(false);
                if (!setOk)
                {
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                await Task.Delay(80).ConfigureAwait(false);
                if (!VerifyAnswerFreshness(buyer, text, attemptStartedAt, "发送前答案时效检查"))
                {
                    await ClearExpectedDraftIfSafeAsync(buyer, text, GetSendFailureReason()).ConfigureAwait(false);
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!await VerifyCurrentBuyerAsync(buyer, "发送前会话确认").ConfigureAwait(false))
                {
                    await ClearExpectedDraftIfSafeAsync(buyer, text, GetSendFailureReason()).ConfigureAwait(false);
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }
                if (!await HasExpectedDraftFastAsync(text, 1200).ConfigureAwait(false))
                {
                    SetSendFailure("发送前文本确认", "输入框内容已变化或无法确认，已阻止发送");
                    SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                    return false;
                }

                SendDeliveryWatchdog.EnsurePending(SellerNick, buyer, text);
                var sendStart = DateTime.Now;
                sendResult = await TrySendTextNativeFirstAsync(buyer, text, sendStart).ConfigureAwait(false);
                if (!sendResult && string.IsNullOrWhiteSpace(LastSendFailureReason))
                {
                    SetSendFailure("发送确认", "发送主按钮动作后未确认消息真实回显");
                }
                if (sendResult)
                {
                    CompleteAttemptLease(buyer, text);
                }
                else
                {
                    await ClearExpectedDraftIfSafeAsync(buyer, text, GetSendFailureReason()).ConfigureAwait(false);
                }
                Log.Info("自动发送完成: result=" + sendResult + ", buyer=" + buyer
                    + ", method=CDP页面按钮+HWND安全消息+UIA安全回退, failure="
                    + (sendResult ? string.Empty : GetSendFailureReason()));
            }
            catch (Exception ex)
            {
                SetSendFailure("自动发送异常", ex.Message);
                await ClearExpectedDraftIfSafeAsync(buyer, text, GetSendFailureReason()).ConfigureAwait(false);
                SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text, GetSendFailureReason());
                Log.Exception(ex);
                sendResult = false;
            }
            return sendResult;
        }
    }
}
