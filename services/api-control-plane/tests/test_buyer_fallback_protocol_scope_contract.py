import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]


def _read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8-sig")


def test_buyer_structured_fallback_explicitly_requests_chat_only_protocol_scope():
    my_openai = _read("src/Bot/ChromeNs/MyOpenAI.cs")
    buyer_pipeline = _read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "bool chatProtocolOnly" in my_openai
    assert 'TryAddWithoutValidation("X-QN-Allowed-Protocols", "chat")' in my_openai
    assert re.search(
        r"MyOpenAI\.CallStructuredChat\(\s*messages,\s*220,\s*0\.15,\s*StructuredFallbackSeconds,\s*token,\s*true\s*\)",
        buyer_pipeline,
    )


def test_background_structured_learning_keeps_default_unrestricted_protocol_routing():
    knowledge_learning = _read("src/Bot/ChromeNs/KnowledgeLearningService.cs")

    assert re.search(
        r"MyOpenAI\.CallStructuredChat\(\s*messages,\s*700,\s*0\.03,\s*25,\s*CancellationToken\.None\s*\)",
        knowledge_learning,
    )
