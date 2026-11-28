from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_revision_candidates_use_real_manual_corrections_and_cross_buyer_evidence():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    assert 'string.Equals(x.EventType, "correction"' in service
    assert 'const string prefix = "manual_reply:"' in service
    assert "DistinctBuyerCount(cluster)" in service
    assert "cluster.Samples.Count < minEvidence || distinctBuyers < minBuyers" in service
    assert "dominance < 0.50" in service
    assert "BuildClusters(samples)" in service
    assert 'Clean(x.Event.Buyer).ToLowerInvariant() + "|" + NormalizeComparable(x.Text)' in service


def test_revision_generation_never_auto_overwrites_knowledge():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    generation = service.split("public static KnowledgeV2RevisionGenerationResult GenerateCandidates", 1)[1].split(
        "public static List<KnowledgeV2RevisionCandidate> GetCandidates", 1
    )[0]
    assert "KnowledgeEngineV2Repository.Save" not in generation
    assert 'Status = "pending"' in generation
    assert "未自动修改知识" in generation
    assert "MyOpenAI" not in service
    assert "CallStructuredChat" not in service


def test_high_risk_revision_requires_stronger_consensus():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    assert "var minEvidence = highRisk ? 3 : 2;" in service
    assert "var minBuyers = highRisk ? 3 : 2;" in service
    assert "var minSimilarity = highRisk ? 0.82 : 0.74;" in service
    assert "KnowledgeEngineV2Semantics.IsHighRisk" in service


def test_apply_is_manual_and_refuses_to_overwrite_newer_knowledge():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    apply = service.split("public static bool ApplyCandidate", 1)[1].split(
        "public static bool RejectCandidate", 1
    )[0]
    assert 'string.Equals(row.Status, "pending"' in apply
    assert "row.OriginalFingerprint" in apply
    assert "EditorRecordFingerprint(record)" in apply
    assert 'MarkStatus(seller, row, "stale"' in apply
    assert "record.Answer = row.ProposedAnswer.Trim();" in apply
    assert "record.LastVerifiedAt = DateTime.Now;" in apply
    assert "KnowledgeEngineV2Repository.TrySaveEditorRecord(seller, record, currentFingerprint)" in apply
    assert 'MarkStatus(seller, row, "applied"' in apply


def test_revision_database_preserves_original_and_evidence_for_audit():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    assert 'Path.Combine(root, "knowledge-revision-v2.db")' in service
    for field in [
        "OriginalAnswer",
        "ProposedAnswer",
        "EvidenceJson",
        "EvidenceCount",
        "DistinctBuyerCount",
        "ClusterScore",
        "ResolutionNote",
    ]:
        assert field in service


def test_revision_review_ui_requires_explicit_apply_or_reject():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2RevisionUi.cs")
    for label in ["修订", "分析并生成候选", "应用所选", "驳回所选", "真实人工纠正证据", "建议修订"]:
        assert label in ui
    assert "MessageBoxButton.YesNo" in ui
    assert "KnowledgeEngineV2RevisionService.ApplyCandidate" in ui
    assert "KnowledgeEngineV2RevisionService.RejectCandidate" in ui
    assert "系统绝不自动覆盖知识" in ui


def test_revision_components_compile_and_bootstrap_for_wpf_projects():
    props = read("src/Bot/Directory.Build.props")
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2RevisionUi.cs")
    assert "KnowledgeEngineV2RevisionService.cs" in props
    assert "KnowledgeCenterV2RevisionUi.cs" in props
    assert "KnowledgeV2RevisionUiBridge.InitializeForApp()" in ui
    assert "_knowledgeV2RevisionUiBootstrap" in ui

def test_revision_generation_is_shop_serialized_cancellable_and_not_capped_to_first_120():
    service = read("src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs")
    assert "OperationLocks.GetOrAdd(shop.ShopKey" in service
    assert "token.ThrowIfCancellationRequested();" in service
    assert ".Take(120)" not in service
    assert "OriginalFingerprint = KnowledgeEngineV2Repository.EditorRecordFingerprint(record)" in service
    assert "SafeDate(x.CreatedAtTicks) >= record.LastVerifiedAt" in service
