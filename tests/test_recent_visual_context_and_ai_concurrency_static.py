from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_recent_visual_semantics_are_injected_into_later_text_ai_context():
    helper = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    openai = text("src/Bot/ChromeNs/MyOpenAI.cs")
    targets = text("src/Directory.Build.targets")

    assert "BuildPromptAddon(string seller, string buyer, string currentQuestion)" in helper
    assert "VisualKnowledgeObservationEntity" in helper
    assert "最近图片理解缓存" in helper
    assert "不代表订单、付款、账号、充值等实时状态" in helper
    assert "如果图片已撤回，只能在买家明确引用该历史图片时作为对话上下文" in helper
    assert "已撤回，仅限买家明确引用时使用" in helper
    assert "RecentVisualContextService.BuildPromptAddon(seller, buyer, question)" in openai
    assert "RecentVisualContextService.cs" in targets


def test_post_order_photo_preset_uses_shop_configured_application_identity():
    helper = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    guard = text("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")

    assert "TrySatisfyOrderPhotoRequirement" in helper
    assert "LooksLikeConfiguredPhotoGuidance" in helper
    assert "PhotoInstructionAllCues" in helper
    assert "PhotoInstructionAnyCues" in helper
    assert "config == null || !config.Enabled" in helper
    assert "TryResolveConfiguredApplication" in helper
    assert "HasConfiguredOfficialAppEvidence" in helper
    assert "ExplicitOfficialAppRegex" in helper
    assert "电视自带" in helper
    assert "系统内置" in helper
    assert "TryFindHumanConfiguredAppConfirmation" in helper
    assert "人工客服已确认：" in helper
    assert "IsBotMarked" in helper
    assert "The newest observation for a configured application is authoritative" in helper
    assert "if (!HasConfiguredOfficialAppEvidence(" in helper
    assert "酷狗" not in helper and "Kugou" not in helper

    visual_check = guard.index("RecentVisualContextService.TrySatisfyOrderPhotoRequirement")
    old_message_check = guard.index("FindEquivalentSellerReply", visual_check)
    assert visual_check < old_message_check
    assert "下单前图片已满足确认要求" in guard
    assert "目标应用" in guard
    assert "applicationName" in guard
    assert "if (!plan.IsBuyerFollowUp)" in guard


