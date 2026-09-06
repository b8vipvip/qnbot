from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8-sig")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 occurrence, found {count}")
    return text.replace(old, new, 1)

# 1) A stability probe must never mutate the lifecycle state.
path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
s = read(path)
s = replace_once(s,
'''        public async Task<bool> ConfirmStableAsync(int milliseconds)
        {
            try
            {
                await Task.Delay(Math.Max(0, milliseconds), CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (!IsCurrent) return false;
            return MarkReady("send_barrier_stable");
        }
''',
'''        public async Task<bool> ConfirmStableAsync(int milliseconds)
        {
            try
            {
                await Task.Delay(Math.Max(0, milliseconds), CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            // Read-only barrier: lifecycle ownership remains with the actual answer/send path.
            // A timing probe must never publish Ready before the answer is fully materialized.
            return IsCurrent && !CancellationToken.IsCancellationRequested;
        }
''', "ConfirmStableAsync")
write(path, s)

# 2) Make post-generation validation/dedup cancellation-aware, and allow authoritative local
# knowledge to remain immutable instead of silently invoking an AI rewrite after the V2 decision.
path = "src/Bot/ChromeNs/ReplyDeduplicationService.cs"
s = read(path)
s = replace_once(s,
'''        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            string aiFailureFallbackAnswer;
''',
'''        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            return EnsureDistinct(
                seller, buyer, question, candidateAnswer, CancellationToken.None, false);
        }

        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer,
            CancellationToken cancellationToken,
            bool preserveTrustedAnswer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preserveTrustedAnswer)
            {
                // Knowledge V2 has already made the production-authority decision. Do not run a
                // hidden AI validator/regenerator here: that would both mutate authoritative facts
                // and let a "local" reply outlive its BuyerSessionAgent generation.
                return new ReplyDeduplicationResult
                {
                    Answer = BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer),
                    PreviousAnswer = string.Empty,
                    Source = "权威知识V2",
                    Regenerated = false
                };
            }

            string aiFailureFallbackAnswer;
''', "EnsureDistinct overload")

s = replace_once(s,
'''            var aiFailureFallbackApplied = AiFailureKnowledgeFallbackService.TryResolve(
                seller,
''',
'''            cancellationToken.ThrowIfCancellationRequested();
            var aiFailureFallbackApplied = AiFailureKnowledgeFallbackService.TryResolve(
                seller,
''', "dedup initial cancellation")

s = replace_once(s,
'''                    var repaired = RegenerateInvalidAnswer(
                        seller, buyer, question, candidateAnswer, knowledge, validation);
''',
'''                    cancellationToken.ThrowIfCancellationRequested();
                    var repaired = RegenerateInvalidAnswer(
                        seller, buyer, question, candidateAnswer, knowledge, validation,
                        cancellationToken);
''', "validation regeneration token")

s = replace_once(s,
'''            var regenerated = result.Regenerated
                ? BuildSafeFallback(question)
                : Regenerate(seller, buyer, question, previousAnswer, knowledge);
''',
'''            cancellationToken.ThrowIfCancellationRequested();
            var regenerated = result.Regenerated
                ? BuildSafeFallback(question)
                : Regenerate(seller, buyer, question, previousAnswer, knowledge, cancellationToken);
''', "duplicate regeneration token")

s = replace_once(s,
'''            KnowledgeBaseEntry knowledge,
            AnswerValidationResult validation)
        {
            try
''',
'''            KnowledgeBaseEntry knowledge,
            AnswerValidationResult validation,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
''', "RegenerateInvalidAnswer signature")
# The previous replacement intentionally included the opening brace; remove the duplicate generated one.
s = s.replace('''            {
            {
                cancellationToken.ThrowIfCancellationRequested();
''', '''            {
                cancellationToken.ThrowIfCancellationRequested();
''', 1)

s = replace_once(s,
'''                var response = MyOpenAI.CallStructuredChat(
                    messages, 220, 0.10, 45, CancellationToken.None);
''',
'''                var response = MyOpenAI.CallStructuredChat(
                    messages, 220, 0.10, 45, cancellationToken);
''', "validator AI token")

s = replace_once(s,
'''            catch (Exception ex)
            {
                Log.Info("本店发送前答案校验重答失败：" + ex.Message);
''',
'''            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info("本店发送前答案校验重答失败：" + ex.Message);
''', "validator OCE")

