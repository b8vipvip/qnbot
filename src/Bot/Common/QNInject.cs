using IniParser.Model;
using IniParser;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using BotLib;
using System.Windows;

namespace Bot.Common
{
    // zh-CN-persistent
// language repair enabled

public class QNInject
    {
        private static readonly string[] workbenchProcessNames = { "AliWorkbench", "new_AliWorkbench", "AliRender", "wwcmd", "wangwang" };
        private const string webuiResDir = "newWebui";
        private const string webuiFile = "webui.zip";
        private const string signFile = "sign.json";
        private const string chatRecentHtmlFile = "web_chat-packer/recent.html";
        private const string injectedScriptFile = "web_chat-packer/qnbot-inject.js";
        private const string injectedScriptSrc = "qnbot-inject.js";
        private const string languageScriptFileName = "qnbot-language.js";
        private const string embeddedInjectResource = "Bot.Resources.inject.js";
        private const string embeddedLanguageResource = "Bot.Resources.language.js";
        private const string injectVersionMarker = "20260906-zh-cn-ws-retire-v10";
        private const string languageVersionMarker = "20260713-hans-all-pages-v3";
        private const string injectedScriptVersionedSrc = injectedScriptSrc + "?v=" + injectVersionMarker;
        private const string languageScriptVersionedSrc = languageScriptFileName + "?v=" + languageVersionMarker;
        // 注入负载随 Bot.exe 编译，外部 JS 只在开发环境中作为同版本兜底，避免 EXE 与脚本版本漂移。
        private const string oldRemoteOverwriteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js";

