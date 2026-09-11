from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_auto_reply_rules_expose_custom_first_inquiry_reply():
    source = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    assert "OrganizeAutoReplyRulesPage" in source
    assert '"自动回复规则"' in source
    assert 'Text = "自动回复规则"' not in source
    assert '"启用首条咨询固定回复"' in source
    assert '"固定答案"' in source
    assert '"① 首条咨询固定回复"' not in source
    assert '"② 下单后及高级自动回复"' not in source
    assert "DetachLegacyScrollHost" in source
    assert "new ScrollViewer" in source
    assert "VerticalScrollBarVisibility = ScrollBarVisibility.Auto" in source
    assert "FirstInquiryFixedReplyService.Load(Seller)" in source
    assert "FirstInquiryFixedReplyService.Save(" in source
    assert "_firstInquiryFixedReplyAnswer.Text" in source


def test_first_inquiry_reply_is_shop_scoped_and_customizable():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert 'SettingsScope = "feature"' in source
    assert 'EnabledKey = "FirstInquiryFixedReplyEnabled"' in source
    assert 'AnswerKey = "FirstInquiryFixedReplyAnswer"' in source
    assert "ShopSettingsScope.Current" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "PersistentParams.TrySaveParam2Key" in source
    assert "PersistentParams.GetParam2Key" in source


def test_first_inquiry_defaults_to_enabled_and_expected_answer():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    compact = "".join(source.split())
    assert 'DefaultAnswer = "在的，亲！"' in source
    assert 'GetParam2Key(EnabledKey,SettingsScope,"true")' in compact
    assert 'GetParam2Key(AnswerKey,SettingsScope,DefaultAnswer)' in compact


def test_first_inquiry_is_once_per_30_minute_actual_problem_session():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "SessionResetMinutes = 30" in source
    assert "ConversationContextStore.GetRecentTurns(" in source
    assert "currentQuestion" in source
    assert "latestPrior.Timestamp == DateTime.MinValue" in source
    assert "latestPrior.Timestamp >= now.AddMinutes(-SessionResetMinutes)" in source
    assert "TriggeredAt" in source
    assert "PendingReplies" in source
    assert "SameBurstHistoryGraceSeconds = 8" in source
    assert "IsIgnorableFirstInquiryHistoryTurn" in source
    assert 'string.Equals(x.Role, "user", StringComparison.Ordinal)' in source
    history_guard = source[source.index("private static bool IsIgnorableFirstInquiryHistoryTurn"):source.index("private static string Compact")]
    assert 'string.Equals(turn.Role, "user", StringComparison.Ordinal)' in history_guard
    assert "return !IsActualProblemText(turn.Text);" in history_guard


def test_first_inquiry_session_is_committed_after_real_delivery():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "public static void MarkDelivered" in source
    assert "public static void ReleaseReservation" in source
    assert "InFlight" in source
    resolve = source[source.index("public static bool TryResolve"):source.index("public static void MarkDelivered")]
    assert "TriggeredAt[key] = now" not in resolve
    delivered = source[source.index("public static void MarkDelivered"):source.index("public static void ReleaseReservation")]
    assert "TriggeredAt[key] = DateTime.Now" in delivered


def test_first_inquiry_requires_substantive_actual_problem_and_keeps_buxing_meaningful():
    service = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")

    classifier = service[service.index("internal static bool IsActualProblemMessage"):service.index("private static bool ShouldSuppressForOffHours")]
    assert "ConversationContextStore.IsPlatformSystemTip(message, messageText)" in classifier
    assert "ConversationContextStore.IsProductLink(message, messageText)" in classifier
    assert "ConversationContextStore.IsWithdrawalNotice(message, messageText)" in classifier
    assert "GreetingOnlyTexts.Contains(semantic)" in classifier
    assert "MeaninglessOnlyTexts.Contains(semantic)" in classifier
    assert "return IsActualProblemText(rawText);" in classifier

    greeting_block = service[service.index("GreetingOnlyTexts"):service.index("MeaninglessOnlyTexts")]
    noise_block = service[service.index("MeaninglessOnlyTexts"):service.index("private sealed class PendingReply")]
    for text in ["你好", "您好", "在吗", "有人吗", "hi", "hello"]:
        assert f'"{text}"' in greeting_block
    for text in ["嗯", "哦", "好的", "谢谢", "111", "666"]:
        assert f'"{text}"' in noise_block
    # “不行” is a short but substantive failure report and must not be classified as greeting/noise.
    assert '"不行"' not in greeting_block
    assert '"不行"' not in noise_block


