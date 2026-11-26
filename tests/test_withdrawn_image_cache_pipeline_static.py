from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_incoming_image_is_cached_before_burst_delay_and_withdrawal_is_recorded():
    source = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")

    assert "VisionImageCacheService.Prime(message, messageText, shopKey, browserClient);" in source
    assert "CDPClient browserClient = null" in source
    assert "VisionImageCacheService.MarkLatestBuyerImageWithdrawn(message, messageText, shopKey);" in source
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

    assert "VisionImageCacheService.ResolveAsync(message, endpoint, cancellationToken, shopKey, browserClient)" in source
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


def test_vision_cache_records_and_withdrawal_markers_are_shopkey_isolated():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    manual = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")

    assert "public string ShopKey { get; set; }" in cache
    assert "public string ShopKey;" in cache
    assert "BuildKey(string shopKey, string messageKey, string url)" in cache
    assert 'return shopKey + "|message:" + messageKey.Trim();' in cache
    assert "ResolveRuntimeByShopKey(shopKey)" in cache
    assert "Records.Values" in cache
    assert "string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal)" in cache
    assert "var current = QN.CurQN;" not in cache
    assert "VisionImageCacheService.Prime(visionItem.Message, visionItem.DisplayText, burst.ShopKey, qn.CDP)" in pipeline
    assert "visionItem.Message,\n                burst.ShopKey)" in pipeline
    assert "out recent,\n                burst.ShopKey)" in pipeline
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in manual
    assert "TryGetRecentReference(\n                seller,\n                buyer,\n                TimeSpan.FromMinutes(5),\n                out recent,\n                shopKey)" in manual
    assert "Key(string shopKey, string seller, string buyer, string messageKey)" in manual


def test_vision_cache_never_uses_global_current_qn_for_shop_identity():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    assert "QN.CurQN" not in cache
    assert "GetSellerNick(message, shopKey)" in cache
    assert "GetSellerNick(notice, shopKey)" in cache
    assert "ResolveRuntimeByShopKey(shopKey)" in cache



def test_image_download_falls_back_to_authenticated_qianniu_browser_and_message_history():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    cdp = read("src/Bot/ChromeNs/CDPClient.cs")
    assert "DownloadAndPersistFromBrowserAsync" in cache
    assert "DownloadBuyerImageWithQianniuSession" in cache
    assert "credentials:'include'" in cache
    assert "im.singlemsg.GetRemoteHisMsg" in cache
    assert "resolvedUrl:candidates.length" not in cache
    assert "resolvedUrl:resolvedUrl" in cache
    assert "DownloadAndPersistDirectAsync(\n                            record, endpoint, payload.resolvedUrl)" in cache
    assert "record.Url = payload.resolvedUrl" not in cache
    assert "var sourceUrl = string.IsNullOrWhiteSpace(urlOverride) ? record.Url : urlOverride" in cache
    assert "IsHttpImageUrl(payload.resolvedUrl)" in cache
    assert "买家图片未能转存到客户端缓存，服务端OCR不会收到图片二进制" in cache
    assert "window._db&&window._db.msgDataMap" in cache
    assert "tokens.some(t=>vals.some(v=>v===t||" in cache
    assert "fileId = original == null ? string.Empty : (original.fileId ?? string.Empty)" in cache
    assert "browserClient.ShopKey" in cache
    assert "IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey, CDP)" in qn
    assert "IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey, CDP)" in recovery
    assert "responseTimeoutMs: responseTimeoutMs" in cdp
    assert "safeResponseTimeoutMs" in cdp


def test_failed_image_download_can_retry_once_with_browser_session_and_refreshed_source():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    assert "var canRetry = (record.DownloadTask == null" in cache
    assert "record.DownloadAttempts < 2" in cache
    assert "record.BrowserClient = browserClient" in cache
    assert "record.Url = url;" in cache
    assert "SameConversation(seller, buyer, record.SellerNick, record.BuyerNick)" in cache
    assert "record.Message = message;" in cache
    assert "record.DownloadTask.IsCompleted && !IsTaskSuccessful(record.DownloadTask)" in cache
    assert "图片获取失败：直连下载=" in cache


def test_withdrawn_image_cache_miss_fallback_rechecks_human_takeover_before_send():
    source = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    fallback = source[
        source.index("private static async Task SendCacheMissFallbackAsync"):
        source.index("private static bool TryRebindRecentCachedImage")
    ]

    assert "ParallelReplyRelevanceGate.ShouldSend(" in fallback
    assert "lifecycleLease.IsCurrent" in fallback
    assert "lifecycleLease.CancellationToken.IsCancellationRequested" in fallback
    assert "preSendGuard" in fallback
    assert "burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken, preSendGuard" in fallback

def test_inflight_image_download_refreshes_media_source_for_the_bounded_retry():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    prime = cache[
        cache.index("public static void Prime("):
        cache.index("private static void EnsureCleanupTimer")
    ]

    assert "var taskPending = record.DownloadTask != null && !record.DownloadTask.IsCompleted;" in prime
    assert "if ((taskPending || canRetry) && !string.IsNullOrWhiteSpace(url))" in prime
    assert "record.Url = url;" in prime
    assert "if ((taskPending || canRetry) && message != null" in prime
    assert "record.Message = message;" in prime
    assert prime.index("record.Url = url;") < prime.index("if (canRetry)")
    assert prime.index("record.Message = message;") < prime.index("if (canRetry)")
    assert "record.DownloadAttempts++;" in prime