def test_recent_account_evidence_is_derived_from_shop_configured_app_and_generic_field_labels():
    visual = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    rules = text("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    assert "TryGetRecentAccountEvidence" in visual
    assert "AccountVisualEvidence" in visual
    assert "ApplicationName = applicationName" in visual
    assert "AccountNicknameRegex" in visual
    assert "AccountIdRegex" in visual
    assert "TryResolveConfiguredApplication" in rules
    assert ".Where(x => x.Length >= 2 && !IsApplicationNamePlaceholder(x))" in rules
    assert 'name.Contains("{") || name.Contains("}")' in rules
    assert "酷狗" not in visual and "Kugou" not in visual


def test_vision_prompt_is_domain_neutral_and_uses_configured_image_rules():
    vision = text("src/Bot/ChromeNs/VisionRequestService.cs")
    assert "所有行业与店铺类型" in vision
    assert "不得预设图片一定属于设备、电视、账号、会员、充值" in vision
    assert "图片知识、OCR命中规则和视觉理解规则" in vision
    assert "规则是检索证据，不是自动结论" in vision
    assert "只记录实际出现的内容" in vision
    assert "品牌官方APP/官方电视版" not in vision
    assert "酷狗" not in vision


def test_visual_analysis_continues_after_reply_lease_becomes_stale_and_persists_semantics():
    qn = text("src/Bot/ChromeNs/QN.cs")
    vision = text("src/Bot/ChromeNs/VisionRequestService.cs")

    vision_method = qn.index("private async Task ProcessVisionBurstAsync")
    execute = qn.index("_visionRequestService.ExecuteAsync(task, CancellationToken.None)", vision_method)
    stale_check = qn.index("if (!lease.IsCurrent)", execute)
    assert execute < stale_check

    # The caller deliberately does not pass the burst lease as a cancellation token, so a
    # newer buyer task or manual intervention can suppress sending without killing analysis.
    assert "CancellationToken.None" in qn[vision_method:stale_check]

    record = vision.index("VisualKnowledgeLearningService.RecordVisionAnalysis")
    successful_return = vision.index("return result;", record)
    assert record < successful_return
    assert "result.VisualSummary" in vision[record - 500:successful_return]


def test_multiple_buyers_get_independent_ai_workers_while_real_sends_remain_serialized():
    burst = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")
    openai = text("src/Bot/ChromeNs/MyOpenAI.cs")
    server_router = text("services/api-control-plane/runtime_routing_guard.py")

    assert "ConcurrentDictionary<string, BurstState> _states" in burst
    assert "var key = Key(item.ShopKey, item.SellerNick, item.BuyerNick);" in burst
    assert 'return shopKey + "#" + (seller ?? string.Empty).Trim().ToLowerInvariant()' in burst
    assert "Key(item.SellerNick, item.BuyerNick)" not in burst
    assert "if (startWorker) Task.Run(() => RunAsync(key, state));" in burst

    text_method = qn.index("private async Task ProcessTextBurstAsync")
    vision_method = qn.index("private async Task ProcessVisionBurstAsync")
    assert "Task.Run(() => MyOpenAI.GetAnswer" in qn[text_method:vision_method]

    # Text calls use a shared HttpClient but there is no process-wide AI semaphore/lock.
    assert "private static readonly HttpClient SharedHttp" in openai
    assert "SemaphoreSlim" not in openai

    # The server router also performs each request independently rather than using a global mutex.
    assert "threading.Lock" not in server_router
    assert "asyncio.Semaphore" not in server_router

    # Only the actual Qianniu UI send is serialized, which is required to prevent cross-buyer sends.
    assert "private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);" in qn
    send_method = qn.index("public async Task<bool> SendTextWithRetryAsync")
    assert "await _sendGate.WaitAsync(cancellationToken);" in qn[send_method:send_method + 2500]


def test_withdrawn_visual_context_is_injected_only_for_explicit_historical_image_references():
    helper = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    method = helper[
        helper.index("private static bool ExplicitlyReferencesWithdrawnImage"):
        helper.index("public static string BuildPromptAddon")
    ]
    prompt = helper[
        helper.index("public static string BuildPromptAddon(string shopKey"):
        helper.index("internal static List<VisualKnowledgeObservationEntity> GetRecentWorkflowEvidence")
    ]

    assert "VisionFollowUpContextPipeline.IsVisionReferentialFollowUp(question)" in method
    assert "刚才|刚刚|之前|上次|前面|上一张|那张|这张|这个" in method
    assert r'Regex.Replace(question ?? string.Empty, @"\s+"' in method
    assert "allowWithdrawnContext || !IsWithdrawnObservation(" in prompt
    assert "已撤回，仅限买家明确引用时使用" in prompt


def test_oversized_bursts_cannot_be_intercepted_by_knowledge_or_vision_runtime_wrappers():
    burst = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    memory = text("src/Bot/ChromeNs/KnowledgeMemoryEngine.cs")
    v2 = text("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    streaming = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    vision = text("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    property_block = burst[
        burst.index("public bool HasReplyableItem"):
        burst.index("public BuyerMessageBurst(", burst.index("public bool HasReplyableItem"))
    ]
    assert "return !InputOverflow && Items.Any(" in property_block

    memory_guard = memory.split("var burst = lease == null ? null : lease.Burst;", 1)[1].split(
        "KnowledgeMemoryDecision decision;", 1
    )[0]
    assert "!burst.HasReplyableItem" in memory_guard

    v2_guard = v2.split("var burst = lease == null ? null : lease.Burst;", 1)[1].split(
        "var shopScope", 1
    )[0]
    assert "!burst.HasReplyableItem" in v2_guard

    streaming_guard = streaming.split("var burst = lease == null ? null : lease.Burst;", 1)[1].split(
        "await ProcessTextBurstStreamingAsync", 1
    )[0]
    assert "burst.InputOverflow" in streaming_guard

    vision_guard = vision.split("private static async Task HandleAsync", 1)[1].split(
        "private static async Task ProcessVisionAsync", 1
    )[0]
    assert "burst.InputOverflow" in vision_guard
    assert vision_guard.index("burst.InputOverflow") < vision_guard.index("var visionItem = burst.LatestVisionItem")
    assert "diagnostic-only truncated question" in vision_guard
