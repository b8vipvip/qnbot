"""Static regression contracts for bounded and lifecycle-safe WPF client resources."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_dashboard_timers_are_reused_and_stopped_when_unloaded():
    ui = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    diagnostics = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.DataDesk.cs")
    assert "if (_statsTimer == null)" in ui
    assert "Unloaded += CtlRobot_Unloaded;" in ui
    assert "if (_statsTimer != null) _statsTimer.Stop();" in ui
    assert "if (_dashboardVisible) RefreshStats();" in ui
    assert "if (!_dashboardVisible) return;" in ui
    assert "Loaded += CtlRobot_DiagnosticsLoaded;" in diagnostics
    assert "Unloaded += CtlRobot_DiagnosticsUnloaded;" in diagnostics
    assert "if (_diagnosticsTimer != null) _diagnosticsTimer.Stop();" in diagnostics


def test_ui_only_answer_cache_is_bounded_without_deleting_history():
    ui = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    history = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.History.cs")
    assert "MaxCachedBuyerPanels = 16;" in ui
    assert "MaxCachedAnswerCardsPerBuyer = 120;" in ui
    assert "buyerConversations.TryRemove(key, out stale)" in ui
    assert "string.Equals(key, activeKey, StringComparison.Ordinal)" in ui
    assert "_historyLoadedKeys.TryRemove(key, out ignoredHistory);" in ui
    assert "ReleaseConversationUiHandlers(stale);" in ui
    assert "PruneConversationUiCache(key);" in ui
    assert "PruneConversationUiCache(key);" in history
    assert "BotConversationHistoryStore.LoadRecent(" in history
    assert "BotConversationHistoryStore" in source("src/Bot/ChromeNs/BotConversationHistoryStore.cs")


def test_mirror_fallback_poll_is_throttled_without_disabling_refresh():
    mirror = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "Interval = TimeSpan.FromSeconds(1)" in mirror
    assert "RefreshMessageMirror(true);" in mirror
    assert "if (_messageMirrorTimer != null) _messageMirrorTimer.Stop();" in mirror


def test_minute_resource_telemetry_is_non_invasive_and_privacy_safe():
    monitor = source("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "HeartbeatIntervalSeconds = 60;" in monitor
    assert "Bot资源采样: workingSetMiB=" in monitor
    assert "process.PrivateMemorySize64" in monitor
    assert "GC.GetTotalMemory(false)" in monitor
    assert "GC.CollectionCount(2)" in monitor
    assert "process.HandleCount" in monitor
    assert "monitoredQn=" in monitor
    assert "GC.Collect(" not in monitor
