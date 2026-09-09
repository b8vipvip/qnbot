using BotLib;
using BotLib.Extensions;
using Bot.ControllerNs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bot.Common.Db;
using Bot.Common;
using BotLib.Wpf.Extensions;
using Bot.ChromeNs;

namespace Bot
{
    public class BootStrap
    {
        public static async Task Init()
        {
            ClearTmpPathFiles();
            var languageResult = await LanguageStartupSafetyGate.CheckAndRepairLanguageSafely();
            BotConnectionDiagnostics.RecordLanguageStatus(languageResult.IsOk, languageResult.StatusText, languageResult.Detail);
            DeskScanner.LoopScan();
            MyWebSocketServer.WSocketSvrInst.Start();
            await QNInject.StartInject();
            QnStartupConnectionSelfHeal.Start();
            WeComAppBridgeClient.Start();

            //var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"inject.js"));
            //IseiyaHttpProxy.StartProxy(script);
        }

        private static void ClearTmpPathFiles()
        {
            try
            {
                if (Directory.GetFiles(PathEx.TmpPath).Length > 0)
                {
                    DirectoryEx.Delete(PathEx.TmpPath, true);
                    Directory.CreateDirectory(PathEx.TmpPath);
                }
            }
            catch (Exception e)
            {
                Log.Exception(e);
            }
        }
    }

    /// <summary>
    /// Startup must never destroy a live Qianniu WebView just to re-apply files that are already
    /// current. The legacy repair service is still used when Qianniu is closed, but a running
    /// workbench is inspected read-only and either accepted or marked for deferred repair.
    /// QNInject.IsInjected is the single version authority for both injection and language files;
    /// this gate must not carry a second set of version markers that can become stale.
    /// </summary>
    internal static class LanguageStartupSafetyGate
    {
        private const string WebuiRelativePath = @"Resources\newWebui\webui.zip";
        private static readonly string[] WorkbenchProcessNames =
        {
            "AliWorkbench", "new_AliWorkbench", "AliRender"
        };

        public static async Task<LanguageRepairResult> CheckAndRepairLanguageSafely()
        {
            if (!IsQianniuRunning())
            {
                return await LanguageRepairService.CheckAndRepairLanguage();
            }

            string activeZip;
            string discovery;
            if (TryGetActiveResourceZip(out activeZip, out discovery)
                && ActiveResourceHasCurrentMarkers(activeZip))
            {
                Log.Info("语言启动安全检查：运行中的千牛资源已由QNInject权威扫描确认为当前版本，跳过自动修复，不关闭WebView。 path=" + activeZip);
                return new LanguageRepairResult
                {
                    IsOk = true,
                    Repaired = false,
                    CurrentLanguage = "zh-CN",
                    StatusText = "语言：简体中文 ✓",
                    Detail = "运行中的千牛资源已通过QNInject完整扫描，注入与简体中文资源均为当前版本；启动未修改WebView。"
                };
            }

            var detail = "检测到千牛正在运行，但当前活动 webui 资源尚未通过QNInject权威扫描；"
                + "为保护登录态，本次启动不关闭WebView、不清缓存、不覆盖资源，待千牛关闭后再安全修复。"
                + (string.IsNullOrWhiteSpace(discovery) ? string.Empty : " " + discovery);
            Log.ErrorWithMaxCount("语言启动安全检查已延后破坏性修复：" + detail, 5);
            return new LanguageRepairResult
            {
                IsOk = false,
                Repaired = false,
                CurrentLanguage = "zh-CN",
                StatusText = "语言：待安全修复",
                Detail = detail
            };
        }

        internal static bool IsQianniuRunning()
        {
            foreach (var name in WorkbenchProcessNames)
            {
                try
                {
                    if (Process.GetProcessesByName(name).Any()) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        internal static bool TryGetActiveResourceZip(out string zipPath, out string detail)
        {
            zipPath = string.Empty;
            detail = string.Empty;

            foreach (var name in WorkbenchProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        var exe = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                        var directory = string.IsNullOrWhiteSpace(exe) ? null : new FileInfo(exe).Directory;
                        for (var depth = 0; directory != null && depth < 5; depth++, directory = directory.Parent)
                        {
                            var direct = Path.Combine(directory.FullName, WebuiRelativePath);
                            if (File.Exists(direct))
                            {
                                zipPath = direct;
                                detail = "activeExe=" + exe;
                                return true;
                            }

                            string fromIni;
                            if (TryResolveVersionZipFromInstallRoot(directory.FullName, out fromIni))
                            {
                                zipPath = fromIni;
                                detail = "activeExe=" + exe;
                                return true;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            foreach (var root in KnownInstallRoots())
            {
                string fromIni;
                if (TryResolveVersionZipFromInstallRoot(root, out fromIni))
                {
                    zipPath = fromIni;
                    detail = "installRoot=" + root;
                    return true;
                }
            }

            detail = "未定位到活动版本 webui.zip";
            return false;
        }

        private static IEnumerable<string> KnownInstallRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench")
            };
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static bool TryResolveVersionZipFromInstallRoot(string root, out string zipPath)
        {
            zipPath = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
                var ini = Path.Combine(root, "AliWorkbench.ini");
                if (!File.Exists(ini)) return false;
                var versionLine = File.ReadLines(ini)
                    .Select(line => (line ?? string.Empty).Trim())
                    .FirstOrDefault(line => line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(versionLine)) return false;
                var version = versionLine.Substring(versionLine.IndexOf('=') + 1).Trim().Trim('"');
                if (version.Length == 0) return false;
                var candidate = Path.Combine(root, version, WebuiRelativePath);
                if (!File.Exists(candidate)) return false;
                zipPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool ActiveResourceHasCurrentMarkers(string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return false;
            try
            {
                var newWebuiDirectory = Path.GetDirectoryName(zipPath);
                var resourceDirectory = string.IsNullOrWhiteSpace(newWebuiDirectory)
                    ? null
                    : Directory.GetParent(newWebuiDirectory);
                if (resourceDirectory == null) return false;

                var ready = QNInject.IsInjected(resourceDirectory.FullName);
                Log.Info("语言启动安全检查复用QNInject权威扫描: ready=" + ready
                    + ", resourcePath=" + resourceDirectory.FullName);
                return ready;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("语言启动安全检查读取活动webui失败：" + ex.Message, 5);
                return false;
            }
        }
    }
}
