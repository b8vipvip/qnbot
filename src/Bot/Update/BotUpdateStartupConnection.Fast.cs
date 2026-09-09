using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Common;

namespace Bot.Update
{
    internal static class QnStartupConnectionSelfHeal
    {
        private static int _started;
        private static readonly int[] RetryDelaySeconds = { 3, 5, 8, 12 };

        internal static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            QnLanguageStatusReconciler.TryReconcile();
            Task.Run(RunAsync);
        }

        private static async Task RunAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (IsInjectionReady())
                {
                    BotConnectionDiagnostics.RecordInjectionStatus(true, "千牛注入连接已就绪。");
                    return;
                }

                for (var attempt = 0; attempt < RetryDelaySeconds.Length; attempt++)
                {
                    if (IsInjectionReady())
                    {
                        BotConnectionDiagnostics.RecordInjectionStatus(true, "千牛注入连接已恢复。");
                        return;
                    }

                    var attemptNo = attempt + 1;
                    Log.Warn("[Bot][StartupRecovery] 千牛注入尚未连接，执行启动期安全恢复 attempt=" + attemptNo + "/" + RetryDelaySeconds.Length);
                    BotConnectionDiagnostics.RecordInjectionStatus(false, "千牛注入尚未连接，正在执行启动期安全恢复（" + attemptNo + "/" + RetryDelaySeconds.Length + "）。");

                    try { QNInject.StartInject(); }
                    catch (Exception ex) { Log.Warn("[Bot][StartupRecovery] 启动期注入维护失败：" + ex.Message); }

                    try
                    {
                        var qnProc = WinApi.GetQNProc();
                        if (qnProc != null && !qnProc.HasExited)
                        {
                            await QN.TryBringReceptionWindowForRecoveryAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[Bot][StartupRecovery] 启动期接待窗口恢复失败：" + ex.Message);
                    }

                    var deadline = DateTime.UtcNow.AddSeconds(RetryDelaySeconds[attempt]);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (IsInjectionReady())
                        {
                            BotConnectionDiagnostics.RecordInjectionStatus(true, "千牛注入连接已恢复。");
                            Log.Info("[Bot][StartupRecovery] 千牛注入连接恢复成功 attempt=" + attemptNo);
                            return;
                        }
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }

                var postUpdate = await PostUpdateQianniuRecovery.TryRecoverAsync(IsInjectionReady).ConfigureAwait(false);
                if (postUpdate.Recovered && IsInjectionReady())
                {
                    BotConnectionDiagnostics.RecordInjectionStatus(true, postUpdate.Message);
                    Log.Info("[Bot][StartupRecovery] " + postUpdate.Message);
                    return;
                }
                if (postUpdate.Eligible)
                {
                    Log.Warn("[Bot][StartupRecovery] 更新后千牛恢复结果：" + postUpdate.Message);
                }

                BotConnectionDiagnostics.RecordInjectionStatus(false,
                    postUpdate.Eligible
                        ? "更新后已执行有界千牛恢复，但注入仍未连接；已停止自动重启以避免循环，请查看运行日志。"
                        : "千牛注入仍未连接；已进入低频后台恢复，不会自动重启千牛以保护登录态。");
                Log.Warn("[Bot][StartupRecovery] 启动期安全恢复结束，injection 仍未连接。 " + postUpdate.Message);
            }
            catch (Exception ex)
            {
                Log.Warn("[Bot][StartupRecovery] 启动期恢复任务异常：" + ex.Message);
            }
        }

        private static bool IsInjectionReady()
        {
            try
            {
                var hasSeller = QN.MyWebSocketServer?.SellerDict != null &&
                                QN.MyWebSocketServer.SellerDict.Values.Any(x => x != null && x.ReadyState == 1);
                if (hasSeller) return true;
            }
            catch { }

            try { return QN.RPA != null && QN.RPA.Connected; }
            catch { return false; }
        }
    }
}
