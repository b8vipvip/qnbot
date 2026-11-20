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

    assert "evidenceIds.All(promptHumanMessageIds.Contains)" in parser
    assert "evidenceIds.All(promptAllMessageIds.Contains)" in parser
    assert 'GetOrCreatePromptIdentifier(promptIdentifiers, "msg", turn.MessageKey)' in transcript
    assert "Redact(Clean(x, 120))" not in transcript
    assert "KnowledgeId = record.Id" in parser
    assert 'promptId.StartsWith("knowledge_", StringComparison.Ordinal)' in parser
    assert "promptIdentifiers.PromptToSource.TryGetValue(promptId, out id)" in parser
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

    assert "ConcurrentDictionary<string, ShopGateEntry> CorpusAnalysisGates" in service
    assert "AddShopGateReference(CorpusAnalysisGates, shopKey)" in service
    assert "await entry.Gate.WaitAsync().ConfigureAwait(false)" in service
    assert "entry.References++;" in service
    assert "entry.References--;" in service
    assert "entry.References != 0" in service
    assert "ICollection<KeyValuePair<string, ShopGateEntry>>" in service
    assert "entry.Gate.Dispose();" in service
    assert "await AcquireShopGateAsync(session.ShopKey)" in finalize
    assert "corpusGateLease.Dispose();" in finalize
    assert "finally" in finalize
    assert finalize.index("await AcquireShopGateAsync(session.ShopKey)") < finalize.index("analysis = await AnalyzeAsync(")
    assert finalize.index("analysis = await AnalyzeAsync(") < finalize.index("AnalyzeKnowledgeCorpusAsync(")



def test_structured_chat_timeout_exception_retries_instead_of_bypassing_batch_retry():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]

    assert "catch (OperationCanceledException ex)" in batch
    assert 'var timeoutError = "timeout:" + Clean(ex.Message, 300);' in batch
    assert "failed.Error = timeoutError;" in batch
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

    assert "var retryCorpus = !corpusComplete && session.LearningRetryCount < 3" in finalize
    assert "session.LearningRetryCount++" in finalize
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
    assert "if (!hasMoreKnown)" in method
    assert "if (hasMore > 0)" in method
    assert 'history_empty_page_missing_has_more' in method
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



def test_history_response_missing_messages_field_is_not_treated_as_an_empty_terminal_page():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert "if (messagesToken == null || messagesToken.Type == JTokenType.Null)" in method
    assert 'result.ErrorCode = "history_page_missing_messages"' in method
    assert 'result.StopReason = "missing_messages"' in method
    assert "var messages = messagesToken.ToObject<List<QNChatMessage>>()" in method



def test_invalid_message_list_shape_is_not_coerced_to_a_complete_empty_page():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]

    assert "var messages = messagesToken.ToObject<List<QNChatMessage>>();" in method
    assert "if (messages == null)" in method
    assert 'result.ErrorCode = "history_page_invalid_messages"' in method



def test_scheduler_invariant_opens_shop_scoped_learning_circuit_immediately():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    retry = service[
        service.index("private static bool IsRetryableKnowledgeCorpusError"):
        service.index("private static async Task<JObject> AnalyzeAsync")
    ]
    assert "KnowledgeCorpusCircuitBreakers" in service
    assert "KnowledgeCorpusCircuitFailureThreshold = 3" in service
    assert "KnowledgeCorpusCircuitCooldown = TimeSpan.FromMinutes(5)" in service
    assert "KnowledgeCorpusCircuitBreakers.GetOrAdd(\n                shopKey" in retry
    assert "state.ConsecutiveFailures >= KnowledgeCorpusCircuitFailureThreshold" in retry
    assert "IsKnowledgeCorpusSchedulerInvariantError(error)" in retry
    assert 'error.Contains("scheduler invariant")' in retry
    assert 'error.Contains("already owns request")' in retry
    assert "TryGetKnowledgeCorpusCircuitOpenUntil" in retry
    assert "ai_upstream_circuit_open_until:" in service


