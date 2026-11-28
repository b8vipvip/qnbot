from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_embedding_gateway_is_installed_and_packaged():
    assert "runtime_embedding_guard.install(control_plane)" in read("services/api-control-plane/bootstrap.py")
    assert "runtime_embedding_guard.py" in read("services/api-control-plane/Dockerfile")
    guard = read("services/api-control-plane/runtime_embedding_guard.py")
    assert '/v1/embeddings' in guard
    assert 'embedding_upstream_exhausted' in guard


def test_optional_embedding_setting_and_cache_exist():
    assert not (ROOT / "src/Bot/ChromeNs/SemanticEmbeddingService.cs").exists()
    assert "SemanticEmbeddingService.cs" not in read("src/Directory.Build.targets")
    assert "runtime_embedding_guard.install(control_plane)" in read("services/api-control-plane/bootstrap.py")


def test_semantics_only_assist_retrieval_not_direct_fixed_reply():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "SemanticRetrievalApplied = false" in router
    assert "Route = SmartReplyRouteKind.AiGeneral" in router
    assert "&& standaloneSafe" in public and "&& !highRisk" in public
    assert "best.Score >= threshold" in public and "best.ConfidenceScore >= minConfidence" in public
    assert "IsStrongAutoPublishedHumanEvidenceSimilarMatch" in public

