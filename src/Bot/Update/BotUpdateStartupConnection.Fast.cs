using Bot.UpdateNs;
using BotLib;
using System;
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
    ///
    /// Automatic Bot updates must preserve the already logged-in Qianniu process and session.
    /// Recovery therefore owns only the Bot listener and waiting/retry policy. It never closes,
    /// kills, restarts, focuses, clicks, or otherwise drives Qianniu UI.
    /// </summary>
    internal static class QnStartupConnectionSelfHeal
    {
        private const int WebSocketPort = 41010;
        private static readonly TimeSpan DegradedRetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PostUpdateGracePeriod = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan PostUpdateHealthWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PostUpdateReconnectWindow = TimeSpan.FromSeconds(120);
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            var postUpdateLaunchAuthorized = UpdateStartupHealthService.IsPostUpdateLaunchAuthorized();
            if (postUpdateLaunchAuthorized)
            {
                Log.Info("千牛启动连接自恢复识别到真实更新目标进程；更新后仅恢复Bot监听并等待原千牛注入重连，不自动重启千牛。");
            }
            Task.Run(() => RunAsync(postUpdateLaunchAuthorized));
        }

        private static async Task RunAsync(bool postUpdateLaunchAuthorized)
        {
            // Keep the quick startup recovery for the common race where the new Bot process has not
            // yet rebound 41010 or the existing Qianniu WebView needs a few seconds to reconnect.
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
                        Log.Error("千牛启动连接自恢复：127.0.0.1:41010 未监听，重新启动Bot WebSocket服务，attempt=" + (attempt + 1));
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
                    Log.Error("更新后连接恢复未获得启动健康确认；保持原千牛进程与登录态并回落到无破坏性低频等待。");
                }
            }

            Log.Error("千牛启动连接进入降级恢复：Bot进程保持运行；保护当前千牛进程与登录态，不自动重启千牛。后续每30秒低频检测并自动恢复。"
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
            Log.Info("更新后连接恢复进入有限宽限期：等待原千牛注入页面自行重连 "
                + (int)PostUpdateGracePeriod.TotalSeconds
                + " 秒；自动更新不会关闭或重启千牛。");

            if (await WaitForInjectionAsync(PostUpdateGracePeriod, "post-update-grace").ConfigureAwait(false))
            {
                Log.Info("更新后连接恢复：宽限期内原千牛注入已自行重连；千牛进程和登录态保持不变。");
                return true;
            }

            if (!IsLoopbackListenerActive())
            {
                Log.Error("更新后连接恢复：Bot 41010监听仍未就绪，先恢复Bot WebSocket；不会把Bot端监听故障转换为千牛重启。");
                try { MyWebSocketServer.WSocketSvrInst.Start(); }
                catch (Exception ex) { Log.Exception(ex); }

                if (await WaitForInjectionAsync(TimeSpan.FromSeconds(10), "post-update-listener-retry").ConfigureAwait(false))
                {
                    Log.Info("更新后连接恢复：41010恢复后原千牛注入已重新连接；未重启千牛。");
                    return true;
                }
            }

            Log.Error("更新后Bot WS已恢复但原千牛注入尚未重连；为保护已登录千牛会话，不执行千牛重启、不操作登录界面。"
                + " 继续等待 " + (int)PostUpdateReconnectWindow.TotalSeconds + " 秒。");

            if (await WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session").ConfigureAwait(false))
            {
                Log.Info("更新后连接恢复完成：原千牛注入在保护窗口内重新连接；未重启千牛。");
                return true;
            }

            Log.Error("更新后千牛注入在 " + (int)PostUpdateReconnectWindow.TotalSeconds
                + " 秒内仍未重连；保持原千牛进程与登录态，转入低频等待，不重启千牛、不操作登录界面。");
            return false;
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
                        Log.ErrorWithMaxCount("千牛降级连接自恢复：127.0.0.1:41010 未监听，重新启动Bot WebSocket服务。 cycle=" + cycle, 20);
                        MyWebSocketServer.WSocketSvrInst.Start();
                        continue;
                    }

                    if (cycle == 1 || cycle % 20 == 0)
                    {
                        Log.Info("千牛降级连接仍在等待原注入页面：保护千牛进程与登录态，不会自动重启或操作登录界面。 cycle=" + cycle
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
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == WebSocketPort
                        && (IPAddress.IsLoopback(endpoint.Address)
                            || endpoint.Address.Equals(IPAddress.Any)
                            || endpoint.Address.Equals(IPAddress.IPv6Any)));
            }
            catch { return false; }
        }
    }
}