def test_corpus_batch_loop_stops_after_shop_circuit_opens_and_keeps_checkpoint_pending():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    method = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]
    assert method.index("TryGetKnowledgeCorpusCircuitOpenUntil(session.ShopKey") < method.index(
        "AnalyzeKnowledgeCorpusBatchAsync("
    )
    assert 'result.Error = "ai_upstream_circuit_open_until:"' in method
    assert "session.ShopKey, batch, batchIndex + 1" in method
    assert "RegisterKnowledgeCorpusAiFailure(shopKey" in batch
    assert "GetKnowledgeCorpusCircuitRetryDelay(corpusCircuitOpenUntil)" in finalize
    assert 'report.CompletedAtTicks = 0;' in finalize


def test_primary_session_ai_502_is_requeued_instead_of_becoming_terminal_learning_failure():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]
    assert "RegisterKnowledgeCorpusAiFailure(session.ShopKey, aiException.Message" in finalize
    assert "if (IsRetryableKnowledgeCorpusError(ex.Message)" in finalize
    assert "session.LearningRetryCount < 3" in finalize
    assert "ai_upstream_retryable:" in finalize
    assert 'Requeue(session, "等待AI上游恢复后再次复盘", TimeSpan.FromMinutes(1))' in finalize
    assert 'report.CompletedAtTicks = 0;' in finalize


def test_open_ai_learning_circuit_cooldown_survives_application_restart():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryGetPersistedKnowledgeCorpusCircuitOpenUntil"):
        service.index("private static async Task<JObject> AnalyzeAsync")
    ]
    recovery = service[
        service.index("private static void ResumePending"):
        service.index("private static void CleanupReports")
    ]
    requeue = service[
        service.index("private static void Requeue"):
        service.index("private static void CompleteSkipped")
    ]
    snapshot = service[
        service.index("private static void SaveSnapshot"):
        service.index("private static ConversationSessionLearningReportEntity NewReport")
    ]

    assert 'const string prefix = "ai_upstream_circuit_open_until:"' in parser
    assert "DateTimeStyles.RoundtripKind" in parser
    assert "CultureInfo.InvariantCulture" in parser
    assert "return openUntilUtc > DateTime.UtcNow;" in parser
    assert "var circuitDelay = GetKnowledgeCorpusCircuitRetryDelay(circuitOpenUntil);" in recovery
    assert "if (circuitDelay > due) due = circuitDelay;" in recovery
    # The successful requeue path calls SaveSnapshot, which must preserve report.Error.
    assert "SaveSnapshot(session, waitingStatus);" in requeue
    assert "report.Error =" not in snapshot
    assert "var delay = retryDelay ?? TimeSpan.FromMinutes(1);" in requeue
    assert "Schedule(session, delay);" in requeue
    assert "Schedule(session, TimeSpan.FromMinutes(1));" not in requeue


def test_startup_restores_latest_unexpired_circuit_before_scheduling_all_sessions_for_that_shop():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    restore = service[
        service.index("private static void RestoreKnowledgeCorpusCircuitBreakers"):
        service.index("private static async Task<JObject> AnalyzeAsync")
    ]
    recovery = service[
        service.index("private static void ResumePending"):
        service.index("private static void CleanupReports")
    ]

    assert "string.IsNullOrWhiteSpace(report.ShopKey)" in restore
    assert "report.Error, out openUntilUtc" in restore
    assert 'ParseCoverage(report.InputCoverageJson)["knowledgeCorpusError"]' in restore
    assert "coverageDeadline > openUntilUtc" in restore
    assert "KnowledgeCorpusCircuitBreakers.GetOrAdd(\n                    report.ShopKey" in restore
    assert "if (openUntilUtc <= state.OpenUntilUtc) continue;" in restore
    assert "state.OpenUntilUtc = openUntilUtc;" in restore
    assert recovery.index("RestoreKnowledgeCorpusCircuitBreakers(allReports);") < recovery.index(
        "foreach (var report in reports)"
    )
    assert "allReports.Where(x => x.CompletedAtTicks == 0)" in recovery
    assert recovery.index("TryGetKnowledgeCorpusCircuitOpenUntil(session.ShopKey") < recovery.index(
        "Schedule(session, due);"
    )


def test_corpus_retry_classifier_recognizes_dotnet_transport_failures():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    retry = service[
        service.index("private static bool IsRetryableKnowledgeCorpusError"):
        service.index("private static async Task<JObject> AnalyzeAsync")
    ]

    for phrase in (
        "error occurred while sending",
        "underlying connection was closed",
        "connection reset",
        "connection refused",
        "no such host",
        "bad gateway",
        "service unavailable",
        "基础连接已经关闭",
        "远程名称无法解析",
    ):
        assert 'error.Contains("' + phrase + '")' in retry


