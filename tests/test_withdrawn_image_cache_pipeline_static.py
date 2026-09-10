from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_incoming_image_is_cached_before_burst_delay_and_withdrawal_is_recorded():
    source = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")

    assert "VisionImageCacheService.Prime(message, messageText);" in source
    assert "VisionImageCacheService.MarkLatestBuyerImageWithdrawn(message, messageText);" in source
    assert "图片若已收到将继续后台分析" in source
    assert source.index("MarkLatestBuyerImageWithdrawn") < source.index('return Skip("[撤回提示]"')


def test_raw_images_are_persisted_under_permanent_user_data_with_atomic_write_and_retention():
    source = read("src/Bot/ChromeNs/VisionImageCacheService.cs")

    assert '"QianniuAiBot",' in source
    assert '"data",' in source
    assert '"vision-cache"' in source
    assert "File.WriteAllBytes(temp, bytes);" in source
    assert "File.Move(temp, path);" in source
    assert "CacheRetentionHours = 24" in source
    assert "MaxCacheBytes = 512L * 1024L * 1024L" in source
    assert "图片已完整缓存到本地" in source


def test_vision_resolver_uses_local_complete_copy_instead_of_remote_url():
    source = read("src/Bot/ChromeNs/VisionImageResolver.cs")

    assert "VisionImageCacheService.ResolveAsync(message, endpoint, cancellationToken)" in source
    assert "FromLocalCache" in source
    assert "CacheComplete" in source
    assert "http.GetAsync" not in source


def test_withdrawn_image_keeps_analysis_but_suppresses_image_only_reply():
    source = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    analysis = source.index("Vision.ExecuteAsync(task, CancellationToken.None)")
    withdrawn = source.index("VisionImageCacheService.IsWithdrawn(")
    suppress = source.index("if (withdrawn && !hasFollowUpText)")
    send = source.index("burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken")

    assert analysis < withdrawn < suppress < send
    assert "撤回只取消旧回复，图片语义已保存供后续对话使用" in source
    assert "最新消息或人工客服已接管" in source
    assert "后续回复可继续使用本次图片分析结果" in source


def test_follow_up_can_reuse_withdrawn_cached_image_and_only_requests_resend_on_cache_failure():
    source = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    assert "TryRebindRecentCachedImage" in source
    assert "TryGetRecentReference" in source
    assert "recent.Withdrawn" in source
    assert "recent.CacheComplete" in source
    assert "withdrawn && hasFollowUpText && !cacheComplete" in source
    assert "刚才图片已撤回且未能完整保存，请重新发送清晰图片后我再确认" in source
    helper = source[source.index("private static bool ShouldBindToRecentImage"):source.index("private static bool HasSubstantiveFollowUpText")]
    assert "VisionFollowUpContextPipeline.IsVisionReferentialFollowUp(text)" in helper
    assert 'compact.Contains("可以")' not in helper
    assert 'compact.Contains("充值")' not in helper
    assert 'compact.Contains("能充")' not in helper


def test_rebound_lease_uses_original_lifecycle_owner_and_cannot_recurse_into_itself():
    source = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    capture = source.index("var lifecycleLease = lease;")
    source_lease = source.index("var sourceLease = lease;", capture)
    rebound = source.index("lease = new BuyerMessageBurstLease(burst, () => sourceLease.IsCurrent);", source_lease)
    assert capture < source_lease < rebound
    assert "new BuyerMessageBurstLease(burst, () => lease.IsCurrent)" not in source
    assert "CtlConversation ctl" in source
    assert "dynamic ctl" not in source


def test_pipeline_is_initialized_between_streaming_and_existing_visual_followup_wrapper():
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    streaming = app.index("BuyerStreamingReplyPipeline.Initialize();")
    withdrawn = app.index("VisionWithdrawalAwarePipeline.Initialize();")
    followup = app.index("VisionFollowUpContextPipeline.Initialize();")
    assert streaming < withdrawn < followup
    assert "VisionImageCacheService.cs" in targets
    assert "VisionWithdrawalAwarePipeline.cs" in targets
