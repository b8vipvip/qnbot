using System;
using Bot.ChromeNs;
using Bot.ShopScope;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal enum KnowledgeV2RecordsPageMode
    {
        All,
        Product,
        Process,
        Learning
    }

    internal sealed class KnowledgeV2RecordsPage : UserControl, IKnowledgeV2Searchable, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private readonly KnowledgeV2RecordsPageMode _mode;
        private readonly ObservableCollection<KnowledgeV2Record> _view = new ObservableCollection<KnowledgeV2Record>();
        private List<KnowledgeV2Record> _all = new List<KnowledgeV2Record>();
        private DataGrid _grid;
        private TextBox _search;
        private TextBlock _count;
        private TextBox _title;
        private ComboBox _type;
        private TextBox _intent;
        private TextBox _subject;
        private TextBox _predicate;
        private TextBox _entities;
        private TextBox _aliases;
        private TextBox _answer;
        private TextBox _shortAnswer;
        private TextBox _conditions;
        private TextBox _exclusions;
        private TextBox _required;
        private TextBox _products;
        private ComboBox _risk;
        private TextBox _confidence;
        private TextBox _authority;
        private CheckBox _enabled;
        private ComboBox _status;
        private KnowledgeV2Record _selected;
        private string _globalSearch = string.Empty;
        private string _pendingRecordId = string.Empty;
        private bool _recordsLoaded;
        private bool _recordsLoading;

        public KnowledgeV2RecordsPage(Window owner, string seller, KnowledgeV2RecordsPageMode mode)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            _mode = mode;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void OpenRecordById(string recordId)
        {
            // A producer record ID, not an answer substring, identifies the exact
            // knowledge item to edit. Do not select a similar knowledge candidate.
            _pendingRecordId = (recordId ?? string.Empty).Trim();
            _globalSearch = string.Empty;
            if (_search != null) _search.Text = string.Empty;
            if (_recordsLoaded) ApplyFilter();
            else RefreshView();
        }

        public void ApplyGlobalSearch(string text)
        {
            _globalSearch = (text ?? string.Empty).Trim();
            ApplyFilter();
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller) || _recordsLoading) return;
            _recordsLoading = true;
            _count.Text = "正在读取 Knowledge Center V2...";
            Task.Run(() => KnowledgeEngineV2Repository.LoadAll(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _recordsLoading = false;
                    if (t.IsFaulted)
                    {
                        _count.Text = "读取失败：" + (t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message);
                        return;
                    }
                    _all = t.Result ?? new List<KnowledgeV2Record>();
                    _recordsLoaded = true;
                    ApplyFilter();
                })));
        }

        private void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _search = new TextBox { Width = 260, Height = 30, ToolTip = "本页筛选" };
            _search.TextChanged += delegate { ApplyFilter(); };
            toolbar.Children.Add(_search);
            var refresh = Btn("刷新", 68); refresh.Click += delegate { RefreshView(); };
            var add = Btn("新增知识", 86); add.Click += delegate { NewRecord(); };
            var save = Btn("保存", 68); save.Click += delegate { SaveCurrent(); };
            var delete = Btn("删除", 68); delete.Click += delegate { DeleteCurrent(); };
            toolbar.Children.Add(refresh); toolbar.Children.Add(add); toolbar.Children.Add(save); toolbar.Children.Add(delete);
            if (_mode == KnowledgeV2RecordsPageMode.All)
            {
                var undo = Btn("回滚Bot人工纠错", 138);
                undo.Click += delegate { RollbackLastBotCorrection(); };
                toolbar.Children.Add(undo);
                var reconcile = Btn("条件性建议发送核对", 156);
                reconcile.Click += delegate { OpenConditionalAdviceReconciliation(); };
                toolbar.Children.Add(reconcile);
            }
            if (_mode == KnowledgeV2RecordsPageMode.Learning)
            {
                var approve = Btn("批准入库", 86); approve.Click += delegate { ApproveCurrent(); };
                toolbar.Children.Add(approve);
            }
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Star) });
            Grid.SetRow(main, 1);
            root.Children.Add(main);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _view,
                SelectionMode = DataGridSelectionMode.Single,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _grid.Columns.Add(Col("标题", "Title", 220));
            _grid.Columns.Add(Col("类型", "Type", 110));
            _grid.Columns.Add(Col("Intent", "Intent", 100));
            _grid.Columns.Add(Col("Subject", "Subject", 150));
            _grid.Columns.Add(Col("Predicate", "Predicate", 120));
            _grid.Columns.Add(Col("可信度", "ConfidenceText", 72));
            _grid.Columns.Add(Col("状态", "Status", 80));
            _grid.Columns.Add(Col("更新", "UpdatedAtText", 126));
            _grid.SelectionChanged += delegate
            {
                _selected = _grid.SelectedItem as KnowledgeV2Record;
                LoadEditor(_selected);
            };
            Grid.SetColumn(_grid, 0);
            main.Children.Add(_grid);

            var editorBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 230)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10)
            };
            Grid.SetColumn(editorBorder, 1);
            main.Children.Add(editorBorder);
            editorBorder.Child = BuildEditor();

            _count = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray };
            Grid.SetRow(_count, 2);
            root.Children.Add(_count);
            Content = root;
        }

        private void OpenConditionalAdviceReconciliation()
        {
            ShopContext shop = null;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeBySellerNick(_seller);
            }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "当前店铺身份存在歧义，已取消打开发送核对页面：" + ex.Message, "条件性建议发送核对",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey))
            {
                MessageBox.Show(_owner, "无法解析当前店铺的ShopKey，已取消打开发送核对页面。", "条件性建议发送核对",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new ConditionalBusinessAdviceReconciliationWindow(_owner, shop.ShopKey, _seller);
            window.ShowDialog();
        }

        private UIElement BuildEditor()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel();
            scroll.Content = panel;
            panel.Children.Add(new TextBlock { Text = "知识详情", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            _title = Field(panel, "标题", false, 30);
            _type = Combo(panel, "知识类型", new[] { "business_fact", "procedure", "presale", "order_rule", "after_sale", "safety_rule", "fixed_reply", "product_knowledge", "learning_candidate", "temporary" });
            _intent = Field(panel, "Intent", false, 30);
            _subject = Field(panel, "Subject（业务对象）", false, 30);
            _predicate = Field(panel, "Predicate（询问属性）", false, 30);
            _entities = Field(panel, "Entities（逗号/换行分隔）", true, 58);
            _aliases = Field(panel, "同义问法 / Aliases", true, 72);
            _answer = Field(panel, "标准答案", true, 100);
            _shortAnswer = Field(panel, "简短答案（可选）", true, 60);
            _conditions = Field(panel, "适用条件", true, 58);
            _exclusions = Field(panel, "排除条件", true, 58);
            _required = Field(panel, "必要上下文", true, 58);
            _required.ToolTip = "可填写通用证据条件（每行一项）。条件性售后建议可使用：evidence:image.matches_record、evidence:order.paid、evidence:order.open、evidence:order.product_matches_record。图片知识本身不限定行业；只有明确依赖订单的知识才填写订单证据条件。";
            _products = Field(panel, "绑定商品ID", true, 48);
            _products.ToolTip = "可填写当前知识适用的商品ID；订单条件性建议会复核订单商品ID，缺少商品ID时必须精确匹配商品名称，并复核已提供的SKU，避免跨商品误答。";
            _risk = Combo(panel, "风险等级", new[] { "normal", "high" });
            _confidence = Field(panel, "可信度 0~1", false, 30);
            _authority = Field(panel, "权威度 0~1", false, 30);
            _enabled = new CheckBox { Content = "启用", Margin = new Thickness(0, 8, 0, 6) };
            panel.Children.Add(_enabled);
            _status = Combo(panel, "状态", new[] { "active", "candidate", "disabled" });
            return scroll;
        }

        private static TextBox Field(Panel panel, string label, bool multiline, double height)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3), FontWeight = FontWeights.SemiBold });
            var box = new TextBox
            {
                Height = height,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
            };
            panel.Children.Add(box);
            return box;
        }

        private static ComboBox Combo(Panel panel, string label, IEnumerable<string> values)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3), FontWeight = FontWeights.SemiBold });
            var box = new ComboBox { Height = 30, IsEditable = true };
            foreach (var value in values) box.Items.Add(value);
            panel.Children.Add(box);
            return box;
        }

        private void ApplyFilter()
        {
            if (_view == null) return;
            var query = string.Join(" ", new[] { _globalSearch, _search == null ? string.Empty : _search.Text }).Trim();
            var filtered = (_all ?? new List<KnowledgeV2Record>())
                .Where(x => x != null && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                .Where(MatchesMode)
                .Where(x => MatchesQuery(x, query))
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.Title)
                .ToList();
            _view.Clear();
            foreach (var item in filtered) _view.Add(item);
            if (!string.IsNullOrWhiteSpace(_pendingRecordId) && _recordsLoaded)
            {
                var targetId = _pendingRecordId;
                _pendingRecordId = string.Empty;
                var exact = _view.FirstOrDefault(x =>
                    string.Equals(x.Id, targetId, StringComparison.Ordinal));
                if (exact != null)
                {
                    _grid.SelectedItem = exact;
                    _grid.ScrollIntoView(exact);
                    LoadEditor(exact);
                    if (_count != null) _count.Text = "已定位知识ID：" + targetId;
                    return;
                }
                if (_count != null)
                    _count.Text = "未找到知识ID：" + targetId
                        + "（可能已删除或未同步）；不会自动选择相似条目。";
                return;
            }
            var visibleCount = (_all ?? new List<KnowledgeV2Record>())
                .Count(x => x != null && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase));
            if (_count != null) _count.Text = "V2记录 " + visibleCount + " 条；当前显示 " + _view.Count + " 条";
        }

        private bool MatchesMode(KnowledgeV2Record item)
        {
            if (item == null) return false;
            switch (_mode)
            {
                case KnowledgeV2RecordsPageMode.Product:
                    return item.Type == "product_knowledge" || (item.ProductIds != null && item.ProductIds.Count > 0);
                case KnowledgeV2RecordsPageMode.Process:
                    return item.Type == "procedure" || item.Type == "order_rule";
                case KnowledgeV2RecordsPageMode.Learning:
                    return item.Type == "learning_candidate" || item.Status == "candidate";
                default:
                    return true;
            }
        }

        private static bool MatchesQuery(KnowledgeV2Record item, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var haystack = string.Join(" ", new[]
            {
                item.Title, item.Type, item.Intent, item.Subject, item.Predicate, item.Answer,
                string.Join(" ", item.Entities ?? new List<string>()),
                string.Join(" ", item.Aliases ?? new List<string>()),
                string.Join(" ", item.ProductIds ?? new List<string>())
            });
            foreach (var term in query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private void NewRecord()
        {
            _selected = new KnowledgeV2Record
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = _mode == KnowledgeV2RecordsPageMode.Process ? "procedure"
                    : (_mode == KnowledgeV2RecordsPageMode.Learning ? "learning_candidate" : "business_fact"),
                Intent = "general",
                Predicate = "general",
                RiskLevel = "normal",
                Confidence = 0.80,
                Authority = 0.90,
                Enabled = true,
                Status = _mode == KnowledgeV2RecordsPageMode.Learning ? "candidate" : "active",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                LastVerifiedAt = DateTime.Now
            };
            LoadEditor(_selected);
            _title.Focus();
        }

        private void RollbackLastBotCorrection()
        {
            if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id))
            {
                _count.Text = "请先选择需要回滚的知识记录。";
                return;
            }
            var id = _selected.Id;
            var answer = MessageBox.Show(Window.GetWindow(this),
                "确认回滚该知识最近一次来自Bot右键“修改”的人工纠错？"
                + "\n如果该知识此后已被其他人更新，系统将拒绝覆盖。",
                "回滚Bot人工纠错", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            string reason;
            if (!KnowledgeEngineV2Repository.TryRollbackLastBotCorrection(_seller, id, out reason))
            {
                _count.Text = reason;
                return;
            }
            _pendingRecordId = id;
            RefreshView();
            _count.Text = reason;
        }

        private void LoadEditor(KnowledgeV2Record item)
        {
            if (item == null) return;
            _title.Text = item.Title ?? string.Empty;
            _type.Text = item.Type ?? string.Empty;
            _intent.Text = item.Intent ?? string.Empty;
            _subject.Text = item.Subject ?? string.Empty;
            _predicate.Text = item.Predicate ?? string.Empty;
            _entities.Text = Join(item.Entities);
            _aliases.Text = Join(item.Aliases);
            _answer.Text = item.Answer ?? string.Empty;
            _shortAnswer.Text = item.ShortAnswer ?? string.Empty;
            _conditions.Text = Join(item.Conditions);
            _exclusions.Text = Join(item.Exclusions);
            _required.Text = Join(item.RequiredContext);
            _products.Text = Join(item.ProductIds);
            _risk.Text = item.RiskLevel ?? "normal";
            _confidence.Text = item.Confidence.ToString("0.00");
            _authority.Text = item.Authority.ToString("0.00");
            _enabled.IsChecked = item.Enabled;
            _status.Text = item.Status ?? "active";
        }

        private void SaveCurrent()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            var record = _selected ?? new KnowledgeV2Record { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.Now };
            record.Title = _title.Text.Trim();
            record.Type = _type.Text.Trim();
            record.Intent = _intent.Text.Trim();
            record.Subject = _subject.Text.Trim();
            record.Predicate = _predicate.Text.Trim();
            record.Entities = Split(_entities.Text);
            record.Aliases = Split(_aliases.Text);
            record.Answer = _answer.Text.Trim();
            record.ShortAnswer = _shortAnswer.Text.Trim();
            record.Conditions = Split(_conditions.Text);
            record.Exclusions = Split(_exclusions.Text);
            record.RequiredContext = Split(_required.Text);
            record.ProductIds = Split(_products.Text);
            record.RiskLevel = _risk.Text.Trim();
            double confidence;
            double authority;
            record.Confidence = double.TryParse(_confidence.Text, out confidence) ? confidence : 0.80;
            record.Authority = double.TryParse(_authority.Text, out authority) ? authority : 0.90;
            record.Enabled = _enabled.IsChecked == true;
            record.Status = _status.Text.Trim();
            if (string.IsNullOrWhiteSpace(record.Title) || string.IsNullOrWhiteSpace(record.Answer))
            {
                MessageBox.Show(_owner, "标题和标准答案不能为空。", "知识中心 V2", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _count.Text = "正在保存...";
            Task.Run(() =>
            {
                KnowledgeEngineV2Repository.Save(_seller, record);
                KnowledgeEngineV2Service.Warm(_seller);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) MessageBox.Show(_owner, "保存失败：" + t.Exception.GetBaseException().Message);
                else { _selected = null; RefreshView(); }
            })));
        }

        private void DeleteCurrent()
        {
            if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id)) return;
            if (MessageBox.Show(_owner, "确定删除这条V2知识吗？旧兼容知识镜像也会同步删除。", "删除知识", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var id = _selected.Id;
            Task.Run(() =>
            {
                KnowledgeEngineV2Repository.Delete(_seller, id);
                KnowledgeEngineV2Service.Warm(_seller);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) MessageBox.Show(_owner, "删除失败：" + t.Exception.GetBaseException().Message);
                else { _selected = null; RefreshView(); }
            })));
        }

        private void ApproveCurrent()
        {
            if (_selected == null) return;
            var id = _selected.Id;
            Task.Run(() =>
            {
                KnowledgeEngineV2Service.PromoteCandidate(_seller, id);
                KnowledgeEngineV2Service.Warm(_seller);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) MessageBox.Show(_owner, "批准失败：" + t.Exception.GetBaseException().Message);
                else RefreshView();
            })));
        }

        private static List<string> Split(string value)
        {
            return (value ?? string.Empty).Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(Environment.NewLine, values ?? Enumerable.Empty<string>());
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        }

        private static DataGridTextColumn Col(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
        }
    }

    internal sealed class ConditionalBusinessAdviceReconciliationWindow : Window
    {
        private readonly string _shopKey;
        private readonly string _seller;
        private readonly ObservableCollection<ConditionalBusinessAdviceReconciliationItem> _items =
            new ObservableCollection<ConditionalBusinessAdviceReconciliationItem>();
        private DataGrid _grid;
        private TextBlock _status;

        public ConditionalBusinessAdviceReconciliationWindow(Window owner, string shopKey, string seller)
        {
            _shopKey = (shopKey ?? string.Empty).Trim();
            _seller = (seller ?? string.Empty).Trim();
            Title = "条件性建议发送状态核对";
            Width = 860;
            Height = 520;
            MinWidth = 700;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (owner != null) Owner = owner;

            var root = new DockPanel { Margin = new Thickness(14) };
            var header = new StackPanel { Orientation = Orientation.Vertical };
            header.Children.Add(new TextBlock
            {
                Text = "以下记录表示发送结果不确定。请先核对千牛中该买家的真实聊天记录，再选择确认已发送或确认未发送。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            header.Children.Add(new TextBlock
            {
                Text = "注意：此账本属于当前Windows用户配置文件；同一店铺若在多台电脑同时运行，仍需要单一发送主实例或云端共享幂等服务。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkRed,
                Margin = new Thickness(0, 0, 0, 10)
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = _items
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "买家", Binding = new Binding("Buyer"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "订单号", Binding = new Binding("OrderId"), Width = 170 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "知识记录", Binding = new Binding("AdviceKey"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "状态更新时间", Binding = new Binding("UpdatedAt"), Width = 160 });
            root.Children.Add(_grid);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            footer.Children.Add(_status);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var refresh = new Button { Content = "刷新", Width = 70, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            refresh.Click += delegate { RefreshItems(); };
            var delivered = new Button { Content = "确认已发送", Width = 100, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            delivered.Click += delegate { ResolveSelected(true); };
            var notDelivered = new Button { Content = "确认未发送并允许重试", Width = 160, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            notDelivered.Click += delegate { ResolveSelected(false); };
            var close = new Button { Content = "关闭", Width = 70, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            close.Click += delegate { Close(); };
            buttons.Children.Add(refresh);
            buttons.Children.Add(delivered);
            buttons.Children.Add(notDelivered);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            Content = root;
            Loaded += delegate { RefreshItems(); };
        }

        private void RefreshItems()
        {
            _status.Text = "正在读取待核对记录…";
            Task.Run(() => ConditionalBusinessAdviceService.GetPendingReconciliationRecords(_shopKey, _seller))
                .ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (task.IsFaulted)
                    {
                        _status.Text = "读取失败：" + task.Exception.GetBaseException().Message;
                        return;
                    }
                    _items.Clear();
                    foreach (var item in task.Result ?? new List<ConditionalBusinessAdviceReconciliationItem>())
                        _items.Add(item);
                    _status.Text = "待核对记录：" + _items.Count;
                })));
        }

        private void ResolveSelected(bool confirmedDelivered)
        {
            var item = _grid.SelectedItem as ConditionalBusinessAdviceReconciliationItem;
            if (item == null)
            {
                MessageBox.Show(this, "请先选择一条待核对记录。", "条件性建议发送核对",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prompt = confirmedDelivered
                ? "请确认你已在千牛聊天记录中看到这条建议确实发送给该买家。确认后将永久保留防重复发送标记。"
                : "请确认你已在千牛聊天记录中核实这条建议没有发送给该买家。确认后会解除幂等锁，但下一次发送仍必须重新通过图片、商品和订单证据校验。";
            if (MessageBox.Show(this, prompt, "请核对真实聊天记录",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _status.Text = "正在保存人工核对结果…";
            Task.Run(() =>
            {
                string reason;
                var ok = ConditionalBusinessAdviceService.ResolveUncertainDelivery(
                    _shopKey, _seller, item.Buyer, item.OrderId, item.AdviceKey,
                    confirmedDelivered, out reason);
                return new { Ok = ok, Reason = reason };
            }).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (task.IsFaulted)
                {
                    _status.Text = "核对失败：" + task.Exception.GetBaseException().Message;
                    return;
                }
                _status.Text = task.Result.Reason;
                if (task.Result.Ok) RefreshItems();
            })));
        }
    }
}
