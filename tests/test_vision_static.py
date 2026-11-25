import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')


def test_endpoint_fields_and_defaults():
    text = read('src/Bot/Options/CtlRobotOptions.xaml.cs')
    for field in ['TextModel', 'VisionModel', 'SupportsVision', 'MaxImageSizeMb', 'VisionTimeoutSeconds']:
        assert 'public ' in text and field in text
    assert 'SupportsVision = false' in text
    assert 'MaxImageSizeMb = 5' in text
    assert 'VisionTimeoutSeconds = 45' in text
    assert 'Math.Max(1, Math.Min(20, MaxImageSizeMb))' in text
    assert 'Math.Max(10, Math.Min(180, VisionTimeoutSeconds))' in text


def test_visual_config_ui_and_paste_recognition():
    ui = read('src/Bot/Options/CtlRobotOptions.ApiManagement.cs')
    assert '启用图片视觉理解' in ui
    assert '测试视觉能力' in ui
    assert 'VisionModelNames' in ui
    for name in ['vision_model', 'image_model', 'multimodal_model']:
        assert name in ui
    grid = read('src/Bot/Options/CtlRobotOptions.xaml')
    assert 'Binding="{Binding VisionStatus}"' in grid
    assert 'ApiKeyMasked' in grid


def test_multimodal_payload_uses_vision_model_image_array_and_timeline():
    service = read('src/Bot/ChromeNs/VisionRequestService.cs')
    assert '["model"] = endpoint.VisionModel' in service
    assert '["type"] = "text"' in service
    assert '["type"] = "image_url"' in service
    assert 'endpoint.VisionTimeoutSeconds' in service
    assert 'GetVisionEnabledEndpoints' in service
    assert 'ConversationContextStore.BuildTimelineText' in service
    assert '不得混入其他买家信息' in service


def test_message_safety_and_cross_buyer_guard_remain_in_path():
    qn = read('src/Bot/ChromeNs/QN.cs')
    assert 'IncomingMessageSafety.Evaluate' in qn
    assert 'BuildMessageKey' in qn
    assert 'var task = new VisionReplyTask' in qn
    assert 'SellerNick = burst.SellerNick' in qn
    assert 'BuyerNick = burst.BuyerNick' in qn
    assert 'MessageKey = visionItem.MessageKey' in qn
    assert 'CombinedQuestion = burst.CombinedQuestion' in qn
    assert (
        "burst.BuyerNick, answer, 1, lease.CancellationToken, visionPreSendGuard"
        in " ".join(qn.split())
    )
    assert 'EnsureActiveBuyerForSendAsync' in qn and 'GetCurrentConversationID' in qn
    assert '识别完成，但目标买家会话未确认，未发送。' in qn


def test_image_resolver_downloads_caches_and_validates_every_image():
    resolver = read('src/Bot/ChromeNs/VisionImageResolver.cs')
    cache = read('src/Bot/ChromeNs/VisionImageCacheService.cs')
    combined = resolver + '\\n' + cache
    for mime in ['image/jpeg', 'image/png', 'image/webp', 'image/gif']:
        assert mime in combined
    assert 'image/svg' not in combined
    assert '图片超过大小限制' in combined
    assert '图片 MIME 与实际内容不一致' in combined
    assert '图片数据损坏或格式不支持' in combined
    assert 'data:" + detectedMime + ";base64,"' in combined
    assert 'message.originalData.url.Trim()' in resolver
    assert 'message.originalData.fileId.Trim()' in resolver
    assert 'http.GetAsync(' in cache
    assert 'HttpCompletionOption.ResponseHeadersRead' in cache
    assert 'File.WriteAllBytes(temp, bytes)' in cache
    assert 'File.Move(temp, path)' in cache
    assert 'VisionImageCacheService.ResolveAsync' in resolver
    assert 'HasSensitiveQuery' not in combined
    all_changed = '\\n'.join(read(p) for p in [
        'src/Bot/ChromeNs/QN.cs',
        'src/Bot/ChromeNs/VisionRequestService.cs',
        'src/Bot/ChromeNs/VisionImageResolver.cs',
        'src/Bot/ChromeNs/VisionImageCacheService.cs'
    ])
    assert 'Log.Info(dataUri' not in all_changed
    assert 'Log.Info(image.ImageUrl' not in all_changed


def test_all_supported_image_extensions_enter_the_buyer_image_route():
    safety = read('src/Bot/ChromeNs/IncomingMessageSafety.cs')
    chat = read('src/Bot/ChatRecord/ChatResponse.cs')
    extensions = ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.heic', '.heif']
    image_list = safety.split('ImageExtensions =', 1)[1].split(';', 1)[0]
    chat_image = chat.split('public bool IsImage(string txt)', 1)[1].split('public class', 1)[0]
    for extension in extensions:
        assert extension in image_list
        assert extension in chat_image