def test_session_summary_retries_only_transient_upstream_failures_once():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analyze = service[
        service.index("private static async Task<JObject> AnalyzeAsync"):
        service.index("private static string BuildTranscript")
    ]

    assert "for (var attempt = 0; attempt < 2; attempt++)" in analyze
    assert "IsRetryableKnowledgeCorpusError(error)" in analyze
    assert "await Task.Delay(1200);" in analyze
    assert "return ParseObject(result.Answer);" in analyze
    assert "JSON/schema errors are deterministic input failures" in analyze
    assert "throw new InvalidOperationException(" in analyze
    assert "会话复盘AI请求重试后仍不可用:" in analyze
    assert "Clean(lastRetryableError, 500)" in analyze



def test_empty_history_page_without_explicit_hasmore_is_not_complete():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync"):
        context.index("private static bool TryReadInt")
    ]
    empty_page = method[
        method.index("if (messages.Count == 0)"):
        method.index("if (messages.Any(x => x == null))")
    ]
    assert "if (!hasMoreKnown)" in empty_page
    assert 'history_empty_page_missing_has_more' in empty_page
    assert 'empty_page_missing_has_more' in empty_page
    assert "result.IsComplete = true;" not in empty_page[:empty_page.index("if (hasMore > 0)")]
    assert "result.StopReason = \"server_end\";" in empty_page



def test_session_summary_preserves_retryable_upstream_error_for_shop_circuit_breaker():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analyze = service[
        service.index("private static async Task<JObject> AnalyzeAsync"):
        service.index("private static string BuildTranscript")
    ]
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult>")
    ]

    assert "var lastRetryableError = string.Empty;" in analyze
    assert analyze.count("lastRetryableError = error;") == 2
    assert "Clean(lastRetryableError, 500)" in analyze
    assert "会话复盘AI请求重试后仍不可用:" in analyze
    assert "RegisterKnowledgeCorpusAiFailure(session.ShopKey, aiException.Message" in finalize
    assert "IsRetryableKnowledgeCorpusError(ex.Message)" in finalize
    assert "ai_upstream_circuit_open_until:" in finalize



def test_checkpoint_resume_revalidates_candidate_batch_and_live_message_evidence():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    restore = service[
        service.index("var restoredCandidates = ParseSuggestions(priorCandidates)"):
        service.index("if (!canResume)", service.index("var restoredCandidates = ParseSuggestions(priorCandidates)"))
    ]

    assert "batches[batchIndex - 1].Any" in restore
    assert "result.CompletedBatchIndices.Contains(batchIndex - 1)" in restore
    assert "evidenceIds.All(humanMessageIds.Contains)" in restore
    assert "evidenceIds.All(allMessageIds.Contains)" in restore
    assert 'candidate.EvidenceType, "conversation_synthesis"' in restore
    assert 'candidate.Action != "update"' in restore
    assert "candidate.SourceFingerprint, result.SourceFingerprint" in restore


def test_ai_learning_confidence_must_be_finite_and_inside_zero_to_one():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static double ParseConfidence"):
        service.index("private static List<Suggestion> ParseSuggestions")
    ]
    batch = service[
        service.index("private static bool TryParseKnowledgeCorpusBatch"):
        service.index("private static bool IsRetryableKnowledgeCorpusError")
    ]
    suggestions = service[
        service.index("private static List<Suggestion> ParseSuggestions"):
        service.index("private static IEnumerable<Suggestion> DeduplicateSuggestions")
    ]
    can_apply = service[
        service.index("private static bool CanApply"):
        service.index("private static bool ContainsHighRisk")
    ]

    assert "NumberStyles.Float" in parser
    assert "CultureInfo.InvariantCulture" in parser
    assert "double.IsNaN(confidence)" in parser
    assert "double.IsInfinity(confidence)" in parser
    assert "confidence < 0" in parser
    assert "confidence > 1" in parser
    assert 'ParseConfidence(token["confidence"])' in batch
    assert 'ParseConfidence(item["confidence"])' in suggestions
    assert "double.IsNaN(suggestion.Confidence)" in can_apply
    assert "suggestion.Confidence > 1.0" in can_apply


