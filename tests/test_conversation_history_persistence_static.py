from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_is_persisted_in_bot_sqlite_with_bounded_retention():
    source = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "BotConversationHistoryEntity" in source
    assert "create table if not exists BotConversationHistoryEntity" in source
    assert "SaveRecordsInTransaction" in source
    assert "RetentionDays = 90" in source
    assert "MaxTotalRecords = 10000" in source
    assert "MaxRecordsPerConversation = 200" in source
    assert "DefaultLoadCount = 100" in source
    assert "UpdatedAtTicks < ?" in source
    assert "order by UpdatedAtTicks desc limit" in source


def test_complete_session_card_loader_is_unbounded_while_recent_ui_reads_remain_paged():
    source = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    complete = source[
        source.index("public static List<BotConversationHistoryEntity> LoadRangeComplete"):
        source.index("public static void FlushNow")
    ]

    assert "order by CreatedAtTicks asc" in complete
    assert "limit " not in complete.lower()
    assert "TrimConversationUnsafe" not in source
    assert "MaxTotalRecords = 10000" in source


def test_history_write_is_debounced_and_flushed_on_app_exit():
    source = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    assert "ConcurrentDictionary<string, BotConversationHistoryEntity> Pending" in source
    assert "Task.Delay(250)" in source
    assert "Application.Current.Exit += (s, e) => FlushNow();" in source
    assert "Pending[copy.EntityId] = copy;" in source


def test_conversation_card_tracks_state_and_can_restore_timestamps_and_status():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.History.cs")
    assert "BotConversationHistoryStore.QueueSave" in source
    assert "CreateFromHistory" in source
    assert "QuestionDetectedAtTicks" in source
    assert "AnswerReadyAtTicks" in source
    assert "CanResend = _canResend" in source
    assert 'statusText = "上次运行未完成"' in source
    assert "HistoryId" in source
    assert "HistorySortTicks" in source


def test_buyer_switch_restores_recent_history_without_counting_it_as_new_runtime_stats():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.History.cs")
    assert "LoadRecent(" in source
    assert "DefaultLoadCount" in source
    assert "CreateFromHistory(record)" in source
    assert "knownIds.Contains(record.EntityId)" in source
    assert "RefreshConversations();" in source
    assert "BotRuntimeStats.RecordDisplayedAnswer" not in source


def test_build_includes_history_store_and_partial_extensions():
    targets = read("src/Directory.Build.targets")
    assert "BotConversationHistoryStore.cs" in targets
    assert "CtlConversation.History.cs" in targets
    assert "CtlRobot.History.cs" in targets
