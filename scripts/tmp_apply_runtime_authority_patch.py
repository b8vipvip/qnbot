from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def write(rel, content):
    (ROOT / rel).write_text(content, encoding="utf-8-sig")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def insert_before(text, marker, block, label):
    if block.strip() in text:
        return text
    idx = text.find(marker)
    if idx < 0:
        raise RuntimeError(f"{label}: marker not found")
    return text[:idx] + block + text[idx:]


def patch_order_guard():
    rel = "src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"
    text = read(rel)
    marker = "        public static bool TryGetLatestOrderSnapshot(string seller, string buyer, out OrderSnapshot snapshot)\n"
    block = r'''        /// <summary>
        /// Returns recent confirmed orders as independent per-order snapshots. CommerceContext uses
        /// this projection to select the order relevant to the current question instead of blindly
        /// inheriting whichever order happened to be observed most recently.
        /// </summary>
        public static List<OrderSnapshot> GetRecentOrderSnapshots(string seller, string buyer, int maxCount = 8)
        {
            var result = new List<OrderSnapshot>();
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return result;
            maxCount = Math.Max(1, Math.Min(20, maxCount));
            lock (Sync)
            {
                EnsureLoaded();
                CleanupInternal();
                foreach (var record in _state.Records
                    .Where(x => x != null
                        && Same(x.Seller, seller)
                        && Same(x.Buyer, buyer)
                        && x.Snapshot != null
                        && x.ObservedAt >= DateTime.Now.AddDays(-7))
                    .OrderByDescending(x => x.EventTime)
                    .ThenByDescending(x => x.ObservedAt)
                    .Take(maxCount))
                {
                    var clone = CloneSnapshot(record.Snapshot);
                    if (clone != null) result.Add(clone);
                }
            }
            return result;
        }

'''
    text = insert_before(text, marker, block, "order recent snapshots")
    write(rel, text)


