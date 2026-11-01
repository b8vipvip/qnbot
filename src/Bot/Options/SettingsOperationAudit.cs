using BotLib;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Bot.Options
{
    /// <summary>
    /// Central audit of user-triggered settings operations. No textbox values,
    /// passwords, API keys, URLs, buyer nicknames or other free-text config
    /// content are ever logged. The normal runtime log retains the results.
    /// </summary>
    internal static class SettingsOperationAudit
    {
        internal static void Attach(Window window, Func<string> page, string surface)
        {
            if (window == null) return;
            var safeSurface = SafeToken(surface, "settings");
            window.Loaded += delegate
            {
                Write(safeSurface, page, "open", "window");
            };
            window.Closed += delegate
            {
                Write(safeSurface, page, "close", "window");
            };
            window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, e) =>
            {
                var button = e.OriginalSource as ButtonBase;
                if (button == null) return;
                string action = SafeControlName(button.Name);
                if (action == "unnamed")
                {
                    var text = button.Content as string;
                    action = SafeButtonAction(text);
                }
                var toggle = button as ToggleButton;
                Write(safeSurface, page, toggle == null ? "click" : "toggle",
                    action + (toggle == null ? "" : ";checked=" + toggle.IsChecked));
            }), true);
            window.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((sender, e) =>
            {
                var selection = e.OriginalSource as Selector;
                // Ignore initial programmatic binding and background refreshes.
                if (selection == null || !selection.IsKeyboardFocusWithin) return;
                Write(safeSurface, page, "selection_changed",
                    SafeControlName(selection.Name) + ";type=" + selection.GetType().Name);
            }), true);
            window.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((sender, e) =>
            {
                var element = e.OriginalSource as FrameworkElement;
                if (element == null || !(element is TextBoxBase)
                    && !(element is ComboBox)) return;
                // Fields are recorded only by stable control name/type, never value.
                Write(safeSurface, page, "field_focus_left",
                    SafeControlName(element.Name) + ";type=" + element.GetType().Name);
            }), true);
        }

        internal static void SaveResult(string surface, string page, string action,
            bool success, Exception error = null)
        {
            Write(SafeToken(surface, "settings"), () => page,
                success ? "save_success" : "save_failed",
                SafeToken(action, "save")
                    + (error == null ? "" : ";errorType=" + error.GetType().Name));
        }

        private static void Write(string surface, Func<string> getPage, string operation,
            string control)
        {
            try
            {
                var page = getPage == null ? "unknown" : SafePage(getPage());
                Log.Info("[SettingsOperation] surface=" + surface
                    + ";page=" + page
                    + ";event=" + operation
                    + ";control=" + control);
            }
            catch (Exception) { /* Audit must never break settings UI. */ }
        }

        private static string SafePage(string page)
        {
            switch (page ?? "")
            {
                case "店铺绑定": case "知识库": case "自动回复规则":
                case "转人工策略": case "消息策略": case "自动发货":
                case "数据管理": case "日志与调试": case "商业化合规":
                case "商业化合规清单": case "关于与更新":
                case "消息通知": case "账号与授权":
                    return page;
                default: return "other";
            }
        }

        private static string SafeControlName(string value)
        {
            return Regex.IsMatch(value ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,63}$")
                ? value : "unnamed";
        }

        private static string SafeToken(string value, string fallback)
        {
            return Regex.IsMatch(value ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,39}$")
                ? value : fallback;
        }

        private static string SafeButtonAction(string content)
        {
            // Only known static verbs, never arbitrary user-entered captions.
            if (string.IsNullOrWhiteSpace(content)) return "unnamed_button";
            var verbs = new[] { "保存", "刷新", "取消", "关闭", "检查", "更新",
                "测试", "添加", "删除", "移除", "重置", "恢复", "打开", "导入",
                "导出", "清空", "确认", "编辑", "复制", "选择", "下载",
                "扫描", "修订", "连接", "断开", "应用", "查看" };
            return verbs.FirstOrDefault(v => content.StartsWith(v, StringComparison.Ordinal))
                ?? "other_button";
        }
    }
}
