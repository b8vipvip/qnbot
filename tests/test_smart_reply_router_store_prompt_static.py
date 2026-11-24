from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_smart_reply_router_has_three_routes_and_two_stage_small_retrieval():
    code = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "DirectKnowledge" in code
    assert "ContextualKnowledge" in code
    assert "AiGeneral" in code
    assert "RetrievalPoolSize = 10" in code
    assert "PromptCandidateCount = 3" in code
    assert "CalculateContextDependency" in code
    assert "CanDirectReply" in code
    assert "DetectIntent" in code
    assert "候选知识" in code
    assert "这些知识是候选事实依据，不是必须原样发送的固定答案" in code


def test_streaming_pipeline_uses_router_before_ai_and_keeps_manual_rules_first():
    code = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    manual = code.index("var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question)")
    plan = code.index("var plan = await SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token)")
    direct = code.index("plan.Route == SmartReplyRouteKind.DirectKnowledge", plan)
    prompt = code.index("SmartReplyRouterService.BuildPromptAddon(plan)", direct)
    assert manual < plan < direct < prompt
    assert "StorePromptProfileService.BuildPromptAddon()" in code
    assert "ConversationSessionLearningService.BuildReplyStylePromptAddon(seller)" in code
    assert "plan.ContextDigest" in code
    assert "plan.RecentTurns" in code
    assert "智能路由-知识上下文" in code


def test_legacy_non_streaming_local_hit_is_gated_by_same_smart_router():
    helper = read("src/Bot/ChromeNs/KnowledgeContextualReplyService.cs")
    assert "SmartReplyRouterService.BuildPlan" in helper
    assert "decision.SmartPlan.Route != SmartReplyRouteKind.DirectKnowledge" in helper
    assert "SmartReplyRouterService.BuildPromptAddon(decision.SmartPlan)" in helper
    assert "StorePromptProfileService.BuildPromptAddon()" in helper


def test_store_rule_center_generates_core_and_scene_rules_instead_of_one_long_prompt():
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    ui = read("src/Bot/Knowledge/StorePromptProfileUi.cs")
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "store-prompt-profile.json" in service
    assert "GenerateStructuredProfileAsync" in service
    assert '"core_prompt"' in service
    assert '"rules"' in service
    assert "MaxCoreCharacters = 2500" in service
    assert "MaxTextRules = 3" in service
    assert "MaxVisionRules = 8" in service
    assert "BuildTextRulesAddon" in service
    assert "BuildVisionPromptAddon" in service
    assert "NeedsStructuredMigration" in service
    assert "店铺核心规则与服务边界" in service
    assert "按当前场景动态选取的店铺规则" in service
    assert "StorePromptProfileService.BuildTextRulesAddon(state)" in state
    assert "StorePromptProfileService.BuildVisionPromptAddon(prompt)" in vision
    assert 'Content = "店铺规则中心"' in ui
    assert 'Content = "AI生成结构化规则"' in ui
    assert "原始店铺资料" in ui
    assert "核心规则" in ui
    assert "场景规则卡" in ui
    assert "StorePromptProfileUi.Initialize()" in app
    assert "StorePromptProfileService.cs" in targets
    assert "StorePromptProfileUi.cs" in targets


def test_structured_rules_have_scope_priority_triggers_and_runtime_limits():
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    for field in ["Scope", "Priority", "Triggers", "Content", "Enabled"]:
        assert "public " in service and field in service
    assert "StoreRuleScopes.Text" in service
    assert "StoreRuleScopes.Vision" in service
    assert "StoreRuleScopes.Both" in service
    assert "SelectRules" in service
    assert "CalculateRuleScore" in service
    assert "includePriorityFallback" in service
    assert "MaxTextRuleCharacters = 4200" in service
    assert "MaxVisionRuleCharacters = 6500" in service



def test_ai_general_never_injects_weak_legacy_candidates():
    code = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    prompt = code[code.index("public static string BuildPromptAddon"):code.index("private static string BuildContextDigest")]
    retrieval = code[code.index("private static List<SmartKnowledgeCandidate> RetrieveCandidates"):code.index("private static SmartKnowledgeCandidate ScoreCandidate")]

    assert "plan.Route == SmartReplyRouteKind.AiGeneral" in prompt
    assert prompt.index("plan.Route == SmartReplyRouteKind.AiGeneral") < prompt.index("【智能知识路由】")
    assert "PolicyEvaluation.ScoreAdjustment > 0" not in retrieval
    assert "x.RetrievalScore >= 0.20" in retrieval
    assert "x.ResolvedQueryScore >= 0.24" in retrieval


def test_legacy_retrieval_paths_exclude_unapproved_learning_records():
    smart = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    local = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    embedding = read("src/Bot/ChromeNs/SemanticEmbeddingService.cs")

    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in smart
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x)" in local
    assert "KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(entry)" in embedding
