import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')


def test_same_buyer_timeline_is_loaded_and_time_ordered():
    store = read('src/Bot/ChromeNs/ConversationContextStore.cs')
    assert 'im.singlemsg.GetRemoteHisMsg' in store
    assert 'requestedCount = 40' in store
    assert 'Math.Min(100, requestedCount)' in store
    assert 'OrderBy(GetSortValue)' in store
    assert 'GetRecentTurns' in store
    assert 'yyyy-MM-dd HH:mm:ss' in store
    assert 'seller + "#" + buyer' not in store  # null-safe Key() is required
    assert 'Key(seller, buyer)' in store


def test_short_buyer_replies_use_recent_manual_agent_context():
    ai = read('src/Bot/ChromeNs/MyOpenAI.cs')
    assert '一串数字、手机号、账号、验证码、型号或短字符' in ai
    assert 'ConversationContextStore.GetRecentTurns' in ai
    assert '严格按时间顺序理解' in ai
    assert '不得引用其他买家的内容' in ai
    assert '[当前消息 ' in ai


def test_agent_withdrawal_blocks_same_reply_from_resending():
    store = read('src/Bot/ChromeNs/ConversationContextStore.cs')
    ai = read('src/Bot/ChromeNs/MyOpenAI.cs')
    vision = read('src/Bot/ChromeNs/VisionRequestService.cs')
    assert 'MarkLastSellerTurnWithdrawn' in store
    assert 'WithdrawnAnswers' in store
    assert 'IsWithdrawnAnswer' in ai
    assert '该回复已被客服撤回，已阻止再次发送' in ai
    assert 'ConversationContextStore.IsWithdrawnAnswer' in vision


def test_product_links_use_local_presets_and_platform_tips_skip():
    safety = read('src/Bot/ChromeNs/IncomingMessageSafety.cs')
    store = read('src/Bot/ChromeNs/ConversationContextStore.cs')
    ai = read('src/Bot/ChromeNs/MyOpenAI.cs')
    assert 'IsPlatformSystemTip' in safety
    assert '淘宝/千牛自动生成的系统提示' in safety
    assert 'RegisterProductLinkReply' in safety
    assert 'ProductLinkReplies' in store
    assert 'TryTakeProductLinkReply' in ai
    preset_pos = ai.index('TryTakeProductLinkReply')
    config_pos = ai.index('EnsureConfig()', preset_pos)
    assert preset_pos < config_pos
    assert '商品链接使用本地预设回复，未调用AI接口' in ai


def test_new_context_source_is_included_for_wpf_temp_builds():
    targets = read('src/Directory.Build.targets')
    assert 'ConversationContextStore.cs' in targets
