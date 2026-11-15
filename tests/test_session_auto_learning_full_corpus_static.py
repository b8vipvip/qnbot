from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_session_learning_loads_and_batches_the_entire_shop_scoped_v2_corpus():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]

    assert "KnowledgeEngineV2Repository.LoadAll(session.Seller)" in method
    assert "using (ShopSettingsScope.Enter(shop))" not in method  # caller owns the exact scope
    assert ".OrderBy(x => x.Id ?? string.Empty, StringComparer.Ordinal)" in method
    assert "BuildKnowledgeCorpusBatches(records)" in method
    assert "result.BatchesCompleted == result.BatchCount" in method
    assert "result.FailedBatches == 0" in method
    assert "KnowledgeCorpusBatchSize = 18" in service


def test_corpus_batch_requires_exactly_one_decision_for_every_record_id():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryParseKnowledgeCorpusBatch"):
        service.index("private static bool IsRetryableKnowledgeCorpusError")
    ]

    assert 'decisions == null' in parser
    assert "decisions.Count != expected.Count" in parser
    assert "expected.TryGetValue(id, out record)" in parser
    assert "!seen.Add(id)" in parser
    assert "seen.Count != expected.Count" in parser
    assert 'action != "keep" && action != "update" && action != "skip"' in parser


def test_corpus_revision_drift_or_any_failed_batch_prevents_complete_status():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]

    assert "ComputeKnowledgeCorpusFingerprint(records)" in method
    assert "ComputeKnowledgeCorpusFingerprint(afterRecords)" in method
    assert "result.SourceFingerprint, result.AfterFingerprint" in method
    assert "result.BatchesCompleted == result.BatchCount" in method
    assert "&& result.FailedBatches == 0" in method
    assert 'coverage["knowledgeCorpusComplete"] = result.Complete;' in method
    assert 'candidate.ApplyMessage = "语料覆盖不完整或源版本发生变化' in method


def test_corpus_candidates_require_real_human_message_ids_and_are_bound_to_source_revision():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryParseKnowledgeCorpusBatch"):
        service.index("private static bool IsRetryableKnowledgeCorpusError")
    ]
    transcript = service[
        service.index("private static string BuildCorpusTranscript"):
        service.index("private static void CollectEvidenceMessageIds")
    ]

    assert "evidenceIds.All(humanMessageIds.Contains)" in parser
    assert "evidenceIds.All(allMessageIds.Contains)" in parser
    assert "KnowledgeId = record.Id" in parser
    assert "SourceFingerprint = sourceFingerprint" in parser
    assert "EvidenceMessageIds = evidenceIds" in parser
    assert "turn.MessageKey" in transcript
    assert "turn.NativeAliases" in transcript


def test_corpus_batches_retry_transient_502_503_and_record_model_protocol_metadata():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]
    retry = service[
        service.index("private static bool IsRetryableKnowledgeCorpusError"):
        service.index("private static async Task<JObject> AnalyzeAsync")
    ]
    my = read("src/Bot/ChromeNs/MyOpenAI.cs")

    assert "attempt < 2" in batch
    assert "IsRetryableKnowledgeCorpusError(rawError)" in batch
    assert "await Task.Delay(1200)" in batch
    assert "raw.EndpointName" in batch
    assert "raw.Model" in batch
    assert "raw.Protocol" in batch
    assert 'error.Contains("http 502")' in retry
    assert 'error.Contains("http 503")' in retry
    assert 'Protocol = "openai_chat_completions"' in my
    assert "public string EndpointName { get; set; }" in my


def test_session_learning_stays_dry_run_and_persists_live_coverage_progress():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "private const bool EnableAutomaticWrites = false;" in service
    assert "AnalyzeKnowledgeCorpusAsync(" in finalize
    assert "SaveReport(report);" in finalize
    assert '["knowledgeCorpusComplete"] = false' in finalize
    assert "allSuggestions = suggestions.Concat(corpusAnalysis.Candidates)" in finalize
    assert "corpusAnalysis.Complete" in finalize
    assert "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge" in finalize
    assert finalize.index("if (!EnableAutomaticWrites)") < finalize.index(
        "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge"
    )


