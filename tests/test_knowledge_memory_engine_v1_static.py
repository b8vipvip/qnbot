from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v1_memory_engine_and_ui_are_retired():
    props = read("src/Bot/Directory.Build.props")
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeMemoryEngine.cs").exists()
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeMemoryEngineUi.cs").exists()
    assert "KnowledgeMemoryEngine.cs" not in props
    assert "KnowledgeMemoryEngineUi.cs" not in props


def test_v2_is_the_only_runtime_knowledge_retriever_and_direct_answer_gate():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    my_openai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "KnowledgeEngineV2Service.Resolve(" in runtime
    assert "KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)" in pipeline
    assert "var v2Decision = KnowledgeEngineV2Service.Resolve(seller, buyer, question);" in my_openai
    assert "KnowledgeMemoryEngine" not in runtime
    assert "KnowledgeMemoryEngine" not in pipeline
    assert "KnowledgeMemoryEngine" not in my_openai


def test_v2_context_memory_is_shop_scoped_and_does_not_reactivate_v1_retrieval():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    state = read("src/Bot/ChromeNs/ConversationStateService.cs")
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "ShopSettingsScope.Enter(shop)" in runtime
    assert "ConversationStateService.Build(" in router
    assert "ConversationStateService.BuildPromptAddon(" in router
    assert "KnowledgeLearningService.TryFindLocalAnswer" not in state
    assert "KnowledgeMemoryEngine" not in state
