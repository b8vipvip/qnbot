from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    p = ROOT / path
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return p, raw.decode("utf-8-sig"), bom


def write(p, text, bom):
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    p.write_bytes(data)


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


# 1) BuyerSessionAgent generation token is the sole cancellation authority for streaming AI.
p, text, bom = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
text = replace_once(
    text,
    '''            var generationCts = new CancellationTokenSource();\n            generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));\n            var monitorCts = new CancellationTokenSource();\n            var monitor = MonitorLeaseAsync(lease, generationCts, monitorCts.Token);\n''',
    '''            // BuyerSessionAgent owns generation lifetime. Link the AI budget directly to that\n            // canonical token instead of polling lease.IsCurrent in a second lifecycle loop.\n            var generationCts = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);\n            generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));\n''',
    "stream linked generation token")
text = replace_once(
    text,
    '''            finally\n            {\n                monitorCts.Cancel();\n                try { await monitor.ConfigureAwait(false); } catch { }\n                monitorCts.Dispose();\n                generationCts.Dispose();\n            }\n''',
    '''            finally\n            {\n                generationCts.Dispose();\n            }\n''',
    "stream monitor cleanup removal")
text = replace_once(
    text,
    '''        private static async Task MonitorLeaseAsync(\n            BuyerMessageBurstLease lease,\n            CancellationTokenSource generationCts,\n            CancellationToken stopToken)\n        {\n            try\n            {\n                while (!stopToken.IsCancellationRequested && !generationCts.IsCancellationRequested)\n                {\n                    if (!lease.IsCurrent)\n                    {\n                        generationCts.Cancel();\n                        return;\n                    }\n                    await Task.Delay(80, stopToken);\n                }\n            }\n            catch (OperationCanceledException)\n            {\n            }\n        }\n\n''',
    '',
    "remove duplicate lease monitor")
if text.count("await response.Content.ReadAsStringAsync();") != 2:
    raise RuntimeError("expected two non-cancellable response body reads")
text = text.replace("await response.Content.ReadAsStringAsync();", "await ReadResponseBodyAsync(response.Content, linked.Token);")
helper_anchor = '''        private static bool TryExtractDelta(string data, out string delta)\n'''
helper = '''        private static async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken token)\n        {\n            if (content == null) return string.Empty;\n            token.ThrowIfCancellationRequested();\n            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))\n            using (var reader = new StreamReader(stream, Encoding.UTF8))\n            {\n                var result = new StringBuilder();\n                var buffer = new char[4096];\n                Task<int> pendingRead = null;\n                while (true)\n                {\n                    token.ThrowIfCancellationRequested();\n                    if (pendingRead == null) pendingRead = reader.ReadAsync(buffer, 0, buffer.Length);\n                    var completed = await Task.WhenAny(pendingRead, Task.Delay(120, token)).ConfigureAwait(false);\n                    if (completed != pendingRead)\n                    {\n                        token.ThrowIfCancellationRequested();\n                        continue;\n                    }\n\n                    var read = await pendingRead.ConfigureAwait(false);\n                    pendingRead = null;\n                    if (read <= 0) break;\n                    result.Append(buffer, 0, read);\n                }\n                return result.ToString();\n            }\n        }\n\n'''
text = replace_once(text, helper_anchor, helper + helper_anchor, "cancellable response reader")
write(p, text, bom)


