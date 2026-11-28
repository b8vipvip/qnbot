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


def test_buyer_runtime_builds_a_cold_v2_snapshot_instead_of_skipping_retrieval():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")

    assert "public static bool IsSnapshotReady" in public
    assert "PrepareSeller(burst.ShopKey, burst.SellerNick);" in runtime
    assert "QueueWarm(shopKey, seller)" in runtime
    assert "KnowledgeEngineV2Service.Resolve(burst.SellerNick, burst.BuyerNick, retrievalQuestion)" in runtime
    assert "if (!KnowledgeEngineV2Service.IsSnapshotReady(burst.SellerNick))" not in runtime
    assert "builds the exact ShopKey snapshot under its per-shop build lock when cold" in runtime
    assert "return KnowledgeV2RuntimeResult.Continue(warmContext);" not in runtime

def test_v2_edits_use_atomic_incremental_snapshot_updates():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    assert 'KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)' in repo
    assert 'internal static void ApplyRecordUpdate' in incremental
    assert 'CloneSnapshot(current)' in incremental
    assert 'RemoveRecordFromIndexes' in incremental
    assert 'AddRecordToIndexes' in incremental
    assert 'Snapshots[shopKey] = next;' in incremental
    refresh = incremental[incremental.index("private static void RefreshCommittedRecord"):incremental.index("internal static void ApplyRecordDelete")]
    assert refresh.index("lock (BuildLocks.GetOrAdd(shopKey") < refresh.index("KnowledgeEngineV2Repository.LoadCommittedRecordForSnapshot(seller, id, out version, out mutationId)") < refresh.index("Snapshots[shopKey] = next;")
    assert "KnowledgeV2AuthorityPolicy.NormalizeForRead(record)" in refresh
    assert "RefreshCommittedRecord(seller, shop.ShopKey, record.Id)" in incremental
    assert "internal static KnowledgeV2Record LoadCommittedRecord" in repo


def test_v2_disable_delete_and_bulk_replace_keep_snapshot_hot():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    assert 'KnowledgeEngineV2Service.ApplyRecordDelete(seller, id)' in repo
    assert 'KnowledgeEngineV2Service.ReplaceSnapshot(seller, normalized)' in repo
    assert 'internal static void ApplyRecordDelete' in incremental
    assert 'replacement.Status = "deleted";' in incremental
    assert "RefreshCommittedRecord(seller, shop.ShopKey, id)" in incremental
    assert 'internal static void ReplaceSnapshot' in incremental
    assert 'BuildSnapshot(shop.ShopKey, enabled)' in incremental
    assert 'if (indexable && index >= 0)' in incremental
    replace = incremental[incremental.index("internal static void ReplaceSnapshot"):incremental.index("private static Snapshot CloneSnapshot")]
    assert replace.index("lock (BuildLocks.GetOrAdd(shop.ShopKey") < replace.index("KnowledgeEngineV2Repository.LoadSnapshotCorpus(seller, out version)") < replace.index("BuildSnapshot(shop.ShopKey, enabled)")
    assert "records ?? Enumerable.Empty" not in replace
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
    assert 'if (!hasCurrentProduct)' in semantics
    assert 'memory.Entities : memory.Entities.Where(IsProductEntity)' in semantics
    assert 'if (query.Predicate == "general") query.Predicate = memory.Predicate;' in semantics


def test_v2_runtime_has_no_memory_v1_handler_and_preserves_safe_send_path():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    app = read("src/Bot/App.xaml.cs")
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeMemoryEngine.cs").exists()
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeMemoryEngineUi.cs").exists()
    assert "KnowledgeMemoryRuntimeBridge" not in runtime
    assert "KnowledgeMemoryEngine" not in app
    assert "TryHandleWithContextAsync(" in runtime
    assert "BuyerMessageBurstLease lease" in runtime
    assert "if (!KnowledgeEngineV2Service.IsEnabled(burst.SellerNick))" in runtime
    assert "ParallelReplyRelevanceGate.ShouldSend" in runtime
    assert "SendTextWithRetryAsync" in runtime

