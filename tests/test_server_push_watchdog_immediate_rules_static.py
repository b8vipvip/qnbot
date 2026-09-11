from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_fixed_rules_run_before_any_burst_quiet_delay_or_context_merge():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    enqueue = coordinator.index("public void Enqueue(BuyerMessageBurstItem item)")
    queued = coordinator.index("state.PendingRules.Enqueue(item)", enqueue)
    worker = coordinator.index("private async Task RunAsync", queued)
    before_merge = coordinator.index("CanonicalPreMergeDecisionService.HandleAsync(", worker)
    enqueue_merge = coordinator.index("EnqueueForMerge(item)", before_merge)
    quiet_delay = coordinator.index("QuietDelayMilliseconds", enqueue_merge)
    assert enqueue < queued < worker < before_merge < enqueue_merge < quiet_delay

    canonical_start = coordinator.index("internal static class CanonicalPreMergeDecisionService")
    canonical_end = coordinator.index("internal sealed class BuyerMessageBurstCoordinator", canonical_start)
    canonical = coordinator[canonical_start:canonical_end]
    off_hours = canonical.index("TryResolveOffHours(out offHoursReply)")
    first = canonical.index("FirstInquiryFixedReplyService.TryResolve(")
    local_short = canonical.index("LocalShortReplyService.TryResolve(")
    assert off_hours < first < local_short
    assert "OffHoursRepeatMinutes = 2" in canonical
    assert "SendTextWithRetryAsync(" in canonical
    assert "SemaphoreSlim" not in canonical
    assert "BuyerSessionAgent" not in canonical

    first_block = canonical[first:local_short]
    release = first_block.index("FirstInquiryFixedReplyService.ReleaseReservation(")
    assert "CanonicalPreMergeOutcome.Failed" in first_block[release:]


def test_order_auto_reply_still_precedes_burst_merge_path():
    qn = read("src/Bot/ChromeNs/QN.cs")
    order = qn.index("OrderPlacedAutoReplyService.TryCreatePlan(")
    merge = qn.index("_buyerMessageBurstCoordinator.Enqueue(", order)
    assert order < merge


def test_server_push_replaces_client_periodic_version_polling():
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")
    client = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
    server = read("services/api-control-plane/bot_update_push.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")

    assert "RestartServerPushListener();" in core
    assert "clientAutoCheck=False" in core
    assert "CheckNowAsync(false)" not in state
    assert "new Timer(" not in state
    assert "text/event-stream" in client
    assert "ResponseHeadersRead" in client
    assert "StreamingResponse" in server
    assert "/api/public/v1/bot-update/events" in server
    assert "bot_update_push.router" in bootstrap


def test_download_progress_identifies_server_only_channel_and_prepare_state():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    auto_window = read("src/Bot/Update/BotUpdateAutoProgressWindow.Fast.cs")
    prompt = read("src/Bot/Update/BotUpdatePromptWindow.Fast.cs")

    assert "EnsureServerPackageReadyAsync" in download
    assert 'CurrentDownloadChannel = "服务器"' in download
    assert 'CurrentDownloadChannel = "服务器准备中"' in download
    assert "release.PackageUrl" not in download
    assert 'AddDownloadSource(sources, "GitHub", release.PackageUrl)' not in download
    assert "CurrentDownloadPercent" in download
    assert "DownloadedBytes" in download
    assert "正在下载更新｜通道：" in download
    assert "下载通道：" in auto_window
    assert "下载通道：" in prompt
    assert "客户端不会直连 GitHub 下载安装包" in prompt


def test_external_watchdog_restarts_unexpected_process_exit_only():
    watchdog = read("src/Bot/Update/BotUpdateProcessWatchdog.Fast.cs")

    assert "Wait-Process -Id $CurrentPid" in watchdog
    assert "ExpectedExitMarker" in watchdog
    assert "MarkExpectedExit(\"normal-app-exit\")" in watchdog
    assert "Start-Process -FilePath $ExePath" in watchdog
    assert "5 unexpected exits in 10 minutes" in watchdog
    assert "Bot外部进程守护已启动" in watchdog


def test_handoff_strategy_is_built_directly_not_only_by_loaded_runtime_patch():
    wnd = read("src/Bot/Options/WndOption.xaml.cs")
    feature = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    legacy_bridge = read("src/Bot/Update/BotUpdateHandoffSettingsUi.Fast.cs")

    assert 'AddFeaturePage("回复与通知", "转人工策略"' in wnd
    assert 'OptionEnum.Notifications, "转人工策略")' in wnd
    assert 'AddFeaturePage("回复与通知", "消息通知"' not in wnd

    constructor = feature.index("public FeatureSettingsOptionsControl(string seller)")
    direct_migration = feature.index("OrganizeHandoffStrategyPage();", constructor)
    hide_tabs = feature.index("HideLegacyTabHeaders();", direct_migration)
    assert constructor < direct_migration < hide_tabs
    assert 'notificationTab.Header = "转人工策略"' in feature
    assert 'pageTitle = "转人工策略"' in feature
    assert '"_rulesEnabled"' in feature
    assert '"_manualKeywords"' in feature
    assert '"_noAutoKeywords"' in feature
    assert '"_handoffText"' in feature
    assert "在构造阶段将“启用转人工规则”及关键词/话术移动到“转人工策略”" in feature

    assert "HandoffSettingsUiBridge" in legacy_bridge