def test_bmp_and_decodable_heif_buyer_images_are_normalized_before_vision_and_ocr():
    resolver = read('src/Bot/ChromeNs/VisionImageResolver.cs')
    cache = read('src/Bot/ChromeNs/VisionImageCacheService.cs')
    safety = read('src/Bot/ChromeNs/IncomingMessageSafety.cs')

    assert '".bmp"' in safety and '".heic"' in safety
    assert 'return "image/bmp"' in resolver
    assert 'return "image/heic"' in resolver
    assert 'return "image/heif"' in resolver
    assert 'BitmapDecoder.Create(' in resolver
    assert 'BitmapCacheOption.OnDemand' in resolver
    assert 'HasSafeBmpDimensions(sourceBytes' in resolver
    assert 'BitConverter.ToUInt32(bytes, 14)' in resolver
    assert 'new PngBitmapEncoder()' in resolver
    assert 'TryNormalizeForVision(' in cache
    assert cache.count('TryNormalizeForVision(') >= 2
    assert 'normalizedBytes.LongLength > maxBytes' in cache
    assert '当前 Windows 未安装可用的 HEIC/HEIF 解码组件' in resolver
    assert 'image/bmp' not in cache.split('private static readonly string[] AllowedMimeTypes', 1)[1].split('};', 1)[0]


def test_image_transfer_uses_authenticated_qianniu_session_when_direct_url_download_fails():
    cache = read('src/Bot/ChromeNs/VisionImageCacheService.cs')
    ocr = read('src/Bot/ChromeNs/LocalOcrService.cs')
    qn = read('src/Bot/ChromeNs/QN.cs')
    recovery = read('src/Bot/ChromeNs/QN.MessageRecovery.cs')
    assert 'DownloadAndPersistFromBrowserAsync' in cache
    assert "credentials:'include'" in cache
    assert 'im.singlemsg.GetRemoteHisMsg' in cache
    assert 'Array.from(map.values?map.values():Object.values(map))' in cache
    assert 'new ByteArrayContent(imageBytes)' in ocr
    assert 'HttpMethod.Post, NormalizeOcrUrl(endpoint.BaseUrl)' in ocr
    assert 'IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey, CDP)' in qn
    assert 'IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt, shopKey, CDP)' in recovery



def test_vision_reply_task_keeps_authoritative_browser_session_for_image_resolution():
    task = read('src/Bot/ChromeNs/VisionReplyTask.cs')
    pipeline = read('src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs')
    service = read('src/Bot/ChromeNs/VisionRequestService.cs')
    ocr_first = read('src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs')
    assert 'public CDPClient BrowserClient { get; set; }' in task
    assert 'BrowserClient = qn.CDP' in pipeline
    assert 'task.BrowserClient' in service
    assert 'task.BrowserClient' in ocr_first



def test_direct_qn_vision_task_carries_current_shop_cdp_session():
    qn = read('src/Bot/ChromeNs/QN.cs')
    task_start = qn.index('var task = new VisionReplyTask')
    task_end = qn.index('};', task_start)
    task = qn[task_start:task_end]
    assert 'BrowserClient = CDP' in task



def test_ocr_text_fallback_preserves_authoritative_shop_browser_session():
    service = read('src/Bot/ChromeNs/VisionRequestService.cs')
    fallback = service[service.index('private async Task<VisionRequestResult> TryOcrTextAiFallbackAsync'):]
    assert 'task.ShopKey, task.BrowserClient' in fallback
    assert 'LocalOcrService.TryGetCachedRecognition(image.LocalCachePath)' in fallback


def test_server_ocr_upload_requires_complete_client_image_cache_and_posts_binary():
    cache = read('src/Bot/ChromeNs/VisionImageCacheService.cs')
    ocr = read('src/Bot/ChromeNs/LocalOcrService.cs')
    assert 'DownloadAndPersistFromBrowserAsync' in cache
    assert '买家图片未能转存到客户端缓存，服务端OCR不会收到图片二进制' in cache
    assert 'ReadAllBytesAsync(imagePath, timeout.Token)' in ocr
    assert 'new ByteArrayContent(imageBytes)' in ocr
    assert 'HttpMethod.Post, NormalizeOcrUrl(endpoint.BaseUrl)' in ocr
    assert 'X-Image-Sha256' in ocr


def test_ocr_text_fallback_includes_the_same_buyers_recent_conversation_context():
    service = read('src/Bot/ChromeNs/VisionRequestService.cs')
    fallback = service[service.index('private async Task<VisionRequestResult> TryOcrTextAiFallbackAsync'):]
    assert 'BuildScopedTimelineText(task, question, 12)' in fallback
    assert '最近同一买家与客服对话（用于理解当前图片的上下文）' in fallback
    assert 'OCR文字AI兜底未获得有效答案' in fallback


def test_vision_timeline_reads_are_shopkey_scoped():
    service = read('src/Bot/ChromeNs/VisionRequestService.cs')
    assert 'BuildScopedTimelineText(task, currentQuestion, 16)' in service
    assert 'BuildScopedTimelineText(task, question, 12)' in service
    helper = service[service.index('private static string BuildScopedTimelineText'):service.index('public static JObject BuildVisionPayload')]
    assert 'ResolveRuntimeByShopKey(task.ShopKey)' in helper
    assert 'ShopSettingsScope.Enter(shop, false)' in helper


def test_cached_image_rebinding_preserves_shop_key_in_both_paths():
    followup = read('src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs')
    withdrawal = read('src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs')
    assert 'ShopKey = source.ShopKey' in followup
    assert 'ShopKey = burst.ShopKey' in withdrawal