def test_superseded_learning_session_keeps_open_shop_circuit_deadline_for_restart():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    requeue = service[
        service.index("private static void Requeue("):
        service.index("private static void CompleteSkipped")
    ]
    recovery = service[
        service.index("private static void ResumePending"):
        service.index("private static void CleanupReports")
    ]

    assert "TryGetPersistedKnowledgeCorpusCircuitOpenUntil(" in requeue
    assert 'ParseCoverage(report.InputCoverageJson)["knowledgeCorpusError"]' in requeue
    assert "report.Error = hasCircuitDeadline" in requeue
    assert '"requeue_conflict_new_session_active"' in requeue
    assert "RestoreKnowledgeCorpusCircuitBreakers(allReports);" in recovery
    assert "allReports.Where(x => x.CompletedAtTicks == 0)" in recovery


def test_learning_timeline_clone_preserves_native_identity_aliases_and_feed_provenance():
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    clone = bridge[
        bridge.index("private static ConversationContextTurn CloneTurn"):
        bridge.index("private static string ExtractMessageText")
    ]

    assert "NativeAliases = source.NativeAliases == null" in clone
    assert "source.NativeAliases.ToArray()" in clone
    assert "PreciseTimeTicks = source.PreciseTimeTicks" in clone
    assert "SeenLive = source.SeenLive" in clone
    assert "SeenHistory = source.SeenHistory" in clone



def test_corpus_prompt_uses_random_local_aliases_and_restores_real_evidence_ids_before_checkpointing():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analysis = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    helpers = service[
        service.index("private static PromptIdentifierContext RestorePromptIdentifierContext"):
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync")
    ]
    batch = service[
        service.index("private static async Task<KnowledgeCorpusBatchResult> AnalyzeKnowledgeCorpusBatchAsync"):
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages")
    ]
    prompt = service[
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages"):
        service.index("private static bool TryParseKnowledgeCorpusBatch")
    ]
    parser = service[
        service.index("private static bool TryParseKnowledgeCorpusBatch"):
        service.index("private static bool IsRetryableKnowledgeCorpusError")
    ]

    assert 'Guid.NewGuid().ToString("N")' in helpers
    assert "knowledgeCorpusPromptIdentifierMap" in analysis
    assert "SerializePromptIdentifierContext(promptIdentifiers)" in analysis
    assert "RestoreSourceEvidenceMessageIds(" in analysis
    assert "candidate.EvidenceMessageIds = restored" in helpers
    assert "promptAllMessageIds" in batch and "promptHumanMessageIds" in batch
    assert "evidenceIds.All(promptHumanMessageIds.Contains)" in parser
    assert "evidenceIds.All(promptAllMessageIds.Contains)" in parser
    assert "匿名message_id token" in prompt
    assert "随机匿名token" in prompt
    assert 'GetOrCreatePromptIdentifier(promptIdentifiers, "product", x)' in prompt
    assert 'GetOrCreatePromptIdentifier(promptIdentifiers, "source", record.SourceId)' in prompt
    assert '["intent"] = Redact(record.Intent)' in prompt
    assert '["predicate"] = Redact(record.Predicate)' in prompt
    assert "KnowledgeCorpusPromptVersion = 3" in service



def test_corpus_prompt_never_exposes_raw_knowledge_record_ids():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    helpers = service[
        service.index("private static PromptIdentifierContext RestorePromptIdentifierContext"):
        service.index("private static string GetOrCreatePromptIdentifier")
    ]
    prompt = service[
        service.index("private static JArray BuildKnowledgeCorpusBatchMessages"):
        service.index("private static bool TryParseKnowledgeCorpusBatch")
    ]
    parser = service[
        service.index("private static bool TryParseKnowledgeCorpusBatch"):
        service.index("private static bool IsRetryableKnowledgeCorpusError")
    ]

    assert "KnowledgeCorpusPromptVersion = 3" in service
    assert 'promptId.StartsWith("knowledge_", StringComparison.Ordinal)' in helpers
    assert '["id"] = GetOrCreatePromptIdentifier(promptIdentifiers, "knowledge", record.Id)' in prompt
    assert "匿名knowledge token" in prompt
    assert 'promptId.StartsWith("knowledge_", StringComparison.Ordinal)' in parser
    assert "promptIdentifiers.PromptToSource.TryGetValue(promptId, out id)" in parser
    assert "expected.TryGetValue(id, out record)" in parser
    assert "KnowledgeId = record.Id" in parser
    assert '["id"] = record.Id ?? string.Empty' not in prompt



