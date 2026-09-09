using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Common;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Newtonsoft.Json.Linq;

namespace Bot.Update
{
    internal static class PostUpdateQianniuRecovery
    {
        private const string ResultFileName = "last-update-result.json";
        private const string ReportedResultFileName = "last-update-result.reported.json";
        private const string ConsumedFileName = "post-update-qn-recovery.consumed.json";
        private static readonly TimeSpan EvidenceWait = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan EvidenceMaxAge = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan NaturalRecoveryGrace = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan LoginRecoveryTimeout = TimeSpan.FromSeconds(90);

        internal sealed class Result
        {
            internal bool Eligible { get; set; }
            internal bool AttemptedRestart { get; set; }
            internal bool Recovered { get; set; }
            internal string Message { get; set; }
        }

        internal static async Task<Result> TryRecoverAsync(Func<bool> injectionReady)
        {
            var result = new Result { Message = "当前启动不是可确认的更新后恢复场景。" };
            if (injectionReady == null || injectionReady())
            {
                result.Recovered = true;
                result.Message = "千牛注入已经恢复，无需重启千牛。";
                return result;
            }

            var evidence = await WaitForFreshSuccessfulUpdateAsync(injectionReady).ConfigureAwait(false);
            if (evidence == null)
            {
                return result;
            }

            result.Eligible = true;
            if (!TryConsumeEvidence(evidence, out var consumeMessage))
            {
                result.Message = consumeMessage;
                return result;
            }

            Log.Warn("[Bot][PostUpdateQnRecovery] 已确认本次为真实更新后的恢复场景；先给予千牛注入最后的自然恢复宽限。 version=" + evidence.TargetVersion);
            var graceUntil = DateTime.UtcNow + NaturalRecoveryGrace;
            while (DateTime.UtcNow < graceUntil)
            {
                if (injectionReady())
                {
                    result.Recovered = true;
                    result.Message = "更新后千牛注入在重启前自行恢复。";
                    return result;
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }

            var executable = FindQianniuExecutable();
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                result.Message = "已确认更新后恢复场景，但无法定位千牛可执行文件；为避免误启动其它程序，未执行重启。";
                return result;
            }

            result.AttemptedRestart = true;
            try
            {
                Log.Warn("[Bot][PostUpdateQnRecovery] 更新后注入仍未恢复，执行一次受控千牛重启。 exe=" + executable);
                StopQianniuProcesses();
                await Task.Delay(1800).ConfigureAwait(false);
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error("[Bot][PostUpdateQnRecovery] 千牛重启失败：" + ex.Message);
                result.Message = "千牛重启失败：" + ex.Message;
                return result;
            }

            var deadline = DateTime.UtcNow + LoginRecoveryTimeout;
            var lastLoginClick = DateTime.MinValue;
            var lastReceptionClick = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                if (injectionReady())
                {
                    result.Recovered = true;
                    result.Message = "更新后千牛已重启并恢复注入连接。";
                    return result;
                }

                TryConfirmRestorePreviousMessages();

                if ((DateTime.UtcNow - lastLoginClick) >= TimeSpan.FromSeconds(8) && TryClickRememberedAccountLogin())
                {
                    lastLoginClick = DateTime.UtcNow;
                    Log.Info("[Bot][PostUpdateQnRecovery] 已点击千牛历史记住密码账号登录按钮。等待千牛恢复会话。");
                }

                if ((DateTime.UtcNow - lastReceptionClick) >= TimeSpan.FromSeconds(10) && TryOpenReceptionWindow())
                {
                    lastReceptionClick = DateTime.UtcNow;
                    Log.Info("[Bot][PostUpdateQnRecovery] 已尝试打开千牛接待/聊天窗口。");
                }

                await Task.Delay(1500).ConfigureAwait(false);
            }

            TryConfirmRestorePreviousMessages();
            if (injectionReady())
            {
                result.Recovered = true;
                result.Message = "更新后千牛已恢复注入连接。";
                return result;
            }

            result.Message = "已执行一次更新后千牛重启，但在限定时间内仍未恢复注入；本次更新不会再次自动重启千牛。";
            return result;
        }

        private sealed class UpdateEvidence
        {
            internal string TargetVersion { get; set; }
            internal string CreatedAt { get; set; }
            internal string PackageSha256 { get; set; }
            internal string Fingerprint { get; set; }
        }

