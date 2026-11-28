from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWLEDGE = (ROOT / "src/Bot/Knowledge/KnowledgeCenterWindow.cs").read_text(encoding="utf-8-sig")
SAFETY = (ROOT / "src/Bot/ChromeNs/BulkListManagementUiBridge.cs").read_text(encoding="utf-8-sig")


def test_manual_takeover_generates_compare_only_ai_and_history():
    assert "MessageProcessingTraceService.RecordLearningComparison(" in KNOWLEDGE
    assert "MyOpenAI.CallStructuredChat(" in KNOWLEDGE
    assert "should_learn" in KNOWLEDGE and "confidence < 0.95" in KNOWLEDGE
    assert "double.IsNaN(confidence) || double.IsInfinity(confidence)" in KNOWLEDGE
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in KNOWLEDGE
    assert "seller, question, manualAnswer, sourceMessageIds" in KNOWLEDGE


def test_ingress_watchdog_recovers_active_history_and_order_panel():
    assert 'RuntimeIngressReconciliationBridge' in KNOWLEDGE
    assert 'ReconcileActiveConversationIngressAsync' in KNOWLEDGE
    assert 'im.singlemsg.GetRemoteHisMsg' in KNOWLEDGE
    assert 'runtimePassiveIngressWatchdog' in KNOWLEDGE
    assert 'TryRecoverVisibleOrderPanelForBackgroundProbeAsync' in KNOWLEDGE


def test_conversation_map_watchdog_recovers_hidden_business_ingress_without_switching_buyer():
    assert 'ConversationMapIngressWatchdog' in SAFETY
    assert 'window._db && window._db.msgDataMap' in SAFETY
    assert 'im.singlemsg.GetNewMsg' in SAFETY
    assert 'im.singlemsg.GetRemoteHisMsg' in SAFETY
    assert 'DispatchInboundEvent' in SAFETY
    assert '"receiveNewMsg"' in SAFETY
    assert 'RemoteHistoryMinutes = 4' in SAFETY
    assert 'now.AddMinutes(-4)' in SAFETY
    assert 'application.openChat' not in SAFETY
    assert 'OpenChat(' not in SAFETY


def test_ingress_watchdog_repairs_only_provably_replaced_hooks():
    assert 'state.imsdk !== window.imsdk' in SAFETY
    assert 'state.qn !== window.QN' in SAFETY
    assert "eventSource.indexOf('onEventNotify wrapper failed') < 0" in SAFETY
    assert "invokeSource.indexOf('onInvokeNotify wrapper failed') < 0" in SAFETY
    assert 'window.__qnbotImsdkHookInstalled = false' in SAFETY
    assert 'window.__qnbotQnNotifyInstalled = false' in SAFETY


def test_log_noise_safety_override_preserves_real_faults():
    assert 'RuntimeLogNoiseSafetyOverride' in SAFETY
    assert '"SendForGetText"' not in SAFETY.split('private static string BuildPattern()', 1)[1]
    assert 'rendered.IndexOf("SendForGetText"' in SAFETY
    assert 'Regex.Escape("\\\"extra\\\":\\\"loop\\\"")' in SAFETY
    assert '设置界面已将“人工客服工作时间与下班回复”迁移' in SAFETY
    assert '设置界面已在构造阶段将“启用转人工规则”' in SAFETY
    assert '运行日志降噪安全边界已校正' not in SAFETY
