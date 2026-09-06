from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT/'src/Bot/Knowledge/KnowledgeAiService.cs').read_text(encoding='utf-8')
UI = (ROOT/'src/Bot/Knowledge/KnowledgeImportControl.cs').read_text(encoding='utf-8')
OPENAI = (ROOT/'src/Bot/ChromeNs/MyOpenAI.cs').read_text(encoding='utf-8')
OPTS = (ROOT/'src/Bot/Options/CtlRobotOptions.xaml.cs').read_text(encoding='utf-8')
WF = (ROOT/'.github/workflows/windows-build.yml').read_text(encoding='utf-8')

def test_smart_import_timeout_config_defaults_and_ui():
    assert 'SmartImportTimeoutSeconds' in OPTS
    assert 'GetSmartImportTimeoutSeconds' in OPTS
    assert 'SaveSmartImportTimeoutSeconds' in OPTS
    assert 'ClampTimeout' in SERVICE and 'Math.Max(120, Math.Min(1800' in SERVICE
    assert 'AI分析超时：' in UI and ' 秒' in UI

def test_structured_chat_has_request_level_timeout_and_token():
    assert 'CallStructuredChat(messages, maxTokens, temperature, 0, CancellationToken.None)' in OPENAI
    assert 'CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)' in OPENAI
    assert 'deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout))' in OPENAI
    assert 'HttpCompletionOption.ResponseHeadersRead' in OPENAI
    assert 'ReadResponseBodyWithCancellationAsync' in OPENAI
    assert 'http.Timeout = Timeout.InfiniteTimeSpan' in OPENAI

def test_batching_rules_are_encoded():
    assert 'TargetMinChars = 3000' in SERVICE
    assert 'TargetMaxChars = 5000' in SERVICE
    assert 'MaxBatchChars = 6000' in SERVICE
    assert 'Regex.Split(text.Replace("\\r\\n", "\\n"), "\\n{2,}")' in SERVICE
    assert 'SplitLongParagraph' in SERVICE

def test_incremental_idempotent_save_and_hashes():
    assert 'SaveDeduped(parsed.Items)' in SERVICE
    assert 'ContentHash' in SERVICE
    assert 'SHA256.Create' in SERVICE
    assert 'BotFeatureStore.SaveKnowledgeBase(existing)' in SERVICE

def test_error_classification_and_cancellation_sources():
    for term in ['UserCancel', 'Timeout', 'WindowClosed', 'ReplacedByNewTask']:
        assert term in SERVICE and term in UI
    assert '智能导入超时' in SERVICE
    assert 'JSON解析失败' in SERVICE and 'JSON解析失败' in UI
    assert 'HttpRequestException' in UI

def test_json_fence_and_truncated_json_handling():
    assert 'StripFence' in SERVICE
    assert 'StartsWith("```")' in SERVICE
    assert '可能是响应被截断' in SERVICE

def test_progress_fields():
    for term in ['正在分析第 {0}/{1} 批', '当前批次字符数', '已导入', '已跳过重复', '当前耗时', '当前接口']:
        assert term in SERVICE

def test_windows_release_artifact_excludes_params_db():
    assert 'runs-on: windows-2022' in WF
    assert '/p:Configuration=Release' in WF and '/p:Platform=x64' in WF
    assert "Where-Object { $_.Name -ne 'params.db' }" in WF
    assert r'package\Bin\Bot.exe' in WF and r'package\data\README-PARAMS.txt' in WF