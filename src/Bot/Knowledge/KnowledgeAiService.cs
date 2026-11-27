using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;

namespace Bot.Knowledge
{
    public class KnowledgeImportResult
    {
        public int TextChars { get; set; }
        public int ImageCount { get; set; }
        public int VideoSkipped { get; set; }
        public int AiGenerated { get; set; }
        public int Added { get; set; }
        public int DuplicateSkipped { get; set; }
        public int NewTypeCount { get; set; }
        public int UnsupportedImageSkipped { get; set; }
        public List<KnowledgeV2Record> AddedItems { get; set; }

        public KnowledgeImportResult()
        {
            AddedItems = new List<KnowledgeV2Record>();
        }
    }

    public enum SmartImportCancelSource
    {
        None,
        UserCancel,
        Timeout,
        WindowClosed,
        ReplacedByNewTask
    }

    public class SmartImportException : Exception
    {
        public new SmartImportCancelSource Source { get; private set; }
        public int BatchIndex { get; private set; }

        public SmartImportException(string message, SmartImportCancelSource source, int batchIndex)
            : base(message)
        {
            Source = source;
            BatchIndex = batchIndex;
        }
    }

    public class SmartImportProgress
    {
        public int BatchIndex;
        public int BatchCount;
        public int BatchChars;
        public int Added;
        public int DuplicateSkipped;
        public int TimeoutSeconds;
        public long ElapsedMs;
        public string EndpointName;

        public override string ToString()
        {
            return string.Format(
                "正在分析第 {0}/{1} 批\n当前批次字符数：{2:N0}\n已导入：{3}条\n已跳过重复：{4}条\n当前耗时：{5:mm\\:ss}\n当前接口：{6}",
                BatchIndex,
                BatchCount,
                BatchChars,
                Added,
                DuplicateSkipped,
                TimeSpan.FromMilliseconds(ElapsedMs),
                string.IsNullOrWhiteSpace(EndpointName) ? "-" : EndpointName);
        }
    }

    public class KnowledgeAiService
    {
        private const int TargetMinChars = 3000;
        private const int TargetMaxChars = 5000;
        private const int MaxBatchChars = 6000;
        private const int ImagesPerBatch = 5;

        private const string SystemPrompt =
            "你是 Knowledge Engine V2 的结构化知识整理助手。只能依据输入资料生成知识，禁止编造价格、库存、发货时间、物流时效或售后承诺。" +
            "每批最多生成40条记录。只输出严格JSON，不要输出解释、Markdown代码围栏或额外说明。" +
            "根对象必须包含 records 数组；每条记录使用 title,type,intent,subject,predicate,entities,aliases,answer,short_answer,conditions,exclusions,required_context,product_ids,risk_level,confidence,authority,status。" +
            "禁止输出 faqs/category/question/keywords 等旧版字段；不确定的事实不要猜测，缺失字段用空字符串或空数组。";

        public bool SupportsDirectVideo
        {
            get { return false; }
        }

        public async Task<KnowledgeImportResult> ImportAsync(ClipboardKnowledgeData data, Action<string> progress)
        {
            return await ImportAsync(
                data,
                BotFeatureStore.GetSmartImportTimeoutSeconds(),
                CancellationToken.None,
                () => SmartImportCancelSource.None,
                progress);
        }

        public async Task<KnowledgeImportResult> ImportAsync(
            ClipboardKnowledgeData data,
            int timeoutSeconds,
            CancellationToken userToken,
            Func<SmartImportCancelSource> cancelSource,
            Action<string> progress)
        {
            timeoutSeconds = ClampTimeout(timeoutSeconds);

            if (data == null || !data.HasAnalyzableContent)
            {
                throw new Exception("没有检测到可导入的文字、图片或媒体内容。");
            }

            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints.Count < 1)
            {
                throw new Exception("请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。");
            }

            var primary = endpoints.FirstOrDefault();
            var batches = BuildBatches(data, primary);
            var total = new KnowledgeImportResult
            {
                TextChars = (data.Text ?? string.Empty).Length,
                ImageCount = data.Images.Count,
                VideoSkipped = data.Videos.Count
            };

