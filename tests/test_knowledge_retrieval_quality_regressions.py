from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


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
    assert "TryOcrTextAiFallbackAsync(task, cancellationToken, knowledgeV2ContextAddon)" in vision
