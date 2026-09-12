from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_historical_image_context_does_not_backdate_followup_response_metrics():
    source = read("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")

    # The historical image still participates in visual reasoning and keeps its transport ordering,
    # but its synthetic clone is timed from the first message in the current follow-up burst.
    assert "var responseAnchorAt = burst.Items" in source
    assert ".DefaultIfEmpty(DateTime.Now)" in source
    assert ".Min();" in source
    assert "CloneVisionItem(recent.Item, responseAnchorAt)" in source
    assert "ReceivedAt = receivedAtOverride.HasValue ? receivedAtOverride.Value : source.ReceivedAt" in source
    assert "SortValue = source.SortValue" in source
    assert "SessionGeneration = source.SessionGeneration" in source
    assert 'responseAnchorAt=" + responseAnchorAt.ToString("HH:mm:ss.fff")' in source


def test_remembered_visual_context_keeps_original_observation_time():
    source = read("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")

    # Only the per-follow-up synthetic clone gets the timing override; stored recent vision context
    # must retain the original image timestamp so the 45-second follow-up window stays authoritative.
    remember = source.split("private static void Remember", 1)[1].split(
        "private static BuyerMessageBurstItem CloneVisionItem", 1
    )[0]
    assert "Item = CloneVisionItem(item)" in remember
    assert "ObservedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt" in remember
