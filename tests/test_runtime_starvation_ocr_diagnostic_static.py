from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_server_ocr_reuses_shop_control_plane_credentials():
    text = read("src/Bot/ChromeNs/LocalOcrService.cs")
    assert "ShopControlPlaneConnectionStore" in text
    assert "ResolveRuntimeByShopKey(shopKey)" in text
    assert "ResolveRuntimeBySellerNick" in text
    assert 'Source = "shop-control-plane"' in text
    assert "AiEndpointStore.GetEnabledEndpoints" in text  # compatibility fallback only
    assert "LocalOcrWorker.exe" not in text


def test_ocr_first_passes_seller_scope():
    text = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert "task.SellerNick," in text
    assert "task.ShopKey," in text
    assert "LocalOcrService.TryRecognizeAsync" in text


def test_runtime_reserves_threadpool_capacity_for_deadline_continuations():
    text = read("src/Bot/App.xaml.cs")
    assert "ThreadPool.GetMinThreads" in text
    assert "ThreadPool.SetMinThreads" in text
    assert "运行时线程池抗饥饿保护已启用" in text
    assert "Environment.ProcessorCount * 8" in text


def test_diagnostic_json_paths_do_not_use_first_last_brace_extraction():
    send = read("src/Bot/ChromeNs/SendFailureAnomalyService.cs")
    slow = read("src/Bot/ChromeNs/SlowResponseAnomalyService.cs")
    helper = read("src/Bot/ChromeNs/BuyerReplyOutputGuard.cs")
    assert "StructuredJsonObjectRecovery" in send
    assert "StructuredJsonObjectRecovery" in slow
    assert "ExtractBalancedObjects" in helper
    assert "inString" in helper and "escaped" in helper
    assert "LastIndexOf('}')" not in send
    assert "LastIndexOf('}')" not in slow


def test_handoff_docs_track_current_runtime_contract_instead_of_2026_08_pending_todos():
    incident = read("docs/QIANNIU_SEND_RELIABILITY_INCIDENTS.md")
    progress = read("docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md")
    handoff = read("docs/PROJECT_HANDOFF_CONTEXT.md")

    assert "bot-v1.1.1213" in incident
    assert "bot-v1.1.1139" in incident  # retained as historical incident evidence, not a pending TODO
    assert "BuyerActionAccepted" in progress
    assert "55 秒绝对期限覆盖" in progress
    assert "1.5 秒" in progress and "9 秒" in progress
    assert "当前没有开放 PR" in handoff
    assert "下一步应使用 `bot-v1.1.2481` 真实运行证据继续验收" in handoff