def test_resolved_browser_image_url_does_not_mutate_shared_cache_record():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    direct = cache[
        cache.index("private static async Task<CacheDownloadResult> DownloadAndPersistDirectAsync"):
        cache.index("private static async Task<byte[]> ReadWithLimitAsync")
    ]
    browser = cache[
        cache.index("private static async Task<CacheDownloadResult> DownloadAndPersistFromBrowserAsync"):
        cache.index("private static async Task<CacheDownloadResult> DownloadAndPersistDirectAsync")
    ]

    assert "string urlOverride = null" in direct
    assert "var sourceUrl = string.IsNullOrWhiteSpace(urlOverride) ? record.Url : urlOverride" in direct
    assert "Uri.TryCreate(sourceUrl" in direct
    assert 'Sha256(record.Key + "|" + sourceUrl)' in direct
    assert "DownloadAndPersistDirectAsync(" in browser
    assert "payload.resolvedUrl" in browser
    assert "record.Url = payload.resolvedUrl" not in browser
    assert "var originalUrl = record.Url" not in browser

def test_withdrawal_tombstones_are_exact_message_and_conversation_scoped():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    withdrawn = cache[
        cache.index("public static bool IsWithdrawn("):
        cache.index("internal static bool IsMessageKeyWithdrawn")
    ]
    find = cache[
        cache.index("private static CacheRecord FindRecord("):
        cache.index("private static async Task<CacheDownloadResult> DownloadAndPersistAsync")
    ]

    assert "return record != null && record.Withdrawn;" in withdrawn
    assert "return Records.Values" not in withdrawn
    assert "FindRecord(" in withdrawn
    assert "string seller, string buyer" in find
    assert "SameConversation(seller, buyer, record.SellerNick, record.BuyerNick)" in find
    assert "SameConversation(seller, buyer, x.SellerNick, x.BuyerNick)" in find
    assert "string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)" in find


def test_delayed_withdrawal_notice_cannot_mark_an_image_that_arrived_after_the_notice():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    method = cache[
        cache.index("public static void MarkLatestBuyerImageWithdrawn"):
        cache.index("public static async Task<VisionImageResult> ResolveAsync")
    ]

    assert "var noticeAt = GetObservedAt(notice);" in method
    assert "var cutoff = noticeAt.AddMinutes(-5);" in method
    assert "x.ObservedAt <= noticeAt.AddSeconds(2)" in method
    assert "SameConversation(seller, buyer, x.SellerNick, x.BuyerNick)" in method
    assert "ThenBy(x => x.MessageKey, StringComparer.Ordinal)" in method



def test_withdrawal_notice_after_restart_falls_back_to_durable_visual_observation():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    learning = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")
    withdraw = cache.split("public static void MarkLatestBuyerImageWithdrawn", 1)[1].split(
        "public static async Task<VisionImageResult> ResolveAsync", 1
    )[0]
    assert "VisualKnowledgeLearningService.FindLatestObservationMessageKey(" in withdraw
    assert "MarkObservationWithdrawn(" in withdraw
    assert "FindLatestObservationMessageKey" in learning


def test_vision_withdrawal_checks_durable_observation_tombstone_after_cache_restart():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    is_withdrawn = cache.split("public static bool IsWithdrawn(", 1)[1].split(
        "internal static bool IsMessageKeyWithdrawn", 1
    )[0]
    by_message_key = cache.split("internal static bool IsMessageKeyWithdrawn(", 1)[1].split(
        "public static async Task<string> TryGetCachedImagePathAsync", 1
    )[0]
    assert "VisualKnowledgeLearningService.IsObservationWithdrawn(" in is_withdrawn
    assert "!string.IsNullOrWhiteSpace(messageKey)" in is_withdrawn
    assert "VisualKnowledgeLearningService.IsObservationWithdrawn(" in by_message_key
    assert "record != null && record.Withdrawn" in is_withdrawn
    assert "record != null && record.Withdrawn" in by_message_key


def test_duplicate_withdrawal_notice_reuses_latest_existing_tombstone_instead_of_marking_older_image():
    learning = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")
    lookup = learning.split("internal static string FindLatestObservationMessageKey", 1)[1].split(
        "internal static void MarkObservationWithdrawn", 1
    )[0]
    assert "Duplicate/replayed" in lookup
    assert 'x.Status, "图片已撤回"' not in lookup
    assert "SameBuyerForWithdrawal(seller, x.Buyer, buyer)" in lookup
    assert "order by ObservedAtTicks desc, UpdatedAtTicks desc" in lookup


def test_duplicate_withdrawal_notice_does_not_skip_latest_in_memory_tombstone():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    withdraw = cache.split("public static void MarkLatestBuyerImageWithdrawn", 1)[1].split(
        "public static async Task<VisionImageResult> ResolveAsync", 1
    )[0]
    assert "Duplicate/replayed withdrawal notices" in withdraw
    assert ".Where(x => !x.Withdrawn" not in withdraw
    assert "OrderByDescending(x => x.ObservedAt)" in withdraw
    assert "FindLatestObservationMessageKey(" in withdraw
