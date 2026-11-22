from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ai_generates_complete_knowledge_content_and_requests_direct_production_publish():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    prompt = service[
        service.index("private static JArray BuildKnowledgeNewCandidateMessages"):
        service.index("private static bool TryParseKnowledgeNewCandidates")
    ]

    assert "knowledge_question" in prompt
    assert "knowledge_answer" in prompt
    assert "aliases" in prompt and "keywords" in prompt
    assert "category" in prompt and "product_feature" in prompt and "usage_guide" in prompt
    assert "AI必须主动整理" in prompt
    assert "不经过人工审批或灰度发布" in prompt
    assert '\\"aliases\\":[\\"同义问法\\"],\\"keywords\\"' in prompt
    assert "只返回消息ID" not in prompt
    assert "不得补充推测、承诺、未验证规格或外部常识" in prompt


def test_new_knowledge_still_requires_exact_real_buyer_and_non_bot_human_message_ids():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryParseKnowledgeNewCandidates"):
        service.index("private static bool TryCreateAiGeneratedKnowledgeCandidate")
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
    assert "TimeSpan.FromMinutes(30)" in evidence
    assert "another_buyer_message_interrupted_the_evidence_pair" in evidence


def test_ai_generated_question_answer_are_automatically_checked_against_source_evidence():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TryParseKnowledgeNewCandidates"):
        service.index("private static bool TryCreateAiGeneratedKnowledgeCandidate")
    ]
    generated = service[
        service.index("private static bool ApplyAiGeneratedKnowledgeContent"):
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate")
    ]

    assert "token[\"knowledge_question\"]" in parser
    assert "token[\"knowledge_answer\"]" in parser
    assert "TryCreateAiGeneratedKnowledgeCandidate(" in parser
    assert "TextSimilarity(sourceQuestion, candidateQuestion) >= 0.45" in generated
    assert "TextSimilarity(sourceAnswer, candidateAnswer) >= 0.45" in generated
    assert "HasKnowledgePolarityMismatch(sourceAnswer, candidateAnswer)" in generated
    assert "HasUnverifiedKnowledgeAnchors(sourceQuestion, candidateQuestion)" in generated
    assert "HasUnverifiedKnowledgeAnchors(sourceQuestion + \" \" + sourceAnswer, candidateAnswer)" in generated
    assert "var answer = answerGrounded ? candidateAnswer : sourceAnswer;" in generated
    assert "var question = questionGrounded ? candidateQuestion : sourceQuestion;" in generated
    assert "private static bool HasKnowledgePolarityMismatch(" in generated
    assert "private static bool HasUnverifiedKnowledgeAnchors(" in generated
    assert "|| !appearsInSource" in generated
    assert "ai_generated_answer_introduced_unverified_numeric_fact" in generated
    assert "ai_generated_answer_exceeds_human_evidence_scope" in generated
    assert "evidenceCandidate.Question = question;" in generated
    assert "evidenceCandidate.Answer = answer;" in generated
    assert "evidenceCandidate.Category = category;" in generated


def test_no_human_review_gate_for_safe_faq_auto_add_and_eight_candidates_can_publish():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    evidence = service[
        service.index("private static bool TryCreateVerifiedNewKnowledgeCandidate"):
        service.index("private static ConversationContextTurn FindUniqueTurnByMessageId")
    ]
    apply = service[
        service.index("private static int ApplyVerifiedCorpusNewKnowledge"):
        service.index("private static bool CanAutoApplyVerifiedNewKnowledge")
    ]

    assert "private const bool EnableVerifiedCorpusNewKnowledgeWrites = true;" in service
    assert "private const int MaxVerifiedNewKnowledgePerSession = 8;" in service
    assert "store_workflow_requires_human_review" not in evidence
    assert "RequiresWorkflowReview(policyCandidate)" not in evidence
    assert "KnowledgeEngineV2Repository.TryCreateVerifiedSessionKnowledge(" in apply
    assert "ApplyAiGeneratedKnowledgeContent(" in service
    assert "Status = \"active\"" in read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")


