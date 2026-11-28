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
