from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_v2_conflict_detection_requires_same_question_and_incompatible_claims():
    text = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "IsMeaningfulConflictPair(best.Record, second.Record)" in text
    assert "IsMeaningfulConflictPair(records[i], records[j])" in text
    assert "KnowledgeEngineV2Semantics.TextSimilarity(left.Title, right.Title)" in text
    assert "questionSimilarity < 0.72" in text
    assert "AnswersEquivalent(left.Answer, right.Answer)" in text
    assert "leftDirection != 0 && rightDirection != 0" in text
    assert "questionSimilarity >= 0.86" in text
    assert "leftDirection < 0 || rightDirection < 0" in text


def test_same_fact_key_different_procedures_are_not_automatically_conflicts():
    text = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    old = "return !AnswersEquivalent(best.Record.Answer, second.Record.Answer);"
    assert old not in text
    assert "FactKey intentionally groups a broad business scope for retrieval" in text
    assert "same buyer question" in text
    assert "different" in text
    assert "同一明确业务问题存在相互矛盾的生产知识" in text
