from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ai_final_send_rechecks_committed_context_and_current_business_policy():
    code = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    start = code.index("Func<bool> preSendGuard = () =>")
    end = code.index("bool sendOk;", start)
    guard = code[start:end]
    assert "ShopReceptionPolicy.BlockOutgoing(" in guard
    assert "burst.ShopKey, workflowDecision != null" in guard
    assert "IsContextKnowledgeCurrent(" in guard
    assert "BuyerReplyOutputGuard.TryNormalizeForBuyer" in guard
    assert "PreSendAnswerValidator.Validate(" in guard
    assert "validation.Action != AnswerValidationAction.Pass" in guard
    assert "preSendGuard" in code[end:code.index("catch (OperationCanceledException)", end)]
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = bridge[bridge.index("internal static bool IsContextKnowledgeCurrent"):bridge.index("private static string SafeContext")]
    assert "LoadCommittedRecord(seller, record.Id)" in method
    assert "EditorRecordFingerprint(record)" in method
    assert "EditorRecordFingerprint(committed)" in method
    assert "ShopReceptionPolicy.IsQuarantined" in method


def test_recall_filters_inapplicable_candidates_before_bounded_ranking():
    code = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    recall = code[code.index("private static HashSet<int> Recall"):code.index("private static KnowledgeV2Match Score")]
    assert recall.index("IsApplicable(snapshot.Records[i]") < recall.index("Take(Math.Max(0, 96")
    assert "OrderByDescending(i => KnowledgeV2AuthorityPolicy.IsProductionApproved" in recall
    assert "new HashSet<int>(exactIds)" in recall


def test_remote_scan_binds_exact_checkpoint_before_contact_work_and_recovers_no_mutation():
    scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    bound = scan.index("options.OnCheckpointBound(checkpoint.Id, evidence.Record.RunId)")
    assert scan.index("BuyerHistoryScanCheckpointStore.Save(seller, checkpoint);") < bound
    assert bound < scan.index("FindConnectedQnByShopKey(shopKey)", bound)
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    recovery = sync[sync.index("private static JObject RecoverInterruptedKnowledgeOperation"):sync.index("private static void StartKnowledgeOperation")]
    assert "ReadBoundRun(" in recovery
    assert 'checkpoint.State == "completed"' in recovery
    assert "checkpoint.Completed.Count == checkpoint.Contacts.Count" in recovery
    assert '["coverage_verified"] = false' in recovery
    assert 'completed ? "completed" : "interrupted"' in recovery
    assert "ExecuteKnowledgeOperationAsync" not in recovery
    assert "Latest(" not in recovery
    assert 'receipt["checkpoint_id"] = checkpointId;' in sync
    assert 'receipt["run_id"] = runId;' in sync
    store = read("src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs")
    assert "string.Equals(value.LastRunId, runId, StringComparison.Ordinal)" in store


def test_both_vision_senders_use_current_shop_facts_and_preserve_context_decision():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    guard = vision[vision.index("internal static bool CanSendCurrentAnswer"):vision.index("private static void ApplyImageKnowledgeContext")]
    assert "ShopSettingsScope.Enter(shop, false)" in guard
    assert "ShopReceptionPolicy.BlockOutgoing(" in guard
    assert "IsMatchCurrent(" in guard
    assert "IsSelectedKnowledgeCurrent(" in guard
    assert "IsContextKnowledgeCurrent(" in guard
    assert "AllowsUnresolvedKnowledgeReply(" in guard
    assert "PreSendAnswerValidator.Validate(" in guard
    assert "result.Answer = learned.Answer" not in vision
    assert "result.ContextKnowledgeDecision = localDecision.ContextKnowledgeDecision" in vision
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert "ContextKnowledgeDecision = decision" in ocr
    assert "MatchedImageKnowledgeFingerprint = imageKnowledgeMatch" in ocr
    for path, anchor in [
        ("src/Bot/ChromeNs/QN.cs", "Func<bool> visionPreSendGuard = () =>"),
        ("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs", "Func<bool> preSendGuard = () =>"),
    ]:
        pipeline = read(path)
        start = pipeline.index(anchor)
        end = pipeline.index("bool sendOk;", start)
        assert "VisionRequestService.CanSendCurrentAnswer(" in pipeline[start:end]


def test_remote_mutations_have_durable_receipts_and_recovery_does_not_reexecute():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    rollback = repo[repo.index("public static bool TryRollbackLastBotCorrection"):repo.index("internal static bool TryDeleteEditorRecords")]
    assert 'RemoteMutationReceipt(operationReceipt, "rollback")' in rollback
    assert "writes.Add(receipt)" in rollback
    assert "SaveRecordsInTransaction(writes)" in rollback
    cas = repo[repo.index("internal static bool TryReplaceAllIfUnchanged"):repo.index("internal static int ResumePortableImport")]
    assert 'RemoteMutationReceipt(operationReceipt, "conflict_resolve")' in cas
    assert "replacementRows.Add(receipt)" in cas
    assert "ReplaceRecordsInTransaction(" in cas
    sync = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    recovery = sync[sync.index("private static JObject RecoverInterruptedKnowledgeOperation"):sync.index("private static void StartKnowledgeOperation")]
    assert "HasRemoteMutationReceipt(" in recovery
    assert "HasCommittedCandidateOutcome(" in recovery
    assert "ExecuteKnowledgeOperationAsync" not in recovery


def test_revision_sync_merges_only_immutable_history_and_checkpoint_atomically():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    apply = repo[repo.index("internal static bool ApplyRevisionSyncResponse"):repo.index("internal static Dictionary<string, long> GetCommittedRevisionCandidateReceipts")]
    assert "JToken.DeepEquals" in apply
    assert "RevisionSyncCheckpointKey(scope)" in apply
    assert "SaveRecordsInTransaction(writes)" in apply
    assert "ToRow(" not in apply
    assert "ReplaceRecordsInTransaction(" not in apply
    cloud = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    assert '"/api/runtime/v1/bot-web/knowledge-v2-revision-sync"' in cloud


def test_rule_version_and_snapshot_generation_are_rechecked_on_send():
    visual = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    assert "best == null || tied" in visual
    assert "RuleFingerprint = RuleFingerprint(best)" in visual
    assert "return TryMatch(shopKey, seller, ocrText, out current)" in visual
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    assert "VisualUnderstandingRuleService.IsMatchCurrent(" in vision
    assert "result.MatchedVisualRuleOcrText" in vision
    assert "result.MatchedVisualRuleFingerprint = localDecision.MatchedVisualRuleFingerprint" in vision
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "decision.SnapshotGeneration = snapshot.Generation" in repo
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert bridge.count("IsDecisionSnapshotCurrent(seller,") >= 3


def test_real_windows_sqlite_crash_injection_is_ci_gated():
    workflow = read(".github/workflows/windows-build.yml")
    assert "test-knowledge-v2-sqlite-recovery.ps1" in workflow
    script = read("tests/windows/test-knowledge-v2-sqlite-recovery.ps1")
    assert "5001" in script and "ReadTable($rowType)" in script
    assert "RunInTransaction" in script and "SaveRecordsInTransaction" in script
    assert "Stop-Process -Id $PID -Force" in script
    assert "before-marker" in script and "after-marker" in script
    assert "receipt.Value -eq 'rollback'" in script
    assert "reload_rebuild_recall_p95_ms" in script
