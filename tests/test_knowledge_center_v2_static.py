from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_uses_sqlite_repository_and_structured_indexes():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    models = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    assert 'knowledge-center-v2.db' in repo
    assert 'SQLiteHelper' in repo
    assert 'Dictionary<string, HashSet<int>> Exact' in read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'Dictionary<string, HashSet<int>> Predicate' in read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'snapshot.Ngram' in index
    assert 'public string Predicate { get; set; }' in models
    assert 'public string Subject { get; set; }' in models


def test_v2_query_does_not_periodically_rebuild_full_index():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'ExpiresAt' not in public.split('private sealed class Snapshot', 1)[1].split('}', 1)[0]
    assert 'Snapshots.TryGetValue(shop.ShopKey' in index
    assert 'DateTime.Now.AddMinutes(10)' not in index
    assert 'Recall(snapshot, query)' in public


def test_buyer_runtime_never_waits_for_cold_v2_snapshot():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert 'public static bool IsSnapshotReady' in public
    assert 'if (!KnowledgeEngineV2Service.IsSnapshotReady(burst.SellerNick))' in runtime
    assert 'QueueWarm(burst.SellerNick);' in runtime
    assert 'await inner(lease);' in runtime
    assert 'WarmingSellers.TryAdd' in runtime


def test_v2_edits_use_atomic_incremental_snapshot_updates():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    assert 'KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)' in repo
    assert 'internal static void ApplyRecordUpdate' in incremental
    assert 'CloneSnapshot(current)' in incremental
    assert 'RemoveRecordFromIndexes' in incremental
    assert 'AddRecordToIndexes' in incremental
    assert 'Snapshots[shop.ShopKey] = next;' in incremental


def test_v2_disable_delete_and_bulk_replace_keep_snapshot_hot():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    assert 'KnowledgeEngineV2Service.ApplyRecordDelete(seller, id)' in repo
    assert 'KnowledgeEngineV2Service.ReplaceSnapshot(seller, normalized)' in repo
    assert 'internal static void ApplyRecordDelete' in incremental
    assert 'tombstone.Status = "deleted";' in incremental
    assert 'internal static void ReplaceSnapshot' in incremental
    assert 'BuildSnapshot(shop.ShopKey, enabled)' in incremental
    assert 'if (record.Enabled && index >= 0)' in incremental
    assert 'else KnowledgeEngineV2Service.Invalidate(seller);' not in repo


def test_conflict_is_scoped_to_same_fact_key():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    assert 'private static bool IsMeaningfulConflictPair' in index
    assert 'KnowledgeEngineV2Semantics.FactKey(left)' in index
    assert 'KnowledgeEngineV2Semantics.FactKey(right)' in index
    assert 'KnowledgeEngineV2Semantics.TextSimilarity(left.Title, right.Title)' in index

    # Conflict identity must distinguish otherwise similar facts that apply to
    # different intents/products/entities/conditions. FactKey remains the broad
    # business-scope boundary, while the pair helper additionally requires the
    # buyer questions and conclusions to be genuinely incompatible.
    assert 'var subject = Compact(record.Subject);' in semantics
    assert 'var predicate = NormalizePredicate(record.Predicate);' in semantics
    assert 'var intent = NormalizeIntent(record.Intent);' in semantics
    assert 'var products = Signature(record.ProductIds' in semantics
    assert 'var entities = Signature(' in semantics
    assert 'var conditions = Signature(record.Conditions' in semantics
    assert 'var required = Signature(record.RequiredContext' in semantics
    assert 'var exclusions = Signature(record.Exclusions' in semantics
    assert '+ "|p=" + products' in semantics
    assert '+ "|e=" + entities' in semantics
    assert '+ "|c=" + conditions' in semantics
    assert '+ "|r=" + required' in semantics
    assert '+ "|x=" + exclusions' in semantics


