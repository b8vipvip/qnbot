using BotLib;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public class MyOpenAI
    {
        public static ChatClient ChatClient { get; set; }

        private static string systemPrompt;
        private static string lastConfigFingerprint;
        private static readonly HttpClient SharedHttp = CreateSharedHttpClient();

        private static HttpClient CreateSharedHttpClient()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            };
            var http = new HttpClient(handler);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
            return http;
        }

        private static string DefaultSystemPrompt
        {
            get
            {
                return "你是淘宝店铺客服助手。只回复买家当前问题，语气像真人客服，简短自然。不要编造库存、价格、物流、订单状态。遇到退款、投诉、差评、赔偿、订单隐私等高风险问题时，建议转人工客服确认。";
            }
        }

        private static string ReplyStyleGuard
        {
            get
            {
                return "\n\n固定回复规则：每次最多1句话，优先20到35个字，最多不超过60个字；不要连续说多个解决方案；不要反复感谢、不要过度客套、不要像机器人话术；不要重复称呼“亲”；买家只发数字、手机号、账号、型号、表情、嗯/好/好的/是的/谢谢时，必须先结合最近一轮客服提问理解其含义，不要脱离上下文扩展营销。";
            }
        }

        private static string TimelineGuard
        {
            get
            {
                return "\n\n会话时间线规则：后续 messages 中包含同一客服与同一买家的最近聊天记录，并带有本地时间。必须严格按时间顺序理解；买家仅回复一串数字、手机号、账号、验证码、型号或短字符时，优先关联时间上最近且尚待回答的客服问题。较长时间间隔后不要强行延续旧话题；不得引用其他买家的内容；被客服撤回的回复不属于有效上下文，也不得再次发送。";
            }
        }

        private static string HumanConversationGuard
        {
            get
            {
                return "\n\n真人客服式会话规则：买家可能把一句话拆成多条发送，换行表示同一轮消息的先后顺序。先判断这些消息是同一句拆分、补充信息、纠正前文、连续追问、重复催促、寒暄后提问、只回复数字/型号，还是多个相关问题；不要按每一行逐条作答，只发送一条合并后的自然回复。后一条明确纠正前文时，以后一条为准；同义重复和连续问号只回应一次；寒暄与实际问题同时出现时直接处理实际问题；买家说“好的、嗯、知道了、谢谢、解决了”时简短收尾，不重新讲方案；信息不足时只追问一个最关键的信息；多个相关问题可在一句到两句内合并处理，多个无关问题优先处理最新或最影响交易的一项，再自然询问另一项。生成答案后如果买家又发来新消息，旧答案应作废，结合新消息重新生成。看到[图片]、[视频]、[语音]、[表情]等占位符时，不得假装看懂未解析的内容。";
            }
        }

        private static string BuildSystemPrompt(string prompt)
        {
            var basePrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt.Trim();
            if (basePrompt.Contains("固定回复规则")) return basePrompt + TimelineGuard + HumanConversationGuard;
            return basePrompt + ReplyStyleGuard + TimelineGuard + HumanConversationGuard;
        }

        private static bool EnsureConfig()
        {
            try
            {
                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints.Count < 1) return false;

                var primary = endpoints.First();
                systemPrompt = BuildSystemPrompt(string.IsNullOrWhiteSpace(primary.SystemPrompt) ? Params.Robot.GetSystemPrompt() : primary.SystemPrompt);
                var featureFingerprint = BotFeatureStore.GetMessagePolicy().Tone + ":" + BotFeatureStore.GetKnowledgeBase().Count + ":" + BotFeatureStore.GetAutoReplyRules().Enabled;
                var fingerprint = string.Join("|", endpoints.Select(e => string.Format("{0}:{1}:{2}:{3}", e.Name, e.BaseUrl, e.TextModel, e.Enabled))) + "|" + featureFingerprint;
                if (fingerprint != lastConfigFingerprint)
                {
                    lastConfigFingerprint = fingerprint;
                    Log.Info("AI配置加载成功, endpointCount=" + endpoints.Count + ", primary=" + primary.Name + ", model=" + primary.TextModel + ", baseUrl=" + primary.BaseUrl);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return false;
            }
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://api.openai.com/v1";
            baseUrl = baseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return baseUrl;
            return baseUrl + "/chat/completions";
        }

        private static string SafeError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "未知错误";
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 500) msg = msg.Substring(0, 500) + "...";
            return msg;
        }

        private static async Task<string> ReadResponseBodyWithCancellationAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content == null) return string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(
                        chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    buffer.Write(chunk, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static JObject CreateMessage(string role, string content)
        {
            return new JObject
            {
                ["role"] = role,
                ["content"] = content ?? string.Empty
            };
        }

        private static string ExtractAnswer(string responseBody)
        {
            var json = JObject.Parse(responseBody);
            var content = json["choices"]?[0]?["message"]?["content"];
            if (content == null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            return content.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static int GetIntToken(JToken token)
        {
            if (token == null) return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Math.Max(1, text.Length / 2);
        }

        private static void FillUsage(ApiCallResult result, string payloadText, string answerText, string responseBody)
        {
            try
            {
                var json = JObject.Parse(responseBody);
                result.InputTokens = GetIntToken(json["usage"]?["prompt_tokens"]);
                result.OutputTokens = GetIntToken(json["usage"]?["completion_tokens"]);
                result.TotalTokens = GetIntToken(json["usage"]?["total_tokens"]);
            }
            catch
            {
            }

            if (result.InputTokens <= 0) result.InputTokens = EstimateTokens(payloadText);
            if (result.OutputTokens <= 0) result.OutputTokens = EstimateTokens(answerText);
            if (result.TotalTokens <= 0) result.TotalTokens = result.InputTokens + result.OutputTokens;
        }

        private static string CleanAnswer(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            answer = answer.Replace("\r", " ").Replace("\n", " ");
            while (answer.Contains("  ")) answer = answer.Replace("  ", " ");
            if (answer.Length > 80)
            {
                answer = answer.Substring(0, 80).TrimEnd('，', '。', '；', '、', ' ') + "。";
            }
            return answer;
        }

        private static ApiCallResult CallChatCompletions(AiEndpointConfig endpoint, JArray messages)
        {
            var sw = Stopwatch.StartNew();
            var url = NormalizeBaseUrl(endpoint.BaseUrl);
            var payload = new JObject
            {
                ["model"] = endpoint.TextModel,
                ["messages"] = messages,
                ["temperature"] = 0.15,
                ["max_tokens"] = 120
            };
            var payloadText = payload.ToString(Newtonsoft.Json.Formatting.None);

            try
            {
                var timeoutSeconds = endpoint.TimeoutSeconds <= 0 ? 35 : endpoint.TimeoutSeconds;
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");
                    using (var response = SharedHttp.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellation.Token).GetAwaiter().GetResult())
                    {
                        var body = ReadResponseBodyWithCancellationAsync(
                            response.Content, cancellation.Token).GetAwaiter().GetResult();
                        sw.Stop();
                        if (!response.IsSuccessStatusCode)
                        {
                            var failed = new ApiCallResult
                            {
                                Success = false,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Error = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "，接口返回：" + SafeError(body)
                            };
                            failed.InputTokens = EstimateTokens(payloadText);
                            failed.TotalTokens = failed.InputTokens;
                            return failed;
                        }

                        var answer = CleanAnswer(ExtractAnswer(body));
                        if (string.IsNullOrWhiteSpace(answer))
                        {
                            var empty = new ApiCallResult
                            {
                                Success = false,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Error = "HTTP 200，但未解析到 choices[0].message.content。原始返回：" + SafeError(body)
                            };
                            empty.InputTokens = EstimateTokens(payloadText);
                            empty.TotalTokens = empty.InputTokens;
                            return empty;
                        }

                        var ok = new ApiCallResult
                        {
                            Success = true,
                            Answer = answer,
                            Raw = body,
                            LatencyMs = sw.ElapsedMilliseconds
                        };
                        FillUsage(ok, payloadText, answer, body);
                        return ok;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ApiCallResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Error = SafeError(ex.Message),
                    InputTokens = EstimateTokens(payloadText),
                    TotalTokens = EstimateTokens(payloadText)
                };
            }
        }

        public static StructuredChatResult CallStructuredChat(JArray messages, int maxTokens, double temperature)
        {
            return CallStructuredChat(messages, maxTokens, temperature, 0, CancellationToken.None);
        }

        public static StructuredChatResult CallStructuredChat(JArray messages, int maxTokens, double temperature, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints.Count < 1)
            {
                return new StructuredChatResult { Success = false, Error = "请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。" };
            }
            var errors = new List<string>();
            foreach (var endpoint in endpoints)
            {
                var result = CallRawChatCompletions(endpoint, messages, maxTokens, temperature, timeoutSeconds, cancellationToken);
                BotRuntimeStats.RecordAiCall(endpoint, result.InputTokens, result.OutputTokens, result.Success, result.LatencyMs, result.Success ? "成功" : result.Error);
                endpoint.LastLatencyMs = result.LatencyMs;
                endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                if (result.Success) return result;
                var err = endpoint.Name + "：" + result.Error;
                errors.Add(err);
                Log.Error("AI结构化接口调用失败：" + err);
            }
            return new StructuredChatResult { Success = false, Error = string.Join("；", errors) };
        }

        private static StructuredChatResult CallRawChatCompletions(AiEndpointConfig endpoint, JArray messages, int maxTokens, double temperature)
        {
            return CallRawChatCompletions(endpoint, messages, maxTokens, temperature, 0, CancellationToken.None);
        }

        private static StructuredChatResult CallRawChatCompletions(AiEndpointConfig endpoint, JArray messages, int maxTokens, double temperature, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var url = NormalizeBaseUrl(endpoint.BaseUrl);
            var payload = new JObject
            {
                ["model"] = endpoint.TextModel,
                ["messages"] = messages,
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens <= 0 ? 2000 : maxTokens
            };
            var payloadText = payload.ToString(Newtonsoft.Json.Formatting.None);
            try
            {
                using (var http = new HttpClient())
                {
                    var effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : (endpoint.TimeoutSeconds <= 0 ? 60 : Math.Max(endpoint.TimeoutSeconds, 60));
                    http.Timeout = Timeout.InfiniteTimeSpan;
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                        request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");
                        using (var response = http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token).GetAwaiter().GetResult())
                        {
                            var body = ReadResponseBodyWithCancellationAsync(
                                response.Content, deadline.Token).GetAwaiter().GetResult();
                            sw.Stop();
                            if (!response.IsSuccessStatusCode)
                            {
                                return new StructuredChatResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Error = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "，接口返回：" + SafeError(body), InputTokens = EstimateTokens(payloadText), TotalTokens = EstimateTokens(payloadText), Raw = body };
                            }
                            var answer = ExtractAnswer(body);
                            if (string.IsNullOrWhiteSpace(answer))
                            {
                                return new StructuredChatResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Error = "HTTP 200，但未解析到 choices[0].message.content。原始返回：" + SafeError(body), InputTokens = EstimateTokens(payloadText), TotalTokens = EstimateTokens(payloadText), Raw = body };
                            }
                            var ok = new StructuredChatResult { Success = true, Answer = answer.Trim(), Raw = body, LatencyMs = sw.ElapsedMilliseconds };
                            FillUsage(ok, payloadText, answer, body);
                            return ok;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new StructuredChatResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Error = SafeError(ex.Message), InputTokens = EstimateTokens(payloadText), TotalTokens = EstimateTokens(payloadText) };
            }
        }

        public static string TestConnection(string baseUrl, string apiKey, string model, string prompt)
        {
            var endpoint = new AiEndpointConfig
            {
                Name = "测试接口",
                Type = "OpenAI兼容",
                BaseUrl = (baseUrl ?? string.Empty).Trim(),
                ApiKey = (apiKey ?? string.Empty).Trim(),
                Model = (model ?? string.Empty).Trim(),
                TextModel = (model ?? string.Empty).Trim(),
                SystemPrompt = prompt ?? string.Empty,
                Enabled = true,
                Priority = 1,
                TimeoutSeconds = 35
            };
            return TestConnection(endpoint);
        }

        public static string TestConnection(AiEndpointConfig endpoint)
        {
            try
            {
                if (endpoint == null) return "失败：接口配置为空。";
                endpoint.BaseUrl = (endpoint.BaseUrl ?? string.Empty).Trim();
                endpoint.ApiKey = (endpoint.ApiKey ?? string.Empty).Trim();
                endpoint.NormalizeVisionDefaults();
                endpoint.TextModel = (endpoint.TextModel ?? string.Empty).Trim();

                if (string.IsNullOrEmpty(endpoint.ApiKey)) return "失败：ApiKey 为空。";
                if (string.IsNullOrEmpty(endpoint.TextModel)) return "失败：Model 为空。";
                if (!string.IsNullOrEmpty(endpoint.BaseUrl)
                    && !endpoint.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !endpoint.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return "失败：BaseUrl 必须以 http:// 或 https:// 开头。";
                }

                var prompt = BuildSystemPrompt(endpoint.SystemPrompt);
                var messages = new JArray
                {
                    CreateMessage("system", prompt),
                    CreateMessage("user", "请只回复：连接测试成功")
                };
                var result = CallChatCompletions(endpoint, messages);
                endpoint.LastLatencyMs = result.LatencyMs;
                endpoint.LastTestTime = DateTime.Now;
                endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                if (!result.Success) return "失败：" + result.Error;
                return "成功：API 连接正常。模型回复：" + SafeError(result.Answer) + "，耗时 " + result.LatencyMs + "ms";
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return "失败：" + SafeError(ex.Message);
            }
        }

        private static string BuildOffHoursHandoffReply(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            var fallback = BotFeatureStore.ApplyOutputPolicy(decision.ReplyText);
            try
            {
                if (!EnsureConfig()) return fallback;
                var messages = new JArray
                {
                    CreateMessage("system",
                        "你是电商店铺的下班转人工助手。当前人工客服已下班。你只能礼貌告知人工客服不在线、工作时间，以及问题已记录或建议买家在上班时间联系；不得回答退款、投诉、赔偿、隐私、订单核验等具体高风险结论。回复一句到两句，禁止编造。"),
                    CreateMessage("user",
                        "人工客服工作时间：" + decision.WorkHoursText
                        + "\n触发原因：" + decision.Reason
                        + "\n买家问题：" + question)
                };
                foreach (var endpoint in AiEndpointStore.GetEnabledEndpoints())
                {
                    var result = CallChatCompletions(endpoint, messages);
                    BotRuntimeStats.RecordAiCall(endpoint, result.InputTokens, result.OutputTokens, result.Success, result.LatencyMs, result.Success ? "下班转人工回复成功" : result.Error);
                    if (result.Success && !string.IsNullOrWhiteSpace(result.Answer))
                    {
                        return BotFeatureStore.ApplyOutputPolicy(result.Answer);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("生成下班转人工回复失败，使用固定兜底话术：" + ex.Message);
            }
            return fallback;
        }

        public static string GetAnswer(string seller, string buyer, string question)
        {
            return GetAnswer(seller, buyer, question, false);
        }

        internal static string GetAnswer(
            string seller,
            string buyer,
            string question,
            bool deferLearningUntilDelivered)
        {
            try
            {
                string presetReply;
                if (ConversationContextStore.TryTakeProductLinkReply(seller, buyer, question, out presetReply))
                {
                    if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, presetReply))
                    {
                        return "错误：该预设回复已被客服撤回，未再次发送。";
                    }
                    Log.Info("商品链接使用本地预设回复，未调用AI接口。buyer=" + buyer);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, presetReply, "本地");
                    return presetReply;
                }

                if (string.IsNullOrWhiteSpace(question)) return "错误：买家消息为空，未调用AI。";

                KnowledgeBaseEntry contextualKnowledge = null;
                ContextualKnowledgeDecision contextualDecision = null;
                double contextualKnowledgeScore = 0;
                KnowledgeBaseEntry localKnowledge;
                double localScore;
                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
                {
                    var localAnswer = BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                    var contextDecision = KnowledgeContextualReplyService.Analyze(seller, buyer, question, localKnowledge);
                    if (!contextDecision.IsFollowUp)
                    {
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localAnswer, "本地");
                        Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));
                        return localAnswer;
                    }

                    contextualKnowledge = localKnowledge;
                    contextualDecision = contextDecision;
                    contextualKnowledgeScore = localScore;
                    Log.Info("命中本地知识库，但当前消息属于上下文续答，将基于知识库事实进行衔接改写。buyer="
                        + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00")
                        + ", reason=" + contextDecision.Reason);
                }

                var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
                if (manualDecision.Matched)
                {
                    HandoffNotificationService.QueueNotify(seller, buyer, question, manualDecision);
                    if (!manualDecision.AllowAutoReply)
                    {
                        return "错误：命中人工确认规则，未自动回复。" + manualDecision.ReplyText + " 原因：" + manualDecision.Reason;
                    }

                    var offHoursAnswer = manualDecision.UseAiReply
                        ? BuildOffHoursHandoffReply(seller, buyer, question, manualDecision)
                        : BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
                    var offHoursSource = "转人工回复";
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, offHoursAnswer, offHoursSource);
                    return offHoursAnswer;
                }

                if (!EnsureConfig())
                {
                    if (contextualKnowledge != null)
                    {
                        var fallback = KnowledgeContextualReplyService.BuildOfflineFallback(contextualDecision, contextualKnowledge);
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            fallback = BotFeatureStore.ApplyOutputPolicy(fallback);
                            KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fallback, "本地知识库上下文");
                            Log.Info("上下文知识回复使用本地安全兜底。buyer=" + buyer + ", answer=" + fallback);
                            return fallback;
                        }
                    }
                    return "错误：AI配置不完整，请检查 API接口 列表中的 BaseUrl / ApiKey / Model。";
                }
                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints.Count < 1) return "错误：没有可用的AI接口，请在设置-API接口中启用至少一个接口。";

                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 18);
                var contextForKnowledge = new StringBuilder(question);
                foreach (var turn in turns)
                {
                    if (contextForKnowledge.Length > 3500) break;
                    contextForKnowledge.Append(' ').Append(turn.Text);
                }
                var dynamicSystemPrompt = systemPrompt + BotFeatureStore.BuildPromptAddon(contextForKnowledge.ToString());
                dynamicSystemPrompt += RecentVisualContextService.BuildPromptAddon(seller, buyer, question);
                if (contextualKnowledge != null)
                {
                    dynamicSystemPrompt += KnowledgeContextualReplyService.BuildPromptAddon(contextualDecision, contextualKnowledge);
                }
                var messages = new JArray { CreateMessage("system", dynamicSystemPrompt) };

                foreach (var turn in turns)
                {
                    var time = turn.Timestamp == DateTime.MinValue ? "时间未知" : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                    var speaker = turn.Role == "assistant" ? "客服" : "买家";
                    messages.Add(CreateMessage(turn.Role, "[" + time + " " + speaker + "] " + turn.Text));
                }
                messages.Add(CreateMessage("user", "[当前消息 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 买家] " + question));

                var errors = new List<string>();
                foreach (var endpoint in endpoints)
                {
                    var result = CallChatCompletions(endpoint, messages);
                    BotRuntimeStats.RecordAiCall(endpoint, result.InputTokens, result.OutputTokens, result.Success, result.LatencyMs, result.Success ? "成功" : result.Error);
                    endpoint.LastLatencyMs = result.LatencyMs;
                    endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                    if (result.Success)
                    {
                        var finalAnswer = BotFeatureStore.ApplyOutputPolicy(result.Answer);
                        if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, finalAnswer))
                        {
                            return "错误：该回复已被客服撤回，已阻止再次发送。";
                        }
                        var answerSource = contextualKnowledge == null ? "AI生成" : "本地知识库上下文";
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, finalAnswer, answerSource);
                        if (contextualKnowledge == null)
                        {
                            if (!deferLearningUntilDelivered)
                            {
                                KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);
                            }
                        }
                        else
                        {
                            Log.Info("上下文知识回复生成成功。buyer=" + buyer
                                + ", knowledgeId=" + contextualKnowledge.Id
                                + ", score=" + contextualKnowledgeScore.ToString("0.00")
                                + ", answer=" + finalAnswer);
                        }
                        return finalAnswer;
                    }

                    var err = endpoint.Name + "：" + result.Error;
                    errors.Add(err);
                    Log.Error("AI接口调用失败：" + err);
                }

                return "错误：AI接口调用失败，未自动回复。" + string.Join("；", errors);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return "错误：AI接口调用失败，未自动回复。" + SafeError(ex.Message);
            }
        }

        public class ApiCallResult
        {
            public bool Success { get; set; }
            public string Answer { get; set; }
            public string Error { get; set; }
            public string Raw { get; set; }
            public int InputTokens { get; set; }
            public int OutputTokens { get; set; }
            public int TotalTokens { get; set; }
            public long LatencyMs { get; set; }
        }
    }

    public class StructuredChatResult : MyOpenAI.ApiCallResult
    {
    }
}
