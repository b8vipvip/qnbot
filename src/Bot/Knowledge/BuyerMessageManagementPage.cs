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
    // A display-only projection of one anonymous archived message.
    // The archive continues to store one row per actual buyer message event.
    internal sealed class BuyerMessageListItem
    {
        public BuyerQuestionRow Message { get; set; }
        public DateTime ReceivedAt { get; set; }
        public string Text { get; set; }
        public string GroupKey { get; set; }
        public string GroupTitle { get; set; }
        public int GroupCount { get; set; }
    }

    internal sealed class BuyerMessageManagementPage : UserControl, IKnowledgeV2Refreshable, IKnowledgeV2Searchable
    {
        private readonly string _seller;
        private readonly DataGrid _list;
        private readonly TextBox _search;
        private readonly DatePicker _startDate;
        private readonly DatePicker _endDate;
        private readonly TextBlock _status;
        private readonly Button _groupButton;
        private readonly Button _scanButton;
        private readonly Button _stopScan;
        private CancellationTokenSource _cancel;
        private CancellationTokenSource _scanCancellation;

        internal BuyerMessageManagementPage(Window owner, string seller)
        {
            _seller = seller ?? "";
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = "买家消息管理", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "逐条展示本店已归档的匿名买家消息，默认按接收时间倒序排列。"
                    + "同类消息不折叠，AI归类仅用于分类与统计，不保存买家身份。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
            });
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
            toolbar.Children.Add(new TextBlock { Text = "筛选开始日期", VerticalAlignment = VerticalAlignment.Center });
            _startDate = new DatePicker { Width = 127, SelectedDate = DateTime.Today.AddDays(-30),
                Margin = new Thickness(5, 0, 8, 0) };
            _startDate.SelectedDateChanged += delegate { RefreshView(); };
            toolbar.Children.Add(_startDate);
            toolbar.Children.Add(new TextBlock { Text = "结束日期", VerticalAlignment = VerticalAlignment.Center });
            _endDate = new DatePicker { Width = 127, SelectedDate = DateTime.Today,
                Margin = new Thickness(5, 0, 8, 0) };
            _endDate.SelectedDateChanged += delegate { RefreshView(); };
            toolbar.Children.Add(_endDate);
            _search = new TextBox { Width = 225, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "搜索消息内容或所属分类" };
            _search.TextChanged += delegate { RefreshView(); };
            toolbar.Children.Add(_search);
            var refresh = Button("刷新", 70);
            refresh.Click += delegate { RefreshView(); }; toolbar.Children.Add(refresh);
            _scanButton = Button("扫描历史消息", 115);
            _scanButton.Click += async delegate { await ScanHistoryAsync(); };
            toolbar.Children.Add(_scanButton);
            _stopScan = Button("取消扫描", 90);
            _stopScan.IsEnabled = false;
            _stopScan.Click += delegate { if (_scanCancellation != null) _scanCancellation.Cancel(); };
            toolbar.Children.Add(_stopScan);
            _groupButton = Button("AI分析并归类", 126);
            _groupButton.Click += async delegate { await GroupAsync(); };
            toolbar.Children.Add(_groupButton);
            var stats = Button("AI统计优化", 100);
            stats.Click += delegate { new BuyerQuestionOptimizationWindow(owner, _seller).ShowDialog(); };
            toolbar.Children.Add(stats);
            var rename = Button("修改组标题", 100);
            rename.Click += delegate { RenameSelected(); };
            toolbar.Children.Add(rename);
            var delete = Button("删除选中消息", 110);
            delete.Click += delegate { DeleteSelected(); };
            toolbar.Children.Add(delete);
            var deleteGroup = Button("删除所属分类", 110);
            deleteGroup.Click += delegate { DeleteSelectedGroup(); };
            toolbar.Children.Add(deleteGroup);
            DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

            _status = new TextBlock { Text = "尚未读取", Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);

            _list = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserSortColumns = true,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeaderWidth = 0, MinRowHeight = 30,
                EnableRowVirtualization = true, EnableColumnVirtualization = true,
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                FontSize = 13
            };
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            _list.Columns.Add(new DataGridTextColumn
            {
                Header = "接收时间",
                Binding = new Binding("ReceivedAt") { StringFormat = "{0:yyyy-MM-dd HH:mm:ss}" },
                SortMemberPath = "ReceivedAt", Width = new DataGridLength(160)
            });

            // Text may be long. Wrap its preview while keeping full redacted text
            // accessible via tooltip and double-click detail; do not merge events.
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding("Text"));
            textFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text"));
            textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            textFactory.SetValue(FrameworkElement.MaxHeightProperty, 90.0);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 4, 5, 4));
            _list.Columns.Add(new DataGridTemplateColumn
            {
                Header = "买家消息内容", CellTemplate = new DataTemplate { VisualTree = textFactory },
                SortMemberPath = "Text", MinWidth = 280,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _list.Columns.Add(new DataGridTextColumn
            {
                Header = "所属分类", Binding = new Binding("GroupTitle"),
                SortMemberPath = "GroupTitle", Width = new DataGridLength(210)
            });
            _list.Columns.Add(new DataGridTextColumn
            {
                Header = "筛选内同类次数", Binding = new Binding("GroupCount"),
                SortMemberPath = "GroupCount", Width = new DataGridLength(118)
            });
            _list.MouseDoubleClick += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                var row = ItemsControl.ContainerFromElement(
                    _list, e.OriginalSource as DependencyObject) as DataGridRow;
                if (row != null) ShowMessageDetails(row.Item as BuyerMessageListItem);
            };
            root.Children.Add(_list);
            Loaded += delegate { RefreshView(); };
            Unloaded += delegate
            {
                if (_cancel != null) _cancel.Cancel();
                if (_scanCancellation != null) _scanCancellation.Cancel();
            };
        }

        private static Button Button(string label, double width)
        {
            return new Button { Content = label, Width = width, Height = 29,
                Margin = new Thickness(0, 0, 7, 3) };
        }
        public void ApplyGlobalSearch(string text) { _search.Text = text ?? ""; }
        public void RefreshView()
        {
            if (_list == null || _status == null) return;
            try
            {
                var start = _startDate == null || !_startDate.SelectedDate.HasValue
                    ? (DateTime?)null : _startDate.SelectedDate.Value.Date;
                var end = _endDate == null || !_endDate.SelectedDate.HasValue
                    ? (DateTime?)null : _endDate.SelectedDate.Value.Date.AddDays(1).AddTicks(-1);
                var grouped = BuyerMessageArchiveService.Groups(_seller, start, end, _search.Text);
                // Display each original message once and sort across ALL categories,
                // instead of displaying a sorted list of expandable category trees.
                var rows = grouped.SelectMany(g => g.Messages.Select(message => new BuyerMessageListItem
                {
                    Message = message, ReceivedAt = new DateTime(message.ReceivedAtTicks),
                    Text = message.Text, GroupKey = g.Key, GroupTitle = g.Title,
                    GroupCount = g.Count
                })).OrderByDescending(x => x.ReceivedAt)
                    .ThenBy(x => x.Message.Id, StringComparer.Ordinal).ToList();
                _list.ItemsSource = rows;
                _status.Text = "当前筛选结果：" + rows.Count + "条消息，" + grouped.Count
                    + "个分类。按接收时间倒序；单击列标题可排序，双击消息可查看全文。";
            }
            catch (Exception ex) { _status.Text = "加载消息失败：" + ex.Message; }
        }

        private void ShowMessageDetails(BuyerMessageListItem item)
        {
            if (item == null) return;
            var w = new Window
            {
                Owner = Window.GetWindow(this), Title = "匿名买家消息详情",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 720, Height = 400, MinWidth = 520, MinHeight = 270,
                ShowInTaskbar = false
            };
            var panel = new DockPanel { Margin = new Thickness(12) };
            var info = new TextBlock
            {
                Text = "接收时间：" + item.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss")
                    + "    所属分类：" + item.GroupTitle,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
            panel.Children.Add(new TextBox
            {
                Text = item.Text ?? "", IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(8)
            });
            w.Content = panel; w.ShowDialog();
        }

        private async Task ScanHistoryAsync()
        {
            if (_scanCancellation != null) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "扫描当前店铺千牛可访问的全部买家历史消息。"
                + " 过程中需要切换聊天以读取历史记录，结束后恢复原会话。"
                + " 仅保存脱敏后的消息文本，不导入知识库，也不会调用AI或发送客服消息。是否开始？",
                "扫描历史买家消息", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _scanCancellation = new CancellationTokenSource();
            _scanButton.IsEnabled = false;
            _stopScan.IsEnabled = true;
            try
            {
                _status.Text = "正在扫描本店可访问的全部历史买家消息...";
                var scan = await new ChatHistoryScanService().ScanBuyerMessagesAsync(
                    _seller, new ChatHistoryScanOptions { ScanAll = true, MaxContacts = 5000 },
                    progress => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_scanCancellation != null)
                            _status.Text = "扫描进度 " + progress.ContactIndex + "/"
                                + progress.ContactCount + "；已读取消息 " + progress.MessageCount;
                    })), _scanCancellation.Token);
                RefreshView();
                _status.Text = "历史扫描完成：已扫描 " + scan.ScannedContacts
                    + "/" + scan.ContactCount + " 位联系人，读取 " + scan.MessageCount
                    + " 条买家消息，失败 " + scan.FailedContacts
                    + " 个；重复事件自动去重。请调整日期范围查看。";
            }
            catch (OperationCanceledException) { _status.Text = "扫描已取消，已保存的归档消息保留。"; }
            catch (Exception ex) { _status.Text = "扫描失败：" + ex.Message; }
            finally
            {
                _scanCancellation.Dispose();
                _scanCancellation = null;
                _scanButton.IsEnabled = true;
                _stopScan.IsEnabled = false;
            }
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
                RefreshView();
                _status.Text = "AI分组完成：" + count + "个语义组。";
            }
            catch (OperationCanceledException) { _status.Text = "已取消本次AI分析。"; }
            catch (Exception ex) { _status.Text = "AI分析失败，保留原分组：" + ex.Message; }
            finally { _cancel.Dispose(); _cancel = null; _groupButton.IsEnabled = true; }
        }

        private BuyerMessageListItem SelectedGroupRow()
        {
            var rows = _list.SelectedItems.OfType<BuyerMessageListItem>().ToList();
            if (rows.Count == 0) { _status.Text = "请先选择一条所属分类的消息"; return null; }
            if (rows.Select(x => x.GroupKey).Distinct(StringComparer.Ordinal).Count() != 1)
            { _status.Text = "选择的消息跨越不同分类，请只选同一分类"; return null; }
            return rows[0];
        }

        private void RenameSelected()
        {
            var selected = SelectedGroupRow();
            if (selected == null) return;
            var value = Prompt(Window.GetWindow(this), "修改分组名称", selected.GroupTitle);
            if (value == null) return;
            try { BuyerMessageArchiveService.RenameGroup(_seller, selected.GroupKey, value); RefreshView(); }
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
            var save = new Button { Content = "保存", Width = 95, Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            save.Click += delegate { w.DialogResult = true; };
            panel.Children.Add(save); w.Content = panel;
            return w.ShowDialog() == true ? text.Text.Trim() : null;
        }

        private void DeleteSelected()
        {
            // Multiple selection deletes only the explicitly selected rows.
            var ids = _list.SelectedItems.OfType<BuyerMessageListItem>()
                .Select(x => x.Message.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) { _status.Text = "请先选中要删除的消息"; return; }
            if (MessageBox.Show(Window.GetWindow(this),
                "确认从本店匿名归档中删除选中的 " + ids.Length
                    + " 条消息？不会更改原千牛聊天记录。",
                "删除选中消息", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { BuyerMessageArchiveService.Delete(_seller, ids); RefreshView(); }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }

        private void DeleteSelectedGroup()
        {
            var selected = SelectedGroupRow();
            if (selected == null) return;
            // Explicit whole-category deletion includes messages outside the date filter.
            var ids = BuyerMessageArchiveService.Read(_seller)
                .Where(x => x.GroupKey == selected.GroupKey)
                .Select(x => x.Id).ToArray();
            if (ids.Length == 0) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "确定删除分类「" + selected.GroupTitle + "」的全部 " + ids.Length
                    + " 条匿名消息吗？包括当前日期筛选以外的记录；不会修改千牛聊天记录。",
                "删除整个分类", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { BuyerMessageArchiveService.Delete(_seller, ids); RefreshView(); }
            catch (Exception ex) { _status.Text = "删除分类失败：" + ex.Message; }
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
