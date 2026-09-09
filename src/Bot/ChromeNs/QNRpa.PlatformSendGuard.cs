using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const int PlatformReadProbeTimeoutMs = 650;
        private readonly object _serviceAttitudeReadProbeSync = new object();
        private Task<ServiceAttitudeProbeResult> _serviceAttitudeReadProbeTask;
        private int _lateServiceAttitudeWatchArmed;
        private DateTime _lastServiceAttitudeContinueAt = DateTime.MinValue;

        private sealed class ServiceAttitudeProbeResult
        {
            public bool Detected;
            public bool Continued;
            public string Detail = string.Empty;
            public AutomationElement ContinueButton;
        }

        /// <summary>
        /// Qianniu's “服务态度提醒” is a platform anti-abuse signal, not a transient transport
        /// dialog. Bot must fail closed and leave the reminder for a human. Automatically clicking
        /// “继续发送” can turn a bad/repeated answer into a buyer complaint, as observed in the
        /// 2026-09-09 field incident.
        /// </summary>
        private async Task<bool> StopIfPlatformSendBlockedAsync(string buyer, string stage)
        {
            var detected = await GetBoundedServiceAttitudeReadProbeAsync(buyer, stage).ConfigureAwait(false);
            if (detected == null || !detected.Detected) return false;

            _lastServiceAttitudeContinueAt = DateTime.Now;
            var detail = string.IsNullOrWhiteSpace(detected.Detail)
                ? "检测到千牛“服务态度提醒”，Bot已停止自动发送并等待人工处理"
                : detected.Detail + "；Bot已停止自动发送并等待人工处理";
            SetSendCancellation("平台发送拦截", detail);
            Log.ErrorWithMaxCount(
                "检测到千牛服务态度提醒，已失败关闭本次Bot发送；不会自动点击“继续发送”: seller="
                + SellerNick + ", buyer=" + buyer + ", stage=" + stage + ", detail=" + detail,
                50);
            return true;
        }

        private async Task<ServiceAttitudeProbeResult> GetBoundedServiceAttitudeReadProbeAsync(string buyer, string stage)
        {
            Task<ServiceAttitudeProbeResult> probe;
            lock (_serviceAttitudeReadProbeSync)
            {
                if (_serviceAttitudeReadProbeTask == null || _serviceAttitudeReadProbeTask.IsCompleted)
                    _serviceAttitudeReadProbeTask = Task.Run(() => ProbeServiceAttitudeReminder(false));
                probe = _serviceAttitudeReadProbeTask;
            }

            var winner = await Task.WhenAny(probe, Task.Delay(PlatformReadProbeTimeoutMs)).ConfigureAwait(false);
            if (winner != probe)
            {
                Log.Info("千牛服务态度提醒只读探测超时，已放行发送主链且复用同一后台探测避免UIA堆积: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage
                    + ", timeoutMs=" + PlatformReadProbeTimeoutMs);
                return null;
            }

            try { return await probe.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Info("千牛平台发送拦截只读探测失败，保持原发送状态: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A verified send action followed by a stable empty composer is strong evidence that
        /// Qianniu accepted this exact Bot-owned draft. Live seller echo is still preferred, but a
        /// missing/delayed echo must never cause the same text to be written and sent a second time.
        /// </summary>
        private async Task<bool> WaitForTextSubmissionAcceptedAsync(string buyer, string text, DateTime sendStart, string method, int timeoutMs)
        {
            var end = DateTime.Now.AddMilliseconds(Math.Max(900, timeoutMs));
            var emptyObserved = false;
            var emptyObservedAt = DateTime.MinValue;
            var earlyPlatformProbeDone = false;
            var stablePlatformProbeDone = false;

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
                catch (Exception ex) { Log.Info("检查卖家消息回显失败: " + ex.Message); }

                var remaining = Math.Max(250, (int)(end - DateTime.Now).TotalMilliseconds);
                var probe = await ProbeInputboxEmptyAsync(method + "提交确认", Math.Min(700, remaining)).ConfigureAwait(false);
                if (probe.Completed && probe.IsEmpty)
                {
                    if (!emptyObserved)
                    {
                        emptyObserved = true;
                        emptyObservedAt = DateTime.Now;
                        Log.Info(method + "发送动作后观察到本次输入框清空，进入稳定提交确认；buyer=" + buyer);
                    }

                    if ((DateTime.Now - emptyObservedAt).TotalMilliseconds >= 220)
                    {
                        var stable = await ProbeInputboxEmptyAsync(method + "稳定清空确认", 650).ConfigureAwait(false);
                        if (stable.Completed && stable.IsEmpty)
                        {
                            if (!stablePlatformProbeDone)
                            {
                                stablePlatformProbeDone = true;
                                if (await StopIfPlatformSendBlockedAsync(buyer, method + "稳定清空后平台确认").ConfigureAwait(false)) return false;
                            }
                            if (!await VerifyCurrentBuyerWithoutNavigationAsync(buyer, method + "提交后会话确认").ConfigureAwait(false)) return false;
                            ResetSendFailure();
                            var submissionEvidence = method + "发送动作后本次Bot精确草稿稳定清空，且目标买家复核通过";
                            SendDeliveryWatchdog.MarkSubmissionAccepted(SellerNick, buyer, text, submissionEvidence);
                            BotConnectionDiagnostics.RecordSendAttempt(true, method + "，发送动作后输入框稳定清空，按千牛已接收提交处理；卖家回显可异步补证");
                            Log.Info(method + "发送提交确认成功：本次精确草稿在发送动作后稳定清空；禁止因实时回显缺失重新写入同一文本。buyer=" + buyer);
                            ArmLateServiceAttitudeContinuationWatch(buyer, method);
                            return true;
                        }
                    }
                }
                else if (probe.Completed && !probe.IsEmpty && !earlyPlatformProbeDone && (DateTime.Now - sendStart).TotalMilliseconds >= 350)
                {
                    earlyPlatformProbeDone = true;
                    if (await StopIfPlatformSendBlockedAsync(buyer, method + "发送动作后平台确认").ConfigureAwait(false)) return false;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }

            if (!stablePlatformProbeDone && await StopIfPlatformSendBlockedAsync(buyer, method + "超时前平台确认").ConfigureAwait(false)) return false;
            SetSendFailure("发送确认", method + "后既未检测到卖家回显，也未观察到发送动作后的稳定输入框清空；emptyObserved=" + emptyObserved);
            return false;
        }

        private void ArmLateServiceAttitudeContinuationWatch(string buyer, string method)
        {
            if (Interlocked.CompareExchange(ref _lateServiceAttitudeWatchArmed, 1, 0) != 0) return;
            var startedAt = DateTime.Now;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(650).ConfigureAwait(false);
                    if (_lastServiceAttitudeContinueAt >= startedAt) return;
                    await StopIfPlatformSendBlockedAsync(buyer, method + "迟到服务态度提醒单次监控").ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Info("迟到服务态度提醒单次监控异常: " + ex.Message); }
                finally { Interlocked.Exchange(ref _lateServiceAttitudeWatchArmed, 0); }
            });
        }

        private ServiceAttitudeProbeResult ProbeServiceAttitudeReminder(bool clickContinue)
        {
            var result = new ServiceAttitudeProbeResult();
            if (!EnsureSellerDeskBinding(false) || automationApplication == null || uia3Automation == null) return result;

            try
            {
                var reminderRoots = new List<AutomationElement>();
                var windows = automationApplication.GetAllTopLevelWindows(uia3Automation);
                if (windows != null)
                {
                    foreach (var window in windows.Where(x => x != null))
                    {
                        var title = RegexCompactPlatformGuardText(SafeName(window));
                        if (title.IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0) reminderRoots.Add(window);
                    }
                }

                if (reminderRoots.Count == 0)
                {
                    var desk = ResolveSellerDesk();
                    if (desk != null && desk.Hwnd != null && desk.Hwnd.Handle > 0)
                    {
                        try
                        {
                            var main = uia3Automation.FromHandle(new IntPtr(desk.Hwnd.Handle));
                            if (main != null && RootContainsServiceAttitudeReminder(main)) reminderRoots.Add(main);
                        }
                        catch (Exception ex) { Log.Info("扫描卖家根窗口服务态度提醒失败: " + ex.Message); }
                    }
                }

                if (reminderRoots.Count == 0) return result;
                result.Detected = true;
                var continueButtons = new List<AutomationElement>();
                foreach (var root in reminderRoots)
                {
                    var elements = new List<AutomationElement> { root };
                    try { elements.AddRange(root.FindAllDescendants().Where(x => x != null)); }
                    catch (Exception ex) { Log.Info("读取服务态度提醒子控件失败: " + ex.Message); }
                    continueButtons.AddRange(elements.Where(x => string.Equals(RegexCompactPlatformGuardText(SafeName(x)), "继续发送", StringComparison.Ordinal)));
                }

                continueButtons = continueButtons.Distinct().ToList();
                result.ContinueButton = continueButtons.Count == 1 ? continueButtons[0] : null;
                result.Detail = continueButtons.Count == 1
                    ? "检测到千牛“服务态度提醒”及唯一“继续发送”按钮"
                    : (continueButtons.Count == 0 ? "检测到千牛“服务态度提醒”，未找到“继续发送”按钮" : "检测到千牛“服务态度提醒”，存在多个“继续发送”候选按钮");
                // Kept for binary/source compatibility only. Production policy is read-only: never invoke.
                return result;
            }
            catch (Exception ex)
            {
                Log.Info("扫描千牛服务态度提醒失败: " + ex.Message);
                return result;
            }
        }

        private bool RootContainsServiceAttitudeReminder(AutomationElement root)
        {
            if (root == null) return false;
            if (RegexCompactPlatformGuardText(SafeName(root)).IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0) return true;
            try
            {
                foreach (var element in root.FindAllDescendants())
                    if (RegexCompactPlatformGuardText(SafeName(element)).IndexOf("服务态度提醒", StringComparison.Ordinal) >= 0) return true;
            }
            catch { }
            return false;
        }

        private ServiceAttitudeProbeResult InvokeServiceAttitudeContinue(ServiceAttitudeProbeResult detected)
        {
            // Deliberately fail closed. This method remains so older reflection/static contracts do
            // not break, but it no longer performs any UIA side effect.
            var result = detected ?? new ServiceAttitudeProbeResult();
            result.Continued = false;
            result.Detail = string.IsNullOrWhiteSpace(result.Detail)
                ? "服务态度提醒已检测；安全策略禁止Bot自动点击“继续发送”"
                : result.Detail + "；安全策略禁止Bot自动点击“继续发送”";
            return result;
        }

        private static string RegexCompactPlatformGuardText(string value)
        {
            return string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Trim();
        }
    }
}