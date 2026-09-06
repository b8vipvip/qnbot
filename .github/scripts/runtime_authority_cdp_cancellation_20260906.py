from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8-sig")


def replace_once(path, old, new):
    text = read(path)
    if old not in text:
        raise SystemExit(f"anchor not found in {path}: {old[:120]!r}")
    text = text.replace(old, new, 1)
    write(path, text)


# 1) Move business-critical send authority out of ResponseProgressTracker/UI state.
order_path = "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
replace_once(order_path, "using System.Text.RegularExpressions;\nusing System.Threading.Tasks;", "using System.Text.RegularExpressions;\nusing System.Threading;\nusing System.Threading.Tasks;")
replace_once(
    order_path,
    "namespace Bot.ChromeNs\n{\n    internal sealed class OrderPlacedReplyPlan",
    '''namespace Bot.ChromeNs
{
    /// <summary>
    /// Async-flow scoped authority for a business side effect that has already been accepted by its
    /// own durable action ledger. This is intentionally separate from ResponseProgressTracker:
    /// UI/metrics state can disappear or be recreated and must never decide whether a business
    /// message keeps its send eligibility. The scope is inherited only by the exact async send call.
    /// </summary>
    internal static class ReliableSendAuthority
    {
        private sealed class ScopeState
        {
            public string Seller;
            public string Buyer;
            public string Text;
            public string Reason;
            public ScopeState Previous;
        }

        private sealed class ScopeLease : IDisposable
        {
            private readonly ScopeState _state;
            private readonly ScopeState _previous;
            private bool _disposed;

            public ScopeLease(ScopeState state, ScopeState previous)
            {
                _state = state;
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (ReferenceEquals(Current.Value, _state)) Current.Value = _previous;
            }
        }

        private static readonly AsyncLocal<ScopeState> Current = new AsyncLocal<ScopeState>();

        public static IDisposable BeginBusinessCritical(string seller, string buyer, string text, string reason)
        {
            var previous = Current.Value;
            var state = new ScopeState
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Text = (text ?? string.Empty).Trim(),
                Reason = (reason ?? string.Empty).Trim(),
                Previous = previous
            };
            Current.Value = state;
            return new ScopeLease(state, previous);
        }

        public static bool IsProtectedFromBuyerUpdate(string seller, string buyer, string text, out string reason)
        {
            reason = string.Empty;
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();

            // Text is intentionally diagnostic rather than the identity key: QN applies the
            // configured outbound suffix after the order layer opens this async scope. AsyncLocal
            // already constrains the authority to this one send call, while seller+buyer prevents a
            // nested send for another conversation from inheriting the privilege.
            for (var state = Current.Value; state != null; state = state.Previous)
            {
                if (!string.Equals(state.Seller, seller, StringComparison.Ordinal)) continue;
                if (!BuyerIdentityAliasService.AreEquivalent(seller, state.Buyer, buyer)) continue;
                reason = string.IsNullOrWhiteSpace(state.Reason) ? "business_critical" : state.Reason;
                return true;
            }
            return false;
        }
    }

    internal sealed class OrderPlacedReplyPlan''')
replace_once(
    order_path,
    '''                var sendStartedAt = DateTime.Now;
                KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);
                var sent = await SendTextWithRetryAsync(plan.Buyer, text, 0);
                if (sent) return true;''',
    '''                var sendStartedAt = DateTime.Now;
                bool sent;
                using (ReliableSendAuthority.BeginBusinessCritical(
                    plan.Seller,
                    plan.Buyer,
                    text,
                    plan.IsBuyerFollowUp ? "order_followup_action_ledger" : "order_action_ledger"))
                {
                    KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);
                    sent = await SendTextWithRetryAsync(plan.Buyer, text, 0);
                }
                if (sent) return true;''')

