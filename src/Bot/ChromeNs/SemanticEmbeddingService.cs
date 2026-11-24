using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class SemanticKnowledgeScore
    {
        public KnowledgeBaseEntry Entry { get; set; }
        public double Score { get; set; }
    }

    internal sealed class SemanticEmbeddingResult
    {
        public bool Applied { get; set; }
        public string Model { get; set; }
        public long LatencyMs { get; set; }
        public List<SemanticKnowledgeScore> Scores { get; set; }

        public SemanticEmbeddingResult()
        {
            Model = string.Empty;
            Scores = new List<SemanticKnowledgeScore>();
        }
    }

    internal static class SemanticEmbeddingService
    {
        private const string Scope = "ai-control-plane";
        private const string ModelKey = "ControlPlaneEmbeddingModel";
        private const int RequestTimeoutMilliseconds = 2200;
        private const int ImmediateMissingDocuments = 12;
        private const int WarmupBatchSize = 24;
        private const int MaxCacheEntries = 3000;
        private static readonly object CacheSync = new object();
        private static readonly SemaphoreSlim WarmupGate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = CreateHttpClient();
        private static EmbeddingCacheFile _cache;
        private static DateTime _lastFailureLogAt = DateTime.MinValue;

        private sealed class EmbeddingCacheFile
        {
            public int Version { get; set; }
            public List<EmbeddingCacheItem> Items { get; set; }

            public EmbeddingCacheFile()
            {
                Version = 1;
                Items = new List<EmbeddingCacheItem>();
            }
        }

        private sealed class EmbeddingCacheItem
        {
            public string KnowledgeId { get; set; }
            public string ContentHash { get; set; }
            public string Model { get; set; }
            public float[] Vector { get; set; }
            public string UpdatedAt { get; set; }
        }

        private sealed class DocumentDescriptor
        {
            public KnowledgeBaseEntry Entry { get; set; }
            public string Id { get; set; }
            public string Text { get; set; }
            public string ContentHash { get; set; }
        }

        public static bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(GetConfiguredModel()) && ResolveEndpoint() != null;
        }

        public static SemanticEmbeddingResult TryScore(
            string query,
            IEnumerable<KnowledgeBaseEntry> allKnowledge,
            IEnumerable<SmartKnowledgeCandidate> localCandidates)
        {
            return TryScoreAsync(query, allKnowledge, localCandidates, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        public static async Task<SemanticEmbeddingResult> TryScoreAsync(
            string query,
            IEnumerable<KnowledgeBaseEntry> allKnowledge,
            IEnumerable<SmartKnowledgeCandidate> localCandidates,
            CancellationToken cancellationToken)
        {
            var result = new SemanticEmbeddingResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = GetConfiguredModel();
                if (string.IsNullOrWhiteSpace(model)) return result;
                var endpoint = ResolveEndpoint();
                if (endpoint == null) return result;
                query = (query ?? string.Empty).Trim();
                if (query.Length < 2) return result;

                var documents = BuildDocuments(allKnowledge).ToList();
                if (documents.Count == 0) return result;
                var localIds = new HashSet<string>(
                    (localCandidates ?? new SmartKnowledgeCandidate[0])
                        .Where(x => x != null && x.Entry != null)
                        .Select(x => StableId(x.Entry)),
                    StringComparer.Ordinal);

                var cache = LoadCache();
                var cached = BuildValidCacheMap(cache, model, documents);
                var immediateMissing = documents
                    .Where(x => localIds.Contains(x.Id) && !cached.ContainsKey(x.Id))
                    .Take(ImmediateMissingDocuments)
                    .ToList();

                var inputs = new List<string> { query };
                inputs.AddRange(immediateMissing.Select(x => x.Text));
                var started = DateTime.Now;
                var vectors = await RequestEmbeddingsAsync(endpoint, model, inputs, cancellationToken)
                    .ConfigureAwait(false);
                result.LatencyMs = Math.Max(0, (long)(DateTime.Now - started).TotalMilliseconds);
                if (vectors == null || vectors.Count != inputs.Count || vectors[0] == null || vectors[0].Length == 0)
                {
                    return result;
                }

                for (var i = 0; i < immediateMissing.Count; i++)
                {
                    var vector = vectors[i + 1];
                    if (vector == null || vector.Length == 0) continue;
                    UpsertCache(cache, immediateMissing[i], model, vector);
                    cached[immediateMissing[i].Id] = vector;
                }
                SaveCache(cache);

                var queryVector = vectors[0];
                result.Model = model;
                result.Applied = true;
                result.Scores = documents
                    .Where(x => cached.ContainsKey(x.Id))
                    .Select(x => new SemanticKnowledgeScore
                    {
                        Entry = x.Entry,
                        Score = Cosine(queryVector, cached[x.Id])
                    })
                    .Where(x => x.Score >= 0.30)
                    .OrderByDescending(x => x.Score)
                    .Take(24)
                    .ToList();

                QueueWarmup(endpoint, model, documents, cached);
                return result;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                LogFailure("Embedding语义检索超过" + RequestTimeoutMilliseconds + "ms子预算，已自动降级到本地混合检索");
                return result;
            }
            catch (Exception ex)
            {
                LogFailure("Embedding语义检索已自动降级到本地混合检索：" + ex.Message);
                return result;
            }
        }

        private static void QueueWarmup(
            AiEndpointConfig endpoint,
            string model,
            List<DocumentDescriptor> documents,
            Dictionary<string, float[]> cached)
        {
            var missing = documents
                .Where(x => !cached.ContainsKey(x.Id))
                .Take(WarmupBatchSize)
                .ToList();
            if (missing.Count == 0 || !WarmupGate.Wait(0)) return;

            Task.Run(async () =>
            {
                try
                {
                    var vectors = await RequestEmbeddingsAsync(
                        endpoint,
                        model,
                        missing.Select(x => x.Text).ToList(),
                        CancellationToken.None).ConfigureAwait(false);
                    if (vectors == null || vectors.Count != missing.Count) return;
                    var cache = LoadCache();
                    for (var i = 0; i < missing.Count; i++)
                    {
                        if (vectors[i] == null || vectors[i].Length == 0) continue;
                        UpsertCache(cache, missing[i], model, vectors[i]);
                    }
                    SaveCache(cache);
                    Log.Info("知识Embedding后台预热完成: model=" + model + ", count=" + missing.Count);
                }
                catch (OperationCanceledException)
                {
                    LogFailure("知识Embedding后台预热超过" + RequestTimeoutMilliseconds + "ms预算，已忽略");
                }
                catch (Exception ex)
                {
                    LogFailure("知识Embedding后台预热失败，已忽略：" + ex.Message);
                }
                finally
                {
                    WarmupGate.Release();
                }
            });
        }

        private static async Task<List<float[]>> RequestEmbeddingsAsync(
            AiEndpointConfig endpoint,
            string model,
            IList<string> inputs,
            CancellationToken cancellationToken)
        {
            if (endpoint == null || inputs == null || inputs.Count == 0) return null;
            var url = NormalizeEmbeddingUrl(endpoint.BaseUrl);
            var payload = new JObject
            {
                ["model"] = model,
                ["input"] = new JArray(inputs.Select(x => new JValue(x))),
                ["encoding_format"] = "float",
                ["timeout_seconds"] = 15
            };

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                linked.CancelAfter(RequestTimeoutMilliseconds);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    linked.Token).ConfigureAwait(false))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 240));
                    }
                    var json = JObject.Parse(body);
                    var rows = json["data"] as JArray;
                    if (rows == null || rows.Count != inputs.Count)
                    {
                        throw new Exception("Embedding返回数量与请求数量不一致");
                    }
                    var output = new float[inputs.Count][];
                    foreach (var row in rows.OfType<JObject>())
                    {
                        int index;
                        if (!int.TryParse(Convert.ToString(row["index"]), out index)) continue;
                        if (index < 0 || index >= output.Length) continue;
                        var vector = row["embedding"] as JArray;
                        if (vector == null || vector.Count == 0) continue;
                        output[index] = vector.Select(x => Convert.ToSingle(x)).ToArray();
                    }
                    return output.ToList();
                }
            }
        }

        private static IEnumerable<DocumentDescriptor> BuildDocuments(IEnumerable<KnowledgeBaseEntry> knowledge)
        {
            foreach (var entry in knowledge ?? new KnowledgeBaseEntry[0])
            {
                if (entry == null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Answer)
                    || Bot.Knowledge.KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(entry)) continue;
                var id = StableId(entry);
                var text = BuildDocumentText(entry);
                yield return new DocumentDescriptor
                {
                    Entry = entry,
                    Id = id,
                    Text = text,
                    ContentHash = Sha256(text)
                };
            }
        }

        private static Dictionary<string, float[]> BuildValidCacheMap(
            EmbeddingCacheFile cache,
            string model,
            IEnumerable<DocumentDescriptor> documents)
        {
            var descriptors = documents.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            lock (CacheSync)
            {
                foreach (var item in cache.Items ?? new List<EmbeddingCacheItem>())
                {
                    DocumentDescriptor document;
                    if (item == null
                        || string.IsNullOrWhiteSpace(item.KnowledgeId)
                        || item.Vector == null
                        || item.Vector.Length == 0
                        || !string.Equals(item.Model, model, StringComparison.Ordinal)
                        || !descriptors.TryGetValue(item.KnowledgeId, out document)
                        || !string.Equals(item.ContentHash, document.ContentHash, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    result[item.KnowledgeId] = item.Vector;
                }
            }
            return result;
        }

        private static void UpsertCache(
            EmbeddingCacheFile cache,
            DocumentDescriptor document,
            string model,
            float[] vector)
        {
            lock (CacheSync)
            {
                var existing = cache.Items.FirstOrDefault(x => x != null
                    && string.Equals(x.KnowledgeId, document.Id, StringComparison.Ordinal)
                    && string.Equals(x.Model, model, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new EmbeddingCacheItem();
                    cache.Items.Add(existing);
                }
                existing.KnowledgeId = document.Id;
                existing.ContentHash = document.ContentHash;
                existing.Model = model;
                existing.Vector = vector;
                existing.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (cache.Items.Count > MaxCacheEntries)
                {
                    cache.Items = cache.Items
                        .OrderByDescending(x => x == null ? string.Empty : x.UpdatedAt)
                        .Take(MaxCacheEntries)
                        .ToList();
                }
            }
        }

        private static EmbeddingCacheFile LoadCache()
        {
            lock (CacheSync)
            {
                if (_cache != null) return _cache;
                try
                {
                    var path = GetCachePath();
                    _cache = File.Exists(path)
                        ? JsonConvert.DeserializeObject<EmbeddingCacheFile>(File.ReadAllText(path, Encoding.UTF8))
                        : null;
                }
                catch
                {
                    _cache = null;
                }
                if (_cache == null) _cache = new EmbeddingCacheFile();
                if (_cache.Items == null) _cache.Items = new List<EmbeddingCacheItem>();
                return _cache;
            }
        }

        private static void SaveCache(EmbeddingCacheFile cache)
        {
            lock (CacheSync)
            {
                var path = GetCachePath();
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(cache), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                _cache = cache;
            }
        }

        private static string GetConfiguredModel()
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(ModelKey, Scope, string.Empty).Trim();
        }

        private static AiEndpointConfig ResolveEndpoint()
        {
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints == null || endpoints.Count == 0) return null;
            return endpoints.FirstOrDefault(x => x != null && x.Type == "服务端控制面")
                ?? endpoints.FirstOrDefault(x => x != null && !string.IsNullOrWhiteSpace(x.BaseUrl) && !string.IsNullOrWhiteSpace(x.ApiKey));
        }

        private static string NormalizeEmbeddingUrl(string baseUrl)
        {
            var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            var suffixes = new[] { "/chat/completions", "/responses", "/embeddings" };
            foreach (var suffix in suffixes)
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - suffix.Length).TrimEnd('/');
                    break;
                }
            }
            return value + "/embeddings";
        }

        private static string BuildDocumentText(KnowledgeBaseEntry entry)
        {
            return "问题：" + Safe(entry.Title, 500)
                + "\n分类：" + Safe(entry.Category, 160)
                + "\n关键词：" + Safe(entry.Keywords, 400)
                + "\n答案：" + Safe(entry.Answer, 900);
        }

        private static string StableId(KnowledgeBaseEntry entry)
        {
            var id = entry == null ? string.Empty : Convert.ToString(entry.Id);
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            return Sha256(BuildDocumentText(entry));
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static double Cosine(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length == 0 || left.Length != right.Length) return 0;
            double dot = 0;
            double a = 0;
            double b = 0;
            for (var i = 0; i < left.Length; i++)
            {
                dot += left[i] * right[i];
                a += left[i] * left[i];
                b += right[i] * right[i];
            }
            if (a <= 0 || b <= 0) return 0;
            return Math.Max(0, Math.Min(1, dot / (Math.Sqrt(a) * Math.Sqrt(b))));
        }

        private static string GetCachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "knowledge-embeddings.json");
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static void LogFailure(string message)
        {
            if (DateTime.Now - _lastFailureLogAt < TimeSpan.FromMinutes(5)) return;
            _lastFailureLogAt = DateTime.Now;
            Log.Info(message);
        }
    }
}
