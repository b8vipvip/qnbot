import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BOT = ROOT / "src" / "Bot"


def read(path):
    return path.read_text(encoding="utf-8-sig")


def policy():
    return json.loads(read(BOT / "default-business-policy.json"))


def test_default_policy_is_neutral_and_preserves_runtime_key_contract():
    data = policy()
    assert data["schema"] == "qnbot.business-policy"
    assert data["version"] >= 3
    for key in ("patterns", "stages", "facts", "buyerGoals", "prompts", "validationIssues", "handoffOverrides"):
        assert key in data

    progress = read(BOT / "ChromeNs" / "ConversationProgressGuardService.cs")
    used_patterns = set(re.findall(r'R\("([^"]+)"\)', progress))
    assert used_patterns.issubset(set(data["patterns"]))
    # PII and channel-shape detectors are universal safety primitives, not merchant policy.
    # Keep them active in the neutral package so phone numbers, verification codes and
    # image-only turns still enter the contextual path without exposing their raw values.
    core_safety_patterns = {
        "phone", "verificationCode", "codeRequest", "phoneRequest", "image"
    }
    assert core_safety_patterns.issubset(data["patterns"])
    assert all(data["patterns"][key] for key in core_safety_patterns)
    for key, pattern in data["patterns"].items():
        if key not in core_safety_patterns:
            assert pattern == ""
        elif pattern:
            re.compile(pattern, re.I)
    assert data["stages"] == {"preSaleGeneral": {"stage": "一般咨询", "nextAction": ""}}
    assert data["facts"] == {}
    assert data["buyerGoals"] == {}
    assert data["prompts"] == {}
    assert data["validationIssues"] == {}
    assert data["handoffOverrides"] == []

    serialized = json.dumps(data, ensure_ascii=False)
    for merchant_specific in ("酷狗", "大屏VIP", "k2n.cn", "guide_url"):
        assert merchant_specific not in serialized
    assert not re.search(r"https?://", serialized, re.I)


def test_empty_default_patterns_are_valid_noop_runtime_rules():
    service = read(BOT / "ChromeNs" / "BusinessPolicyProfileService.cs")
    assert 'if (string.IsNullOrWhiteSpace(pattern))' in service
    assert 'state.Regexes[path ?? string.Empty] = NeverRegex;' in service
    assert 'if (!string.IsNullOrWhiteSpace(pattern))' in service
    assert "CompileRequired(" not in service


def test_existing_shop_policy_is_preserved_and_legacy_adoption_is_single_shop_only():
    service = read(BOT / "ChromeNs" / "BusinessPolicyProfileService.cs")
    ensure = service[
        service.index("private static void EnsureUserFile"):
        service.index("private static bool CanAutoAdoptLegacy")
    ]
    assert "if (File.Exists(userPath)) return;" in ensure
    assert "Paths.GetRulesRoot(shop)" in service
    assert "shop != null && CanAutoAdoptLegacy() && File.Exists(legacy)" in ensure
    assert "Profiles.GetAll().Count == 1" in service


def test_custom_policy_can_configure_account_handoff_without_shipping_it_by_default():
    override = {
        "keyword": "account",
        "strongRiskPattern": r"password|verification code|account recovery|stolen|frozen",
        "allowAiPattern": r"can i (?:sign in|log in) with my own account",
        "fixedReplyPattern": r"another account|friend.?s account",
        "fixedReply": "Please ask the account owner to confirm the supported sign-in method."
    }
    normal_question = "Can I sign in with my own account on this device?"
    risky_question = "I forgot my account password and cannot receive a verification code."
    assert not re.search(override["strongRiskPattern"], normal_question, re.I)
    assert re.search(override["allowAiPattern"], normal_question, re.I)
    assert re.search(override["strongRiskPattern"], risky_question, re.I)
    assert policy()["handoffOverrides"] == []


def test_custom_patterns_can_disambiguate_purchase_selection_from_screenshot_questions():
    patterns = {
        "screenshotTargetQuestion": r"\b(?:which|what)\s+(?:page|screen)\b",
        "purchaseTargetQuestion": r"\b(?:which|what)\s+(?:product|link|sku)\b",
        "purchaseTargetAnswer": r"\b(?:product|link|sku|option)\b",
        "badPurchaseTargetAnswer": r"\b(?:capture|screenshot)\b.*\b(?:account|screen)\b"
    }
    order_question = "Which product link should I choose?"
    screenshot_question = "Which page should I capture?"
    assert not re.search(patterns["screenshotTargetQuestion"], order_question, re.I)
    assert re.search(patterns["purchaseTargetQuestion"], order_question, re.I)
    assert re.search(patterns["screenshotTargetQuestion"], screenshot_question, re.I)

    good = "Choose the product link that matches the requested item and SKU."
    wrong = "Please capture the account screen."
    assert re.search(patterns["purchaseTargetAnswer"], good, re.I)
    assert not re.search(patterns["badPurchaseTargetAnswer"], good, re.I)
    assert re.search(patterns["badPurchaseTargetAnswer"], wrong, re.I)