def patch_commerce():
    rel = "src/Bot/ChromeNs/CommerceContextService.cs"
    text = read(rel)
    text = replace_once(
        text,
        "        public DateTime EvidenceTime { get; set; }\n",
        "        public DateTime EvidenceTime { get; set; }\n        public string SelectionReason { get; set; }\n",
        "commerce selection property")

    old = r'''            OrderSnapshot order;
            if (OrderGuidanceDeliveryGuard.TryGetLatestOrderSnapshot(seller, buyer, out order)
                && order != null)
            {
                ApplyOrder(result, order);
                return result;
            }

            ApplyConversationFallback(result, currentQuestion, turns, state);
            return result;
'''
    new = r'''            string selectionReason;
            var order = SelectRelevantOrderSnapshot(
                seller,
                buyer,
                currentQuestion,
                turns,
                state,
                out selectionReason);
            if (order != null)
            {
                ApplyOrder(result, order);
                result.SelectionReason = selectionReason;
                Log.Info("Commerce订单选择: sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", seller)
                    + ", buyerRef=" + MyWebSocketServer.DiagnosticRef("buyer", buyer)
                    + ", orderRef=" + MyWebSocketServer.DiagnosticRef("order", order.OrderId)
                    + ", itemRef=" + MyWebSocketServer.DiagnosticRef("item", order.ItemId)
                    + ", skuRef=" + MyWebSocketServer.DiagnosticRef("sku", order.SkuId)
                    + ", phase=" + result.PurchasePhase
                    + ", tradeStatus=" + Safe(result.TradeStatus, 80)
                    + ", eventType=" + result.OrderEventType
                    + ", evidenceTime=" + result.EvidenceTime.ToString("o")
                    + ", selectionReason=" + selectionReason);
                return result;
            }

            result.SelectionReason = "no_structured_order";
            ApplyConversationFallback(result, currentQuestion, turns, state);
            return result;
'''
    text = replace_once(text, old, new, "commerce build selection")

    marker = "        public static void EnrichState(\n"
    block = r'''        internal static OrderSnapshot SelectRelevantOrderSnapshot(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns,
            ConversationStateSnapshot state,
            out string selectionReason)
        {
            selectionReason = "none";
            var candidates = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 12);
            if (candidates == null || candidates.Count == 0) return null;

            var current = Compact(currentQuestion);
            var recent = Compact(string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn)
                .OrderByDescending(x => x.Timestamp)
                .Take(12)
                .Select(x => x.Text ?? string.Empty)));
            var currentEntity = Compact(state == null ? string.Empty : state.CurrentEntity);

            OrderSnapshot selected = null;
            var selectedScore = int.MinValue;
            var selectedReason = "latest_confirmed_fallback";
            DateTime selectedEvidence = DateTime.MinValue;

            foreach (var candidate in candidates.Where(x => x != null))
            {
                var score = 0;
                var reasons = new List<string>();
                var orderId = Compact(candidate.OrderId);
                var itemId = Compact(candidate.ItemId);
                var skuId = Compact(candidate.SkuId);
                var title = Compact(candidate.ItemTitle);
                var skuText = Compact(candidate.SkuText);

                if (ContainsSignal(current, orderId, 6)) { score += 240; reasons.Add("current_order_id"); }
                else if (ContainsSignal(recent, orderId, 6)) { score += 150; reasons.Add("recent_order_id"); }

                if (ContainsSignal(current, skuId, 3)) { score += 150; reasons.Add("current_sku_id"); }
                else if (ContainsSignal(recent, skuId, 3)) { score += 90; reasons.Add("recent_sku_id"); }

                if (ContainsSignal(current, itemId, 3)) { score += 130; reasons.Add("current_item_id"); }
                else if (ContainsSignal(recent, itemId, 3)) { score += 75; reasons.Add("recent_item_id"); }

                if (ContainsSignal(current, title, 4)) { score += 95; reasons.Add("current_title"); }
                else if (ContainsSignal(recent, title, 4)) { score += 55; reasons.Add("recent_title"); }

                if (ContainsSignal(current, skuText, 3)) { score += 85; reasons.Add("current_sku_text"); }
                else if (ContainsSignal(recent, skuText, 3)) { score += 45; reasons.Add("recent_sku_text"); }

                if (currentEntity.Length >= 3
                    && (ContainsSignal(title, currentEntity, 3) || ContainsSignal(skuText, currentEntity, 3)
                        || ContainsSignal(currentEntity, title, 4) || ContainsSignal(currentEntity, skuText, 3)))
                {
                    score += 100;
                    reasons.Add("current_entity");
                }

                var statusText = Compact((candidate.TradeStatus ?? string.Empty) + " " + (candidate.EventText ?? string.Empty));
                var asksAfterSale = Regex.IsMatch(current, "退款|退货|售后|关闭|取消|refund|closed");
                var isAfterSale = candidate.EventType == OrderEventType.RefundRequested
                    || candidate.EventType == OrderEventType.Closed
                    || Regex.IsMatch(statusText, "退款|退货|关闭|取消|refund|closed");
                if (asksAfterSale && isAfterSale)
                {
                    score += 70;
                    reasons.Add("after_sale_intent");
                }

                var evidence = candidate.EventTime == DateTime.MinValue ? candidate.DetectedAt : candidate.EventTime;
                if (selected == null || score > selectedScore || (score == selectedScore && evidence > selectedEvidence))
                {
                    selected = candidate;
                    selectedScore = score;
                    selectedEvidence = evidence;
                    selectedReason = reasons.Count == 0
                        ? "latest_confirmed_fallback"
                        : string.Join("+", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }

            selectionReason = selectedReason + ";score=" + Math.Max(0, selectedScore);
            return selected;
        }

        private static bool ContainsSignal(string haystack, string needle, int minLength)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
            if (needle.Length < minLength) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

'''
    text = insert_before(text, marker, block, "commerce selector")
    write(rel, text)