# 2) Recovery/reconciliation owns replay suppression in one shared ledger.
p, text, bom = read("src/Bot/ChromeNs/BulkListManagementUiBridge.cs")
text = replace_once(text, "using BotLib;\n", "using Bot.ChatRecord;\nusing BotLib;\n", "bulk add chatrecord using")
ledger_anchor = '''    // The WebSocket/CDP transport can stay alive while Qianniu replaces an SDK object or one of\n'''
ledger = r'''    /// <summary>
    /// Single source-of-truth for recovery/reconciliation replay ownership. Live ingress keeps its
    /// normal business deduplicators; every recovery path must claim a physical message here before
    /// re-injecting it, so periodic watchdogs cannot repeatedly replay the same remote-history row.
    /// </summary>
    internal static class ConversationIngressRecoveryLedger
    {
        private const int MaxClaims = 20000;
        private static readonly TimeSpan ClaimRetention = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, DateTime> Claims =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static int _cleanupTick;

        public static bool TryClaim(
            string seller,
            QNChatMessage message,
            string rawFallback,
            out string claimKey)
        {
            claimKey = BuildClaimKey(seller, message, rawFallback);
            if (claimKey.Length == 0) return true;
            var now = DateTime.UtcNow;
            if (!Claims.TryAdd(claimKey, now)) return false;
            Cleanup(now);
            return true;
        }

        public static void Release(string claimKey)
        {
            if (string.IsNullOrWhiteSpace(claimKey)) return;
            DateTime ignored;
            Claims.TryRemove(claimKey, out ignored);
        }

        private static string BuildClaimKey(string seller, QNChatMessage message, string rawFallback)
        {
            seller = (seller ?? string.Empty).Trim();
            var messageKey = message == null
                ? string.Empty
                : IncomingMessageSafety.BuildMessageKey(message, string.Empty);
            if (string.IsNullOrWhiteSpace(messageKey) && !string.IsNullOrWhiteSpace(rawFallback))
                messageKey = "raw:" + StableHash64(rawFallback).ToString("x16");
            return string.IsNullOrWhiteSpace(messageKey)
                ? string.Empty
                : seller + "#" + messageKey;
        }

        private static ulong StableHash64(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return hash;
        }

        private static void Cleanup(DateTime now)
        {
            if ((Interlocked.Increment(ref _cleanupTick) & 127) != 0 && Claims.Count <= MaxClaims) return;
            var cutoff = now - ClaimRetention;
            foreach (var pair in Claims)
            {
                if (pair.Value >= cutoff) continue;
                DateTime ignored;
                Claims.TryRemove(pair.Key, out ignored);
            }
            var overflow = Claims.Count - MaxClaims;
            if (overflow <= 0) return;
            foreach (var pair in Claims.OrderBy(x => x.Value).Take(overflow))
            {
                DateTime ignored;
                Claims.TryRemove(pair.Key, out ignored);
            }
        }
    }

'''
text = replace_once(text, ledger_anchor, ledger + ledger_anchor, "insert recovery ledger")
text = replace_once(
    text,
    '''            if (HasDispatchableResult(unread))\n            {\n                DispatchReceiveNewMsgCompat(cdp, unread.ToString(Formatting.None));\n                return true;\n            }\n''',
    '''            if (HasDispatchableResult(unread))\n            {\n                var unreadMessages = unread["result"] as JArray;\n                var claimedUnread = ClaimRecoveryMessages(qn, unreadMessages);\n                if (claimedUnread.Messages.Count > 0)\n                {\n                    try\n                    {\n                        var unreadPayload = (JObject)unread.DeepClone();\n                        unreadPayload["result"] = claimedUnread.Messages;\n                        DispatchReceiveNewMsgCompat(cdp, unreadPayload.ToString(Formatting.None));\n                    }\n                    catch\n                    {\n                        ReleaseRecoveryClaims(claimedUnread.ClaimKeys);\n                        throw;\n                    }\n                }\n                return true;\n            }\n''',
    "dedupe unread recovery")
