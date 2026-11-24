using Bot.ChromeNs;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2VisualUnderstandingPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly ObservableCollection<VisualUnderstandingRuleEntity> _items = new ObservableCollection<VisualUnderstandingRuleEntity>();
        private DataGrid _grid;
        private TextBox _name, _app, _screen, _required, _optional, _excluded, _patterns, _description;
        private CheckBox _enabled;
        private TextBlock _status;
        private VisualUnderstandingRuleEntity _selected;

        public KnowledgeV2VisualUnderstandingPage(string seller, string shopKey)
        {
            _seller = seller ?? string.Empty;
            _shopKey = shopKey ?? string.Empty;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            _status.Text = "正在读取当前店铺的视觉理解规则…";
            Task.Run(() => VisualUnderstandingRuleService.Load(_shopKey, _seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted) { _status.Text = "读取失败：" + t.Exception.GetBaseException().Message; return; }
                    _items.Clear();
                    foreach (var item in t.Result ?? new System.Collections.Generic.List<VisualUnderstandingRuleEntity>()) _items.Add(item);
                    _status.Text = "共 " + _items.Count + " 条规则；规则按 ShopKey 隔离，仅当前店铺生效。";
                })));
        }

        private void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var add = Button("新增预设"); add.Click += delegate { Select(null); };
            var save = Button("保存规则"); save.Click += delegate { Save(); };
            var del = Button("删除规则"); del.Click += delegate { Delete(); };
            var refresh = Button("刷新"); refresh.Click += delegate { RefreshView(); };
            toolbar.Children.Add(add); toolbar.Children.Add(save); toolbar.Children.Add(del); toolbar.Children.Add(refresh);
            Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62, GridUnitType.Star) });
            Grid.SetRow(body, 1); root.Children.Add(body);
            _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                ItemsSource = _items, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 0, 8, 0) };
            _grid.Columns.Add(new DataGridTextColumn { Header = "规则名称", Binding = new System.Windows.Data.Binding("Name"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "应用", Binding = new System.Windows.Data.Binding("AppName"), Width = 100 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "页面", Binding = new System.Windows.Data.Binding("ScreenName"), Width = 130 });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new System.Windows.Data.Binding("Enabled"), Width = 55 });
            _grid.SelectionChanged += delegate { Select(_grid.SelectedItem as VisualUnderstandingRuleEntity); };
            Grid.SetColumn(_grid, 0); body.Children.Add(_grid);

            var form = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            form.Children.Add(Label("规则名称")); _name = TextBox(); form.Children.Add(_name);
            form.Children.Add(Label("应用名称")); _app = TextBox(); form.Children.Add(_app);
            form.Children.Add(Label("页面名称")); _screen = TextBox(); form.Children.Add(_screen);
            form.Children.Add(Label("必需 OCR 关键词（每行一个，必须全部命中）")); _required = Multi(58); form.Children.Add(_required);
            form.Children.Add(Label("可选关键词（每行一个，用于提高匹配分）")); _optional = Multi(42); form.Children.Add(_optional);
            form.Children.Add(Label("排除关键词（任一命中即拒绝）")); _excluded = Multi(42); form.Children.Add(_excluded);
            form.Children.Add(Label("字段提取规则（每行：字段名|正则表达式；第一个捕获组为值）")); _patterns = Multi(105); form.Children.Add(_patterns);
            form.Children.Add(Label("理解说明 / 交给知识库的上下文")); _description = Multi(62); form.Children.Add(_description);
            _enabled = new CheckBox { Content = "启用此规则", IsChecked = true, Margin = new Thickness(0, 8, 0, 8) }; form.Children.Add(_enabled);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form };
            Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray };
            Grid.SetRow(_status, 2); root.Children.Add(_status);
            Content = root;
        }

        private void Select(VisualUnderstandingRuleEntity item)
        {
            _selected = item;
            _name.Text = item == null ? "" : item.Name;
            _app.Text = item == null ? "" : item.AppName;
            _screen.Text = item == null ? "" : item.ScreenName;
            _required.Text = item == null ? "" : item.RequiredKeywords;
            _optional.Text = item == null ? "" : item.OptionalKeywords;
            _excluded.Text = item == null ? "" : item.ExcludedKeywords;
            _patterns.Text = item == null ? "" : item.FieldPatterns;
            _description.Text = item == null ? "" : item.Description;
            _enabled.IsChecked = item != null && item.Enabled;
        }

        private void Save()
        {
            try
            {
                var item = _selected == null ? new VisualUnderstandingRuleEntity() : _selected;
                item.Name = _name.Text; item.AppName = _app.Text; item.ScreenName = _screen.Text;
                item.RequiredKeywords = _required.Text; item.OptionalKeywords = _optional.Text;
                item.ExcludedKeywords = _excluded.Text; item.FieldPatterns = _patterns.Text;
                item.Description = _description.Text; item.Enabled = _enabled.IsChecked == true;
                Task.Run(() => VisualUnderstandingRuleService.Save(_shopKey, _seller, item))
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (t.IsFaulted) { _status.Text = "保存失败：" + t.Exception.GetBaseException().Message; return; }
                        _selected = item; _status.Text = "保存成功。规则已立即用于当前店铺的图片 OCR 匹配。"; RefreshView();
                    })));
            }
            catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
        }

        private void Delete()
        {
            if (_selected == null) return;
            var id = _selected.EntityId;
            if (MessageBox.Show("确定删除选中的视觉理解规则？", "删除规则", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Task.Run(() => VisualUnderstandingRuleService.Delete(_shopKey, _seller, id))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted) _status.Text = "删除失败：" + t.Exception.GetBaseException().Message;
                    else { Select(null); RefreshView(); }
                })));
        }

        private static Button Button(string text) { return new Button { Content = text, MinWidth = 82, Height = 28, Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(8, 2, 8, 2) }; }
        private static TextBlock Label(string text) { return new TextBlock { Text = text, Margin = new Thickness(0, 7, 0, 3), Foreground = Brushes.DimGray }; }
        private static TextBox TextBox() { return new TextBox { Height = 27, Padding = new Thickness(5, 3, 5, 3) }; }
        private static TextBox Multi(double height) { return new TextBox { Height = height, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(5), FontFamily = new System.Windows.Media.FontFamily("Consolas") }; }
    }
}