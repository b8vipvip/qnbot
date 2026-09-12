from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_pipeline_is_initialized_and_built():
    app = text("src/Bot/App.xaml.cs")
    targets = text("src/Directory.Build.targets")
    pipeline = text("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "VisionFollowUpContextPipeline.Initialize();" in app
    assert "VisionFollowUpContextPipeline.cs" in targets
    assert "_buyerMessageBurstCoordinator" in pipeline
    assert '"_handler"' in pipeline


def test_referential_text_reuses_recent_image_and_keeps_original_lease():
    pipeline = text("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "FollowUpWindowSeconds = 45" in pipeline
    assert "CaptionWindowSeconds = 15" in pipeline
    assert "SourceClockSkewToleranceSeconds = 15" in pipeline
    assert "RecentVision" in pipeline
    assert "IsVisionReferentialFollowUp" in pipeline
    assert 'compact == "这个吗"' in pipeline
    assert 'compact == "是这个吗"' in pipeline
    assert 'compact == "这里吗"' in pipeline
    assert 'compact == "对吗"' in pipeline
    assert 'compact == "这种能使用吗"' in pipeline
    assert 'compact.Contains("这种")' in pipeline
    assert 'compact.Contains("这类")' in pipeline
    assert "var responseAnchorAt = burst.Items" in pipeline
    assert "var items = new List<BuyerMessageBurstItem> { CloneVisionItem(recent.Item, responseAnchorAt) };" in pipeline
    assert "items.AddRange(burst.Items.Where(x => x != null));" in pipeline
    assert "new BuyerMessageBurst(" in pipeline
    assert "ResolveSessionAgent(lease)" in pipeline
    assert "SessionGeneration = source.SessionGeneration" in pipeline
    assert "SemanticContinuationContext = source.SemanticContinuationContext" in pipeline
    assert "await next(combinedLease);" in pipeline


def test_pipeline_stays_outside_streaming_wrapper_and_avoids_stale_image_binding():
    pipeline = text("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "ReferenceEquals(current, installed)" in pipeline
    assert "handlerField.SetValue(coordinator, wrapped);" in pipeline
    assert "图片指代续问已重新绑定最近图片" in pipeline
    assert "RecentVision.TryRemove(conversationKey" in pipeline
    assert "elapsed >= TimeSpan.FromSeconds(-SourceClockSkewToleranceSeconds)" in pipeline
    assert "elapsed > TimeSpan.FromSeconds(FollowUpWindowSeconds)" in pipeline
    assert "!referential && !likelyCaption" in pipeline


def test_existing_vision_flow_receives_image_text_and_timeline_together():
    qn = text("src/Bot/ChromeNs/QN.cs")
    vision = text("src/Bot/ChromeNs/VisionRequestService.cs")
    assert "var visionItem = burst.LatestVisionItem;" in qn
    assert "CombinedQuestion = burst.CombinedQuestion" in qn
    assert "图片和这些文字属于同一轮，请合并理解后只回复一次" in vision
    assert "ConversationContextStore.BuildTimelineText" in vision
    assert "买家发送的图片可能是在回答最近一条客服问题" in vision
