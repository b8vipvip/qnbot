using Bot.ChromeNs;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Options
{
    internal static class ConversationSessionLearningUi
    {
        private const string TabHeader = "自动学习记录";
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(FeatureSettingsWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded),
                true);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as FeatureSettingsWindow;
            if (window == null) return;
            TryInject(window, 0);
        }

        private static void TryInject(FeatureSettingsWindow window, int attempt)
        {
            if (window == null || attempt > 5) return;
            try
            {
                var field = typeof(FeatureSettingsWindow).GetField(
                    "_tabs",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var rootTabs = field == null ? null : field.GetValue(window) as TabControl;
                if (rootTabs == null) return;

                var logTab = rootTabs.Items
                    .OfType<TabItem>()
                    .FirstOrDefault(x => string.Equals(
                        Convert.ToString(x.Header),
                        "日志与调试",
                        StringComparison.Ordinal));
                if (logTab == null) return;

                var nested = logTab.Content as TabControl;
                if (nested == null)
                {
                    window.Dispatcher.BeginInvoke(new Action(() => TryInject(window, attempt + 1)));
                    return;
                }
                if (nested.Items.OfType<TabItem>().Any(x => string.Equals(
                    Convert.ToString(x.Header),
                    TabHeader,
                    StringComparison.Ordinal)))
                {
                    return;
                }

                nested.Items.Add(new TabItem
                {
                    Header = TabHeader,
                    Content = new ConversationSessionLearningReportControl()
                });
            }
            catch (Exception ex)
            {
                BotLib.Log.ErrorWithMaxCount("注入自动学习记录页面失败：" + ex.Message, 10);
            }
        }
    }

    internal sealed class ConversationSessionLearningReportControl : UserControl
    {
        private readonly ObservableCollection<ConversationSessionLearningReportView> _items =
            new ObservableCollection<ConversationSessionLearningReportView>();
        private readonly DataGrid _grid;
        private readonly TextBox _detail;
        private readonly TextBlock _summary;
        private bool _subscribed;

        public ConversationSessionLearningReportControl()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            Content = root;

            var top = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var refresh = MakeButton("刷新学习记录", 110);
            refresh.Click += (s, e) => RefreshReports();
            top.Children.Add(refresh);

            var copy = MakeButton("复制选中报告", 110);
            copy.Click += (s, e) =>
            {
                var selected = _grid.SelectedItem as ConversationSessionLearningReportView;
                if (selected != null)
                {
                    Clipboard.SetText(ConversationSessionLearningService.FormatReport(selected));
                }
            };
            top.Children.Add(copy);

            _summary = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(_summary, Dock.Top);
            root.Children.Add(_summary);

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(245) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(content);

            _grid = new DataGrid
            {
                ItemsSource = _items,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            _grid.Columns.Add(Column("时间", "CompletedAtText", 120));
            _grid.Columns.Add(Column("客服", "Seller", 105));
            _grid.Columns.Add(Column("买家", "Buyer", 105));
            _grid.Columns.Add(Column("状态", "Status", 110));
            _grid.Columns.Add(Column("应用", "AppliedCount", 65));
            _grid.Columns.Add(Column("跳过", "SkippedCount", 65));
            _grid.Columns.Add(Column("历史覆盖", "InputCoverageSummary", 210));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "复盘摘要",
                Binding = new Binding("Summary"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.SelectionChanged += (s, e) => ShowSelected();
            Grid.SetRow(_grid, 0);
            content.Children.Add(_grid);

            var splitter = new GridSplitter
            {
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                ResizeDirection = GridResizeDirection.Rows
            };
            Grid.SetRow(splitter, 1);
            content.Children.Add(splitter);

            _detail = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Padding = new Thickness(8)
            };
            Grid.SetRow(_detail, 2);
            content.Children.Add(_detail);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            RefreshReports();
        }

        private static DataGridTextColumn Column(string header, string binding, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(binding),
                Width = new DataGridLength(width)
            };
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(8, 2, 8, 2)
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            _subscribed = true;
            ConversationSessionLearningService.ReportsChanged += OnReportsChanged;
            RefreshReports();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _subscribed = false;
            ConversationSessionLearningService.ReportsChanged -= OnReportsChanged;
        }

        private void OnReportsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshReports));
                return;
            }
            RefreshReports();
        }

        private void RefreshReports()
        {
            try
            {
                var selectedId = (_grid == null
                    ? null
                    : _grid.SelectedItem as ConversationSessionLearningReportView)?.Id;
                var reports = ConversationSessionLearningService.GetReports(200);
                _items.Clear();
                foreach (var report in reports) _items.Add(report);
                _summary.Text = "买家或客服连续 " + ConversationSessionLearningService.InactivityMinutes
                    + " 分钟无新消息后复盘整轮接待；历史覆盖不完整时不调用AI，当前默认dry-run候选，不自动写入生产知识库。"
                    + "完整Knowledge V2语料覆盖和Windows灰度验收完成前保持关闭自动写入。当前显示最近 "
                    + reports.Count + " 条。";
                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    _grid.SelectedItem = _items.FirstOrDefault(x => x.Id == selectedId);
                }
                if (_grid.SelectedItem == null && _items.Count > 0) _grid.SelectedIndex = 0;
                ShowSelected();
            }
            catch (Exception ex)
            {
                _summary.Text = "读取自动学习记录失败：" + ex.Message;
            }
        }

        private void ShowSelected()
        {
            var selected = _grid.SelectedItem as ConversationSessionLearningReportView;
            _detail.Text = selected == null
                ? "暂无接待自动学习记录。"
                : ConversationSessionLearningService.FormatReport(selected);
        }
    }
}
