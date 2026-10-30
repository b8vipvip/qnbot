using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class BuyerMessageManagementPage : UserControl, IKnowledgeV2Refreshable, IKnowledgeV2Searchable
    {
        private readonly string _seller;
        private readonly TreeView _tree;
        private readonly TextBox _search;
        private readonly TextBox _days;
        private readonly TextBlock _status;
        private readonly Button _groupButton;
        private CancellationTokenSource _cancel;

        internal BuyerMessageManagementPage(Window owner, string seller)
        {
            _seller = seller ?? "";
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = "买家消息管理", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "仅按店铺汇总买家消息，不保存买家身份。相同语义按组折叠；次数按去重后的真实消息事件统计。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
            });
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
            toolbar.Children.Add(new TextBlock { Text = "显示最近", VerticalAlignment = VerticalAlignment.Center });
            _days = new TextBox { Width = 57, Height = 28, Text = "30", Margin = new Thickness(5, 0, 5, 0) };
            toolbar.Children.Add(_days);
            toolbar.Children.Add(new TextBlock { Text = "天", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0) });
            _search = new TextBox { Width = 225, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "搜索组标题或消息文本" };
            _search.TextChanged += delegate { RefreshView(); };
            toolbar.Children.Add(_search);
            var refresh = Button("刷新", 70); refresh.Click += delegate { RefreshView(); }; toolbar.Children.Add(refresh);
            _groupButton = Button("AI分析并归类", 126);
            _groupButton.Click += async delegate { await GroupAsync(); };
            toolbar.Children.Add(_groupButton);
            var stats = Button("AI统计优化", 100);
            stats.Click += delegate
            {
                new BuyerQuestionOptimizationWindow(owner, _seller).ShowDialog();
            };
            toolbar.Children.Add(stats);
            var rename = Button("修改组标题", 100);
            rename.Click += delegate { RenameSelected(); };
            toolbar.Children.Add(rename);
            var delete = Button("删除选中消息/组", 125);
            delete.Click += delegate { DeleteSelected(); };
            toolbar.Children.Add(delete);
            DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

            _status = new TextBlock { Text = "尚未读取", Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
            _tree = new TreeView { BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1), FontSize = 13 };
            root.Children.Add(_tree);
            Loaded += delegate { RefreshView(); };
            Unloaded += delegate { if (_cancel != null) _cancel.Cancel(); };
        }

        private static Button Button(string label, double width)
        {
            return new Button { Content = label, Width = width, Height = 29,
                Margin = new Thickness(0, 0, 7, 3) };
        }
        private int Days()
        {
            int value;
            return int.TryParse(_days.Text, out value) ? Math.Max(1, Math.Min(3650, value)) : 30;
        }
        public void ApplyGlobalSearch(string text) { _search.Text = text ?? ""; }
        public void RefreshView()
        {
            if (_tree == null || _status == null) return;
            try
            {
                var grouped = BuyerMessageArchiveService.Groups(_seller, Days(), _search.Text);
                _tree.Items.Clear();
                foreach (var group in grouped)
                {
                    var node = new TreeViewItem
                    {
                        Header = group.Title + "    （" + group.Count + "次 · 最近 "
                            + group.Latest.ToString("MM-dd HH:mm") + "）",
                        Tag = group, IsExpanded = false
                    };
                    foreach (var message in group.Messages)
                        node.Items.Add(new TreeViewItem
                        {
                            Header = new DateTime(message.ReceivedAtTicks).ToString("yyyy-MM-dd HH:mm:ss")
                                + "    " + message.Text,
                            Tag = message
                        });
                    _tree.Items.Add(node);
                }
                _status.Text = "当前时间范围：" + Days() + "天；" + grouped.Count + "组，"
                    + grouped.Sum(x => x.Count) + "条消息。点击组展开查看各次提问。";
            }
            catch (Exception ex) { _status.Text = "加载消息失败：" + ex.Message; }
        }
        private async Task GroupAsync()
        {
            if (_cancel != null) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "将当前店铺全部已脱敏的买家消息分批发送到已配置AI接口进行语义归类。"
                + " 可能消耗AI额度；不同语义不会自动删除消息。继续？",
                "AI分析", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _cancel = new CancellationTokenSource();
            _groupButton.IsEnabled = false;
            try
            {
                _status.Text = "正在分析所有归档消息...";
                var count = await BuyerMessageArchiveService.GroupByAi(_seller,
                    message => Dispatcher.BeginInvoke(new Action(() => _status.Text = message)), _cancel.Token);
                _status.Text = "AI分组完成：" + count + "个语义组。";
                RefreshView();
            }
            catch (OperationCanceledException) { _status.Text = "已取消本次AI分析。"; }
            catch (Exception ex) { _status.Text = "AI分析失败，保留原分组：" + ex.Message; }
            finally { _cancel.Dispose(); _cancel = null; _groupButton.IsEnabled = true; }
        }
        private void RenameSelected()
        {
            var node = _tree.SelectedItem as TreeViewItem;
            var group = node == null ? null : node.Tag as BuyerQuestionGroup;
            if (group == null) { _status.Text = "请先选中一个主分组"; return; }
            var value = Prompt(Window.GetWindow(this), "修改分组名称", group.Title);
            if (value == null) return;
            try { BuyerMessageArchiveService.RenameGroup(_seller, group.Key, value); RefreshView(); }
            catch (Exception ex) { _status.Text = "修改失败：" + ex.Message; }
        }
        internal static string Prompt(Window owner, string title, string old)
        {
            var w = new Window { Title = title, Owner = owner, Width = 540, Height = 175,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var panel = new DockPanel { Margin = new Thickness(12) };
            var text = new TextBox { Text = old ?? "", Height = 62, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DockPanel.SetDock(text, Dock.Top); panel.Children.Add(text);
            var save = new Button { Content = "保存", Width = 95, Height = 30, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            save.Click += delegate { w.DialogResult = true; };
            panel.Children.Add(save); w.Content = panel;
            return w.ShowDialog() == true ? text.Text.Trim() : null;
        }
        private void DeleteSelected()
        {
            var node = _tree.SelectedItem as TreeViewItem;
            if (node == null) return;
            var group = node.Tag as BuyerQuestionGroup;
            var message = node.Tag as BuyerQuestionRow;
            var ids = group != null ? group.Messages.Select(x => x.Id).ToArray()
                : message != null ? new[] { message.Id } : new string[0];
            if (ids.Length == 0) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "确认从本店匿名归档中删除 " + ids.Length + " 条消息？不会更改原千牛聊天记录。",
                "删除消息", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { BuyerMessageArchiveService.Delete(_seller, ids); RefreshView(); }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }
    }

    internal sealed class BuyerQuestionOptimizationWindow : Window
    {
        private readonly string _seller;
        private readonly TextBox _days, _top, _question, _answer, _reason;
        private readonly DataGrid _list;
        private readonly TextBlock _status;
        private readonly Button _run;
        private CancellationTokenSource _cancel;
        private BuyerAiOptimizationRow _selected;

        internal BuyerQuestionOptimizationWindow(Window owner, string seller)
        {
            _seller = seller;
            Owner = owner; Title = "买家消息 · AI优化";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 1020; Height = 750; MinWidth = 750; MinHeight = 520;
            var layout = new DockPanel { Margin = new Thickness(12) }; Content = layout;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = "高频买家问题 AI 优化", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "按语义组的实际消息次数排序；建议可查看修改，点击“人工确认并应用”才即时更新生产知识。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
            });
            DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
            controls.Children.Add(new TextBlock { Text = "时间范围（天）", VerticalAlignment = VerticalAlignment.Center });
            _days = new TextBox { Width = 70, Height = 28, Text = "30", Margin = new Thickness(5, 0, 10, 0) };
            controls.Children.Add(_days);
            controls.Children.Add(new TextBlock { Text = "前N组", VerticalAlignment = VerticalAlignment.Center });
            _top = new TextBox { Width = 65, Height = 28, Text = "10", Margin = new Thickness(5, 0, 10, 0) };
            controls.Children.Add(_top);
            _run = new Button { Content = "AI统计优化", Width = 112, Height = 29 };
            _run.Click += async delegate { await GenerateAsync(); };
            controls.Children.Add(_run);
            var refresh = new Button { Content = "刷新记录", Width = 85, Height = 29, Margin = new Thickness(8, 0, 0, 0) };
            refresh.Click += delegate { RefreshRecords(); }; controls.Children.Add(refresh);
            DockPanel.SetDock(controls, Dock.Top); layout.Children.Add(controls);
            _status = new TextBlock { Text = "选择时间范围并点击 AI统计优化。",
                Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);

            var editor = new Grid { Height = 214, Margin = new Thickness(0, 8, 0, 0) };
            editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _question = new TextBox { Margin = new Thickness(0, 0, 8, 5), ToolTip = "对应主问题（可修改）" };
            Grid.SetColumnSpan(_question, 2); editor.Children.Add(_question);
            _answer = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 8, 5),
                ToolTip = "可直接编辑拟应用的标准答案" };
            Grid.SetRow(_answer, 1); editor.Children.Add(_answer);
            _reason = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "AI分析依据和注意事项" };
            Grid.SetRow(_reason, 1); Grid.SetColumn(_reason, 1); editor.Children.Add(_reason);
            var save = new Button { Content = "保存编辑草稿", Height = 31, Width = 108,
                HorizontalAlignment = HorizontalAlignment.Left };
            save.Click += delegate { SaveDraft(); };
            Grid.SetRow(save, 2); editor.Children.Add(save);
            var apply = new Button { Content = "人工确认并应用直答", Width = 156, Height = 31,
                HorizontalAlignment = HorizontalAlignment.Right };
            apply.Click += delegate { ApplySelected(); };
            Grid.SetRow(apply, 2); Grid.SetColumn(apply, 1); editor.Children.Add(apply);
            DockPanel.SetDock(editor, Dock.Bottom); layout.Children.Add(editor);
            _list = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
            _list.Columns.Add(new DataGridTextColumn { Header = "时间", Width = 135, Binding = new Binding("CreatedAtText") });
            _list.Columns.Add(new DataGridTextColumn { Header = "频次", Width = 65, Binding = new Binding("Frequency") });
            _list.Columns.Add(new DataGridTextColumn { Header = "提问", Width = new DataGridLength(2, DataGridLengthUnitType.Star), Binding = new Binding("Question") });
            _list.Columns.Add(new DataGridTextColumn { Header = "状态", Width = 78, Binding = new Binding("Status") });
            _list.Columns.Add(new DataGridTextColumn { Header = "建议答案", Width = new DataGridLength(3, DataGridLengthUnitType.Star), Binding = new Binding("ProposedAnswer") });
            _list.SelectionChanged += delegate { SelectCurrent(); };
            layout.Children.Add(_list);
            Loaded += delegate { RefreshRecords(); };
            Closed += delegate { if (_cancel != null) _cancel.Cancel(); };
        }
        private static int Number(TextBox tb, int fallback, int max)
        {
            int v; return int.TryParse(tb.Text, out v) ? Math.Max(1, Math.Min(max, v)) : fallback;
        }
        private async Task GenerateAsync()
        {
            if (_cancel != null) return;
            if (MessageBox.Show(this, "将按店铺发送已脱敏的高频问题和相关知识到当前AI接口。"
                + " 可能消耗真实API额度；本操作只生成候选记录，不自动覆盖直答。继续？",
                "AI统计优化", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _run.IsEnabled = false; _cancel = new CancellationTokenSource();
            try
            {
                var count = await BuyerMessageArchiveService.OptimizeTop(_seller,
                    Number(_days, 30, 3650), Number(_top, 10, 100),
                    value => Dispatcher.BeginInvoke(new Action(() => _status.Text = value)), _cancel.Token);
                RefreshRecords(); _status.Text = "已生成 " + count + " 条可审核修改的AI优化记录。";
            }
            catch (OperationCanceledException) { _status.Text = "已取消AI统计优化。"; }
            catch (Exception ex) { _status.Text = "AI统计优化失败：" + ex.Message; }
            finally { _cancel.Dispose(); _cancel = null; _run.IsEnabled = true; }
        }
        private void RefreshRecords()
        {
            try
            {
                _list.ItemsSource = BuyerMessageArchiveService.ReadOptimizations(_seller);
                _status.Text = "已加载本店AI优化记录，选中后可编辑答案并人工应用。";
            }
            catch (Exception ex) { _status.Text = "读取AI优化记录失败：" + ex.Message; }
        }
        private void SelectCurrent()
        {
            _selected = _list.SelectedItem as BuyerAiOptimizationRow;
            _question.Text = _selected == null ? "" : _selected.Question;
            _answer.Text = _selected == null ? "" : _selected.ProposedAnswer;
            _reason.Text = _selected == null ? "" : "当前知识：" + _selected.ExistingAnswer
                + "\nAI依据：" + _selected.Reason;
        }
        private bool SaveDraft()
        {
            if (_selected == null) { _status.Text = "请先选中AI优化记录"; return false; }
            if (_selected.Status == "applied") { _status.Text = "已应用记录不可覆盖，请生成新的优化版本"; return false; }
            _selected.Question = BuyerMessageArchiveService.Redact(_question.Text);
            _selected.ProposedAnswer = BuyerMessageArchiveService.Redact(_answer.Text);
            BuyerMessageArchiveService.SaveOptimization(_seller, _selected);
            _status.Text = "草稿已保存，生产知识未修改。";
            return true;
        }
        private void ApplySelected()
        {
            if (!SaveDraft()) return;
            if (MessageBox.Show(this, "确认将编辑后的直答立即应用到本店生产知识库？"
                + "\n问题：" + _selected.Question + "\n答案：" + _selected.ProposedAnswer,
                "人工确认直答", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var status = BuyerMessageArchiveService.Apply(_seller, _selected);
                RefreshRecords(); _status.Text = status;
            }
            catch (Exception ex) { _status.Text = "应用失败：" + ex.Message; }
        }
    }
}
