using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisionRequestResult
    {
        public bool Success { get; set; }
        public string Answer { get; set; }
        public string Error { get; set; }
        public string EndpointName { get; set; }
        public string VisionModel { get; set; }
        public long LatencyMs { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public string MatchedVisualKnowledgeId { get; set; }
        public double VisualKnowledgeScore { get; set; }
        public string LocalOcrText { get; set; }
        public double LocalOcrConfidence { get; set; }
        public long LocalOcrLatencyMs { get; set; }
        public bool LocalOcrCacheHit { get; set; }
        public string LocalOcrEngine { get; set; }
    }

    internal sealed class VisionRequestService
    {
        private readonly VisionImageResolver _resolver = new VisionImageResolver();
        private const string UserPrompt =
            "你是淘宝/千牛客服视觉助手。请理解买家当前发送的图片，并结合当前会话上下文生成客服回复。"
            + "只描述图片中能够确认的内容，不要猜测模糊、遮挡或无法识别的信息；不要声称已经核实订单、账号、付款、充值或售后状态，除非上下文提供了明确数据。"
            + "只输出一个JSON对象，不要输出Markdown："
            + "{\"answer\":\"给买家的简短自然回复\",\"visual_question\":\"这类图片对应的通用问题\",\"visual_summary\":\"仅描述以后可用于匹配相似图片的稳定视觉特征，不包含买家个人信息\",\"visual_tags\":[\"商品或对象\",\"部位\",\"现象\",\"场景\"]}。"
            + "无论能否判断业务结论，visual_summary都不能为空；无法判断是否支持时，也要客观描述图片里可见的设备类型、应用名称、页面标题、按钮、二维码、提示文字或界面布局。"
            + "如果是电视应用界面，要尽量区分品牌官方APP/官方电视版与电视系统自带、第三方聚合或仿版；能从酷狗品牌、导航和页面结构确认是酷狗官方APP时，在visual_summary或visual_tags明确写出酷狗官方APP/酷狗音乐电视端。识别官方APP界面不要求买家必须已经登录账号。"
            + "visual_summary要具体到可区分不同图片场景，但不得保存手机号、订单号、账号、验证码等个人信息。";

        private const string StrictSemanticRepairPrompt =
            "\n\n【结构化输出修复】上一次响应没有提供可学习的visual_summary。"
            + "这一次必须只返回合法JSON，answer、visual_question、visual_summary、visual_tags四项都必须存在。"
            + "即使无法确认兼容性，也必须在visual_summary里描述图片实际可见的稳定界面特征；禁止只写‘无法确认’。";

        public async Task<VisionRequestResult> ExecuteAsync(VisionReplyTask task, CancellationToken cancellationToken)
        {
            // OCR-first runs before provider selection. Therefore a high-confidence local OCR +
            // Knowledge Engine V2 hit can answer without any configured or billable vision API.
            // A local miss is soft and preserves the existing vision route unchanged.
            var localDecision = await OcrFirstKnowledgeDecisionService.TryResolveAsync(task, cancellationToken);
            if (localDecision != null) return localDecision;

            var endpoints = AiEndpointStore.GetVisionEnabledEndpoints();
            if (endpoints.Count < 1) return Fail("未配置可用的视觉模型，且本地OCR未命中可安全直答的知识");

            var currentQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion)
                ? "[图片]"
                : task.CombinedQuestion.Trim();
            var timeline = ConversationContextStore.BuildTimelineText(task.SellerNick, task.BuyerNick, currentQuestion, 16);
            var prompt = UserPrompt + ConversationSessionLearningService.BuildReplyStylePromptAddon(task.SellerNick)
                + KugouReceptionPolicy.BuildPromptAddon(task.SellerNick, task.BuyerNick);
            if (!string.Equals(currentQuestion, "[图片]", StringComparison.Ordinal))
            {
                prompt += "\n\n买家本轮连续发送的消息如下，换行代表先后顺序。图片和这些文字属于同一轮，请合并理解后只回复一次：\n" + currentQuestion;
            }
            if (!string.IsNullOrWhiteSpace(timeline))
            {
                prompt += "\n\n以下是同一客服与同一买家按时间排序的最近对话。买家发送的图片可能是在回答最近一条客服问题，请结合时间线理解；不得混入其他买家信息：\n" + timeline;
            }

            VisionRequestResult last = null;
            foreach (var endpoint in endpoints)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(endpoint.VisionTimeoutSeconds));
                        var image = await _resolver.ResolveAsync(task.Message, endpoint, cts.Token);
                        if (!image.Success)
                        {
                            last = Fail(image.Error);
                            last.EndpointName = endpoint.Name;
                            last.VisionModel = endpoint.VisionModel;
                            if (!IsRetryable(image.Error)) break;
                            continue;
                        }

                        // Local OCR remains attached as evidence for the vision fallback. Because the
                        // OCR cache is SHA-256 keyed, this second lookup is normally a local cache hit.
                        var localOcr = await LocalOcrService.TryRecognizeAsync(image.LocalCachePath, cts.Token);
                        var requestPrompt = prompt + LocalOcrService.BuildPromptEvidence(localOcr);
                        if (localOcr.Success)
                        {
                            Log.Info("视觉请求附加本地OCR证据: seller=" + task.SellerNick
                                + ", buyer=" + task.BuyerNick
                                + ", chars=" + (localOcr.Text == null ? 0 : localOcr.Text.Length)
                                + ", confidence=" + localOcr.Confidence.ToString("0.000")
                                + ", cacheHit=" + localOcr.CacheHit);
                        }

                        var result = await CallVisionAsync(endpoint, image.ImageUrl, requestPrompt, cts.Token);
                        ApplyLocalOcr(result, localOcr);
                        if (result.Success && string.IsNullOrWhiteSpace(result.VisualSummary))
                        {
                            var originalAnswer = result.Answer;
                            Log.Info("视觉接口未返回结构化语义，开始一次同图JSON修复: seller=" + task.SellerNick
                                + ", buyer=" + task.BuyerNick + ", endpoint=" + endpoint.Name);
                            var repaired = await CallVisionAsync(
                                endpoint,
                                image.ImageUrl,
                                requestPrompt + StrictSemanticRepairPrompt,
                                cts.Token);
                            ApplyLocalOcr(repaired, localOcr);
                            if (repaired.Success && !string.IsNullOrWhiteSpace(repaired.VisualSummary))
                            {
                                if (string.IsNullOrWhiteSpace(repaired.Answer)) repaired.Answer = originalAnswer;
                                result = repaired;
                                Log.Info("视觉结构化语义修复成功: seller=" + task.SellerNick
                                    + ", buyer=" + task.BuyerNick + ", endpoint=" + endpoint.Name);
                            }
                            else
                            {
                                Log.Info("视觉结构化语义修复仍为空，本轮可回复但不会建立图片学习候选: seller="
                                    + task.SellerNick + ", buyer=" + task.BuyerNick + ", endpoint=" + endpoint.Name);
                            }
                        }

                        result.LatencyMs = sw.ElapsedMilliseconds;
                        result.EndpointName = endpoint.Name;
                        result.VisionModel = endpoint.VisionModel;
                        if (result.Success)
                        {
                            if (string.IsNullOrWhiteSpace(result.VisualQuestion)) result.VisualQuestion = currentQuestion;
                            var generatedAnswer = result.Answer;
                            VisualKnowledgeMatch learned;
                            if (!string.IsNullOrWhiteSpace(result.VisualSummary)
                                && VisualKnowledgeLearningService.TryFindMatch(
                                    task.SellerNick,
                                    result.VisualQuestion,
                                    result.VisualSummary,
                                    result.VisualTags,
                                    out learned))
                            {
                                result.Answer = learned.Answer;
                                result.MatchedVisualKnowledgeId = learned.KnowledgeId;
                                result.VisualKnowledgeScore = learned.Score;
                                Log.Info("视觉回复采用人工学习知识: seller=" + task.SellerNick
                                    + ", buyer=" + task.BuyerNick
                                    + ", knowledgeId=" + learned.KnowledgeId
                                    + ", score=" + learned.Score.ToString("0.00"));
                            }

                            VisualKnowledgeLearningService.RecordVisionAnalysis(
                                task.SellerNick,
                                task.BuyerNick,
                                task.Message,
                                task.MessageKey,
                                result.VisualQuestion,
                                result.VisualSummary,
                                result.VisualTags,
                                generatedAnswer);

                            string verifiedReply;
                            string policyReason;
                            if (KugouReceptionPolicy.TryResolveVisual(
                                task.SellerNick, task.BuyerNick, currentQuestion,
                                result.VisualSummary, result.LocalOcrText,
                                out verifiedReply, out policyReason))
                            {
                                result.Answer = verifiedReply;
                                Log.Info("业务设备证据覆盖普通视觉回答: seller=" + task.SellerNick
                                    + ", buyer=" + task.BuyerNick + ", decision=" + policyReason);
                            }

                            if (ConversationContextStore.IsWithdrawnAnswer(task.SellerNick, task.BuyerNick, result.Answer))
                            {
                                var blocked = new VisionRequestResult
                                {
                                    Success = false,
                                    Error = "该回复已被客服撤回，已阻止再次发送",
                                    EndpointName = endpoint.Name,
                                    VisionModel = endpoint.VisionModel,
                                    LatencyMs = result.LatencyMs
                                };
                                ApplyLocalOcr(blocked, localOcr);
                                return blocked;
                            }
                            var source = string.IsNullOrWhiteSpace(result.MatchedVisualKnowledgeId) ? "AI生成" : "视觉知识";
                            KnowledgeLearningService.RegisterAnswerSource(task.SellerNick, task.BuyerNick, currentQuestion, result.Answer, source);
                            if (!task.DeferLearningUntilDelivered && string.IsNullOrWhiteSpace(result.MatchedVisualKnowledgeId))
                            {
                                KnowledgeLearningService.QueueLearn(
                                    "买家本轮消息：" + currentQuestion + (string.IsNullOrWhiteSpace(timeline) ? string.Empty : "\n" + timeline),
                                    result.Answer,
                                    "视觉AI",
                                    task.SellerNick,
                                    task.BuyerNick);
                            }
                            return result;
                        }
                        last = result;
                        if (!IsRetryable(result.Error)) break;
                    }
                }
                catch (TaskCanceledException)
                {
                    last = Fail("视觉 API 超时");
                    last.EndpointName = endpoint.Name;
                    last.VisionModel = endpoint.VisionModel;
                }
                catch (Exception ex)
                {
                    last = Fail("视觉 API 异常：" + SafeText(ex.Message));
                    last.EndpointName = endpoint.Name;
                    last.VisionModel = endpoint.VisionModel;
                }
                finally
                {
                    sw.Stop();
                }
            }
            return last ?? Fail("所有视觉接口失败");
        }

        public static JObject BuildVisionPayload(AiEndpointConfig endpoint, string imageUrl, string prompt)
        {
            var systemPrompt = string.IsNullOrWhiteSpace(endpoint.SystemPrompt)
                ? "你是淘宝店铺客服助手。"
                : endpoint.SystemPrompt;
            systemPrompt += StorePromptProfileService.BuildVisionPromptAddon(prompt);
            return new JObject
            {
                ["model"] = endpoint.VisionModel,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = prompt },
                            new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = imageUrl } }
                        }
                    }
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 420,
                ["stream"] = false
            };
        }

        private async Task<VisionRequestResult> CallVisionAsync(AiEndpointConfig endpoint, string imageUrl, string prompt, CancellationToken token)
        {
            var payload = BuildVisionPayload(endpoint, imageUrl, prompt).ToString(Formatting.None);
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(endpoint.VisionTimeoutSeconds);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var response = await http.PostAsync(BuildChatUrl(endpoint.BaseUrl), content, token);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) return Fail("HTTP " + (int)response.StatusCode + " " + Classify(response.StatusCode) + "：" + SafeText(body));
                    var raw = ExtractAnswer(body).Trim();
                    if (string.IsNullOrWhiteSpace(raw)) return Fail("返回内容为空");
                    return ParseVisionResult(raw);
                }
            }
        }

        private static void ApplyLocalOcr(VisionRequestResult result, LocalOcrResult localOcr)
        {
            if (result == null || localOcr == null) return;
            result.LocalOcrText = localOcr.Text;
            result.LocalOcrConfidence = localOcr.Confidence;
            result.LocalOcrLatencyMs = localOcr.ElapsedMs;
            result.LocalOcrCacheHit = localOcr.CacheHit;
            result.LocalOcrEngine = localOcr.Engine;
        }

        private static VisionRequestResult ParseVisionResult(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            try
            {
                var start = raw.IndexOf('{');
                var end = raw.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    var obj = JObject.Parse(raw.Substring(start, end - start + 1));
                    var answer = Convert.ToString(obj["answer"]).Trim();
                    var visualQuestion = Convert.ToString(obj["visual_question"]).Trim();
                    var visualSummary = Convert.ToString(obj["visual_summary"]).Trim();
                    var tagsToken = obj["visual_tags"];
                    var visualTags = tagsToken is JArray
                        ? string.Join(",", ((JArray)tagsToken).Select(x => x.ToString().Trim()).Where(x => x.Length > 0))
                        : Convert.ToString(tagsToken).Trim();
                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        return new VisionRequestResult
                        {
                            Success = true,
                            Answer = answer,
                            VisualQuestion = visualQuestion,
                            VisualSummary = RedactVisualSemantic(visualSummary),
                            VisualTags = RedactVisualSemantic(visualTags)
                        };
                    }
                }
            }
            catch
            {
            }
            return new VisionRequestResult { Success = true, Answer = raw };
        }

        private static string RedactVisualSemantic(string text)
        {
            text = text ?? string.Empty;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\d)\d{8,}(?!\d)", "[编号]");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(验证码|校验码)[：:\s]*\d{4,8}", "$1：[已脱敏]");
            return text.Trim();
        }

        private static string BuildChatUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
        }

        private static string ExtractAnswer(string body)
        {
            var json = JObject.Parse(body);
            return json["choices"]?[0]?["message"]?["content"] == null ? string.Empty : json["choices"][0]["message"]["content"].ToString();
        }

        private static bool IsRetryable(string error)
        {
            error = error ?? string.Empty;
            return error.Contains("429") || error.Contains("500") || error.Contains("502") || error.Contains("503") || error.Contains("504") || error.Contains("超时") || error.Contains("下载失败");
        }

        private static string Classify(HttpStatusCode code)
        {
            var value = (int)code;
            if (value == 401) return "鉴权失败";
            if (value == 404) return "模型或路径不存在";
            if (value == 400) return "请求格式错误或模型不支持图片";
            if (value == 413) return "请求过大";
            if (value == 429) return "限流";
            if (value >= 500 && value <= 504) return "上游服务异常";
            return "视觉请求失败";
        }

        private static string SafeText(string text)
        {
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

        private static VisionRequestResult Fail(string error)
        {
            return new VisionRequestResult { Success = false, Error = error };
        }
    }
}