def test_repository_directly_publishes_ai_knowledge_with_aliases_and_reversible_transaction():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository[
        repository.index("public static bool TryCreateVerifiedSessionKnowledge("):
        repository.index("private static bool IsSafeAutomaticNewKnowledge")
    ]

    assert "string category" in method
    assert "IEnumerable<string> aliases" in method
    assert "IEnumerable<string> keywords" in method
    assert "normalizedKeywords" in method
    assert "var normalizedKeywords" in method
    assert "Concat(normalizedKeywords)" in method
    assert "ResolveShopRequired(seller)" in method
    assert "shop.ShopKey, expectedShopKey" in method
    assert "KnowledgeEngineV2Service.IsEnabled(seller)" in method
    assert "lock (state.Sync)" in method
    assert "ComputeRepositoryCorpusFingerprint(currentRecords)" in method
    assert "currentFingerprint, expectedCorpusFingerprint" in method
    assert "priorEvidence != null" in method
    assert "questionVariants.Any(compact" in method
    assert "exactMatches.All(x =>" in method
    assert "if (similar)" in method
    assert "Aliases = new[] { question }.Concat(normalizedAliases)" in method
    assert "SourceType = \"ai_auto_learned_human_evidence\"" in method
    assert "Type = knowledgeType" in method
    assert "Enabled = true" in method
    assert "Status = \"active\"" in method
    assert "SaveRecordsInTransaction(new List<object> { ToRow(record), revision });" in method
    assert "PreviousJson = JsonConvert.SerializeObject(tombstone)" in method
    assert "MirrorOneToLegacy(shop, record)" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)" in method
    assert "session_ai_auto_add" in method
    assert "recoveredExisting = true;" in method
    assert 'seller, recoveredExisting ? "session_ai_auto_add_recovered" : "session_ai_auto_add"' in method
    assert "自动发布不会重新启用或覆盖" in method
    assert "AutoLearningPersonalData.IsMatch(value)" in repository
    assert "API密钥|远程控制|下载并运行|执行命令" in repository


def test_ai_auto_published_source_is_not_misclassified_as_human_approved():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    explicit = bridge[
        bridge.index("public static bool IsExplicitHumanConfirmationSource"):
        bridge.index("public static bool IsAutoPublishedHumanEvidenceSource")
    ]

    assert "human_evidence_auto_learned" not in explicit
    assert "ai_auto_learned_human_evidence" in bridge
    assert "public static bool IsAutoPublishedHumanEvidenceSource" in bridge
    assert "private static bool IsTrustedProductionSource" in bridge
    assert "if (IsTrustedProductionSource(entry.SourceType)) return true;" in bridge
    assert "ApplyAutoPublished(record);" in bridge
    assert "if (IsTrustedProductionSource(record.SourceType)) return false;" in bridge
    assert "record.Authority = Math.Max(record.Authority, 0.90);" in bridge
    assert "record.Confidence = Math.Max(record.Confidence, 0.90);" in bridge


def test_generated_content_and_aliases_survive_checkpoint_restart_revalidation():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analysis = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]
    serializer = service[
        service.index("private static List<Suggestion> ParseSuggestions"):
        service.index("private static IEnumerable<Suggestion> DeduplicateSuggestions")
    ]

    assert 'priorCoverage.Value<int?>("knowledgeCorpusNewCandidatePromptVersion") == KnowledgeCorpusNewCandidatePromptVersion' in analysis
    assert 'priorCoverage["knowledgeCorpusNewCandidateSourceFingerprint"]' in analysis
    assert 'priorCoverage["knowledgeCorpusNewCandidateTranscriptFingerprint"]' in analysis
    assert "ApplyAiGeneratedKnowledgeContent(" in analysis
    assert "savedCandidate.Question" in analysis
    assert "savedCandidate.Answer" in analysis
    assert "savedCandidate.Aliases" in analysis
    assert 'item["aliases"] is JArray' in serializer
    assert '["aliases"] = JArray.FromObject(x.Aliases' in service