def test_progress_guard_routes_configured_purchase_target_as_contextual_order_intent():
    source = read(BOT / "ChromeNs" / "ConversationProgressGuardService.cs")
    assert "AsksPurchaseTarget" in source
    assert "!asksScreenshotTarget && R(\"purchaseTargetQuestion\")" in source
    assert "purchase_target_question" in source
    assert "prompts.purchaseTarget" in source
    assert "validationIssues.purchaseTargetScreenshot" in source
    assert "validationIssues.purchaseTargetMissingSelection" in source


def test_handoff_json_override_runs_before_remote_compatibility_layer():
    source = read(BOT / "ChromeNs" / "HandoffNotificationService.cs")
    client = source.index("BusinessPolicyProfileService.TryOverrideHandoff")
    remote = source.index("HandoffRuleRemoteConfigService.TryApplySafeAutoReply")
    assert client < remote


def test_business_policy_editor_and_package_wiring_exist():
    ui = read(BOT / "Knowledge" / "BusinessPolicyProfileUi.cs")
    props = read(BOT / "Directory.Build.props")
    assert "运行策略JSON" in ui
    assert "BusinessPolicyProfileService.SaveJson" in ui
    assert "BusinessPolicyProfileUi.cs" in props
    assert "BusinessPolicyProfileService.cs" in props
    assert "default-business-policy.json" in props
    assert "CopyToOutputDirectory" in props


def test_neutral_default_keeps_privacy_and_image_detection_enabled():
    data = policy()
    patterns = data["patterns"]
    assert re.search(patterns["phone"], "买家手机号 13800138000")
    assert re.search(patterns["verificationCode"], " 123456 ")
    assert re.search(patterns["codeRequest"], "请提供验证码")
    assert re.search(patterns["phoneRequest"], "麻烦发送手机号")
    assert re.search(patterns["image"], "[图片]")

    # The runtime stores only progress booleans / input kinds, never the matched values.
    source = read(BOT / "ChromeNs" / "ConversationProgressGuardService.cs")
    assert "HasPhoneNumber = R(\"phone\").IsMatch(buyerText)" in source
    assert "CurrentInputKind = \"phone_number\"" in source
    assert "CurrentInputKind = \"verification_code\"" in source
    assert "AddFact(state, progress.HasPhoneNumber, T(\"facts.phone\"))" in source
    assert "AddFact(state, progress.HasVerificationCode, T(\"facts.code\"))" in source
    assert "string PhoneNumber" not in source
    assert "string VerificationCode" not in source

def test_shop_policy_updates_are_cross_process_atomic_and_custom_regexes_are_bounded():
    service = read(BOT / "ChromeNs" / "BusinessPolicyProfileService.cs")
    save = service[
        service.index("public static string SaveJson"):
        service.index("public static string RestoreDefault")
    ]
    ensure = service[
        service.index("private static void EnsureUserFile"):
        service.index("private static bool CanAutoAdoptLegacy")
    ]
    compile_optional = service[
        service.index("private static Regex CompileOptional"):
        service.index("private static string ReadString")
    ]
    get_regex = service[
        service.index("public static Regex GetRegex"):
        service.index("public static string GetString")
    ]

    assert 'CrossProcessAtomicStateFile.Acquire(path, "BusinessPolicyProfile", 3000)' in save
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(path, normalized, 3, 50, out error)" in save
    assert "File.Copy(path, backup, false);" in save
    assert 'CrossProcessAtomicStateFile.Acquire(userPath, "BusinessPolicyProfile", 3000)' in ensure
    assert "if (File.Exists(userPath)) return;" in ensure
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(userPath, initial, 3, 50, out error)" in ensure
    assert "TimeSpan.FromMilliseconds(100)" in compile_optional
    assert "TimeSpan.FromMilliseconds(100)" in get_regex

def test_runtime_policy_editor_requires_and_enters_exact_store_prompt_shop_scope():
    ui = read(BOT / "Knowledge" / "BusinessPolicyProfileUi.cs")
    handler = ui[
        ui.index('button.Click += (s, args) =>'):
        ui.index('var index = panel.Children.IndexOf(save);')
    ]
    assert 'GetField<ShopContext>(window, "_shop")' in handler
    assert "ShopScopedUiBridge.Get(window)" in handler
    assert "if (shop == null)" in handler
    assert "阻止打开全局运行策略配置" in handler
    assert "using (ShopSettingsScope.Enter(shop))" in handler
    assert "ShopScopedUiBridge.Attach(editor, shop);" in handler


def test_custom_policy_regexes_and_handoff_overrides_have_hard_size_limits():
    service = read(BOT / "ChromeNs" / "BusinessPolicyProfileService.cs")
    assert "MaxPolicyRegexLength = 1000" in service
    assert "MaxPolicyPatternCount = 100" in service
    assert "MaxHandoffOverrides = 100" in service
    assert "patterns.Properties().Count() > MaxPolicyPatternCount" in service
    assert 'property.Value.Type != JTokenType.String' in service
    assert 'ReadPolicyString(obj, "keyword", 128, true)' in service
    assert 'ReadPolicyString(obj, "fixedReply", 2000, false)' in service
    assert 'if (pattern.Length > MaxPolicyRegexLength)' in service
    assert 'TimeSpan.FromMilliseconds(100)' in service
