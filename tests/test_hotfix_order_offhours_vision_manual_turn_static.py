from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ORDER = (ROOT / "src/Bot/ChromeNs/BotActivityCoordinator.cs").read_text(encoding="utf-8-sig")
DETERMINISTIC = (ROOT / "src/Bot/ChromeNs/DeterministicAutoReplyService.cs").read_text(encoding="utf-8-sig")
RUNTIME = (ROOT / "src/Bot/ChromeNs/QN.RuntimeSafety.cs").read_text(encoding="utf-8-sig")
VISION = (ROOT / "src/Bot/ChromeNs/VisionMessageDecision.cs").read_text(encoding="utf-8-sig")
KNOWLEDGE = (ROOT / "src/Bot/Knowledge/KnowledgeCenterWindow.cs").read_text(encoding="utf-8-sig")


def test_fixed_order_preset_does_not_wait_for_trade_enrichment():
    assert 'var fixedPreset = !string.Equals(mode, "调用HTTP接口", StringComparison.Ordinal);' in ORDER
    assert 'if (!fixedPreset' in ORDER
    assert 'OrderTemplateRequiredFieldsV2.TryOwnExistingPlan' in ORDER
    assert '固定预设进入即时发送路径，不等待交易字段补全' in ORDER
    assert 'await ProcessOrderPlacedReplyAsync(plan);' in ORDER


def test_invalid_work_hours_never_fall_back_to_0900_1800():
    deterministic_offhours = DETERMINISTIC.split('private static bool TryResolveOffHours(out string answer)', 1)[1]
    deterministic_offhours = deterministic_offhours.split('private static bool TryParseClock', 1)[0]
    runtime_offhours = RUNTIME.split('private static bool ShouldSuppressForOffHours', 1)[1]
    runtime_offhours = runtime_offhours.split('private static bool TryParseClock', 1)[0]

    assert '|| !TryParseClock(cfg.WorkEndTime, out end)' in deterministic_offhours
    assert 'return false;' in deterministic_offhours
    assert 'new TimeSpan(9, 0, 0)' not in deterministic_offhours
    assert 'new TimeSpan(18, 0, 0)' not in deterministic_offhours

    assert '|| !TryParseClock(cfg.WorkEndTime, out end)' in runtime_offhours
    assert 'return false;' in runtime_offhours
    assert 'new TimeSpan(9, 0, 0)' not in runtime_offhours
    assert 'new TimeSpan(18, 0, 0)' not in runtime_offhours


def test_local_ocr_knowledge_can_route_images_without_external_vision_api():
    assert 'var localUsable = CanUseLocalOcrKnowledge(seller);' in VISION
    assert 'var usable = localUsable || externalUsable;' in VISION
    assert 'Kind = VisionDecisionKind.Vision' in VISION
    assert '使用本地OCR+Knowledge V2图片理解；无需外部视觉模型。' in VISION
    assert 'KnowledgeEngineV2Service.IsEnabled(sellerNick)' in VISION
    assert 'ReplyModeService.IsLocalFirst(sellerNick)' not in VISION
    assert 'KnowledgeEngineV2Service.IsEnabled(sellerNick)' in VISION
    assert 'KnowledgeEngineV2Service.IsSnapshotReady(sellerNick)' in VISION


def test_manual_ai_optimization_is_idempotent_per_buyer_turn_for_ten_minutes():
    start = KNOWLEDGE.split('public static void StartForManualReply', 1)[1]
    start = start.split('public static List<AiOptimizationRecordView>', 1)[0]
    run = KNOWLEDGE.split('private static async Task RunAsync', 1)[1]
    run = run.split('private static string BuildRecentBuyerQuestion', 1)[0]

    assert 'ManualTurnReservationMinutes = 10' in KNOWLEDGE
    assert 'TryReserveManualTurn' in KNOWLEDGE
    assert 'BuildManualTurnKey(seller, buyer, question)' in run
    assert 'if (!TryReserveManualTurn(key, DateTime.Now)) return;' in run
    assert 'Normalize(humanAnswer)' not in start
    assert 'TryRemove(key' not in start
