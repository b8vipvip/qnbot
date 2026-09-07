from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_shop_ai_falls_back_to_control_plane_without_persisting_token():
    text = read("src/Bot/ShopScope/ShopScopedParamBridge.cs")
    assert 'AiEndpointListJson' in text
    assert '/api/runtime/v1/ai-proxy/' in text
    assert 'TryGetToken' in text
    assert 'text-default' in text
    assert 'JsonConvert.SerializeObject' in text
    assert 'SetString(AiEndpointListKey' not in text
    assert 'HasUsableExplicitAiEndpoint(value)' in text
    assert 'apiKey.Length > 0 && model.Length > 0' in text


def test_shop_ai_proxy_enforces_shop_binding_and_reuses_control_plane_dispatch():
    text = read("services/api-control-plane/runtime_shop_ai_proxy.py")
    assert '/api/runtime/v1/ai-proxy/{shop_key}/chat/completions' in text
    assert 'core._runtime_client(request)' in text
    assert 'bot_client_shop_binding.ensure_binding' in text
    assert '_cp.dispatch_chat' in text
    assert 'qianniu_routing' in text
    assert 'shop_key' in text


def test_message_processing_trace_is_shop_scoped_and_async():
    text = read("src/Bot/ChromeNs/MessageProcessingTraceService.cs")
    assert 'ConcurrentQueue<TraceItem>' in text
    assert 'Task.Run' in text
    assert '/api/runtime/v1/message-processing-traces/batch' in text
    assert 'AuthenticationHeaderValue("Bearer", token)' in text
    assert '"X-Shop-Key", state.Shop.ShopKey' in text
    assert 'ApiKey' not in text


def test_response_progress_records_key_chain_stages():
    text = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    for marker in (
        'MessageProcessingTraceService.RecordQuestion',
        'MessageProcessingTraceService.RecordGenerationStarted',
        'MessageProcessingTraceService.RecordAnswerReady',
        'MessageProcessingTraceService.RecordDelivery',
        'MessageProcessingTraceService.RecordManualObservation',
        'MessageProcessingTraceService.RecordCancelled',
        'MessageProcessingTraceService.RecordFailure',
    ):
        assert marker in text


def test_trace_service_exposes_knowledge_fallback_and_learning_comparison_stages():
    text = read("src/Bot/ChromeNs/MessageProcessingTraceService.cs")
    for marker in (
        'knowledge_decision',
        'ai_fallback_started',
        'manual_reply_observed',
        'knowledge_learning_compare',
        'processing_cancelled',
    ):
        assert marker in text


def test_server_trace_storage_and_admin_query_are_shop_aware():
    text = read("services/api-control-plane/message_processing_traces.py")
    assert 'CREATE TABLE IF NOT EXISTS bot_message_processing_traces' in text
    assert 'shop_key TEXT NOT NULL' in text
    assert '/api/runtime/v1/message-processing-traces/batch' in text
    assert '/api/admin/message-processing-traces' in text
    assert '/api/admin/message-processing-conversations' in text
    assert '/api/admin/message-processing-conversations/detail' in text
    assert '/api/admin/message-processing-conversations/export' in text
    assert 'bot_client_shop_binding.ensure_binding' in text
    assert '20_000' in text


def test_trace_console_is_visible_and_beijing_time_aware():
    html = read("services/api-control-plane/static/index.html")
    js = read("services/api-control-plane/static/message-traces.js")
    assert 'data-page="message-traces"' in html
    assert 'id="page-message-traces"' in html
    assert 'id="messageTraceTable"' in html
    assert '/static/message-traces.js?v=2' in html
    assert '/api/admin/message-processing-conversations?' in js
    assert '/api/admin/message-processing-conversations/detail?' in js
    assert '/api/admin/message-processing-conversations/export?' in js
    assert 'viewMessageConversation' in js
    assert '导出所有买家' in js
    assert '查询消息处理日志失败' in js
    assert 'cnTime(' in js
    assert 'traceShopKey' in js
    assert 'traceSeller' in js
    assert 'traceBuyer' in js
    assert 'traceId' in js


def test_trace_admin_dependency_receives_typed_fastapi_request():
    source = read("services/api-control-plane/message_processing_traces.py")
    assert "def _require_admin(request: Request)" in source
    assert "Depends(_require_admin)" in source
    assert "Depends(lambda request:" not in source


def test_new_runtime_modules_are_bootstrapped_and_packaged():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    props = read("src/Bot/Directory.Build.props")
    for module in ('runtime_shop_ai_proxy', 'message_processing_traces'):
        assert f'import {module}' in bootstrap
        assert f'{module}.install(control_plane)' in bootstrap
        assert f'{module}.py' in dockerfile
    assert 'message_processing_traces.init_db()' in bootstrap
    assert 'MessageProcessingTraceService.cs' in props


def test_force_rebind_clears_old_shop_traces():
    text = read("services/api-control-plane/bot_client_shop_binding.py")
    assert '"bot_message_processing_traces"' in text
