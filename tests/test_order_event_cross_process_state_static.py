from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_event_state_uses_path_scoped_cross_process_mutex():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    hub = code.split("internal static class OrderEventHub", 1)[1]
    assert "new Mutex(false, BuildStateMutexName(path))" in hub
    assert "mutex.WaitOne(StateMutexWaitMilliseconds)" in hub
    assert "catch (AbandonedMutexException)" in hub
    assert 'return @"Local\\QianniuAiBot.OrderEventState."' in hub
    publish = hub.split("public static OrderEventPublishResult Publish", 1)[1]
    publish = publish.split("private static StateMutexLease AcquireStateMutex", 1)[0]
    assert "using (var lease = AcquireStateMutex(path))" in publish
    assert "ReloadAndMergeFromDisk(path)" in publish


def test_state_reload_merges_other_process_and_unsaved_local_events_before_write():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    reload_region = code.split("private static void ReloadAndMergeFromDisk", 1)[1]
    reload_region = reload_region.split("private static StoredState TryReadState", 1)[0]
    assert "var disk = TryReadState(path);" in reload_region
    assert "foreach (var local in _state.Events" in reload_region
    assert "merged.Events.FirstOrDefault" in reload_region
    assert "Merge(existing.Snapshot, local.Snapshot)" in reload_region
    assert "local.SeenAt > existing.SeenAt" in reload_region


def test_order_state_write_is_flush_to_disk_and_atomic_without_delete_window():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    save = code.split("private static bool SaveInternal", 1)[1]
    save = save.split("private static string GetPath", 1)[0]
    assert 'Guid.NewGuid().ToString("N")' in save
    assert "Process.GetCurrentProcess().Id" in save
    assert "FileMode.CreateNew" in save
    assert "FileOptions.WriteThrough" in save
    assert "stream.Flush(true)" in save
    assert "File.Replace(temp, path, null, true)" in save
    assert "StateIoRetryCount" in save
    assert "catch (IOException ex)" in save
    assert "File.Delete(path)" not in save
    assert "旧状态文件已保留" in save

def test_first_accept_timestamp_is_immutable_across_duplicate_observations_and_process_merge():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "public DateTime AcceptedAt { get; set; }" in code
    assert "var previousSeenAt = existing.SeenAt;" in code
    assert "existing.AcceptedAt == DateTime.MinValue" in code
    assert "existing.AcceptedAt = previousSeenAt == DateTime.MinValue ? now : previousSeenAt;" in code
    assert "AcceptedAt = now" in code
    assert "var localAcceptedAt = local.AcceptedAt == DateTime.MinValue ? local.SeenAt : local.AcceptedAt;" in code
    assert "localAcceptedAt < existing.AcceptedAt" in code
    publish = code.split("public static OrderEventPublishResult Publish", 1)[1]
    publish = publish.split("private static StateMutexLease AcquireStateMutex", 1)[0]
    duplicate = publish.split("if (existing != null)", 1)[1].split("_state.Events.Add", 1)[0]
    assert duplicate.index("existing.AcceptedAt") < duplicate.index("existing.SeenAt = now")
