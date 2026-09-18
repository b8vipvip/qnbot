using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.UpdateNs
{
    internal static partial class BotUpdateService
    {
        private static int _remoteInstallRunning;

        public static async Task<JObject> RemoteInstallLatestAsync()
        {
            if (Interlocked.CompareExchange(ref _remoteInstallRunning, 1, 0) != 0)
                throw new Exception("已有远程更新任务正在执行。");
            try
            {
                var check = await CheckNowAsync(false);
                if (check == null || !check.Success)
                    throw new Exception(check == null ? "检查更新未返回结果。" : check.Message);
                if (!check.UpdateAvailable || check.Release == null)
                    return new JObject
                    {
                        ["started"] = false,
                        ["current_version"] = CurrentVersion,
                        ["message"] = "当前已是最新版本。"
                    };
                if (string.IsNullOrWhiteSpace(check.Release.Sha256))
                    throw new Exception("正式发布清单缺少 SHA-256，拒绝远程安装。");

                var package = await DownloadPackageAsync(check.Release, null, CancellationToken.None);
                if (!IsPackageReady(check.Release))
                    throw new Exception("更新包下载后校验未通过。");

                var result = new JObject
                {
                    ["started"] = true,
                    ["current_version"] = CurrentVersion,
                    ["target_version"] = check.Release.Version,
                    ["message"] = "更新包已校验，正在安全交接到现有 updater/watchdog。"
                };
                LaunchInstaller(package, check.Release);
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref _remoteInstallRunning, 0);
            }
        }
    }
}
