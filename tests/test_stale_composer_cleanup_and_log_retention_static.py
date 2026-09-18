from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QNRPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"
APP_LIFE = ROOT / "src" / "Bot" / "StartUp" / "AppLife.cs"
LOG_WRITER = ROOT / "src" / "BotLib" / "LogWriter.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_stale_desktop_composer_is_cleared_only_when_bot_ownership_and_target_buyer_are_proven():
    text = _read(QNRPA)
    method = text.index("private async Task<bool> ClearStaleComposerBeforeNewDraftAsync")
    method_end = text.index("private async Task<bool> TrySetPlainTextByCdpAsync", method)
    block = text[method:method_end]
    target_read = block.index("var currentBuyer = await ReadCurrentBuyerNickAsync()")
    target_guard = block.index("if (!IsExpectedBuyer(buyer, currentBuyer))", target_read)
    ownership = block.index("if (!IsOwnedDraftForBuyer(buyer, observedText))", target_guard)
    clear_log = block.index("检测到同一买家的Bot历史残留草稿", ownership)
    mutation = block.index("RunUiMutationAsync", clear_log)
    exact_recheck = block.index("EditorMatchesExpectedText(latestText, ownedText)", mutation)
    ownership_recheck = block.index("IsOwnedDraftForBuyer(buyer, latestText)", exact_recheck)
    ctrl_a = block.index("PressCtrlA();", ownership_recheck)
    backspace = block.index("PressBackspace();", ctrl_a)
    second_target = block.index("var buyerAfterClear = await ReadCurrentBuyerNickAsync()", backspace)
    cdp_probe = block.index("残留草稿清理后确认", second_target)

    assert target_read < target_guard < ownership < clear_log < mutation < exact_recheck < ownership_recheck < ctrl_a < backspace < second_target < cdp_probe
    assert "输入框存在所有权无法证明的内容，已保留并阻止覆盖/追加发送" in block
    assert "RunUiActionAsync" not in block
    assert "RunUiMutationAsync" in block
    assert "UIA文本尚未刷新，交由CDP二次确认" in block
    assert "残留草稿清理后确认" in block


def test_exact_current_task_draft_is_adopted_and_side_effect_mutations_use_bounded_exclusive_lease():
    text = _read(QNRPA)
    method = text.index("private async Task<bool> ClearStaleComposerBeforeNewDraftAsync")
    method_end = text.index("private async Task<bool> TrySetPlainTextByCdpAsync", method)
    block = text[method:method_end]
    exact = block.index("EditorMatchesExpectedText(observedText, expected)")
    remember = block.index("RememberOwnedDraft(buyer, expected)", exact)
    ownership = block.index("IsOwnedDraftForBuyer(buyer, observedText)", remember)
    clear_log = block.index("检测到同一买家的Bot历史残留草稿", ownership)
    assert exact < remember < ownership < clear_log

    mutation = text.index("private async Task<bool> RunUiMutationAsync")
    mutation_end = text.index("private async Task<bool> HasExpectedDraftFastAsync", mutation)
    helper = text[mutation:mutation_end]
    assert "Task.WhenAny" in helper
    assert "Task.Delay(UiMutationTimeoutMs)" in helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in helper
    assert "原任务保持独占租约直到安全退出" in helper
    assert "Thread.Abort" not in helper
    assert "OwnedDraftRetention = TimeSpan.FromMinutes(30)" in text


def test_text_send_clears_stale_buffer_before_new_cdp_insert_and_image_path_does_too():
    text = _read(QNRPA)
    setter = text.index("private async Task<bool> TrySetPlainTextByCdpAsync")
    cleanup = text.index("ClearStaleComposerBeforeNewDraftAsync(buyer, text)", setter)
    recheck = text.index("新任务写入前清空确认", cleanup)
    insert = text.index("InsertText2Inputbox(buyer, text)", recheck)
    assert setter < cleanup < recheck < insert

    image_method = text.index("OpenAndSendImageAsync")
    image_cleanup = text.index("ClearStaleComposerBeforeNewDraftAsync(buyer, string.Empty)", image_method)
    set_image = text.index("SetImage(image)", image_cleanup)
    assert image_method < image_cleanup < set_image


def test_runtime_logs_live_in_persistent_user_data_and_use_exact_1mib_segments():
    app = _read(APP_LIFE)
    log = _read(LOG_WRITER)

    assert 'Path.Combine(PathEx.UserDataRoot, "logs")' in app
    assert 'Path.Combine(logDir, "运行日志.txt")' in app
    assert "RuntimeLogSegmentBytes = 1024 * 1024" in app

    assert "DefaultSegmentBytes = 1024 * 1024" in log
    assert "TimeSpan.FromHours(24)" in log
    assert "RotateCurrentFile()" in log
    assert "DeleteExpiredSegments" in log
    assert "File.Move(FileName, destination)" in log
    assert "ClearFileIfNeed" not in log


def test_rotation_happens_before_a_normal_entry_would_cross_segment_cap():
    log = _read(LOG_WRITER)
    size_guard = log.index("currentBytes + entryBytes > _limitFileSize")
    flush = log.index("WriteBatch(batch);", size_guard)
    rotate = log.index("RotateCurrentFile();", flush)
    add = log.index("batch.Add(part);", rotate)
    assert size_guard < flush < rotate < add