def patch_shop_auth():
    rel = "src/Bot/ShopScope/ShopTokenBindingService.cs"
    text = read(rel)
    marker = "    internal static class ShopTokenBindingService\n"
    prelude = r'''    internal sealed class ShopRuntimeCredentialResolution
    {
        public bool Success { get; set; }
        public bool TokenChanged { get; set; }
        public ShopContext Shop { get; set; }
        public string ServerUrl { get; set; }
        public string Token { get; set; }
        public string TokenFingerprint { get; set; }
        public string Error { get; set; }
    }

    internal static class ShopRuntimeCredentialAuthority
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static bool ResolveForCurrentShop(
            out ShopContext shop,
            out string serverUrl,
            out string token,
            out string error)
        {
            shop = ShopSettingsScope.Current;
            serverUrl = string.Empty;
            token = string.Empty;
            error = string.Empty;
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey))
            {
                error = "当前请求没有可确认的 ShopKey，已阻止控制面调用";
                return false;
            }

            var connection = new ShopControlPlaneConnectionStore(shop, Paths);
            serverUrl = connection.GetServerUrl();
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                error = "当前店铺没有可用的控制面地址";
                return false;
            }
            if (!connection.TryGetToken(out token, out error) || string.IsNullOrWhiteSpace(token))
            {
                error = string.IsNullOrWhiteSpace(error) ? "当前店铺没有可用的客户端令牌" : error;
                return false;
            }
            token = token.Trim();
            return true;
        }

        public static async Task<ShopRuntimeCredentialResolution> RecoverUnauthorizedAsync(
            ShopContext expectedShop,
            string rejectedToken)
        {
            var result = new ShopRuntimeCredentialResolution { Shop = expectedShop };
            if (expectedShop == null || string.IsNullOrWhiteSpace(expectedShop.ShopKey))
            {
                result.Error = "401恢复缺少当前 ShopKey";
                return result;
            }

            var ambient = ShopSettingsScope.Current;
            if (ambient == null || !string.Equals(ambient.ShopKey, expectedShop.ShopKey, StringComparison.Ordinal))
            {
                result.Error = "401恢复时活动 ShopKey 已变化，已阻止跨店凭据回退";
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", result=blocked_shop_scope_changed");
                return result;
            }

            var connection = new ShopControlPlaneConnectionStore(expectedShop, Paths);
            result.ServerUrl = connection.GetServerUrl();
            string refreshed;
            string error;
            if (!connection.TryGetToken(out refreshed, out error) || string.IsNullOrWhiteSpace(refreshed))
            {
                result.Error = string.IsNullOrWhiteSpace(error) ? "同店令牌重读失败" : error;
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", result=token_missing");
                return result;
            }

            refreshed = refreshed.Trim();
            result.Token = refreshed;
            result.TokenFingerprint = Fingerprint(refreshed);
            result.TokenChanged = !string.Equals(
                (rejectedToken ?? string.Empty).Trim(),
                refreshed,
                StringComparison.Ordinal);
            if (result.TokenChanged)
            {
                result.Success = true;
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", refreshedFingerprint=" + result.TokenFingerprint
                    + ", tokenChanged=true, result=retry_same_shop");
                return result;
            }

            var claim = await ShopTokenBindingService.ClaimAsync(expectedShop, refreshed, false).ConfigureAwait(false);
            result.Success = claim != null && claim.Success;
            result.Error = result.Success ? string.Empty : (claim == null ? "同店绑定验证没有返回结果" : claim.Error);
            Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                + ", refreshedFingerprint=" + result.TokenFingerprint
                + ", tokenChanged=false, claimSuccess=" + result.Success
                + ", conflict=" + (claim != null && claim.Conflict)
                + ", result=" + (result.Success ? "retry_same_shop" : "fail_closed"));
            return result;
        }

        private static string Fingerprint(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0) return "none";
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in token)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return "token#" + hash.ToString("x16").Substring(0, 10);
        }
    }

'''
    text = insert_before(text, marker, prelude, "shop auth authority")
    write(rel, text)


