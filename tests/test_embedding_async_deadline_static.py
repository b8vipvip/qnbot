from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_embedding_http_path_is_true_async_with_linked_wall_clock_deadline():
    assert not (ROOT / "src/Bot/ChromeNs/SemanticEmbeddingService.cs").exists()
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "SemanticEmbeddingService" not in router
    assert "SemanticRetrievalApplied = false" in router
    assert "Task.FromResult(plan)" in router


def test_foreground_router_awaits_embedding_and_propagates_total_budget_token():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "public static Task<SmartReplyPlan> BuildPlanAsync(" in router
    assert "CancellationToken cancellationToken" in router
    assert router.count("cancellationToken.ThrowIfCancellationRequested();") >= 4
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "await SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token)" in pipeline
    assert "generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds))" in pipeline


def test_embedding_subdeadline_fails_open_but_outer_budget_cancellation_propagates():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    build = router.split("public static Task<SmartReplyPlan> BuildPlanAsync(", 1)[1].split("public static bool CanUseOfflineKnowledgeFallback", 1)[0]
    assert build.index("cancellationToken.ThrowIfCancellationRequested();") < build.index("ConversationContextStore.GetRecentTurns")
    assert build.rindex("cancellationToken.ThrowIfCancellationRequested();") < build.index("return Task.FromResult(plan)")
    assert "SemanticEmbeddingService" not in build

