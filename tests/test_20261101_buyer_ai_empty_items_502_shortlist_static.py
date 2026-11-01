"""AI buyer-message optimization should not mistake empty relation JSON for no knowledge."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
archive = (ROOT / "src/Bot/Knowledge/BuyerMessageArchiveService.cs").read_text(encoding="utf-8-sig")
ui = (ROOT / "src/Bot/Knowledge/BuyerMessageManagementPage.cs").read_text(encoding="utf-8-sig")

def test_local_knowledge_retrieval_limits_ai_requests_and_payload():
    area = archive.split("internal static async Task<int> OptimizeTop(", 1)[1].split(
        "// Strictly map an archived message to one already-approved direct answer.", 1)[0]
    assert "CandidateKnowledge(g, knowledge)" in area
    assert ".Take(5).Select(x => x.Knowledge)" in archive
    assert "const int batchSize = 4;" in area
    assert "pendingIndices.Skip(offset).Take(batchSize)" in area
    assert "1900, 70).ConfigureAwait(false)" in area
    assert "for (var offset = 0; offset < knowledge.Count; offset += 20)" not in area
    assert "links[g.Key]" not in area
    assert "new JArray(candidates[globalIndex].Select(k =>" in area
    assert '"knowledge"] = new JArray(' in area
    assert "GroupByAi(seller" not in area

def test_ai_json_contract_is_total_and_empty_items_not_a_success():
    area = archive.split("internal static async Task<int> OptimizeTop(", 1)[1].split(
        "// Strictly map an archived message to one already-approved direct answer.", 1)[0]
    assert "每个index必须返回恰好一条结果" in area
    assert "绝对不可省略或直接返回items:[]" in area
    assert 'items == null || items.Count != indexes.Count' in area
    assert "AI返回items数目不正确" in area
    assert "items:[]不代表没有匹配知识" in area
    assert "!seen.Add(index)" in area
    assert "seen.Count != indexes.Count" in area
    assert "matched == null || string.IsNullOrWhiteSpace(proposed)" in area
    assert "ct.ThrowIfCancellationRequested();" in area

def test_upstream_timeout_is_reported_as_error_not_no_matches():
    area = archive.split("internal static async Task<int> OptimizeTop(", 1)[1].split(
        "// Strictly map an archived message to one already-approved direct answer.", 1)[0]
    assert "HTTP 502/90秒超时" in area
    assert "不能当作无知识命中" in area
    assert 'throw new InvalidOperationException("AI分析直答证据第 "' in area
    assert "在服务恢复后重新分析" in area
    assert "1900, 70)" in area
    assert "MyOpenAI.CallStructuredChat(messages, maxTokens, 0.1, timeoutSeconds, ct)" in archive

def test_no_evidence_never_updates_production_answers():
    area = archive.split("internal static async Task<int> OptimizeTop(", 1)[1].split(
        "// Strictly map an archived message to one already-approved direct answer.", 1)[0]
    assert "NeedsContextInsteadOfDirectAnswer" in area
    assert 'KnowledgeEngineV2Semantics.IsHighRisk(group.Title)' in archive
    assert 'x.Status == "active"' in area
    assert "(x.ProductIds == null || x.ProductIds.Count == 0)" in area
    assert 'ProposedAnswer = ""' in area
    assert 'KnowledgeId = ""' in area
    assert "SaveOptimization(seller, item)" in area
    assert "BuyerMessageArchiveService.Apply(" not in area
    assert "PreviewPending(latest);" in ui
    assert "selected = rows.Where(x => x.Selected)" in ui