def test_live_session_learning_rejects_mismatched_shop_scope():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    observe = service[
        service.index("public static void ObserveLiveMessage"):
        service.index("public static string BuildReplyStylePromptAddon")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in observe
    assert "shop.SellerNick, seller, StringComparison.Ordinal" in observe
    assert "ShopSettingsScope.Current.ShopKey, shop.ShopKey" in observe
    assert "拒绝卖家与当前ShopKey作用域不匹配" in observe



def test_history_and_knowledge_corpus_retry_budgets_are_independent_and_restart_persistent():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]
    recovery = service[
        service.index("private static void ResumePending()"):
        service.index("private static void CleanupReports()")
    ]
    snapshot = service[
        service.index("private static void SaveSnapshot"):
        service.index("private static ConversationSessionLearningReportEntity NewReport")
    ]

    assert "public int HistoryRetryCount;" in service
    assert "public int LearningRetryCount;" in service
    assert "session.HistoryRetryCount < 3" in finalize
    assert "session.HistoryRetryCount++" in finalize
    assert "session.LearningRetryCount < 3" in finalize
    assert "session.LearningRetryCount++" in finalize
    assert 'initialCoverage["historyRetryCount"] = session.HistoryRetryCount' in finalize
    assert 'initialCoverage["learningRetryCount"] = session.LearningRetryCount' in finalize
    assert 'retryCoverage["historyRetryCount"] = session.HistoryRetryCount' in finalize
    assert 'retryCoverage["learningRetryCount"] = session.LearningRetryCount' in finalize
    assert 'retryCoverage.Value<int?>("historyRetryCount")' in recovery
    assert 'retryCoverage.Value<int?>("learningRetryCount")' in recovery
    assert "HistoryRetryCount = historyRetryCount" in recovery
    assert "LearningRetryCount = learningRetryCount" in recovery
    assert 'coverage["historyRetryCount"] = session.HistoryRetryCount' in snapshot
    assert 'coverage["learningRetryCount"] = session.LearningRetryCount' in snapshot



def test_checkpoint_write_failure_cannot_promote_knowledge_v2_to_complete_or_enable_writes():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analyze = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    catch_block = analyze[
        analyze.index("catch (Exception ex)"):
        analyze.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "result.Complete = false;" in catch_block
    assert 'coverage["knowledgeCorpusComplete"] = false;' in catch_block
    assert "SaveReport(report);" in analyze
    assert "private const bool EnableAutomaticWrites = false;" in service
    assert "var retryCorpus = !corpusComplete && session.LearningRetryCount < 3;" in finalize
    assert "Requeue(session, \"等待Knowledge V2全库补偿重试\")" in finalize


def test_corpus_checkpoint_resume_requires_exact_source_transcript_prompt_and_batch_contract():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analyze = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    resume = analyze[
        analyze.index("var canResume ="):
        analyze.index("if (!canResume)")
    ]

    for required in (
        "knowledgeCorpusSourceFingerprint",
        "knowledgeCorpusTranscriptFingerprint",
        "KnowledgeCorpusBatchSize",
        "KnowledgeCorpusPromptVersion",
        "knowledgeCorpusSessionSummary",
        "knowledgeCorpusCompletedBatchIndices",
    ):
        assert required in resume
    assert "result.CompletedBatchIndices.Add(completedIndex)" in resume
    assert "batches[batchIndex - 1].Any" in resume
    assert "evidenceIds.All(humanMessageIds.Contains)" in resume
    assert "candidate.SourceFingerprint, result.SourceFingerprint" in resume


def test_pending_learning_recovery_only_resumes_durable_unfinished_reports_and_deduplicates_latest():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    recovery = service[
        service.index("private static void ResumePending()"):
        service.index("private static void CleanupReports")
    ]

    assert "allReports.Where(x => x.CompletedAtTicks == 0)" in recovery
    assert "order by UpdatedAtTicks desc" in recovery
    assert "var resumedKeys = new HashSet<string>(StringComparer.Ordinal);" in recovery
    assert "if (!resumedKeys.Add(key))" in recovery
    assert "superseded_pending_session_duplicate" in recovery
    assert "legacy_report_shop_scope_unavailable" in recovery