s = replace_once(s,
'''            string previousAnswer,
            KnowledgeBaseEntry knowledge)
        {
            try
            {
''',
'''            string previousAnswer,
            KnowledgeBaseEntry knowledge,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
''', "Regenerate signature")

s = replace_once(s,
'''                var response = MyOpenAI.CallStructuredChat(
                    messages, 180, 0.35, 90, CancellationToken.None);
''',
'''                var response = MyOpenAI.CallStructuredChat(
                    messages, 180, 0.35, 90, cancellationToken);
''', "duplicate AI token")

s = replace_once(s,
'''            catch (Exception ex)
            {
                Log.Info("本店重复答案重新生成失败，使用安全兜底：" + ex.Message);
''',
'''            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info("本店重复答案重新生成失败，使用安全兜底：" + ex.Message);
''', "duplicate OCE")
write(path, s)

# 3) Streaming path: cancellation must cover the hidden validation/rewrite phase and Ready is
# published only after the fully materialized answer survives the stability/relevance gates.
path = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
s = read(path)
s = replace_once(s,
'''            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            answer = deduplication.Answer;

            if (!await lease.ConfirmStableAsync(180))
''',
'''            try
            {
                var deduplication = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    lease.CancellationToken,
                    false);
                answer = deduplication.Answer;
            }
            catch (OperationCanceledException)
            {
                const string cancelledDuringValidation = "generation在发送前答案校验/去重期间已失效";
                if (conversationCtl != null) conversationCtl.SetStatus(cancelledDuringValidation, false);
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, cancelledDuringValidation);
                return;
            }

            if (!await lease.ConfirmStableAsync(180))
''', "streaming token-aware dedup")

s = replace_once(s,
'''                Log.Info("并发旧答案已抑制: buyer=" + burst.BuyerNick + ", reason=" + relevanceReason);
                return;
            }

            var answerReadyAt = DateTime.Now;
''',
'''                Log.Info("并发旧答案已抑制: buyer=" + burst.BuyerNick + ", reason=" + relevanceReason);
                return;
            }

            if (!lease.MarkReady("streaming_answer_materialized"))
            {
                const string invalidAtReady = "generation在答案发布前已终止，迟到答案已丢弃";
                if (conversationCtl != null) conversationCtl.SetStatus(invalidAtReady, false);
                ResponseProgressTracker.Cancel(burst.SellerNick, burst.BuyerNick, invalidAtReady);
                return;
            }

            var answerReadyAt = DateTime.Now;
''', "streaming explicit ready")
write(path, s)

# 4) Knowledge V2: BuyerSessionAgent is the sole lifecycle/terminal authority. Remove the local
# duplicate 55s policy, never AI-rewrite an authoritative V2 answer, and bind final send to lease token.
path = "src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs"
s = read(path)
s = s.replace('''        private const int MaxDirectReplyAgeSeconds = 55;\n\n''', '', 1)

age_block = '''            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            if ((DateTime.Now - detectedAt).TotalSeconds > MaxDirectReplyAgeSeconds)
            {
                Log.ErrorWithMaxCount(
                    "Knowledge Engine V2迟到结果超过generation绝对年龄，已丢弃且禁止进入Ready/Sending: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration
                    + ", ageMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds),
                    50);
                return;
            }

            var autoSend = Params.Robot.GetIsAutoReply();
'''
s = replace_once(s, age_block,
'''            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
''', "remove V2 age authority")

s = replace_once(s,
'''            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                || (DateTime.Now - detectedAt).TotalSeconds > MaxDirectReplyAgeSeconds)
            {
                Log.Info("Knowledge Engine V2稳定性确认后generation失效/超龄，未发布答案: buyer="
''',
'''            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
            {
                Log.Info("Knowledge Engine V2稳定性确认后generation失效，未发布答案: buyer="
''', "V2 stable gate")

s = replace_once(s,
'''            var dedup = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer);
            answer = dedup.Answer;
            var readyAt = DateTime.Now;
''',
'''            ReplyDeduplicationResult dedup;
            try
            {
                dedup = ReplyDeduplicationService.EnsureDistinct(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    lease.CancellationToken,
                    true);
            }
            catch (OperationCanceledException)
            {
                Log.Info("Knowledge Engine V2答案发布前generation已终止，权威本地答案未进入UI/发送: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
            }
            answer = dedup.Answer;
            if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested
                || !lease.MarkReady("knowledge_v2_answer_materialized"))
            {
                Log.Info("Knowledge Engine V2答案完成后generation已终止，迟到本地答案已丢弃: buyer="
                    + burst.BuyerNick + ", generation=" + burst.SessionGeneration);
                return;
            }
            var readyAt = DateTime.Now;
''', "V2 trusted dedup and ready")