qnrpa_path = "src/Bot/ChromeNs/QNRpa.cs"
replace_once(
    qnrpa_path,
    '''            if (ResponseProgressTracker.IsMandatoryOrderAnswer(SellerNick, buyer, text))
            {
                Log.Info("下单固定预设受保护，买家后续消息不会取消本次优先发送: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage);
                return true;
            }''',
    '''            string authorityReason;
            if (ReliableSendAuthority.IsProtectedFromBuyerUpdate(
                SellerNick, buyer, text, out authorityReason))
            {
                Log.Info("业务可靠发送权限受保护，买家后续聊天不会取消已由业务动作ledger授权的本次发送: seller="
                    + SellerNick + ", buyer=" + buyer + ", stage=" + stage
                    + ", authority=" + authorityReason);
                return true;
            }''')
replace_once(
    qnrpa_path,
    '''            SetSendFailure(stage, "买家已发送更新消息，旧答案不会发送");
            CompleteAttemptLease(buyer, text);''',
    '''            SetSendCancellation(stage, "买家已发送更新消息，旧答案不会发送");
            CompleteAttemptLease(buyer, text);''')

tracker_path = "src/Bot/ChromeNs/ResponseProgressTracker.cs"
replace_once(
    tracker_path,
    '''        public static bool IsMandatoryOrderAnswer(string seller, string buyer, string answer)
        {
            DeliveryUiEntry ui;
            return DeliveryUi.TryGetValue(DeliveryKey(seller, buyer, answer), out ui)
                && ui != null
                && (ui.Source ?? string.Empty).IndexOf("下单自动回复", StringComparison.OrdinalIgnoreCase) >= 0;
        }

''',
    "")

# 2) Make HTTP response-body consumption explicitly cancellation-aware from headers to EOF.
ai_path = "src/Bot/ChromeNs/MyOpenAI.cs"
replace_once(ai_path, "using System.Diagnostics;\nusing System.Linq;", "using System.Diagnostics;\nusing System.IO;\nusing System.Linq;")
replace_once(ai_path, "using System.Threading;\n", "using System.Threading;\nusing System.Threading.Tasks;\n")
replace_once(
    ai_path,
    '''        private static string SafeError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "未知错误";
            msg = msg.Replace("\\r", " ").Replace("\\n", " ").Trim();
            if (msg.Length > 500) msg = msg.Substring(0, 500) + "...";
            return msg;
        }
''',
    '''        private static string SafeError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "未知错误";
            msg = msg.Replace("\\r", " ").Replace("\\n", " ").Trim();
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
''')
replace_once(
    ai_path,
    '''                    using (var response = SharedHttp.SendAsync(request, cancellation.Token).GetAwaiter().GetResult())
                    {
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();''',
    '''                    using (var response = SharedHttp.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellation.Token).GetAwaiter().GetResult())
                    {
                        var body = ReadResponseBodyWithCancellationAsync(
                            response.Content, cancellation.Token).GetAwaiter().GetResult();''')
replace_once(
    ai_path,
    '''                using (var http = new HttpClient())
                {
                    var effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : (endpoint.TimeoutSeconds <= 0 ? 60 : Math.Max(endpoint.TimeoutSeconds, 60));
                    http.Timeout = TimeSpan.FromSeconds(effectiveTimeout);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                    using (var content = new StringContent(payloadText, Encoding.UTF8, "application/json"))
                    {
                        var response = cancellationToken.CanBeCanceled ? http.PostAsync(url, content, cancellationToken).GetAwaiter().GetResult() : http.PostAsync(url, content).GetAwaiter().GetResult();
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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
                }''',
    '''                using (var http = new HttpClient())
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
                }''')

# 3) Keep duplicate/raw recovery WebSocket pages useful but bounded and reap stale silent sessions.
ws_path = "src/Bot/ChromeNs/MyWebSocketServer.cs"
replace_once(ws_path, "using System.Linq;\nusing System.Threading.Tasks;", "using System.Linq;\nusing System.Threading;\nusing System.Threading.Tasks;")
replace_once(
    ws_path,
    '''        private readonly ConcurrentDictionary<string, string> _duplicateSellerSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly object _sellerSessionSync = new object();''',
    '''        private readonly ConcurrentDictionary<string, string> _duplicateSellerSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, WebSocketSession> _liveSessions = new ConcurrentDictionary<string, WebSocketSession>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _sessionLastActivityUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _closingDuplicateSessions = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private readonly object _sellerSessionSync = new object();
        private const int MaxDuplicateSellerSessions = 3;
        private static readonly TimeSpan DuplicateSessionIdleTimeout = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan DuplicateSessionSweepInterval = TimeSpan.FromSeconds(45);
        private int _duplicateSessionSweeperStarted;''')
