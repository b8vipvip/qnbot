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


def test_timeline_cache_sweeps_only_after_context_window():
    timeline = source("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert "SoftTimelineStateLimit = 256;" in timeline
    assert "TimelineContextWindow = TimeSpan.FromHours(72);" in timeline
    assert "TimelineMaxIdle = TimeSpan.FromDays(8);" in timeline
    assert "SweepInactiveTimelinesIfDue();" in timeline
    assert "idle < TimelineContextWindow" in timeline
    assert "state.PendingPreset != null && state.PendingPreset.ExpiresAt >= DateTime.Now" in timeline
    assert "ReferenceEquals(current, state)" in timeline
    assert "States.TryRemove(pair.Key, out ignored)" in timeline
    assert "CachedTimelineCount" in timeline
    # Runtime changes must not accidentally truncate the existing 100-turn mirror.
    assert "if (state.Turns.Count > 100)" in timeline


def test_expired_working_memory_does_not_evict_active_buyers():
    index = source("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "SoftWorkingMemoryLimit = 256;" in index
    assert "memory.UpdatedAt >= DateTime.Now.AddMinutes(-45)" in index
    assert "idle < TimeSpan.FromMinutes(60)" in index
    assert "SweepExpiredWorkingMemoryIfDue();" in index
    assert "ReferenceEquals(current, memory)" in index
    assert "Working.TryRemove(pair.Key, out ignored)" in index


def test_data_desk_avoids_layout_feedback_and_identical_api_card_rebuild():
    data_desk = source("src/Bot/AssistWindow/Widget/Robot/CtlRobot.DataDesk.cs")
    assert "TimeSpan.FromMilliseconds(250)" in data_desk
    assert "Math.Abs(Height - anchorHeight) >= 1" in data_desk
    assert "Math.Abs(Left - targetLeft) >= 0.5" in data_desk
    assert "Math.Abs(Top - targetTop) >= 0.5" in data_desk
    assert "_lastApiStatsSignature" in data_desk
    assert "string.Equals(apiSignature, _lastApiStatsSignature, StringComparison.Ordinal)" in data_desk


def test_resource_samples_know_cache_cardinalities_without_user_content():
    monitor = source("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert '", timelineStates=" + ConversationContextStore.CachedTimelineCount' in monitor
    assert '", workingMemory=" + Bot.Knowledge.KnowledgeEngineV2Service.CachedWorkingMemoryCount' in monitor
    assert '", imageRecords=" + VisionImageCacheService.CachedRecordCount' in monitor
    images = source("src/Bot/ChromeNs/VisionImageCacheService.cs")
    assert "internal static int CachedRecordCount" in images
