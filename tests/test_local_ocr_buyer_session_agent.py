from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_ocr_client_is_compiled_cached_authenticated_and_used_by_vision():
    props = read("src/Directory.Build.props")
    service = read("src/Bot/ChromeNs/LocalOcrService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")

    assert "LocalOcrService.cs" in props
    assert "ComputeSha256" in service
    assert "ocr-cache" in service
    assert "ResolveControlPlaneEndpoint" in service
    assert 'new AuthenticationHeaderValue("Bearer", endpoint.ApiKey)' in service
    assert '"X-Image-Sha256"' in service
    assert "CancellationTokenSource.CreateLinkedTokenSource" in service
    assert "timeout.CancelAfter(safeTimeout)" in service
    assert '"/api/runtime/v1/ocr"' in service
    assert "LocalOcrWorker.exe" not in service
    assert "LocalOcrService.TryRecognizeAsync(image.LocalCachePath" in vision
    assert "LocalOcrService.BuildPromptEvidence(localOcr)" in vision
    assert "服务端OCR预识别，仅作辅助证据" in service


def test_server_ocr_runtime_is_authenticated_bounded_and_windows_release_excludes_worker():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    requirements = read("services/api-control-plane/requirements.txt")
    workflow = read(".github/workflows/windows-build.yml")

    assert '@app.post("/api/runtime/v1/ocr")' in runtime
    assert "Depends(require_client)" in runtime
    assert "OCR_MAX_IMAGE_BYTES" in runtime
    assert 'request.headers.get("content-length"' in runtime
    assert 'request.headers.get("x-image-sha256"' in runtime
    assert "hashlib.sha256(raw).hexdigest()" in runtime
    assert "asyncio.Semaphore" in runtime
    assert "asyncio.wait_for(asyncio.shield(task)" in runtime
    assert "run_in_threadpool(_run_ocr, raw)" in runtime
    assert "RapidOCR" in runtime
    assert "runtime_ocr.install(control_plane)" in bootstrap
    assert "rapidocr==" in requirements
    assert "onnxruntime==" in requirements

    # Formal Windows releases must neither build nor copy local OCR inference assets.
    assert "Build multilingual local OCR worker" not in workflow
    assert "dotnet publish tools/LocalOcrWorker" not in workflow
    assert "PP-OCRv6_det_small.onnx" not in workflow
    assert "PP-OCRv6_rec_small.onnx" not in workflow
    # Keep a release-time tripwire that rejects accidental reintroduction of local OCR assets.
    assert "Local OCR runtime must not be bundled after server OCR migration." in workflow
    assert "Test-Path -LiteralPath 'package\\Bin\\local-ocr'" in workflow


def test_buyer_session_agent_keeps_independent_generations_and_retires_merged_ones():
    props = read("src/Directory.Build.props")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    assert "BuyerSessionAgent.cs" in props
    assert "state.Generation++" in agent
    assert "previous.Cancel()" not in agent
    assert "ActiveGenerations" in agent
    assert "independentGeneration=True" in agent
    assert "ReusedCoalescingGeneration = false" in agent
    assert "Duplicate = true" in agent
    assert "public void CancelAll" in agent
    assert "Sending = 6" in agent
    assert "Waiting = 7" in agent
    assert "Completed = 8" in agent
    assert "_sessionAgent.ObserveBuyerMessage" in burst
    assert "if (observation.Duplicate)" in burst
    assert "SessionGeneration" in burst
    assert "CompleteMergedAwayGenerations" in burst
    assert "coalesced_into_generation_" in burst
    assert "_sessionAgent.CancelAll(seller, buyer, reason)" in burst
    assert "coalescing_buffer_trimmed" in burst
    assert "_sessionAgent.IsCurrent" in burst

    stable = burst[burst.index("public async Task<bool> ConfirmStableAsync"):burst.index("public bool MarkProcessing")]
    assert 'MarkReady("send_barrier_stable")' not in stable
    assert "return IsCurrent && !CancellationToken.IsCancellationRequested;" in stable

    # Completion is conditional: timeout/error must become Failed instead of being
    # overwritten as Completed when a replyable pipeline returns without a ready answer.
    assert "returnedWithoutReady && burst.HasReplyableItem" in burst
    assert "MarkFailed(\"reply_pipeline_returned_without_ready\")" in burst
    assert '"reply_pipeline_completed"' in burst
