from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")




def test_structured_product_ids_participate_in_full_and_incremental_knowledge_indexes():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")

    record_entities = index.split("private static IEnumerable<string> RecordEntities", 1)[1].split(
        "private static HashSet<int> Recall", 1
    )[0]
    assert "record.ProductIds" in record_entities
    assert '"product:" + value' in record_entities
    assert "Distinct(StringComparer.OrdinalIgnoreCase)" in record_entities

    assert "foreach (var entity in RecordEntities(record))" in index
    assert "EntitySimilarity(query.Entities, RecordEntities(record))" in index
    assert "foreach (var entity in RecordEntities(record))" in incremental
    assert "Add(snapshot.Entity" in incremental
    assert "Remove(snapshot.Entity" in incremental
    assert "foreach (var productId in ExtractProductIds(value))" in semantics
    extractor = semantics.split("private static List<string> ExtractProductIds", 1)[1].split(
        "private static string NormalizeEntity", 1
    )[0]
    assert "商品" in extractor and "产品" in extractor and "宝贝" in extractor
    assert "product|item|goods" in extractor
    assert "item\\.taobao\\.com/item\\.htm" in extractor
    assert "item\\.jd\\.com" in extractor
    assert 'Regex.Matches(text ?? string.Empty, @"\\b\\d{8,16}\\b")' not in extractor


def test_explicit_product_id_mismatch_cannot_use_product_bound_knowledge():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    applicable = semantics[
        semantics.index("public static bool IsApplicable"):
        semantics.index("public static string FactKey")
    ]
    normalize = semantics[
        semantics.index("private static List<string> NormalizeProductIds"):
        semantics.index("private static List<string> NormalizeScopeTerms")
    ]

    assert "NormalizeProductIds(record.ProductIds)" in applicable
    assert "NormalizeProductIds(" in applicable
    assert "recordProductIds.Count > 0 && queryProductIds.Count > 0" in applicable
    assert "商品ID与当前问题中的商品不匹配" in applicable
    assert "recordProductIds.Any(id => queryProductIds.Any(queryId =>" in applicable
    assert "StringComparison.OrdinalIgnoreCase" in normalize
    # No supplied product ID is not a mismatch: buyers can identify a product by name.
    assert "queryProductIds.Count > 0" in applicable


def test_ocr_evidence_enriches_knowledge_recall_without_rewriting_buyer_query_or_memory():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")

    resolve = service[
        service.index("public static KnowledgeV2Decision ResolveWithEvidence"):
        service.index("private static KnowledgeV2Decision ResolveInternal")
    ]
    internal = service[
        service.index("private static KnowledgeV2Decision ResolveInternal"):
        service.index("private static bool IsEligibleProductionMatch")
    ]

    assert "ResolveInternal(seller, buyer, message, false, evidenceText, evidenceSubject, false)" in resolve
    assert "ResolveWithCuratedImageKnowledgeEvidence" in resolve
    assert "【已命中本店人工维护的图片知识】" in resolve
    assert "hasCuratedImageKnowledge" in resolve
    assert "if (message.Length == 0 && !hasEvidence)" in internal
    assert "if (IsMediaPlaceholder(message)) message = string.Empty;" in internal
    assert "if (message.Length > 0)" in internal
    assert internal.index("UpdateWorkingMemory(shop.ShopKey, seller, buyer, query)") < internal.index(
        "ExtractEntities(evidenceText)"
    )
    assert "var buyerMessageHasProductId" in internal
    assert "var evidenceHasProductId" in internal
    assert 'StartsWith("product:", StringComparison.OrdinalIgnoreCase)' in internal
    assert "if (buyerMessageHasProductId)" in internal
    assert "else if (evidenceHasProductId)" in internal
    assert "current image supersedes a stale product" in internal
    assert "OrderByDescending(x => x.StartsWith(\"product:\"" in internal
    assert "query.Entities = (query.Entities ?? new List<string>())" in internal
    assert "query.Subject = string.IsNullOrWhiteSpace(query.Subject)" in internal
    assert "decision.Query = query;" in internal
    assert "curatedImageEvidence && IsSafeCuratedImageFollowUp(query, message)" in internal
    assert "private static bool IsSafeCuratedImageFollowUp" in service
    assert "query.ContextDependent" in service
    assert "KnowledgeEngineV2Semantics.IsHighRisk(message)" in service
    assert "你" in service and "看看" in service

    assert "KnowledgeEngineV2Service.ResolveWithEvidence(" in ocr
    assert "KnowledgeEngineV2Service.ResolveWithCuratedImageKnowledgeEvidence(" in ocr
    assert "imageKnowledgeMatch.Description" in ocr
    assert "var evidenceSubject = imageKnowledgeMatch != null" in ocr
    assert "task.CombinedQuestion" in ocr
    assert "var evidenceText = ocr.Text;" in ocr
    assert "if (visualMatch != null && !string.IsNullOrWhiteSpace(visualMatch.ContextText))" in ocr
    assert "visualMatch == null ? string.Empty : visualMatch.AppName" in ocr
    assert "if ((visualMatch == null && imageKnowledgeMatch == null) || decision == null" in ocr
    assert "仍独立执行Knowledge V2证据检索" in ocr
    assert "BuildKnowledgeQuestion" not in ocr
    assert "BuildTimelineText" not in ocr


