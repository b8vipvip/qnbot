from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DET = ROOT / "src" / "Bot" / "ChromeNs" / "DeterministicAutoReplyService.cs"
ORDER = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"
SYNC = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebAutoReplyRulesSyncService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_off_hours_repeat_window_defaults_to_five_minutes():
    text = read(DET)
    assert "private const int OffHoursRepeatMinutes = 5;" in text
    assert "DateTime.Now.AddMinutes(OffHoursRepeatMinutes)" in text
    assert "距离下一次下班提示不足5分钟" in text
    assert "OffHoursRepeatMinutes = 2" not in text


def test_fixed_auto_replies_compile_ten_variants_and_retry_ai_every_five_minutes():
    text = read(DET)
    assert "internal static class FixedAutoReplyVariantService" in text
    assert "TimeSpan.FromMinutes(5)" in text
    assert "variants.Length != 10" in text
    assert "list.Count != 10" in text
    assert "MyOpenAI.CallStructuredChat(" in text
    assert "固定自动回复同义编译失败，5分钟后重试" in text
    assert "Guid.NewGuid().GetHashCode()" in text
    assert "FixedAutoReplyVariantService.Select(source, answer)" in text


def test_semantic_variants_preserve_dynamic_stable_tokens_and_cover_order_presets():
    text = read(DET)
    order = read(ORDER)
    assert "StableTokenRegex" in text
    assert 'var key = "[[T" + index++ + "]]";' in text
    assert "AI同义编译丢失稳定占位符" in text
    assert "RestoreTokens(selected, tokens)" in text
    assert 'FixedAutoReplyVariantService.Select("订单固定预设", segments[i])' in order


def test_changed_off_hours_config_warms_variant_compiler_immediately():
    sync = read(SYNC)
    save = sync.index("BotFeatureStore.SaveAutoReplyRules(cfg);")
    warm = sync.index('FixedAutoReplyVariantService.Warmup("下班自动回复", cfg.OffHoursFixedText);', save)
    assert save < warm
