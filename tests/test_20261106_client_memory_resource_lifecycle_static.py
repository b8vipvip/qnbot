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
    assert "if (!IsVisible) return;" in mirror
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
    # Live sessions retain their complete timeline; age-based cleanup is the retention boundary.
    assert "Do not silently truncate a live session to 100 turns." in timeline
    assert "state.Turns.RemoveRange" not in timeline


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
    assert '", buyerSortEntries=" + LatestBuyerSourceSort.Count' in monitor


def test_buyer_ordering_cache_has_a_ttl_and_hard_count_bound():
    monitor = source("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "MaxLatestBuyerSourceSortEntries = 10000;" in monitor
    assert "LatestBuyerSourceSortRetention = TimeSpan.FromHours(6);" in monitor
    assert "LatestBuyerSourceSortSweepInterval = TimeSpan.FromMinutes(5);" in monitor
    assert "SweepLatestBuyerSourceSortIfDue(now);" in monitor
    assert "LatestBuyerSourceSort.Count <= MaxLatestBuyerSourceSortEntries" in monitor
    assert "exactEntries.Remove(pair);" in monitor
    assert "buyerState.LastSeenUtc" in monitor


def test_vision_cache_releases_raw_messages_and_bounds_download_pressure():
    images = source("src/Bot/ChromeNs/VisionImageCacheService.cs")
    assert "MaxCacheRecords = 512;" in images
    assert "MaxConcurrentDownloads = 2;" in images
    assert "RecentMessageReferenceRetentionMinutes = 6;" in images
    assert "DownloadSlots.WaitAsync(timeout.Token)" in images
    assert "DownloadSlots.Release();" in images
    assert "Interlocked.Exchange(ref record.Message, null);" in images
    assert "TrimCompletedRecordsForAdmission" in images
    assert "EnsureCleanupTimer();" in images


def test_windows_memory_sampler_is_read_only_and_collects_machine_pressure():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "Get-Process -Name $processNames" in collector
    assert "Get-CimInstance Win32_OperatingSystem" in collector
    assert "WorkingSetMiB" in collector
    assert "PrivateMiB" in collector
    assert "CpuPercentOfMachine" in collector
    assert "SystemFreeMiB" in collector
    assert "Export-Csv" in collector
    assert "Get-Process -Name $processNames" in collector
    assert "TopProcessCount" in collector
    assert "SampleKind          = 'system'" in collector
    assert "Process             = '__SYSTEM__'" in collector
    assert "SystemUsedPercent" in collector
    assert "Stop-Process" not in collector
    assert "GC]::Collect" not in collector
    assert "Get-ChildItem" not in collector

def test_windows_memory_sampler_tracks_pseudonymized_runtime_shop_lifecycle_over_time():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "RuntimeSnapshotPath" in collector
    assert "runtime-snapshot.json" in collector
    assert "ProcessStartTimeUtcTicks" in collector
    assert "RuntimeSnapshotStatus" in collector
    assert "RuntimeProcessGenerationChanged" in collector
    assert "RuntimeOwnerChangesSincePreviousSample" in collector
    assert "RuntimeDuplicateSessions" in collector
    assert "RuntimeUnmappedConnectedSessions" in collector
    assert "RuntimeShopOwners" in collector
    assert "ShopRef" in collector
    assert "OwnerSessionRef" in collector
    assert "Get-Process -Id $runtimeProcessId" in collector
    assert "Get-Process -Id $runtimeProcessId" in collector
    assert "SellerNick" not in collector
    assert "shop.ShopKey" not in collector
    assert "CommandLine" not in collector
    assert "Invoke-WebRequest" not in collector

def test_windows_memory_sampler_compares_knowledge_v2_cloud_revision_and_fingerprint():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "KnowledgeV2SyncSnapshotPath" in collector
    assert "knowledge-v2-sync-snapshot.json" in collector
    assert "KnowledgeV2SyncSnapshotStatus" in collector
    assert "KnowledgeV2ConvergedShopCount" in collector
    assert "KnowledgeV2DivergentShopCount" in collector
    assert "KnowledgeV2LatestCloudRevision" in collector
    assert "KnowledgeV2ShopStates" in collector
    assert "LocalFingerprintPrefix" in collector
    assert "CloudFingerprintPrefix" in collector
    assert "process_mismatch" in collector

def test_windows_memory_sampler_includes_150_message_and_restart_checkpoint_metrics():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "KnowledgeV2LearningSnapshotPath" in collector
    assert "knowledge-v2-learning-snapshot.json" in collector
    assert "KnowledgeV2LearningSnapshotStatus" in collector
    assert "KnowledgeV2LatestHistoryMessagesInWindow" in collector
    assert "KnowledgeV2LatestCorpusRecords" in collector
    assert "KnowledgeV2LatestRecordsReviewed" in collector
    assert "KnowledgeV2LatestBatchCount" in collector
    assert "KnowledgeV2LatestBatchesCompleted" in collector
    assert "KnowledgeV2LatestCorpusComplete" in collector
    assert "KnowledgeV2LatestResumeCount" in collector
    assert "KnowledgeV2LatestRevisionStable" in collector
    assert "KnowledgeV2LatestSourceFingerprintPrefix" in collector
    assert "KnowledgeV2LatestTranscriptFingerprintPrefix" in collector
    assert "KnowledgeV2LearningReportStates" in collector


def test_windows_memory_sampler_tracks_a_b_a_only_with_fresh_active_focus_proof():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "ActiveShopRef" in collector
    assert "ActiveSessionRef" in collector
    assert "ActiveFocusProofFresh" in collector
    assert "RuntimeActiveShopRef" in collector
    assert "RuntimeActiveFocusProofFresh" in collector
    assert "RuntimeActiveShopChangedSincePreviousSample" in collector
    assert "RuntimeActiveShopTransitions" in collector
    assert "RuntimeActiveShopSequence" in collector
    assert "RuntimeActiveShopMappingValid" in collector
    assert "OwnerConnected -eq $true" in collector
    assert "OwnerInitialized -eq $true" in collector
    assert "OwnerClientInvalidated -ne $true" in collector
    assert "if ($runtimeActiveFocusProofFresh -eq $true -and $runtimeActiveShopMappingValid)" in collector
    assert "$activeShopSequence.Clear()" in collector
    assert "while ($activeShopSequence.Count -gt 32)" in collector


def test_auto_delivery_diagnostics_are_read_only_aggregate_and_privacy_minimized():
    script = source("scripts/Invoke-QnBotAutoDeliveryDiagnostics.ps1")
    assert "virtual-goods-auto-delivery-queue.json" in script
    assert "virtual-goods-auto-delivery-confirmation-ledger.json" in script
    assert "virtual-goods-auto-delivery-event-cursor.json" in script
    assert "order-event-state.json" in script
    assert "ConfirmationIntentCount" in script
    assert "ConfirmationUncertainCount" in script
    assert "UnresolvedCount" in script
    assert "EnabledShopCount" in script
    assert "EventCount" in script
    assert "REVIEW_REQUIRED" in script
    assert "NO_UNRESOLVED_CONFIRMATION_INTENTS" in script
    assert "RawShopKeysIncluded = $false" in script
    assert "SellerNicknamesIncluded = $false" in script
    assert "BuyerNamesIncluded = $false" in script
    assert "OrderIdsIncluded = $false" in script
    assert "EventPayloadsIncluded = $false" in script
    assert "ConfirmationKeysIncluded = $false" in script
    assert "ReadAllText" in script
    assert "WriteAllText" in script
    assert "Remove-Item" not in script

def test_sampler_process_rows_use_system_schema_and_per_shop_sync_freshness():
    collector = source("scripts/Collect-QnBotMemory.ps1")
    assert "$csvColumns = @($systemRow.PSObject.Properties.Name)" in collector
    assert "Select-Object -Property $csvColumns | Export-Csv" in collector
    assert "$syncShop.LastSuccessUtc" in collector
    assert "$successAge -ge -30 -and $successAge -le 180" in collector
    assert "[string]$shopConverged" in collector
