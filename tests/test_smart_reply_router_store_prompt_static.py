from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_smart_reply_router_is_context_only_and_v2_owns_knowledge_retrieval():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")

    assert "ContextualQueryRewriteService.Resolve" in router
    assert "ConversationStateService.Build" in router
    assert "CanUseOfflineKnowledgeFallback" in router
    fallback = router[router.index("public static bool CanUseOfflineKnowledgeFallback"):router.index("public static string BuildPromptAddon")]
    assert "return false;" in fallback
    assert "Knowledge V2 已完成唯一知识检索" in router
    assert "KnowledgeEngineV2Service.Resolve(" in runtime
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in public
    assert "RetrieveCandidates" not in router

def test_streaming_pipeline_runs_v2_before_ai_and_manual_rules_precede_v2_retrieval():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    manual = runtime.index("BotFeatureStore.EvaluateAutoReplyRule(currentQuestion)")
    resolve = runtime.index("KnowledgeEngineV2Service.Resolve(")
    v2 = pipeline.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)")
    ai = pipeline.index("StreamingBuyerAnswerService.GetAnswerAsync(")
    assert manual < resolve
    assert v2 < ai
    assert "StorePromptProfileService.BuildPromptAddon()" in pipeline
    assert "ConversationSessionLearningService.BuildReplyStylePromptAddon(seller)" in pipeline
    assert "SmartReplyRouterService.BuildPromptAddon(plan)" in pipeline
    assert "knowledgeV2Result.ContextPromptAddon" in pipeline

def test_legacy_non_streaming_knowledge_router_is_removed_not_reactivated():
    root = ROOT / "src/Bot/ChromeNs/KnowledgeContextualReplyService.cs"
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    my_openai = read("src/Bot/ChromeNs/MyOpenAI.cs")

    assert not root.exists()
    assert "KnowledgeContextualReplyService" not in pipeline
    assert "KnowledgeContextualReplyService" not in my_openai
    assert "KnowledgeEngineV2Service.Resolve(" in my_openai

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
    options = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    assert 'MakeButton("店铺规则中心", 110)' in options
    assert "StorePromptProfileUi.Show(this)" in options
    assert 'Content = "AI生成结构化规则"' in ui
    assert "原始店铺资料" in ui
    assert "核心规则" in ui
    assert "场景规则卡" in ui
    assert "RulePolicyImportExportUi.InitializeForApp()" in app
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



def test_ai_general_receives_only_v2_approved_knowledge_context():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")

    assert "Knowledge V2 已完成唯一知识检索" in router
    assert "CanUseOfflineKnowledgeFallback" in router
    assert "return false;" in router[router.index("public static bool CanUseOfflineKnowledgeFallback"):router.index("public static string BuildPromptAddon")]
    assert "BuildContextPromptAddon(decision)" in runtime
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in public
    assert "RetrieveCandidates" not in router

def test_retired_v1_retrieval_services_are_not_compiled_or_called():
    props = read("src/Bot/Directory.Build.props")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    for name in ["KnowledgeContextualReplyService.cs", "SemanticEmbeddingService.cs", "KnowledgeMemoryEngine.cs"]:
        assert name not in props
    assert "KnowledgeContextualReplyService" not in runtime
    assert "KnowledgeContextualReplyService" not in pipeline
    assert "KnowledgeMemoryEngine" not in runtime
    assert "BotFeatureStore.GetKnowledgeBase" not in repository
    assert "MirrorOneToLegacy" not in repository

