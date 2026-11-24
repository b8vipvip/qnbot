using Bot.ChromeNs;
using System;
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
    internal sealed class VisualUnderstandingRulesPage : UserControl, IKnowledgeV2Refreshable, IKnowledgeV2Searchable
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly ObservableCollection<VisualUnderstandingRule> _view =
            new ObservableCollection<VisualUnderstandingRule>();
        private List<VisualUnderstandingRule> _all = new List<VisualUnderstandingRule>();
        private DataGrid _grid;
        private TextBox _search;
        private TextBox _name;
        private TextBox _scene;
        private TextBox _platform;
        private TextBox _device;
        private TextBox _page;
        private TextBox _requiredKeywords;
        private TextBox _anyKeywords;
        private TextBox _excludedKeywords;
        private TextBox _fieldRules;
        private TextBox _priority;
        private CheckBox _enabled;
        private TextBox _testOcr;
        private TextBox _testResult;
        private TextBlock _status;
        private VisualUnderstandingRule _selected;
        private string _globalSearch = string.Empty;
        private bool _loading;

        public VisualUnderstandingRulesPage(string seller, string shopKey)
        {
            _seller = seller ?? string.Empty;
            _shopKey = shopKey ?? string.Empty;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void ApplyGlobalSearch(string text)
        {
            _globalSearch = (text ?? string.Empty).Trim();
            ApplyFilter();
        }

        public void RefreshView()
        {
            if (_loading) return;
            if (string.IsNullOrWhiteSpace(_shopKey) || string.IsNullOrWhiteSpace(_seller))
            {
                _status.Text = "当前没有确认唯一 ShopKey，视觉理解规则保持只读。";
                return;
            }
            _loading = true;
            _status.Text = "正在读取本店视觉理解规则...";
            Task.Run(() => VisualUnderstandingRuleService.LoadRules(_shopKey, _seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _loading = false;
                    if (t.IsFaulted)
                    {
                        _status.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _all = t.Result ?? new List<VisualUnderstandingRule>();
                    ApplyFilter();
                    _status.Text = "已读取 " + _all.Count + " 条视觉理解规则。规则随 Knowledge V2 本店云同步设置同步；未命中时交给上游 AI。";
                })));
        }

        private void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _search = new TextBox { Width = 220, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "筛选规则名称、场景或关键词" };
            _search.TextChanged += delegate { _globalSearch = _search.Text; ApplyFilter(); };
            toolbar.Children.Add(_search);
            var refresh = Btn("刷新", 68);
            refresh.Click += delegate { RefreshView(); };
            var add = Btn("新增规则", 82);
            add.Click += delegate { NewRule(); };
            var kugou = Btn("载入酷狗账号中心预设", 158);
            kugou.Click += delegate { LoadKugouPreset(); };
            var save = Btn("保存规则", 82);
            save.Click += delegate { SaveCurrent(); };
            var delete = Btn("删除规则", 82);
            delete.Click += delegate { DeleteCurrent(); };
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(add);
            toolbar.Children.Add(kugou);
            toolbar.Children.Add(save);
            toolbar.Children.Add(delete);
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(57, GridUnitType.Star) });
            Grid.SetRow(main, 1);
            root.Children.Add(main);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = _view,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "规则名称", Binding = new Binding("Name"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "平台", Binding = new Binding("Platform"), Width = 90 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "终端", Binding = new Binding("DeviceType"), Width = 70 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "页面", Binding = new Binding("PageType"), Width = 90 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "优先级", Binding = new Binding("Priority"), Width = 60 });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new Binding("Enabled"), Width = 48 });
            _grid.SelectionChanged += delegate
            {
                _selected = _grid.SelectedItem as VisualUnderstandingRule;
                LoadEditor(_selected);
            };
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

            var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var editor = new StackPanel();
            editor.Children.Add(Info("规则命中后先提取场景和字段，再结合当前买家对话检索 Knowledge V2。未命中或字段规则不完整时，不做本地直答，交给上游 AI。"));
            editor.Children.Add(Label("规则名称"));
            _name = Input();
            editor.Children.Add(_name);
            editor.Children.Add(Label("场景描述"));
            _scene = Input();
            editor.Children.Add(_scene);
            editor.Children.Add(Label("平台 / 应用"));
            _platform = Input();
            editor.Children.Add(_platform);
            editor.Children.Add(Label("设备终端"));
            _device = Input();
            editor.Children.Add(_device);
            editor.Children.Add(Label("页面类型"));
            _page = Input();
            editor.Children.Add(_page);
            editor.Children.Add(Label("必须同时出现的关键词（逗号分隔）"));
            _requiredKeywords = Input();
            editor.Children.Add(_requiredKeywords);
            editor.Children.Add(Label("以下关键词至少命中一个（逗号分隔）"));
            _anyKeywords = Input();
            editor.Children.Add(_anyKeywords);
            editor.Children.Add(Label("排除关键词（命中任一项则不匹配）"));
            _excludedKeywords = Input();
            editor.Children.Add(_excludedKeywords);
            editor.Children.Add(Label("字段提取规则 JSON（正则表达式捕获组）"));
            _fieldRules = new TextBox
            {
                Height = 190,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 6)
            };
            editor.Children.Add(_fieldRules);
            var options = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            _priority = new TextBox { Width = 70, Height = 28, Text = "50", Margin = new Thickness(0, 0, 12, 0) };
            _enabled = new CheckBox { Content = "启用此规则", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            options.Children.Add(LabelInline("优先级"));
            options.Children.Add(_priority);
            options.Children.Add(_enabled);
            editor.Children.Add(options);
            editorBorder.Child = editorScroll;
            editorScroll.Content = editor;

            var testBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 230)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(testBorder, 2);
            root.Children.Add(testBorder);
            var testGrid = new Grid();
            testGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            testGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            testBorder.Child = testGrid;

            var testLeft = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            testLeft.Children.Add(new TextBlock { Text = "OCR 测试文本", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _testOcr = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 126,
                ToolTip = "粘贴 OCR 原始识别文本，验证当前已保存规则是否能识别场景和字段。"
            };
            testLeft.Children.Add(_testOcr);
            var testButton = Btn("测试匹配", 90);
            testButton.Margin = new Thickness(0, 5, 0, 0);
            testButton.Click += delegate { TestMatch(); };
            testLeft.Children.Add(testButton);
            Grid.SetColumn(testLeft, 0);
            testGrid.Children.Add(testLeft);

            var testRight = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            testRight.Children.Add(new TextBlock { Text = "结构化理解结果 / 下游上下文", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _testResult = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 126
            };
            testRight.Children.Add(_testResult);
            Grid.SetColumn(testRight, 1);
            testGrid.Children.Add(testRight);

            _status = new TextBlock { Margin = new Thickness(0, 7, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);
            Content = root;
        }

        private void ApplyFilter()
        {
            var query = (_globalSearch ?? string.Empty).Trim();
            _view.Clear();
            foreach (var rule in (_all ?? new List<VisualUnderstandingRule>())
                .Where(x => string.IsNullOrWhiteSpace(query)
                    || Contains(x.Name, query) || Contains(x.Scene, query)
                    || Contains(x.Platform, query) || Contains(x.PageType, query)
                    || Contains(x.RequiredKeywords, query) || Contains(x.MatchAnyKeywords, query)))
                _view.Add(rule);
        }

        private void NewRule()
        {
            _selected = new VisualUnderstandingRule
            {
                Enabled = true,
                Priority = 50,
                UpdatedAt = DateTime.Now,
                FieldRulesJson = "[]"
            };
            _grid.SelectedItem = null;
            LoadEditor(_selected);
            _status.Text = "正在编辑新规则。填写字段和关键词后保存。";
        }

        private void LoadKugouPreset()
        {
            _selected = VisualUnderstandingRuleService.CreateKugouAccountCenterPreset();
            _grid.SelectedItem = null;
            LoadEditor(_selected);
            _status.Text = "已载入酷狗账号中心示例预设；这是编辑草稿，点击【保存规则】后才会参与运行时匹配。";
        }

        private void LoadEditor(VisualUnderstandingRule rule)
        {
            if (rule == null)
            {
                _name.Text = string.Empty;
                _scene.Text = string.Empty;
                _platform.Text = string.Empty;
                _device.Text = string.Empty;
                _page.Text = string.Empty;
                _requiredKeywords.Text = string.Empty;
                _anyKeywords.Text = string.Empty;
                _excludedKeywords.Text = string.Empty;
                _fieldRules.Text = "[]";
                _priority.Text = "50";
                _enabled.IsChecked = true;
                return;
            }
            _name.Text = rule.Name ?? string.Empty;
            _scene.Text = rule.Scene ?? string.Empty;
            _platform.Text = rule.Platform ?? string.Empty;
            _device.Text = rule.DeviceType ?? string.Empty;
            _page.Text = rule.PageType ?? string.Empty;
            _requiredKeywords.Text = rule.RequiredKeywords ?? string.Empty;
            _anyKeywords.Text = rule.MatchAnyKeywords ?? string.Empty;
            _excludedKeywords.Text = rule.ExcludedKeywords ?? string.Empty;
            _fieldRules.Text = rule.FieldRulesJson ?? "[]";
            _priority.Text = rule.Priority.ToString();
            _enabled.IsChecked = rule.Enabled;
        }

        private void SaveCurrent()
        {
            if (_selected == null) _selected = new VisualUnderstandingRule();
            int priority;
            if (!int.TryParse(_priority.Text, out priority))
            {
                _status.Text = "优先级必须是整数。";
                return;
            }
            _selected.Name = _name.Text;
            _selected.Scene = _scene.Text;
            _selected.Platform = _platform.Text;
            _selected.DeviceType = _device.Text;
            _selected.PageType = _page.Text;
            _selected.RequiredKeywords = _requiredKeywords.Text;
            _selected.MatchAnyKeywords = _anyKeywords.Text;
            _selected.ExcludedKeywords = _excludedKeywords.Text;
            _selected.FieldRulesJson = _fieldRules.Text;
            _selected.Priority = priority;
            _selected.Enabled = _enabled.IsChecked == true;
            try
            {
                _selected = VisualUnderstandingRuleService.SaveRule(_shopKey, _seller, _selected);
                _status.Text = "规则已保存。Knowledge V2 云同步启用时将随本店规则同步到其他设备。";
                RefreshView();
            }
            catch (Exception ex)
            {
                _status.Text = "保存失败：" + ex.Message;
            }
        }

        private void DeleteCurrent()
        {
            if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id))
            {
                _status.Text = "请先选择一条已保存的规则。";
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                "删除后该规则将不再参与图片识别。此操作会保留同步删除标记。",
                "删除视觉理解规则", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                VisualUnderstandingRuleService.DeleteRule(_shopKey, _seller, _selected.Id);
                _selected = null;
                LoadEditor(null);
                _status.Text = "规则已删除。";
                RefreshView();
            }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }

        private void TestMatch()
        {
            if (string.IsNullOrWhiteSpace(_testOcr.Text))
            {
                _testResult.Text = "请先粘贴 OCR 文本。";
                return;
            }
            VisualUnderstandingMatch match;
            string reason;
            if (VisualUnderstandingRuleService.TryMatch(_shopKey, _seller, _testOcr.Text, out match, out reason))
                _testResult.Text = match.BuildContextText() + Environment.NewLine
                    + Environment.NewLine + "规则匹配分数：" + match.Score.ToString("0.00");
            else
                _testResult.Text = "未命中规则：" + reason + Environment.NewLine
                    + "运行时将交给上游视觉 / 文字 AI 处理。";
        }

        private static TextBox Input()
        {
            return new TextBox { Height = 28, Margin = new Thickness(0, 2, 0, 5), VerticalContentAlignment = VerticalAlignment.Center };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 1) };
        }

        private static TextBlock LabelInline(string text)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        }

        private static TextBlock Info(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 7, 0) };
        }

        private static bool Contains(string value, string query)
        {
            return (value ?? string.Empty).IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
