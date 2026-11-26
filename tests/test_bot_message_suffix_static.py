# Regression coverage for the per-shop configurable Bot message suffix.
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_message_strategy_removes_redundant_inner_heading_and_adds_suffix_field():
    source = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    assert 'result.Add(MakeSectionTitle("消息策略"));' not in source
    assert 'private TextBox _botMessageSuffix;' in source
    assert '"Bot消息后缀"' in source
    assert 'BotMessageSuffixService.GetSuffix(Seller)' in source
    assert 'BotMessageSuffixService.Save(effectiveSeller, _botMessageSuffix.Text ?? string.Empty)' in source


def test_suffix_setting_is_shop_scoped_and_defaults_to_ai_marker():
    source = read("src/Bot/ChromeNs/ReplyModeService.cs")
    assert 'SettingsKey = "message.bot_message_suffix"' in source
    assert 'DefaultSuffix = "[AI]"' in source
    assert 'MaxSuffixLength = 32' in source
    assert 'store.SetString(SettingsKey, normalized)' in source
    assert 'if (store == null || !store.TryGetString(SettingsKey, out value)) return DefaultSuffix;' in source


def test_final_send_path_applies_suffix_before_echo_tracking_and_retry():
    source = read("src/Bot/ChromeNs/QN.cs")
    start = source.index("public async Task<bool> SendTextWithRetryAsync")
    end = source.index("private bool IsPreSendGuardSatisfied", start)
    block = source[start:end]
    apply_pos = block.index("BotMessageSuffixService.Apply(")
    gate_pos = block.index("await _sendGate.WaitAsync(cancellationToken)")
    send_pos = block.index("var ok = await SendTextAsync(buyer, text, preSendGuard)")
    assert apply_pos < gate_pos < send_pos
    assert 'HasRecentSellerEcho(buyer, text, sendStartedAt)' in block


def test_formatter_and_guard_understand_configured_suffix():
    formatter = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    guard = read("src/Bot/ChromeNs/BuyerReplyOutputGuard.cs")
    assert 'var suffix = BotMessageSuffixService.GetCurrentSuffix();' in formatter
    assert 'var configuredSuffix = BotMessageSuffixService.GetCurrentSuffix();' in formatter
    assert 'var configuredSuffix = BotMessageSuffixService.GetCurrentSuffix();' in guard