        public static async Task StartInject()
        {
            try
            {
                var installPaths = FindInstallPaths();
                if (installPaths.Count < 1)
                {
                    MessageBox.Show("没有检测到安装的千牛!!");
                    return;
                }

                var resourcePaths = FindResourcePaths(installPaths);
                if (resourcePaths.Count < 1)
                {
                    MessageBox.Show("获取千牛资源目录失败!!");
                    return;
                }

                var needInjectPaths = resourcePaths.Where(p => !IsInjected(p)).ToList();
                Log.Info("千牛注入检测: marker=" + injectVersionMarker + ", languageMarker=" + languageVersionMarker + ", resources=" + resourcePaths.Count + ", needInject=" + needInjectPaths.Count);
                if (needInjectPaths.Count < 1)
                {
                    Log.Info("千牛注入已是最新版本: " + injectVersionMarker);
                    return;
                }

                if (IsWorkbenchRunning())
                {
                    if (MessageBox.Show("检测到千牛正在运行。需要先退出千牛后注入插件，是否现在关闭千牛并继续？", "提示", MessageBoxButton.YesNo)
                        == MessageBoxResult.No)
                    {
                        return;
                    }
                    else
                    {
                        KillWorkbenchProcesses();
                    }
                    await Task.Delay(3000);
                }

                int success = 0;
                foreach (var resourcePath in needInjectPaths)
                {
                    try
                    {
                        if (InjectScript(resourcePath))
                        {
                            success++;
                            Log.Info("千牛插件注入成功: marker=" + injectVersionMarker + ", resourcePath=" + resourcePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "InjectScript:" + resourcePath);
                    }
                }

                // webui.zip can be updated successfully while Chromium continues to serve the
                // previous HTML/JS from disk. Clear only disposable web caches after a real
                // version upgrade. Keep Local Storage/IndexedDB/cookies so the user stays signed in.
                if (success > 0)
                {
                    ClearQianniuWebCaches();
                }

                if (success == needInjectPaths.Count)
                {
                    MessageBox.Show("千牛插件注入成功，请重新启动千牛!!");
                }
                else if (success > 0)
                {
                    MessageBox.Show("千牛插件部分注入成功，请重新启动千牛后检查连接状态。");
                }
                else
                {
                    MessageBox.Show("千牛插件注入失败!!");
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static string FindInstallPath()
        {
            return FindInstallPaths().FirstOrDefault();
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
            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
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
                    var exePath = ExtractExePath(command);
                    return InstallPathFromExe(exePath);
                }
            }
            catch (Exception e)
            {
                Log.Exception(e, "installPath");
                return string.Empty;
            }
        }

        private static IEnumerable<string> FindInstallPathsFromRunningProcesses()
        {
            foreach (var name in workbenchProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    string path = string.Empty;
                    try
                    {
                        path = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                    }
                    catch
                    {
                    }
                    var installPath = InstallPathFromExe(path);
                    if (!string.IsNullOrWhiteSpace(installPath))
                    {
                        yield return installPath;
                    }
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
                var name = file.Name;
                if (name.Equals("AliRender.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return file.Directory == null || file.Directory.Parent == null ? string.Empty : file.Directory.Parent.FullName;
                }
                if (name.Equals("wwcmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("wangwang.exe", StringComparison.OrdinalIgnoreCase))
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
            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, webuiResDir, webuiFile)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => File.GetLastWriteTime(Path.Combine(p, webuiResDir, webuiFile)))
                .ToList();
        }

        private static string FindResourcePath(string installPath)
        {
            return FindResourcePaths(new[] { installPath }).FirstOrDefault() ?? string.Empty;
        }

        private static void AddResourcePathFromIni(List<string> paths, string installPath)
        {
            try
            {
                var aliWorkbenchConfigPath = Path.Combine(installPath, "AliWorkbench.ini");
                if (!File.Exists(aliWorkbenchConfigPath)) return;
                var version = ReadAliConfigFile(aliWorkbenchConfigPath);
                AddResourcePath(paths, Path.Combine(installPath, version, "Resources"));
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
                if (File.Exists(Path.Combine(resourcePath, webuiResDir, webuiFile))
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
            return workbenchProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);
        }

        private static void KillWorkbenchProcesses()
        {
            foreach (var name in workbenchProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                }
            }
        }

        private static void ClearQianniuWebCaches()
        {
            var cacheNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Cache",
                "Code Cache",
                "GPUCache",
                "DawnCache",
                "GrShaderCache",
                "GraphiteDawnCache",
                "Service Worker",
                "blob_storage"
            };
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "AliWorkBench");
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "AliWorkbench");
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AliWorkbench");
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliWorkbench");
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alibaba", "AliWorkbench");
            AddCacheRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alibaba", "AliWorkBench");
            AddQianniuCefCacheRoots(roots);

            Log.Info("千牛网页缓存扫描目录: " + string.Join(" | ", roots.OrderBy(p => p)));

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var path in FindCacheDirectories(root, cacheNames))
                {
                    targets.Add(path);
                }
            }

            int cleared = 0;
            int failed = 0;
            foreach (var path in targets.OrderByDescending(p => p.Length))
            {
                try
                {
                    if (!Directory.Exists(path)) continue;
                    Directory.Delete(path, true);
                    cleared++;
                    Log.Info("已清理千牛网页缓存: " + path);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Exception(ex, "ClearQianniuWebCache:" + path);
                }
            }
            Log.Info("千牛网页缓存清理完成: cleared=" + cleared + ", failed=" + failed + ", preserved=Local Storage/IndexedDB/cookies");
        }

        private static void AddCacheRoot(HashSet<string> roots, string basePath, params string[] parts)
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

