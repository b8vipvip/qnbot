using Bot.ChromeNs;
using Bot.Options;
using Bot.UpdateNs;
using BotLib;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            QianniuWebSocketJsonCompatibility.Initialize();
            RuntimeBuildIdentityService.Initialize();
            LegacyAboutUpdateRedirect.Initialize();
            SlowResponseDiagnosticsUi.Initialize();
            ConversationSessionLearningUi.Initialize();
            ReplyQualityCenterUi.Initialize();
            OrderPlacedReplyDelaySettings.Initialize();
            OrderAttentionSettings.Initialize();
            // Explicitly initialize order-template runtime/UI hooks. A never-read static field on a
            // partial App type is not guaranteed to run because of beforefieldinit semantics.
            OrderTemplateRequiredFieldsV2.InitializeForApp();
            SelectableSettingsText.Initialize();
            DirectOrderEventBridge.Initialize();
            OrderPaymentNotificationFallback.Initialize();
            OrderNotificationTraceBridge.Start();
            BotUpdateService.Initialize();
            HandoffRuleRemoteConfigService.Initialize();
            BuyerIdentityAliasRuntimeBridge.Initialize();
            BuyerIdentityAliasUiBridge.Start();
            QnRuntimeSafetyMonitor.Start();
            Bot.Knowledge.KnowledgeV2CloudSyncService.InitializeForApp();
            // Retired V1 knowledge UI hooks are intentionally not bootstrapped.
            // Knowledge V2 owns the import/export surface; do not register the legacy V1 UI.
            ConversationSessionLearningService.Initialize();
            ManualVisualReplyLearningService.Initialize();
            // Explicit startup call: partial-App field initializers are not a reliable
            // bootstrap boundary under beforefieldinit semantics.
            KnowledgeEngineV2RuntimeBridge.InitializeForApp();
            BuyerStreamingReplyPipeline.Initialize();
            VisionWithdrawalAwarePipeline.Initialize();
            VisionFollowUpContextPipeline.Initialize();
            Startup += App_Startup;
            SessionEnding += App_SessionEnding;
            Exit += App_Exit;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        void App_Exit(object sender, ExitEventArgs e)
        {
            try { AdaptiveReplyTimingService.Flush(); } catch { }
            try { ReplyQualityMetricsService.Flush(); } catch { }
        }

        void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try { AdaptiveReplyTimingService.Flush(); } catch { }
            try { ReplyQualityMetricsService.Flush(); } catch { }
        }

        void App_Startup(object sender, StartupEventArgs e)
        {
            // The Qianniu runtime has several legacy synchronous UIA/CDP compatibility paths. Under
            // burst traffic they can temporarily occupy the CLR's very small default worker pool,
            // which in turn delays Task.Delay/CancelAfter continuations. A production log from
            // 1.1.1139 showed a nominal 50-second generation deadline firing 5-12 minutes late.
            // Reserve enough workers so timeout/cancellation and delivery watchdog continuations
            // always have scheduling capacity while the remaining blocking paths are isolated.
            try
            {
                int workerMin;
                int ioMin;
                ThreadPool.GetMinThreads(out workerMin, out ioMin);
                var targetWorker = Math.Max(workerMin, Math.Max(32, Environment.ProcessorCount * 8));
                var targetIo = Math.Max(ioMin, Math.Max(16, Environment.ProcessorCount * 4));
                if (ThreadPool.SetMinThreads(targetWorker, targetIo))
                {
                    Log.Info("运行时线程池抗饥饿保护已启用: workerMin=" + targetWorker
                        + ", ioMin=" + targetIo
                        + ", cpu=" + Environment.ProcessorCount
                        + "；确保AI超时、取消和发送确认定时器不会被同步兼容任务长期饿死。");
                }
                else
                {
                    Log.Info("运行时线程池抗饥饿保护未能调整最小线程数，继续使用CLR当前配置: workerMin="
                        + workerMin + ", ioMin=" + ioMin);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化运行时线程池抗饥饿保护失败: " + ex.Message, 5);
            }
        }

        void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception != null)
            {
                Log.Error("出现UnhandledException");
                Log.Exception(e.Exception);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// 将“功能设置”中的说明、路径、占位符等信息改成可选择复制的只读文本。
    /// 标题和字段标签仍保留 TextBlock，避免整个界面出现不必要的文本光标。
    /// </summary>
    internal static class SelectableSettingsText
    {
        private static readonly ConditionalWeakTable<FeatureSettingsWindow, object> EnhancedWindows =
            new ConditionalWeakTable<FeatureSettingsWindow, object>();

        private static readonly Regex PlaceholderRegex =
            new Regex(@"\{[^{}\r\n]{1,24}\}", RegexOptions.Compiled);

        private static readonly string[] OrderPlaceholders =
        {
            "{客服}",
            "{买家}",
            "{订单号}",
            "{时间}",
            "{商品}",
            "{sku}",
            "{数量}",
            "{金额}",
            "{实付}",
            "{订单状态}",
            "{买家备注}",
            "{分段符}"
        };

        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnFeatureSettingsLoaded),
                true);
        }

        private static void OnFeatureSettingsLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as FeatureSettingsWindow;
            if (window == null) return;

            object marker;
            if (EnhancedWindows.TryGetValue(window, out marker)) return;
            EnhancedWindows.Add(window, new object());

            // 其它设置扩展也会在 Loaded 阶段动态插入控件，延后到空闲队列统一处理。
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => EnhanceWindow(window)));
        }

        private static void EnhanceWindow(FeatureSettingsWindow window)
        {
            try
            {
                var candidates = new List<TextBlock>();
                CollectCandidates(window, candidates);
                foreach (var source in candidates)
                {
                    var replacement = BuildReplacement(source);
                    if (replacement != null)
                    {
                        ReplaceElement(source, replacement);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("初始化可复制设置文本失败：" + ex.Message, 10);
            }
        }

        private static void CollectCandidates(DependencyObject root, ICollection<TextBlock> output)
        {
            if (root == null) return;

            foreach (var child in LogicalChildren(root))
            {
                var text = child as TextBlock;
                if (text != null && ShouldEnhance(text))
                {
                    output.Add(text);
                }

                CollectCandidates(child, output);
            }
        }

        private static bool ShouldEnhance(TextBlock source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Text)) return false;
            // The order-template hint is owned by OrderTemplateSkuUiMigration so its blue clickable
            // placeholders can insert at the answer TextBox caret. Do not replace it with copy-only UI.
            if (IsOrderTemplateHint(source.Text)) return false;
            if (PlaceholderRegex.IsMatch(source.Text)) return true;

            // 粗体栏目标题和字段标签不转换，只处理明确采用灰色说明样式的内容。
            if (source.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()) return false;
            var brush = source.Foreground as SolidColorBrush;
            if (brush == null || !IsMutedColor(brush.Color)) return false;

            return source.TextWrapping == TextWrapping.Wrap
                || source.FontSize <= 11.5
                || source.Text.Trim().Length >= 12;
        }

        private static bool IsOrderTemplateHint(string text)
        {
            text = text ?? string.Empty;
            return text.IndexOf("支持 {客服}", StringComparison.Ordinal) >= 0
                && text.IndexOf("接口失败", StringComparison.Ordinal) >= 0;
        }

        private static bool IsMutedColor(Color color)
        {
            var strongest = Math.Max(color.R, Math.Max(color.G, color.B));
            return color.A > 0 && strongest <= 175;
        }

        private static FrameworkElement BuildReplacement(TextBlock source)
        {
            var displayText = ExpandKnownPlaceholderHelp(source.Text);
            var placeholders = PlaceholderRegex.Matches(displayText)
                .Cast<Match>()
                .Select(x => x.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (placeholders.Count == 0)
            {
                return CreateSelectableTextBox(source, displayText, true);
            }

            var container = new StackPanel();
            CopyElementLayout(source, container, true);

            var selectable = CreateSelectableTextBox(source, displayText, false);
            selectable.Margin = new Thickness(0);
            container.Children.Add(selectable);

            var copyRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            copyRow.Children.Add(new TextBlock
            {
                Text = "点击复制：",
                Foreground = source.Foreground,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            foreach (var placeholder in placeholders)
            {
                copyRow.Children.Add(CreatePlaceholderButton(placeholder));
            }

            container.Children.Add(copyRow);
            return container;
        }

        private static string ExpandKnownPlaceholderHelp(string text)
        {
            text = text ?? string.Empty;
            if (text.Contains("{客服}")
                && text.Contains("{买家}")
                && text.Contains("{订单号}")
                && text.IndexOf("接口失败", StringComparison.Ordinal) >= 0)
            {
                return "支持 " + string.Join("、", OrderPlaceholders)
                    + "。接口失败时也会使用这段话兜底。可拖选文字复制，也可点击下方占位符直接复制。";
            }

            if (PlaceholderRegex.IsMatch(text)
                && text.IndexOf("点击", StringComparison.Ordinal) < 0)
            {
                return text.TrimEnd() + " 可拖选文字复制，也可点击下方占位符直接复制。";
            }

            return text;
        }

        private static TextBox CreateSelectableTextBox(TextBlock source, string text, bool copyMargin)
        {
            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Foreground = source.Foreground,
                FontFamily = source.FontFamily,
                FontSize = source.FontSize,
                FontStretch = source.FontStretch,
                FontStyle = source.FontStyle,
                FontWeight = source.FontWeight,
                TextAlignment = source.TextAlignment,
                TextWrapping = source.TextWrapping == TextWrapping.Wrap
                    || (text ?? string.Empty).Contains("\n")
                    || (text ?? string.Empty).Length > 48
                        ? TextWrapping.Wrap
                        : TextWrapping.NoWrap,
                AcceptsReturn = (text ?? string.Empty).Contains("\n"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Cursor = Cursors.IBeam,
                ToolTip = string.IsNullOrWhiteSpace(Convert.ToString(source.ToolTip))
                    ? "可用鼠标拖选文字并按 Ctrl+C 复制"
                    : source.ToolTip,
                Tag = source.Tag
            };

            CopyElementLayout(source, box, copyMargin);
            return box;
        }

        private static Button CreatePlaceholderButton(string placeholder)
        {
            var button = new Button
            {
                Content = placeholder,
                Tag = placeholder,
                Height = 24,
                MinWidth = 58,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(0, 0, 6, 4),
                Focusable = false,
                ToolTip = "点击复制 " + placeholder
            };

            button.Click += (sender, args) => CopyPlaceholder(sender as Button, placeholder);
            return button;
        }

        private static void CopyPlaceholder(Button button, string placeholder)
        {
            try
            {
                Clipboard.SetText(placeholder ?? string.Empty);
                if (button == null) return;

                var original = placeholder;
                button.Content = "已复制";
                button.IsEnabled = false;

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();
                    button.Content = original;
                    button.IsEnabled = true;
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "复制失败：" + ex.Message,
                    "复制文本",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static void CopyElementLayout(FrameworkElement source, FrameworkElement target, bool copyMargin)
        {
            if (copyMargin) target.Margin = source.Margin;
            target.HorizontalAlignment = source.HorizontalAlignment;
            target.VerticalAlignment = source.VerticalAlignment;
            target.Width = source.Width;
            target.Height = source.Height;
            target.MinWidth = source.MinWidth;
            target.MinHeight = source.MinHeight;
            target.MaxWidth = source.MaxWidth;
            target.MaxHeight = source.MaxHeight;
            target.Visibility = source.Visibility;
            target.Opacity = source.Opacity;
        }

        private static void ReplaceElement(TextBlock source, FrameworkElement replacement)
        {
            var parent = LogicalTreeHelper.GetParent(source);
            if (parent == null) return;

            CopyAttachedLayout(source, replacement);

            var panel = parent as Panel;
            if (panel != null)
            {
                var index = panel.Children.IndexOf(source);
                if (index < 0) return;
                panel.Children.RemoveAt(index);
                panel.Children.Insert(index, replacement);
                return;
            }

            var decorator = parent as Decorator;
            if (decorator != null && ReferenceEquals(decorator.Child, source))
            {
                decorator.Child = replacement;
                return;
            }

            var contentControl = parent as ContentControl;
            if (contentControl != null && ReferenceEquals(contentControl.Content, source))
            {
                contentControl.Content = replacement;
            }
        }

        private static void CopyAttachedLayout(UIElement source, UIElement target)
        {
            Grid.SetRow(target, Grid.GetRow(source));
            Grid.SetColumn(target, Grid.GetColumn(source));
            Grid.SetRowSpan(target, Grid.GetRowSpan(source));
            Grid.SetColumnSpan(target, Grid.GetColumnSpan(source));
            DockPanel.SetDock(target, DockPanel.GetDock(source));
            Canvas.SetLeft(target, Canvas.GetLeft(source));
            Canvas.SetTop(target, Canvas.GetTop(source));
            Canvas.SetRight(target, Canvas.GetRight(source));
            Canvas.SetBottom(target, Canvas.GetBottom(source));
            Panel.SetZIndex(target, Panel.GetZIndex(source));
        }

        private static DependencyObject[] LogicalChildren(DependencyObject root)
        {
            try
            {
                return LogicalTreeHelper.GetChildren(root)
                    .Cast<object>()
                    .OfType<DependencyObject>()
                    .ToArray();
            }
            catch
            {
                return new DependencyObject[0];
            }
        }
    }
}