def test_serialized_full_corpus_candidates_include_id_fingerprint_and_message_provenance():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    serializer = service[
        service.index("private static string SerializeSuggestions"):
        service.index("private static string Normalize")
    ]

    assert '["knowledge_id"] = x.KnowledgeId ?? string.Empty' in serializer
    assert '["source_fingerprint"] = x.SourceFingerprint ?? string.Empty' in serializer
    assert '["corpus_batch_index"] = x.CorpusBatchIndex' in serializer
    assert '["evidence_message_ids"] = JArray.FromObject' in serializer



def test_corpus_coverage_percentage_counts_records_not_batches_and_includes_provenance():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    prompt = service[
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages"):
        service.index("private static bool TryParseKnowledgeCorpusBatch")
    ]

    assert "result.RecordsReviewed += batch.Count" in method
    assert 'coverage["knowledgeCorpusRecordsReviewed"] = result.RecordsReviewed' in method
    assert "result.RecordsReviewed * 100.0 / result.RecordCount" in method
    assert '["source_type"] = Redact(record.SourceType)' in prompt
    assert '["correction_count"] = record.CorrectionCount' in prompt
    assert '["last_verified_at"]' in prompt
    assert "提示注入" in prompt



def test_full_corpus_ai_scans_are_serialized_per_shop_to_avoid_duplicate_fanout():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "ConcurrentDictionary<string, SemaphoreSlim> CorpusAnalysisGates" in service
    assert "CorpusAnalysisGates.GetOrAdd(" in finalize
    assert "await corpusGate.WaitAsync();" in finalize
    assert "finally" in finalize
    assert "corpusGate.Release();" in finalize



def test_structured_chat_timeout_exception_retries_instead_of_bypassing_batch_retry():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]

    assert "catch (OperationCanceledException ex)" in batch
    assert "failed.Error = \"timeout:\"" in batch
    assert batch.index("catch (OperationCanceledException ex)") < batch.index(
        "if (raw == null || !raw.Success"
    )
    assert "attempt == 0" in batch
    assert "await Task.Delay(1200)" in batch



def test_full_corpus_checkpoint_resumes_only_when_shop_corpus_and_transcript_are_unchanged():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "ComputeKnowledgeCorpusTranscriptFingerprint(" in method
    assert 'priorCoverage["knowledgeCorpusCompletedBatchIndices"] as JArray' in method
    assert 'priorCoverage["knowledgeCorpusCandidateSuggestions"] as JArray' in method
    assert 'priorCoverage.Value<int?>("knowledgeCorpusBatchSize") == KnowledgeCorpusBatchSize' in method
    assert 'priorCoverage.Value<int?>("knowledgeCorpusPromptVersion") == KnowledgeCorpusPromptVersion' in method
    assert "result.CompletedBatchIndices.Contains(batchIndex)" in method
    assert "result.CompletedBatchIndices.Sum(i => batches[i].Count)" in method
    assert "ParseSuggestions(priorCandidates)" in method
    assert 'coverage["knowledgeCorpusCandidateSuggestions"] = JArray.Parse(SerializeSuggestions(result.Candidates))' in method
    assert "RestoreStringSet(priorCoverage[\"knowledgeCorpusModels\"], result.Models)" in method
    assert "RestoreStringSet(priorCoverage[\"knowledgeCorpusProtocols\"], result.Protocols)" in method
    assert "RestoreStringSet(priorCoverage[\"knowledgeCorpusEndpointNames\"], result.Endpoints)" in method
    assert 'property.Name.StartsWith("knowledgeCorpus", StringComparison.Ordinal)' in finalize
    assert "property.Value.DeepClone()" in finalize


def test_checkpoint_candidates_restore_knowledge_and_evidence_provenance():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static List<Suggestion> ParseSuggestions"):
        service.index("private static IEnumerable<Suggestion> DeduplicateSuggestions")
    ]

    assert 'KnowledgeId = Clean(Convert.ToString(item["knowledge_id"]), 256)' in parser
    assert 'SourceFingerprint = Clean(Convert.ToString(item["source_fingerprint"]), 128)' in parser
    assert 'CorpusBatchIndex = Math.Max(0, item.Value<int?>("corpus_batch_index") ?? 0)' in parser
    assert 'item["evidence_message_ids"] is JArray' in parser
    assert "Distinct(StringComparer.Ordinal).ToList()" in parser