def patch_openai():
    rel = "src/Bot/ChromeNs/MyOpenAI.cs"
    text = read(rel)
    if "using Bot.ShopScope;" not in text:
        text = replace_once(text, "using BotLib;\n", "using BotLib;\nusing Bot.ShopScope;\n", "openai shop scope import")

    marker = "        private static async Task<string> ReadResponseBodyWithCancellationAsync(\n"
    helpers = r'''        private sealed class RuntimeEndpointAuthority
        {
            public bool IsControlPlane;
            public ShopContext Shop;
            public string Url;
            public string Token;
        }

        private static bool IsControlPlaneEndpoint(AiEndpointConfig endpoint)
        {
            return endpoint != null
                && (string.Equals(endpoint.Type, "服务端控制面", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(endpoint.Id, "control-plane-gateway", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryResolveRuntimeEndpointAuthority(
            AiEndpointConfig endpoint,
            out RuntimeEndpointAuthority authority,
            out string error)
        {
            authority = null;
            error = string.Empty;
            if (endpoint == null)
            {
                error = "AI接口配置为空";
                return false;
            }

            if (!IsControlPlaneEndpoint(endpoint))
            {
                authority = new RuntimeEndpointAuthority
                {
                    IsControlPlane = false,
                    Url = NormalizeBaseUrl(endpoint.BaseUrl),
                    Token = (endpoint.ApiKey ?? string.Empty).Trim()
                };
                return !string.IsNullOrWhiteSpace(authority.Token);
            }

            ShopContext shop;
            string serverUrl;
            string token;
            if (!ShopRuntimeCredentialAuthority.ResolveForCurrentShop(
                out shop,
                out serverUrl,
                out token,
                out error))
            {
                return false;
            }
            authority = new RuntimeEndpointAuthority
            {
                IsControlPlane = true,
                Shop = shop,
                Url = NormalizeBaseUrl(serverUrl.TrimEnd('/') + "/v1"),
                Token = token
            };
            return true;
        }

        private static bool TryRecoverControlPlaneUnauthorized(
            RuntimeEndpointAuthority rejected,
            out RuntimeEndpointAuthority recovered,
            out string error)
        {
            recovered = null;
            error = string.Empty;
            if (rejected == null || !rejected.IsControlPlane || rejected.Shop == null)
            {
                error = "当前接口不是可恢复的同店控制面调用";
                return false;
            }
            try
            {
                var result = ShopRuntimeCredentialAuthority
                    .RecoverUnauthorizedAsync(rejected.Shop, rejected.Token)
                    .GetAwaiter().GetResult();
                if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Token))
                {
                    error = result == null ? "同店401恢复没有返回结果" : result.Error;
                    return false;
                }
                recovered = new RuntimeEndpointAuthority
                {
                    IsControlPlane = true,
                    Shop = rejected.Shop,
                    Url = NormalizeBaseUrl(result.ServerUrl.TrimEnd('/') + "/v1"),
                    Token = result.Token
                };
                return true;
            }
            catch (Exception ex)
            {
                error = SafeError(ex.Message);
                return false;
            }
        }

'''
    text = insert_before(text, marker, helpers, "openai runtime authority helpers")

    start = text.index("        private static ApiCallResult CallChatCompletions(AiEndpointConfig endpoint, JArray messages)\n")
    end = text.index("        public static StructuredChatResult CallStructuredChat", start)
    new_method = r'''        private static ApiCallResult CallChatCompletions(AiEndpointConfig endpoint, JArray messages)
        {
            var sw = Stopwatch.StartNew();
            RuntimeEndpointAuthority authority;
            string authorityError;
            if (!TryResolveRuntimeEndpointAuthority(endpoint, out authority, out authorityError))
            {
                sw.Stop();
                return new ApiCallResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Error = "运行时凭据解析失败：" + SafeError(authorityError)
                };
            }

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
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, authority.Url))
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authority.Token);
                        if (authority.IsControlPlane && authority.Shop != null)
                            request.Headers.TryAddWithoutValidation("X-Shop-Key", authority.Shop.ShopKey);
                        request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");
                        using (var response = SharedHttp.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellation.Token).GetAwaiter().GetResult())
                        {
                            var body = ReadResponseBodyWithCancellationAsync(
                                response.Content, cancellation.Token).GetAwaiter().GetResult();
                            if (response.StatusCode == HttpStatusCode.Unauthorized
                                && authority.IsControlPlane
                                && attempt == 0)
                            {
                                RuntimeEndpointAuthority recovered;
                                string recoveryError;
                                if (TryRecoverControlPlaneUnauthorized(authority, out recovered, out recoveryError))
                                {
                                    authority = recovered;
                                    continue;
                                }
                                sw.Stop();
                                return new ApiCallResult
                                {
                                    Success = false,
                                    LatencyMs = sw.ElapsedMilliseconds,
                                    Error = "HTTP 401 Unauthorized，同店凭据恢复失败并已停止重试：" + SafeError(recoveryError),
                                    InputTokens = EstimateTokens(payloadText),
                                    TotalTokens = EstimateTokens(payloadText)
                                };
                            }

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

            sw.Stop();
            return new ApiCallResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Error = "控制面401恢复重试未得到终态结果",
                InputTokens = EstimateTokens(payloadText),
                TotalTokens = EstimateTokens(payloadText)
            };
        }

'''
    text = text[:start] + new_method + text[end:]

    start = text.index("        private static StructuredChatResult CallRawChatCompletions(\n            AiEndpointConfig endpoint,\n            JArray messages,\n            int maxTokens,\n            double temperature,\n            int timeoutSeconds,\n            CancellationToken cancellationToken,\n            bool chatProtocolOnly)\n")
    end = text.index("        public static string TestConnection(string baseUrl", start)
    new_raw = r'''        private static StructuredChatResult CallRawChatCompletions(
            AiEndpointConfig endpoint,
            JArray messages,
            int maxTokens,
            double temperature,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            bool chatProtocolOnly)
        {
            var sw = Stopwatch.StartNew();
            RuntimeEndpointAuthority authority;
            string authorityError;
            if (!TryResolveRuntimeEndpointAuthority(endpoint, out authority, out authorityError))
            {
                sw.Stop();
                return new StructuredChatResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Error = "运行时凭据解析失败：" + SafeError(authorityError)
                };
            }

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
                var effectiveTimeout = timeoutSeconds > 0
                    ? timeoutSeconds
                    : (endpoint.TimeoutSeconds <= 0 ? 60 : Math.Max(endpoint.TimeoutSeconds, 60));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using (var http = new HttpClient())
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    using (var request = new HttpRequestMessage(HttpMethod.Post, authority.Url))
                    {
                        http.Timeout = Timeout.InfiniteTimeSpan;
                        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                        deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authority.Token);
                        if (authority.IsControlPlane && authority.Shop != null)
                            request.Headers.TryAddWithoutValidation("X-Shop-Key", authority.Shop.ShopKey);
                        if (chatProtocolOnly)
                            request.Headers.TryAddWithoutValidation("X-QN-Allowed-Protocols", "chat");
                        request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");
                        using (var response = http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token).GetAwaiter().GetResult())
                        {
                            var body = ReadResponseBodyWithCancellationAsync(
                                response.Content, deadline.Token).GetAwaiter().GetResult();
                            if (response.StatusCode == HttpStatusCode.Unauthorized
                                && authority.IsControlPlane
                                && attempt == 0)
                            {
                                RuntimeEndpointAuthority recovered;
                                string recoveryError;
                                if (TryRecoverControlPlaneUnauthorized(authority, out recovered, out recoveryError))
                                {
                                    authority = recovered;
                                    continue;
                                }
                                sw.Stop();
                                return new StructuredChatResult
                                {
                                    Success = false,
                                    LatencyMs = sw.ElapsedMilliseconds,
                                    Error = "HTTP 401 Unauthorized，同店凭据恢复失败并已停止重试：" + SafeError(recoveryError),
                                    InputTokens = EstimateTokens(payloadText),
                                    TotalTokens = EstimateTokens(payloadText),
                                    Raw = body
                                };
                            }

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

            sw.Stop();
            return new StructuredChatResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Error = "控制面401恢复重试未得到终态结果", InputTokens = EstimateTokens(payloadText), TotalTokens = EstimateTokens(payloadText) };
        }

'''
    text = text[:start] + new_raw + text[end:]
    write(rel, text)


