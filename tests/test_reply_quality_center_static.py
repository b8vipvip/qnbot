from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_quality_metrics_are_daily_private_and_bounded():
    code = read("src/Bot/ChromeNs/ReplyQualityMetricsService.cs")
    daily = code.split("internal sealed class ReplyQualityDailyMetric", 1)[1].split(
        "internal sealed class ReplyQualitySummary", 1
    )[0]
    assert "reply-quality-metrics.json" in code
    assert "AddDays(-89)" in code
    assert "samples.Count > 240" in code
    assert "TimeSpan.FromSeconds(30)" in code
    assert "RouteDirect" in daily
    assert "ValidationPass" in daily
    assert "SendSuccess" in daily
    assert "HumanCorrection" in daily
    assert "AnswerLatencyMs" in daily
    for forbidden in ("Buyer", "Question", "AnswerText", "OrderId", "MessageContent"):
        assert forbidden not in daily


def test_quality_center_has_ranges_reports_and_privacy_notice():
    ui = read("src/Bot/Options/ReplyQualityCenterUi.cs")
    assert 'TabHeader = "回复质量中心"' in ui
    assert 'Name = "今天", Days = 1' in ui
    assert 'Name = "最近7天", Days = 7' in ui
    assert 'Name = "最近30天", Days = 30' in ui
    assert 'Name = "最近90天", Days = 90' in ui
    assert "复制质量报告" in ui
    assert "打开数据目录" in ui
    assert "不保存买家名称、聊天内容、答案正文或订单信息" in ui
    card = ui.split("private static TextBlock Card", 1)[1].split("private static void AddCard", 1)[0]
    assert "Padding =" not in card
    assert "Margin =" in card


def test_response_progress_records_route_answer_latency_without_false_manual_cancellation():
    code = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "ReplyQualityMetricsService.RecordRoute" in code
    assert "ReplyQualityMetricsService.RecordAnswerReady" in code
    assert "ResolveQualityRoute" in code
    assert 'return "DIRECT_KNOWLEDGE"' in code
    assert 'return "CONTEXTUAL_KNOWLEDGE"' in code
    assert 'return "VISION"' in code
    manual_start = code.index("public static void MarkManualIntervention")
    manual_end = code.index("public static void ObserveNewBuyerTurn", manual_start)
    assert "RecordCancellation" not in code[manual_start:manual_end]
    assert "Bot继续" in code[manual_start:manual_end]
    assert "MessageProcessingTraceService.RecordCancelled" in code


def test_real_send_metrics_distinguish_echo_submission_and_unproven_timeout():
    code = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")

    timeout_remove = code.index("Pending.TryRemove(pending.Id")
    submission_branch = code.index("if (!delivered && submissionTicks > 0)", timeout_remove)
    submission_metric = code.index("ReplyQualityMetricsService.RecordSendResult", submission_branch)
    true_timeout_branch = code.index("if (!delivered)", submission_branch + 1)
    timeout_metric = code.index("ReplyQualityMetricsService.RecordSendResult", true_timeout_branch)

    confirm = code.index("public static bool ConfirmDelivery")
    confirm_remove = code.index("Pending.TryRemove(pair.Key", confirm)
    confirm_metric = code.index("ReplyQualityMetricsService.RecordSendResult", confirm_remove)

    # A seller echo is the strongest proof and remains a successful send metric.
    assert confirm < confirm_remove < confirm_metric
    assert "true," in code[confirm_metric:confirm_metric + 160]

    # A verified Qianniu submission is also successful for transport metrics even when the live
    # echo is absent; this is the state that used to create false failures and duplicate retries.
    assert timeout_remove < submission_branch < submission_metric < true_timeout_branch
    assert "true," in code[submission_metric:submission_metric + 160]
    assert "[本店发送回显缺失但提交已确认]" in code[submission_branch:true_timeout_branch]

    # Only when neither seller echo nor verified submission exists may the watchdog count failure.
    assert true_timeout_branch < timeout_metric < confirm
    assert "false," in code[timeout_metric:timeout_metric + 160]
    assert "也没有取得输入框稳定清空等千牛提交证据" in code[true_timeout_branch:confirm]


def test_validator_and_reviewed_knowledge_feed_quality_metrics():
    dedup = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    review = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")
    assert dedup.count("ReplyQualityMetricsService.RecordValidation") >= 3
    assert "ReplyQualityMetricsService.RecordRepair(false)" in dedup
    assert "ReplyQualityMetricsService.RecordRepair(true)" in dedup
    assert "ReplyQualityMetricsService.RecordDuplicateRewrite()" in dedup
    assert "ReplyQualityMetricsService.RecordHumanEvidence(evidenceType)" in review
    assert '"manual_reply"' in review
    assert '"manual_correction"' in review
    assert '"operator_reviewed"' in review
    assert "TrySaveReviewedDirectAnswer(" in review


def test_quality_center_is_built_initialized_and_flushed():
    targets = read("src/Directory.Build.targets")
    app = read("src/Bot/App.xaml.cs")
    assert "ReplyQualityMetricsService.cs" in targets
    assert "ReplyQualityCenterUi.cs" in targets
    assert "ReplyQualityCenterUi.Initialize()" in app
    assert "ReplyQualityMetricsService.Flush()" in app
