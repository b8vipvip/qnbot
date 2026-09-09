from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
GUARD = ROOT / "src" / "Bot" / "ChromeNs" / "WebSocketPageLifecycleGuard.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def source() -> str:
    return GUARD.read_text(encoding="utf-8-sig")


def test_inert_qianniu_pages_retire_only_with_explicit_protocol_and_no_business_surface():
    text = source()
    assert 'InertPageGrace = TimeSpan.FromSeconds(20)' in text
    assert 'ReadBool(status, "duplicateRetire")' in text
    assert 'ReadBool(status, "hasImsdk")' in text
    assert 'ReadBool(status, "hasQN")' in text
    assert 'ReadBool(status, "hasVs")' in text
    assert 'ReadBool(status, "hasLoginID")' in text
    assert 'reason = "no_business_surface"' in text
    assert 'current.Session.Close();' in text
    assert '回收无业务能力千牛WebSocket页面通道' in text


def test_business_capability_cancels_pending_inert_page_retirement():
    text = source()
    observe = text[text.index("private static void ObserveStatus"):text.index("private static void ScheduleRetirement")]
    assert 'if (!retireCapable || businessCapable)' in observe
    assert 'InertCandidates.TryRemove(sessionId, out ignored)' in observe
    assert 'InertCandidates.GetOrAdd' in observe


def test_duplicate_receive_new_msg_requests_existing_history_authority_not_second_reply_pipeline():
    text = source()
    assert 'string.Equals(e.Type, "receiveNewMsg"' in text
    assert 'IsAuthoritativeSellerSession(seller, sessionId)' in text
    assert 'RequestImmediateHistoryProbe(seller, sessionId)' in text
    assert 'ProbeActiveConversationForMissedInboundAsync()' in text
    assert 'ProcessIncomingMessageAsync(' not in text
    assert 'ImmediateProbeDebounce = TimeSpan.FromMilliseconds(900)' in text


def test_guard_is_compiled_for_normal_and_wpf_temp_projects():
    props = PROPS.read_text(encoding="utf-8-sig")
    assert 'ChromeNs\\WebSocketPageLifecycleGuard.cs' in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\WebSocketPageLifecycleGuard.cs" />' in props
