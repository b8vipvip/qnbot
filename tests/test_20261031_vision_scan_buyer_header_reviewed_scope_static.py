"""Regressions for image route, buyer metadata ordering and real history backfill."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_buyer_header_order_only_not_bot_or_agent():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert '(isSeller ? timestamp + " " + sender : sender + " " + timestamp)' in source
    assert 'var sender = isBot ? "Bot" : (isSeller ? seller : buyer);' in source

def test_vision_routing_retains_placeholder_but_calls_ocr_or_multimodal():
    pre = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    assert 'QuestionLabel = "[图片]"' in pre
    assert 'return KnowledgeEngineV2Service.IsEnabled(sellerNick)' in pre
    assert 'if (!KnowledgeEngineV2Service.IsEnabled(task.SellerNick)' in ocr
    assert 'ReplyModeService.IsLocalFirst(task.SellerNick)' not in ocr
    assert '[VisionRoute] route=local_ocr_knowledge' in vision
    assert '[VisionRoute] route=multimodal_ai' in vision
    assert 'var localDecision = await OcrFirstKnowledgeDecisionService.TryResolveAsync' in vision
    assert 'var endpoints = AiEndpointStore.GetVisionEnabledEndpoints()' in vision

def test_history_scan_uses_real_contact_discovery_and_native_paginated_history_without_ai():
    src = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    part = src.split("public async Task<ChatHistoryScanResult> ScanBuyerMessagesAsync(", 1)[1].split(
        "public async Task<ChatHistoryScanResult> ScanAndImportAsync(", 1)[0]
    assert "ReadVisibleChatBuyerListContactsAsync" in part
    assert "ReadContactsFromApisAsync" in part
    assert "ReadHistoryAsync(qn, seller, names[i], ccode, options, token)" in part
    assert 'turns.Where(x => x.Role == "buyer")' in part
    assert "BuyerMessageArchiveService.Observe(seller, turn.Text, turn.MessageKey, turn.Time)" in part
    assert "KnowledgeAiService" not in part
    assert "ImportAsync(" not in part
    assert "originalBuyer" in part and "qn.OpenChat(originalBuyer)" in part
    assert "public string MessageKey;" in src
    assert "IncomingMessageSafety.BuildMessageKey(message, text)" in src

def test_archive_supports_inclusive_date_range_filter_and_scan_with_progress():
    archive = read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    page = read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert "(!start.HasValue || x.ReceivedAtTicks >= start.Value.Ticks)" in archive
    assert "(!end.HasValue || x.ReceivedAtTicks <= end.Value.Ticks)" in archive
    assert 'Button("扫描历史消息", 115)' in page
    assert 'Button("取消扫描", 90)' in page
    assert "new ChatHistoryScanService().ScanBuyerMessagesAsync(" in page
    assert 'ScanAll = true, MaxContacts = 5000' in page
    assert "progress.ContactIndex" in page
    assert "DatePicker" in page

def test_manual_direct_edit_needs_explicit_standalone_scope_for_ambiguous_prompt():
    edit = read("src/Bot/AssistWindow/Widget/Robot/ConversationEditWindow.cs")
    handler = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    sandbox = read("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    engine = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "EditedQuestion" in edit
    assert "parsed.ContextDependent" in edit
    assert 'EditedQuestion == "[图片]"' in edit
    assert "wnd.EditedQuestion" in handler
    assert "SaveDirectAnswer(prompt.Text" in sandbox
    assert "var best = manualExact ?? productionMatches.FirstOrDefault();" in engine
