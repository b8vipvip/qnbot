from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
GUARD = ROOT / "src" / "Bot" / "ChromeNs" / "WebSocketPageLifecycleGuard.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def source() -> str:
    return GUARD.read_text(encoding="utf-8-sig")


def test_inert_qianniu_pages_become_recoverable_standby_without_physical_close():
    text = source()
    assert 'InertPageGrace = TimeSpan.FromSeconds(20)' in text
    assert 'ReadBool(status, "duplicateRetire")' in text
    assert 'ReadBool(status, "hasImsdk")' in text
    assert 'ReadBool(status, "hasQN")' in text
    assert 'ReadBool(status, "hasVs")' in text
    assert 'ReadBool(status, "hasLoginID")' in text
    assert 'ScheduleStandbyObservation' in text
    assert 'physicalClose=false' in text
    assert '保持为轻量standby，不关闭通道' in text
    assert 'method = "retireDuplicate"' not in text
    assert 'current.Session.Close();' not in text


def test_business_capability_cancels_pending_inert_standby_observation():
    text = source()
    observe = text[text.index("private static void ObserveStatus"):text.index("private static void ScheduleStandbyObservation")]
    assert 'if (businessCapable)' in observe
    assert 'InertCandidates.TryRemove(sessionId, out ignored)' in observe
    assert 'StandbyLogged.TryRemove(sessionId, out standbyIgnored)' in observe
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
