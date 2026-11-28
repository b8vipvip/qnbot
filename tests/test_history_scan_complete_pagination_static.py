from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_pagination_has_no_fixed_page_cap_and_fails_closed_on_stalled_cursor():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")

    assert "private const int PageSize = 100;" in source
    assert "MaxHistoryPages" not in source
    assert "while (true)" in source
    assert "visitedCursors.Add(cursor)" in source
    assert "history_pagination_incomplete:server_reports_more_but_page_is_empty" in source
    assert "history_pagination_incomplete:cursor_stalled" in source
    assert "hasMoreKnown && hasMore == 0" in source


def test_history_pagination_understands_boolean_hasmore_and_empty_final_page():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")

    assert "token.Type == JTokenType.Boolean" in source
    assert "bool.TryParse(token.ToString(), out flag)" in source
    assert "if (messages.Count < 1)" in source
    assert "hasMoreKnown && hasMore > 0" in source


def test_contact_discovery_stops_on_stability_not_an_arbitrary_120_round_cap():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    method = source[
        source.index("private static async Task<List<string>> ReadVisibleMessageManagerContactsAsync"):
        source.index("private static void ClickPoint")
    ]

    assert "while (stableRounds < 6)" in method
    assert "round < 120" not in method
    assert "catch (OperationCanceledException) { throw; }" in method


def test_old_history_checkpoints_are_migrated_and_forced_to_rescan():
    source = read("src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs")
    decode = source[
        source.index("private static BuyerHistoryScanCheckpoint Decode"):
        source.index("internal static BuyerHistoryScanResumeInfo Latest")
    ]

    assert "private const int Schema = 4;" in source
    assert "var requiresFullRescan = value.SchemaVersion == 1 || value.SchemaVersion == 2;" in decode
    assert "value.Contacts = new List<string>();" in decode
    assert "value.Completed = new List<string>();" in decode
    assert 'value.State = "interrupted";' in decode
    assert "if (requiresFullRescan) Save(seller, value);" in decode

def test_legacy_checkpoint_full_rescan_resets_archived_message_counter():
    source = read("src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs")
    decode = source[
        source.index("private static BuyerHistoryScanCheckpoint Decode"):
        source.index("internal static BuyerHistoryScanResumeInfo Latest")
    ]

    assert "value.Contacts = new List<string>();" in decode
    assert "value.Completed = new List<string>();" in decode
    assert "value.ArchivedMessages = 0;" in decode
    assert decode.index("value.ArchivedMessages = 0;") < decode.index("Save(seller, value)")

def test_resume_keeps_old_pending_roster_and_fails_closed_on_malformed_history():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "if (options.ScanAll || contacts.Count <= 1)" in source
    assert "var names = checkpoint.Contacts.Where(IsPlausibleBuyerNick)" in source
    assert "successful.Remove(buyer)" in source
    assert "checkpoint.ArchivedMessages -= count" in source
    assert "history_pagination_incomplete:invalid_has_more" in source
    assert 'excluded == "history_missing_timestamp" || excluded == "history_seller_identity_mismatch"' in source

def test_history_learning_commits_each_contact_before_checkpoint_and_never_reuses_archive_only_completion():
    scan=read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    checkpoint=read("src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs")
    window=read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    method=scan.split("public async Task<ChatHistoryScanResult> ScanBuyerMessagesAsync",1)[1].split("public async Task<ChatHistoryScanResult> ScanAndImportAsync",1)[0]
    assert "if (options.LearnKnowledge)" in method
    assert method.index("analysis.SourceEvidenceByToken = sourceEvidence") < method.index("PromoteHistoryImport(seller, shopKey, analysis)") < method.index("checkpoint.Completed.Add(buyer)")
    assert "checkpoint.AnalyzedHumanPairs -= pairCount" in method
    assert "value.LearnKnowledge != options.LearnKnowledge" in checkpoint
    assert "value.LearnKnowledge = false" in checkpoint
    assert "buyers.Where(x => x.HasNativeMessageId)" in scan
    assert "sellers.Where(x => x.HasNativeMessageId)" in scan
    assert "options.LearnKnowledge = true" in window and "ScanBuyerMessagesAsync" in window

def test_historical_evidence_does_not_store_fallback_message_keys():
    scan=read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "NativeEvidenceKey = message.mcode" in scan
    assert "buyers.Where(x => x.HasNativeMessageId).Select(x => (x.NativeEvidenceKey" in scan
    assert "sellers.Where(x => x.HasNativeMessageId).Select(x => (x.NativeEvidenceKey" in scan
    assert "previousSellerMessageId = (turn.NativeEvidenceKey" in scan