            var currentShop = ShopSettingsScope.Current;
            var existingV2 = currentShop == null || !currentShop.HasStableSellerId
                ? new List<KnowledgeV2Record>()
                : KnowledgeEngineV2Repository.LoadAll(currentShop.SellerNick);
            var existingTypes = new HashSet<string>(
                existingV2.Select(x => x.Type ?? string.Empty).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            var importedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var batchHash = Hash((batch.Text ?? string.Empty) + "\n" + string.Join("\n", batch.Images.Select(x => x.AiUrl ?? string.Empty)));
                var stopwatch = Stopwatch.StartNew();

                ReportProgress(progress, batch, i + 1, batches.Count, total, timeoutSeconds, stopwatch.ElapsedMilliseconds);

                ParseResult parsed = null;
                Exception lastError = null;

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token))
                    {
                        try
                        {
                            parsed = await AnalyzeBatchAsync(batch.Text, batch.Images, timeoutSeconds, linked.Token);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            var source = timeoutCts.IsCancellationRequested && !userToken.IsCancellationRequested
                                ? SmartImportCancelSource.Timeout
                                : (cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource());
                            var cancelError = BuildCancelException(source, i + 1, timeoutSeconds, batch.EndpointName);

                            if (source == SmartImportCancelSource.Timeout && attempt == 0)
                            {
                                lastError = cancelError;
                                if (progress != null)
                                {
                                    progress("第 " + (i + 1) + " 批请求超时，正在自动重试一次...");
                                }
                                continue;
                            }

                            throw cancelError;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            if (attempt == 0 && IsRetryable(ex.Message))
                            {
                                if (progress != null)
                                {
                                    progress("第 " + (i + 1) + " 批遇到临时错误，正在自动重试一次...");
                                }
                                continue;
                            }
                            throw;
                        }
                    }
                }

                if (parsed == null)
                {
                    if (lastError != null) throw lastError;
                    throw new Exception("智能导入未返回有效结果，本批没有写入知识库。");
                }

                total.UnsupportedImageSkipped += parsed.UnsupportedImages;
                total.AiGenerated += parsed.Items.Count;

                var save = SaveDeduped(parsed.Items);
                total.Added += save.Added;
                total.DuplicateSkipped += save.DuplicateSkipped;
                total.AddedItems.AddRange(save.AddedItems);

                foreach (var category in save.AddedItems
                    .Select(x => x.Type ?? string.Empty)
                    .Where(x => x.Length > 0 && !existingTypes.Contains(x)))
                {
                    importedTypes.Add(category);
                }
                total.NewTypeCount = importedTypes.Count;

                Log.Info(string.Format(
                    "SmartImport batch ok endpoint={0} model={1} batch={2}/{3} input_chars={4} batch_hash={5} elapsed_ms={6} timeout_seconds={7} parsed_items={8} added={9} dup={10}",
                    batch.EndpointName,
                    batch.Model,
                    i + 1,
                    batches.Count,
                    (batch.Text ?? string.Empty).Length,
                    batchHash,
                    stopwatch.ElapsedMilliseconds,
                    timeoutSeconds,
                    parsed.Items.Count,
                    save.Added,
                    save.DuplicateSkipped));

                ReportProgress(progress, batch, i + 1, batches.Count, total, timeoutSeconds, stopwatch.ElapsedMilliseconds);
            }

            return total;
        }

        public static int ClampTimeout(int seconds)
        {
            return Math.Max(120, Math.Min(1800, seconds <= 0 ? 600 : seconds));
        }

        private static void ReportProgress(
            Action<string> progress,
            Batch batch,
            int batchIndex,
            int batchCount,
            KnowledgeImportResult total,
            int timeoutSeconds,
            long elapsedMs)
        {
            if (progress == null) return;
            progress(new SmartImportProgress
            {
                BatchIndex = batchIndex,
                BatchCount = batchCount,
                BatchChars = (batch.Text ?? string.Empty).Length,
                Added = total.Added,
                DuplicateSkipped = total.DuplicateSkipped,
                ElapsedMs = elapsedMs,
                EndpointName = batch.EndpointName,
                TimeoutSeconds = timeoutSeconds
            }.ToString());
        }

        private SmartImportException BuildCancelException(
            SmartImportCancelSource source,
            int batchIndex,
            int timeoutSeconds,
            string endpointName)
        {
            if (source == SmartImportCancelSource.Timeout)
            {
                return new SmartImportException(
                    string.Format(
                        "智能导入超时：接口“{0}”等待超过{1}秒，未收到完整响应。本批内容尚未导入，可以重试本批。",
                        string.IsNullOrWhiteSpace(endpointName) ? "当前接口" : endpointName,
                        timeoutSeconds),
                    source,
                    batchIndex);
            }
            if (source == SmartImportCancelSource.WindowClosed)
            {
                return new SmartImportException("窗口已关闭，智能导入任务已停止。已完成批次会保留。", source, batchIndex);
            }
            if (source == SmartImportCancelSource.ReplacedByNewTask)
            {
                return new SmartImportException("新的智能导入任务已开始，旧任务已停止。已完成批次会保留。", source, batchIndex);
            }
            return new SmartImportException(
                "用户已取消智能导入。已完成批次会保留。",
                SmartImportCancelSource.UserCancel,
                batchIndex);
        }

        private sealed class Batch
        {
            public string Text;
            public List<KnowledgeMediaItem> Images;
            public string EndpointName;
            public string Model;
        }

        private sealed class ParseResult
        {
            public readonly List<KnowledgeV2Record> Items = new List<KnowledgeV2Record>();
            public int UnsupportedImages;
        }

        public static List<string> SplitTextBatches(string text)
        {
            text = text ?? string.Empty;
            var paragraphs = Regex.Split(text.Replace("\r\n", "\n"), "\n{2,}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (paragraphs.Count == 0)
            {
                return new List<string> { string.Empty };
            }

            var result = new List<string>();
            var current = new StringBuilder();

            foreach (var paragraph in paragraphs)
            {
                foreach (var piece in SplitLongParagraph(paragraph))
                {
                    if (current.Length > 0 && current.Length + piece.Length + 2 > TargetMaxChars)
                    {
                        FlushBatch(result, current);
                    }

                    if (piece.Length > TargetMaxChars)
                    {
                        FlushBatch(result, current);
                        result.Add(piece.Trim());
                        continue;
                    }

                    if (current.Length > 0) current.Append("\n\n");
                    current.Append(piece.Trim());

                    if (current.Length >= TargetMinChars)
                    {
                        FlushBatch(result, current);
                    }
                }
            }

            FlushBatch(result, current);
            return result.Count == 0 ? new List<string> { string.Empty } : result;
        }

        private static void FlushBatch(List<string> result, StringBuilder current)
        {
            if (current.Length == 0) return;
            result.Add(current.ToString().Trim());
            current.Length = 0;
        }

        private static IEnumerable<string> SplitLongParagraph(string paragraph)
        {
            paragraph = paragraph ?? string.Empty;
            if (paragraph.Length <= MaxBatchChars)
            {
                yield return paragraph;
                yield break;
            }

            var sentences = Regex.Split(paragraph, "(?<=[。！？?？!；;])")
                .Where(x => !string.IsNullOrEmpty(x));
            var current = new StringBuilder();

            foreach (var sentence in sentences)
            {
                foreach (var piece in SplitOversizedSegment(sentence, MaxBatchChars))
                {
                    if (current.Length > 0 && current.Length + piece.Length > MaxBatchChars)
                    {
                        yield return current.ToString();
                        current.Length = 0;
                    }
                    current.Append(piece);
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        private static IEnumerable<string> SplitOversizedSegment(string value, int maxChars)
        {
            value = value ?? string.Empty;
            var offset = 0;
            while (offset < value.Length)
            {
                var remaining = value.Length - offset;
                if (remaining <= maxChars)
                {
                    yield return value.Substring(offset);
                    yield break;
                }

                var cut = FindPreferredSplit(value, offset, maxChars);
                yield return value.Substring(offset, cut);
                offset += cut;
            }
        }

        private static int FindPreferredSplit(string value, int offset, int maxChars)
        {
            var minimum = Math.Max(1, maxChars / 2);
            for (var length = maxChars; length >= minimum; length--)
            {
                var c = value[offset + length - 1];
                if (char.IsWhiteSpace(c) || "，,、：:）)]}》>".IndexOf(c) >= 0)
                {
                    return length;
                }
            }
            return maxChars;
        }

        private List<Batch> BuildBatches(ClipboardKnowledgeData data, AiEndpointConfig primary)
        {
            var chunks = SplitTextBatches(data.Text);
            var images = data.Images
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.AiUrl))
                .ToList();
            var count = Math.Max(chunks.Count, (int)Math.Ceiling(images.Count / (double)ImagesPerBatch));
            count = Math.Max(1, count);

            return Enumerable.Range(0, count)
                .Select(i => new Batch
                {
                    Text = i < chunks.Count ? chunks[i] : string.Empty,
                    Images = images.Skip(i * ImagesPerBatch).Take(ImagesPerBatch).ToList(),
                    EndpointName = primary == null ? string.Empty : primary.Name,
                    Model = primary == null ? string.Empty : primary.TextModel
                })
                .ToList();
        }

        private async Task<ParseResult> AnalyzeBatchAsync(
            string text,
            List<KnowledgeMediaItem> images,
            int timeoutSeconds,
            CancellationToken token)
        {
            try
            {
                return await AnalyzeBatchCoreAsync(text, images, timeoutSeconds, token);
            }
            catch (Exception ex)
            {
                if (images != null && images.Count > 0 && IsVisionUnsupported(ex.Message))
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new ParseResult { UnsupportedImages = images.Count };
                    }

                    var fallback = await AnalyzeBatchCoreAsync(
                        text,
                        new List<KnowledgeMediaItem>(),
                        timeoutSeconds,
                        token);
                    fallback.UnsupportedImages = images.Count;
                    return fallback;
                }
                throw;
            }
        }

        private async Task<ParseResult> AnalyzeBatchCoreAsync(
            string text,
            List<KnowledgeMediaItem> images,
            int timeoutSeconds,
            CancellationToken token)
        {
            var userText = string.Format(
                "请整理以下资料为客服问答知识库。每批最多20到40条问答，max_tokens受限时优先保证JSON完整。资料文本：\n{0}",
                text ?? string.Empty);
            JToken content = userText;

            if (images != null && images.Count > 0)
            {
                var array = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = userText }
                };
                foreach (var image in images)
                {
                    array.Add(new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject { ["url"] = image.AiUrl }
                    });
                }
                content = array;
            }

            var messages = new JArray
            {
                Message("system", SystemPrompt),
                new JObject { ["role"] = "user", ["content"] = content }
            };

            var raw = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 4000, 0.1, timeoutSeconds, token),
                token);
            if (!raw.Success)
            {
                throw new Exception(raw.Error);
            }

            try
            {
                var result = new ParseResult();
                result.Items.AddRange(ParseAiKnowledgeResult(raw.Answer));
                return result;
            }
            catch (Exception ex)
            {
                throw new Exception("JSON解析失败：AI返回的数据格式异常，本批没有写入知识库。" + ex.Message);
            }
        }

        private static JObject Message(string role, string text)
        {
            return new JObject
            {
                ["role"] = role,
                ["content"] = text ?? string.Empty
            };
        }

        public static List<KnowledgeV2Record> ParseAiKnowledgeResult(string text)
        {
            text = StripFence(text);
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                throw new Exception("未找到 Knowledge V2 JSON 对象，可能是响应被截断。");

            var obj = JObject.Parse(text.Substring(start, end - start + 1));
            if (obj["faqs"] != null || obj["question"] != null || obj["category"] != null || obj["keywords"] != null)
                throw new Exception("AI 返回了旧版 FAQ 格式；当前只接受 Knowledge V2 records。");

            var records = obj["records"] as JArray;
            if (records == null)
            {
                foreach (var key in new[] { "knowledge", "items", "data", "result" })
                {
                    var nested = obj[key] as JArray;
                    if (nested == null) continue;
                    records = nested;
                    break;
                }
            }
            if (records == null && obj["answer"] != null && obj["title"] != null)
                records = new JArray(obj);
            if (records == null) throw new Exception("缺少 Knowledge V2 records 数组。");

            var result = new List<KnowledgeV2Record>();
            foreach (var item in records.OfType<JObject>().Take(40))
            {
                if (item["question"] != null || item["category"] != null || item["keywords"] != null)
                    throw new Exception("records 中包含旧版 FAQ 字段，拒绝混合格式导入。");

                var title = ReadText(item, "title");
                var answer = ReadText(item, "answer");
                if (title.Length == 0 || answer.Length == 0) continue;

                var entities = ReadStrings(item, "entities");
                if (entities.Count == 0)
                    entities = KnowledgeEngineV2Semantics.ExtractEntities(title + " " + answer);
                var aliases = ReadStrings(item, "aliases");
                aliases.Insert(0, title);
                var subject = ReadText(item, "subject");
                if (subject.Length == 0) subject = KnowledgeEngineV2Semantics.ResolveSubject(title, entities);
                var intent = ReadText(item, "intent");
                if (intent.Length == 0) intent = KnowledgeEngineV2Semantics.DetectIntent(title + " " + answer);
                var predicate = ReadText(item, "predicate");
                if (predicate.Length == 0) predicate = KnowledgeEngineV2Semantics.DetectPredicate(title + " " + answer);
                var risk = ReadText(item, "risk_level");
                if (string.Equals(risk, "high", StringComparison.OrdinalIgnoreCase)
                    || KnowledgeEngineV2Semantics.IsHighRisk(title + " " + answer))
                    risk = "high";
                else
                    risk = "normal";

                double confidence;
                if (!double.TryParse(ReadText(item, "confidence"), out confidence)
                    || double.IsNaN(confidence) || double.IsInfinity(confidence))
                    confidence = 0.86;
                confidence = Math.Max(0, Math.Min(1, confidence));
                double authority;
                if (!double.TryParse(ReadText(item, "authority"), out authority)
                    || double.IsNaN(authority) || double.IsInfinity(authority))
                    authority = 0.90;
                authority = Math.Max(0, Math.Min(1, authority));

                var now = DateTime.Now;
                var candidate = string.Equals(ReadText(item, "status"), "candidate", StringComparison.OrdinalIgnoreCase)
                    || risk == "high"
                    || confidence < 0.72
                    || authority < 0.70;
                result.Add(new KnowledgeV2Record
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = NormalizeDisplay(title),
                    Type = candidate ? "learning_candidate" : KnowledgeEngineV2Semantics.NormalizeType(ReadText(item, "type")),
                    Intent = KnowledgeEngineV2Semantics.NormalizeIntent(intent),
                    Subject = subject,
                    Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(predicate),
                    Entities = CleanStrings(entities),
                    Aliases = CleanStrings(aliases),
                    Answer = NormalizeDisplay(answer),
                    ShortAnswer = BuildShortAnswer(ReadText(item, "short_answer"), answer),
                    Conditions = CleanStrings(ReadStrings(item, "conditions")),
                    Exclusions = CleanStrings(ReadStrings(item, "exclusions")),
                    RequiredContext = CleanStrings(ReadStrings(item, "required_context")),
                    ProductIds = CleanStrings(ReadStrings(item, "product_ids")),
                    RiskLevel = risk,
                    Confidence = confidence,
                    Authority = authority,
                    Enabled = !candidate,
                    Status = candidate ? "candidate" : "active",
                    SourceType = "chat_history_analysis",
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastVerifiedAt = now
                });
            }

            if (result.Count < 1) throw new Exception("没有有效的 Knowledge V2 记录。");
            return result;
        }

        private static string ReadText(JObject obj, string name)
        {
            var token = obj == null ? null : obj[name];
            return token == null || token.Type == JTokenType.Null ? string.Empty : Convert.ToString(token).Trim();
        }

        private static List<string> ReadStrings(JObject obj, string name)
        {
            var token = obj == null ? null : obj[name];
            var array = token as JArray;
            if (array != null)
                return array.Select(x => Convert.ToString(x).Trim())
                    .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
            var value = token == null ? string.Empty : Convert.ToString(token);
            return string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(new[] { ',', '，', ';', '；', '|', '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        }

        private static List<string> CleanStrings(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        }

        private static string BuildShortAnswer(string requested, string answer)
        {
            requested = (requested ?? string.Empty).Trim();
            if (requested.Length > 0) return requested.Length <= 160 ? requested : requested.Substring(0, 160);
            answer = (answer ?? string.Empty).Trim();
            return answer.Length <= 100 ? answer : answer.Substring(0, 100);
        }

        public static string StripFence(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("```"))
            {
                text = Regex.Replace(text, "^```[a-zA-Z]*", string.Empty).Trim();
                if (text.EndsWith("```"))
                {
                    text = text.Substring(0, text.Length - 3).Trim();
                }
            }
            return text;
        }

        private KnowledgeImportResult SaveDeduped(List<KnowledgeV2Record> generated)
        {
            generated = generated ?? new List<KnowledgeV2Record>();
            var shop = ShopSettingsScope.Current;
            if (shop == null || !shop.HasStableSellerId || string.IsNullOrWhiteSpace(shop.SellerNick))
                throw new InvalidOperationException("历史/智能导入必须在精确 ShopKey 作用域内执行。");

            var existing = KnowledgeEngineV2Repository.LoadAll(shop.SellerNick)
                ?? new List<KnowledgeV2Record>();
            var oldTypes = new HashSet<string>(
                existing.Select(x => x.Type ?? string.Empty).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(existing.Select(x => ContentHash(x.Title, x.Answer)), StringComparer.Ordinal);
            var added = new List<KnowledgeV2Record>();
            var duplicateCount = 0;

            foreach (var item in generated)
            {
                if (item == null) continue;
                var key = ContentHash(item.Title, item.Answer);
                if (string.IsNullOrWhiteSpace(NormalizeQuestion(item.Title)) || !seen.Add(key))
                {
                    duplicateCount++;
                    continue;
                }
                added.Add(item);
            }

            // Analysis only: the caller promotes these V2 records through the exact
            // ShopKey-scoped history import service. No V1 DTO or JSON store exists.
            return new KnowledgeImportResult
            {
                AiGenerated = generated.Count,
                Added = added.Count,
                DuplicateSkipped = duplicateCount,
                NewTypeCount = added
                    .Select(x => x.Type ?? string.Empty)
                    .Where(x => x.Length > 0 && !oldTypes.Contains(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                AddedItems = added
            };
        }

        public static string NormalizeQuestion(string question)
        {
            var chars = (question ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Where(c => !char.IsWhiteSpace(c) && ",，.。?？!！;；:：、'\"“”‘’()（）[]【】{}《》<>-—_~`".IndexOf(c) < 0);
            return new string(chars.ToArray());
        }

        private static string NormalizeDisplay(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");
        }

        public static string ContentHash(string question, string answer)
        {
            return Hash(NormalizeQuestion(question) + "\n" + NormalizeDisplay(answer).ToLowerInvariant());
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static bool IsVisionUnsupported(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("unsupported image")
                || error.Contains("vision not supported")
                || error.Contains("invalid content type")
                || error.Contains("image_url")
                || error.Contains("multimodal")
                || error.Contains("http 400");
        }

        private static bool IsRetryable(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("超时")
                || error.Contains("timeout")
                || error.Contains("http 429")
                || error.Contains("http 500")
                || error.Contains("http 501")
                || error.Contains("http 502")
                || error.Contains("http 503")
                || error.Contains("http 504")
                || error.Contains("network")
                || error.Contains("连接")
                || error.Contains("断开");
        }
    }
}