        private static void AddQianniuCefCacheRoots(HashSet<string> roots)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData) || !Directory.Exists(localAppData)) return;

                // Qianniu 9.97+ launches AliRender with --cef_dir/--user-data-dir under
                // %LOCALAPPDATA%\QNCEF<version>Temp\instance_*. These are the active Chromium
                // profiles used by bench_home/bench_im. Previous cache cleanup only scanned
                // AliWorkbench folders, so it found zero targets and stale remote UI bundles
                // survived a successful webui.zip injection.
                foreach (var path in Directory.GetDirectories(localAppData, "QNCEF*Temp", SearchOption.TopDirectoryOnly))
                {
                    if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "FindQianniuCefCacheRoots");
            }
        }

        private static IEnumerable<string> FindCacheDirectories(string root, HashSet<string> cacheNames)
        {
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
                catch (Exception ex)
                {
                    Log.Exception(ex, "ScanQianniuWebCache:" + current);
                    continue;
                }

                foreach (var child in children)
                {
                    if (cacheNames.Contains(Path.GetFileName(child)))
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

        public static bool IsInjected(string resourcePath)
        {
            try
            {
                var webuiResPath = Path.Combine(resourcePath, webuiResDir, webuiFile);
                if (!File.Exists(webuiResPath)) return false;
                using (var zipFile = new ZipFile(webuiResPath))
                {
                    var recentContent = ReadZipEntryText(zipFile, chatRecentHtmlFile);
                    if (string.IsNullOrWhiteSpace(recentContent)
                        || !ContainsScriptReference(recentContent, injectedScriptSrc)
                        || recentContent.Contains(oldRemoteOverwriteUrl)) return false;

                    var injectContent = ReadZipEntryText(zipFile, injectedScriptFile);
                    if (string.IsNullOrWhiteSpace(injectContent) || !injectContent.Contains(injectVersionMarker)) return false;

                    var htmlEntries = GetHtmlEntryNames(zipFile);
                    if (htmlEntries.Count < 1) return false;
                    var checkedLanguageEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var htmlEntryName in htmlEntries)
                    {
                        var html = ReadZipEntryText(zipFile, htmlEntryName);
                        if (string.IsNullOrWhiteSpace(html) || !ContainsScriptReference(html, languageScriptFileName)) return false;

                        var languageEntryName = GetZipDirectory(htmlEntryName) + languageScriptFileName;
                        if (!checkedLanguageEntries.Add(languageEntryName)) continue;
                        var languageContent = ReadZipEntryText(zipFile, languageEntryName);
                        if (string.IsNullOrWhiteSpace(languageContent) || !languageContent.Contains(languageVersionMarker)) return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "IsInjected:" + resourcePath);
                return false;
            }
        }

        private static string ReadScriptPayload(string resourceName, string externalFileName, string requiredMarker)
        {
            try
            {
                using (var stream = typeof(QNInject).Assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var embeddedContent = reader.ReadToEnd();
                            if (embeddedContent.Contains(requiredMarker))
                            {
                                Log.Info("使用Bot.exe内置注入资源: " + resourceName + ", marker=" + requiredMarker);
                                return embeddedContent;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ReadEmbeddedResource:" + resourceName);
            }

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, externalFileName);
            if (!File.Exists(path))
            {
                Log.Error("没有找到内置或本地注入资源: " + resourceName + ", " + path);
                return string.Empty;
            }
            var externalContent = File.ReadAllText(path, Encoding.UTF8);
            if (!externalContent.Contains(requiredMarker))
            {
                Log.Error("本地注入资源版本不匹配: " + path + ", requiredMarker=" + requiredMarker);
                return string.Empty;
            }
            Log.Info("使用本地注入资源兜底: " + path + ", marker=" + requiredMarker);
            return externalContent;
        }

        private static bool InjectScript(string resourcePath)
        {
            var webuiResPath = Path.Combine(resourcePath, webuiResDir, webuiFile);
            var signPath = Path.Combine(resourcePath, webuiResDir, signFile);
            var injectJsContent = ReadScriptPayload(embeddedInjectResource, "inject.js", injectVersionMarker);
            var languageJsContent = ReadScriptPayload(embeddedLanguageResource, "language.js", languageVersionMarker);
            if (string.IsNullOrWhiteSpace(injectJsContent) || string.IsNullOrWhiteSpace(languageJsContent)) return false;

            BackupWebuiZip(webuiResPath);
            using (var zipFile = new ZipFile(webuiResPath))
            {
                var htmlEntryNames = GetHtmlEntryNames(zipFile);
                if (!htmlEntryNames.Any(name => string.Equals(name, chatRecentHtmlFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Error("webui.zip中没有找到 " + chatRecentHtmlFile + ": " + webuiResPath);
                    return false;
                }

                var patchedHtml = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var languageEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var htmlEntryName in htmlEntryNames)
                {
                    var html = ReadZipEntryText(zipFile, htmlEntryName);
                    if (html == null) continue;
                    if (string.Equals(htmlEntryName, chatRecentHtmlFile, StringComparison.OrdinalIgnoreCase))
                    {
                        html = PlaceInjectedScriptFirst(html);
                    }
                    html = PlaceLanguageScriptFirst(html);
                    patchedHtml[htmlEntryName] = html;
                    languageEntryNames.Add(GetZipDirectory(htmlEntryName) + languageScriptFileName);
                }

                zipFile.BeginUpdate();
                foreach (var item in patchedHtml)
                {
                    zipFile.Add(new ZipStaticDataSource(item.Value), item.Key);
                }
                foreach (var languageEntryName in languageEntryNames)
                {
                    zipFile.Add(new ZipStaticDataSource(languageJsContent), languageEntryName);
                }
                zipFile.Add(new ZipStaticDataSource(injectJsContent), injectedScriptFile);
                zipFile.CommitUpdate();
                Log.Info("千牛简体中文脚本覆盖HTML入口: " + patchedHtml.Count + ", languageFiles=" + languageEntryNames.Count);

                if (File.Exists(signPath))
                {
                    var signFi = new FileInfo(signPath);
                    if (signFi.Length > 0)
                    {
                        signFi.Delete();
                        File.Create(signPath).Dispose();
                    }
                }
                return true;
            }
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
            html = Regex.Replace(
                html,
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*qnbot-language\.js[^\""']*[\""'][^>]*>\s*</script\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return InsertFirstInHead(html, "<script src=\"" + languageScriptVersionedSrc + "\"></script>");
        }

        private static string InsertFirstInHead(string html, string tag)
        {
            var head = Regex.Match(html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
            if (head.Success)
            {
                return html.Insert(head.Index + head.Length, tag);
            }
            return tag + html;
        }

        private static string PlaceInjectedScriptFirst(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;

            // Locale must be selected before Qianniu's application bundles start.
            // Remove old/late injection tags and place our bootstrap first in <head>.
            var scriptPatterns = new[]
            {
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*qnbot-inject\.js[^\""']*[\""'][^>]*>\s*</script\s*>",
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*iseiya\.taobao\.com/imsupport[^\""']*[\""'][^>]*>\s*</script\s*>",
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*5CFB5E11D17E63CDD8CB37B52FA6ACFD\.js[^\""']*[\""'][^>]*>\s*</script\s*>"
            };
            foreach (var pattern in scriptPatterns)
            {
                html = Regex.Replace(html, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            return InsertFirstInHead(html, "<script src=\"" + injectedScriptVersionedSrc + "\"></script>");
        }

        private static void BackupWebuiZip(string webuiResPath)
        {
            try
            {
                var backupPath = webuiResPath + ".bak-qnbot-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                if (!File.Exists(backupPath))
                {
                    File.Copy(webuiResPath, backupPath);
                    Log.Info("已备份千牛webui.zip: " + backupPath);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "BackupWebuiZip");
            }
        }

        private static string ReadAliConfigFile(string path)
        {
            var parser = new FileIniDataParser();
            var data = parser.ReadFile(path);
            string version = data["Common"]["Version"];
            return version;
        }
    }

    public class WorkbenchResult
    {
        public WorkbenchStatus Status { get; set; }
        public string Message { get; set; }
    }

    public enum WorkbenchStatus
    {
        NotInstalled,
        ResourceNotFound,
        Running,
        Success,
        Failed
    }

    public class ZipStaticDataSource : IStaticDataSource
    {
        private string _content;
        public ZipStaticDataSource(string content)
        {
            _content = content;
        }

        public Stream GetSource()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_content);
            return new MemoryStream(bytes);
        }
    }
}

