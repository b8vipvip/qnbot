from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_adaptive_timing_learns_only_realistic_intervals_and_is_bounded():
    service = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "interval < 120 || interval > 4500" in service
    assert "RecentIntervalsMs.Count > 24" in service
    assert "p75 + 180" in service
    assert "Clamp(p75 + 180, 350, 1600)" in service
    assert "profile.RecentIntervalsMs.Count < 3" in service
    assert "AdaptiveDelayKind.Fragment" in service
    assert "AdaptiveDelayKind.Complete" in coordinator
    assert "Clamp(adjusted, 300, 950)" in service
    assert "Clamp(adjusted, 650, 1550)" in service


def test_burst_coordinator_records_interval_after_duplicate_filter():
    code = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    duplicate = code.index("state.Items.Any(x => string.Equals(x.MessageKey")
    previous = code.index("var previousReceivedAt", duplicate)
    record = code.index("AdaptiveReplyTimingService.RecordInterval", previous)
    add = code.index("state.Items.Add(item)", record)
    assert duplicate < previous < record < add


def test_existing_base_rules_and_four_second_cap_remain():
    code = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "TimeSpan.FromSeconds(4)" in code
    assert "return 80;" in code
    assert "baseline = 700" in code
    assert "baseline = 950" in code
    assert "baseline = 1200" in code
    assert "baseline = 800" in code
    assert "baseline = 350" in code
    assert "AdaptiveReplyTimingService.AdjustDelay" in code


def test_timing_profile_is_persistent_private_and_flushed_on_shutdown():
    service = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")
    assert "adaptive-reply-timing.json" in service
    assert "Environment.SpecialFolder.LocalApplicationData" in service
    assert "TimeSpan.FromSeconds(30)" in service
    assert "Take(2000)" in service
    assert "AddDays(-30)" in service
    assert "AdaptiveReplyTimingService.Flush()" in app
    assert "AdaptiveReplyTimingService.cs" in targets


def test_adaptive_timing_profiles_are_isolated_by_shopkey():
    service = read("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")

    assert "RecordInterval(\n            string shopKey," in service
    assert "AdjustDelay(\n            string shopKey," in service
    assert "GetSnapshot(string shopKey, string seller, string buyer)" in service
    assert "BuildKey(string shopKey, string seller, string buyer)" in service
    assert 'return (shopKey ?? string.Empty).Trim()' in service
    assert "Legacy unscoped profiles are intentionally not reused" in service
    assert "RecordInterval(\n                    item.ShopKey, item.SellerNick, item.BuyerNick" in coordinator
    assert "AdjustDelay(\n                latestItem.ShopKey, latestItem.SellerNick, latestItem.BuyerNick" in coordinator
