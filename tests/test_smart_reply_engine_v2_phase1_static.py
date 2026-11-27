from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_conversation_state_tracks_topic_entity_goal_pending_question_and_stage():
    code = read("src/Bot/ChromeNs/ConversationStateService.cs")
    assert "class ConversationStateSnapshot" in code
    assert "CurrentTopic" in code
    assert "CurrentEntity" in code
    assert "BuyerGoal" in code
    assert "PendingQuestion" in code
    assert "ConversationStage" in code
    assert "ConfirmedFacts" in code
    assert "ExtractKnownEntities" in code
    assert "FindPendingQuestion" in code
    assert "DetectStage" in code


def test_contextual_query_rewrite_is_local_and_only_runs_for_dependent_queries():
    code = read("src/Bot/ChromeNs/ConversationStateService.cs")
    assert "class ContextualQueryResolution" in code
    assert "ContextualQueryRewriteService" in code
    assert "contextDependencyScore < 0.30" in code
    assert "买家回答/追问" in code
    assert "买家当前追问" in code
    assert "已继承最近主题" in code
    assert "MyOpenAI" not in code


def test_v2_runtime_uses_contextually_rewritten_query_before_knowledge_retrieval():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")

    assert "ConversationStateService.Build" in router
    assert "ContextualQueryRewriteService.Resolve" in router
    assert "QueryResolution" in router
    assert "ConversationState" in router
    assert "contextualPlan = await SmartReplyRouterService.BuildPlanAsync(" in runtime
    assert "contextualResolution = contextualPlan == null ? null : contextualPlan.QueryResolution;" in runtime
    assert "contextualResolution.Rewritten" in runtime
    assert "retrievalQuestion = contextualResolution.ResolvedQuery;" in runtime
    assert "KnowledgeEngineV2Service.Resolve(burst.SellerNick, burst.BuyerNick, retrievalQuestion)" in runtime
    assert "decision.Query.UserOriginal = currentQuestion;" in runtime
    assert "UserOriginal" in read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    assert "上下文问题还原" in router or "BuildContextPromptAddon" in runtime
    assert "当前会话状态" in state

def test_rewritten_queries_are_contextual_and_preserve_the_original_buyer_question():
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    model = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")

    assert "class ContextualQueryResolution" in state
    assert "ContextualQueryRewriteService.Resolve(" in state
    assert "contextDependencyScore < 0.30" in state
    assert "买家当前追问" in state
    assert "retrievalQuestion = contextualResolution.ResolvedQuery;" in runtime
    assert "decision.Query.UserOriginal = currentQuestion;" in runtime
    assert "public string UserOriginal { get; set; }" in model
    assert "decision.Query.UserOriginal ?? decision.Query.Original" in runtime

def test_new_source_is_included_in_wpf_temp_and_normal_builds():
    targets = read("src/Directory.Build.targets")
    assert "ChromeNs\\ConversationStateService.cs" in targets