def test_working_memory_only_supplements_context_dependent_messages():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    assert 'if (query.ContextDependent && memory != null' in semantics
    assert 'if (query.Entities.Count == 0 && memory.Entities != null)' in semantics
    assert 'if (query.Predicate == "general") query.Predicate = memory.Predicate;' in semantics


def test_v2_runtime_retires_memory_v1_and_preserves_safe_send_path():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert 'StopLegacyMemoryTimer();' in runtime
    assert 'StripLegacyMemoryWrapper' in runtime
    assert 'ReplyModeService.IsLocalFirst' in runtime
    assert 'ParallelReplyRelevanceGate.ShouldSend' in runtime
    assert 'SendTextWithRetryAsync' in runtime


def test_v2_runtime_wraps_each_coordinator_only_once_even_when_other_layers_move_outermost():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "ConcurrentDictionary<BuyerMessageBurstCoordinator, byte> PatchedCoordinators" in runtime
    assert "PatchedCoordinators.TryAdd(coordinator, 0)" in runtime
    assert "PatchedCoordinators.TryRemove(coordinator, out ignored)" in runtime
    add = runtime.index("PatchedCoordinators.TryAdd(coordinator, 0)")
    read_handler = runtime.index("handlerField.GetValue(coordinator)", add)
    assert add < read_handler


def test_learning_candidates_require_explicit_approval_before_direct_reply():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    assert 'productionMatches = rankedMatches' in public
    assert '.Where(IsApprovedProductionMatch)' in public
    assert 'var best = productionMatches.FirstOrDefault();' in public
    assert 'var visibleMatches = rankedMatches.Take(5).ToList();' in public
    assert 'if (best != null && !visibleMatches.Contains(best)) visibleMatches.Add(best);' in public
    assert 'KnowledgeV2AuthorityPolicy.IsProductionApproved' in public
    assert 'public static bool IsCandidate(KnowledgeV2Record record)' in bridge
    assert 'if (IsExplicitHumanConfirmationSource(record.SourceType)) return false;' in bridge
    assert '当前只命中尚未批准的学习候选' in public
    assert 'record.Type = "learning_candidate";' in bridge
    assert 'record.Status = "candidate";' in bridge
    assert 'KnowledgeV2Record existing = null;' in bridge
    assert '!string.Equals(record.Status, "candidate"' in repo
    assert '!string.Equals(record.Type, "learning_candidate"' in repo


def test_disabled_records_remain_visible_to_knowledge_center_management():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert 'return KnowledgeEngineV2Repository.LoadAll(seller)' in public
    assert '.Select(Clone)' in public


def test_new_knowledge_center_has_required_navigation_and_debugger():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    ops = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    for label in ["知识", "商品知识", "流程", "学习", "冲突", "测试台", "导入导出", "设置"]:
        assert f'Nav("{label}"' in ui
    assert '30次性能测试' in ops
    assert 'P95 ≤ 50ms' in ops
    assert '导出V2完整包' in ops
    assert '导入V2完整包' in ops
    assert 'CreateAutomaticBackup' in ops


def test_build_props_compiles_all_v2_sources():
    props = read("src/Bot/Directory.Build.props")
    for name in [
        "KnowledgeEngineV2.Models.cs",
        "KnowledgeEngineV2.Repository.cs",
        "KnowledgeEngineV2.Semantics.cs",
        "KnowledgeEngineV2.Service.Index.cs",
        "KnowledgeEngineV2.Service.Incremental.cs",
        "KnowledgeEngineV2.Service.Public.cs",
        "KnowledgeCenterV2Ui.cs",
        "KnowledgeCenterHelpWindow.cs",
        "KnowledgeCenterV2RecordsPage.cs",
        "KnowledgeCenterV2OperationsPages.cs",
        "KnowledgeEngineV2RuntimeBridge.cs",
        "KnowledgeEngineV2LearningBridge.cs",
    ]:
        assert name in props
