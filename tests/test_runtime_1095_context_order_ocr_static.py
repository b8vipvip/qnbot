from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_deictic_followup_uses_recent_buyer_context_without_global_long_delay():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 180" in source
    assert "买家当前消息是对上一条未解决问题的省略补充或催问" in source
    assert '"这个", "这款", "这种"' in source
    assert "semantic_continuation_superseded" in source
    assert "QuietDelayMilliseconds" in source
    assert "SemanticContinuationWindowSeconds * 1000" not in source


def test_generation_terminal_state_is_tracked_per_generation():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "TryGetGenerationState" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent
    assert "hasState && generationState == BuyerSessionAgentState.Generating" in coordinator

    dispatch = coordinator.split("private async Task DispatchOwnedAsync", 1)[1].split(
        "private void OnOwnedDispatchCompleted", 1
    )[0]
    finalizer = coordinator.split("private void FinalizeReplyOutcome", 1)[1].split(
        "private void CompleteMergedAwayGenerations", 1
    )[0]
    assert "FinalizeReplyOutcome(burst, lease);" in dispatch
    assert "TryGetGenerationState" in finalizer
    assert "GetSnapshot(burst.SellerNick, burst.BuyerNick)" not in dispatch
    assert "GetSnapshot(burst.SellerNick, burst.BuyerNick)" not in finalizer


def test_fixed_preset_refreshes_only_local_hub_snapshot_before_render():
    service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    hub = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "RefreshLocalSnapshotBeforeRenderAsync" in service
    assert "Task.Delay(120)" in service
    assert "OrderEventHub.RefreshFromCanonical(plan.Snapshot)" in service
    helper = service.split("private static async Task RefreshLocalSnapshotBeforeRenderAsync", 1)[1].split("public static void Complete", 1)[0]
    assert "CallReplyApiAsync" not in helper
    assert "HttpClient" not in helper
    assert "GetRemote" not in helper
    assert "public static OrderSnapshot RefreshFromCanonical" in hub
    assert "Merge(existing.Snapshot, snapshot)" in hub


def test_formal_release_uses_server_ocr_and_has_no_local_worker_dependency():
    client = read("src/Bot/ChromeNs/LocalOcrService.cs")
    runtime = read("services/api-control-plane/runtime_ocr.py")
    workflow = read(".github/workflows/windows-build.yml")

    assert '"/api/runtime/v1/ocr"' in client
    assert 'new AuthenticationHeaderValue("Bearer", endpoint.ApiKey)' in client
    assert '"X-Image-Sha256"' in client
    assert "LocalOcrWorker.exe" not in client
    assert '@app.post("/api/runtime/v1/ocr")' in runtime
    assert "Depends(require_client)" in runtime
    assert "RapidOCR" in runtime
    assert "LocalOcrWorker.exe" not in workflow
    assert "PP-OCRv6_det_small.onnx" not in workflow
    assert "ppocrv6_small_dict.txt" not in workflow