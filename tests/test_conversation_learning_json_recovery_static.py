from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "ConversationSessionLearningService.cs"


def test_learning_parser_supports_common_model_wrappers():
    source = SOURCE.read_text(encoding="utf-8-sig")

    assert "TryParseLearningObject" in source
    assert "TrySelectLearningObject" in source
    assert "ExtractBalancedJsonObjects" in source
    assert '```(?:json)?\\s*(?<body>[\\s\\S]*?)```' in source
    assert "JToken.Parse(candidate)" in source
    assert "JTokenType.String" in source


def test_learning_parser_requires_expected_learning_shape():
    source = SOURCE.read_text(encoding="utf-8-sig")

    expected_gate = (
        'obj["summary"] != null || obj["suggestions"] != null '
        '|| obj["reply_style_profile"] != null '
        '|| obj["decisions"] != null || obj["candidates"] != null'
    )
    assert expected_gate in source
    assert "IndexOf('{')" not in source
    assert "LastIndexOf('}')" not in source
    assert "已尝试纯JSON、Markdown代码块、字符串包裹和括号平衡恢复" in source


def test_balanced_object_scanner_is_string_and_escape_aware():
    source = SOURCE.read_text(encoding="utf-8-sig")

    assert "var inString = false;" in source
    assert "var escaped = false;" in source
    assert "if (ch == '\\\\')" in source
    assert "if (ch == '\"') inString = false;" in source
    assert "if (result.Count >= 12) break;" in source