def test_fresh_actual_problem_can_prepare_fixed_reply_with_full_message_metadata():
    service = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert "public static bool TryPrepare(" in service
    assert 'decision.MessageLabel, "历史消息"' in service
    assert "FirstInquiryFixedReplyService.TryPrepare(" in router
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    ordinary_text = router.index("if (safetyDecision.ShouldCallAi)", prepare)
    image_route = router.index('if (!string.Equals(safetyDecision.MessageLabel, "[图片]"', ordinary_text)
    assert prepare < ordinary_text < image_route
    prepare_call = router[prepare:router.index("out fixedAnswer);", prepare)]
    assert "message," in prepare_call
    assert "IncomingMessageSafety.GetDisplayText(message, text)" in router
    assert "Kind = VisionDecisionKind.Text" in router[prepare:ordinary_text]
    assert "随后同一实际问题继续进入正常文本回复处理" in router
    assert "首条咨询固定回复已预留" in service
    assert "actualProblem=true" in service


def test_platform_system_and_product_messages_cannot_late_create_first_inquiry_reservation():
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    service = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert 'Skip("[淘宝系统提示]"' in safety
    assert 'decision.MessageLabel, "[淘宝系统提示]"' in service
    assert 'decision.MessageLabel, "[撤回提示]"' in service
    assert 'decision.MessageLabel, "[空白或未知消息]"' in service
    assert "ConversationContextStore.IsProductLink(message, messageText)" in service
    prepare = router.index("FirstInquiryFixedReplyService.TryPrepare(")
    normal_skip = router.index("return Skip(safetyDecision.MessageLabel, safetyDecision.Note);", prepare)
    assert prepare < normal_skip

    resolve = service[service.index("public static bool TryResolve"):service.index("public static void MarkDelivered")]
    assert "PendingReplies.TryGetValue" in resolve
    assert "ResolveFreshCurrentScope" not in resolve
    assert "full incoming message" in resolve


def test_first_reply_is_premerge_prelude_and_same_problem_continues_normal_reply_pipeline():
    deterministic = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    router = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    resolve = deterministic.index("FirstInquiryFixedReplyService.TryResolve(")
    invoke_send = deterministic.index("var firstOk = await SendFixedAsync(", resolve)
    mark = deterministic.index("FirstInquiryFixedReplyService.MarkDelivered(", invoke_send)
    local_short = deterministic.index("if (allowLocalShortReply)", mark)
    normal_continue = deterministic.index("return true;", local_short)
    assert resolve < invoke_send < mark < local_short < normal_continue
    assert "随后同一实际问题继续进入正常文本回复处理" in router
    assert "StreamingBuyerAnswerService.GetAnswerAsync(" in streaming


def test_fixed_reply_does_not_enter_ai_learning_path():
    source = read("src/Bot/ChromeNs/QN.cs")
    assert 'if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))' in source
    assert 'KnowledgeLearningService.RegisterAnswerSource(' in source
    assert '"首条咨询固定回复"' in source