def patch_websocket():
    rel = "src/Bot/ChromeNs/MyWebSocketServer.cs"
    text = read(rel)
    marker = "        internal bool IsAuthoritativeSellerSession(string sellerNick, string sessionId)\n"
    block = r'''        private bool TryPromoteDuplicateSellerSession(string sellerNick, WebSocketSession session, string reason)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID) || sellerNick.Length == 0) return false;
            var sessionId = session.SessionID.Trim();
            string previousOwner = string.Empty;

            lock (_sellerSessionSync)
            {
                if (!_connectedSessions.ContainsKey(sessionId)) return false;
                string duplicateSeller;
                string mappedSeller;
                var knownDuplicate = _duplicateSellerSessions.TryGetValue(sessionId, out duplicateSeller)
                    && string.Equals(duplicateSeller, sellerNick, StringComparison.Ordinal);
                var knownSellerSession = _sessionSellers.TryGetValue(sessionId, out mappedSeller)
                    && string.Equals(mappedSeller, sellerNick, StringComparison.Ordinal);
                if (!knownDuplicate && !knownSellerSession) return false;

                _sellerSessions.TryGetValue(sellerNick, out previousOwner);
                if (string.Equals(previousOwner, sessionId, StringComparison.Ordinal)) return true;

                if (!string.IsNullOrWhiteSpace(previousOwner) && _connectedSessions.ContainsKey(previousOwner))
                {
                    _duplicateSellerSessions[previousOwner] = sellerNick;
                    _sessionSellers[previousOwner] = sellerNick;
                }

                _sellerSessions[sellerNick] = sessionId;
                _sessionSellers[sessionId] = sellerNick;
                string ignored;
                _duplicateSellerSessions.TryRemove(sessionId, out ignored);
                _standbyLoggedSessions.TryRemove(sessionId, out _);
                _sessionLastActivityUtc[sessionId] = DateTime.UtcNow;
                BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_sellerSessions.Count);
            }

            try
            {
                var qn = QN.FindExistingBySellerNick(sellerNick);
                if (qn != null) qn.CDP = GetOrCreateClient(session);
            }
            catch (Exception ex)
            {
                Log.Info("前台CDP权威切换后绑定客服实例失败: " + ex.Message);
            }

            Log.Info("千牛前台会话切换已提升standby为权威CDP: sellerRef=" + DiagnosticRef("seller", sellerNick)
                + ", previousSessionRef=" + DiagnosticRef("session", previousOwner)
                + ", activeSessionRef=" + DiagnosticRef("session", sessionId)
                + ", reason=" + (reason ?? "foreground"));
            return true;
        }

'''
    text = insert_before(text, marker, block, "websocket promoter")

    old = r'''                            else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs" || wMsg.Type == "onChatDlgActive")
                            {
                                string duplicateSeller;
                                if (!_duplicateSellerSessions.TryGetValue(session.SessionID, out duplicateSeller))
                                {
                                    Task.Run(() => TryInitSession(session, "event:" + wMsg.Type));
                                }
                            }
'''
    new = r'''                            else if (wMsg.Type == "onChatDlgActive")
                            {
                                string duplicateSeller;
                                if (_duplicateSellerSessions.TryGetValue(session.SessionID, out duplicateSeller))
                                {
                                    if (TryPromoteDuplicateSellerSession(duplicateSeller, session, "onChatDlgActive"))
                                    {
                                        var activeSeller = duplicateSeller;
                                        var activeBuyer = string.Empty;
                                        try
                                        {
                                            var activePayload = JObject.Parse(wMsg.Response ?? "{}");
                                            var payloadSeller = ReadJsonString(activePayload, "loginNick");
                                            if (!string.IsNullOrWhiteSpace(payloadSeller)
                                                && !string.Equals(payloadSeller, activeSeller, StringComparison.Ordinal))
                                            {
                                                Log.Info("前台CDP权威切换忽略不一致的payload sellerRef="
                                                    + DiagnosticRef("seller", payloadSeller)
                                                    + ", expectedSellerRef=" + DiagnosticRef("seller", activeSeller));
                                            }
                                            activeBuyer = ReadJsonString(activePayload, "conversationNick");
                                            if (string.IsNullOrWhiteSpace(activeBuyer))
                                                activeBuyer = ReadJsonString(activePayload, "buyerNick");
                                        }
                                        catch { }
                                        Task.Run(() => TryBindStatusConversation(session, activeSeller, activeBuyer));
                                    }
                                }
                                else
                                {
                                    Task.Run(() => TryInitSession(session, "event:onChatDlgActive"));
                                }
                            }
                            else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs")
                            {
                                string duplicateSeller;
                                if (!_duplicateSellerSessions.TryGetValue(session.SessionID, out duplicateSeller))
                                {
                                    Task.Run(() => TryInitSession(session, "event:" + wMsg.Type));
                                }
                            }
'''
    text = replace_once(text, old, new, "websocket foreground branch")
    write(rel, text)


def main():
    patch_order_guard()
    patch_commerce()
    patch_shop_auth()
    patch_openai()
    patch_websocket()
    print("runtime authority patches applied")


if __name__ == "__main__":
    main()
