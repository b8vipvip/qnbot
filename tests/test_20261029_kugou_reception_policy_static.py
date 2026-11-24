"""Kugou merchant reception: non-destructive policy and send authority guards."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

POLICY = "src/Bot/ChromeNs/KugouReceptionPolicy.cs"
TEXT = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
VISION = "src/Bot/ChromeNs/VisionRequestService.cs"
SENDER = "src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs"

def test_scoped_identity_prevents_cross_shop_knowledge_leak():
    p = src(POLICY)
    assert "KnowledgeEngineV2Repository.LoadAll(seller)" in p
    assert "config.ProductIds" in p
    assert "productIds.Contains(id ?? string.Empty)" in p
    assert "config.Enabled" in p
    assert "HasStableSellerId" in p

def test_quarantine_is_non_destructive_and_applies_to_both_knowledge_paths():
    p = src(POLICY)
    assert "config.QuarantineKnowledgeIds" in p
    assert "config.QuarantineTextPatterns" in p
    v2 = src("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    smart = src("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "KugouReceptionPolicy.IsQuarantined(seller, record.Id, record.Title, record.Answer)" in v2
    assert "KugouReceptionPolicy.IsQuarantined(seller, x.Id, x.Title, x.Answer)" in smart
    assert "KnowledgeEngineV2Repository.ReplaceAll" not in p
    assert "Delete(" not in p

def test_phone_only_wait_blocks_before_other_auto_reply_lanes():
    p = src(POLICY)
    premerge = src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "PhoneOnly.IsMatch(input)" in p
    assert "PhotoInstruction.IsMatch(instruction)" in p
    assert "config.PhotoInstructionExcludedCues" in p
    assert "HasAllCues(instruction, config.PhotoInstructionAllCues)" in p
    assert premerge.index("KugouReceptionPolicy.TryWaitForEvidence(") < premerge.index(
        "ManualRechargeAssistantService.TryHandleAsync("
    )
    assert 'return CanonicalPreMergeOutcome.Consumed;' in premerge

def test_all_reply_lanes_share_reception_send_gate():
    text = src(TEXT)
    premerge = src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    vision = src(SENDER)
    assert 'lease.MarkCompleted("reception_business_guard_suppressed");' in text
    assert "KugouReceptionPolicy.BlockOutgoing(" in text
    assert "KugouReceptionPolicy.BlockOutgoing(" in premerge
    assert "KugouReceptionPolicy.BlockOutgoing(" in vision
    assert 'lifecycleLease.MarkCompleted("vision_business_guard_suppressed");' in vision
    assert "ParallelReplyRelevanceGate.ShouldSend(" in vision

def test_visual_branch_uses_evidence_not_product_title_to_authorize():
    policy = src(POLICY)
    vision = src(VISION)
    assert "KugouReceptionPolicy.TryResolveVisual(" in vision
    assert "result.VisualSummary, result.LocalOcrText" in vision
    assert "config.VisualRules" in policy
    assert "MatchesVisualRule(compact, rule)" in policy
    assert 'rule.Reply.Replace("{guide_url}", config.GuideUrl' in policy
    assert "hisense_builtin_decline" not in policy
    assert "tcl_qq_reject" not in policy
    assert "tcl_need_verified_binding" not in policy
    assert "xiaomi_builtin_karaoke" not in policy
    assert "if (evidence.Trim().Length == 0) return false;" in policy

def test_explicit_photo_referential_followups_last_90_seconds():
    followup = src("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "ExplicitReferentialWindowSeconds = 90" in followup
    assert 'compact == "是这个不"' in followup
    assert r'@"^\d{5,20}$"' in followup
    assert "最近图片未与后续文字合并" in followup

def test_decision_audit_is_bounded_and_anonymizes_buyer():
    p = src(POLICY)
    assert "CrossProcessAtomicStateFile.Acquire(" in p
    assert "WriteAllTextAtomic(" in p
    assert "HashBuyer(seller, buyer)" in p
    assert "SHA256.Create()" in p
    assert ".Take(300)" in p
    assert "AddDays(-7)" in p

def test_uncertain_or_negated_image_evidence_cannot_authorize_device():
    policy = src(POLICY)
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    assert "visual_evidence_uncertain" in policy
    assert "ASK_EVIDENCE" in policy
    assert "need_resource_confirmation" in policy
    assert "HasPositiveCue(text, cue)" in policy
    assert "NonePositiveCues" in policy
    assert "visual_uncertain_patterns" in config
    assert "positive_cue_groups" in config
    assert "none_positive_cues" in config


def test_reception_policy_cache_and_knowledge_detection_are_shopkey_scoped():
    policy = src(POLICY)
    applies = policy[
        policy.index("public static bool Applies"):
        policy.index("internal static bool RequiresManualKnowledgeReview")
    ]

    assert "internal static ShopContext ResolveShopForSeller(string seller)" in policy
    assert "var cacheKey = shop.ShopKey;" in applies
    assert "Shops.TryGetValue(cacheKey" in applies
    assert "Shops[cacheKey]" in applies
    assert "using (ShopSettingsScope.Enter(shop))" in applies
    assert "KnowledgeEngineV2Repository.LoadAll(seller)" in applies
    locator = src("src/Bot/ShopScope/ShopContextLocator.cs")
    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in policy
    assert "current.SellerNick" in locator
    assert "current.DisplayName" in locator
    assert "请求的ShopKey与当前店铺作用域不一致" in locator
    assert "请求的卖家与当前店铺作用域不一致" in locator


def test_reception_config_and_audit_keep_the_exact_current_shop_scope():
    policy = src(POLICY)
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")

    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in config
    assert policy.count("var shop = ResolveShopForSeller(seller);") >= 3
    assert "var cacheKey = shop.ShopKey;" in policy



def test_private_reception_profile_v2_is_shopkey_scoped_and_disabled_by_default():
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    policy = src(POLICY)
    assert 'qnbot.shop.reception-rules.v3' in config
    assert '["enabled"] = false' in config
    assert '["product_ids"] = new JArray()' in config
    assert '["quarantine_knowledge_ids"] = new JArray()' in config
    assert '["guide_url"] = string.Empty' in config
    assert '["policy_prompt"] = string.Empty' in config
    assert "shop.HasStableSellerId" in config
    assert 'Path.Combine(folder, "shop-reception-rules.json")' in config
    assert "KugouReceptionPolicy.InvalidateShopCache(shop.ShopKey)" in config
    assert "KugouReceptionConfigService.GetConfig(shop)" in policy


def test_legacy_reception_profile_migrates_disabled_and_requires_manual_review():
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    migration = config[
        config.index("private static JObject MigrateLegacyV1"):
        config.index("private static bool IsHttpsUrl")
    ]
    assert "LegacySchema" in migration
    assert 'migrated["notes"]' in migration
    assert 'migrated = DefaultDocument()' in migration
    assert '["enabled"] = false' in config
    assert 'legacy.Value<bool?>("manual_review_required") != true' in migration
    assert 'legacy.Value<bool?>("automatic_recharge_enabled") != false' in migration


def test_store_visual_facts_and_reply_templates_are_data_driven_not_public_constants():
    policy = src(POLICY)
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    assert "KugouVisualRule" in config
    assert "all_cue_groups" in config
    assert "positive_cue_groups" in config
    assert "visual_rules" in config
    assert "policy_prompt" in config
    assert "blocked_outgoing_patterns" in config
    assert "quarantine_text_patterns" in config
    assert "TClGuideUrl" not in policy
    assert "FirstProduct" not in policy
    assert "SecondProduct" not in policy



def test_high_risk_learning_review_is_global_and_not_disabled_by_optional_shop_profile():
    policy = src(POLICY)
    method = policy[
        policy.index("internal static bool RequiresManualKnowledgeReview"):
        policy.index("public static bool IsQuarantined")
    ]
    assert "if (!Applies(seller))" not in method
    assert "会员|设备|账号|登录|绑定|充值|退款|支付|付款|到账|验证码|密码" in method


def test_private_config_regex_patterns_have_a_runtime_timeout_and_fail_closed():
    policy = src(POLICY)
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    assert "SafeIsMatch" in policy
    assert "return true;" in policy[policy.index("private static bool SafeIsMatch"):policy.index("private static bool MatchesVisualRule")]
    assert "TimeSpan.FromMilliseconds(100)" in policy
    assert "new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))" in config



def test_private_profile_schema_documentation_covers_fail_closed_enablement_and_visual_rule_semantics():
    docs = src("docs/SHOPKEY_RECEPTION_PROFILE_V2.md")
    assert "qnbot.shop.reception-rules.v3" in docs
    assert "enabled" in docs and "product_ids" in docs
    assert "all_cue_groups" in docs and "positive_cue_groups" in docs
    assert "none_positive_cues" in docs
    assert "manual_review_required" in docs
    assert "automatic_recharge_enabled" in docs
    assert "默认关闭" in docs
    assert "v1" in docs and "备份" in docs



def test_public_policy_sources_and_test_fixtures_contain_no_merchant_identifiers_or_urls():
    paths = [
        POLICY,
        "src/Bot/ChromeNs/KugouReceptionConfigService.cs",
        "docs/SHOPKEY_RECEPTION_PROFILE_V2.md",
        "tests/test_20261029_kugou_reception_learning_static.py",
    ]
    combined = "\n".join(src(path) for path in paths)
    fixture = src("tests/test_20261029_kugou_reception_policy_static.py")
    import re
    assert not re.search(r"(?<![A-Za-z0-9])\d{12,20}(?![A-Za-z0-9])", combined + "\n" + fixture)
    assert not re.search(r"(?<![A-Za-z0-9])[a-f0-9]{24,}(?![A-Za-z0-9])", combined + "\n" + fixture, re.I)
    assert not re.search(r"https?://", combined, re.I)

def test_corrupt_reception_audit_is_never_replaced_with_an_empty_journal():
    policy = src(POLICY)
    audit = policy[
        policy.index("internal static void Audit("):
        policy.index("private static string HashBuyer")
    ]
    assert "if (!string.IsNullOrWhiteSpace(error))" in audit
    assert "保留原文件不覆盖" in audit
    assert "new InvalidDataException" in audit
    assert "entries = new List<ReceptionAuditEntry>();" in audit
    assert "catch { entries = new List<ReceptionAuditEntry>(); }" not in audit

def test_private_reception_restore_skips_corrupt_newest_backup_without_overwriting_current_config():
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    restore = config[
        config.index("public static string RestoreLatestBackup"):
        config.index("\n    }\n}")
    ]
    assert "OrderByDescending(x => x, StringComparer.Ordinal).ToList()" in restore
    assert "foreach (var candidate in backups)" in restore
    assert "Validate(File.ReadAllText(candidate, Encoding.UTF8))" in restore
    assert "没有找到有效的店铺接待规则备份，当前配置未修改" in restore
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(path, original, 3, 50, out error)" in restore
    assert "Guid.NewGuid().ToString(\"N\")" in restore

def test_private_profile_backup_names_are_unique_under_rapid_repeated_saves():
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    save = config[
        config.index("public static string SaveJson"):
        config.index("public static string RestoreLatestBackup")
    ]
    assert 'path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")' in save
    assert 'Guid.NewGuid().ToString("N")' in save
    assert "File.Copy(path, backup, false);" in save




def test_private_profile_string_fields_and_visual_rule_ids_are_strictly_bounded():
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    assert "private static string ReadStringField(JObject obj, string name, int maxLength, bool required)" in config
    assert 'ReadStringField(obj, "policy_prompt", 12000, true)' in config
    assert 'ReadStringField(obj, "visual_uncertain_reply", 2000, true)' in config
    assert 'ReadStringField(obj, "screenshot_reply_pattern", 1000, true)' in config
    assert 'ReadStringField(obj, "phone_wait_reason", 500, true)' in config
    assert 'ReadStringField(obj, "notes", 2000, false)' in config
    assert 'if (!SafeId.IsMatch(id))' in config
    assert 'visualRuleIds.Add(ValidateVisualRule(token, guideUrl))' in config
    assert 'RequireBoolean(obj, "automatic_recharge_enabled")' in config


def test_offline_shop_scope_uses_display_name_only_when_it_matches_the_exact_profile():
    locator = src("src/Bot/ShopScope/ShopContextLocator.cs")
    resolver = locator[
        locator.index("public static ShopContext ResolveCurrentOrBySellerNick"):
        locator.index("private static bool IsQianniuShopKey")
    ]

    assert "displayNameMatches" in resolver
    assert "sellerMatches" in resolver
    assert "if (!sellerMatches && !displayNameMatches)" in resolver
    assert "return ResolveRuntimeByShopKey(sellerNick)" in resolver


def test_expired_shop_detection_cache_entries_are_pruned_without_racing_refreshes():
    policy = src(POLICY)
    cleanup = policy[
        policy.index("private static void CleanupExpiredShopCache"):
        policy.index("internal static ShopContext ResolveShopForSeller")
    ]
    applies = policy[
        policy.index("public static bool Applies"):
        policy.index("internal static bool RequiresManualKnowledgeReview")
    ]

    assert "Interlocked.Read(ref _lastShopCacheSweepUtcTicks)" in cleanup
    assert "Interlocked.CompareExchange(" in cleanup
    assert "TimeSpan.FromMinutes(1).Ticks" in cleanup
    assert "pair.Value.ExpiresUtc <= now" in cleanup
    assert "ICollection<KeyValuePair<string, ShopDetection>>" in cleanup
    assert "exactEntries.Remove(pair)" in cleanup
    assert "CleanupExpiredShopCache();" in applies
    assert applies.index("CleanupExpiredShopCache();") < applies.index("Shops.TryGetValue(cacheKey")


def test_cross_process_shop_profile_changes_invalidate_cached_detection_immediately():
    policy = src(POLICY)
    config = src("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    applies = policy[
        policy.index("public static bool Applies"):
        policy.index("internal static bool RequiresManualKnowledgeReview")
    ]
    revision = config[
        config.index("internal static string GetProfileRevision"):
        config.index("internal static KugouReceptionConfig GetConfig(ShopContext shop)")
    ]

    assert "KugouReceptionConfigService.GetProfileRevision(shop)" in applies
    assert "cached.ProfileRevision, profileRevision" in applies
    assert "ProfileRevision = profileRevision" in applies
    assert "info.LastWriteTimeUtc.Ticks.ToString()" in revision
    assert "info.Length.ToString()" in revision
    assert ': "missing";' in revision
    assert "return string.Empty;" in revision
