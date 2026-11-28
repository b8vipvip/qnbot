using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class KnowledgeV2QualityUiBridge
    {
        private static readonly ConditionalWeakTable<KnowledgeCenterWindow, object> Installed =
            new ConditionalWeakTable<KnowledgeCenterWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(typeof(KnowledgeCenterWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow;
            if (window == null) return;
            object marker;
            if (Installed.TryGetValue(window, out marker)) return;
            try { Installed.Add(window, new object()); } catch { return; }
            window.Dispatcher.BeginInvoke(new Action(() => InjectButton(window)));
        }

        private static void InjectButton(KnowledgeCenterWindow window)
        {
            try
            {
                var toolbar = FindHeaderToolbar(window.Content as DependencyObject);
                if (toolbar == null) return;
                if (toolbar.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "质量", StringComparison.Ordinal))) return;
                var button = new Button
                {
                    Content = "质量",
                    Width = 74,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                button.Click += delegate
                {
                    var seller = KnowledgeCenterV2Context.ResolveSeller(window);
                    var quality = new KnowledgeV2QualityWindow(window, seller);
                    quality.ShowDialog();
                };
                toolbar.Children.Insert(Math.Max(0, toolbar.Children.Count - 1), button);
            }
            catch { }
        }

        private static WrapPanel FindHeaderToolbar(DependencyObject root)
        {
            if (root == null) return null;
            var wrap = root as WrapPanel;
            if (wrap != null)
            {
                var labels = wrap.Children.OfType<Button>().Select(x => Convert.ToString(x.Content)).ToList();
                if (labels.Contains("刷新") && labels.Contains("测试台") && labels.Contains("导入导出")) return wrap;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindHeaderToolbar(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class KnowledgeV2QualityWindow : Window
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly ObservableCollection<KnowledgeV2QualityItem> _view = new ObservableCollection<KnowledgeV2QualityItem>();
        private List<KnowledgeV2QualityItem> _all = new List<KnowledgeV2QualityItem>();
        private TextBox _search;
        private ComboBox _filter;
        private TextBlock _summary;
        private DataGrid _grid;
        private TextBox _details;

        public KnowledgeV2QualityWindow(Window owner, string seller)
        {
            Owner = owner;
            _seller = seller ?? string.Empty;
            _shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);
            Title = "Knowledge Center V2 - 知识质量";
            Width = 1180;
            Height = 760;
            MinWidth = 960;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = "知识质量与真实使用反馈",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold
            });
            title.Children.Add(new TextBlock
            {
                Text = "明确发送成功计入命中；买家明确认可、人工纠正和撤回作为质量证据。发送失败只统计传输问题，不惩罚知识正确性。",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 10)
            });
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _search = new TextBox { Height = 30, ToolTip = "搜索知识标题或类型" };
            _search.TextChanged += delegate { ApplyFilter(); };
            toolbar.Children.Add(_search);
            _filter = new ComboBox { Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var value in new[] { "全部", "低质量", "观察", "健康", "未使用", "最近使用" }) _filter.Items.Add(value);
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += delegate { ApplyFilter(); };
            Grid.SetColumn(_filter, 1);
            toolbar.Children.Add(_filter);
            var refresh = new Button { Content = "刷新", Width = 72, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            refresh.Click += delegate { RefreshView(); };
            Grid.SetColumn(refresh, 2);
            toolbar.Children.Add(refresh);
            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Foreground = Brushes.DimGray };
            Grid.SetColumn(_summary, 3);
            toolbar.Children.Add(_summary);
            Grid.SetRow(toolbar, 1);
            root.Children.Add(toolbar);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _view,
                SelectionMode = DataGridSelectionMode.Single
            };
            _grid.Columns.Add(Col("标题", "Title", 250));
            _grid.Columns.Add(Col("类型", "Type", 105));
            _grid.Columns.Add(Col("命中次数", "UseCount", 78));
            _grid.Columns.Add(Col("已确认", "AcceptedCount", 70));
            _grid.Columns.Add(Col("人工纠正", "CorrectionCount", 78));
            _grid.Columns.Add(Col("撤回", "WithdrawCount", 58));
            _grid.Columns.Add(Col("纠错率", "CorrectionRateText", 72));
            _grid.Columns.Add(Col("发送失败", "SendFailureCount", 72));
            _grid.Columns.Add(Col("质量", "QualityText", 68));
            _grid.Columns.Add(Col("最近使用", "LastUsedAtText", 120));
            _grid.Columns.Add(Col("状态", "HealthStatus", 72));
            _grid.SelectionChanged += delegate { LoadDetails(_grid.SelectedItem as KnowledgeV2QualityItem); };
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            _details = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 253))
            };
            Grid.SetRow(_details, 3);
            root.Children.Add(_details);
            Content = root;
        }

        private static DataGridTextColumn Col(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
        }

        private T InShop<T>(Func<T> action)
        {
            if (string.IsNullOrWhiteSpace(_shopKey))
                throw new InvalidOperationException("质量统计缺少精确 ShopKey。");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, _shopKey, StringComparison.Ordinal)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, _seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, _seller)))
                throw new InvalidOperationException("质量统计店铺身份不匹配。");
            using (ShopSettingsScope.Enter(shop)) return action();
        }

        private void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _summary.Text = "未识别当前店铺";
                return;
            }
            _summary.Text = "正在读取质量数据...";
            Task.Run(() => InShop(() => KnowledgeEngineV2FeedbackService.GetQualityItems(_seller)))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _summary.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _all = t.Result ?? new List<KnowledgeV2QualityItem>();
                    ApplyFilter();
                })));
        }

        private void ApplyFilter()
        {
            var query = (_search == null ? string.Empty : _search.Text ?? string.Empty).Trim();
            var filter = _filter == null ? "全部" : Convert.ToString(_filter.SelectedItem) ?? "全部";
            IEnumerable<KnowledgeV2QualityItem> items = _all ?? new List<KnowledgeV2QualityItem>();
            if (filter == "最近使用") items = items.Where(x => x.LastUsedAt >= DateTime.Now.AddDays(-7));
            else if (filter != "全部") items = items.Where(x => x.HealthStatus == filter);
            if (query.Length > 0)
                items = items.Where(x => (x.Title ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (x.Type ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = items.ToList();
            _view.Clear();
            foreach (var item in list) _view.Add(item);

            if (_summary != null)
            {
                var low = _all.Count(x => x.HealthStatus == "低质量");
                var watch = _all.Count(x => x.HealthStatus == "观察");
                var used = _all.Count(x => x.UseCount > 0);
                var avg = _all.Count == 0 ? 0 : _all.Average(x => x.QualityScore) * 100;
                _summary.Text = "共 " + _all.Count + " 条｜已使用 " + used + "｜观察 " + watch + "｜低质量 " + low + "｜平均质量 " + avg.ToString("0") + "%";
            }
        }

        private void LoadDetails(KnowledgeV2QualityItem item)
        {
            if (_details == null) return;
            if (item == null)
            {
                _details.Text = string.Empty;
                return;
            }
            Task.Run(() => InShop(() => KnowledgeEngineV2FeedbackService.GetRecentEvents(_seller, item.KnowledgeId, 12)))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    var current = _grid.SelectedItem as KnowledgeV2QualityItem;
                    if (current == null || !string.Equals(current.KnowledgeId, item.KnowledgeId, StringComparison.Ordinal)) return;
                    var lines = new List<string>
                    {
                        "知识：" + item.Title,
                        "质量评分：" + item.QualityText + "　状态：" + item.HealthStatus,
                        "命中=" + item.UseCount + "，确认=" + item.AcceptedCount + "，纠正=" + item.CorrectionCount + "，撤回=" + item.WithdrawCount + "，发送失败=" + item.SendFailureCount,
                        "最近证据：" + (string.IsNullOrWhiteSpace(item.LastEvidence) ? "暂无" : item.LastEvidence),
                        "",
                        "最近反馈事件："
                    };
                    if (!t.IsFaulted)
                    {
                        foreach (var ev in t.Result ?? new List<KnowledgeV2FeedbackEventRow>())
                        {
                            DateTime at;
                            try { at = ev.CreatedAtTicks <= 0 ? DateTime.MinValue : new DateTime(ev.CreatedAtTicks); }
                            catch { at = DateTime.MinValue; }
                            lines.Add((at == DateTime.MinValue ? "-" : at.ToString("MM-dd HH:mm:ss"))
                                + "  " + (ev.EventType ?? string.Empty)
                                + (string.IsNullOrWhiteSpace(ev.Evidence) ? string.Empty : "  " + ev.Evidence));
                        }
                    }
                    _details.Text = string.Join(Environment.NewLine, lines);
                })));
        }
    }
}
