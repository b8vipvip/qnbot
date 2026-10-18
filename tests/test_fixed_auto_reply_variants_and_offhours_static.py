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


def test_fixed_auto_replies_compile_ten_variants_with_bounded_on_demand_retries():
    text = read(DET)
    assert "internal static class FixedAutoReplyVariantService" in text
    assert "MaxAttemptsPerVersion = 3" in text
    assert "entry.Attempts >= MaxAttemptsPerVersion" in text
    assert "attempt == 1 ? 5 : 30" in text
    assert "RetryTimer" not in text
    assert "RetryPending()" not in text
    assert "variants.Length != 10" in text
    assert "array.Count != 10" in text
    assert "MyOpenAI.CallStructuredChat(" in text
    assert "仅在后续真实回复触发且达到退避时间时有限重试" in text
    assert "Guid.NewGuid().GetHashCode()" in text
    assert "FixedAutoReplyVariantService.Select(source, answer)" in text


def test_semantic_variants_preserve_dynamic_stable_tokens_and_cover_order_presets():
    text = read(DET)
    order = read(ORDER)
    assert "StableTokenRegex" in text
    assert r'\d+(?:[\d\.:/\-]*\d)?' in text
    assert 'var key = "[[T" + index++ + "]]";' in text
    assert "expected.SequenceEqual(found, StringComparer.Ordinal)" in text
    assert 'new Regex(@"https?://\\S+|\\{[^{}\\r\\n]{1,80}\\}' in text
    assert "RestoreTokens(selected, tokens)" in text
    assert 'FixedAutoReplyVariantService.Select("订单固定预设", segments[i])' not in order
    assert "Live order/payment facts must remain identical" in order


def test_changed_off_hours_config_warms_variant_compiler_immediately():
    sync = read(SYNC)
    save = sync.index("BotFeatureStore.SaveAutoReplyRules(cfg);")
    warm = sync.index('FixedAutoReplyVariantService.Warmup("下班自动回复", cfg.OffHoursFixedText);', save)
    assert save < warm


def test_sync_poll_does_not_warmup_or_retry_ai_while_idle():
    sync = read(SYNC)
    before = sync.split("private static JObject BuildCurrentSettings()", 1)[1].split(
        "private static void ApplyDesiredSettings(", 1)[0]
    assert "FixedAutoReplyVariantService.Warmup(" not in before
    assert 'string.Equals(previousOffHoursText, cfg.OffHoursFixedText, StringComparison.Ordinal)' in sync
    assert 'FixedAutoReplyVariantService.Warmup("订单固定预设"' not in sync


def test_shop_scoped_content_version_and_durable_retry_budget():
    text = read(DET)
    assert 'var shop = ShopSettingsScope.Current;' in text
    assert 'if (shop == null) return text;' in text
    assert 'shop.ShopKey + "\\n" + normalizedSource + "\\n" + text' in text
    assert 'Paths.GetCacheRoot(shop)' in text
    assert "using (ShopSettingsScope.Enter(entry.Shop))" in text
    assert "entry.Attempts++;" in text
    assert text.index("SaveSnapshot(entry);", text.index("entry.Attempts++;")) < text.index(
        "Task.Run(() =>", text.index("entry.Attempts++;")
    )
    assert "entry.Attempts = MaxAttemptsPerVersion" in text
    assert "(!allowRetry && entry.Attempts > 0)" in text
    assert "File.Replace(temp, entry.CachePath, null)" in text
    assert '"订单固定预设", StringComparison.Ordinal)) return text' in text


def test_ai_paraphrase_rejects_business_mutations():
    text = read(DET)
    assert 'canonical.Contains("不支持") && !variant.Contains("不支持")' in text
    assert 'canonical.Contains("已付款") && !variant.Contains("已付款")' in text
    assert 'variant.Contains("已上班") || variant.Contains("正在上班")' in text
    assert "array.Any(x => x.Type != JTokenType.String)" in text
