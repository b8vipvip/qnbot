from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_direct_send_outcomes_are_recorded_without_treating_transport_failure_as_bad_knowledge():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "KnowledgeEngineV2FeedbackService.RecordDirectSend" in runtime
    assert 'success ? "sent" : "send_failed"' in feedback
    assert "Transport send failures are intentionally excluded from knowledge-quality penalties" in feedback
    adjustment = feedback.split("public static double GetQualityAdjustment", 1)[1].split("public static List<KnowledgeV2QualityItem>", 1)[0]
    assert "SendFailed" not in adjustment


def test_feedback_loop_uses_explicit_evidence_and_ignores_fast_bot_echo():
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert '"buyer_positive:"' in feedback
    assert '"buyer_negative"' in feedback
    assert '"manual_reply:"' in feedback
    assert '"withdrawal"' in feedback
    assert "if (similarity >= 0.92 && KnowledgeEngineV2Service.AnswerFactsCompatible" in feedback
    assert "manual_same_answer" not in feedback
    assert "IsAfterSend(pending, messageTime)" in feedback
    assert "BuyerIdentityAliasService.AreEquivalent" in feedback


def test_feedback_is_append_only_and_keeps_hot_aggregate_cache():
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert 'knowledge-feedback-v2.db' in feedback
    assert "KnowledgeV2FeedbackEventRow" in feedback
    assert "ConcurrentDictionary<string, Aggregate> ByKnowledgeId" in feedback
    assert "ApplyEventToAggregate(cached.ByKnowledgeId, row)" in feedback
    assert "public static void Warm(string seller)" in feedback


def test_feedback_quality_influences_ranking_conservatively():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "Score(seller, snapshot.Records[i], query)" in public
    assert "KnowledgeEngineV2FeedbackService.GetQualityAdjustment(seller, record.Id, record.Answer)" in index
    assert "record.Confidence * 0.72 + record.Authority * 0.28 + feedbackAdjustment" in index
    assert 'feedback=" + feedbackAdjustment' in index


def test_raw_qn_messages_feed_quality_evidence():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2FeedbackBridge.cs")
    assert "EvRecieveNewMessage" in bridge
    assert "ConversationContextStore.IsWithdrawalNotice" in bridge
    assert "KnowledgeEngineV2FeedbackService.ObserveBuyerMessage" in bridge
    assert "KnowledgeEngineV2FeedbackService.ObserveSellerMessage" in bridge
    assert "KnowledgeEngineV2FeedbackService.ObserveWithdrawal" in bridge


def test_quality_dashboard_exposes_required_operational_metrics():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2QualityUi.cs")
    for label in ["知识质量与真实使用反馈", "命中次数", "人工纠正", "撤回", "纠错率", "发送失败", "最近使用", "低质量"]:
        assert label in ui
    assert 'Content = "质量"' in ui
    assert "GetQualityItems" in ui
    assert "GetRecentEvents" in ui


def test_feedback_components_are_compiled_for_wpf_and_normal_bot_projects():
    props = read("src/Bot/Directory.Build.props")
    for name in [
        "KnowledgeEngineV2FeedbackService.cs",
        "KnowledgeCenterV2QualityUi.cs",
        "KnowledgeEngineV2FeedbackBridge.cs",
    ]:
        assert name in props


def test_runtime_bootstraps_feedback_observer_and_quality_ui():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "KnowledgeV2QualityUiBridge.Initialize()" in runtime
    assert "KnowledgeEngineV2FeedbackBridge.Initialize()" in runtime


def test_feedback_ingress_deduplicates_by_shopkey_and_uses_canonical_message_roles():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2FeedbackBridge.cs")
    observe = bridge[
        bridge.index("private static void ObserveMessage"):
        bridge.index("private static string ExtractMessageText")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in observe
    assert "using (ShopSettingsScope.Enter(shop))" in observe
    assert 'shop.ShopKey + "|v2-feedback:" + messageKey' in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(to, seller)" in observe
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message)" in observe
    assert "var isBuyer = fromIsBuyer && toIsSeller;" in observe
    assert "var isSeller = fromIsSeller && toIsBuyer;" in observe