s = replace_once(s,
'''            if (!autoSend)
            {
                if (ctl != null) ctl.SetStatus("仅生成答案（Knowledge Engine V2本地命中）", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }
''',
'''            if (!autoSend)
            {
                lease.MarkCompleted("knowledge_v2_answer_generated_only");
                if (ctl != null) ctl.SetStatus("仅生成答案（Knowledge Engine V2本地命中）", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }
''', "V2 generated-only complete")

s = replace_once(s,
'''            var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            var failureReason = sendOk || qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason();
''',
'''            if (!lease.MarkSending("knowledge_v2_send_started"))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在V2发送前已失效");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge V2发送前generation已失效");
                return;
            }

            bool sendOk;
            try
            {
                sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick, answer, 1, lease.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在等待发送资源/会话确认期间已失效");
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge V2发送期间generation已失效");
                Log.Info("Knowledge Engine V2发送期间generation硬失效，已停止后续UI发送副作用: buyer="
                    + burst.BuyerNick);
                return;
            }
            var failureReason = sendOk || qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason();
''', "V2 token-bound send")

s = replace_once(s,
'''            Log.Info("Knowledge Engine V2本地直答完成: buyer=" + burst.BuyerNick
                + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
''',
'''            if (sendOk)
                lease.MarkCompleted("knowledge_v2_send_completed");
            else
                lease.MarkFailed("knowledge_v2_send_failed");
            Log.Info("Knowledge Engine V2本地直答完成: buyer=" + burst.BuyerNick
                + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
''', "V2 terminal transition")
write(path, s)

# 5) Passive remote-history recovery may still process order cards, but only an actually recovered
# buyer message counts as a missed business inbound. Seller/order echoes must not raise a false ERROR.
path = "src/Bot/Knowledge/KnowledgeCenterWindow.cs"
s = read(path)
s = replace_once(s,
'''                    await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);
                    processed++;
''',
'''                    await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);
                    // Order/system recovery remains useful, but the bridge's return value means
                    // "buyer business ingress was actually recovered". Do not count seller echoes
                    // that downstream dedupe correctly ignores.
                    if (IsBuyerMessage(message)) processed++;
''', "recovery count semantics")
write(path, s)

# 6) Regression coverage.
test = ROOT / "tests/test_1300_v2_terminal_authority_static.py"
test.write_text(r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_stability_barrier_is_read_only_and_ready_is_materialization_owned():
    burst = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    streaming = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    start = burst.index("public async Task<bool> ConfirmStableAsync")
    end = burst.index("public bool MarkProcessing", start)
    stable = burst[start:end]
    assert 'MarkReady("send_barrier_stable")' not in stable
    assert "return IsCurrent && !CancellationToken.IsCancellationRequested;" in stable
    assert 'lease.MarkReady("streaming_answer_materialized")' in streaming


def test_dedup_hidden_ai_respects_generation_cancellation():
    source = text("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    assert "CancellationToken cancellationToken" in source
    assert "bool preserveTrustedAnswer" in source
    assert "cancellationToken.ThrowIfCancellationRequested();" in source
    assert "messages, 220, 0.10, 45, cancellationToken" in source
    assert "messages, 180, 0.35, 90, cancellationToken" in source
    assert source.count("catch (OperationCanceledException)") >= 2


def test_v2_uses_session_agent_as_only_terminal_authority():
    source = text("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "MaxDirectReplyAgeSeconds" not in source
    assert 'lease.MarkReady("knowledge_v2_answer_materialized")' in source
    assert 'lease.MarkSending("knowledge_v2_send_started")' in source
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in source
    assert "true);" in source[source.index("ReplyDeduplicationService.EnsureDistinct"):source.index("ReplyDeduplicationService.EnsureDistinct") + 500]
    assert 'lease.MarkCompleted("knowledge_v2_send_completed")' in source
    assert 'lease.MarkFailed("knowledge_v2_send_failed")' in source


def test_recovery_reports_only_real_buyer_business_ingress():
    source = text("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    region = source[source.index("internal async Task<int> ReconcileActiveConversationIngressAsync"):]
    assert "if (IsBuyerMessage(message)) processed++;" in region
    assert "processed++;\n" not in region.replace("if (IsBuyerMessage(message)) processed++;\n", "")
''', encoding="utf-8")

print("repair applied")
