using Bot.ShopScope;
using Bot.ChromeNs;
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
    internal static class KnowledgeV2RevisionUiBridge
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

        public static object InitializeForApp()
        {
            Initialize();
            return new object();
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
                if (toolbar.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "修订", StringComparison.Ordinal))) return;
                var button = new Button
                {
                    Content = "修订",
                    Width = 74,
                    Height = 30,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "根据真实人工纠正聚类生成知识修订候选；必须人工复核后才会应用"
                };
                button.Click += delegate
                {
                    var seller = KnowledgeCenterV2Context.ResolveSeller(window);
                    new KnowledgeV2RevisionWindow(window, seller).ShowDialog();
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

    internal sealed class KnowledgeV2RevisionWindow : Window
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly CancellationTokenSource _generationCancellation = new CancellationTokenSource();
        private readonly ObservableCollection<KnowledgeV2RevisionCandidate> _view =
            new ObservableCollection<KnowledgeV2RevisionCandidate>();
        private List<KnowledgeV2RevisionCandidate> _all = new List<KnowledgeV2RevisionCandidate>();
        private ComboBox _filter;
        private TextBox _search;
        private TextBlock _summary;
        private DataGrid _grid;
        private TextBox _details;
        private Button _generate;
        private Button _apply;
        private Button _reject;

        public KnowledgeV2RevisionWindow(Window owner, string seller)
        {
            Owner = owner;
            _seller = seller ?? string.Empty;
            _shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);
            Closed += delegate { _generationCancellation.Cancel(); };
            Title = "Knowledge Center V2 - 修订候选复核";
            Width = 1240;
            Height = 800;
            MinWidth = 1000;
            MinHeight = 650;
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
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(245) });

            var header = new StackPanel();
            header.Children.Add(new TextBlock
            {
                Text = "人工纠正聚类 → 知识修订候选",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "只使用真实人工纠正作为提议来源；同一修订至少需要多个不同买家的一致纠正。系统绝不自动覆盖知识，必须人工复核通过。",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _generate = Btn("分析并生成候选", 124);
            _generate.Click += async delegate { await GenerateAsync(); };
            var refresh = Btn("刷新", 72);
            refresh.Click += delegate { RefreshView(); };
            _apply = Btn("应用所选", 90);
            _apply.Click += delegate { ApplySelected(); };
            _reject = Btn("驳回所选", 90);
            _reject.Click += delegate { RejectSelected(); };
            _filter = new ComboBox { Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            _filter.Items.Add("待复核");
            _filter.Items.Add("全部");
            _filter.Items.Add("已应用");
            _filter.Items.Add("已驳回");
            _filter.Items.Add("已过期");
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += delegate { ApplyFilter(); };
            _search = new TextBox { Width = 220, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "搜索知识标题或候选答案" };
            _search.TextChanged += delegate { ApplyFilter(); };
            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, Margin = new Thickness(8, 6, 0, 0) };
            toolbar.Children.Add(_generate);
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(_apply);
            toolbar.Children.Add(_reject);
            toolbar.Children.Add(_filter);
            toolbar.Children.Add(_search);
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
            _grid.Columns.Add(Col("知识", "KnowledgeTitle", 260));
            _grid.Columns.Add(Col("风险", "RiskLevel", 72));
            _grid.Columns.Add(Col("纠正证据", "EvidenceCount", 78));
            _grid.Columns.Add(Col("不同买家", "DistinctBuyerCount", 78));
            _grid.Columns.Add(Col("聚类可信度", "ClusterScoreText", 88));
            _grid.Columns.Add(Col("状态", "StatusText", 78));
            _grid.Columns.Add(Col("生成时间", "CreatedAtText", 120));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "建议答案",
                Binding = new Binding("ProposedAnswer"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.SelectionChanged += delegate { LoadDetails(_grid.SelectedItem as KnowledgeV2RevisionCandidate); };
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            _details = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 253))
            };
            Grid.SetRow(_details, 3);
            root.Children.Add(_details);
            Content = root;
        }

        private T InShop<T>(Func<T> action)
        {
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
            if (shop == null || !shop.HasStableSellerId || !string.Equals(shop.ShopKey, _shopKey, StringComparison.Ordinal)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, _seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, _seller)))
                throw new InvalidOperationException("知识修订店铺身份不匹配。");
            using (ShopSettingsScope.Enter(shop)) return action();
        }

        private async Task GenerateAsync()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                MessageBox.Show(this, "未识别当前店铺，不能生成修订候选。", "知识修订", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _generate.IsEnabled = false;
            _summary.Text = "正在聚类最近120天真实人工纠正...";
            try
            {
                var result = await Task.Run(() => InShop(() => KnowledgeEngineV2RevisionService.GenerateCandidates(_seller, _generationCancellation.Token)));
                _summary.Text = "扫描 " + result.ScannedKnowledge + " 条｜纠正事件 " + result.CorrectionEvents
                    + "｜新增候选 " + result.Generated + "｜已有候选 " + result.ExistingPending
                    + "｜证据不足 " + result.SkippedInsufficientEvidence;
                RefreshView();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "生成修订候选失败：" + ex.Message, "知识修订", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _generate.IsEnabled = true;
            }
        }

        private void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _summary.Text = "未识别当前店铺";
                return;
            }
            Task.Run(() => InShop(() => KnowledgeEngineV2RevisionService.GetCandidates(_seller, "all", 300)))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _summary.Text = "读取候选失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _all = t.Result ?? new List<KnowledgeV2RevisionCandidate>();
                    ApplyFilter();
                })));
        }

        private void ApplyFilter()
        {
            var filter = _filter == null ? "待复核" : Convert.ToString(_filter.SelectedItem) ?? "待复核";
            var search = (_search == null ? string.Empty : _search.Text ?? string.Empty).Trim();
            IEnumerable<KnowledgeV2RevisionCandidate> items = _all ?? new List<KnowledgeV2RevisionCandidate>();
            if (filter == "待复核") items = items.Where(x => x.Status == "pending");
            else if (filter == "已应用") items = items.Where(x => x.Status == "applied");
            else if (filter == "已驳回") items = items.Where(x => x.Status == "rejected");
            else if (filter == "已过期") items = items.Where(x => x.Status == "stale");
            if (search.Length > 0)
            {
                items = items.Where(x => (x.KnowledgeTitle ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                    || (x.ProposedAnswer ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var list = items.ToList();
            _view.Clear();
            foreach (var item in list) _view.Add(item);
            if (_summary != null)
            {
                _summary.Text = "共 " + _all.Count + " 个｜待复核 " + _all.Count(x => x.Status == "pending")
                    + "｜已应用 " + _all.Count(x => x.Status == "applied")
                    + "｜已驳回 " + _all.Count(x => x.Status == "rejected")
                    + "｜已过期 " + _all.Count(x => x.Status == "stale");
            }
        }

        private void LoadDetails(KnowledgeV2RevisionCandidate item)
        {
            if (_details == null) return;
            if (item == null)
            {
                _details.Text = string.Empty;
                return;
            }
            var lines = new List<string>
            {
                "知识：" + item.KnowledgeTitle,
                "状态：" + item.StatusText + "　风险：" + item.RiskLevel + "　聚类可信度：" + item.ClusterScoreText,
                "证据：" + item.EvidenceCount + " 次人工纠正 / " + item.DistinctBuyerCount + " 个不同买家",
                "",
                "【当前答案】",
                item.OriginalAnswer ?? string.Empty,
                "",
                "【建议修订】",
                item.ProposedAnswer ?? string.Empty,
                "",
                "【真实人工纠正证据】"
            };
            foreach (var ev in item.Evidence ?? new List<KnowledgeV2RevisionEvidence>())
                lines.Add(ev.CreatedAtText + "  " + (ev.Buyer ?? string.Empty) + "：" + (ev.Reply ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(item.ResolutionNote))
            {
                lines.Add("");
                lines.Add("处理记录：" + item.ResolutionNote);
            }
            _details.Text = string.Join(Environment.NewLine, lines);
            _apply.IsEnabled = item.Status == "pending";
            _reject.IsEnabled = item.Status == "pending";
        }

        private void ApplySelected()
        {
            var item = _grid.SelectedItem as KnowledgeV2RevisionCandidate;
            if (item == null || item.Status != "pending") return;
            var warning = string.Equals(item.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)
                ? "\n\n注意：这是高风险知识。系统已要求至少3个不同买家的一致人工纠正，但仍请逐字核对。"
                : string.Empty;
            if (MessageBox.Show(this,
                "确定将建议答案替换当前知识答案吗？\n\n系统会保留原答案和纠正证据作为审计记录，且不会自动修改其他字段。" + warning,
                "应用知识修订", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string error = string.Empty;
            if (!InShop(() => KnowledgeEngineV2RevisionService.ApplyCandidate(_seller, item.Id, out error)))
            {
                MessageBox.Show(this, error, "应用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshView();
                return;
            }
            MessageBox.Show(this, "修订已应用。Knowledge Engine V2索引已同步更新。", "应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshView();
        }

        private void RejectSelected()
        {
            var item = _grid.SelectedItem as KnowledgeV2RevisionCandidate;
            if (item == null || item.Status != "pending") return;
            if (MessageBox.Show(this,
                "确定驳回这个修订候选吗？驳回只影响候选，不会修改当前知识。",
                "驳回修订候选", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string error = string.Empty;
            if (!InShop(() => KnowledgeEngineV2RevisionService.RejectCandidate(_seller, item.Id, "人工复核驳回。", out error)))
            {
                MessageBox.Show(this, error, "驳回失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshView();
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }

        private static DataGridTextColumn Col(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeV2RevisionUiBootstrap = Knowledge.KnowledgeV2RevisionUiBridge.InitializeForApp();
    }
}