def test_legacy_ai_generated_learning_is_imported_as_a_v2_candidate():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    from_legacy = semantics[
        semantics.index("public static KnowledgeV2Record FromLegacy"):
        semantics.index("public static KnowledgeV2Query Parse")
    ]

    assert "if (entry.AiGenerated) return true;" in bridge
    assert "IsUnapprovedLegacyLearningEntry(entry)" in from_legacy
    assert 'if (isUnapprovedLearning) type = "learning_candidate";' in from_legacy
    assert 'Status = isUnapprovedLearning ? "candidate" : "active"' in from_legacy
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in bridge

def test_candidate_mirror_sync_compares_effective_legacy_enabled_state():
    authority = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    method = authority[
        authority.index("public static bool IsPersistedStateSynchronized"):
        authority.index("public static void ApplyImportedLegacyProvenance")
    ]
    assert "var isCandidate = IsCandidate(record);" in method
    assert "var expectedLegacyEnabled = record.Enabled" in method
    assert "if (entry.Enabled != expectedLegacyEnabled) return false;" in method
    assert "if (isCandidate)" in method


def test_verified_human_evidence_is_normalized_before_building_v2_snapshot():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "KnowledgeV2AuthorityPolicy.NormalizeForRead" in index
    assert index.index("Select(KnowledgeV2AuthorityPolicy.NormalizeForRead)") < index.index("BuildSnapshot(shop.ShopKey, records)")


def test_non_direct_v2_matches_are_forwarded_to_ai_as_approved_context():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]
    assert "BuildContextPromptAddon(decision)" in method
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved(match.Record)" in runtime
    assert "decision.HasConflict" in runtime
    assert "KnowledgeV2RuntimeResult.Continue(contextPromptAddon)" in method
    assert "TryHandleWithContextAsync(qn, lease)" in pipeline
    assert "knowledgeV2Result.ContextPromptAddon" in pipeline
    assert "knowledgeV2ContextAddon ?? string.Empty" in pipeline


def test_v2_context_handoff_excludes_conflict_candidate_and_high_risk_records():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    helper = runtime[
        runtime.index("internal static string BuildContextPromptAddon"):
        runtime.index("private static string SafeContext")
    ]
    assert "decision.HasConflict" in helper
    assert "KnowledgeEngineV2Semantics.IsHighRisk(decision.Query.Original)" in helper
    assert "IsProductionApproved(match.Record)" in helper
    assert "match.Score >= 0.52" in helper
    assert "match.ConfidenceScore >= 0.68" in helper
    assert 'match.Record.RiskLevel, "high"' in helper


def test_ai_general_route_does_not_retrieve_weak_or_unapproved_legacy_candidates():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    build = router[
        router.index("public static Task<SmartReplyPlan> BuildPlanAsync"):
        router.index("public static bool CanUseOfflineKnowledgeFallback")
    ]

    assert "Route = SmartReplyRouteKind.AiGeneral" in build
    assert "Candidates = new List<SmartKnowledgeCandidate>()" in build
    assert "ConversationStateService.Build(" in build
    assert "KnowledgeEngineV2Service.GetRecords(seller)" in state
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved(x)" in state
    assert "BotFeatureStore.GetKnowledgeBase()" not in router

