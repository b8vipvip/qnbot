from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_v2_cloud_sync_isolates_profile_failures_per_shop():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    queue_all = service.split("private static void QueueAll()", 1)[1].split(
        "private static void Queue(ShopContext shop)", 1
    )[0]
    assert "foreach (var profile in profiles ?? new List<ShopProfile>())" in queue_all
    assert "Queue(profile.ToContext());" in queue_all
    assert "catch (Exception ex)" in queue_all
    assert "跳过异常店铺并继续处理其他店铺" in queue_all


def test_legacy_knowledge_and_store_rule_sync_isolate_each_shop_failure():
    knowledge = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    rules = read("src/Bot/Knowledge/StoreRuleCloudSyncService.cs")
    for source, message in (
        (knowledge, "知识库云同步跳过异常店铺并继续"),
        (rules, "店铺规则云同步跳过异常店铺并继续"),
    ):
        queue_all = source.split("private static void QueueAll()", 1)[1].split(
            "private static void QueueSync(ShopContext shop)", 1
        )[0]
        assert "foreach (var profile in profiles ?? new List<ShopProfile>())" in queue_all
        assert "profile.ToContext()" in queue_all
        assert "catch (Exception ex)" in queue_all
        assert message in queue_all


def test_legacy_handoff_policy_migration_continues_after_one_bad_shop_profile():
    migration = read("src/Bot/ChromeNs/HandoffPolicyLegacyMigrationService.cs")
    loop = migration.split("private static async Task TryMigrateAllAsync()", 1)[1].split(
        "private static async Task TryMigrateAsync", 1
    )[0]
    assert "foreach (var profile in profiles.Where(x => x != null))" in loop
    assert "var shop = profile.ToContext();" in loop
    assert "await TryMigrateAsync(shop);" in loop
    assert "catch (Exception ex)" in loop
    assert "跳过异常店铺并继续" in loop
