from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_generation_deadline_watchdog_uses_dedicated_background_thread():
    agent = text("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "AbsoluteGenerationAgeSeconds = BuyerStreamingReplyPipeline.TotalAiBudgetSeconds + 20" in agent
    assert "+ BuyerSessionAgent.AbsoluteGenerationAgeSeconds" in source
    assert "DeadlineWatchdogSleepMilliseconds = 250" in source
    assert "new Thread(GenerationDeadlineWatchdogLoop)" in source
    assert "IsBackground = true" in source
    assert 'Name = "QnBot.GenerationDeadlineWatchdog"' in source
    assert "Thread.Sleep(DeadlineWatchdogSleepMilliseconds)" in source


def test_generation_watchdog_tracks_actionable_lifetime_without_discovery_race():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "RegisterAcceptedGeneration(" in source
    assert "WatchedGenerations.AddOrUpdate" in source
    assert "Agent.TryGetGenerationState" in source
    assert "Agent.TryGetGenerationAcceptedAtUtc" in source
    assert "ConcurrentDictionary<string, WatchedGeneration> WatchedGenerations" in source
    assert "AcceptedAtUtc" in source
    assert "state == BuyerSessionAgentState.Generating" not in source
    assert "foreach (var pair in WatchedGenerations.ToArray())" in source
    assert "elapsed.TotalSeconds <= BuyerSessionAgent.AbsoluteGenerationAgeSeconds" in source
    assert '"absolute_generation_age_exceeded"' in source
    assert "Agent.Cancel(" in source
    assert "禁止迟到结果进入Ready/Sending" in source


def test_generation_watchdog_covers_all_non_terminal_states_and_drops_terminal_watches():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "Every generation is registered synchronously" in source
    assert "state == BuyerSessionAgentState.Completed" in source
    assert "state == BuyerSessionAgentState.Cancelled" in source
    assert "state == BuyerSessionAgentState.Failed" in source
    assert "WatchedGenerations.TryRemove" in source


def test_generation_watchdog_is_not_triggered_by_human_reply():
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "WatchSession(shopKey, seller, buyer);" in source
    assert "SellerHumanReply" in source
    assert "false, shopKey);" in source


def test_generation_watchdog_removes_only_exact_watch_snapshots_and_ids_survive_session_pruning():
    agent = text("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    source = text("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "private static long _generationSequence;" in agent
    assert "Interlocked.Increment(ref _generationSequence)" in agent
    assert "state.Generation = generation" in agent
    assert "private static bool RemoveExact<TValue>" in source
    assert "((ICollection<KeyValuePair<string, TValue>>)dictionary).Remove(snapshot)" in source
    assert "RemoveExact(WatchedGenerations, pair)" in source
    assert "RemoveExact(WatchedSessions, pair)" in source
    assert "return new WatchedGeneration" in source
    assert "return new WatchedSession" in source
    assert "WatchedGenerations.TryRemove(pair.Key" not in source
    assert "WatchedSessions.TryRemove(pair.Key" not in source