        private static async Task<UpdateEvidence> WaitForFreshSuccessfulUpdateAsync(Func<bool> injectionReady)
        {
            var deadline = DateTime.UtcNow + EvidenceWait;
            while (DateTime.UtcNow < deadline)
            {
                if (injectionReady()) return null;
                var evidence = TryReadFreshSuccessfulUpdate();
                if (evidence != null) return evidence;
                await Task.Delay(1000).ConfigureAwait(false);
            }
            return TryReadFreshSuccessfulUpdate();
        }

        private static UpdateEvidence TryReadFreshSuccessfulUpdate()
        {
            try
            {
                var root = UpdateStartupHealthService.GetUpdaterRoot();
                var candidates = new[] { Path.Combine(root, ResultFileName), Path.Combine(root, ReportedResultFileName) };
                var currentVersion = NormalizeVersion(BotVersionInfo.GetVersion());
                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    JObject json;
                    try { json = JObject.Parse(File.ReadAllText(path)); }
                    catch { continue; }

                    var status = Convert.ToString(json["status"] ?? string.Empty).Trim();
                    var targetVersion = NormalizeVersion(Convert.ToString(json["target_version"] ?? string.Empty));
                    var createdAtText = Convert.ToString(json["created_at"] ?? string.Empty).Trim();
                    if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(targetVersion) ||
                        !string.Equals(targetVersion, currentVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DateTime.TryParse(createdAtText, out var createdAt)) continue;

                    var createdUtc = createdAt.Kind == DateTimeKind.Utc ? createdAt : createdAt.ToUniversalTime();
                    var age = DateTime.UtcNow - createdUtc;
                    if (age < TimeSpan.FromMinutes(-2) || age > EvidenceMaxAge) continue;

                    var packageSha = Convert.ToString(json["package_sha256"] ?? string.Empty).Trim();
                    return new UpdateEvidence
                    {
                        TargetVersion = targetVersion,
                        CreatedAt = createdAtText,
                        PackageSha256 = packageSha,
                        Fingerprint = targetVersion + "|" + createdAtText + "|" + packageSha
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[Bot][PostUpdateQnRecovery] 读取 updater 成功证据失败：" + ex.Message);
            }
            return null;
        }

        private static bool TryConsumeEvidence(UpdateEvidence evidence, out string message)
        {
            message = string.Empty;
            try
            {
                var path = Path.Combine(UpdateStartupHealthService.GetUpdaterRoot(), ConsumedFileName);
                if (File.Exists(path))
                {
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(path));
                        var fingerprint = Convert.ToString(existing["fingerprint"] ?? string.Empty);
                        if (string.Equals(fingerprint, evidence.Fingerprint, StringComparison.Ordinal))
                        {
                            message = "本次更新后的千牛恢复动作已经执行过；为避免循环重启，不再重复执行。";
                            return false;
                        }
                    }
                    catch
                    {
                        message = "更新后千牛恢复去重标记不可读；为避免循环重启，已禁止自动重启千牛。";
                        return false;
                    }
                }

                var payload = new JObject
                {
                    ["schema_version"] = 1,
                    ["fingerprint"] = evidence.Fingerprint,
                    ["target_version"] = evidence.TargetVersion,
                    ["update_created_at"] = evidence.CreatedAt,
                    ["package_sha256"] = evidence.PackageSha256,
                    ["consumed_at"] = DateTime.Now.ToString("o")
                };
                AtomicWrite(path, payload.ToString());
                return true;
            }
            catch (Exception ex)
            {
                message = "无法持久化更新后千牛恢复去重标记；为避免循环重启，未自动重启千牛：" + ex.Message;
                Log.Warn("[Bot][PostUpdateQnRecovery] " + message);
                return false;
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content);
            try
            {
                if (File.Exists(path))
                {
                    try { File.Replace(temp, path, null); }
                    catch (PlatformNotSupportedException) { File.Delete(path); File.Move(temp, path); }
                }
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string FindQianniuExecutable()
        {
            foreach (var process in SafeGetQianniuProcesses())
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }

            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliWorkbench"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench")
            };
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    var direct = Path.Combine(root, "AliWorkbench.exe");
                    if (File.Exists(direct)) return direct;
                    var nested = Directory.GetFiles(root, "AliWorkbench.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
                catch { }
            }
            return null;
        }

        private static void StopQianniuProcesses()
        {
            foreach (var process in SafeGetQianniuProcesses())
            {
                try { if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow(); }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }

            var gracefulDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                var remaining = SafeGetQianniuProcesses();
                var count = remaining.Length;
                foreach (var process in remaining) { try { process.Dispose(); } catch { } }
                if (count == 0) break;
                Thread.Sleep(250);
            }

            foreach (var process in SafeGetQianniuProcesses())
            {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
        }

        private static Process[] SafeGetQianniuProcesses()
        {
            try { return Process.GetProcessesByName("AliWorkbench"); }
            catch { return new Process[0]; }
        }

        private static bool TryClickRememberedAccountLogin()
        {
            return TryInvokeExactQianniuElement("登录", false);
        }

        private static bool TryConfirmRestorePreviousMessages()
        {
            return TryInvokeExactQianniuElement("确认", true);
        }

        private static bool TryOpenReceptionWindow()
        {
            foreach (var name in new[] { "接待中心", "接待工作台", "旺旺接待", "客服接待" })
            {
                if (TryInvokeExactQianniuElement(name, false)) return true;
            }
            return false;
        }

        private static bool TryInvokeExactQianniuElement(string exactName, bool requirePreviousMessagePrompt)
        {
            try
            {
                var processes = SafeGetQianniuProcesses();
                var pids = new HashSet<int>(processes.Select(p => p.Id));
                foreach (var process in processes) { try { process.Dispose(); } catch { } }
                if (pids.Count == 0) return false;

                using (var automation = new UIA3Automation())
                {
                    var windows = automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window));
                    foreach (var window in windows)
                    {
                        var pid = window.Properties.ProcessId.ValueOrDefault;
                        if (!pids.Contains(pid)) continue;

                        AutomationElement[] descendants;
                        try { descendants = window.FindAllDescendants(); }
                        catch { continue; }

                        if (requirePreviousMessagePrompt)
                        {
                            var hasPrompt = descendants.Any(element => SafeName(element).IndexOf("之前的消息", StringComparison.Ordinal) >= 0);
                            if (!hasPrompt) continue;
                        }

                        var target = descendants.FirstOrDefault(element => string.Equals(SafeName(element), exactName, StringComparison.Ordinal));
                        if (target == null) continue;
                        try { target.AsButton().Invoke(); return true; }
                        catch
                        {
                            try { target.Click(); return true; }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[Bot][PostUpdateQnRecovery] UI 自动恢复动作失败 name=" + exactName + " error=" + ex.Message);
            }
            return false;
        }

        private static string SafeName(AutomationElement element)
        {
            try { return element?.Properties.Name.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string NormalizeVersion(string version)
        {
            var text = (version ?? string.Empty).Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);
            var dash = text.IndexOf('-');
            if (dash >= 0) text = text.Substring(0, dash);
            return text.Trim();
        }
    }

    internal static class QnLanguageStatusReconciler
    {
        private const string LanguageMarkerEntry = "./qn-ai-bot-hans.marker";
        private const string LanguageMarkerValue = "20260713-hans-all-pages-v3";

        internal static void TryReconcile()
        {
            try
            {
                var method = typeof(QNInject).GetMethod("GetActiveResourceZip", BindingFlags.Static | BindingFlags.NonPublic);
                var activeZip = method?.Invoke(null, null) as string;
                if (string.IsNullOrWhiteSpace(activeZip) || !File.Exists(activeZip)) return;

                using (var zip = ZipFile.OpenRead(activeZip))
                {
                    var entry = zip.GetEntry(LanguageMarkerEntry) ?? zip.GetEntry(LanguageMarkerEntry.TrimStart('.', '/'));
                    if (entry == null)
                    {
                        BotConnectionDiagnostics.RecordLanguageStatus(false, "当前千牛语言资源未发现最新简体中文标记，等待安全修复。");
                        return;
                    }
                    using (var reader = new StreamReader(entry.Open()))
                    {
                        var marker = (reader.ReadToEnd() ?? string.Empty).Trim();
                        if (string.Equals(marker, LanguageMarkerValue, StringComparison.Ordinal))
                        {
                            BotConnectionDiagnostics.RecordLanguageStatus(true, "已由当前千牛资源包确认简体中文语言标记为最新。");
                            Log.Info("[Bot][Language] 当前活动千牛资源已确认简体中文标记最新，清除早期“待安全修复”临时状态。");
                        }
                        else
                        {
                            BotConnectionDiagnostics.RecordLanguageStatus(false, "当前千牛语言资源标记不是最新版本，等待安全修复。");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[Bot][Language] 语言状态终态核对失败，保留现有安全状态：" + ex.Message);
            }
        }
    }
}
