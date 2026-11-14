using Bot.UpdateNs;
using Bot.Common;
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
    /// 127.0.0.1:41010 listener or Qianniu injected business channel is not yet available.
    ///
    /// Recovery owns the Bot listener and safe resource migration only. It never closes,
    /// kills, restarts, focuses, clicks, or otherwise drives a live Qianniu UI.
    /// </summary>
    internal static class QnStartupConnectionSelfHeal
    {
        private const int WebSocketPort = 41010;
        private static readonly TimeSpan DegradedRetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PostUpdateGracePeriod = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan PostUpdateHealthWait = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PostUpdateReconnectWindow = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan HealthySnapshotMaxAge = TimeSpan.FromSeconds(90);
        private static int _started;
        private static int _deferredRepairCompleted;
        private static int _deferredRepairRunning;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            var postUpdateLaunchAuthorized = UpdateStartupHealthService.IsPostUpdateLaunchAuthorized();
            if (postUpdateLaunchAuthorized)
            {
                Log.Info("千牛启动连接自恢复识别到真实更新目标进程；仅恢复Bot监听、等待原千牛注入重连和安全延迟迁移，不自动重启千牛。");
            }
            Task.Run(() => RunAsync(postUpdateLaunchAuthorized));
        }

        private static async Task RunAsync(bool postUpdateLaunchAuthorized)
        {
            var delays = new[] { 2000, 3500, 5500, 8000, 11000 };
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                await Task.Delay(delays[attempt]).ConfigureAwait(false);
                try
                {
                    var snapshot = BotConnectionDiagnostics.GetSnapshot();
                    if (IsBusinessChannelReady(snapshot))
                    {
                        Log.Info("千牛启动连接自恢复完成：权威CDP业务通道已就绪，attempt=" + (attempt + 1));
                        return;
                    }

                    if (!IsBotListenerActive())
                    {
                        Log.Error("千牛启动连接自恢复：Bot 127.0.0.1:41010监听未就绪，重建Bot WebSocket服务，attempt=" + (attempt + 1));
                        MyWebSocketServer.WSocketSvrInst.Start();
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("千牛启动连接自恢复检查失败：" + ex.Message, 5);
                }
            }

            var finalSnapshot = BotConnectionDiagnostics.GetSnapshot();
            if (IsBusinessChannelReady(finalSnapshot))
            {
                Log.Info("千牛启动连接自恢复完成：权威CDP业务通道已就绪，fast-recovery-final=true");
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

            Log.Error("千牛启动连接进入降级恢复：Bot进程保持运行；保护当前千牛进程与登录态，不自动重启千牛。"
                + " 每30秒检查Bot监听、权威CDP和延迟资源迁移。"
                + " ws=" + (finalSnapshot == null ? string.Empty : finalSnapshot.WebSocketStatus)
                + ", injection=" + (finalSnapshot == null ? string.Empty : finalSnapshot.InjectionStatus)
                + ", authoritativeCdp=" + (finalSnapshot == null ? 0 : finalSnapshot.AuthoritativeCdpSessionCount)
                + ", cdpReady=" + (finalSnapshot != null && finalSnapshot.CdpReady));
            await RunDegradedRecoveryAsync().ConfigureAwait(false);
        }

        private static bool IsBusinessChannelReady(ConnectionDiagnosticsSnapshot snapshot)
        {
            if (snapshot == null
                || !snapshot.WebSocketServerStarted
                || snapshot.WebSocketSessionCount <= 0
                || !snapshot.InjectionConnected
                || snapshot.AuthoritativeCdpSessionCount <= 0
                || !snapshot.CdpReady
                || string.IsNullOrWhiteSpace(snapshot.Seller))
            {
                return false;
            }

            return snapshot.LastUpdateTime != DateTime.MinValue
                && DateTime.Now - snapshot.LastUpdateTime <= HealthySnapshotMaxAge;
        }

        private static bool IsBotListenerActive()
        {
            var snapshot = BotConnectionDiagnostics.GetSnapshot();
            return snapshot != null
                && snapshot.WebSocketServerStarted
                && IsLoopbackListenerActive();
        }

        private static async Task<bool> WaitForPostUpdateStartupReadyAsync()
        {
            var until = DateTime.UtcNow + PostUpdateHealthWait;
            while (DateTime.UtcNow < until)
            {
                if (UpdateStartupHealthService.IsPostUpdateStartupReady()) return true;
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                if (IsBusinessChannelReady(snapshot)) return false;
                await Task.Delay(300).ConfigureAwait(false);
            }
            return UpdateStartupHealthService.IsPostUpdateStartupReady();
        }

        private static async Task<bool> RunPostUpdateRecoveryAsync()
        {
            Log.Info("更新后连接恢复进入有限宽限期：等待原千牛注入页面建立权威CDP业务通道 "
                + (int)PostUpdateGracePeriod.TotalSeconds
                + " 秒；自动更新不会关闭或重启千牛。");

            if (await WaitForInjectionAsync(PostUpdateGracePeriod, "post-update-grace").ConfigureAwait(false))
            {
                Log.Info("更新后连接恢复：原千牛权威CDP业务通道已就绪；千牛进程和登录态保持不变。");
                return true;
            }

            if (!IsBotListenerActive())
            {
                Log.Error("更新后连接恢复：Bot 41010监听未就绪，先恢复Bot WebSocket；不会把Bot端监听故障转换为千牛重启。");
                try { MyWebSocketServer.WSocketSvrInst.Start(); }
                catch (Exception ex) { Log.Exception(ex); }

                if (await WaitForInjectionAsync(TimeSpan.FromSeconds(10), "post-update-listener-retry").ConfigureAwait(false))
                {
                    Log.Info("更新后连接恢复：41010恢复后权威CDP业务通道已就绪；未重启千牛。");
                    return true;
                }
            }

            Log.Error("更新后Bot WS已恢复但权威CDP业务通道尚未就绪；为保护已登录千牛会话，不执行千牛重启、不操作登录界面。"
                + " 继续等待 " + (int)PostUpdateReconnectWindow.TotalSeconds + " 秒。");

            if (await WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session").ConfigureAwait(false))
            {
                Log.Info("更新后连接恢复完成：原千牛权威CDP业务通道已恢复；未重启千牛。");
                return true;
            }

            Log.Error("更新后千牛权威CDP业务通道在 " + (int)PostUpdateReconnectWindow.TotalSeconds
                + " 秒内仍未恢复；保持原千牛进程与登录态，转入低频等待，不重启千牛、不操作登录界面。");
            return false;
        }

        private static async Task<bool> WaitForInjectionAsync(TimeSpan timeout, string stage)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (IsBusinessChannelReady(BotConnectionDiagnostics.GetSnapshot())) return true;

                if (!IsBotListenerActive())
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
            if (!Bot.LanguageStartupSafetyGate.IsQianniuRunning())
            {
                await TryRepairDeferredResourcesAsync(0).ConfigureAwait(false);
            }

            var cycle = 0;
            while (true)
            {
                await Task.Delay(DegradedRetryDelay).ConfigureAwait(false);
                cycle++;
                try
                {
                    var snapshot = BotConnectionDiagnostics.GetSnapshot();
                    if (IsBusinessChannelReady(snapshot))
                    {
                        Log.Info("千牛降级连接自恢复完成：权威CDP业务通道已重新建立，cycle=" + cycle);
                        return;
                    }

                    if (!IsBotListenerActive())
                    {
                        Log.ErrorWithMaxCount("千牛降级连接自恢复：Bot 127.0.0.1:41010监听未就绪，重建Bot WebSocket服务。 cycle=" + cycle, 20);
                        MyWebSocketServer.WSocketSvrInst.Start();
                    }

                    if (Volatile.Read(ref _deferredRepairCompleted) == 0
                        && !Bot.LanguageStartupSafetyGate.IsQianniuRunning())
                    {
                        await TryRepairDeferredResourcesAsync(cycle).ConfigureAwait(false);
                    }

                    if (cycle == 1 || cycle % 20 == 0)
                    {
                        Log.Info("千牛降级连接仍未达到业务就绪：不会把standby页面误判为恢复成功；保护千牛进程与登录态。"
                            + " cycle=" + cycle
                            + ", ws=" + (snapshot == null ? string.Empty : snapshot.WebSocketStatus)
                            + ", injection=" + (snapshot == null ? string.Empty : snapshot.InjectionStatus)
                            + ", authoritativeCdp=" + (snapshot == null ? 0 : snapshot.AuthoritativeCdpSessionCount)
                            + ", cdpReady=" + (snapshot != null && snapshot.CdpReady)
                            + ", deferredRepairCompleted=" + (Volatile.Read(ref _deferredRepairCompleted) != 0));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("千牛降级连接自恢复检查失败：" + ex.Message, 20);
                }
            }
        }

        private static async Task TryRepairDeferredResourcesAsync(int cycle)
        {
            if (Volatile.Read(ref _deferredRepairCompleted) != 0) return;
            if (Interlocked.CompareExchange(ref _deferredRepairRunning, 1, 0) != 0) return;

            try
            {
                if (Bot.LanguageStartupSafetyGate.IsQianniuRunning()) return;

                var languageResult = await Bot.LanguageStartupSafetyGate.CheckAndRepairLanguageSafely().ConfigureAwait(false);
                BotConnectionDiagnostics.RecordLanguageStatus(
                    languageResult.IsOk,
                    languageResult.StatusText,
                    languageResult.Detail);

                if (Bot.LanguageStartupSafetyGate.IsQianniuRunning())
                {
                    Log.Info("千牛在延迟语言修复后重新启动；停止后续资源迁移，保留当前登录态。");
                    return;
                }

                var injectReady = await QNInject.TryRepairWhenQianniuClosedAsync().ConfigureAwait(false);
                if (languageResult.IsOk && injectReady)
                {
                    Interlocked.Exchange(ref _deferredRepairCompleted, 1);
                    Log.Info("千牛延迟资源修复已完成：v12注入与简体中文资源均已验证；等待用户启动/重新打开千牛加载新WebView。 cycle=" + cycle);
                }
                else
                {
                    Log.ErrorWithMaxCount("千牛延迟资源修复尚未完成，将在下一个安全周期重试。"
                        + " cycle=" + cycle
                        + ", languageOk=" + languageResult.IsOk
                        + ", injectReady=" + injectReady
                        + ", languageStatus=" + (languageResult.StatusText ?? string.Empty), 10);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("千牛延迟资源修复失败，将在下一个安全周期重试: " + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _deferredRepairRunning, 0);
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