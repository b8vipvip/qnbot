using BotLib;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.Common
{
    public class LanguageRepairResult
    {
        public bool IsOk { get; set; }
        public bool Repaired { get; set; }
        public string CurrentLanguage { get; set; }
        public string StatusText { get; set; }
        public string Detail { get; set; }
    }

    public static class LanguageRepairService
    {
        private static readonly string[] WorkbenchProcessNames = { "AliWorkbench", "new_AliWorkbench", "AliRender", "wwcmd", "wangwang", "AliApp" };
        private static readonly string[] ResourceOwnerProcessNames = { "AliWorkbench", "new_AliWorkbench", "AliRender", "AliApp" };
        private static readonly string[] AbnormalLanguageTokens = { "zh-TW", "zh_TW", "zh-HK", "zh_HK", "traditional", "繁体", "繁體" };
        private static readonly string[] LanguageKeys = { "locale", "language", "lang" };
        private const string WebuiResDir = "newWebui";
        private const string WebuiFile = "webui.zip";
        private const string SignFile = "sign.json";
        private const string ChatRecentHtmlFile = "web_chat-packer/recent.html";
        private const string InjectedScriptFile = "web_chat-packer/qnbot-inject.js";
        private const string InjectedScriptSrc = "qnbot-inject.js";
        private const string LanguageScriptFileName = "qnbot-language.js";
        private const string EmbeddedInjectResource = "Bot.Resources.inject.js";
        private const string EmbeddedLanguageResource = "Bot.Resources.language.js";
        private const string InjectVersionMarker = "20261114-ws-recoverable-reconnect-v12";
        private const string LanguageVersionMarker = "20260713-hans-all-pages-v3";
        private const string LanguageLockMarker = "qnbot persistent zh-CN language lock";
        private const string OldRemoteOverwriteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js";
        private static readonly string StatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qnbot_language_state.txt");

        public static async Task<LanguageRepairResult> CheckAndRepairLanguage()
        {
            return await Task.Run(() => CheckAndRepairLanguageCore());
        }

        private static LanguageRepairResult CheckAndRepairLanguageCore()
        {
            var result = new LanguageRepairResult
            {
                IsOk = false,
                Repaired = false,
                CurrentLanguage = "zh-CN",
                StatusText = "语言：正在检测",
                Detail = string.Empty
            };

            try
            {
                Log.Info("语言环境检查开始");
                var previousAbnormal = ReadPreviousLanguageStateAbnormal();
                var installPaths = FindInstallPaths();
                if (installPaths.Count < 1)
                {
                    result.StatusText = "语言：未检测到千牛";
                    result.Detail = "没有找到千牛安装路径";
                    Log.Info(result.Detail);
                    WriteLanguageState(false, result.Detail);
                    return result;
                }

                Log.Info("千牛安装路径: " + string.Join(" | ", installPaths));
                var resourcePaths = FindResourcePaths(installPaths);
                if (resourcePaths.Count < 1)
                {
                    result.StatusText = "语言：未找到webui.zip";
                    result.Detail = "没有找到 Resources\\newWebui\\webui.zip";
                    Log.Info(result.Detail);
                    WriteLanguageState(false, result.Detail);
                    return result;
                }

                var languageScan = DetectLanguageFromProfiles();
                result.CurrentLanguage = languageScan.CurrentLanguage;
                Log.Info("检测当前语言 " + result.CurrentLanguage);

                var notReadyPaths = resourcePaths.Where(p => !IsLanguageReady(p)).ToList();
                var needRepair = languageScan.IsAbnormal || previousAbnormal || notReadyPaths.Count > 0;
                if (!needRepair)
                {
                    result.IsOk = true;
                    result.StatusText = "语言：简体中文 ✓";
                    result.Detail = "语言环境正常 zh-CN";
                    Log.Info("语言环境正常 zh-CN");
                    WriteLanguageState(true, result.Detail);
                    return result;
                }

                Log.Info("执行自动修复");
                if (IsWorkbenchRunning())
                {
                    result.StatusText = "语言：待安全修复";
                    result.Detail = "检测到千牛资源进程仍在运行；为保护登录态，本次不修改webui.zip/sign.json/CEF缓存，待千牛完全退出后重试。";
                    Log.Info(result.Detail);
                    WriteLanguageState(false, result.Detail);
                    return result;
                }

                var success = 0;
                foreach (var resourcePath in resourcePaths)
                {
                    try
                    {
                        if (RepairResourcePath(resourcePath))
                        {
                            success++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "LanguageRepair:" + resourcePath);
                    }
                }

                if (success > 0 || languageScan.IsAbnormal || previousAbnormal)
                {
                    if (IsWorkbenchRunning())
                    {
                        Log.Info("千牛资源进程已重新启动，跳过语言CEF缓存清理以保护当前会话。");
                    }
                    else
                    {
                        Log.Info("清理CEF临时缓存（保留Local Storage/Cookies/IndexedDB）");
                        ClearQianniuLanguageCaches();
                    }
                }

                result.Repaired = success > 0 || languageScan.IsAbnormal || previousAbnormal;
                result.IsOk = resourcePaths.All(IsLanguageReady);
                result.CurrentLanguage = "zh-CN";
                result.StatusText = languageScan.IsAbnormal || previousAbnormal ? "语言：发现繁体，已自动修复" : "语言：简体中文 ✓";
                result.Detail = result.IsOk ? "语言修复完成 zh-CN" : "语言修复未完全完成，请检查千牛安装目录权限";
                Log.Info(result.Detail);
                WriteLanguageState(result.IsOk, result.Detail);
                return result;
            }
            catch (Exception ex)
            {
                result.StatusText = "语言：检测失败";
                result.Detail = ex.Message;
                Log.Exception(ex);
                WriteLanguageState(false, ex.Message);
                return result;
            }
        }

        private static bool IsLanguageReady(string resourcePath)
        {
            try
            {
                var webuiPath = Path.Combine(resourcePath, WebuiResDir, WebuiFile);
                if (!File.Exists(webuiPath)) return false;
                using (var zipFile = new ZipFile(webuiPath))
                {
                    var signPath = Path.Combine(resourcePath, WebuiResDir, SignFile);
                    if (File.Exists(signPath) && new FileInfo(signPath).Length > 0) return false;
                    var recent = ReadZipEntryText(zipFile, ChatRecentHtmlFile);
                    if (string.IsNullOrWhiteSpace(recent)
                        || !ContainsScriptReference(recent, InjectedScriptSrc)
                        || recent.IndexOf(OldRemoteOverwriteUrl, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return false;
                    }

                    var inject = ReadZipEntryText(zipFile, InjectedScriptFile);
                    if (string.IsNullOrWhiteSpace(inject)
                        || inject.IndexOf(InjectVersionMarker, StringComparison.OrdinalIgnoreCase) < 0
                        || inject.IndexOf(LanguageLockMarker, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return false;
                    }

                    foreach (var key in LanguageKeys)
                    {
                        if (inject.IndexOf("localStorage.setItem(\"" + key + "\",\"zh-CN\")", StringComparison.OrdinalIgnoreCase) < 0
                            && inject.IndexOf("localStorage.setItem(\"" + key + "\", \"zh-CN\")", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            return false;
                        }
                    }

                    var htmlEntries = GetHtmlEntryNames(zipFile);
                    if (htmlEntries.Count < 1) return false;
                    foreach (var htmlEntryName in htmlEntries)
                    {
                        var html = ReadZipEntryText(zipFile, htmlEntryName);
                        if (string.IsNullOrWhiteSpace(html) || !ContainsScriptReference(html, LanguageScriptFileName)) return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "IsLanguageReady:" + resourcePath);
                return false;
            }
        }

        private static bool RepairResourcePath(string resourcePath)
        {
            if (IsWorkbenchRunning())
            {
                Log.Info("千牛资源进程仍在运行，延后语言资源修复: " + resourcePath);
                return false;
            }
            var webuiPath = Path.Combine(resourcePath, WebuiResDir, WebuiFile);
            var signPath = Path.Combine(resourcePath, WebuiResDir, SignFile);
            if (!File.Exists(webuiPath))
            {
                Log.Info("webui.zip不存在: " + webuiPath);
                return false;
            }

            var injectContent = ReadScriptPayload(EmbeddedInjectResource, "inject.js", InjectVersionMarker, LanguageLockMarker);
            var languageContent = ReadScriptPayload(EmbeddedLanguageResource, "language.js", LanguageVersionMarker, string.Empty);
            if (string.IsNullOrWhiteSpace(injectContent) || string.IsNullOrWhiteSpace(languageContent))
            {
                return false;
            }

            BackupWebuiZip(webuiPath);
            using (var zipFile = new ZipFile(webuiPath))
            {
                var htmlEntries = GetHtmlEntryNames(zipFile);
                if (!htmlEntries.Any(n => string.Equals(n, ChatRecentHtmlFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info("webui.zip中没有找到 " + ChatRecentHtmlFile + ": " + webuiPath);
                    return false;
                }

                var patchedHtml = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var languageEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var htmlEntry in htmlEntries)
                {
                    var html = ReadZipEntryText(zipFile, htmlEntry);
                    if (html == null) continue;
                    if (string.Equals(htmlEntry, ChatRecentHtmlFile, StringComparison.OrdinalIgnoreCase))
                    {
                        html = PlaceInjectedScriptFirst(html);
                    }
                    html = PlaceLanguageScriptFirst(html);
                    patchedHtml[htmlEntry] = html;
                    languageEntries.Add(GetZipDirectory(htmlEntry) + LanguageScriptFileName);
                }

                if (IsWorkbenchRunning())
                {
                    Log.Info("语言资源提交前检测到千牛重新启动，取消本次修复: " + webuiPath);
                    return false;
                }
                zipFile.BeginUpdate();
                foreach (var item in patchedHtml)
                {
                    zipFile.Add(new ZipStaticDataSource(item.Value), item.Key);
                }
                foreach (var languageEntry in languageEntries)
                {
                    zipFile.Add(new ZipStaticDataSource(languageContent), languageEntry);
                }
                zipFile.Add(new ZipStaticDataSource(injectContent), InjectedScriptFile);
                zipFile.CommitUpdate();
            }

            if (IsWorkbenchRunning())
            {
                Log.Info("语言资源写入后检测到千牛重新启动，保留sign.json并等待下次安全修复: " + signPath);
                return false;
            }
            ClearSignJson(signPath);
            Log.Info("语言修复写入完成: " + resourcePath);
            return true;
        }

        private static void ClearSignJson(string signPath)
        {
            if (IsWorkbenchRunning())
            {
                Log.Info("千牛资源进程已启动，跳过sign.json清理。");
                return;
            }
            try
            {
                if (!File.Exists(signPath)) return;
                File.WriteAllText(signPath, string.Empty, Encoding.UTF8);
                Log.Info("清 sign.json: " + signPath);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ClearSignJson:" + signPath);
            }
        }

        private static LanguageScanResult DetectLanguageFromProfiles()
        {
            var result = new LanguageScanResult { CurrentLanguage = "zh-CN", IsAbnormal = false };
            foreach (var file in EnumerateLanguageStateFiles())
            {
                var text = ReadSmallFileText(file, 5 * 1024 * 1024);
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var token in AbnormalLanguageTokens)
                {
                    if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.IsAbnormal = true;
                        result.CurrentLanguage = NormalizeLanguageToken(token);
                        result.Source = file;
                        Log.Info("检测到异常语言缓存: " + token + ", file=" + file);
                        return result;
                    }
                }
            }
            return result;
        }

        private static IEnumerable<string> EnumerateLanguageStateFiles()
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in CollectQianniuProfileRoots())
            {
                foreach (var file in EnumerateFilesSafe(root, "Cookies"))
                {
                    if (yielded.Add(file)) yield return file;
                }
                foreach (var dir in FindNamedDirectories(root, new HashSet<string>(new[] { "Local Storage" }, StringComparer.OrdinalIgnoreCase)))
                {
                    foreach (var file in EnumerateFilesSafe(dir, "*"))
                    {
                        if (yielded.Add(file)) yield return file;
                    }
                }
            }
        }

        private static string ReadSmallFileText(string path, int maxBytes)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 1 || fi.Length > maxBytes) return string.Empty;
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeLanguageToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "zh-CN";
            if (token.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0) return "zh_HK";
            if (token.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0) return "zh-TW";
            if (token.IndexOf("traditional", StringComparison.OrdinalIgnoreCase) >= 0) return "traditional";
            return "繁体";
        }

        private static void ClearQianniuLanguageCaches()
        {
            if (IsWorkbenchRunning())
            {
                Log.Info("千牛资源进程仍在运行，跳过语言CEF缓存清理。");
                return;
            }

            // Only disposable Chromium caches are removed. Local Storage may contain
            // authentication/session data, so it is never included in language repair.
            var cacheNames = new HashSet<string>(new[] { "Cache", "Code Cache", "GPUCache" }, StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in CollectQianniuProfileRoots())
            {
                foreach (var dir in FindNamedDirectories(root, cacheNames))
                {
                    targets.Add(dir);
                }
            }

            int cleared = 0;
            int failed = 0;
            foreach (var target in targets.OrderByDescending(p => p.Length))
            {
                if (IsWorkbenchRunning())
                {
                    Log.Info("千牛资源进程已启动，停止后续语言CEF缓存清理。");
                    break;
                }
                try
                {
                    if (!Directory.Exists(target)) continue;
                    Directory.Delete(target, true);
                    cleared++;
                    Log.Info("清理CEF临时缓存: " + target);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Exception(ex, "ClearLanguageCache:" + target);
                }
            }
            Log.Info("清理CEF临时缓存完成: cleared=" + cleared + ", failed=" + failed
                + ", preserved=LocalStorage/Cookies/IndexedDB/Login");
        }

        private static List<string> CollectQianniuProfileRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "AliWorkBench");
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "AliWorkbench");
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AliWorkbench");
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliWorkbench");
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alibaba", "AliWorkbench");
            AddProfileRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alibaba", "AliWorkBench");
            AddCurrentUserCefRoots(roots);
            AddAllUserWorkbenchRoots(roots);
            return roots.Where(Directory.Exists).ToList();
        }

        private static void AddAllUserWorkbenchRoots(HashSet<string> roots)
        {
            try
            {
                var usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users");
                if (!Directory.Exists(usersRoot)) return;
                foreach (var userDir in Directory.GetDirectories(usersRoot))
                {
                    AddProfileRoot(roots, userDir, "AppData", "Roaming", "AliWorkbench");
                    AddProfileRoot(roots, userDir, "AppData", "Local", "AliWorkbench");
                    AddProfileRoot(roots, userDir, "AppData", "Local", "Alibaba", "AliWorkbench");
                    AddProfileRoot(roots, userDir, "AppData", "Local", "Alibaba", "AliWorkBench");
                    var localAppData = Path.Combine(userDir, "AppData", "Local");
                    if (!Directory.Exists(localAppData)) continue;
                    foreach (var cefRoot in Directory.GetDirectories(localAppData, "QNCEF*Temp", SearchOption.TopDirectoryOnly))
                    {
                        if (Directory.Exists(cefRoot)) roots.Add(cefRoot);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddAllUserWorkbenchRoots");
            }
        }

        private static void AddCurrentUserCefRoots(HashSet<string> roots)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData) || !Directory.Exists(localAppData)) return;
                foreach (var path in Directory.GetDirectories(localAppData, "QNCEF*Temp", SearchOption.TopDirectoryOnly))
                {
                    roots.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddCurrentUserCefRoots");
            }
        }

        private static void AddProfileRoot(HashSet<string> roots, string basePath, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return;
            try
            {
                var path = basePath;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                }
                if (Directory.Exists(path)) roots.Add(path);
            }
            catch
            {
            }
        }

        private static IEnumerable<string> FindNamedDirectories(string root, HashSet<string> names)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }
                foreach (var child in children)
                {
                    if (names.Contains(Path.GetFileName(child)))
                    {
                        yield return child;
                    }
                    else
                    {
                        pending.Push(child);
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
            string[] files;
            try
            {
                files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch
            {
                yield break;
            }
            foreach (var file in files)
            {
                yield return file;
            }
        }

        private static List<string> FindInstallPaths()
        {
            var paths = new List<string>();
            AddInstallPath(paths, FindInstallPathFromRegistry());
            foreach (var path in FindInstallPathsFromRunningProcesses())
            {
                AddInstallPath(paths, path);
            }
            AddInstallPath(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench"));
            AddInstallPath(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench"));
            return paths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddInstallPath(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path.Trim().Trim('"'));
                if (Directory.Exists(path) && !paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(path);
                }
            }
            catch
            {
            }
        }

        private static string FindInstallPathFromRegistry()
        {
            try
            {
                using (var registryKey = Registry.ClassesRoot.OpenSubKey(@"aliim\Shell\Open\Command"))
                {
                    if (registryKey == null) return string.Empty;
                    var command = (registryKey.GetValue("") ?? string.Empty).ToString();
                    return InstallPathFromExe(ExtractExePath(command));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "FindInstallPathFromRegistry");
                return string.Empty;
            }
        }

        private static IEnumerable<string> FindInstallPathsFromRunningProcesses()
        {
            foreach (var name in WorkbenchProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    string exePath = string.Empty;
                    try
                    {
                        exePath = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                    }
                    catch
                    {
                    }
                    var installPath = InstallPathFromExe(exePath);
                    if (!string.IsNullOrWhiteSpace(installPath)) yield return installPath;
                }
            }
        }

        private static string ExtractExePath(string command)
        {
            command = (command ?? string.Empty).Trim();
            if (command.Length < 1) return string.Empty;
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : string.Empty;
            }
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? command.Substring(0, idx + 4).Trim() : command;
        }

        private static string InstallPathFromExe(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return string.Empty;
                var file = new FileInfo(exePath);
                if (file.Name.Equals("AliRender.exe", StringComparison.OrdinalIgnoreCase)
                    || file.Name.Equals("wwcmd.exe", StringComparison.OrdinalIgnoreCase)
                    || file.Name.Equals("wangwang.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return file.Directory == null || file.Directory.Parent == null ? string.Empty : file.Directory.Parent.FullName;
                }
                return file.Directory == null ? string.Empty : file.Directory.FullName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> FindResourcePaths(IEnumerable<string> installPaths)
        {
            var paths = new List<string>();
            foreach (var installPath in installPaths)
            {
                AddResourcePathFromIni(paths, installPath);
                AddAllVersionResourcePaths(paths, installPath);
            }
            return paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, WebuiResDir, WebuiFile)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => File.GetLastWriteTime(Path.Combine(p, WebuiResDir, WebuiFile)))
                .ToList();
        }

        private static void AddResourcePathFromIni(List<string> paths, string installPath)
        {
            try
            {
                var iniPath = Path.Combine(installPath, "AliWorkbench.ini");
                if (!File.Exists(iniPath)) return;
                var version = File.ReadAllLines(iniPath)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(version)) return;
                AddResourcePath(paths, Path.Combine(installPath, version.Substring("Version=".Length).Trim(), "Resources"));
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddResourcePathFromIni");
            }
        }

        private static void AddAllVersionResourcePaths(List<string> paths, string installPath)
        {
            try
            {
                if (!Directory.Exists(installPath)) return;
                foreach (var dir in Directory.GetDirectories(installPath))
                {
                    AddResourcePath(paths, Path.Combine(dir, "Resources"));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddAllVersionResourcePaths");
            }
        }

        private static void AddResourcePath(List<string> paths, string resourcePath)
        {
            try
            {
                if (File.Exists(Path.Combine(resourcePath, WebuiResDir, WebuiFile))
                    && !paths.Any(p => string.Equals(p, resourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(resourcePath);
                }
            }
            catch
            {
            }
        }

        private static bool IsWorkbenchRunning()
        {
            foreach (var name in ResourceOwnerProcessNames)
            {
                try
                {
                    if (Process.GetProcessesByName(name).Length > 0) return true;
                }
                catch (Exception ex)
                {
                    // Unknown process state is treated as running: never mutate a possibly live WebView.
                    Log.ErrorWithMaxCount("无法确认千牛资源进程状态，延后语言修复: process=" + name + ", error=" + ex.Message, 5);
                    return true;
                }
            }
            return false;
        }

        private static List<string> GetHtmlEntryNames(ZipFile zipFile)
        {
            return zipFile.Cast<ZipEntry>()
                .Where(entry => !entry.IsDirectory && entry.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Name.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ReadZipEntryText(ZipFile zipFile, string entryName)
        {
            var entry = zipFile.GetEntry(entryName);
            if (entry == null) return null;
            using (var inputStream = zipFile.GetInputStream(entry))
            using (var streamReader = new StreamReader(inputStream, Encoding.UTF8))
            {
                return streamReader.ReadToEnd();
            }
        }

        private static string GetZipDirectory(string entryName)
        {
            var normalized = (entryName ?? string.Empty).Replace('\\', '/');
            var index = normalized.LastIndexOf('/');
            return index < 0 ? string.Empty : normalized.Substring(0, index + 1);
        }

        private static bool ContainsScriptReference(string html, string scriptFileName)
        {
            return !string.IsNullOrWhiteSpace(html)
                && html.IndexOf(scriptFileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string PlaceLanguageScriptFirst(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;
            html = RemoveScriptTags(html, "qnbot-language\\.js");
            return InsertFirstInHead(html, "<script src=\"" + LanguageScriptFileName + "?v=" + LanguageVersionMarker + "\"></script>");
        }

        private static string PlaceInjectedScriptFirst(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;
            html = RemoveScriptTags(html, "qnbot-inject\\.js");
            html = RemoveScriptTags(html, "iseiya\\.taobao\\.com/imsupport");
            html = RemoveScriptTags(html, "5CFB5E11D17E63CDD8CB37B52FA6ACFD\\.js");
            return InsertFirstInHead(html, "<script src=\"" + InjectedScriptSrc + "?v=" + InjectVersionMarker + "\"></script>");
        }

        private static string RemoveScriptTags(string html, string namePattern)
        {
            return Regex.Replace(
                html,
                @"<script\b[^>]*\bsrc\s*=\s*[""'][^""']*" + namePattern + @"[^""']*[""'][^>]*>\s*</script\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static string InsertFirstInHead(string html, string tag)
        {
            var head = Regex.Match(html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
            return head.Success ? html.Insert(head.Index + head.Length, tag) : tag + html;
        }

        private static string ReadScriptPayload(string resourceName, string externalFileName, string requiredMarker, string requiredText)
        {
            try
            {
                using (var stream = typeof(LanguageRepairService).Assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var embeddedContent = reader.ReadToEnd();
                            if (ScriptPayloadMatches(embeddedContent, requiredMarker, requiredText))
                            {
                                return embeddedContent;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ReadLanguageEmbeddedResource:" + resourceName);
            }

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, externalFileName);
            if (!File.Exists(path))
            {
                Log.Info("没有找到语言修复脚本资源: " + path);
                return string.Empty;
            }

            var externalContent = File.ReadAllText(path, Encoding.UTF8);
            if (!ScriptPayloadMatches(externalContent, requiredMarker, requiredText))
            {
                Log.Info("语言修复脚本版本不匹配: " + path + ", marker=" + requiredMarker);
                return string.Empty;
            }
            return externalContent;
        }

        private static bool ScriptPayloadMatches(string content, string requiredMarker, string requiredText)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            if (!string.IsNullOrWhiteSpace(requiredMarker) && content.IndexOf(requiredMarker, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(requiredText) && content.IndexOf(requiredText, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static void BackupWebuiZip(string webuiPath)
        {
            try
            {
                var backupPath = webuiPath + ".bak-qnbot-language-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                if (!File.Exists(backupPath))
                {
                    File.Copy(webuiPath, backupPath);
                    Log.Info("已备份千牛webui.zip: " + backupPath);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "BackupLanguageWebuiZip");
            }
        }

        private static bool ReadPreviousLanguageStateAbnormal()
        {
            try
            {
                if (!File.Exists(StatePath)) return false;
                var text = File.ReadAllText(StatePath, Encoding.UTF8);
                return text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteLanguageState(bool ok, string detail)
        {
            try
            {
                File.WriteAllText(StatePath, (ok ? "OK" : "ABNORMAL") + Environment.NewLine + (detail ?? string.Empty), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "WriteLanguageState");
            }
        }

        private class LanguageScanResult
        {
            public bool IsAbnormal { get; set; }
            public string CurrentLanguage { get; set; }
            public string Source { get; set; }
        }
    }
}
