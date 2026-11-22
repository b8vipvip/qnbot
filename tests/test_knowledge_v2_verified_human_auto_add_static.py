from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_new_knowledge_candidates_require_distinct_real_buyer_and_human_message_ids():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryParseKnowledgeNewCandidates"):
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate")
    ]
    evidence = service[
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate"):
        service.index("private static ConversationContextTurn FindUniqueTurnByMessageId")
    ]

    assert 'evidenceType != "manual_reply"' in parser
    assert "promptBuyerMessageIds.Contains(questionPromptId)" in parser
    assert "promptHumanMessageIds.Contains(answerPromptId)" in parser
    assert "promptIdentifiers.PromptToSource.TryGetValue(questionPromptId, out questionMessageId)" in parser
    assert "promptIdentifiers.PromptToSource.TryGetValue(answerPromptId, out answerMessageId)" in parser
    assert "buyerMessageIds.Contains(questionMessageId)" in parser
    assert "humanMessageIds.Contains(answerMessageId)" in parser
    assert 'questionTurn.Role, "user"' in evidence
    assert 'answerTurn.Role, "assistant"' in evidence
    assert "IsBotTurn(answerTurn, cards)" in evidence
    assert "questionTurn.Withdrawn || answerTurn.Withdrawn" in evidence


def test_ai_only_selects_message_ids_and_code_uses_exact_transcript_text():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    prompt = service[
        service.index("private static JArray BuildKnowledgeNewCandidateMessages"):
        service.index("private static bool TryParseKnowledgeNewCandidates")
    ]
    evidence = service[
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate"):
        service.index("private static ConversationContextTurn FindUniqueTurnByMessageId")
    ]

    assert "不得生成、改写或概括问题和答案文本" in prompt
    assert "question_message_id" in prompt and "answer_message_id" in prompt
    assert "var question = Redact(Clean(questionTurn.Text, 400));" in evidence
    assert "var answer = Redact(Clean(answerTurn.Text, 1200));" in evidence
    assert "Question = question" in evidence
    assert "Answer = answer" in evidence


def test_new_knowledge_requires_standalone_question_temporal_pair_and_safe_reusable_fact():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    evidence = service[
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate"):
        service.index("private static ConversationContextTurn FindUniqueTurnByMessageId")
    ]

    assert "confidence < 0.95" in evidence
    assert "answerTurn.Timestamp <= questionTurn.Timestamp" in evidence
    assert "TimeSpan.FromMinutes(30)" in evidence
    assert "questionTurn.PreciseTimeTicks > 0" in evidence
    assert "answerTurn.PreciseTimeTicks > 0" in evidence
    assert "another_buyer_message_interrupted_the_evidence_pair" in evidence
    assert "KnowledgeV2Semantics.IsContextDependent(question)" in evidence
    assert "string.IsNullOrWhiteSpace(parsed.Subject)" in evidence
    assert "ContainsHighRisk(question + \" \" + answer)" in evidence
    assert "KnowledgeV2Semantics.IsHighRisk(question + \" \" + answer)" in evidence
    assert "订单|物流|快递|发货|库存|现货|价格" in evidence


def test_verified_new_knowledge_is_a_narrow_write_path_while_general_ai_suggestions_stay_dry_run():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "private const bool EnableAutomaticWrites = false;" in service
    assert "private const bool EnableVerifiedCorpusNewKnowledgeWrites = true;" in service
    assert "if (!EnableAutomaticWrites)" in finalize
    assert "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge" in finalize
    assert "ApplyVerifiedCorpusNewKnowledge(" in finalize
    assert "ApplyVerifiedCorpusCorrections(" in finalize
    assert "newKnowledgeApplied" in finalize
    assert ".Concat(corpusAnalysis.NewCandidates)" in finalize


def test_new_knowledge_addition_is_shop_scoped_fingerprint_guarded_idempotent_and_reversible():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository[
        repository.index("public static bool TryCreateVerifiedSessionKnowledge("):
        repository.index("private static bool IsSafeAutomaticNewKnowledge")
    ]

    assert "ResolveShopRequired(seller)" in method
    assert "shop.ShopKey, expectedShopKey" in method
    assert "if (!KnowledgeEngineV2Service.IsEnabled(seller))" in method
    assert "lock (state.Sync)" in method
    assert "ComputeRepositoryCorpusFingerprint(currentRecords)" in method
    assert "currentFingerprint, expectedCorpusFingerprint" in method
    assert "priorEvidence != null" in method
    assert "exactMatches.Count > 0" in method
    assert "if (similar)" in method
    assert "predicateStrong" in method
    assert "subjectSimilarity >= 0.90" in method
    assert "SourceType = \"human_evidence_auto_learned\"" in method
    assert "Status = \"active\"" in method
    assert "Authority = 0.98" in method
    assert "SaveRecordsInTransaction(new List<object> { ToRow(record), revision });" in method
    assert "PreviousJson = JsonConvert.SerializeObject(tombstone)" in method
    assert "MirrorOneToLegacy(shop, record)" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)" in method
    assert "session_human_evidence_auto_add" in method
    assert "AutoLearningPersonalData.IsMatch(value)" in repository
    assert "API密钥|远程控制|下载并运行|执行命令" in repository
    assert "[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}" in repository


def test_new_auto_learned_source_is_normalized_as_production_approved_human_evidence():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    assert 'source.IndexOf("human_evidence_auto_learned", StringComparison.OrdinalIgnoreCase) >= 0' in bridge
    assert "if (IsExplicitHumanConfirmationSource(record.SourceType)) return false;" in bridge
    assert "record.Authority = Math.Max(record.Authority, 0.98);" in bridge
    assert "record.Confidence = Math.Max(record.Confidence, 0.94);" in bridge


def test_new_candidate_checkpoint_requires_matching_source_and_transcript_fingerprints():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analysis = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]

    assert 'priorCoverage.Value<bool?>("knowledgeCorpusNewCandidateAnalysisComplete") == true' in analysis
    assert 'priorCoverage.Value<int?>("knowledgeCorpusNewCandidatePromptVersion") == KnowledgeCorpusNewCandidatePromptVersion' in analysis
    assert 'priorCoverage["knowledgeCorpusNewCandidateSourceFingerprint"]' in analysis
    assert 'priorCoverage["knowledgeCorpusNewCandidateTranscriptFingerprint"]' in analysis
    assert 'coverage["knowledgeCorpusNewCandidateSuggestions"] = JArray.Parse(SerializeSuggestions(result.NewCandidates))' in analysis
    assert "AnalyzeKnowledgeNewCandidatesAsync(" in analysis


def test_new_adds_chain_the_expected_corpus_fingerprint_before_existing_corrections():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "ref expectedCurrentFingerprint, out newKnowledgeSkipped" in finalize
    assert "session, corpusAnalysis, expectedCurrentFingerprint, out corpusSkipped" in finalize
    assert finalize.index("ApplyVerifiedCorpusNewKnowledge(") < finalize.index("ApplyVerifiedCorpusCorrections(")
    assert "expectedCurrentFingerprint = nextFingerprint;" in service
    assert "expectedCurrentFingerprint = string.Empty;" in service


def test_structured_json_recovery_accepts_both_full_corpus_decisions_and_new_candidates():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TrySelectLearningObject"):
        service.index("private static List<string> ExtractBalancedJsonObjects")
    ]

    assert 'obj["decisions"] != null' in parser
    assert 'obj["candidates"] != null' in parser
