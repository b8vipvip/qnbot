using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private static readonly object RulePolicyImportExportBootstrap =
            Knowledge.RulePolicyImportExportUi.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class RulePolicyImportExportUi
    {
        private const string StoreSchema = "qnbot.store-rules";
        private const int ExportVersion = 2;

        private static readonly ConditionalWeakTable<Window, object> Attached =
            new ConditionalWeakTable<Window, object>();
        private static readonly object AttachedMarker = new object();
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(StorePromptProfileWindow),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnStoreWindowLoaded),
                    true);
            }
            return new object();
        }

        private static void OnStoreWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as StorePromptProfileWindow;
            if (window == null || IsAttached(window)) return;

            var save = GetField<Button>(window, "_save");
            var panel = save == null ? null : save.Parent as Panel;
            if (panel == null)
            {
                Log.Info("店铺规则导入导出按钮挂载失败：未找到保存按钮容器");
                return;
            }

            var import = CreateButton(
                "导入",
                72,
                "从JSON导入店铺原始资料、核心规则和场景规则卡；导入前自动备份当前配置。",
                "store-rules-import");
            import.Click += (s, args) => ImportStoreRules(window);

            var export = CreateButton(
                "导出",
                72,
                "把当前编辑器中的店铺规则导出为带版本信息的JSON文件。",
                "store-rules-export");
            export.Click += (s, args) => ExportStoreRules(window);

            var index = panel.Children.IndexOf(save);
            if (index < 0) index = 0;
            panel.Children.Insert(index, import);
            panel.Children.Insert(index + 1, export);
            MarkAttached(window);
        }

        private static Button CreateButton(string text, double width, string tooltip, string tag)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = tooltip,
                Tag = tag
            };
        }

        private static bool IsAttached(Window window)
        {
            object ignored;
            return window != null && Attached.TryGetValue(window, out ignored);
        }

        private static void MarkAttached(Window window)
        {
            if (window == null || IsAttached(window)) return;
            try { Attached.Add(window, AttachedMarker); } catch { }
        }

        private static void ExportStoreRules(StorePromptProfileWindow window)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出店铺规则",
                    Filter = "JSON文件 (*.json)|*.json",
                    FileName = "qianniu-store-rules-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(window) != true) return;

                var payload = BuildStoreExportObject(window);
                WriteJson(dialog.FileName, payload);
                SetStoreStatus(window, "已导出：" + Path.GetFileName(dialog.FileName), Brushes.SeaGreen);
                Log.Info("店铺规则已导出: file=" + Path.GetFileName(dialog.FileName)
                    + ", rules=" + ((JArray)payload["profile"]["rules"]).Count);
                MessageBox.Show("店铺规则已导出。", "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ImportStoreRules(StorePromptProfileWindow window)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入店铺规则",
                    Filter = "JSON文件 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(window) != true) return;

                var root = ReadObject(dialog.FileName);
                ValidateSchema(root, StoreSchema);
                var profile = root["profile"] as JObject ?? root;
                var raw = ReadString(profile, "rawInput", "RawInput");
                var core = ReadString(
                    profile,
                    "corePrompt",
                    "CorePrompt",
                    "standardPrompt",
                    "StandardPrompt");
                var rulesToken = profile["rules"] ?? profile["Rules"];
                if (rulesToken == null || rulesToken.Type != JTokenType.Array)
                    throw new Exception("文件中没有有效的 rules 场景规则数组。");

                var rules = StorePromptProfileService.ParseRulesJson(
                    rulesToken.ToString(Formatting.None));
                var confirm = MessageBox.Show(
                    "将导入核心规则和 " + rules.Count + " 条场景规则，并覆盖当前店铺规则配置。"
                    + "\n导入前会自动备份当前配置。是否继续？",
                    "确认导入店铺规则",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var backup = BackupStoreRules(window);
                StorePromptProfileService.SaveStructured(raw, core, rules);
                Invoke(window, "LoadProfile");
                SetStoreStatus(window, "导入成功 · 已自动备份原配置", Brushes.SeaGreen);
                Log.Info("店铺规则已导入: file=" + Path.GetFileName(dialog.FileName)
                    + ", rules=" + rules.Count + ", backup=" + Path.GetFileName(backup));
                MessageBox.Show(
                    "导入成功：" + rules.Count + " 条场景规则。\n原配置备份：" + backup,
                    "店铺规则中心",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (JsonException ex)
            {
                MessageBox.Show("导入文件不是有效JSON：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static JObject BuildStoreExportObject(StorePromptProfileWindow window)
        {
            var raw = GetField<TextBox>(window, "_raw");
            var core = GetField<TextBox>(window, "_core");
            var rulesEditor = GetField<TextBox>(window, "_rules");
            if (raw == null || core == null || rulesEditor == null)
                throw new Exception("无法读取店铺规则编辑器，请重新打开窗口后重试。");

            var rules = StorePromptProfileService.ParseRulesJson(rulesEditor.Text);
            return new JObject
            {
                ["schema"] = StoreSchema,
                ["version"] = ExportVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["profile"] = new JObject
                {
                    ["schemaVersion"] = 2,
                    ["rawInput"] = raw.Text ?? string.Empty,
                    ["corePrompt"] = core.Text ?? string.Empty,
                    ["rules"] = JArray.FromObject(rules)
                }
            };
        }

        private static string BackupStoreRules(StorePromptProfileWindow window)
        {
            var path = BuildBackupPath("store-rules-before-import");
            WriteJson(path, BuildStoreExportObject(window));
            return path;
        }

        private static void SetStoreStatus(StorePromptProfileWindow window, string text, Brush brush)
        {
            var status = GetField<TextBlock>(window, "_status");
            if (status == null) return;
            status.Text = text;
            status.Foreground = brush;
        }

        private static string BuildBackupPath(string prefix)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory,
                prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
        }

        private static JObject ReadObject(string path)
        {
            return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        private static void WriteJson(string path, JObject value)
        {
            File.WriteAllText(
                path,
                (value ?? new JObject()).ToString(Formatting.Indented),
                new UTF8Encoding(false));
        }

        private static bool SchemaMatches(string actual, string expected)
        {
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(expected)
                || !expected.StartsWith("qnbot.", StringComparison.OrdinalIgnoreCase)) return false;
            var legacyExpected = ("qianniu" + "-ai-bot") + expected.Substring("qnbot".Length);
            return string.Equals(actual, legacyExpected, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateSchema(JObject root, string expected)
        {
            if (root == null) throw new Exception("导入文件为空。");
            var schema = Convert.ToString(root["schema"]);
            if (!string.IsNullOrWhiteSpace(schema)
                && !SchemaMatches(schema, expected))
            {
                throw new Exception("文件类型不匹配：" + schema);
            }

            int version;
            if (root["version"] != null
                && (!int.TryParse(Convert.ToString(root["version"]), out version)
                    || version < 1
                    || version > ExportVersion))
            {
                throw new Exception("不支持的导入文件版本：" + Convert.ToString(root["version"]));
            }
        }

        private static string ReadString(JObject value, params string[] names)
        {
            if (value == null) return string.Empty;
            foreach (var name in names ?? new string[0])
            {
                var token = value[name];
                if (token != null && token.Type != JTokenType.Null)
                    return Convert.ToString(token).Trim();
            }
            return string.Empty;
        }

        private static double ReadDouble(JObject value, double fallback, params string[] names)
        {
            double parsed;
            return double.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;
        }

        private static int ReadInt(JObject value, int fallback, params string[] names)
        {
            int parsed;
            return int.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;
        }

        private static T GetField<T>(object target, string name) where T : class
        {
            if (target == null) return null;
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void SetField(object target, string name, object value)
        {
            if (target == null) return;
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) throw new Exception("无法刷新窗口字段：" + name);
            field.SetValue(target, value);
        }

        private static object Invoke(object target, string name)
        {
            if (target == null) return null;
            var method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new Exception("无法刷新窗口方法：" + name);
            return method.Invoke(target, null);
        }

        private static Button FindButton(DependencyObject root, string content)
        {
            if (root == null) return null;
            var button = root as Button;
            if (button != null
                && string.Equals(Convert.ToString(button.Content), content, StringComparison.Ordinal))
                return button;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindButton(VisualTreeHelper.GetChild(root, i), content);
                if (found != null) return found;
            }
            if (root is ContentControl)
            {
                var found = FindButton(((ContentControl)root).Content as DependencyObject, content);
                if (found != null) return found;
            }
            return null;
        }
    }
}