def test_all_runtime_retrieval_and_fallback_paths_are_v2_only():
    files = [
        "src/Bot/ChromeNs/SmartReplyRouterService.cs",
        "src/Bot/ChromeNs/ReplyDeduplicationService.cs",
        "src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs",
        "src/Bot/ChromeNs/ConversationStateService.cs",
        "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs",
    ]
    for path in files:
        source = read(path)
        assert "BotFeatureStore.GetKnowledgeBase()" not in source
        assert "KnowledgeLearningService.TryFindLocalAnswer" not in source
        assert "KnowledgeMemoryEngine" not in source
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeMemoryEngine.cs").exists()
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeContextualReplyService.cs").exists()

def test_ocr_non_direct_v2_context_reaches_multimodal_and_ocr_text_fallback():
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")

    assert "KnowledgeEngineV2RuntimeBridge.BuildContextPromptAddon(decision)" in ocr
    assert "KnowledgeV2ContextPromptAddon = contextAddon" in ocr
    assert "internal static string BuildContextPromptAddon" in runtime
    assert "localDecision != null && localDecision.Success" in vision
    assert "localDecision.KnowledgeV2ContextPromptAddon" in vision
    assert "knowledgeV2ContextAddon ?? string.Empty" in vision
    assert "TryOcrTextAiFallbackAsync(task, cancellationToken, knowledgeV2ContextAddon, localDecision)" in vision



def test_v2_ngram_recall_uses_deterministic_even_sampling_not_hashset_order():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")

    assert "public static List<string> RecallNgrams(string value, int n, int maxCount)" in semantics
    assert "for (var i = 0; i <= value.Length - n; i++)" in semantics
    assert "Math.Round((double)i * (ordered.Count - 1) / (maxCount - 1))" in semantics
    assert "Ngrams(exact, 2).Take(24)" not in index
    assert "Ngrams(query.Normalized, 2).Take(24)" not in index
    assert "Ngrams(exact, 2).Take(24)" not in incremental
    assert "RecallNgrams(exact, 2, 24)" in index
    assert "RecallNgrams(query.Normalized, 2, 24)" in index
    assert "RecallNgrams(exact, 2, 24)" in incremental



def test_short_standalone_faqs_are_not_misclassified_as_context_fragments():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    method = semantics[
        semantics.index("public static bool IsContextDependent"):
        semantics.index("public static bool IsHighRisk")
    ]
    assert "if (compact.Length <= 8)" in method
    assert "ExtractEntities(compact)" in method
    assert "hasExplicitIntent" in method
    assert "return entities.Count == 0 || !hasExplicitIntent;" in method


def test_verified_learning_accepts_short_standalone_procedures_but_blocks_volatile_facts():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "question.Length < 4 || question.Length > 400" in repository
    assert "answer.Length < 2 || answer.Length > 1200" in repository
    assert "支付方式|付款方式" in semantics
    assert 'category = "usage_guide";' in learning
    assert 'ContainsVolatileCommerceFact(question + " " + answer)' in learning
    assert "支付成功|付款成功|充值成功|支付状态|付款状态|充值状态" in repository
    assert "支付成功|付款成功|充值成功|支付状态|付款状态|充值状态" in learning



def test_v2_startup_has_no_memory_v1_wrapper_or_reflection_patch():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    app = read("src/Bot/App.xaml.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeMemoryEngine.cs").exists()
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeMemoryEngineUi.cs").exists()
    assert "KnowledgeMemoryRuntimeBridge" not in runtime
    assert "KnowledgeMemoryEngine" not in app
    assert "MemoryHandlerWrapper" not in pipeline
    assert "V2HandlerWrapper" not in runtime

def test_unapproved_legacy_learning_does_not_pollute_v2_conversation_entities():
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")

    entity = state[
        state.index("private static List<string> ExtractKnownEntities"):
        state.index("private static IEnumerable<string> CandidateEntityTerms")
    ]
    assert "KnowledgeEngineV2Service.GetRecords(seller)" in entity
    assert 'string.Equals(x.Status, "active", StringComparison.OrdinalIgnoreCase)' in entity
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved(x)" in entity
    assert "BotFeatureStore.GetKnowledgeBase()" not in entity
    assert "KnowledgeMemoryEngine" not in state
    assert "public static bool IsProductionApproved(KnowledgeV2Record record)" in bridge

def test_existing_v2_learning_candidates_are_normalized_without_disabling_v2_editor_state():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    helper = repository[
        repository.index("private static void EnsureV2CandidateStatesNormalized"):
        repository.index("private static KnowledgeV2Record CloneAndNormalize")
    ]

    assert "candidateRecords" in helper or "candidatesToNormalize" in helper or "recordsToNormalize" in helper
    assert "KnowledgeV2AuthorityPolicy.IsCandidate(record)" in helper
    assert 'record.Status = "candidate";' in helper
    assert "state.Db.SaveRecordsInTransaction(" in helper

