using Bot.ChromeNs;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal interface IKnowledgeV2Searchable
    {
        void ApplyGlobalSearch(string text);
    }

    internal interface IKnowledgeV2Refreshable
    {
        void RefreshView();
    }

    internal static class KnowledgeCenterV2UiBridge
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

        public static void ShowBotReplySearch(Window owner, string seller, string buyer, string question, string answer)
        {
            Initialize();
            try { ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "无法确认 Bot 回复所属店铺：" + ex.Message,
                    "Bot 回复检索", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new KnowledgeCenterWindow { RequestedSeller = seller };
            if (owner != null) window.Owner = owner;
            window.Loaded += delegate
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var shell = window.Content as KnowledgeCenterV2Shell;
                    if (shell != null)
                        shell.OpenBotReplySearch(question, answer);
                    else
                        Log.ErrorWithMaxCount("打开Bot回复反查知识页面失败：Knowledge Center V2 shell未就绪。", 5);
                }));
            };
            window.Show();
        }

        /// <summary>
        /// Open the actual producer/configuration page of one confirmed Bot message.
        /// Never infer a knowledge or preset ID from a similar answer.
        /// </summary>
        public static void ShowBotReplyTarget(
            Window owner, string seller, string buyer, string answer,
            string sourceRecordId, string source, bool manuallyCorrected)
        {
            Initialize();
            try { ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "无法确认消息所属店铺：" + ex.Message,
                    "查看Bot来源设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var route = BotReplySourceRouting.Resolve(source, manuallyCorrected);
            if (route.Kind == BotReplySourceRouting.RouteKind.AutoReplyRule)
            {
                var active = ActiveShopSessionRegistry.GetActiveSellerNick();
                if (!string.IsNullOrWhiteSpace(active)
                    && !string.Equals(active, seller, StringComparison.Ordinal))
                {
                    MessageBox.Show(owner,
                        "当前活动客服与该消息所属客服不同。为避免跨店修改，请切换至该消息的客服账号后重试。"
                        + "\n消息所属客服：" + seller,
                        "店铺作用域保护", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                WndOption.MyShow(seller, null, OptionEnum.AutoReplyRules);
                return;
            }

            var window = new KnowledgeCenterWindow { RequestedSeller = seller };
            if (owner != null) window.Owner = owner;
            window.Loaded += delegate
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var shell = window.Content as KnowledgeCenterV2Shell;
                    if (shell == null)
                    {
                        MessageBox.Show(window, "知识中心界面未加载，无法打开目标设置。",
                            "查看Bot来源设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (route.Kind == BotReplySourceRouting.RouteKind.Preset)
                    {
                        if (shell.OpenPresetById(route.TargetId)) return;
                        MessageBox.Show(window,
                            "历史消息中的固定话术已被删除或配置不可读取。"
                            + "已转到这条消息的来源管理，避免错误打开其他话术。",
                            "历史话术不可定位", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (route.Kind == BotReplySourceRouting.RouteKind.KnowledgeRecord)
                    {
                        shell.OpenKnowledgeRecordById(route.TargetId);
                        return;
                    }
                    shell.OpenReplySourceById(sourceRecordId, answer);
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
            window.Show();
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow;
            if (window == null) return;
            object marker;
            if (Installed.TryGetValue(window, out marker)) return;
            try { Installed.Add(window, new object()); } catch { return; }

            try
            {
                window.Title = "AI客服 - 知识中心 V2";
                window.MinWidth = 1100;
                window.MinHeight = 720;
                window.Width = Math.Max(window.Width, 1320);
                window.Height = Math.Max(window.Height, 820);
                window.Content = new KnowledgeCenterV2Shell(window);
                Log.Info("Knowledge Center V2界面已替换旧知识库页。");
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("加载Knowledge Center V2界面失败，保留旧知识库界面: " + ex.Message, 10);
            }
        }
    }

    internal sealed class KnowledgeCenterV2Shell : UserControl
    {
        private sealed class NavItem
        {
            public string Name { get; set; }
            public Func<UIElement> Factory { get; set; }
            public override string ToString() { return Name; }
        }

        private readonly KnowledgeCenterWindow _owner;
        private readonly string _seller;
        private readonly ListBox _nav;
        private readonly ContentControl _content;
        private TextBox _globalSearch;
        private TextBlock _status;
        private readonly Dictionary<string, UIElement> _pages = new Dictionary<string, UIElement>(StringComparer.Ordinal);

        public KnowledgeCenterV2Shell(KnowledgeCenterWindow owner)
        {
            _owner = owner;
            _seller = !string.IsNullOrWhiteSpace(owner.RequestedSeller)
                ? owner.RequestedSeller : KnowledgeCenterV2Context.ResolveSeller(owner);

            var root = new Grid { Background = Brushes.White };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            _nav = new ListBox
            {
                Margin = new Thickness(10, 8, 6, 10),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)),
                FontSize = 14
            };
            Grid.SetColumn(_nav, 0);
            body.Children.Add(_nav);

            _content = new ContentControl { Margin = new Thickness(6, 8, 10, 10) };
            Grid.SetColumn(_content, 1);
            body.Children.Add(_content);

            var items = new[]
            {
                Nav("知识", () => new KnowledgeV2RecordsPage(_owner, _seller, KnowledgeV2RecordsPageMode.All)),
                Nav("商品知识", () => new KnowledgeV2RecordsPage(_owner, _seller, KnowledgeV2RecordsPageMode.Product)),
                Nav("流程", () => new KnowledgeV2RecordsPage(_owner, _seller, KnowledgeV2RecordsPageMode.Process)),
                Nav("接待决策", () => new KnowledgeV2ReceptionAuditPage(_seller)),
                Nav("接待规则", () => new KnowledgeV2ReceptionRulesPage(_seller)),
                Nav("通用流程草稿", () => new KnowledgeV2WorkflowDraftPage(_seller)),
                Nav("学习", () => new KnowledgeV2RecordsPage(_owner, _seller, KnowledgeV2RecordsPageMode.Learning)),
                Nav("冲突", () => new KnowledgeV2ConflictPage(_owner, _seller)),
                Nav("测试台", () => new KnowledgeV2DebuggerPage(_owner, _seller)),
                Nav("导入导出", () => new KnowledgeV2ImportExportPage(_owner, _seller)),
                Nav("智能导入", () => new KnowledgeV2SmartImportPage(_owner, _seller)),
                Nav("历史聊天整理", () => new KnowledgeV2ChatHistoryPage(_owner, _seller)),
                Nav("Bot回复管理", () => new KnowledgeV2BotReplyManagementPage(_owner, _seller)),
                Nav("AI优化记录", () => new KnowledgeV2AiOptimizationHistoryPage(_owner, _seller)),
                Nav("设置", () => new KnowledgeV2SettingsPage(_owner, _seller))
            };
            foreach (var item in items) _nav.Items.Add(item);
            _nav.SelectionChanged += delegate { Navigate(); };
            _nav.SelectedIndex = 0;
            Content = root;

            Loaded += delegate
            {
                UpdateStatus();
                if (!string.IsNullOrWhiteSpace(_seller))
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { KnowledgeEngineV2Service.Warm(_seller); } catch { }
                    });
            };
        }

        private UIElement BuildHeader()
        {
            var grid = new Grid
            {
                Margin = new Thickness(14, 12, 14, 4),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 253))
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Margin = new Thickness(12, 8, 20, 8) };
            titlePanel.Children.Add(new TextBlock { Text = "Knowledge Center V2", FontSize = 22, FontWeight = FontWeights.SemiBold });
            _status = new TextBlock
            {
                Text = "结构化知识 + 本地倒排索引 + Working Memory补全",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 4, 0, 0)
            };
            titlePanel.Children.Add(_status);
            Grid.SetColumn(titlePanel, 0);
            grid.Children.Add(titlePanel);

            var searchBorder = new Border
            {
                Margin = new Thickness(10, 12, 10, 12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 217, 226)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Background = Brushes.White
            };
            _globalSearch = new TextBox
            {
                BorderThickness = new Thickness(0),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "搜索标题、答案、Intent、Subject、Predicate、实体或别名"
            };
            _globalSearch.TextChanged += delegate
            {
                var searchable = _content == null ? null : _content.Content as IKnowledgeV2Searchable;
                if (searchable != null) searchable.ApplyGlobalSearch(_globalSearch.Text);
            };
            searchBorder.Child = _globalSearch;
            Grid.SetColumn(searchBorder, 1);
            grid.Children.Add(searchBorder);

            var buttonPanel = new WrapPanel { Margin = new Thickness(10, 10, 10, 8), VerticalAlignment = VerticalAlignment.Center };
            var refresh = Button("刷新", 74);
            refresh.Click += delegate
            {
                var page = _content.Content as IKnowledgeV2Refreshable;
                if (page != null) page.RefreshView();
                UpdateStatus();
            };
            var debug = Button("测试台", 82);
            debug.Click += delegate { SelectNav("测试台"); };
            var transfer = Button("导入导出", 92);
            transfer.Click += delegate { SelectNav("导入导出"); };
            var help = Button("使用帮助", 92);
            help.Click += delegate { KnowledgeCenterHelpWindow.MyShow(_owner, _seller); };
            buttonPanel.Children.Add(refresh);
            buttonPanel.Children.Add(debug);
            buttonPanel.Children.Add(transfer);
            buttonPanel.Children.Add(help);
            Grid.SetColumn(buttonPanel, 2);
            grid.Children.Add(buttonPanel);
            return grid;
        }

        private static Button Button(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }

        internal void OpenReplySourceById(string sourceRecordId, string answer)
        {
            SelectNav("Bot回复管理");
            var page = _content.Content as KnowledgeV2BotReplyManagementPage;
            if (page != null) page.OpenSourceById(sourceRecordId, answer);
        }

        internal bool OpenPresetById(string presetId)
        {
            SelectNav("Bot回复管理");
            var page = _content.Content as KnowledgeV2BotReplyManagementPage;
            return page != null && page.OpenPresetById(presetId);
        }

        internal void OpenKnowledgeRecordById(string recordId)
        {
            SelectNav("知识");
            var page = _content.Content as KnowledgeV2RecordsPage;
            if (page != null) page.OpenRecordById(recordId);
        }

        internal void OpenReplySource(string reply)
        {
            SelectNav("Bot回复管理");
            var page = _content.Content as KnowledgeV2BotReplyManagementPage;
            if (page != null) page.OpenSourceSearch(reply);
        }

        internal void OpenPresetManager(string reply)
        {
            SelectNav("Bot回复管理");
            var page = _content.Content as KnowledgeV2BotReplyManagementPage;
            if (page != null) page.OpenPresetSearch(reply);
        }

        private static NavItem Nav(string name, Func<UIElement> factory)
        {
            return new NavItem { Name = name, Factory = factory };
        }

        private void Navigate()
        {
            var item = _nav.SelectedItem as NavItem;
            if (item == null) return;
            UIElement page;
            if (!_pages.TryGetValue(item.Name, out page))
            {
                page = item.Factory();
                _pages[item.Name] = page;
            }
            _content.Content = page;
            var searchable = page as IKnowledgeV2Searchable;
            if (searchable != null) searchable.ApplyGlobalSearch(_globalSearch.Text);
        }

        private void SelectNav(string name)
        {
            foreach (var item in _nav.Items.OfType<NavItem>())
            {
                if (item.Name != name) continue;
                _nav.SelectedItem = item;
                return;
            }
        }

        public void OpenBotReplySearch(string question, string answer)
        {
            SelectNav("Bot回复管理");
            var page = _content.Content as KnowledgeV2BotReplyManagementPage;
            if (page != null) page.OpenKnowledgeSearch(question, answer);
        }

        private void UpdateStatus()
        {
            if (_status == null) return;
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _status.Text = "当前未识别店铺，知识页只读/不可执行检索。";
                return;
            }
            _status.Text = "店铺：" + _seller + "　V2本地知识引擎（查询不触发定时全量重建）";
        }
    }

    /// <summary>Display-only source route from immutable generation evidence.
    /// Legacy generic source labels do not identify a particular configuration record.
    /// </summary>
    internal static class BotReplySourceRouting
    {
        internal enum RouteKind { SourceRecord, Preset, KnowledgeRecord, AutoReplyRule }

        internal sealed class Route
        {
            public RouteKind Kind;
            public string TargetId;
            public string Path;
            public string Evidence;
        }

        public static Route Resolve(string source, bool manuallyCorrected)
        {
            source = (source ?? string.Empty).Trim();
            var fallback = new Route
            {
                Kind = RouteKind.SourceRecord,
                TargetId = string.Empty,
                Path = "知识中心 V2 → Bot回复管理 → Bot消息来源",
                Evidence = manuallyCorrected
                    ? "此消息的来源已被人工更正。仅能确认更正记录，不能据此证明原始生成配置。"
                    : "没有保存可验证的配置条目ID。可查看这条消息的溯源记录，不能凭回复相似度定位配置。"
            };
            if (manuallyCorrected) return fallback;

            const string presetPrefix = "内置话术：";
            if (source.StartsWith(presetPrefix, StringComparison.Ordinal))
            {
                var open = source.LastIndexOf('（');
                if (open > presetPrefix.Length && source.EndsWith("）", StringComparison.Ordinal))
                {
                    var id = source.Substring(open + 1, source.Length - open - 2).Trim();
                    if (id.Length > 0)
                        return new Route
                        {
                            Kind = RouteKind.Preset, TargetId = id,
                            Path = "知识中心 V2 → Bot回复管理 → 内置固定话术 → " + id,
                            Evidence = "发送时保存的话术ID；如该条目之后已删除，管理页会明确提示。"
                        };
                }
            }

            const string knowledgePrefix = "本地知识V2（记录ID：";
            if (source.StartsWith(knowledgePrefix, StringComparison.Ordinal)
                && source.EndsWith("）", StringComparison.Ordinal))
            {
                var id = source.Substring(knowledgePrefix.Length,
                    source.Length - knowledgePrefix.Length - 1).Trim();
                if (id.Length > 0)
                    return new Route
                    {
                        Kind = RouteKind.KnowledgeRecord, TargetId = id,
                        Path = "知识中心 V2 → 知识 → " + id,
                        Evidence = "本地知识引擎直接回复时保存的命中记录ID。"
                    };
            }

            if (source == "首条咨询固定回复" || source == "下班自动回复"
                || source == "本地短消息回复")
                return new Route
                {
                    Kind = RouteKind.AutoReplyRule, TargetId = string.Empty,
                    Path = "设置 → 回复与通知 → 自动回复规则",
                    Evidence = "来自固定规则链路；该设置页管理规则，历史消息不随设置修改。"
                };

            if (source.IndexOf("知识", StringComparison.Ordinal) >= 0)
            {
                fallback.Evidence = "来源指向知识链路，但历史记录没有保存具体知识ID；不能自动定位相似答案。";
            }
            return fallback;
        }
    }

    internal static class KnowledgeCenterV2Context
    {
        public static string ResolveSeller(Window owner)
        {
            try
            {
                var attached = ShopScopedUiBridge.Get(owner);
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                if (attached != null)
                {
                    foreach (var qn in qns)
                    {
                        if (qn == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick)) continue;
                        try
                        {
                            var shop = ShopContextLocator.ResolveBySellerNick(qn.Seller.Nick);
                            if (shop != null && string.Equals(shop.ShopKey, attached.ShopKey, StringComparison.Ordinal))
                                return qn.Seller.Nick.Trim();
                        }
                        catch { }
                    }
                }
                if (QN.CurQN != null && QN.CurQN.Seller != null && !string.IsNullOrWhiteSpace(QN.CurQN.Seller.Nick))
                    return QN.CurQN.Seller.Nick.Trim();
                var first = qns.FirstOrDefault(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick));
                return first == null ? string.Empty : first.Seller.Nick.Trim();
            }
            catch { return string.Empty; }
        }
    }
}
