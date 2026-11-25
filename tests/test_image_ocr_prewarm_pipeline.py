from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_ocr_starts_after_image_cache_without_waiting_for_reply_generation():
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    prime = cache[cache.index("public static void Prime("):cache.index("private static void StartOcrPrewarm")]
    prewarm = cache[cache.index("private static void StartOcrPrewarm"):cache.index("private static void EnsureCleanupTimer")]

    assert "StartOcrPrewarm(record, downloadTaskToPrewarm);" in prime
    assert "Task.Run(async () =>" in prewarm
    assert "await downloadTask.ConfigureAwait(false)" in prewarm
    assert "LocalOcrService.TryRecognizeAsync(" in prewarm
    assert "CancellationToken.None" in prewarm
    assert "LocalOcrService.IsServerOcrConfigured(shop.SellerNick, record.ShopKey)" in prewarm

    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    assert "VisionImageCacheService.Prime(message, messageText, shopKey, browserClient);" in safety


def test_same_shop_same_image_ocr_upload_is_shared_and_cancellation_only_stops_waiting():
    service = read("src/Bot/ChromeNs/LocalOcrService.cs")
    assert "ConcurrentDictionary<string, Lazy<Task<LocalOcrResult>>>" in service
    assert 'var inFlightKey = scopeKey + "|" + sha256;' in service
    assert "InFlight.GetOrAdd(inFlightKey, candidate)" in service
    assert "RecognizeAndCacheAsync(imagePath, sellerNick, shopKey, sha256, timeoutMs)" in service
    assert "后台共享任务继续执行" in service
    assert "AwaitSharedTaskAsync(sharedTask, cancellationToken)" in service


def test_premerge_queue_and_rule_execution_are_logged_separately():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "买家消息预合并规则开始" in coordinator
    assert "买家消息预合并规则完成" in coordinator
    assert "买家消息合并队列派发" in coordinator
    assert "durationMs=" in coordinator
    assert "queueMs=" in coordinator
