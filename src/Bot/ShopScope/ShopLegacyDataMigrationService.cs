using Bot.ChromeNs;
using Bot.Options;
using BotLib;
using BotLib.Db.Sqlite;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopLegacyDataMigrationBootstrap =
            ShopScope.ShopLegacyDataMigrationService.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    internal sealed class ShopMigrationResult
    {
        public bool AlreadyCompleted { get; set; }
        public int SettingCount { get; set; }
        public int FileCount { get; set; }
        public string MarkerPath { get; set; }
        public string BackupManifestPath { get; set; }
    }

    internal static class ShopLegacyDataMigrationService
    {
        private const string MarkerSchema = "qnbot.shop-legacy-migration";
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly object MigrationSync = new object();
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(ShopBindingOptionsControl),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnBindingLoaded),
                    true);
                _timer = new Timer(_ => TryAutomaticSingleShopMigration(), null, 5000, 10000);
            }
            return new object();
        }

        public static ShopMigrationResult Migrate(ShopContext shop, bool overwrite)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            lock (MigrationSync)
            {
                var markerPath = GetMarkerPath(shop);
                if (File.Exists(markerPath) && !overwrite)
                {
                    return new ShopMigrationResult
                    {
                        AlreadyCompleted = true,
                        MarkerPath = markerPath
                    };
                }

                var backupManifest = CreateMigrationBackupManifest(shop);
                var settings = ExportLegacySettings();
                var targetSettings = new ShopScopedSettingsStore(shop, Paths);
                targetSettings.MergeValues(settings, overwrite);
                var fileCount = CopyLegacyFiles(shop, overwrite);
                WriteMarker(shop, markerPath, settings.Count, fileCount, overwrite, backupManifest);
                using (ShopSettingsScope.Enter(shop))
                {
                    Log.Info("旧全局数据已迁移到本店: shop=" + shop.ShopKey
                        + ", settings=" + settings.Count + ", files=" + fileCount
                        + ", overwrite=" + overwrite + ", backupManifest=" + backupManifest);
                }
                return new ShopMigrationResult
                {
                    SettingCount = settings.Count,
                    FileCount = fileCount,
                    MarkerPath = markerPath,
                    BackupManifestPath = backupManifest
                };
            }
        }

        private static void TryAutomaticSingleShopMigration()
        {
            try
            {
                var profiles = Profiles.GetAll();
                if (profiles.Count != 1) return;
                var shop = profiles[0].ToContext();
                if (File.Exists(GetMarkerPath(shop))) return;
                if (!HasLegacyData())
                {
                    WriteMarker(shop, GetMarkerPath(shop), 0, 0, false, string.Empty);
                    return;
                }
                Migrate(shop, false);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("单店旧数据自动迁移失败，将在下次检查重试：" + Safe(ex.Message), 10);
            }
        }

        private static void OnBindingLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as ShopBindingOptionsControl;
            if (control == null) return;
            var scroll = control.Content as ScrollViewer;
            var panel = scroll == null ? null : scroll.Content as StackPanel;
            if (panel == null || panel.Children.OfType<Button>()
                .Any(x => Convert.ToString(x.Tag) == "shop-legacy-data-migration")) return;

            var window = Window.GetWindow(control);
            var shop = ShopSettingsScope.Current ?? ShopScopedUiBridge.Get(window);
            if (shop == null) return;
            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var button = new Button
            {
                Tag = "shop-legacy-data-migration",
                Content = "将旧全局数据迁移到本店",
                Width = 190,
                Height = 31,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 14, 0, 0),
                ToolTip = "多店铺环境不会自动猜测旧数据归属；点击后只迁移到当前 ShopKey。"
            };
            button.Click += (s, args) =>
            {
                if (MessageBox.Show(
                    "将旧版全局 AI/知识/规则设置和 data 业务文件迁移到当前店铺：\n"
                    + (shop.DisplayName ?? shop.ShopKey) + "\nShopKey：" + shop.ShopKey
                    + "\n\n不会迁移旧全局 Bot 令牌、云端游标、日志、缓存或其他店铺目录。是否继续？",
                    "迁移旧全局数据",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    using (ShopSettingsScope.Enter(shop))
                    {
                        var result = Migrate(shop, false);
                        status.Text = result.AlreadyCompleted
                            ? "本店已完成过旧数据迁移；未重复覆盖现有数据。"
                            : "迁移完成：设置 " + result.SettingCount + " 项，业务文件 "
                                + result.FileCount + " 个。迁移清单：" + result.BackupManifestPath;
                        status.Foreground = Brushes.SeaGreen;
                    }
                }
                catch (Exception ex)
                {
                    status.Text = "迁移失败：" + ex.Message;
                    status.Foreground = Brushes.IndianRed;
                }
            };
            panel.Children.Add(button);
            panel.Children.Add(status);

            if (File.Exists(GetMarkerPath(shop)))
            {
                status.Text = "本店已有旧数据迁移标记；按钮不会覆盖已存在的本店数据。";
                status.Foreground = Brushes.Gray;
            }
            else if (Profiles.GetAll().Count > 1)
            {
                status.Text = "检测到多个店铺：已关闭自动迁移，请确认旧数据归属后手动迁移到当前店。";
                status.Foreground = Brushes.DarkOrange;
            }
        }

        private static Dictionary<string, string> ExportLegacySettings()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var field = typeof(PersistentParams).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
            var dictionary = field == null ? null : field.GetValue(null) as IEnumerable;
            if (dictionary == null) return result;

            foreach (var raw in dictionary)
            {
                var type = raw.GetType();
                var keyProperty = type.GetProperty("Key");
                var valueProperty = type.GetProperty("Value");
                if (keyProperty == null || valueProperty == null) continue;
                var key = Convert.ToString(keyProperty.GetValue(raw, null));
                var value = Convert.ToString(valueProperty.GetValue(raw, null));
                if (string.IsNullOrWhiteSpace(key) || IsSecretOrTransient(key)) continue;

                const string aiSuffix = "#-#ai";
                const string featureSuffix = "#-#feature";
                if (key.EndsWith(aiSuffix, StringComparison.Ordinal))
                    result[key.Substring(0, key.Length - aiSuffix.Length)] = value ?? string.Empty;
                else if (key.EndsWith(featureSuffix, StringComparison.Ordinal))
                    result[key.Substring(0, key.Length - featureSuffix.Length)] = value ?? string.Empty;
                else if (string.Equals(key, "IsAutoReply", StringComparison.Ordinal))
                    result["IsAutoReply"] = value ?? string.Empty;
            }
            return result;
        }

        private static bool IsSecretOrTransient(string key)
        {
            key = key ?? string.Empty;
            return key.IndexOf("ControlPlane", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("ClientToken", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("ProcessedCommand", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("Revision", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("LastHash", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("RemotePause", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("machine-id", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("device-id", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CopyLegacyFiles(ShopContext shop, bool overwrite)
        {
            var count = 0;
            var legacyRoot = PathEx.GlobalDataDir;
            if (!Directory.Exists(legacyRoot)) return count;

            var compatibilityRoot = Paths.GetCompatibilityDataRoot(shop);
            foreach (var source in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(legacyRoot, source);
                if (ShouldSkip(relative)) continue;
                var target = Path.GetFullPath(Path.Combine(compatibilityRoot, relative));
                var root = EnsureTrailing(Path.GetFullPath(compatibilityRoot));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) && !overwrite) continue;
                var directory = Path.GetDirectoryName(target);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, target, true);
                count++;
            }

            count += CopyKnownRuleFile(
                Path.Combine(legacyRoot, "business-policy.json"),
                Path.Combine(Paths.GetRulesRoot(shop), "business-policy.json"),
                overwrite);
            count += CopyKnownRuleFile(
                Path.Combine(legacyRoot, "handoff-policy.json"),
                Path.Combine(Paths.GetRulesRoot(shop), "handoff-policy.json"),
                overwrite);
            return count;
        }

        private static int CopyKnownRuleFile(string source, string target, bool overwrite)
        {
            if (!File.Exists(source) || (File.Exists(target) && !overwrite)) return 0;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, true);
            return 1;
        }

        private static bool ShouldSkip(string relative)
        {
            relative = (relative ?? string.Empty).Replace('/', '\\').TrimStart('\\');
            var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var excluded = new[] { "log", "logs", "backup", "backups", "tmp", "temp", "cache", "caches", "restore-backups" };
            if (parts.Any(x => excluded.Contains(x, StringComparer.OrdinalIgnoreCase))) return true;
            var name = Path.GetFileName(relative);
            if (string.Equals(name, "params.db", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("params.db-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "business-policy.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "handoff-policy.json", StringComparison.OrdinalIgnoreCase)) return true;
            var extension = Path.GetExtension(relative);
            return string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bak", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".trace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".etl", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasLegacyData()
        {
            try
            {
                if (ExportLegacySettings().Count > 0) return true;
                return Directory.Exists(PathEx.GlobalDataDir)
                    && Directory.EnumerateFiles(PathEx.GlobalDataDir, "*", SearchOption.AllDirectories)
                        .Any(x => !ShouldSkip(Relative(PathEx.GlobalDataDir, x)));
            }
            catch { return false; }
        }

        private static string CreateMigrationBackupManifest(ShopContext shop)
        {
            var path = Path.Combine(Paths.GetBackupRoot(shop),
                "legacy-migration-source-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            var files = new JArray();
            if (Directory.Exists(PathEx.GlobalDataDir))
            {
                foreach (var source in Directory.EnumerateFiles(PathEx.GlobalDataDir, "*", SearchOption.AllDirectories).Take(2000))
                {
                    var relative = Relative(PathEx.GlobalDataDir, source);
                    if (ShouldSkip(relative)) continue;
                    long length = 0;
                    try { length = new FileInfo(source).Length; } catch { }
                    files.Add(new JObject { ["path"] = relative, ["bytes"] = length });
                }
            }
            var payload = new JObject
            {
                ["schema"] = "qnbot.shop-legacy-migration-source",
                ["shopKey"] = shop.ShopKey,
                ["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["legacyRoot"] = PathEx.GlobalDataDir,
                ["settingKeys"] = new JArray(ExportLegacySettings().Keys.OrderBy(x => x)),
                ["files"] = files
            };
            File.WriteAllText(path, payload.ToString(Formatting.Indented), new UTF8Encoding(false));
            return path;
        }

        private static void WriteMarker(
            ShopContext shop,
            string path,
            int settingCount,
            int fileCount,
            bool overwrite,
            string backupManifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var payload = new JObject
            {
                ["schema"] = MarkerSchema,
                ["version"] = 1,
                ["shopKey"] = shop.ShopKey,
                ["migratedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["settingCount"] = settingCount,
                ["fileCount"] = fileCount,
                ["overwrite"] = overwrite,
                ["source"] = PathEx.GlobalDataDir,
                ["backupManifest"] = backupManifest ?? string.Empty
            };
            AtomicWrite(path, payload.ToString(Formatting.Indented));
        }

        private static string GetMarkerPath(ShopContext shop)
        {
            return Path.Combine(Paths.GetStateRoot(shop), "legacy-data-migration.json");
        }

        private static string Relative(string root, string path)
        {
            root = EnsureTrailing(Path.GetFullPath(root));
            path = Path.GetFullPath(path);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/')
                : Path.GetFileName(path);
        }

        private static string EnsureTrailing(string path)
        {
            return path.EndsWith("\\") ? path : path + "\\";
        }

        private static void AtomicWrite(string path, string content)
        {
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, content, 3, 50, out error, path + ".bak"))
                throw new IOException("店铺配置原子保存失败：" + error);
        }

        private static string Safe(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }
    }
}