def test_restored_checkpoint_is_not_marked_complete_before_history_revalidation():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    preserve = finalize.index('property.Name.StartsWith("knowledgeCorpus", StringComparison.Ordinal)')
    reset = finalize.index('initialCoverage["knowledgeCorpusComplete"] = false;')
    save = finalize.index("report.InputCoverageJson = initialCoverage.ToString(Formatting.None)")
    assert preserve < reset < save



def test_incomplete_corpus_scans_remain_pending_and_retry_from_checkpoint_up_to_three_times():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]
    requeue = service[
        service.index("private static void Requeue("):
        service.index("private static void CompleteSkipped")
    ]

    assert "var retryCorpus = !corpusComplete && session.RetryCount < 3" in finalize
    assert "report.CompletedAtTicks = 0" in finalize
    assert "report.RetryCount = session.RetryCount" in finalize
    assert "Requeue(session, \"等待Knowledge V2全库补偿重试\")" in finalize
    assert "if (retryCorpus)" in finalize
    assert "string waitingStatus = \"等待聊天历史恢复后再次复盘\"" in requeue
    assert "SaveSnapshot(session, waitingStatus)" in requeue



def test_resume_reuses_the_exact_redacted_session_summary_used_by_completed_batches():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]

    assert "var corpusSessionSummary = Redact(Clean(sessionSummary, 1200))" in method
    assert 'priorCoverage["knowledgeCorpusSessionSummary"] != null' in method
    assert 'corpusSessionSummary = Convert.ToString(priorCoverage["knowledgeCorpusSessionSummary"])' in method
    assert 'coverage["knowledgeCorpusSessionSummary"] = corpusSessionSummary' in method
    assert "transcript, corpusSessionSummary, styleBefore, localFirst" in method
    assert "ComputeKnowledgeCorpusTranscriptFingerprint(\n                    transcript, styleBefore, localFirst)" in method



def test_remote_history_page_safety_limit_is_explicit_and_never_reported_as_complete():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    docs = read("docs/SESSION_AUTO_LEARNING.md")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert "LearningHistoryMaxPages = 200" in context
    assert "for (var page = 0; page < LearningHistoryMaxPages; page++)" in method
    assert 'result.ErrorCode = "history_page_limit_reached"' in method
    assert "if (!result.IsComplete && string.IsNullOrWhiteSpace(result.ErrorCode))" in method
    assert "最多尝试 200 页" in docs
    assert "标记历史覆盖不完整并跳过 AI 分析" in docs



def test_remote_history_never_infers_end_from_missing_hasmore_or_empty_page_with_more():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert 'var hasMoreKnown = resultObject != null' in method
    assert 'TryReadInt(resultObject["hasMore"], out hasMore)' in method
    assert "if (hasMoreKnown && hasMore > 0)" in method
    assert 'result.ErrorCode = "history_empty_page_with_more"' in method
    assert "if (hasMoreKnown && hasMore == 0)" in method
    assert "if (hasMore == 0 && messages.Count < LearningHistoryPageSize)" not in method
    assert "private static bool TryReadInt(JToken token, out int value)" in context
    assert "if (token == null || token.Type == JTokenType.Null) return false;" in context



def test_nonempty_history_page_without_any_valid_message_is_not_complete():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert 'result.ErrorCode = "history_page_no_valid_messages"' in method
    assert 'result.StopReason = "no_valid_messages"' in method
    assert "result.IsComplete = true;" not in method[
        method.index("if (oldest == null)"):
        method.index("var oldestTime")
    ]



def test_explicit_server_end_takes_precedence_over_repeated_final_cursor():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    server_end = method.index("if (hasMoreKnown && hasMore == 0)")
    cursor_stall = method.index('result.ErrorCode = "history_cursor_stalled"')
    assert server_end < cursor_stall



def test_history_page_with_null_message_entries_is_not_silently_counted_complete():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert "if (messages.Any(x => x == null))" in method
    assert 'result.ErrorCode = "history_page_contains_null_messages"' in method
    assert 'result.StopReason = "invalid_messages"' in method
    assert "foreach (var item in messages.OrderBy(GetSortValue))" in method
