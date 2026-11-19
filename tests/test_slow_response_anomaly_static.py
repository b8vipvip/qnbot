from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_slow_response_threshold_triggers_after_answer_ready_without_blocking_send():
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    service = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")

    assert "AnswerStartedAt" in tracker
    assert "SlowResponseAnomalyService.QueueIfSlow" in tracker
    assert "ThresholdSeconds = 15" in service
    assert "if (totalMs <= ThresholdSeconds * 1000L) return;" in service
    assert "Task.Run(async () =>" in service


def test_slow_response_analysis_splits_queue_and_generation_latency():
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    service = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")

    assert "MarkAnswerStarted" in tracker
    assert "QueueMilliseconds" in service
    assert "GenerationMilliseconds" in service
    assert "消息聚合/排队耗时(ms)" in service
    assert "答案生成耗时(ms)" in service


def test_slow_response_report_calls_ai_persists_and_notifies_wecom():
    service = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")

    assert "MyOpenAI.CallStructuredChat" in service
    assert '"slow-response-anomalies.json"' in service
    assert "SaveOrUpdate(report)" in service
    assert "WeComAppBridgeClient.SendNotificationAsync" in service
    assert '"msgtype"' in service and '"text"' in service
    assert "[慢响应异常报告]" in service


def test_logs_and_debug_gets_anomaly_report_subtab():
    ui = read("src/Bot/Options/SlowResponseDiagnosticsUi.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert 'string.Equals(header, "日志与调试"' in ui
    assert 'Header = "运行日志"' in ui
    assert 'Header = "异常报告"' in ui
    assert "SlowResponseAnomalyService.GetReports(200)" in ui
    assert "SlowResponseDiagnosticsUi.Initialize();" in app
    assert "SlowResponseAnomalyService.cs" in targets
    assert "SlowResponseDiagnosticsUi.cs" in targets


def test_report_contains_ai_diagnosis_and_wecom_delivery_status():
    service = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")

    for field in (
        "AnalysisStatus",
        "Summary",
        "LikelyCause",
        "Evidence",
        "Recommendations",
        "NotificationStatus",
    ):
        assert field in service
    assert "最近运行日志尾部" in service
    assert "自动AI分析调用失败" in service


def test_report_read_failure_never_overwrites_existing_anomaly_history():
    service = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")
    save = service[service.index("private static void SaveOrUpdate"):service.index("private static List<SlowResponseAnomalyReport> LoadReportsUnsafe")]
    loader = service[service.index("private static bool TryLoadReportsUnsafe"):service.index("private static void SaveReportsUnsafe")]

    assert "TryLoadReportsUnsafe(out reports)" in save
    assert "if (!TryLoadReportsUnsafe(out reports))" in save
    assert "return;" in save
    assert "reports = null;" in loader
    assert "return false;" in loader
    assert "return TryLoadReportsUnsafe(out reports)" in service