def test_new_knowledge_write_mode_is_direct_ai_publish_not_dry_run():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    initial = service[
        service.index('["writeMode"] = EnableVerifiedCorpusNewKnowledgeWrites'):
        service.index('["windowStart"]')
    ]

    assert "ai_auto_publish_new_knowledge_and_verified_updates" in initial
    assert '["newKnowledgeWriteMode"] = EnableVerifiedCorpusNewKnowledgeWrites ? "ai_auto_publish" : "dry_run"' in initial
    assert "MaxVerifiedNewKnowledgePerSession = 8" in service
    assert "newKnowledgeApplied" in service
    assert "knowledgeCorpusNewCandidateApplied" in service


def test_new_candidate_checkpoint_requires_matching_source_and_transcript_fingerprints():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    analysis = service[
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync"):
        service.index("private static List<List<KnowledgeV2Record>> BuildKnowledgeCorpusBatches")
    ]

    assert 'priorCoverage.Value<bool?>("knowledgeCorpusNewCandidateAnalysisComplete") == true' in analysis
    assert 'priorCoverage.Value<int?>("knowledgeCorpusNewCandidatePromptVersion") == KnowledgeCorpusNewCandidatePromptVersion' in analysis
    assert 'coverage["knowledgeCorpusNewCandidateSuggestions"] = JArray.Parse(SerializeSuggestions(result.NewCandidates))' in analysis
    assert "AnalyzeKnowledgeNewCandidatesAsync(" in analysis


def test_new_adds_chain_corpus_fingerprint_before_existing_corrections():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    finalize = service[
        service.index("private static async Task FinalizeAsync"):
        service.index("private static async Task<KnowledgeCorpusAnalysisResult> AnalyzeKnowledgeCorpusAsync")
    ]

    assert "ref expectedCurrentFingerprint, out newKnowledgeSkipped, out newKnowledgeRetryNeeded" in finalize
    assert "session, corpusAnalysis, expectedCurrentFingerprint, out corpusSkipped" in finalize
    assert finalize.index("ApplyVerifiedCorpusNewKnowledge(") < finalize.index("ApplyVerifiedCorpusCorrections(")
    assert "expectedCurrentFingerprint = nextFingerprint;" in service
    assert "expectedCurrentFingerprint = string.Empty;" in service


def test_structured_json_recovery_accepts_full_corpus_decisions_and_ai_candidates():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    parser = service[
        service.index("private static bool TrySelectLearningObject"):
        service.index("private static List<string> ExtractBalancedJsonObjects")
    ]

    assert 'obj["decisions"] != null' in parser
    assert 'obj["candidates"] != null' in parser

def test_ai_normalization_cannot_flip_human_answer_polarity_or_invent_known_product_anchors():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    generated = service.split("private static bool ApplyAiGeneratedKnowledgeContent(", 1)[1].split(
        "private static bool TryCreateVerifiedNewKnowledgeCandidate(", 1
    )[0]

    assert 'var negative = @"不支持|不兼容|不适配|不可以|不能|无法|不可用' in generated
    assert 'var positive = @"支持|兼容|适配|可以|能用|可用|允许|提供|包含' in generated
    assert "sourceNegative != generatedNegative" in generated
    assert "sourcePositive != generatedPositive" in generated
    assert "Android|安卓|iOS|苹果|iPhone|iPad|Windows|macOS|Linux|鸿蒙" in generated
    assert "return Regex.Matches(generatedText ?? string.Empty, anchors" in generated


def test_unverifiable_ai_rewrite_falls_back_to_the_exact_human_question_and_answer():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    generated = service.split("private static bool ApplyAiGeneratedKnowledgeContent(", 1)[1].split(
        "private static bool TryCreateVerifiedNewKnowledgeCandidate(", 1
    )[0]

    assert "var sourceQuestion = Redact(Clean(evidenceCandidate.Question, 400));" in generated
    assert "var sourceAnswer = StripAi(Redact(Clean(evidenceCandidate.Answer, 1200)));" in generated
    assert "var question = questionGrounded ? candidateQuestion : sourceQuestion;" in generated
    assert "var answer = answerGrounded ? candidateAnswer : sourceAnswer;" in generated
    assert "无法充分验证的改写自动回退至人工原文" in generated
