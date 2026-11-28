from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_optimization_uses_smaller_batches_and_long_background_timeout():
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeOptimizationService.cs").exists()
    source = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    assert "OperationLocks.GetOrAdd(shop.ShopKey" in source
    assert "token.ThrowIfCancellationRequested();" in source
    assert "FindPendingByProposal" in source
    assert "TrySaveEditorRecord(seller, record, currentFingerprint," in source
    assert "MyOpenAI" not in source


def test_control_plane_has_separate_background_and_realtime_budgets():
    source = read("services/api-control-plane/runtime_routing_guard.py")

    assert 'BACKGROUND_MIN_MAX_TOKENS' in source
    assert 'BACKGROUND_TOTAL_BUDGET_SECONDS' in source
    assert 'BACKGROUND_ATTEMPT_TIMEOUT_SECONDS' in source
    assert 'return "background", BACKGROUND_TOTAL_BUDGET_SECONDS, BACKGROUND_ATTEMPT_TIMEOUT_SECONDS' in source
    assert '"text-background" if profile == "background" else "text"' in source


def test_background_defaults_are_documented_for_production_env():
    env = read("services/api-control-plane/.env.example")

    assert "BACKGROUND_MIN_MAX_TOKENS=4000" in env
    assert "BACKGROUND_TOTAL_BUDGET_SECONDS=240" in env
    assert "BACKGROUND_ATTEMPT_TIMEOUT_SECONDS=90" in env
