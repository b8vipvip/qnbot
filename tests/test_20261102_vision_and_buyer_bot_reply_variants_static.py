"""Confirmed Bot echoes are scanned separately from knowledge answers; OCR fallback stays bounded."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_scan_pairs_only_transport_verified_bot_echoes():
    scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    evidence = read("src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs")
    assert "MatchBotRepliesToBuyerTurns" in scan
    assert 'turn.Item1 != "seller"' in scan
    assert 'raw.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)' in scan
    assert 'TimeSpan.FromMinutes(30)' in scan
    assert "pendingBuyerKey = null;" in scan
    assert "botReply" in scan
    assert 'bot_reply = botReply' in evidence
    assert "BuyerMessageArchiveService.Redact(turn.Item4)" in evidence


def test_archive_retains_distinct_verified_bot_replies():
    merge = read("src/Bot/Knowledge/BuyerHistoryScanMessageMergeService.cs")
    archive = read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    page = read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert 'record["bot_reply"]' in merge
    assert "Fingerprint(text, time, botReply)" in merge
    assert "public string BotReplyText { get; set; }" in archive
    assert "previous.BotReplyText = botReplyText" in archive
    assert "unpaired.BotReplyText = botReply;" in archive
    assert "BotReply = x.BotReplyText ?? string.Empty" in archive
    assert 'Header = "Bot回复消息"' in page
    assert 'new Binding("BotReplyText")' in page
    assert 'Header = "复制Bot回复消息"' in page
    assert 'BotReplyText = g.Messages.First().BotReplyText ?? string.Empty' in page


def test_ocr_timeout_upgrade_does_not_override_manual_settings():
    server = read("services/api-control-plane/runtime_ocr.py")
    client = read("src/Bot/ChromeNs/LocalOcrService.cs")
    assert '_env_float("OCR_TIMEOUT_SECONDS", 16.0' in server
    assert 'row["updated_by"] == "environment-defaults"' in server
    assert 'float(row["timeout_seconds"]) == 8.0' in server
    assert 'os.getenv("OCR_TIMEOUT_SECONDS") is None' in server
    assert "DefaultTimeoutMs = 22000" in client
    assert "TryGetCachedRecognition" in client


def test_no_multimodal_endpoint_can_use_ocr_text_only_with_strict_guards():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    gate = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    send = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    assert "TryOcrTextAiFallbackAsync(task, cancellationToken)" in vision
    assert "LocalOcrService.TryGetCachedRecognition" in vision
    assert "ocr.Confidence < 0.88" in vision
    assert "MyOpenAI.CallStructuredChat(" in vision
    assert 'EndpointName = "ocr-text-ai-fallback"' in vision
    assert "trustedReviewedImageAnswer = false" in gate
    assert "humanEchoes.All(x => IsGreetingOnly(x.Text))" in gate
    assert "out humanReason, trustedOcrKnowledge" in send
