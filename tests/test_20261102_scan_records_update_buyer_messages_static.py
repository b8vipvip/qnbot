"""Scan records '更新消息' must import all saved evidence idempotently, not change KB rules."""
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def read(p):
    return (ROOT/p).read_text(encoding="utf-8-sig")
ui=read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
merge=read("src/Bot/Knowledge/BuyerHistoryScanMessageMergeService.cs")
archive=read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
targets=read("src/Directory.Build.targets")

def test_button_is_in_scan_records_dialog_and_refreshes_after_merge():
    section=ui.split("private void ShowScanRecords()",1)[1].split("internal static string Prompt(",1)[0]
    assert 'Button("更新消息", 100)' in section
    assert "buttons.Children.Add(updateMessages);" in section
    assert 'BuyerHistoryScanMessageMergeService.MergeAll(_seller)' in section
    assert "RefreshView();" in section
    assert "result.NewlyArchived" in section
    assert "result.AlreadyArchived" in section
    assert "result.DuplicateInScans" in section
    assert "updateMessages.IsEnabled = false;" in section
    assert "updateMessages.IsEnabled = true;" in section

def test_merge_scans_entire_shop_directory_including_partials_and_saved_zip():
    assert "BuyerHistoryScanMessageMergeService.cs" in targets
    assert '"buyer-history-scans"' in merge
    assert "Directory.GetDirectories(root)" in merge
    assert ".Take(100)" not in merge
    assert "manifest.RunId != runId" in merge
    assert "File.ReadLines(sourcePath, Encoding.UTF8)" in merge
    assert "Path.Combine(directory, runId + \".zip\")" in merge
    assert "zip.GetEntry(DataFile)" in merge
    assert "No buyer" not in merge
    assert "manifest.PackagePath" not in merge
    assert "ScansFailed++" in merge

def test_cross_scan_dedup_does_not_use_run_specific_event_hash():
    assert "BuyerMessageArchiveService.Redact(text)" in merge
    assert "time.Ticks.ToString(CultureInfo.InvariantCulture)" in merge
    assert "SHA256.Create()" in merge
    assert 'record["utc_or_local_time"]' in merge
    assert 'record["text"]' in merge
    assert "record[\"event_hash\"]" not in merge
    assert "record[\"contact\"]" not in merge
    assert "unique.ContainsKey(fingerprint)" in merge
    assert "result.DuplicateInScans++" in merge
    assert "DateTime.MinValue" in merge

def test_database_is_locked_and_compares_existing_fingerprints_before_insert():
    block=archive.split("internal static void ImportScannedMessages(",1)[1].split(
        "internal static List<BuyerQuestionRow> Read(",1)[0]
    assert "lock (state.Sync)" in block
    assert "ReadRecords<BuyerQuestionRow>" in block
    assert "BuyerHistoryScanMessageMergeService.Fingerprint(" in block
    assert "byEvent.TryGetValue(eventKey, out variants)" in block
    assert "result.AlreadyArchived++" in block
    assert "state.Db.SaveOneRecord(unpaired);" in block
    assert "state.Db.SaveOneRecord(record);" in block
    assert "variants.Add(record);" in block
    assert "result.NewlyArchived++" in block
    assert 'Hash("scan-import:" + fingerprint)' in block
    assert "KnowledgeEngineV2Repository" not in block
    assert "AI" not in block
