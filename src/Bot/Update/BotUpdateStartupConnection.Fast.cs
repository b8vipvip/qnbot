using Bot.UpdateNs;
using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Repairs the narrow startup case where the Bot process is healthy but the local
    /// 127.0.0.1:41010 listener or Qianniu injected page is not yet available.
    /// Normal startup is non-destructive. A single controlled Qianniu restart is allowed only
    /// for a verified updater-launched target process after its startup health acknowledgement.
    /// </summary>
    internal static class QnStartupConnectionSelfHeal
    {
        private const int WebSocketPort = 41010;
        private static readonly TimeSpan DegradedRetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PostUpdateGracePeriod = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan PostUpdateHealthWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PostRestartRecoveryTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan RecoveryPollDelay = TimeSpan.FromMilliseconds(1800);
        private static readonly string[] MainWorkbenchProcessNames = { "AliWorkbench", "new_AliWorkbench" };
        private static readonly string[] RestartCleanupProcessNames = { "AliWorkbench", "new_AliWorkbench", "AliRender" };
        private static readonly string[] LoginButtonNames = { "登录", "立即登录", "登录千牛", "进入千牛" };
        private static readonly string[] ReceptionEntryNames = { "接待台", "千牛接待台", "接待中心", "客服接待", "消息接待" };
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            // Capture the updater's one-shot capability synchronously. BootStrap starts this
            // recovery worker immediately before ReportReady(), which clears the environment.
            var postUpdateLaunchAuthorized = UpdateStartupHealthService.IsPostUpdateLaunchAuthorized();
            if (postUpdateLaunchAuthorized)
            {
                Log.Info("千牛启动连接自恢复识别到真实更新目标进程；仅在健康确认后允许一次受控千牛恢复。");
            }
            Task.Run(() => RunAsync(postUpdateLaunchAuthorized));
        }

        private static async Task RunAsync(bool postUpdateLaunchAuthorized)
        {
            // Keep the quick startup recovery for the common race where the local listener or
            // WebView appears a few seconds after Bot bootstrap.
            var delays = new[] { 2000, 3500, 5500, 8000, 11000 };
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                await Task.Delay(delays[attempt]).ConfigureAwait(false);

                try
                {
                    var snapshot = BotConnectionDiagnostics.GetSnapshot();
                    if (snapshot != null && snapshot.WebSocketSessionCount > 0)
                    {
                        Log.Info("千牛启动连接自恢复完成：注入WebSocket已连接，attempt=" + (attempt + 1));
                        return;
                    }

                    if (!IsLoopbackListenerActive())
                    {
                        Log.Error("千牛启动连接自恢复：127.0.0.1:41010 未监听，重新启动Bot WebSocket服务，attempt="
                            + (attempt + 1));
                        MyWebSocketServer.WSocketSvrInst.Start();
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("千牛启动连接自恢复检查失败：" + ex.Message, 5);
                }
            }

            var finalSnapshot = BotConnectionDiagnostics.GetSnapshot();
            if (finalSnapshot != null && finalSnapshot.WebSocketSessionCount > 0)
            {
                Log.Info("千牛启动连接自恢复完成：注入WebSocket已连接，fast-recovery-final=true");
                return;
            }

            if (postUpdateLaunchAuthorized)
            {
                var startupReady = await WaitForPostUpdateStartupReadyAsync().ConfigureAwait(false);
                if (startupReady)
                {
                    var recovered = await RunPostUpdateRecoveryAsync().ConfigureAwait(false);
                    if (recovered) return;
                }
                else
                {
                    Log.Error("更新后千牛恢复未获得启动健康确认；拒绝重启千牛并回落到无破坏性低频等待。");
                }
            }

            Log.Error("千牛启动连接进入降级恢复：Bot进程保持运行，注入脚本仍未连接；"
                + "普通启动或未获得更新健康授权时不会自动重启千牛。后续每30秒低频检测并自动恢复。"
                + " ws=" + (finalSnapshot == null ? string.Empty : finalSnapshot.WebSocketStatus)
                + ", injection=" + (finalSnapshot == null ? string.Empty : finalSnapshot.InjectionStatus));

            await RunDegradedRecoveryAsync().ConfigureAwait(false);
        }

        private static async Task<bool> WaitForPostUpdateStartupReadyAsync()
        {
            var until = DateTime.UtcNow + PostUpdateHealthWait;
            while (DateTime.UtcNow < until)
            {
                if (UpdateStartupHealthService.IsPostUpdateStartupReady()) return true;
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                if (snapshot != null && snapshot.WebSocketSessionCount > 0) return false;
                await Task.Delay(300).ConfigureAwait(false);
            }
            return UpdateStartupHealthService.IsPostUpdateStartupReady();
        }

        private static async Task<bool> RunPostUpdateRecoveryAsync()
        {
            Log.Info("更新后千牛恢复进入有限宽限期：等待注入页面自行恢复 "
                + (int)PostUpdateGracePeriod.TotalSeconds + " 秒；若自行恢复则取消千牛重启。");

            if (await WaitForInjectionAsync(PostUpdateGracePeriod, "post-update-grace").ConfigureAwait(false))
            {
                Log.Info("更新后千牛恢复：宽限期内注入已自行恢复，已取消千牛重启。");
                return true;
            }

            if (!IsLoopbackListenerActive())
            {
                Log.Error("更新后千牛恢复：Bot 41010监听仍未就绪，先恢复Bot WebSocket，拒绝把Bot端故障误判为千牛故障。");
                try { MyWebSocketServer.WSocketSvrInst.Start(); } catch (Exception ex) { Log.Exception(ex); }
                if (await WaitForInjectionAsync(TimeSpan.FromSeconds(10), "post-update-listener-retry").ConfigureAwait(false))
                    return true;
                if (!IsLoopbackListenerActive()) return false;
            }

            Log.Error("更新后千牛恢复：Bot WS已监听但注入在有限宽限期内仍未连接，执行唯一一次受控千牛重启。"
                + "本次授权仅来自已通过健康确认的真实版本更新。 ");

            var restarted = await TryRestartQianniuOnceAsync().ConfigureAwait(false);
            if (!restarted)
            {
                Log.Error("更新后千牛恢复：无法安全完成千牛重启，停止破坏性动作并回落低频等待。");
                return false;
            }

            return await WaitForInjectionAndRestoreUiAsync().ConfigureAwait(false);
        }

        private static async Task<bool> WaitForInjectionAsync(TimeSpan timeout, string stage)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                if (snapshot != null && snapshot.WebSocketSessionCount > 0) return true;

                if (!IsLoopbackListenerActive())
                {
                    try { MyWebSocketServer.WSocketSvrInst.Start(); }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("千牛恢复阶段重启Bot WebSocket失败: stage=" + stage + ", " + ex.Message, 10);
                    }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task<bool> TryRestartQianniuOnceAsync()
        {
            var executablePath = CaptureWorkbenchExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                Log.Error("更新后千牛恢复：无法从当前千牛进程取得可执行文件路径；不会猜测路径或启动其它程序。");
                return false;
            }

            Log.Info("更新后千牛恢复：准备受控重启千牛，exe=" + executablePath);
            TryCloseWorkbenchWindows();
            await Task.Delay(2500).ConfigureAwait(false);
            KillRemainingWorkbenchProcesses();
            await Task.Delay(1200).ConfigureAwait(false);

            try
            {
                var workingDirectory = Path.GetDirectoryName(executablePath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppDomain.CurrentDomain.BaseDirectory : workingDirectory,
                    UseShellExecute = true
                };
                var process = Process.Start(startInfo);
                Log.Info("更新后千牛恢复：千牛已重新启动；保留千牛自己的账号、密码和默认账号选择，不读取也不改写登录凭据。 pid="
                    + (process == null ? 0 : process.Id));
                return process != null;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "PostUpdateQianniuRestart");
                return false;
            }
        }

        private static string CaptureWorkbenchExecutablePath()
        {
            foreach (var name in MainWorkbenchProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var process in processes.OrderBy(p => p.Id))
                {
                    try
                    {
                        var path = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                    }
                }
            }
            return string.Empty;
        }

        private static void TryCloseWorkbenchWindows()
        {
            foreach (var name in MainWorkbenchProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var process in processes)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            process.CloseMainWindow();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("更新后千牛恢复：请求正常关闭千牛窗口失败: " + ex.Message, 5);
                    }
                }
            }
        }

        private static void KillRemainingWorkbenchProcesses()
        {
            foreach (var name in RestartCleanupProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("更新后千牛恢复：结束残留千牛进程失败: process=" + name + ", " + ex.Message, 10);
                    }
                }
            }
        }

        private static async Task<bool> WaitForInjectionAndRestoreUiAsync()
        {
            var until = DateTime.UtcNow + PostRestartRecoveryTimeout;
            var loginInvoked = false;
            var historyConfirmed = false;
            var receptionInvoked = false;
            var cycle = 0;

            while (DateTime.UtcNow < until)
            {
                cycle++;
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                if (snapshot != null && snapshot.WebSocketSessionCount > 0)
                {
                    Log.Info("更新后千牛恢复完成：重启后注入WebSocket已连接，cycle=" + cycle);
                    return true;
                }

                if (!IsLoopbackListenerActive())
                {
                    try { MyWebSocketServer.WSocketSvrInst.Start(); }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("更新后千牛恢复：重启后41010监听恢复失败: " + ex.Message, 10);
                    }
                }

                try
                {
                    DriveQianniuSavedSessionUi(ref loginInvoked, ref historyConfirmed, ref receptionInvoked);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("更新后千牛恢复UI检查失败: " + ex.Message, 20);
                }

                await Task.Delay(RecoveryPollDelay).ConfigureAwait(false);
            }

            Log.Error("更新后千牛恢复：已执行唯一一次千牛重启，但在 "
                + (int)PostRestartRecoveryTimeout.TotalSeconds
                + " 秒内仍未建立注入连接；不会重复重启，转入低频等待。"
                + " loginInvoked=" + loginInvoked
                + ", historyConfirmed=" + historyConfirmed
                + ", receptionInvoked=" + receptionInvoked);
            return false;
        }

        private static void DriveQianniuSavedSessionUi(
            ref bool loginInvoked,
            ref bool historyConfirmed,
            ref bool receptionInvoked)
        {
            using (var automation = new UIA3Automation())
            {
                foreach (var process in GetMainWorkbenchProcesses())
                {
                    FlaUI.Core.Application application;
                    try
                    {
                        application = FlaUI.Core.Application.Attach(process.Id);
                    }
                    catch
                    {
                        continue;
                    }

                    Window[] windows;
                    try
                    {
                        windows = application.GetAllTopLevelWindows(automation);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var window in windows)
                    {
                        if (window == null) continue;
                        var windowName = SafeName(window);
                        AutomationElement[] descendants;
                        try { descendants = window.FindAllDescendants(); }
                        catch { descendants = new AutomationElement[0]; }

                        // This is intentionally stricter than a generic “确认” click: the button is
                        // touched only when the same Qianniu window contains the exact historical
                        // message prompt requested by the recovery contract.
                        if (!historyConfirmed
                            && WindowContainsText(windowName, descendants, "是否需要打开之前的消息"))
                        {
                            if (TryClickExactNamedElement(descendants, new[] { "确认" }, "恢复之前的消息确认"))
                            {
                                historyConfirmed = true;
                                Log.Info("更新后千牛恢复：检测到“是否需要打开之前的消息？”并只点击了明确的“确认”。");
                                continue;
                            }
                        }

                        if (IsReceptionWindowName(windowName))
                        {
                            receptionInvoked = true;
                            continue;
                        }

                        // Do not select another account and never type credentials. Clicking the
                        // exact login action leaves Qianniu's own saved/default account selection
                        // and credential store as the sole login authority.
                        if (!loginInvoked
                            && TryClickExactNamedElement(descendants, LoginButtonNames, "默认账号登录"))
                        {
                            loginInvoked = true;
                            Log.Info("更新后千牛恢复：已使用千牛当前默认/历史账号执行登录按钮；未读取、填写或修改账号密码。");
                            continue;
                        }

                        if (!receptionInvoked
                            && !WindowLooksLikeLogin(windowName, descendants)
                            && TryClickExactNamedElement(descendants, ReceptionEntryNames, "打开接待窗口"))
                        {
                            receptionInvoked = true;
                            Log.Info("更新后千牛恢复：已点击明确的接待入口，等待千牛恢复接待聊天WebView与注入连接。");
                        }
                    }
                }
            }
        }

        private static Process[] GetMainWorkbenchProcesses()
        {
            return MainWorkbenchProcessNames
                .SelectMany(name =>
                {
                    try { return Process.GetProcessesByName(name); }
                    catch { return new Process[0]; }
                })
                .GroupBy(process => process.Id)
                .Select(group => group.First())
                .OrderBy(process => process.Id)
                .ToArray();
        }

        private static bool TryClickExactNamedElement(
            AutomationElement[] descendants,
            string[] allowedNames,
            string stage)
        {
            if (descendants == null || allowedNames == null) return false;
            foreach (var element in descendants)
            {
                if (element == null) continue;
                var name = SafeName(element);
                if (!allowedNames.Any(allowed => string.Equals(name, allowed, StringComparison.Ordinal))) continue;

                try
                {
                    element.AsButton().Invoke();
                    Log.Info("更新后千牛恢复UI操作成功: stage=" + stage + ", name=" + name + ", method=Invoke");
                    return true;
                }
                catch
                {
                }

                try
                {
                    var rect = element.BoundingRectangle;
                    if (rect.Width <= 1 || rect.Height <= 1) continue;
                    Mouse.Click(new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
                    Log.Info("更新后千牛恢复UI操作成功: stage=" + stage + ", name=" + name + ", method=bounded-coordinate");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("更新后千牛恢复UI操作失败: stage=" + stage + ", name=" + name + ", " + ex.Message, 10);
                }
            }
            return false;
        }

        private static bool WindowContainsText(string windowName, AutomationElement[] descendants, string text)
        {
            if ((windowName ?? string.Empty).IndexOf(text, StringComparison.Ordinal) >= 0) return true;
            if (descendants == null) return false;
            foreach (var element in descendants)
            {
                if (SafeName(element).IndexOf(text, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool WindowLooksLikeLogin(string windowName, AutomationElement[] descendants)
        {
            if ((windowName ?? string.Empty).IndexOf("登录", StringComparison.Ordinal) >= 0) return true;
            if (descendants == null) return false;
            return descendants.Any(element => LoginButtonNames.Any(
                allowed => string.Equals(SafeName(element), allowed, StringComparison.Ordinal)));
        }

        private static bool IsReceptionWindowName(string windowName)
        {
            windowName = (windowName ?? string.Empty).Trim();
            return windowName.IndexOf("千牛接待台", StringComparison.Ordinal) >= 0
                || windowName.IndexOf("接待中心", StringComparison.Ordinal) >= 0
                || windowName.IndexOf("客服接待", StringComparison.Ordinal) >= 0;
        }

        private static string SafeName(AutomationElement element)
        {
            if (element == null) return string.Empty;
            try { return (element.Name ?? string.Empty).Trim(); }
            catch { return string.Empty; }
        }

        private static async Task RunDegradedRecoveryAsync()
        {
            var cycle = 0;
            while (true)
            {
                await Task.Delay(DegradedRetryDelay).ConfigureAwait(false);
                cycle++;

                try
                {
                    var snapshot = BotConnectionDiagnostics.GetSnapshot();
                    if (snapshot != null && snapshot.WebSocketSessionCount > 0)
                    {
                        Log.Info("千牛降级连接自恢复完成：注入WebSocket重新连接，cycle=" + cycle);
                        return;
                    }

                    if (!IsLoopbackListenerActive())
                    {
                        Log.ErrorWithMaxCount(
                            "千牛降级连接自恢复：127.0.0.1:41010 未监听，重新启动Bot WebSocket服务。 cycle=" + cycle,
                            20);
                        MyWebSocketServer.WSocketSvrInst.Start();
                        continue;
                    }

                    // Normal startup remains non-destructive forever. A post-update run that has
                    // already consumed its one restart also lands here and can never restart again.
                    if (cycle == 1 || cycle % 20 == 0)
                    {
                        Log.Info("千牛降级连接仍在等待注入页面：不会继续/重复重启千牛。 cycle=" + cycle
                            + ", ws=" + (snapshot == null ? string.Empty : snapshot.WebSocketStatus)
                            + ", injection=" + (snapshot == null ? string.Empty : snapshot.InjectionStatus));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("千牛降级连接自恢复检查失败：" + ex.Message, 20);
                }
            }
        }

        private static bool IsLoopbackListenerActive()
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == WebSocketPort
                        && (IPAddress.IsLoopback(endpoint.Address)
                            || endpoint.Address.Equals(IPAddress.Any)
                            || endpoint.Address.Equals(IPAddress.IPv6Any)));
            }
            catch
            {
                return false;
            }
        }
    }
}