def test_feedback_observer_uses_weak_qn_identity_and_retryable_attachment():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2FeedbackBridge.cs")
    attach = bridge[
        bridge.index("private static void AttachExisting"):
        bridge.index("private static void OnRawMessages")
    ]
    assert "ConditionalWeakTable<QN, object> Attached" in bridge
    assert "lock (AttachSync)" in attach
    assert "Attached.TryGetValue(qn, out marker)" in attach
    assert "Attached.Add(qn, new object())" in attach
    assert "var seller = qn.Seller" in attach
    assert "var seller = captured.Seller" not in attach
    assert "RuntimeHelpers.GetHashCode(qn)" not in attach


def test_feedback_pending_keys_and_alias_search_are_shop_isolated():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "Pending[PendingKey(shop.ShopKey, buyer)]" in source
    assert "string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal)" in source
    assert "matches.Count != 1" in source
    assert "PendingKey(pending.ShopKey, pending.Buyer), pending" in source
    assert "if (!RemovePending(pending)) return;" in source
    assert "PendingKey(pending.Seller, pending.Buyer)" not in source

def test_feedback_cache_build_and_event_write_share_repository_lock():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    append = source.split("private static void AppendEvent(", 1)[1].split("private static ConcurrentDictionary<string, Aggregate> GetAggregates", 1)[0]
    assert append.index("lock (state.Sync)") < append.index("state.Db.SaveOneRecord(row)") < append.index("ApplyEventToAggregate(cached.ByKnowledgeId, row)")
    load = source.split("private static ConcurrentDictionary<string, Aggregate> GetAggregates", 1)[1].split("private static void ApplyEventToAggregate", 1)[0]
    assert load.index("lock (state.Sync)") < load.index("state.Db.ReadRecords") < load.index("AggregateCaches[shop.ShopKey] = next")
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2QualityUi.cs")
    assert "ShopContextLocator.ResolveRuntimeByShopKey(_shopKey)" in ui
    assert "InShop(() => KnowledgeEngineV2FeedbackService.GetQualityItems" in ui

def test_feedback_restore_is_shop_bound_idempotent_and_invalidates_cache():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    restore = source.split("internal static int MergeBackupEvents",1)[1].split("private static bool RemovePending",1)[0]
    assert "shop.ShopKey, sourceShopKey" in restore
    assert "existing.TryGetValue(row.Id, out old)" in restore
    assert "state.Db.SaveRecordsInTransaction(additions)" in restore
    assert "AggregateCaches.TryRemove(shop.ShopKey" in restore
    assert "messageTime >= pending.SentAt" in source

def test_manual_correction_does_not_ignore_opposite_or_numeric_answers_at_high_similarity():
    source = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "KnowledgeEngineV2Service.AnswerFactsCompatible(botAnswer, manualAnswer)" in source
    assert "double.IsNaN(confidence) || double.IsInfinity(confidence)" in source
    assert "confidence < 0.95 || confidence > 1.0" in source

def test_feedback_sync_commits_cursor_and_ack_with_event_rows():
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    cloud = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    assert "FeedbackAckKey(scope, epoch, x.Id)" in feedback
    assert "Take(200)" in feedback
    assert "SetEquals(acks.Values<string>())" in feedback
    assert "writes.Add(new KnowledgeV2FeedbackCursorRow" in feedback
    assert "state.Db.SaveRecordsInTransaction(writes)" in feedback
    assert "CloudUtcTicks = utc" in feedback
    assert "Buyer = string.Empty, Question = string.Empty, Answer = string.Empty" in feedback
    assert "/api/runtime/v1/bot-web/knowledge-v2-feedback-sync" in cloud
    assert "state.FeedbackStatus" in cloud

def test_feedback_cache_expires_and_backup_restore_checks_event_identity():
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "cached.ExpiresUtc > DateTime.UtcNow" in feedback
    assert "FeedbackUtcTicks(x) >= cutoff" in feedback
    assert "if (!negative || pending.NegativeBuyerReaction) return;" in feedback
    assert "FeedbackUtcTicks(old) != FeedbackUtcTicks(row)" in feedback
    assert 'old.Evidence == "cloud_synced_statistic"' in feedback
    assert "row.CloudUtcTicks > DateTime.UtcNow.AddMinutes(5).Ticks" in feedback


def test_feedback_rank_is_version_specific_and_negative_reactions_do_not_become_acceptance():
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "aggregate.ByAnswer.TryGetValue(version, out exact)" in feedback
    assert "AnswerVersion(row.Answer)" in feedback
    assert 'type == "buyer_negative"' in feedback
    assert "pending.NegativeBuyerReaction || pending.WithdrawalRecorded" in feedback
    assert "answer.Length >= 1400" in feedback
