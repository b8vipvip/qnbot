using Bot.Automation.ChatDeskNs;
using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        internal const string ChatInputAutomationId = "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.chatInputArea.plainTextEdit";
        internal const string SendButtonAutomationId = "UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.enterAreaKeyWidget.sendMsg";

        public string LastSendFailureReason { get; private set; } = string.Empty;
        internal bool LastSendWasCancelled { get; private set; }

        // A burst of send/recovery paths can all request a forced UIA refresh at once. Scanning
        // the full Qianniu HWND tree concurrently is expensive and the production log showed a
        // thundering herd of identical successful scans in the same second. Share the in-flight
        // scan per seller-scoped QNRpa instance; a later force call still starts a fresh scan once
        // the current one has completed.
        private readonly object _chatControlsRefreshSync = new object();
        private Task<bool> _activeChatControlsRefreshTask;

        internal void ResetSendFailure()
        {
            LastSendFailureReason = string.Empty;
            LastSendWasCancelled = false;
        }

        internal void SetSendFailure(string stage, string detail)
        {
            stage = (stage ?? string.Empty).Trim();
            detail = (detail ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

            // A stale answer is not a transport failure. QNRpa's pre-send freshness guard can
            // discover that the buyer sent a newer turn after this answer was prepared. Production
            // logs from 1.1.1189 showed that classifying that result as an ordinary failure caused
            // QN.SendTextWithRetryAsync to send the already-invalid greeting again. Preserve the
            // existing retry contract by marking only explicit stale-answer reasons as cancelled.
            LastSendWasCancelled = IsNonRetryableStaleAnswer(stage, detail);
            LastSendFailureReason = string.IsNullOrWhiteSpace(detail) ? stage : stage + "：" + detail;
            BotConnectionDiagnostics.RecordSendAttempt(false, LastSendFailureReason);
            Log.Info("发送阶段失败: " + LastSendFailureReason);
            if (LastSendWasCancelled)
            {
                Log.Info("发送失败已分类为不可重试的旧答案取消，可靠发送层必须立即停止重试: "
                    + LastSendFailureReason);
                ClearCancelledExactBotDraftIfPresent();
            }
        }

        private static bool IsNonRetryableStaleAnswer(string stage, string detail)
        {
            var combined = (stage ?? string.Empty) + " " + (detail ?? string.Empty);
            return combined.IndexOf("买家已发送更新消息", StringComparison.Ordinal) >= 0
                || combined.IndexOf("旧答案不会发送", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// A cancelled Bot answer must not remain visible in Qianniu's composer after a newer buyer
        /// turn invalidates it. Clear only when the current editor still matches LastSetPlainText
        /// exactly, and re-check after focusing before sending any key input. Manual/operator text or
        /// a newer Bot draft is never touched.
        /// </summary>
        private void ClearCancelledExactBotDraftIfPresent()
        {
            try
            {
                var expected = (LastSetPlainText ?? string.Empty).Trim();
                if (expected.Length == 0) return;

                string current;
                if (!TryGetEditorText(out current) || !EditorMatchesExpectedText(current, expected)) return;
                if (!FocusEditor()) return;

                string focusedCurrent;
                if (!TryGetEditorText(out focusedCurrent)
                    || !EditorMatchesExpectedText(focusedCurrent, expected)) return;

                PressCtrlA();
                PressBackspace();
                LastSetPlainText = string.Empty;
                LatestSetTextTime = DateTime.MinValue;
                Log.Info("已清除因买家更新消息而作废的Bot精确草稿，避免旧答案滞留千牛输入框");
            }
            catch (Exception ex)
            {
                Log.Info("清除作废Bot精确草稿失败，未触碰无法证明所有权的输入内容: " + ex.Message);
            }
        }

        internal void SetSendCancellation(string stage, string detail)
        {
            SetSendFailure(stage, detail);
            LastSendWasCancelled = true;
        }

        internal void InvalidateChatControls()
        {
            _messageInputTextArea = null;
            _sendMessageButton = null;
            _sendMessageButtonRect = System.Drawing.Rectangle.Empty;
            _preUpdateChatBrowserRectTime = DateTime.MinValue;
        }

        internal string GetSendFailureReason()
        {
            return string.IsNullOrWhiteSpace(LastSendFailureReason) ? "未知发送失败" : LastSendFailureReason;
        }

        internal Task<bool> RefreshChatControlsAsync(bool force)
        {
            lock (_chatControlsRefreshSync)
            {
                if (!force
                    && _messageInputTextArea != null
                    && (DateTime.Now - _preUpdateChatBrowserRectTime).TotalSeconds < 3)
                {
                    return Task.FromResult(true);
                }

                if (_activeChatControlsRefreshTask != null
                    && !_activeChatControlsRefreshTask.IsCompleted)
                {
                    return _activeChatControlsRefreshTask;
                }

                _activeChatControlsRefreshTask = RefreshChatControlsCoreAsync();
                return _activeChatControlsRefreshTask;
            }
        }

        private async Task<bool> RefreshChatControlsCoreAsync()
        {
            _preUpdateChatBrowserRectTime = DateTime.Now;

            var sellerDesk = ResolveSellerDesk();
            if (sellerDesk == null)
            {
                InvalidateChatControls();
                SetSendFailure("UIA扫描", "未找到当前客服唯一对应的千牛接待窗口");
                BotConnectionDiagnostics.RecordRpaScan(false, false, "seller Desk 未绑定");
                return false;
            }

            if (!EnsureSellerDeskBinding(false))
            {
                InvalidateChatControls();
                SetSendFailure("UIA扫描", "当前客服的 RPA/千牛窗口绑定尚未就绪");
                BotConnectionDiagnostics.RecordRpaScan(false, false, "seller RPA 未绑定");
                return false;
            }

            if (!sellerDesk.IsVisibleAndNotMinimized)
            {
                try { sellerDesk.Show(); }
                catch (Exception ex) { Log.Info("显示当前客服千牛接待台失败: " + ex.Message); }
            }

            if (uia3Automation == null || sellerDesk.Hwnd == null || sellerDesk.Hwnd.Handle < 1)
            {
                InvalidateChatControls();
                SetSendFailure("UIA扫描", "当前客服千牛窗口句柄无效");
                return false;
            }

            var expectedHwnd = sellerDesk.Hwnd.Handle;
            return await Task.Run(() =>
            {
                try
                {
                    var mainWnd = uia3Automation.FromHandle(new IntPtr(expectedHwnd));
                    if (mainWnd == null)
                    {
                        InvalidateChatControls();
                        SetSendFailure("UIA扫描", "无法从当前客服千牛 HWND 建立 UIA 根节点；hwnd=" + expectedHwnd);
                        BotConnectionDiagnostics.RecordRpaScan(false, false, "seller HWND UIA root 为空");
                        return false;
                    }

                    var descendants = mainWnd.FindAllDescendants();
                    var inputElement = FindChatInputElement(mainWnd, descendants);
                    var sendElement = FindSendButtonElement(descendants, inputElement);

                    _messageInputTextArea = inputElement == null ? null : inputElement.AsTextBox();
                    _sendMessageButton = sendElement;
                    _sendMessageButtonRect = SafeBoundingRectangle(sendElement);
                    var inputFound = _messageInputTextArea != null;
                    var sendFound = _sendMessageButton != null && _sendMessageButtonRect.Width > 0 && _sendMessageButtonRect.Height > 0;
                    BotConnectionDiagnostics.RecordRpaScan(sendFound, inputFound,
                        "seller HWND UIA扫描 hwnd=" + expectedHwnd
                        + ", input=" + inputFound + ", send=" + sendFound);

                    if (!inputFound)
                    {
                        SetSendFailure("UIA扫描", "当前客服千牛窗口内未找到聊天输入框；hwnd="
                            + expectedHwnd + ", descendants=" + descendants.Length);
                    }
                    else
                    {
                        Log.Info("UIA控件刷新成功: seller=" + SellerNick
                            + ", hwnd=" + expectedHwnd
                            + ", inputAutomationId=" + SafeAutomationId(inputElement)
                            + ", inputClass=" + SafeClassName(inputElement)
                            + ", sendAutomationId=" + SafeAutomationId(sendElement)
                            + ", sendName=" + SafeName(sendElement)
                            + ", sendRect=" + FormatRect(_sendMessageButtonRect));
                    }
                    return inputFound;
                }
                catch (Exception ex)
                {
                    InvalidateChatControls();
                    SetSendFailure("UIA扫描异常", "seller=" + SellerNick
                        + ", hwnd=" + expectedHwnd + ", " + ex.Message);
                    Log.Exception(ex);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        private AutomationElement FindChatInputElement(
            AutomationElement root,
            AutomationElement[] descendants)
        {
            descendants = descendants ?? new AutomationElement[0];

            var exact = descendants.FirstOrDefault(k => string.Equals(
                SafeAutomationId(k), ChatInputAutomationId, StringComparison.Ordinal));
            if (exact != null) return exact;

            var rootRect = SafeBoundingRectangle(root);
            var candidates = descendants
                .Where(k => string.Equals(SafeClassName(k), "TextRichEdit", StringComparison.Ordinal))
                .Where(k => IsPlausibleChatInput(k, rootRect))
                .OrderByDescending(k => SafeBoundingRectangle(k).Bottom)
                .ThenByDescending(k => SafeBoundingRectangle(k).Width)
                .ToArray();
            if (candidates.Length > 0) return candidates[0];

            return descendants
                .Where(k => SafeAutomationId(k).EndsWith("sendMsgWidget.chatInputArea.plainTextEdit", StringComparison.Ordinal))
                .OrderByDescending(k => SafeBoundingRectangle(k).Bottom)
                .FirstOrDefault();
        }

        private AutomationElement FindSendButtonElement(
            AutomationElement[] descendants,
            AutomationElement inputElement)
        {
            descendants = descendants ?? new AutomationElement[0];
            var exact = descendants.FirstOrDefault(k => string.Equals(
                SafeAutomationId(k), SendButtonAutomationId, StringComparison.Ordinal));
            if (exact != null) return exact;

            var named = descendants.Where(k => IsSendButtonName(SafeName(k))).ToArray();
            if (named.Length == 0)
            {
                return descendants.FirstOrDefault(k => SafeAutomationId(k).EndsWith(
                    "sendMsgWidget.enterAreaKeyWidget.sendMsg", StringComparison.Ordinal));
            }

            if (inputElement == null) return named[0];
            var inputRect = SafeBoundingRectangle(inputElement);
            return named
                .OrderBy(k => VerticalDistance(SafeBoundingRectangle(k), inputRect))
                .ThenBy(k => HorizontalDistance(SafeBoundingRectangle(k), inputRect))
                .FirstOrDefault();
        }

        private static bool IsPlausibleChatInput(
            AutomationElement element,
            System.Drawing.Rectangle rectangleRoot)
        {
            var rect = SafeBoundingRectangle(element);
            if (rect.Width < 120 || rect.Height < 18) return false;
            if (rectangleRoot.Width <= 0 || rectangleRoot.Height <= 0) return true;
            var rootMid = rectangleRoot.Top + rectangleRoot.Height * 0.42;
            return rect.Bottom >= rootMid
                && rect.Left >= rectangleRoot.Left - 4
                && rect.Right <= rectangleRoot.Right + 4;
        }

        private static int VerticalDistance(
            System.Drawing.Rectangle candidate,
            System.Drawing.Rectangle input)
        {
            var cy = candidate.Top + candidate.Height / 2;
            var iy = input.Top + input.Height / 2;
            return Math.Abs(cy - iy);
        }

        private static int HorizontalDistance(
            System.Drawing.Rectangle candidate,
            System.Drawing.Rectangle input)
        {
            var cx = candidate.Left + candidate.Width / 2;
            var ix = input.Left + input.Width / 2;
            return Math.Abs(cx - ix);
        }

        private static System.Drawing.Rectangle SafeBoundingRectangle(AutomationElement element)
        {
            try
            {
                return element == null ? System.Drawing.Rectangle.Empty : element.BoundingRectangle;
            }
            catch
            {
                return System.Drawing.Rectangle.Empty;
            }
        }

        private static string FormatRect(System.Drawing.Rectangle rect)
        {
            return rect.Width <= 0 || rect.Height <= 0
                ? "empty"
                : rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height;
        }

        internal bool TryGetEditorText(out string text)
        {
            text = string.Empty;
            try
            {
                if (_messageInputTextArea == null) return false;
                text = _messageInputTextArea.Text ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("读取输入框失败，控件可能已失效: " + ex.Message);
                InvalidateChatControls();
                return false;
            }
        }

        internal static bool EditorMatchesExpectedText(string actual, string expected)
        {
            return string.Equals(NormalizeEditorText(actual), NormalizeEditorText(expected), StringComparison.Ordinal);
        }

        internal bool HasExpectedDraft(string expected)
        {
            if (!string.IsNullOrEmpty(expected))
            {
                string safeText;
                string reason;
                if (!BuyerReplyOutputGuard.TryNormalizeForBuyer(expected, out safeText, out reason))
                {
                    ClearBlockedDraftImmediately(reason);
                    SetSendFailure("发送前内容安全检查", reason);
                    Log.Error("已阻止异常AI内容发送给买家: reason=" + reason
                        + ", preview=" + SafePreview(expected, 180));
                    return false;
                }
                if (!string.Equals((expected ?? string.Empty).Trim(), safeText, StringComparison.Ordinal))
                {
                    ClearBlockedDraftImmediately("回复仍包含内部时间线标签");
                    SetSendFailure("发送前内容安全检查", "回复仍包含需要移除的内部时间线标签");
                    Log.Error("已阻止带内部时间线标签的回复发送给买家: preview=" + SafePreview(expected, 180));
                    return false;
                }
            }

            string text;
            if (!TryGetEditorText(out text)) return false;
            if (string.IsNullOrEmpty(expected)) return !string.IsNullOrWhiteSpace(NormalizeEditorText(text));
            return EditorMatchesExpectedText(text, expected);
        }

        private void ClearBlockedDraftImmediately(string reason)
        {
            try
            {
                if (!FocusEditor()) return;
                PressCtrlA();
                PressBackspace();
                LastSetPlainText = string.Empty;
                Log.Info("已立即清除被内容安全检查阻止的草稿: reason=" + (reason ?? string.Empty));
            }
            catch (Exception ex)
            {
                Log.Info("清除被阻止的异常草稿失败: " + ex.Message);
            }
        }

        private static string NormalizeEditorText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\u200B", string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        private static string SafePreview(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string SafeAutomationId(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.AutomationId.IsSupported
                    ? (element.AutomationId ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string SafeClassName(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.ClassName.IsSupported
                    ? (element.ClassName ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string SafeName(AutomationElement element)
        {
            try
            {
                return element != null && element.Properties.Name.IsSupported
                    ? (element.Name ?? string.Empty)
                    : string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
