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
    assert "private static bool IsSafeCuratedImageFollowUp" in internal
    assert "query.ContextDependent" in internal
    assert "KnowledgeV2Semantics.IsHighRisk(message)" in internal
    assert "你" in internal and "看看" in internal

    assert "KnowledgeEngineV2Service.ResolveWithEvidence(" in ocr
    assert "KnowledgeEngineV2Service.ResolveWithCuratedImageKnowledgeEvidence(" in ocr
    assert "task.CombinedQuestion" in ocr
    assert "var evidenceText = ocr.Text;" in ocr
    assert "if (visualMatch != null && !string.IsNullOrWhiteSpace(visualMatch.ContextText))" in ocr
    assert "visualMatch == null ? string.Empty : visualMatch.AppName" in ocr
    assert "if ((visualMatch == null && imageKnowledgeMatch == null) || decision == null" in ocr
    assert "仍独立执行Knowledge V2证据检索" in ocr
    assert "BuildKnowledgeQuestion" not in ocr
    assert "BuildTimelineText" not in ocr


def test_legacy_ai_generated_learning_is_candidate_and_never_a_legacy_production_answer():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    authority = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    assert "自动学习|会话学习|人工回复学习|学习候选|pending|candidate|AI生成" in semantics
    assert "IsUnapprovedLegacyLearningEntry" in authority
    assert "IsTrustedProductionSource(entry.SourceType)" in authority
    assert "EnsureLegacyNonProductionMirrorsDisabled" in repository
    assert "legacy_nonproduction_mirror_version" in repository
    assert "BotFeatureStore.SaveKnowledgeBase(legacy)" in repository


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


def test_ai_general_route_does_not_pass_weak_smart_reply_candidates():
    smart = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    prompt = smart[
        smart.index("public static string BuildPromptAddon"):
        smart.index("private static string BuildContextDigest")
    ]
    assert "plan.Route == SmartReplyRouteKind.AiGeneral" in prompt
    assert prompt.index("plan.Route == SmartReplyRouteKind.AiGeneral") < prompt.index("【智能知识路由】")


def test_legacy_candidate_filter_applies_to_all_fallback_retrieval_paths():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    smart = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    local = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    embedding = read("src/Bot/ChromeNs/SemanticEmbeddingService.cs")
    ensure = repository[
        repository.index("private static void EnsureMigrated"):
        repository.index("private static void EnsureLegacyNonProductionMirrorsDisabled")
    ]
    assert "EnsureLegacyNonProductionMirrorsDisabled(shop, state);" in ensure
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in smart
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in local
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(entry)" in embedding



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



def test_v2_startup_unwraps_already_installed_memory_v1_handler():
    memory = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    disable = memory[
        memory.index("public static void DisableForV2"):
        memory.index("private static void PatchExisting")
    ]
    assert "timer.Dispose()" in disable
    assert 'GetNestedType("MemoryHandlerWrapper"' in disable
    assert 'GetField("_inner"' in disable
    assert "handlerField.SetValue(coordinator, inner);" in disable
    assert "lock (PatchSync)" in disable
    assert "lock (PatchSync)" in memory
    assert "Volatile.Read(ref _disabledForV2)" in memory
    assert "if (Volatile.Read(ref _disabledForV2) != 0)" in memory
    assert "await inner(lease);" in memory
    assert "lock (PatchSync)" in memory
    assert "KnowledgeMemoryRuntimeBridge.DisableForV2();" in runtime



def test_unapproved_legacy_learning_does_not_pollute_conversation_entities_or_v1_memory():
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    memory = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")

    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in state
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in memory
    assert "var signature = ComputeSignature(knowledge);" in memory



def test_existing_v2_learning_candidates_are_normalized_without_disabling_v2_editor_state():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    helper = repository[
        repository.index("private static void EnsureLegacyNonProductionMirrorsDisabled"):
        repository.index("private static KnowledgeV2Record CloneAndNormalize")
    ]
    assert "legacyCandidatesToNormalize" in helper
    assert 'record.Status = "candidate";' in helper
    assert "state.Db.SaveRecordsInTransaction(" in helper
    assert "legacy_nonproduction_mirror_version" in helper



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
        repository.index("private static void EnsureLegacyNonProductionMirrorsDisabled"):
        repository.index("private static KnowledgeV2Record CloneAndNormalize")
    ]
    assert "KnowledgeV2AuthorityPolicy.IsCandidate(record)" in helper
    assert '!string.Equals(record.Type, "learning_candidate"' in helper
    assert 'record.Type = "learning_candidate";' in helper
    assert 'record.Status = "candidate";' in helper

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

def test_knowledge_v2_and_legacy_memory_direct_sends_recheck_shop_scoped_relevance_at_last_mile():
    v2 = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    memory = read("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")

    for source, method_name, send_anchor in [
        (v2, "Knowledge Engine V2", "burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard"),
        (memory, "Knowledge Memory", "burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard"),
    ]:
        assert "out relevanceReason, false, burst.ShopKey" in source
        assert "Func<bool> preSendGuard = () =>" in source
        assert "lease.CancellationToken.IsCancellationRequested" in source
        assert "Params.Robot.CanUseRobotReal" in source
        assert "Params.Robot.GetIsAutoReply()" in source
        assert "out latestRelevanceReason, false, burst.ShopKey" in source
        assert send_anchor in source

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