def test_legacy_ai_generated_flag_is_authoritative_even_when_source_metadata_is_missing():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    helper = bridge[
        bridge.index("public static bool IsUnapprovedLegacyLearningEntry"):
        bridge.index("public static bool IsPersistedStateSynchronized")
    ]
    from_legacy = semantics[
        semantics.index("public static KnowledgeV2Record FromLegacy"):
        semantics.index("public static KnowledgeV2Query Parse")
    ]
    assert "if (entry.AiGenerated) return true;" in helper
    assert "manual_ai_generated" in helper
    assert "var isUnapprovedLearning = KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(entry)" in from_legacy
    assert 'if (isUnapprovedLearning) type = "learning_candidate";' in from_legacy
    assert "Status = isUnapprovedLearning ? \"candidate\" : \"active\"" in from_legacy


def test_generic_manual_legacy_source_is_not_confused_with_ai_generated_provenance():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    helper = bridge[
        bridge.index("public static bool IsUnapprovedLegacyLearningEntry"):
        bridge.index("public static bool IsPersistedStateSynchronized")
    ]
    assert 'source.IndexOf("manual_ai_generated"' in helper
    assert 'value.IndexOf("manual", StringComparison.OrdinalIgnoreCase)' not in helper



def test_existing_v2_records_keep_ai_generated_source_provenance_as_candidates():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    candidate = bridge[
        bridge.index("public static bool IsCandidate(KnowledgeV2Record record)"):
        bridge.index("public static bool IsProductionApproved")
    ]
    assert 'if (IsTrustedProductionSource(record.SourceType)) return false;' in candidate
    assert 'source.IndexOf("AI生成"' in candidate
    assert 'source.IndexOf("manual_ai_generated"' in candidate
    assert 'source.IndexOf("session"' in candidate
    assert 'source.IndexOf("人工回复"' in candidate



def test_old_ai_source_records_are_normalized_to_candidate_status_without_disabling_them():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    helper = repository[
        repository.index("private static void EnsureV2CandidateStatesNormalized"):
        repository.index("private static KnowledgeV2Record CloneAndNormalize")
    ]
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")

    assert "KnowledgeV2AuthorityPolicy.IsCandidate(record)" in helper
    assert '!string.Equals(record.Type, "learning_candidate"' in helper
    assert 'record.Type = "learning_candidate";' in helper
    assert 'record.Status = "candidate";' in helper
    assert "if (!record.Enabled" not in helper
    assert "record.Enabled = false" not in helper
    assert "public static bool IsCandidate(KnowledgeV2Record record)" in bridge

def test_current_image_product_id_is_not_dropped_by_the_ten_entity_retrieval_bound():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    internal = service[
        service.index("private static KnowledgeV2Decision ResolveInternal"):
        service.index("private static bool IsEligibleProductionMatch")
    ]

    assert "if (!buyerMessageHasProductId && evidenceHasProductId)" in internal
    assert "var evidenceProductIds = evidenceEntities" in internal
    assert "var evidenceNonProductEntities = evidenceEntities" in internal
    assert "var queryEntities = (query.Entities ?? new List<string>())" in internal
    assert "query.Entities = evidenceProductIds" in internal
    assert internal.index("evidenceProductIds") < internal.index(".Concat(evidenceNonProductEntities)")
    assert ".Concat(queryEntities)" in internal
    assert ".Take(10)" in internal

def test_v2_direct_send_rechecks_shop_scoped_relevance_at_last_mile():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    assert "burst.ShopKey" in method
    assert "Func<bool> preSendGuard = () =>" in method
    assert "lease.CancellationToken.IsCancellationRequested" in method
    assert "Params.Robot.CanUseRobotReal" in method
    assert "Params.Robot.GetIsAutoReply()" in method
    assert "ParallelReplyRelevanceGate.ShouldSend(" in method
    assert "SendTextWithRetryAsync(" in method
    assert "KnowledgeMemoryEngine" not in runtime

def test_recent_turns_can_resolve_exact_shop_scope_without_treating_shop_key_as_question_text():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    method = context[
        context.index("public static List<ConversationContextTurn> GetRecentTurns"):
        context.index("public static List<ConversationMirrorSnapshot> GetMirrorConversationSnapshots")
    ]
    key = context[
        context.index("private static string Key("):
        context.index("private static ShopContext ResolveShop(")
    ]

    assert "int maxTurns,\n            string shopKey = null" in method
    assert "Key(seller, buyer, shopKey)" in method
    assert "ScopeKey(seller, shopKey)" in key
    assert "ResolveShop(seller, shopKey)" in key
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in key


