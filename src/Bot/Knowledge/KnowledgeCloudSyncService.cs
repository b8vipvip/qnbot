using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    /// <summary>
    /// Per-ShopKey cloud-sync preference shared by Knowledge V2 and store-rule sync.
    /// The old KnowledgeBaseJson cloud protocol has been removed; all knowledge
    /// synchronization is performed by KnowledgeV2CloudSyncService.
    /// </summary>
    internal static class KnowledgeCloudSyncService
    {
        private const string Scope = "shop-cloud";
        private const string EnabledKey = "KnowledgeCloudSyncEnabled";

        internal static bool IsEnabled
        {
            get
            {
                var shop = ShopSettingsScope.Current;
                return shop != null && IsEnabledForShop(shop);
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null)
            {
                Log.Error("无法修改知识库云同步：当前没有店铺作用域。");
                return;
            }
            SetEnabledForShop(shop, enabled, true);
        }

        internal static bool IsEnabledForShop(ShopContext shop)
        {
            if (shop == null) return false;
            using (ShopSettingsScope.Enter(shop))
            {
                return string.Equals(
                    PersistentParams.GetParam2Key(EnabledKey, Scope, "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void SetEnabledForShop(ShopContext shop, bool enabled, bool queueSync)
        {
            if (shop == null) return;
            using (ShopSettingsScope.Enter(shop))
            {
                PersistentParams.TrySaveParam2Key(EnabledKey, Scope, enabled ? "true" : "false");
            }
            if (enabled && queueSync)
                KnowledgeV2CloudSyncService.QueueSync(shop);
        }

        internal static Task SyncNowAsync(ShopContext shop)
        {
            return KnowledgeV2CloudSyncService.SyncNowAsync(shop);
        }
    }
}
