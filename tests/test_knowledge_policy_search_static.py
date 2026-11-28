from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Knowledge" / "KnowledgeCenterV2RecordsPage.cs"


def source_text() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_v2_records_page_has_search_and_count():
    text = source_text()
    assert "_search.TextChanged += delegate { ApplyFilter(); }" in text
    assert "当前显示 " in text
    assert "ApplyGlobalSearch(string text)" in text


def test_v2_search_covers_knowledge_and_policy_fields():
    text = source_text()
    search = text[text.index("private static bool MatchesQuery"):text.index("private void NewRecord")]
    for field in ("item.Id", "item.Title", "item.Intent", "item.Entities", "item.Aliases",
                  "item.Conditions", "item.Exclusions", "item.RequiredContext", "item.Answer",
                  "item.ShortAnswer", "item.Type", "item.SourceType", "item.SourceId",
                  "item.ProductIds", "item.Status", "item.RiskLevel"):
        assert field in search


def test_v2_search_normalizes_question_punctuation_without_selecting_unrelated_entries():
    text = source_text()
    assert "var compactHaystack = KnowledgeEngineV2Semantics.Compact(haystack)" in text
    assert "var compactTerm = KnowledgeEngineV2Semantics.Compact(term)" in text
    assert "compactHaystack.IndexOf(compactTerm, StringComparison.OrdinalIgnoreCase) < 0" in text


def test_v2_exact_record_navigation_does_not_guess_from_similar_answers():
    text = source_text()
    assert "string.Equals(x.Id, targetId, StringComparison.Ordinal)" in text
    assert "_grid.SelectedItem = exact;" in text
    assert "_grid.ScrollIntoView(exact);" in text
    assert "不会自动选择相似条目" in text
    assert 'Col("状态", "Status", 80)' in text
    assert 'Col("更新", "UpdatedAtText", 126)' in text