def test_relevance_order_and_recharge_queries_use_explicit_shop_scoped_timelines():
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    order = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    recharge = read("src/Bot/ChromeNs/RechargeStatusAutoQueryService.cs")
    gate = streaming[
        streaming.index("class ParallelReplyRelevanceGate"):
        streaming.index("internal static class ShopScopedReplyCircuitBreaker")
    ]

    assert "GetRecentTurns(seller, buyer, string.Empty, 24, shopKey)" in gate
    assert "GetRecentTurns(seller, buyer, string.Empty, 24, shopKey)" in order
    assert "GetRecentTurns(seller, buyer, currentQuestion, 24, shopKey)" in recharge



def test_legacy_fuzzy_local_answer_cannot_bypass_the_v2_runtime():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "TryFindLocalAnswer" not in learning
    runtime_call = pipeline.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)")
    ai_call = pipeline.index("StreamingBuyerAnswerService.GetAnswerAsync(")
    assert runtime_call < ai_call
    assert "knowledgeV2Result.ContextPromptAddon" in pipeline
    assert "knowledgeV2Result.OfflineFallbackDecision" in pipeline
    assert "BotFeatureStore.GetKnowledgeBase()" not in learning

def test_removed_legacy_local_answer_has_no_unscoped_fallback():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    assert "TryFindLocalAnswer" not in learning
    assert "shopScope == null" in method
    assert "shopScope.ShopKey, burst.ShopKey, StringComparison.Ordinal" in method
    assert "return KnowledgeV2RuntimeResult.Continue();" in method
    assert "BotFeatureStore.GetKnowledgeBase()" not in runtime

def test_smart_reply_router_uses_v2_conversation_state_without_legacy_retrieval():
    smart = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    build = smart[
        smart.index("public static Task<SmartReplyPlan> BuildPlanAsync"):
        smart.index("public static bool CanUseOfflineKnowledgeFallback")
    ]

    assert "ConversationStateService.Build(" in build
    assert "ContextualQueryRewriteService.Resolve(" in build
    assert "Candidates = new List<SmartKnowledgeCandidate>()" in build
    assert "CanUseLegacyKnowledgeAuthority" not in smart
    assert "RetrieveCandidates" not in smart
    assert "KnowledgeEngineV2Service.GetRecords(seller)" in state
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved(x)" in state
    assert "BotFeatureStore.GetKnowledgeBase()" not in smart

def test_v2_runtime_yields_to_silence_and_manual_rules_before_local_knowledge():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    resolve = method.index("KnowledgeEngineV2Service.Resolve(")
    assert method.index("ReceptionFlowPolicy.TryChooseSilence") < resolve
    assert method.index("BotFeatureStore.EvaluateAutoReplyRule(currentQuestion)") < resolve
    assert "return KnowledgeV2RuntimeResult.Continue();" in method


def test_ai_failure_fallback_uses_only_a_freshly_revalidated_v2_record():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    fallback = source[
        source.index("private static string TryResolveV2OfflineFallback"):
        source.index("private static bool IsRetryableStreamError")
    ]

    assert "KnowledgeEngineV2Service.Resolve(seller, buyer, v2Question)" in fallback
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in fallback
    assert "KnowledgeEngineV2Repository.LoadAll(seller)" in fallback
    assert "latestMatch.Record.Id, priorMatch.Record.Id, StringComparison.Ordinal" in fallback
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer)" in fallback
    assert "ShopReceptionPolicy.IsQuarantined" not in fallback
    assert "BotFeatureStore.GetKnowledgeBase()" not in fallback

def test_approved_v2_context_reaches_pre_send_validation_and_one_time_regeneration():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    dedup = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    validator = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")

    assert "knowledgeV2Result == null ? null : knowledgeV2Result.ContextPromptAddon" in pipeline
    assert "string approvedV2ContextEvidence = null" in dedup
    assert "PreSendAnswerValidator.Validate(" in dedup
    assert "approvedV2ContextEvidence" in dedup
    assert "RegenerateInvalidAnswer(" in dedup
    assert "BuildAuthoritativeEvidence(state, approvedV2ContextEvidence)" in validator
    assert "HasKnowledgeContradiction(answer, approvedV2ContextEvidence)" in validator

