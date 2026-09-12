from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def vision_pipeline() -> str:
    return read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")


def test_ocr_v2_direct_answer_does_not_reenter_hidden_ai_validator():
    source = vision_pipeline()

    assert '"ocr-direct-knowledge-v2"' in source
    assert "var trustedOcrKnowledge = string.Equals(" in source
    assert "lifecycleLease.CancellationToken" in source
    assert "OCR-first权威知识直答已跳过隐藏AI校验/重答" in source

    fast_start = source.index("var trustedOcrKnowledge = string.Equals(")
    fast_end = source.index("var answer = deduplication.Answer;", fast_start)
    fast_block = source[fast_start:fast_end]
    assert "ReplyDeduplicationService.EnsureDistinct(" in fast_block
    assert "true)" in fast_block


def test_withdrawal_pipeline_rebound_image_does_not_backdate_followup_metrics():
    source = vision_pipeline()
    rebind = source.split("private static bool TryRebindRecentCachedImage", 1)[1].split(
        "private static bool ShouldBindToRecentImage", 1
    )[0]

    assert "var responseAnchorAt = burst.Items" in rebind
    assert ".DefaultIfEmpty(DateTime.Now)" in rebind
    assert ".Min();" in rebind
    assert "SortValue = recent.ObservedAt.Ticks" in rebind
    assert "ReceivedAt = responseAnchorAt" in rebind
    assert 'responseAnchorAt=" + responseAnchorAt.ToString("HH:mm:ss.fff")' in rebind


def test_recent_image_expiry_still_uses_original_observed_at():
    source = vision_pipeline()
    rebind = source.split("private static bool TryRebindRecentCachedImage", 1)[1].split(
        "private static bool ShouldBindToRecentImage", 1
    )[0]

    assert "var elapsed = latestAt - recent.ObservedAt;" in rebind
    assert "elapsed > TimeSpan.FromSeconds(VisionImageCacheService.RecentReferenceWindowSeconds)" in rebind
