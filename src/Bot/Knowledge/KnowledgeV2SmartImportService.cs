using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2SmartImportResult
    {
        public int TextChars { get; set; }
        public int ImageCount { get; set; }
        public int VideoSkipped { get; set; }
        public int AiGenerated { get; set; }
        public int Added { get; set; }
        public int DuplicateSkipped { get; set; }
        public int UnsupportedImageSkipped { get; set; }
        public string ImportId { get; set; }
        public List<KnowledgeV2Record> AddedItems { get; private set; }

        public KnowledgeV2SmartImportResult()
        {
            AddedItems = new List<KnowledgeV2Record>();
            ImportId = string.Empty;
        }
    }

    internal sealed class KnowledgeV2SmartImportService
    {
        private const int ImagesPerBatch = 5;
        private const int MaxRecordsPerBatch = 18;
        private const string SystemPrompt =
            "你是电商客服 Knowledge Center V2 的结构化知识整理助手。只能依据输入资料生成知识，禁止编造价格、库存、发货时间、物流时效、售后承诺、账号、密码或商品属性。" +
            "只输出严格 JSON，不要解释、Markdown代码围栏或额外说明。根对象固定为 records 数组。" +
            "每条记录字段固定为：title,type,intent,subject,predicate,entities,aliases,answer,short_answer,conditions,exclusions,required_context,product_ids,risk_level,confidence,authority,status。" +
            "type 只能是 business_fact/procedure/presale/order_rule/after_sale/safety_rule/fixed_reply/product_knowledge/learning_candidate/temporary；" +
            "risk_level 只能 normal/high；status 只能 active/candidate；confidence/authority 范围0到1。" +
            "entities/aliases/conditions/exclusions/required_context/product_ids 必须是 JSON 字符串数组。" +
            "title 是便于管理的短标题，answer 是可直接给买家的标准答案，short_answer 是简短直答，aliases 是买家可能使用的同义问法。" +
            "无法从资料确定的事实不得猜测，可选字段用空字符串或空数组。禁止输出旧版 faqs/category/question/keywords 结构。";

        private sealed class Batch
        {
            public string Text;
            public List<KnowledgeMediaItem> Images;
            public string EndpointName;
        }

        private sealed class AnalysisResult
        {
            public List<KnowledgeV2Record> Items = new List<KnowledgeV2Record>();
            public int UnsupportedImages;
            public string ParseStrategy = string.Empty;
        }

        public bool SupportsDirectVideo { get { return false; } }

        public async Task<KnowledgeV2SmartImportResult> ImportAsync(
            string seller,
            string shopKey,
            ClipboardKnowledgeData data,
            int timeoutSeconds,
            CancellationToken userToken,
            Func<SmartImportCancelSource> cancelSource,
            Action<string> progress)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (seller.Length == 0 || shopKey.Length == 0)
                throw new InvalidOperationException("智能导入必须指定卖家和精确 ShopKey。");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, seller)))
                throw new InvalidOperationException("智能导入店铺身份不匹配，已阻止跨店写入。");
            // Pin one identity for endpoint selection, every awaited batch,
            // repository writes, index updates and audit records.
            using (ShopSettingsScope.Enter(shop))
            {
                return await ImportInShopAsync(seller, data, timeoutSeconds, userToken, cancelSource, progress);
            }
        }

        private async Task<KnowledgeV2SmartImportResult> ImportInShopAsync(
            string seller,
            ClipboardKnowledgeData data,
            int timeoutSeconds,
            CancellationToken userToken,
            Func<SmartImportCancelSource> cancelSource,
            Action<string> progress)
        {
            timeoutSeconds = KnowledgeAiService.ClampTimeout(timeoutSeconds);
            if (seller.Length == 0)
                throw new InvalidOperationException("无法识别当前店铺客服账号，不能写入 Knowledge Center V2。");
            if (data == null || !data.HasAnalyzableContent)
                throw new InvalidOperationException("没有检测到可导入的文字、图片或媒体内容。");

            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints.Count < 1)
                throw new InvalidOperationException("请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。");

            var primary = endpoints.FirstOrDefault();
            var batches = BuildBatches(data, primary == null ? string.Empty : primary.Name);
            var importId = "v2-ai-import-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new KnowledgeV2SmartImportResult
            {
                ImportId = importId,
                TextChars = (data.Text ?? string.Empty).Length,
                ImageCount = data.Images == null ? 0 : data.Images.Count,
                VideoSkipped = data.Videos == null ? 0 : data.Videos.Count
            };

            var existing = KnowledgeEngineV2Repository.LoadAll(seller);
            var seen = new HashSet<string>(existing.Where(x => x != null)
                .Select(x => KnowledgeAiService.ContentHash(x.Title, x.Answer)), StringComparer.Ordinal);

            try
            {
                for (var i = 0; i < batches.Count; i++)
                {
                    if (userToken.IsCancellationRequested)
                        throw BuildCancelException(cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource(), i + 1, timeoutSeconds, batches[i].EndpointName);
                    var batch = batches[i];
                    var stopwatch = Stopwatch.StartNew();
                    ReportProgress(progress, batch, i + 1, batches.Count, result, stopwatch.ElapsedMilliseconds);
                    AnalysisResult analyzed = null;
                    Exception lastError = null;

                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token))
                        {
                            try
                            {
                                analyzed = await AnalyzeBatchAsync(batch.Text, batch.Images, timeoutSeconds, linked.Token);
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
                                    if (progress != null) progress("第 " + (i + 1) + " 批请求超时，正在自动重试一次...");
                                    continue;
                                }
                                throw cancelError;
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                                if (userToken.IsCancellationRequested)
                                    throw BuildCancelException(cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource(), i + 1, timeoutSeconds, batch.EndpointName);
                                if (attempt == 0 && (ex is HttpRequestException || IsRetryable(ex.Message)))
                                {
                                    if (progress != null) progress("第 " + (i + 1) + " 批遇到临时错误，正在自动重试一次...");
                                    continue;
                                }
                                throw;
                            }
                        }
                    }

                    if (analyzed == null)
                    {
                        if (lastError != null) throw lastError;
                        throw new InvalidOperationException("智能导入未返回有效 V2 结果，本批没有写入知识库。");
                    }

                    result.UnsupportedImageSkipped += analyzed.UnsupportedImages;
                    result.AiGenerated += analyzed.Items.Count;
                    foreach (var record in analyzed.Items)
                    {
                        if (userToken.IsCancellationRequested)
                            throw BuildCancelException(cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource(), i + 1, timeoutSeconds, batch.EndpointName);
                        if (record == null || string.IsNullOrWhiteSpace(record.Title) || string.IsNullOrWhiteSpace(record.Answer))
                            continue;
                        var contentHash = KnowledgeAiService.ContentHash(record.Title, record.Answer);
                        if (!seen.Add(contentHash))
                        {
                            result.DuplicateSkipped++;
                            continue;
                        }
                        record.SourceType = "ai_smart_import";
                        // Batch ID is audit provenance, not a unique record key.
                        record.SourceId = importId + ":" + contentHash;
                        record.Enabled = true;
                        if (!KnowledgeEngineV2Repository.TryAddHistoryImportRecord(seller, record))
                        {
                            result.DuplicateSkipped++;
                            continue;
                        }
                        result.AddedItems.Add(record);
                        result.Added++;
                    }

                    Log.Info(string.Format(
                        "KnowledgeV2 SmartImport batch ok seller={0} endpoint={1} batch={2}/{3} input_chars={4} elapsed_ms={5} generated={6} added_total={7} dup_total={8} parse={9}",
                        seller, batch.EndpointName, i + 1, batches.Count, (batch.Text ?? string.Empty).Length,
                        stopwatch.ElapsedMilliseconds, analyzed.Items.Count, result.Added, result.DuplicateSkipped, analyzed.ParseStrategy));
                    ReportProgress(progress, batch, i + 1, batches.Count, result, stopwatch.ElapsedMilliseconds);
                }

                if (result.Added > 0) KnowledgeEngineV2Service.Warm(seller);
                AppendAudit(seller, result, "success", "AI智能导入完成：原生 Knowledge V2 结构已写入");
                return result;
            }
            catch (Exception ex)
            {
                AppendAudit(seller, result, "failed", "AI智能导入中止：" + Safe(ex.Message, 500));
                throw;
            }
        }

        private static List<Batch> BuildBatches(ClipboardKnowledgeData data, string endpointName)
        {
            var chunks = KnowledgeAiService.SplitTextBatches(data == null ? string.Empty : data.Text);
            var images = data == null || data.Images == null
                ? new List<KnowledgeMediaItem>()
                : data.Images.Where(x => x != null && !string.IsNullOrWhiteSpace(x.AiUrl)).ToList();
            var count = Math.Max(chunks.Count, (int)Math.Ceiling(images.Count / (double)ImagesPerBatch));
            count = Math.Max(1, count);
            return Enumerable.Range(0, count).Select(i => new Batch
            {
                Text = i < chunks.Count ? chunks[i] : string.Empty,
                Images = images.Skip(i * ImagesPerBatch).Take(ImagesPerBatch).ToList(),
                EndpointName = endpointName ?? string.Empty
            }).ToList();
        }

        private static async Task<AnalysisResult> AnalyzeBatchAsync(
            string text, List<KnowledgeMediaItem> images, int timeoutSeconds, CancellationToken token)
        {
            try
            {
                return await AnalyzeBatchCoreAsync(text, images, timeoutSeconds, token);
            }
            catch (Exception ex)
            {
                if (images != null && images.Count > 0 && IsVisionUnsupported(ex.Message))
                {
                    if (string.IsNullOrWhiteSpace(text)) return new AnalysisResult { UnsupportedImages = images.Count };
                    var fallback = await AnalyzeBatchCoreAsync(text, new List<KnowledgeMediaItem>(), timeoutSeconds, token);
                    fallback.UnsupportedImages = images.Count;
                    return fallback;
                }
                throw;
            }
        }

        private static async Task<AnalysisResult> AnalyzeBatchCoreAsync(
            string text, List<KnowledgeMediaItem> images, int timeoutSeconds, CancellationToken token)
        {
            var userText = string.Format(
                "请把以下资料整理为 Knowledge Center V2 原生结构化知识。最多生成 {0} 条，宁可减少条数也必须保证 JSON 完整和字段准确。资料文本：\n{1}",
                MaxRecordsPerBatch, text ?? string.Empty);
            var messages = BuildMessages(userText, images, false, null, null);
            var raw = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 7000, 0.1, timeoutSeconds, token), token);
            if (raw == null || !raw.Success) throw new InvalidOperationException(raw == null ? "AI请求没有返回结果。" : raw.Error);

            List<KnowledgeV2Record> records;
            string strategy;
            string parseError;
            if (!TryParseRecords(raw.Answer, out records, out strategy, out parseError))
            {
                Log.Info("KnowledgeV2 SmartImport native parse failed; repair once. answer_chars=" + SafeLength(raw.Answer)
                    + ", error=" + Safe(parseError, 240));
                var repair = BuildMessages(userText, images, true, raw.Answer, parseError);
                var fixedRaw = await Task.Run(() => MyOpenAI.CallStructuredChat(repair, 7000, 0.0, timeoutSeconds, token), token);
                if (fixedRaw == null || !fixedRaw.Success)
                    throw new InvalidOperationException("V2 JSON解析失败，自动修复请求也失败：" + (fixedRaw == null ? "没有返回结果" : Safe(fixedRaw.Error, 180)));
                if (!TryParseRecords(fixedRaw.Answer, out records, out strategy, out parseError))
                    throw new InvalidOperationException("V2 JSON解析失败：自动修复后仍不是当前 Knowledge V2 结构（" + Safe(parseError, 240) + "）。本批没有写入。 ");
                strategy = "repair/" + strategy;
            }
            return new AnalysisResult { Items = records, ParseStrategy = strategy };
        }

        private static JArray BuildMessages(string userText, List<KnowledgeMediaItem> images, bool repair, string invalid, string parseError)
        {
            JToken content = userText;
            if (images != null && images.Count > 0)
            {
                var array = new JArray { new JObject { ["type"] = "text", ["text"] = userText } };
                foreach (var image in images)
                    array.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = image.AiUrl } });
                content = array;
            }
            var prompt = SystemPrompt;
            if (repair)
                prompt += " 上一次输出未通过当前 V2 校验。现在只修复结构：必须返回 {\"records\":[...]}；禁止旧版faqs/question/category/keywords，禁止解释或道歉。";
            var messages = new JArray { new JObject { ["role"] = "system", ["content"] = prompt } };
            if (repair)
            {
                messages.Add(new JObject { ["role"] = "user", ["content"] = userText
                    + "\n上次校验错误：" + Safe(parseError, 300)
                    + "\n上次无效输出：" + Safe(invalid, 3000)
                    + "\n请仅返回修复后的当前 Knowledge V2 JSON。" });
            }
            else
            {
                messages.Add(new JObject { ["role"] = "user", ["content"] = content });
            }
            return messages;
        }

        private static bool TryParseRecords(string raw, out List<KnowledgeV2Record> records, out string strategy, out string error)
        {
            records = new List<KnowledgeV2Record>();
            strategy = string.Empty;
            error = string.Empty;
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) { error = "AI返回为空"; return false; }

            foreach (var candidate in CandidateJson(raw))
            {
                try
                {
                    List<JObject> objects;
                    string unwrapError;
                    if (!TryUnwrapRecords(JToken.Parse(candidate.Value), out objects, out unwrapError))
                    {
                        error = unwrapError;
                        continue;
                    }
                    if (objects.Count < 1) { error = "records 数组为空"; continue; }
                    var parsed = new List<KnowledgeV2Record>();
                    foreach (var obj in objects)
                    {
                        string itemError;
                        var record = ParseRecord(obj, out itemError);
                        if (record == null)
                        {
                            error = itemError;
                            parsed.Clear();
                            break;
                        }
                        parsed.Add(record);
                    }
                    if (parsed.Count < 1) continue;
                    records = parsed.Take(MaxRecordsPerBatch).ToList();
                    strategy = candidate.Key;
                    return true;
                }
                catch (Exception ex) { error = ex.Message; }
            }
            if (string.IsNullOrWhiteSpace(error)) error = "未找到可解析的 Knowledge V2 JSON";
            if (string.IsNullOrWhiteSpace(ExtractBalancedJson(StripCodeFence(raw))))
                error += "；可能是响应被截断";
            return false;
        }

        private static IEnumerable<KeyValuePair<string, string>> CandidateJson(string raw)
        {
            yield return new KeyValuePair<string, string>("direct-json", raw);
            var unfenced = StripCodeFence(raw);
            if (!string.Equals(unfenced, raw, StringComparison.Ordinal))
                yield return new KeyValuePair<string, string>("markdown-fence", unfenced);
            var balanced = ExtractBalancedJson(unfenced);
            if (!string.IsNullOrWhiteSpace(balanced) && !string.Equals(balanced, unfenced, StringComparison.Ordinal))
                yield return new KeyValuePair<string, string>("balanced-json", balanced);
        }

        private static bool TryUnwrapRecords(JToken token, out List<JObject> objects, out string error)
        {
            objects = new List<JObject>();
            error = string.Empty;
            if (token == null) { error = "JSON为空"; return false; }
            var obj = token as JObject;
            if (obj != null)
            {
                if (obj["faqs"] != null || LooksLegacy(obj)) { error = "检测到旧版 FAQ 字段，要求当前 Knowledge V2 records 结构"; return false; }
                if (LooksCurrentV2(obj)) { objects.Add(obj); return true; }
                foreach (var key in new[] { "records", "knowledge", "items", "data", "result" })
                {
                    var nested = obj[key];
                    if (nested == null) continue;
                    if (TryUnwrapRecords(nested, out objects, out error)) return true;
                }
                error = "JSON对象没有当前 Knowledge V2 字段或 records 数组";
                return false;
            }
            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array)
                {
                    var current = item as JObject;
                    if (current == null || LooksLegacy(current) || !LooksCurrentV2(current))
                    {
                        error = "数组中存在旧版或非 Knowledge V2 记录";
                        objects.Clear();
                        return false;
                    }
                    objects.Add(current);
                }
                return objects.Count > 0;
            }
            var value = token as JValue;
            if (value != null && value.Type == JTokenType.String)
            {
                try { return TryUnwrapRecords(JToken.Parse(Convert.ToString(value.Value)), out objects, out error); }
                catch { error = "字符串内容不是有效 JSON"; return false; }
            }
            error = "JSON根类型不受支持";
            return false;
        }

        private static bool LooksLegacy(JObject obj)
        {
            return obj != null && (obj["question"] != null || obj["category"] != null || obj["keywords"] != null || obj["faqs"] != null);
        }

        private static bool LooksCurrentV2(JObject obj)
        {
            if (obj == null || obj["answer"] == null) return false;
            return obj["title"] != null || obj["type"] != null || obj["intent"] != null || obj["subject"] != null
                || obj["predicate"] != null || obj["entities"] != null || obj["aliases"] != null || obj["short_answer"] != null;
        }

        private static KnowledgeV2Record ParseRecord(JObject obj, out string error)
        {
            error = string.Empty;
            if (LooksLegacy(obj)) { error = "记录仍包含旧版 FAQ 字段"; return null; }
            var title = Text(obj, "title");
            var answer = Text(obj, "answer");
            if (title.Length == 0 || answer.Length == 0)
            {
                error = "Knowledge V2 记录缺少必需字段 title 或 answer";
                return null;
            }
            var entities = Strings(obj, "entities");
            if (entities.Count == 0) entities = KnowledgeEngineV2Semantics.ExtractEntities(title + " " + answer);
            var aliases = Strings(obj, "aliases");
            aliases.Insert(0, title);
            var subject = Text(obj, "subject");
            if (subject.Length == 0) subject = KnowledgeEngineV2Semantics.ResolveSubject(title, entities);
            var intent = Text(obj, "intent");
            if (intent.Length == 0) intent = KnowledgeEngineV2Semantics.DetectIntent(title + " " + answer);
            var predicate = Text(obj, "predicate");
            if (predicate.Length == 0) predicate = KnowledgeEngineV2Semantics.DetectPredicate(title + " " + answer);
            var risk = Text(obj, "risk_level");
            if (string.Equals(risk, "high", StringComparison.OrdinalIgnoreCase)
                || KnowledgeEngineV2Semantics.IsHighRisk(title + " " + answer)) risk = "high"; else risk = "normal";
            var now = DateTime.Now;
            return new KnowledgeV2Record
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Type = KnowledgeEngineV2Semantics.NormalizeType(Text(obj, "type")),
                Intent = KnowledgeEngineV2Semantics.NormalizeIntent(intent),
                Subject = subject,
                Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(predicate),
                Entities = Clean(entities),
                Aliases = Clean(aliases),
                Answer = answer,
                ShortAnswer = BuildShortAnswer(Text(obj, "short_answer"), answer),
                Conditions = Clean(Strings(obj, "conditions")),
                Exclusions = Clean(Strings(obj, "exclusions")),
                RequiredContext = Clean(Strings(obj, "required_context")),
                ProductIds = Clean(Strings(obj, "product_ids").Concat(entities.Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)).Select(x => x.Substring(8))).ToList()),
                RiskLevel = risk,
                Confidence = Clamp(Number(obj, "confidence", 0.86)),
                Authority = Clamp(Number(obj, "authority", 0.90)),
                Enabled = true,
                Status = string.Equals(Text(obj, "status"), "candidate", StringComparison.OrdinalIgnoreCase) ? "candidate" : "active",
                CreatedAt = now,
                UpdatedAt = now,
                LastVerifiedAt = now
            };
        }

        private static string Text(JObject obj, string name)
        {
            var token = obj == null ? null : obj[name];
            return token == null || token.Type == JTokenType.Null ? string.Empty : Convert.ToString(token).Trim();
        }

        private static List<string> Strings(JObject obj, string name)
        {
            var token = obj == null ? null : obj[name];
            var array = token as JArray;
            if (array != null) return array.Select(x => Convert.ToString(x)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var text = token == null ? string.Empty : Convert.ToString(token);
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        }

        private static double Number(JObject obj, string name, double fallback)
        {
            double value;
            return double.TryParse(Text(obj, name), out value)
                && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;
        }

        private static List<string> Clean(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }

        private static string BuildShortAnswer(string requested, string answer)
        {
            requested = (requested ?? string.Empty).Trim();
            if (requested.Length > 0) return requested.Length <= 160 ? requested : requested.Substring(0, 160);
            answer = (answer ?? string.Empty).Trim();
            return answer.Length <= 100 ? answer : answer.Substring(0, 100);
        }

        private static string StripCodeFence(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (!raw.StartsWith("```", StringComparison.Ordinal)) return raw;
            var first = raw.IndexOf('\n');
            var last = raw.LastIndexOf("```", StringComparison.Ordinal);
            return first >= 0 && last > first ? raw.Substring(first + 1, last - first - 1).Trim() : raw;
        }

        private static string ExtractBalancedJson(string raw)
        {
            raw = raw ?? string.Empty;
            for (var start = 0; start < raw.Length; start++)
            {
                if (raw[start] != '{' && raw[start] != '[') continue;
                var open = raw[start];
                var close = open == '{' ? '}' : ']';
                var depth = 0; var inString = false; var escaped = false;
                for (var i = start; i < raw.Length; i++)
                {
                    var ch = raw[i];
                    if (inString)
                    {
                        if (escaped) { escaped = false; continue; }
                        if (ch == '\\') { escaped = true; continue; }
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == open) depth++;
                    else if (ch == close && --depth == 0) return raw.Substring(start, i - start + 1);
                }
            }
            return string.Empty;
        }

        private static void ReportProgress(Action<string> progress, Batch batch, int batchIndex, int batchCount,
            KnowledgeV2SmartImportResult result, long elapsedMs)
        {
            if (progress == null) return;
            progress(string.Format(
                "正在分析第 {0}/{1} 批\n当前批次字符数：{2:N0}\n已写入 Knowledge V2：{3} 条\n已跳过重复：{4} 条\n当前耗时：{5:mm\\:ss}\n当前接口：{6}",
                batchIndex, batchCount, (batch.Text ?? string.Empty).Length, result.Added,
                result.DuplicateSkipped, TimeSpan.FromMilliseconds(elapsedMs),
                string.IsNullOrWhiteSpace(batch.EndpointName) ? "-" : batch.EndpointName));
        }

        private static SmartImportException BuildCancelException(SmartImportCancelSource source, int batchIndex, int timeoutSeconds, string endpointName)
        {
            if (source == SmartImportCancelSource.Timeout)
                return new SmartImportException(string.Format("智能导入超时：接口“{0}”等待超过{1}秒，未收到完整响应。本批内容尚未导入，可以重试。", string.IsNullOrWhiteSpace(endpointName) ? "当前接口" : endpointName, timeoutSeconds), source, batchIndex);
            if (source == SmartImportCancelSource.WindowClosed)
                return new SmartImportException("页面已关闭，智能导入任务已停止。已完成批次会保留在 Knowledge V2。", source, batchIndex);
            if (source == SmartImportCancelSource.ReplacedByNewTask)
                return new SmartImportException("新的智能导入任务已开始，旧任务已停止。已完成批次会保留在 Knowledge V2。", source, batchIndex);
            return new SmartImportException("用户已取消智能导入。已完成批次会保留在 Knowledge V2。", SmartImportCancelSource.UserCancel, batchIndex);
        }

        private static void AppendAudit(string seller, KnowledgeV2SmartImportResult result, string auditResult, string summary)
        {
            if (result == null) return;
            try
            {
                string ignored;
                KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller, "ai_smart_import", "knowledge_import", result.ImportId,
                    string.Empty, "AI智能导入", string.Empty,
                    string.Format("schema=knowledge_v2;generated={0};added={1};duplicate_skipped={2};images={3};unsupported_images={4};video_skipped={5}",
                        result.AiGenerated, result.Added, result.DuplicateSkipped, result.ImageCount, result.UnsupportedImageSkipped, result.VideoSkipped),
                    summary, auditResult, out ignored);
            }
            catch { }
        }

        private static bool IsVisionUnsupported(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("unsupported image") || error.Contains("vision not supported") || error.Contains("invalid content type")
                || error.Contains("image_url") || error.Contains("multimodal") || error.Contains("http 400");
        }

        private static bool IsRetryable(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("超时") || error.Contains("timeout") || error.Contains("temporarily") || error.Contains("connection")
                || error.Contains("http 429") || error.Contains("http 500") || error.Contains("http 502") || error.Contains("http 503") || error.Contains("http 504");
        }

        private static int SafeLength(string value) { return string.IsNullOrEmpty(value) ? 0 : value.Length; }
        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