def test_image_v2_approved_evidence_reaches_pre_send_validation_and_direct_answers_keep_fastpath():
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")

    assert "var approvedV2KnowledgeEvidence = KnowledgeEngineV2RuntimeBridge.BuildContextPromptAddon(decision)" in ocr
    assert "ApprovedV2KnowledgeEvidence = approvedV2KnowledgeEvidence" in ocr
    assert "IsKnowledgeV2DirectAnswer = true" in ocr
    assert "public string ApprovedV2KnowledgeEvidence { get; set; }" in vision
    assert "public bool IsKnowledgeV2DirectAnswer { get; set; }" in vision
    assert "result.ApprovedV2KnowledgeEvidence = localDecision.ApprovedV2KnowledgeEvidence" in vision
    assert "result.ApprovedV2KnowledgeEvidence" in pipeline
    assert "result.IsKnowledgeV2DirectAnswer" in pipeline
    assert "result.ApprovedV2KnowledgeEvidence" in qn


def test_image_route_honors_silence_and_manual_rules_before_ocr_or_vision_knowledge():
    pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    method = pipeline[
        pipeline.index("private static async Task ProcessVisionAsync"):
        pipeline.index("private static void ApplyOcrTextToQuestionCard")
    ]

    vision = method.index("Vision.ExecuteAsync(task, CancellationToken.None)")
    assert method.index("ReceptionFlowPolicy.TryChooseSilence") < vision
    assert method.index("BotFeatureStore.EvaluateAutoReplyRule(policyQuestion)") < vision
    assert "StreamingBuyerAnswerService.GetAnswerAsync(" in method



def test_v2_direct_reply_uses_shop_outgoing_guard_before_ready_and_at_transport():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    first_guard = method.index("ShopReceptionPolicy.BlockOutgoing(")
    assert first_guard < method.index("lease.MarkReady(")
    assert "out outgoingPolicyReason, burst.ShopKey, false" in method
    pre_send = method[
        method.index("Func<bool> preSendGuard = () =>"):
        method.index("bool sendOk;")
    ]
    assert "ShopReceptionPolicy.BlockOutgoing(" in pre_send
    assert "outgoingPolicySuppressed = true" in pre_send
    assert "knowledge_v2_business_guard_suppressed" in method



def test_v2_direct_reply_runs_transcript_sanitizer_and_store_output_policy_before_send():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    sanitize = method.index("ReplyTranscriptSanitizer.Sanitize(decision.Answer)")
    output_policy = method.index("BotFeatureStore.ApplyOutputPolicy(answer)")
    suffix = method.index("BotMessageSuffixService.Apply(burst.SellerNick, answer)")
    dedup = method.index("ReplyDeduplicationService.EnsureDistinct(")
    assert sanitize < output_policy < suffix < dedup
    assert "knowledge_v2_output_policy_empty" in method



def test_prompt_addon_contains_style_and_safety_only_not_legacy_knowledge():
    options = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    method = options[
        options.index("public static string BuildPromptAddon(string question)"):
        options.index("public static string ApplyOutputPolicy")
    ]

    assert "知识检索由 Knowledge V2 独立负责" in method
    assert "不读取旧知识库" in method
    assert "GetKnowledgeBase" not in method
    assert "knowledgeV2Result == null ? string.Empty : knowledgeV2Result.ContextPromptAddon" in read(
        "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
    )

def test_streaming_and_legacy_openai_prompt_paths_do_not_inject_v1_knowledge():
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    openai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    options = read("src/Bot/Options/CtlRobotOptions.xaml.cs")

    assert "BuildPromptAddon(" in streaming
    assert "BuildPromptAddon(" in openai
    assert "知识检索由 Knowledge V2 独立负责" in options
    assert "不读取旧知识库" in options
    assert "BotFeatureStore.GetKnowledgeBase()" not in streaming
    assert "BotFeatureStore.GetKnowledgeBase()" not in openai

def test_default_prompt_addon_fails_closed_without_legacy_knowledge_store():
    options = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    default_overload = options[
        options.index("public static string BuildPromptAddon(string question)"):
        options.index("public static void ApplyOutputPolicy") if "public static void ApplyOutputPolicy" in options
        else options.index("public static string ApplyOutputPolicy")
    ]

    assert "店铺回复策略" in default_overload
    assert "不读取旧知识库" in default_overload
    assert "GetKnowledgeBase" not in default_overload

