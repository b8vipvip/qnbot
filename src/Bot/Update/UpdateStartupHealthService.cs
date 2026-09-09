using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.Common;
using BotLib;

namespace Bot.UpdateNs
{
    internal static class UpdateStartupHealthService
    {
        private const string HealthFileEnvironmentVariable = "QIANNIU_BOT_UPDATE_HEALTH_FILE";
        private const string ExpectedVersionEnvironmentVariable = "QIANNIU_BOT_UPDATE_EXPECTED_VERSION";
        private static int _postUpdateLaunchAuthorized;
        private static int _postUpdateStartupReady;

        /// <summary>
        /// Returns true only for the one-shot process launched by the updater for the expected
        /// target version. Normal Bot startup has no updater capability and can never authorize
        /// destructive Qianniu recovery.
        /// </summary>
        internal static bool IsPostUpdateLaunchAuthorized()
        {
            if (Volatile.Read(ref _postUpdateLaunchAuthorized) != 0) return true;

            try
            {
                var expectedVersion = (Environment.GetEnvironmentVariable(ExpectedVersionEnvironmentVariable) ?? string.Empty).Trim();
                var currentVersion = (BotUpdateService.CurrentVersion ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(expectedVersion)
                    || !string.Equals(expectedVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Interlocked.Exchange(ref _postUpdateLaunchAuthorized, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A post-update Qianniu restart is permitted only after this exact target process has
        /// written the updater health acknowledgement. This prevents a candidate process from
        /// disturbing Qianniu before the updater has accepted the new Bot runtime as healthy.
        /// </summary>
        internal static bool IsPostUpdateStartupReady()
        {
            return Volatile.Read(ref _postUpdateStartupReady) != 0;
        }

        internal static void ReportReady()
        {
            TryReportLastUpdaterResult();

            var path = Environment.GetEnvironmentVariable(HealthFileEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path)) return;

            var expectedVersion = (Environment.GetEnvironmentVariable(ExpectedVersionEnvironmentVariable) ?? string.Empty).Trim();
            var currentVersion = (BotUpdateService.CurrentVersion ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && !string.Equals(expectedVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log.ErrorWithMaxCount(
                    "更新启动健康检查拒绝旧版本进程冒充目标版本: expected=" + expectedVersion
                    + ", actual=" + currentVersion
                    + ", pid=" + Process.GetCurrentProcess().Id,
                    5);
                ClearOneShotUpdateEnvironment();
                return;
            }

            var postUpdateAuthorized = IsPostUpdateLaunchAuthorized();
            try
            {
                // Reassert database readiness at the point where configuration and startup
                // services have also completed. The updater accepts only this explicit contract.
                DbHelper.EnsureInitialized();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temporary = path + ".tmp";
                var payload = new
                {
                    status = "OK",
                    pid = Process.GetCurrentProcess().Id,
                    release_version = currentVersion,
                    expected_version = expectedVersion,
                    executable_path = Process.GetCurrentProcess().MainModule.FileName,
                    database_initialized = true,
                    configuration_loaded = true,
                    services_started = true,
                    created_at = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(temporary, JsonConvert.SerializeObject(payload));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                if (postUpdateAuthorized)
                {
                    Interlocked.Exchange(ref _postUpdateStartupReady, 1);
                }
                Log.Info("更新启动健康检查返回OK: version=" + currentVersion + ", path=" + path);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {
                // The health-file contract is a one-shot updater capability. Never let normal
                // child processes inherit it after startup has reported readiness.
                ClearOneShotUpdateEnvironment();
            }
        }

        private static void ClearOneShotUpdateEnvironment()
        {
            try { Environment.SetEnvironmentVariable(HealthFileEnvironmentVariable, null); } catch { }
            try { Environment.SetEnvironmentVariable(ExpectedVersionEnvironmentVariable, null); } catch { }
        }

        private static void TryReportLastUpdaterResult()
        {
            try
            {
                var updaterRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBotUpdater");
                var resultPath = Path.Combine(updaterRoot, "last-update-result.json");
                if (!File.Exists(resultPath)) return;

                var json = JObject.Parse(File.ReadAllText(resultPath));
                var status = Convert.ToString(json["status"]) ?? string.Empty;
                var target = Convert.ToString(json["target_version"]) ?? string.Empty;
                var stage = Convert.ToString(json["stage"]) ?? string.Empty;
                var detail = Convert.ToString(json["detail"]) ?? string.Empty;
                var rollback = Convert.ToString(json["rollback"]) ?? string.Empty;
                var logPath = Convert.ToString(json["log_path"]) ?? string.Empty;
                var createdAt = Convert.ToString(json["created_at"]) ?? string.Empty;

                Log.Info(
                    "上次Bot更新器结果: status=" + status
                    + ", target=" + target
                    + ", stage=" + stage
                    + ", rollback=" + rollback
                    + ", createdAt=" + createdAt
                    + ", detail=" + detail
                    + ", updaterLog=" + logPath);

                var reportedPath = Path.Combine(updaterRoot, "last-update-result.reported.json");
                try
                {
                    if (File.Exists(reportedPath)) File.Delete(reportedPath);
                    File.Move(resultPath, reportedPath);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取上次Bot更新器结果失败: " + ex.Message, 5);
            }
        }
    }
}