text = replace_once(
    text,
    '''            if (recent.Count < 1) return true;\n\n            var payload = new JObject { ["result"] = recent };\n            DispatchReceiveNewMsgCompat(cdp, payload.ToString(Formatting.None));\n            Log.Info("业务入站缓存补偿已回灌最近候选消息: count=" + recent.Count + ", source=conversation-map");\n            return true;\n        }\n\n        private static bool HasDispatchableResult(JObject response)\n''',
    '''            if (recent.Count < 1) return true;\n\n            var claimedRecent = ClaimRecoveryMessages(qn, recent);\n            if (claimedRecent.Messages.Count < 1) return true;\n            try\n            {\n                var payload = new JObject { ["result"] = claimedRecent.Messages };\n                DispatchReceiveNewMsgCompat(cdp, payload.ToString(Formatting.None));\n                Log.Info("业务入站缓存补偿已回灌新增候选消息: count=" + claimedRecent.Messages.Count + ", source=conversation-map");\n            }\n            catch\n            {\n                ReleaseRecoveryClaims(claimedRecent.ClaimKeys);\n                throw;\n            }\n            return true;\n        }\n\n        private sealed class ClaimedRecoveryBatch\n        {\n            public readonly JArray Messages = new JArray();\n            public readonly List<string> ClaimKeys = new List<string>();\n        }\n\n        private static ClaimedRecoveryBatch ClaimRecoveryMessages(QN qn, JArray messages)\n        {\n            var batch = new ClaimedRecoveryBatch();\n            if (messages == null || messages.Count < 1) return batch;\n            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();\n            foreach (var token in messages)\n            {\n                if (token == null) continue;\n                QNChatMessage model = null;\n                try { model = token.ToObject<QNChatMessage>(); } catch { }\n                string claimKey;\n                if (!ConversationIngressRecoveryLedger.TryClaim(\n                    seller, model, token.ToString(Formatting.None), out claimKey)) continue;\n                batch.Messages.Add(token.DeepClone());\n                if (!string.IsNullOrWhiteSpace(claimKey)) batch.ClaimKeys.Add(claimKey);\n            }\n            return batch;\n        }\n\n        private static void ReleaseRecoveryClaims(IEnumerable<string> claimKeys)\n        {\n            foreach (var claimKey in claimKeys ?? Enumerable.Empty<string>())\n                ConversationIngressRecoveryLedger.Release(claimKey);\n        }\n\n        private static bool HasDispatchableResult(JObject response)\n''',
    "dedupe recent history recovery")
write(p, text, bom)


# 3) Active-conversation reconciliation shares the same ledger and reports only newly processed rows.
p, text, bom = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
text = replace_once(
    text,
    '''            if (candidates.Count == 0) return 0;\n            foreach (var message in candidates)\n            {\n                await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);\n                await Task.Delay(20).ConfigureAwait(false);\n            }\n            return candidates.Count;\n''',
    '''            if (candidates.Count == 0) return 0;\n            var processed = 0;\n            foreach (var message in candidates)\n            {\n                string claimKey;\n                if (!ConversationIngressRecoveryLedger.TryClaim(seller, message, string.Empty, out claimKey))\n                    continue;\n                try\n                {\n                    await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);\n                    processed++;\n                }\n                catch\n                {\n                    ConversationIngressRecoveryLedger.Release(claimKey);\n                    throw;\n                }\n                await Task.Delay(20).ConfigureAwait(false);\n            }\n            return processed;\n''',
    "active reconciliation shared ledger")
write(p, text, bom)


# Focused static regression tests.
test = ROOT / "tests/test_1291_runtime_authority_static.py"
test.write_text(r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_streaming_generation_uses_session_agent_token_directly():
    s = source("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "CreateLinkedTokenSource(lease.CancellationToken)" in s
    assert "MonitorLeaseAsync(" not in s
    assert "monitorCts" not in s
    assert "await response.Content.ReadAsStringAsync();" not in s
    assert "ReadResponseBodyAsync(response.Content, linked.Token)" in s


def test_buyer_alias_canonicalizes_qianniu_transport_prefix_in_one_place():
    s = source("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    assert 'CnTaobaoTransportPrefix = "cntaobao"' in s
    assert "CanonicalIdentity(left)" in s
    assert "CanonicalIdentity(right)" in s
    assert "StartsWith(CnTaobaoTransportPrefix" in s


def test_recovery_paths_share_one_source_replay_ledger():
    bulk = source("src/Bot/ChromeNs/BulkListManagementUiBridge.cs")
    active = source("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    assert "internal static class ConversationIngressRecoveryLedger" in bulk
    assert "ClaimRecoveryMessages(qn, unreadMessages)" in bulk
    assert "ClaimRecoveryMessages(qn, recent)" in bulk
    assert "ConversationIngressRecoveryLedger.TryClaim(seller, message" in active
    assert "return processed;" in active
    assert "已回灌新增候选消息" in bulk
''', encoding="utf-8")

print("runtime authority repair applied")