def test_qnrpa_no_longer_requires_global_desk_and_send_path_is_nonblocking_main_area_click():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    reliable = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    ctor = source[source.index("public QNRpa(QN qn)"):source.index("private bool IsSendButtonName")]
    assert "Application.Attach(Desk.Inst.ProcessId)" not in ctor
    assert "EnsureSellerDeskBinding(false)" in ctor

    assert ".GetAwaiter().GetResult()" not in source
    assert "ProbeInputboxEmptyAsync" in source
    assert "Task.WhenAny" in source
    assert "ConfigureAwait(false)" in source
    assert "已放弃等待且不会阻塞UI线程" in source

    cdp = source[source.index("private async Task<bool> TrySetPlainTextByCdpAsync"):source.index("private async Task<bool> OpenAndSendText")]
    assert "RunCdpActionAsync" in cdp
    assert "CDP写入由UIA严格确认" in cdp
    assert "进入UIA定位发送主按钮动作" in cdp
    assert "检测到本次Bot草稿仍在输入框，重试直接复用且不再次追加" in cdp
    nonempty = cdp.index("if (!before.IsEmpty)")
    insert = cdp.index("准备通过CDP写入输入框")
    assert nonempty < insert

    assert "TryPressEnterTextSendAsync" not in source
    assert "TryFocusEditorForEnterFast" not in source
    assert "PressEnter()" not in source
    assert "keybd_event(0x0D" not in source
    assert "Enter主发送开始" not in source

    button = source[source.index("private async Task<bool> TrySendTextViaUiaAsync"):source.index("public async Task SendImageAsync")]
    assert "TryInvokeCachedSendButtonNow" in source
    assert "_sendMessageButton.AsButton().Invoke()" not in source
    assert "uia3Automation.FromPoint(new System.Drawing.Point(x, y))" in source
    assert "IsSafeMainSendInvokeCandidate" in source
    assert "禁止Invoke整块分裂按钮" in source
    assert "TryClickCachedSendButtonNow" in button
    assert "_sendMessageButtonRect" in source
    assert "arrowGuard" in source
    assert "发送主按钮左侧区域坐标点击" in source
    assert "sellerDesk.BringTop()" in source
    assert "发送主按钮坐标" in button
    assert "_lastSendButtonCoordinateClickRejected" in source
    assert "发送主按钮坐标输入被系统拒绝，准备定位左侧主发送UIA子控件" in button
    assert "if (!_lastSendButtonCoordinateClickRejected)" in button
    assert "HasExpectedDraftFastAsync(text, 900)" in button
    assert "坐标点击异常后目标草稿已不存在或无法确认，禁止UIA二次动作" in button
    assert button.index("TryClickCachedSendButtonNow") < button.index("TryInvokeCachedSendButtonNow")
    click_method = source[source.index("private bool TryClickCachedSendButtonNow"):source.index("private bool TryInvokeCachedSendButtonNow")]
    assert "_lastSendButtonCoordinateClickRejected = false" in click_method
    assert "_lastSendButtonCoordinateClickRejected = true" in click_method
    assert "sendRect=" in reliable

    open_send = source[source.index("private async Task<bool> OpenAndSendText"):]
    assert "HasExpectedDraftFastAsync" in open_send
    assert "TrySendTextNativeFirstAsync" in open_send
    assert "TrySendTextViaUiaAsync(buyer, text, sendStart)" in native
    assert "SetPlainText(text)" not in open_send
    assert "method=CDP页面按钮+HWND安全消息+UIA安全回退" in open_send


def test_order_system_event_uses_order_pipeline_not_a_separate_first_inquiry_bridge():
    direct = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    targets = read("src/Directory.Build.targets")
    assert "ProcessDirectOrderMessageAsync" in direct
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in direct
    assert "OrderTemplateRequiredFieldsV2.TryOwnExistingPlan" in direct
    assert "ProcessOrderPlacedReplyAsync(plan)" in direct
    assert "OrderEventHub.Publish" in v2
    assert "ProcessOrderTemplateRequiredFieldsPlanAsync" in v2
    assert "FirstInquiryDeliveryBridge.cs" not in targets


def test_delayed_exact_seller_echo_cancels_retry_and_false_failure():
    source = read("src/Bot/ChromeNs/QN.cs")
    retry = source[source.index("public async Task<bool> SendTextWithRetryAsync"):source.index("private async Task<bool> EnsureActiveBuyerForSendAsync")]
    assert "WaitForSellerEchoGraceAsync" in retry
    assert "HasRecentSellerEcho(buyer, text, sendStartedAt)" in retry
    assert "按真实送达处理并取消重试" in retry
    assert "按真实送达处理并取消重复发送" in retry
    assert "最终失败判定前收到延迟卖家回显，改判真实送达" in retry
    assert retry.index("HasRecentSellerEcho(buyer, text, sendStartedAt)") < retry.index("ok = await SendTextAsync(buyer, text);", retry.index("for (var i = 0"))
