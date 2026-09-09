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
    /// Ordinary startup never restarts Qianniu. Only PostUpdateQianniuRecovery may do
    /// one controlled restart after a fresh successful updater result proves that this
    /// is the current version's post-update recovery window.
    /// </summary>
    internal static class QnStartupConnectionSelfHeal
    {
        private const int WebSocketPort = 41010;
        private static readonly TimeSpan DegradedRetryDelay = TimeSpan.FromSeconds(30);
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            QnLanguageStatusReconciler.TryReconcile();
            Task.Run(() => RunAsync());
        }

        private static async Task RunAsync()
        {
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

            var postUpdate = await PostUpdateQianniuRecovery.TryRecoverAsync(() =>
            {
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                return snapshot != null && snapshot.WebSocketSessionCount > 0;
            }).ConfigureAwait(false);
            if (postUpdate.Recovered)
            {
                var recoveredSnapshot = BotConnectionDiagnostics.GetSnapshot();
                if (recoveredSnapshot != null && recoveredSnapshot.WebSocketSessionCount > 0)
                {
                    Log.Info("千牛更新后连接恢复完成：" + postUpdate.Message);
                    return;
                }
            }
            if (postUpdate.Eligible)
            {
                Log.ErrorWithMaxCount("千牛更新后有界恢复结束：" + postUpdate.Message, 5);
            }

            finalSnapshot = BotConnectionDiagnostics.GetSnapshot();
            Log.Error("千牛启动连接进入降级恢复：Bot进程保持运行，注入脚本仍未连接；"
                + "不会自动重启千牛，避免破坏登录态。后续每30秒低频检测并自动恢复。"
                + " ws=" + (finalSnapshot == null ? string.Empty : finalSnapshot.WebSocketStatus)
                + ", injection=" + (finalSnapshot == null ? string.Empty : finalSnapshot.InjectionStatus));

            await RunDegradedRecoveryAsync().ConfigureAwait(false);
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

                    if (cycle == 1 || cycle % 20 == 0)
                    {
                        Log.Info("千牛降级连接仍在等待注入页面：未自动重启千牛。 cycle=" + cycle
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