replace_once(
    ws_path,
    '''        private bool TryClaimSellerSession(string sellerNick, string sessionId)
        {''',
    '''        private void TouchSession(WebSocketSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID)) return;
            _liveSessions[session.SessionID] = session;
            _sessionLastActivityUtc[session.SessionID] = DateTime.UtcNow;
        }

        private void ScheduleDuplicateSessionClose(string sessionId, string sellerNick, string reason)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            if (!_closingDuplicateSessions.TryAdd(sessionId, true)) return;
            Task.Run(() =>
            {
                try
                {
                    WebSocketSession session;
                    if (!_liveSessions.TryGetValue(sessionId, out session) || session == null) return;
                    Log.Info("回收非权威千牛WebSocket页面通道: sellerRef=" + DiagnosticRef("seller", sellerNick)
                        + ", sessionRef=" + DiagnosticRef("session", sessionId)
                        + ", reason=" + reason);
                    session.Close();
                }
                catch (Exception ex)
                {
                    Log.Info("回收非权威千牛WebSocket页面通道失败: sessionRef="
                        + DiagnosticRef("session", sessionId) + ", error=" + ex.Message);
                    bool ignored;
                    _closingDuplicateSessions.TryRemove(sessionId, out ignored);
                }
            });
        }

        private void EnforceDuplicateSellerSessionCapLocked(string sellerNick)
        {
            var active = _duplicateSellerSessions
                .Where(x => string.Equals(x.Value, sellerNick, StringComparison.Ordinal)
                    && !_closingDuplicateSessions.ContainsKey(x.Key))
                .Select(x => x.Key)
                .OrderBy(x =>
                {
                    DateTime last;
                    return _sessionLastActivityUtc.TryGetValue(x, out last) ? last : DateTime.MinValue;
                })
                .ToList();
            var excess = active.Count - MaxDuplicateSellerSessions;
            if (excess <= 0) return;
            foreach (var victim in active.Take(excess))
                ScheduleDuplicateSessionClose(victim, sellerNick, "duplicate_cap");
        }

        private void StartDuplicateSessionSweeper()
        {
            if (Interlocked.CompareExchange(ref _duplicateSessionSweeperStarted, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(DuplicateSessionSweepInterval).ConfigureAwait(false);
                    try
                    {
                        var cutoff = DateTime.UtcNow - DuplicateSessionIdleTimeout;
                        List<KeyValuePair<string, string>> stale;
                        lock (_sellerSessionSync)
                        {
                            stale = _duplicateSellerSessions
                                .Where(x => !_closingDuplicateSessions.ContainsKey(x.Key))
                                .Where(x =>
                                {
                                    DateTime last;
                                    return !_sessionLastActivityUtc.TryGetValue(x.Key, out last) || last < cutoff;
                                })
                                .ToList();
                        }
                        foreach (var pair in stale)
                            ScheduleDuplicateSessionClose(pair.Key, pair.Value, "duplicate_idle_timeout");
                    }
                    catch (Exception ex)
                    {
                        Log.Info("千牛重复WebSocket页面通道清理异常: " + ex.Message);
                    }
                }
            });
        }

        private bool TryClaimSellerSession(string sellerNick, string sessionId)
        {''')
replace_once(
    ws_path,
    '''                    if (!string.IsNullOrWhiteSpace(owner) && _connectedSessions.ContainsKey(owner))
                    {
                        _duplicateSellerSessions[sessionId] = sellerNick;
                        return false;
                    }''',
    '''                    if (!string.IsNullOrWhiteSpace(owner) && _connectedSessions.ContainsKey(owner))
                    {
                        _duplicateSellerSessions[sessionId] = sellerNick;
                        EnforceDuplicateSellerSessionCapLocked(sellerNick);
                        return false;
                    }''')
