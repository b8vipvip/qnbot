using Bot.Options;
using BotLib;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class SlowResponseAnomalyService
    {
        public const int ThresholdSeconds = 15;
        private const int MaxStoredReports = 500;
        private static readonly object FileSync = new object();
        private static readonly ConcurrentDictionary<string, byte> Inflight =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly HttpClient NotifyHttp = CreateNotifyHttpClient();

        public static event Action ReportsChanged;

        public static string ReportDirectory
        {
            get
            {
                var path = Path.Combine(PathEx.DataDir, "diagnostics");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        public static string ReportFilePath
        {
            get { return Path.Combine(ReportDirectory, "slow-response-anomalies.json"); }
        }

        public static void QueueIfSlow(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerStartedAt,
            DateTime answerReadyAt)
        {
            var readyAt = answerReadyAt == DateTime.MinValue ? DateTime.Now : answerReadyAt;
            var detected = detectedAt == DateTime.MinValue ? readyAt : detectedAt;
            var started = answerStartedAt == DateTime.MinValue ? detected : answerStartedAt;
            if (started < detected) started = detected;
            if (readyAt < started) readyAt = DateTime.Now;

            var totalMs = Math.Max(0, (long)(readyAt - detected).TotalMilliseconds);
            if (totalMs <= ThresholdSeconds * 1000L) return;

            var key = BuildInflightKey(seller, buyer, detected, question);
            if (!Inflight.TryAdd(key, 0)) return;

            var report = new SlowResponseAnomalyReport
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Question = Safe(question, 2400),
                Answer = Safe(answer, 2400),
                AnswerSource = Safe(source, 200),
                DetectedAt = detected,
                AnswerStartedAt = started,
                AnswerReadyAt = readyAt,
                TotalMilliseconds = totalMs,
                QueueMilliseconds = Math.Max(0, (long)(started - detected).TotalMilliseconds),
                GenerationMilliseconds = Math.Max(0, (long)(readyAt - started).TotalMilliseconds),
                AnalysisStatus = "等待AI分析",
                Severity = totalMs >= 60000 ? "严重" : (totalMs >= 30000 ? "高" : "中"),
                Summary = "检测到从收到买家消息到获取答案超过15秒，已自动进入AI异常分析。",
                NotificationStatus = "等待企业微信通知"
            };

            SaveOrUpdate(report);
            Log.Error("[慢响应异常] seller=" + report.Seller
                + ", buyer=" + report.Buyer
                + ", totalMs=" + report.TotalMilliseconds
                + ", queueMs=" + report.QueueMilliseconds
                + ", generationMs=" + report.GenerationMilliseconds
                + ", source=" + report.AnswerSource
                + ", reportId=" + report.Id);

            Task.Run(async () =>
            {
                try
                {
                    await AnalyzeAndNotifyAsync(report).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    report.AnalysisStatus = "异常分析任务失败";
                    report.Summary = "慢响应异常已记录，但自动分析任务执行失败：" + Safe(ex.Message, 500);
                    report.NotificationStatus = "未发送：分析任务异常";
                    SaveOrUpdate(report);
                    Log.Exception(ex);
                }
                finally
                {
                    byte ignored;
                    Inflight.TryRemove(key, out ignored);
                }
            });
        }

        public static List<SlowResponseAnomalyReport> GetReports(int maxCount)
        {
            lock (FileSync)
            {
                return LoadReportsUnsafe()
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(maxCount <= 0 ? 200 : maxCount)
                    .ToList();
            }
        }

        public static void ClearReports()
        {
            lock (FileSync)
            {
                try
                {
                    if (File.Exists(ReportFilePath)) File.Delete(ReportFilePath);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                    throw;
                }
            }
            RaiseReportsChanged();
        }

        public static string FormatReport(SlowResponseAnomalyReport report)
        {
            if (report == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("异常报告ID：" + report.Id);
            sb.AppendLine("记录时间：" + FormatTime(report.CreatedAt));
            sb.AppendLine("客服：" + report.Seller);
            sb.AppendLine("买家：" + report.Buyer);
            sb.AppendLine("答案来源：" + report.AnswerSource);
            sb.AppendLine("总耗时：" + report.TotalSeconds.ToString("0.000") + " 秒");
            sb.AppendLine("消息聚合/排队：" + report.QueueSeconds.ToString("0.000") + " 秒");
            sb.AppendLine("答案生成：" + report.GenerationSeconds.ToString("0.000") + " 秒");
            sb.AppendLine("收到消息：" + FormatTime(report.DetectedAt));
            sb.AppendLine("开始获取答案：" + FormatTime(report.AnswerStartedAt));
            sb.AppendLine("答案就绪：" + FormatTime(report.AnswerReadyAt));
            sb.AppendLine("严重程度：" + report.Severity);
            sb.AppendLine("分析状态：" + report.AnalysisStatus);
            sb.AppendLine("企业微信：" + report.NotificationStatus);
            sb.AppendLine();
            sb.AppendLine("买家问题：");
            sb.AppendLine(report.Question);
            sb.AppendLine();
            sb.AppendLine("最终答案：");
            sb.AppendLine(report.Answer);
            sb.AppendLine();
            sb.AppendLine("AI分析摘要：");
            sb.AppendLine(report.Summary);
            sb.AppendLine();
            sb.AppendLine("可能原因：");
            sb.AppendLine(report.LikelyCause);
            sb.AppendLine();
            sb.AppendLine("分析依据：");
            sb.AppendLine(report.Evidence);
            sb.AppendLine();
            sb.AppendLine("改进建议：");
            sb.AppendLine(report.Recommendations);
            if (!string.IsNullOrWhiteSpace(report.RawAnalysis))
            {
                sb.AppendLine();
                sb.AppendLine("AI原始分析：");
                sb.AppendLine(report.RawAnalysis);
            }
            return sb.ToString();
        }

        private static async Task AnalyzeAndNotifyAsync(SlowResponseAnomalyReport report)
        {
            report.AnalysisStatus = "AI分析中";
            SaveOrUpdate(report);

            StructuredChatResult result;
            try
            {
                result = MyOpenAI.CallStructuredChat(
                    new JArray
                    {
                        new JObject
                        {
                            ["role"] = "system",
                            ["content"] = "你是桌面端AI客服系统的性能故障分析器。分析一次超过15秒的慢响应。严格基于给出的时间数据和日志，不要编造。只输出JSON对象，字段 severity、summary、likely_cause、evidence、recommendations。recommendations可以是字符串或字符串数组。"
                        },
                        new JObject
                        {
                            ["role"] = "user",
                            ["content"] = BuildAnalysisPrompt(report, ReadRecentLogTail(16000))
                        }
                    },
                    1000,
                    0.1,
                    25,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new StructuredChatResult { Success = false, Error = ex.Message };
            }

            if (result != null && result.Success && !string.IsNullOrWhiteSpace(result.Answer))
            {
                ApplyAiAnalysis(report, result.Answer);
                report.AnalysisStatus = "AI分析完成";
            }
            else
            {
                ApplyFallbackAnalysis(report, result == null ? "AI分析未返回结果" : result.Error);
                report.AnalysisStatus = "AI分析失败，已生成基础诊断";
            }

            SaveOrUpdate(report);
            Log.Error("[慢响应异常报告]\r\n" + FormatReport(report));
            report.NotificationStatus = await NotifyEnterpriseWeChatAsync(report).ConfigureAwait(false);
            SaveOrUpdate(report);
            Log.Info("慢响应异常企业微信通知结果：reportId=" + report.Id + ", result=" + report.NotificationStatus);
        }

        private static string BuildAnalysisPrompt(SlowResponseAnomalyReport report, string logExcerpt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请分析下面这次慢响应并给出可执行的根因判断。若证据不足，请明确写证据不足。");
            sb.AppendLine("总耗时(ms)：" + report.TotalMilliseconds);
            sb.AppendLine("消息聚合/排队耗时(ms)：" + report.QueueMilliseconds);
            sb.AppendLine("答案生成耗时(ms)：" + report.GenerationMilliseconds);
            sb.AppendLine("答案来源：" + report.AnswerSource);
            sb.AppendLine("收到时间：" + FormatTime(report.DetectedAt));
            sb.AppendLine("开始获取答案：" + FormatTime(report.AnswerStartedAt));
            sb.AppendLine("答案就绪：" + FormatTime(report.AnswerReadyAt));
            sb.AppendLine("买家问题：" + Safe(report.Question, 1800));
            sb.AppendLine("最终答案：" + Safe(report.Answer, 1800));
            sb.AppendLine();
            sb.AppendLine("最近运行日志尾部：");
            sb.AppendLine(Safe(logExcerpt, 14000));
            sb.AppendLine();
            sb.AppendLine("优先判断耗时发生在消息聚合/排队、知识库处理、AI接口/统一网关、模型生成、重试/超时、网络或其他阶段，并引用具体证据。不要把AI分析本身的耗时算入原始响应耗时。");
            return sb.ToString();
        }

        private static void ApplyAiAnalysis(SlowResponseAnomalyReport report, string answer)
        {
            report.RawAnalysis = Safe(answer, 8000);
            JObject obj;
            if (StructuredJsonObjectRecovery.TryRecoverObject(answer, out obj) && obj != null)
            {
                report.Severity = TokenText(obj["severity"], report.Severity, 80);
                report.Summary = TokenText(obj["summary"], report.Summary, 1200);
                report.LikelyCause = TokenText(obj["likely_cause"], "AI未明确给出根因。", 1600);
                report.Evidence = TokenText(obj["evidence"], "AI未明确给出证据。", 2200);
                report.Recommendations = TokenText(obj["recommendations"], "请结合日志继续人工排查。", 2200);
                return;
            }

            report.Summary = Safe(answer, 1200);
            report.LikelyCause = BuildTimingBasedCause(report);
            report.Evidence = BuildTimingEvidence(report);
            report.Recommendations = "AI返回内容无法恢复为结构化JSON，建议结合原始分析和运行日志进一步核查。";
        }

        private static void ApplyFallbackAnalysis(SlowResponseAnomalyReport report, string error)
        {
            report.Summary = "自动AI分析调用失败，但系统已根据阶段耗时生成基础诊断。AI错误：" + Safe(error, 500);
            report.LikelyCause = BuildTimingBasedCause(report);
            report.Evidence = BuildTimingEvidence(report);
            report.Recommendations = report.GenerationMilliseconds >= report.QueueMilliseconds
                ? "优先检查统一API服务、上游模型延迟、网络连接、接口重试和超时日志；确认是否存在慢模型或故障切换。"
                : "优先检查消息聚合等待、同买家并发任务排队和前序任务阻塞；确认是否有连续消息导致等待窗口被延长。";
            report.RawAnalysis = string.Empty;
        }

        private static string BuildTimingBasedCause(SlowResponseAnomalyReport report)
        {
            return report.GenerationMilliseconds >= report.QueueMilliseconds
                ? "答案生成阶段占主要耗时，可能与AI接口、统一网关、模型生成、网络或接口重试有关。"
                : "消息聚合或进入答案生成前的排队阶段占主要耗时，可能存在连续消息等待或同会话任务阻塞。";
        }

        private static string BuildTimingEvidence(SlowResponseAnomalyReport report)
        {
            return "总耗时 " + report.TotalMilliseconds + "ms；消息聚合/排队 "
                + report.QueueMilliseconds + "ms；答案生成 " + report.GenerationMilliseconds + "ms。";
        }

        private static async Task<string> NotifyEnterpriseWeChatAsync(SlowResponseAnomalyReport report)
        {
            var results = new List<string>();
            try
            {
                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (cfg != null && cfg.NotifyWeChat && !string.IsNullOrWhiteSpace(cfg.WeChatWebhook))
                {
                    results.Add("企业微信Webhook=" + await PostWeComWebhookAsync(cfg.WeChatWebhook, BuildNotificationText(report)).ConfigureAwait(false));
                }
            }
            catch (Exception ex)
            {
                results.Add("企业微信Webhook=失败：" + Safe(ex.Message, 200));
            }

            try
            {
                if (WeComAppBridgeClient.IsConfigured())
                {
                    var decision = new AutoReplyRuleDecision
                    {
                        Matched = true,
                        AllowAutoReply = false,
                        UseAiReply = false,
                        IsOffHours = false,
                        Reason = "Bot慢响应异常：" + Safe(report.Summary, 500),
                        ReplyText = string.Empty,
                        WorkHoursText = string.Empty
                    };
                    var appResult = await WeComAppBridgeClient.SendNotificationAsync(
                        report.Seller,
                        report.Buyer,
                        BuildNotificationText(report),
                        decision,
                        false).ConfigureAwait(false);
                    results.Add("企业微信应用消息=" + appResult);
                }
            }
            catch (Exception ex)
            {
                results.Add("企业微信应用消息=失败：" + Safe(ex.Message, 200));
            }
            return results.Count == 0 ? "未配置企业微信通知通道" : string.Join("；", results);
        }

        private static string BuildNotificationText(SlowResponseAnomalyReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【千牛Bot慢响应异常报告】");
            sb.AppendLine("时间：" + FormatTime(report.CreatedAt));
            sb.AppendLine("客服：" + Safe(report.Seller, 80));
            sb.AppendLine("买家：" + Safe(report.Buyer, 80));
            sb.AppendLine("总耗时：" + report.TotalSeconds.ToString("0.0") + "秒");
            sb.AppendLine("排队/聚合：" + report.QueueSeconds.ToString("0.0") + "秒");
            sb.AppendLine("答案生成：" + report.GenerationSeconds.ToString("0.0") + "秒");
            sb.AppendLine("答案来源：" + Safe(report.AnswerSource, 120));
            sb.AppendLine("严重程度：" + Safe(report.Severity, 80));
            sb.AppendLine("AI分析：" + Safe(report.Summary, 700));
            sb.AppendLine("可能原因：" + Safe(report.LikelyCause, 700));
            sb.AppendLine("建议：" + Safe(report.Recommendations, 900));
            sb.AppendLine("报告ID：" + report.Id);
            return sb.ToString();
        }

        private static async Task<string> PostWeComWebhookAsync(string url, string message)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "未配置有效Webhook";
            }
            try
            {
                var payload = new JObject
                {
                    ["msgtype"] = "text",
                    ["text"] = new JObject { ["content"] = message }
                };
                using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (var response = await NotifyHttp.PostAsync(uri, content).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return response.IsSuccessStatusCode
                        ? "成功"
                        : "HTTP " + (int)response.StatusCode + " " + Safe(body, 200);
                }
            }
            catch (Exception ex)
            {
                return "失败：" + Safe(ex.Message, 200);
            }
        }

        private static HttpClient CreateNotifyHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }

        // Kept private with this exact name/signature because SendFailureAnomalyService deliberately
        // reuses the same persistent report store through reflection.
        private static void SaveOrUpdate(SlowResponseAnomalyReport report)
        {
            lock (FileSync)
            {
                List<SlowResponseAnomalyReport> reports;
                if (!TryLoadReportsUnsafe(out reports))
                {
                    Log.Info("慢响应异常报告读取失败，本次更新已跳过以保护现有历史文件。");
                    return;
                }
                var index = reports.FindIndex(x => string.Equals(x.Id, report.Id, StringComparison.Ordinal));
                if (index >= 0) reports[index] = report;
                else reports.Add(report);
                reports = reports.OrderByDescending(x => x.CreatedAt).Take(MaxStoredReports).ToList();
                SaveReportsUnsafe(reports);
            }
            RaiseReportsChanged();
        }

        private static List<SlowResponseAnomalyReport> LoadReportsUnsafe()
        {
            List<SlowResponseAnomalyReport> reports;
            return TryLoadReportsUnsafe(out reports)
                ? reports
                : new List<SlowResponseAnomalyReport>();
        }

        private static bool TryLoadReportsUnsafe(out List<SlowResponseAnomalyReport> reports)
        {
            reports = new List<SlowResponseAnomalyReport>();
            if (!File.Exists(ReportFilePath)) return true;
            try
            {
                var text = File.ReadAllText(ReportFilePath, Encoding.UTF8);
                var loaded = JsonConvert.DeserializeObject<List<SlowResponseAnomalyReport>>(text);
                if (loaded == null) throw new InvalidDataException("异常报告文件不是有效的 JSON 数组。");
                reports = loaded;
                return true;
            }
            catch (Exception ex)
            {
                reports = null;
                Log.Info("读取慢响应异常报告失败：" + Safe(ex.Message, 300));
                return false;
            }
        }

        private static void SaveReportsUnsafe(List<SlowResponseAnomalyReport> reports)
        {
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                ReportFilePath, JsonConvert.SerializeObject(reports, Formatting.Indented), 3, 50, out error))
                throw new IOException("慢响应异常报告原子保存失败：" + error);
        }

        private static void RaiseReportsChanged()
        {
            var handler = ReportsChanged;
            if (handler == null) return;
            try { handler(); } catch { }
        }

        private static string ReadRecentLogTail(int maxChars)
        {
            try
            {
                Log.Flush();
                var file = Log.CurrentFileName;
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return "未找到当前运行日志文件。";
                foreach (var encoding in new[] { Encoding.GetEncoding(936), Encoding.UTF8, Encoding.Default })
                {
                    try
                    {
                        var text = File.ReadAllText(file, encoding);
                        if (text.Length > maxChars) text = text.Substring(text.Length - maxChars);
                        return text;
                    }
                    catch { }
                }
                return "当前日志文件无法读取。";
            }
            catch (Exception ex)
            {
                return "读取日志尾部失败：" + ex.Message;
            }
        }

        private static string TokenText(JToken token, string fallback, int max)
        {
            if (token == null) return fallback ?? string.Empty;
            var value = token.Type == JTokenType.Array
                ? string.Join("\n", token.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "- " + x.Trim()))
                : token.ToString();
            value = Safe(value, max);
            return string.IsNullOrWhiteSpace(value) ? (fallback ?? string.Empty) : value;
        }

        private static string BuildInflightKey(string seller, string buyer, DateTime detectedAt, string question)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim() + "#"
                + detectedAt.Ticks + "#" + (question ?? string.Empty).GetHashCode();
        }

        private static string Safe(string value, int max)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            value = Regex.Replace(value, @"(?i)(bearer\s+)[a-z0-9._~+/=-]{12,}", "$1[TOKEN]");
            if (value.Length > max) value = value.Substring(0, max) + "...";
            return value.Trim();
        }

        private static string FormatTime(DateTime time)
        {
            return time == DateTime.MinValue ? "未知" : time.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }
    }

    public class SlowResponseAnomalyReport
    {
        public string Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string AnswerSource { get; set; }
        public DateTime DetectedAt { get; set; }
        public DateTime AnswerStartedAt { get; set; }
        public DateTime AnswerReadyAt { get; set; }
        public long TotalMilliseconds { get; set; }
        public long QueueMilliseconds { get; set; }
        public long GenerationMilliseconds { get; set; }
        public string Severity { get; set; }
        public string AnalysisStatus { get; set; }
        public string Summary { get; set; }
        public string LikelyCause { get; set; }
        public string Evidence { get; set; }
        public string Recommendations { get; set; }
        public string RawAnalysis { get; set; }
        public string NotificationStatus { get; set; }

        [JsonIgnore]
        public double TotalSeconds { get { return TotalMilliseconds / 1000.0; } }
        [JsonIgnore]
        public double QueueSeconds { get { return QueueMilliseconds / 1000.0; } }
        [JsonIgnore]
        public double GenerationSeconds { get { return GenerationMilliseconds / 1000.0; } }
        [JsonIgnore]
        public string CreatedAtText { get { return CreatedAt == DateTime.MinValue ? string.Empty : CreatedAt.ToString("MM-dd HH:mm:ss"); } }

        public SlowResponseAnomalyReport()
        {
            Id = string.Empty;
            Seller = string.Empty;
            Buyer = string.Empty;
            Question = string.Empty;
            Answer = string.Empty;
            AnswerSource = string.Empty;
            Severity = string.Empty;
            AnalysisStatus = string.Empty;
            Summary = string.Empty;
            LikelyCause = string.Empty;
            Evidence = string.Empty;
            Recommendations = string.Empty;
            RawAnalysis = string.Empty;
            NotificationStatus = string.Empty;
        }
    }
}