def test_v2_is_called_explicitly_by_the_canonical_streaming_handler_not_a_racing_reflection_wrapper():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "TryHandleAsync(QN qn, BuyerMessageBurstLease lease)" in runtime
    assert "V2HandlerWrapper" not in runtime
    assert "PatchedCoordinators" not in runtime
    assert "PatchExisting()" not in runtime
    plan = streaming.index("private static async Task ProcessTextBurstStreamingAsync")
    v2 = streaming.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)", plan)
    generating = streaming.index('lease.MarkGenerating("streaming_answer_started")', plan)
    ai = streaming.index("StreamingBuyerAnswerService.GetAnswerAsync(", plan)
    assert v2 < generating < ai
    assert "knowledgeV2Result = await KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease);" in streaming
    assert "knowledgeV2Result.ContextPromptAddon" in streaming


def test_verified_human_evidence_auto_publishes_but_unverified_suggestions_remain_candidates():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    session = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    history = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")

    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in public
    assert "public static bool IsCandidate(KnowledgeV2Record record)" in bridge
    assert "public static bool IsAutoPublishedHumanEvidenceSource" in bridge
    assert "ai_auto_publish_verified_human_evidence" in session
    assert "EnableVerifiedCorpusNewKnowledgeWrites = true" in session
    assert "EnableVerifiedCorpusCorrectionWrites = true" in session
    assert "EnableAutomaticWrites = false" in session
    assert "TryCreateVerifiedSessionKnowledge(" in repo
    assert "ai_auto_learned_human_evidence" in bridge
    assert "if (record != null && IsAutoPublishedHumanEvidenceSource(record.SourceType))" in bridge
    assert "evidenceValid" in history
    assert "chat_history_unverified_candidate" in history
    assert "BotFeatureStore.SaveKnowledgeBase" not in repo

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

def test_knowledge_v2_disabled_and_deleted_provenance_is_never_normalized_back_to_active():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    auto = bridge.split("private static void ApplyAutoPublished(", 1)[1].split(
        "private static void ApplyHumanConfirmed(", 1
    )[0]
    human = bridge.split("private static void ApplyHumanConfirmed(", 1)[1].split(
        "\n    }\n}", 1
    )[0]

    for method in (auto, human):
        assert "record == null || !record.Enabled" in method
        assert '!string.Equals(record.Status, "active"' in method
        assert 'IsCandidate(record)' in method
        assert 'record.Status = "active";' in method
    assert "IsPersistedStateSynchronized" not in bridge
    assert "MirrorOneToLegacy" not in read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

def test_knowledge_center_hides_deleted_tombstones_but_keeps_disabled_records_manageable():
    page = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    apply_filter = page.split("private void ApplyFilter()", 1)[1].split(
        "private bool MatchesMode(", 1
    )[0]
    assert 'x.Status, "deleted"' in apply_filter
    assert "Where(MatchesMode)" in apply_filter

    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    stats = public.split("public static KnowledgeV2Stats GetStats(", 1)[1].split(
        "public static void Invalidate(", 1
    )[0]
    assert 'x.Status, "deleted"' in stats


def test_bot_reply_knowledge_mapping_only_offers_enabled_production_records():
    page = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    rank = page.split("private static List<BotReplyKnowledgeResult> RankKnowledge(", 1)[1].split(
        "private static", 1
    )[0]
    assert "x.Enabled" in rank
    assert 'x.Status, "deleted"' in rank
    assert "KnowledgeV2AuthorityPolicy.IsCandidate(x)" in rank


def test_ocr_cold_snapshot_resolves_instead_of_skipping_the_only_knowledge_engine():
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert "SnapshotWarmWaitMs" not in ocr
    assert "if (!KnowledgeEngineV2Service.IsSnapshotReady(knowledgeSeller))" not in ocr
    assert "Knowledge V2索引未就绪" not in ocr
    assert "KnowledgeEngineV2Service.ResolveWithEvidence(" in ocr
    assert "KnowledgeEngineV2Service.ResolveWithCuratedImageKnowledgeEvidence(" in ocr
    assert "cancellationToken.ThrowIfCancellationRequested();" in ocr