replace_once(
    ws_path,
    '''                        _connectedSessions[session.SessionID] = true;
                        BotConnectionDiagnostics.RecordWebSocketConnect(session.SessionID);''',
    '''                        _connectedSessions[session.SessionID] = true;
                        TouchSession(session);
                        BotConnectionDiagnostics.RecordWebSocketConnect(session.SessionID);''')
replace_once(
    ws_path,
    '''                    try
                    {
                        var wMsg = JsonConvert.DeserializeObject<WSocketMessage>(value);''',
    '''                    try
                    {
                        TouchSession(session);
                        var wMsg = JsonConvert.DeserializeObject<WSocketMessage>(value);''')
replace_once(
    ws_path,
    '''                    _connectedSessions.TryRemove(session.SessionID, out _);
                    ReleaseSellerSession(session.SessionID);''',
    '''                    _connectedSessions.TryRemove(session.SessionID, out _);
                    WebSocketSession ignoredSession;
                    DateTime ignoredActivity;
                    bool ignoredClosing;
                    _liveSessions.TryRemove(session.SessionID, out ignoredSession);
                    _sessionLastActivityUtc.TryRemove(session.SessionID, out ignoredActivity);
                    _closingDuplicateSessions.TryRemove(session.SessionID, out ignoredClosing);
                    ReleaseSellerSession(session.SessionID);''')
replace_once(
    ws_path,
    '''                webSocket.Start();
                BotConnectionDiagnostics.RecordWebSocketServerStarted();''',
    '''                webSocket.Start();
                StartDuplicateSessionSweeper();
                BotConnectionDiagnostics.RecordWebSocketServerStarted();''')

# Regression tests.
test_path = ROOT / "tests/test_1296_runtime_authority_cdp_cancellation_static.py"
test_path.write_text('''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_business_send_authority_is_not_owned_by_progress_ui():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "internal static class ReliableSendAuthority" in order
    assert "AsyncLocal<ScopeState>" in order
    assert "BeginBusinessCritical(" in order
    assert "order_action_ledger" in order
    assert "ReliableSendAuthority.IsProtectedFromBuyerUpdate" in rpa
    assert "SetSendCancellation(stage, \"买家已发送更新消息，旧答案不会发送\")" in rpa
    assert "ResponseProgressTracker.IsMandatoryOrderAnswer" not in rpa
    assert "IsMandatoryOrderAnswer" not in tracker


def test_ai_response_body_inherits_generation_cancellation():
    ai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "ReadResponseBodyWithCancellationAsync" in ai
    assert "HttpCompletionOption.ResponseHeadersRead" in ai
    assert "stream.ReadAsync(" in ai
    assert "chunk, 0, chunk.Length, cancellationToken" in ai
    assert "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)" in ai
    assert "deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout))" in ai
    raw = ai[ai.index("private static StructuredChatResult CallRawChatCompletions(AiEndpointConfig endpoint, JArray messages, int maxTokens, double temperature, int timeoutSeconds"):]
    assert "response.Content.ReadAsStringAsync()" not in raw


def test_duplicate_websocket_recovery_channels_are_bounded_and_reaped():
    ws = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "MaxDuplicateSellerSessions = 3" in ws
    assert "DuplicateSessionIdleTimeout = TimeSpan.FromMinutes(4)" in ws
    assert "EnforceDuplicateSellerSessionCapLocked(sellerNick)" in ws
    assert "StartDuplicateSessionSweeper()" in ws
    assert "duplicate_cap" in ws
    assert "duplicate_idle_timeout" in ws
    assert "_liveSessions.TryRemove(session.SessionID" in ws
    assert "_sessionLastActivityUtc.TryRemove(session.SessionID" in ws
''', encoding="utf-8")

print("runtime authority/CDP/cancellation repair applied")
