from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_generation_acceptance_time_is_independent_from_source_message_time():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    observation = agent[agent.index("public BuyerSessionAgentObservation ObserveBuyerMessage"):agent.index("public BuyerSessionEventResult RecordEvent")]
    assert "state.LastObservedAt = observedAt" in observation
    assert "acceptedAtUtc = DateTime.UtcNow" in observation
    assert "state.GenerationAcceptedAtUtc[generation] = acceptedAtUtc" in observation
    assert "RegisterAcceptedGeneration(" in observation


def test_generation_age_is_rechecked_synchronously_before_state_progression():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    current_gate = agent[agent.index("public bool IsCurrent"):agent.index("public CancellationToken GetCancellationToken")]
    transition_gate = agent[agent.index("public bool TryTransition"):agent.index("public void Cancel(")]
    assert "IsGenerationExpiredLocked" in current_gate
    assert "absolute_generation_age_current_gate" in current_gate
    assert "IsGenerationExpiredLocked" in transition_gate
    assert "absolute_generation_age_transition_gate" in transition_gate
    assert "state.ActiveGenerations.Remove(generation)" in transition_gate


def test_duplicate_cdp_recovery_queue_is_bounded_without_disabling_compensation():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    assert "MaxPendingInboundEvents = 512" in bridge
    assert "while (Pending.Count >= MaxPendingInboundEvents" in bridge
    assert "TryDeliverLive(item)" in bridge
    assert "target.DispatchInboundEvent(item.Type, item.Response)" in bridge
    assert "BeginForwardedInbound(item.SourceSession)" in bridge


def test_ordinary_fixed_rule_gate_never_fail_opens_into_ai_after_timeout():
    source = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = source[source.index("public static async Task<bool> HandleBeforeMergeAsync("):source.index("private static async Task<bool> HandleOffHoursExclusiveAsync")]
    assert "WaitAsync(1800)" not in method
    assert "已放行消息合并/AI链路" not in method
    assert "GetCancellationToken(" in method
    assert "await gate.WaitAsync(generationToken)" in method
    assert "固定规则串行等待期间generation已失效" in method


def test_fixed_send_checks_generation_age_without_advancing_pre_merge_state():
    source = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = source[source.index("private static async Task<bool> SendFixedAsync"):source.index("public static async Task<bool> TryHandleAsync")]
    assert method.count("sessionAgent.IsCurrent(item.SellerNick, item.BuyerNick, item.SessionGeneration, item.ShopKey)") >= 2
    assert "fixed_reply_ready" not in method
    assert "fixed_reply_sending" not in method
    assert "SendTextWithRetryAsync" in method
    # A successful first-inquiry greeting is allowed to continue into the ordinary merge/AI path,
    # so the pre-merge sender must not leave the shared generation stuck in Sending.
    assert "BuyerSessionAgentState.Sending" not in method


def test_repository_does_not_reintroduce_retired_hyphenated_project_name():
    retired_name = "qianniu" + "-ai-bot"
    excluded_dirs = {".git", "bin", "obj", ".vs", "packages"}
    offenders = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(part in excluded_dirs for part in path.parts):
            continue
        if path.suffix.lower() in {".png", ".jpg", ".jpeg", ".gif", ".ico", ".dll", ".exe", ".pdb", ".zip", ".7z"}:
            continue
        try:
            text = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        if retired_name.lower() in text.lower():
            offenders.append(str(path.relative_to(ROOT)))
    assert not offenders, "retired project name found in: " + ", ".join(offenders)


def test_legacy_encrypted_store_entropy_is_preserved_without_retired_plaintext_slug():
    settings = read("src/Bot/ShopScope/ShopScopedSettingsStore.cs")
    token = read("src/Bot/ShopScope/ShopTokenStore.cs")
    assert '"qianniu" + "-ai-bot|shop-settings|"' in settings
    assert '"qianniu" + "-ai-bot|control-plane-token|"' in token
    assert "LegacySchema" in settings
    assert "LegacySchema" in token
    assert 'private const string Schema = "qnbot.shop-settings"' in settings
    assert 'private const string Schema = "qnbot.shop-token"' in token


def test_legacy_schema_inputs_remain_readable_while_new_outputs_use_qnbot():
    profile = read("src/Bot/ShopScope/ShopProfileStore.cs")
    business = read("src/Bot/ChromeNs/BusinessPolicyProfileService.cs")
    handoff = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    backup = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    import_export = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    assert "LegacyRegistrySchema" in profile and "registry.Schema = RegistrySchema" in profile
    assert "LegacySchema" in business and 'root["schema"] = Schema' in business
    assert "LegacySchema" in handoff
    assert "LegacyBackupSchema" in backup
    assert "SchemaMatches" in import_export
    assert '("qianniu" + "-ai-bot") + expected.Substring("qnbot".Length)' in import_export


def test_legacy_appdata_identity_remains_untouched_for_existing_installations():
    # QianniuAiBot is a historical product/data identity, not the retired repository slug.
    matches = []
    for path in (ROOT / "src").rglob("*.cs"):
        try:
            text = path.read_text(encoding="utf-8-sig")
        except (UnicodeDecodeError, OSError):
            continue
        if "QianniuAiBot" in text:
            matches.append(path)
    assert matches, "legacy persistent-data identity unexpectedly disappeared; migration compatibility must be reviewed"
