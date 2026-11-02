using BotLib;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
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
        public List<BuyerQuestionRow> Messages { get; set; }
        public List<string> Details { get; set; }
        public bool IsSemanticMerged { get; set; }
        public string DirectAnswer { get; set; }
        public string DirectKnowledgeId { get; set; }
        public BuyerAiOptimizationRow AppliedRevision { get; set; }
    }

    // No expand glyph for identical messages. Show variants only if AI has
    // merged different literal texts into a semantic group.
    internal sealed class BuyerMessageContentSelector : DataTemplateSelector
    {
        public DataTemplate ExactTemplate { get; set; }
        public DataTemplate SemanticTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var row = item as BuyerMessageListItem;
            return row != null && row.IsSemanticMerged ? SemanticTemplate : ExactTemplate;
        }
    }

    internal sealed class BuyerMessageManagementPage : UserControl, IKnowledgeV2Refreshable, IKnowledgeV2Searchable
    {
        private readonly string _seller;
        private readonly DockPanel _root;
        private readonly DataGrid _list;
        private readonly TextBox _search;
        private readonly DatePicker _startDate;
        private readonly DatePicker _endDate;
        private readonly TextBlock _status;
        private readonly Button _groupButton;
        private readonly Button _scanButton;
        private readonly Button _stopScan;
        private readonly Border _scanCard;
        private readonly TextBlock _scanCardTitle;
        private readonly Button _scanCardClose;
        private readonly ProgressBar _scanProgress;
        private readonly TextBox _scanLog;
        private int _scanGeneration;
        private string _lastScanProgressKey;
        private CancellationTokenSource _cancel;
        private CancellationTokenSource _scanCancellation;

        internal BuyerMessageManagementPage(Window owner, string seller)
        {
            _seller = seller ?? "";
            _root = new DockPanel { Margin = new Thickness(12) };
            var root = _root;
            Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = "买家消息管理", FontSize = 20, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = "完全相同的消息去重计数；已存在的语义合并问法可以展开。直答列展示本店已审核的精确知识及人工审核后关联到本组的直答；实际发送仍需满足业务上下文与安全规则。"
                    + " AI分析只生成待审核直答建议，不再分类，人工确认后才更新生产知识。",
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
                ToolTip = "搜索买家消息内容" };
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
            var records = Button("扫描记录 / 数据包", 145);
            records.Click += delegate { ShowScanRecords(); };
            toolbar.Children.Add(records);
            _groupButton = Button("AI分析优化", 120);
            _groupButton.Click += delegate
            {
                if (!_groupButton.IsEnabled) return;
                _groupButton.IsEnabled = false;
                try
                {
                    // One action: AI semantic regrouping followed by audited optimization
                    // candidates; production knowledge is only changed on human approval.
                    new BuyerQuestionOptimizationWindow(owner, _seller, true).ShowDialog();
                    RefreshView();
                }
                finally { _groupButton.IsEnabled = true; }
            };
            toolbar.Children.Add(_groupButton);
            var delete = Button("删除选中消息", 110);
            delete.Click += delegate { DeleteSelected(); };
            toolbar.Children.Add(delete);
            DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

            _status = new TextBlock { Text = "尚未读取", Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Top); root.Children.Add(_status);
            // Create the progress card once, but DO NOT add it to the page.
            // It is inserted only after the user confirms a history scan.
            // When the scan ends, its summary stays visible until the user
            // explicitly closes it, at which point the message grid expands.
            var scanContents = new DockPanel();
            var scanHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _scanCardClose = new Button
            {
                Content = "×", Width = 28, Height = 26, IsEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "扫描结束后可关闭此进度卡片，不会删除扫描记录或数据包。"
            };
            _scanCardClose.Click += delegate
            {
                if (_scanCancellation == null) _root.Children.Remove(_scanCard);
            };
            DockPanel.SetDock(_scanCardClose, Dock.Right);
            scanHeader.Children.Add(_scanCardClose);
            _scanCardTitle = new TextBlock
            {
                Text = "历史消息扫描", FontWeight = FontWeights.SemiBold,
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center
            };
            scanHeader.Children.Add(_scanCardTitle);
            DockPanel.SetDock(scanHeader, Dock.Top);
            scanContents.Children.Add(scanHeader);
            _scanProgress = new ProgressBar
            {
                Height = 14, Minimum = 0, Maximum = 100, Value = 0,
                ToolTip = "扫描进度 = 已处理联系人 / 总联系人；重试阶段单独显示。"
            };
            DockPanel.SetDock(_scanProgress, Dock.Top);
            scanContents.Children.Add(_scanProgress);
            _scanLog = new TextBox
            {
                Height = 80, IsReadOnly = true, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
                ToolTip = "仅显示匿名进度事件。完整日志和脱敏结果存储在扫描数据包中。"
            };
            scanContents.Children.Add(_scanLog);
            _scanCard = new Border
            {
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10),
                Background = Brushes.White, Margin = new Thickness(0, 0, 0, 8),
                Child = scanContents
            };
            DockPanel.SetDock(_scanCard, Dock.Top);
            // Intentionally not added to root here: never reserve empty space.

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
            // Exact groups render as plain text without an expansion icon.
            var plain = new FrameworkElementFactory(typeof(TextBlock));
            plain.SetBinding(TextBlock.TextProperty, new Binding("Text"));
            plain.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text"));
            plain.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            plain.SetValue(TextBlock.MarginProperty, new Thickness(5, 6, 5, 6));

            // Semantic groups render distinct wordings and their occurrence counts.
            var expander = new FrameworkElementFactory(typeof(Expander));
            expander.SetValue(Expander.IsExpandedProperty, false);
            expander.SetBinding(Expander.HeaderProperty, new Binding("Text"));
            expander.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 4, 5, 4));
            expander.SetValue(FrameworkElement.ToolTipProperty, "点击展开 AI 合并的不同问法及各自接收次数");
            var details = new FrameworkElementFactory(typeof(ListBox));
            details.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Details"));
            details.SetValue(FrameworkElement.MaxHeightProperty, 235.0);
            details.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            details.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            details.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            expander.AppendChild(details);
            _list.Columns.Add(new DataGridTemplateColumn
            {
                Header = "买家消息内容",
                CellTemplateSelector = new BuyerMessageContentSelector
                {
                    ExactTemplate = new DataTemplate { VisualTree = plain },
                    SemanticTemplate = new DataTemplate { VisualTree = expander }
                },
                SortMemberPath = "Text", MinWidth = 280,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            var answerFactory = new FrameworkElementFactory(typeof(TextBlock));
            answerFactory.SetBinding(TextBlock.TextProperty, new Binding("DirectAnswer"));
            answerFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("DirectAnswer"));
            answerFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            answerFactory.SetValue(TextBlock.MarginProperty, new Thickness(5, 5, 5, 5));
            answerFactory.AddHandler(UIElement.PreviewMouseRightButtonDownEvent,
                new System.Windows.Input.MouseButtonEventHandler((sender, e) =>
                {
                    var target = sender as FrameworkElement;
                    var item = target == null ? null : target.DataContext as BuyerMessageListItem;
                    if (item != null)
                    {
                        _list.SelectedItem = item;
                        var menu = new ContextMenu();
                        var edit = new MenuItem { Header = "修改直答" };
                        edit.Click += delegate { EditDirectAnswer(item); };
                        menu.Items.Add(edit);
                        var rollback = new MenuItem
                        {
                            Header = "回滚本次直答更新",
                            IsEnabled = item.AppliedRevision != null
                        };
                        rollback.Click += delegate { RollbackDirectAnswer(item); };
                        menu.Items.Add(rollback);
                        target.ContextMenu = menu;
                    }
                }));
            _list.Columns.Add(new DataGridTemplateColumn
            {
                Header = "直答消息（右键修改或回滚）",
                CellTemplate = new DataTemplate { VisualTree = answerFactory },
                MinWidth = 260,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _list.Columns.Add(new DataGridTextColumn
            {
                Header = "接收次数", Binding = new Binding("GroupCount"),
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
                var knowledge = KnowledgeEngineV2Repository.LoadAll(_seller);
                var applied = BuyerMessageArchiveService.ReadOptimizations(_seller)
                    .Where(x => x.Status == "applied").OrderByDescending(x => x.AppliedAtTicks)
                    .GroupBy(x => x.GroupKey).ToDictionary(x => x.Key, x => x.First());
                // Exact duplicate events are counted but never repeated visually.
                // If AI groups multiple different wordings, expose only the
                // distinct wordings on expansion (not receipt timestamps).
                var rows = grouped.Select(g =>
                {
                    var wordings = g.Messages.GroupBy(x => x.Text ?? string.Empty,
                        StringComparer.Ordinal).OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                    var match = BuyerMessageArchiveService.FindConfirmedDirect(_seller,
                        wordings.Select(x => x.Key), knowledge);
                    BuyerAiOptimizationRow revision;
                    applied.TryGetValue(g.Key, out revision);
                    if (revision != null)
                    {
                        var record = knowledge.FirstOrDefault(x => x.Id == revision.KnowledgeId);
                        if (record == null || record.Answer != revision.AppliedAnswer
                            || !record.Enabled || record.Status != "active")
                            revision = null;
                    }
                    return new BuyerMessageListItem
                    {
                        // Show an explicitly approved answer associated with
                        // this group, even if the authored full question differs
                        // from the original shorthand. Runtime reply still uses
                        // the normal context and eligibility checks.
                        DirectAnswer = match != null ? match.Answer
                            : revision != null ? revision.AppliedAnswer : "",
                        DirectKnowledgeId = match != null ? match.Id
                            : revision != null ? revision.KnowledgeId : "",
                        AppliedRevision = revision,
                        Message = g.Messages.First(),
                        ReceivedAt = g.Latest,
                        Text = wordings.Count == 1 ? wordings[0].Key : g.Title,
                        GroupKey = g.Key, GroupTitle = g.Title,
                        GroupCount = g.Count,
                        IsSemanticMerged = wordings.Count > 1,
                        Messages = g.Messages,
                        Details = wordings.Select(x => x.Key + "（接收" + x.Count() + "次）").ToList()
                    };
                }).OrderByDescending(x => x.ReceivedAt)
                    .ThenBy(x => x.Message.Id, StringComparer.Ordinal).ToList();
                _list.ItemsSource = rows;
                _status.Text = "当前筛选结果：" + grouped.Sum(x => x.Count) + "次接收，"
                    + rows.Count + "条去重/语义分组；其中 "
                    + rows.Count(x => x.IsSemanticMerged) + "组包含不同问法；"
                    + rows.Count(x => !string.IsNullOrWhiteSpace(x.DirectAnswer))
                    + "组命中已审核直答，直答单元格可右键操作。";
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
                Text = "接收次数：" + item.GroupCount
                    + "    直答：" + (string.IsNullOrWhiteSpace(item.DirectAnswer) ? "未匹配" : "已匹配"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
            panel.Children.Add(new TextBox
            {
                Text = item.IsSemanticMerged ? string.Join(Environment.NewLine, item.Details)
                    : (item.Text ?? ""), IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(8)
            });
            w.Content = panel; w.ShowDialog();
        }

        private void UpdateScanProgress(ChatHistoryScanProgress p)
        {
            if (p == null || _scanCancellation == null) return;
            var count = Math.Max(1, p.ContactCount);
            _scanProgress.Value = Math.Min(100,
                Math.Round(100.0 * Math.Min(p.ContactIndex, count) / count));
            _status.Text = p.Phase + " · " + p.ContactIndex + "/" + p.ContactCount
                + " · 已读消息 " + p.MessageCount
                + " · 失败 " + p.FailedCount
                + " · 会话切换 " + p.InterruptionCount
                + " · 重试 " + p.RetryCount;
            var key = p.Phase + "|" + p.ContactIndex + "|" + p.ContactCount + "|"
                + p.RetryCount + "|" + p.MessageCount;
            if (key == _lastScanProgressKey) return;
            _lastScanProgressKey = key;
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + _status.Text;
            _scanLog.AppendText(line + Environment.NewLine);
            if (_scanLog.Text.Length > 12000) _scanLog.Text = _scanLog.Text.Substring(
                _scanLog.Text.Length - 8000);
            _scanLog.ScrollToEnd();
        }

        private async Task ScanHistoryAsync()
        {
            if (_scanCancellation != null) return;
            BuyerHistoryScanResumeInfo previous;
            try { previous = BuyerHistoryScanCheckpointStore.Latest(_seller); }
            catch (Exception ex)
            {
                _status.Text = "无法读取历史扫描断点：" + ex.Message
                    + "。请检查本店的断点记录，避免无意覆盖未完成数据。";
                return;
            }
            string resumeId = null;
            if (previous != null)
            {
                var choice = MessageBox.Show(Window.GetWindow(this),
                    "找到本店上次扫描任务：" + previous.Id
                    + "\n状态：" + previous.State
                    + "\n已完成：" + previous.Completed + "/" + previous.Total
                    + " 位联系人。\n\n“是”：从断点续扫，跳过已成功的买家，补扫未完成者并发现新买家。"
                    + "\n“否”：开始新扫描（上次断点和日志保留）。"
                    + "\n“取消”：不执行。",
                    "历史消息扫描 · 断点续扫",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) resumeId = previous.Id;
            }
            else if (MessageBox.Show(Window.GetWindow(this),
                "开始扫描本店可访问的历史消息？每成功读取一位买家都将加密保存断点。"
                + "任务取消、客户端退出或异常中断后可继续，正常接待与自动回复仍优先。",
                "扫描历史买家消息", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            // Attach the existing card only when a scan really begins, not on
            // initial page load or when browsing saved scan records.
            if (!_root.Children.Contains(_scanCard))
                _root.Children.Insert(_root.Children.IndexOf(_list), _scanCard);
            _scanCancellation = new CancellationTokenSource();
            var generation = ++_scanGeneration;
            _scanButton.IsEnabled = false;
            _stopScan.IsEnabled = true;
            _scanCardClose.IsEnabled = false;
            _scanCardTitle.Text = resumeId == null
                ? "历史消息扫描 · 正在进行" : "历史消息扫描 · 断点续扫中";
            _scanProgress.Value = resumeId != null && previous != null && previous.Total > 0
                ? Math.Round(100.0 * previous.Completed / previous.Total) : 0;
            _scanLog.Text = resumeId == null ? ""
                : "继续断点任务 " + resumeId + "；已完成 "
                    + previous.Completed + "/" + previous.Total + " 位买家将跳过。"
                    + Environment.NewLine;
            _lastScanProgressKey = null;
            try
            {
                _status.Text = "正在扫描本店可访问的历史消息...";
                var scan = await new ChatHistoryScanService().ScanBuyerMessagesAsync(
                    _seller, new ChatHistoryScanOptions
                    {
                        ScanAll = true, MaxContacts = 5000,
                        ResumeCheckpointId = resumeId
                    },
                    p => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // A queued callback from a previous run must not alter
                        // a newer scan card after a new scan has started.
                        if (generation == _scanGeneration) UpdateScanProgress(p);
                    })),
                    _scanCancellation.Token);
                RefreshView();
                _scanProgress.Value = 100;
                _scanCardTitle.Text = scan.FailedContacts == 0 && scan.ContactCoverageVerified
                    ? "历史消息扫描 · 已完成" : "历史消息扫描 · 覆盖待核实";
                _status.Text = "历史扫描结束：成功 " + scan.ScannedContacts + "/"
                    + scan.ContactCount + " 位联系人（跳过上次已完成 "
                    + scan.PreviouslyCompletedContacts + " 位）；累计消息 " + scan.MessageCount
                    + "；失败 " + scan.FailedContacts
                    + "；会话切换 " + scan.InterruptionCount
                    + "；重试 " + scan.RetryCount
                    + "。数据包已保存在店铺扫描记录中。"
                    + " " + scan.CoverageWarning;
                _scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + _status.Text
                    + Environment.NewLine + "本轮任务 " + scan.RunId
                    + Environment.NewLine + "断点任务 " + scan.ResumeCheckpointId
                    + Environment.NewLine + "ZIP：" + scan.PackagePath + Environment.NewLine);
                _scanLog.ScrollToEnd();
            }
            catch (OperationCanceledException)
            {
                _scanCardTitle.Text = "历史消息扫描 · 已取消";
                _status.Text = "扫描已取消，已完成的消息和部分数据包保留；可点击“扫描记录 / 数据包”查看。";
                _scanLog.AppendText(_status.Text + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _scanCardTitle.Text = "历史消息扫描 · 失败";
                _status.Text = "扫描失败：" + ex.Message + "；请到扫描记录查看已保存部分。";
                _scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + _status.Text
                    + Environment.NewLine);
            }
            finally
            {
                _scanCancellation.Dispose();
                _scanCancellation = null;
                _scanButton.IsEnabled = true;
                _stopScan.IsEnabled = false;
                // Keep the finished card until the user clicks ×.
                // Closing only removes the visual; persisted logs stay intact.
                _scanCardClose.IsEnabled = true;
            }
        }

        private void ShowScanRecords()
        {
            IList<BuyerHistoryScanEvidence> data;
            try { data = BuyerHistoryScanEvidenceSession.List(_seller); }
            catch (Exception ex)
            {
                _status.Text = "无法读取本店历史扫描记录：" + ex.Message; return;
            }
            var w = new Window
            {
                Owner = Window.GetWindow(this),
                Title = "扫描记录与结果数据包（仅当前店铺）",
                Width = 900, Height = 490, MinWidth = 640, MinHeight = 320,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var dock = new DockPanel { Margin = new Thickness(12) };
            var note = new TextBlock
            {
                Text = "扫描记录存放在本店日志目录；每次任务均生成 manifest.json、events.jsonl、"
                    + "messages-redacted.jsonl 及 ZIP。不主动保存买家昵称和原始消息 Key，"
                    + "并对常见敏感信息脱敏；文本仍可能含个人信息，请仅交给授权人员。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top); dock.Children.Add(note);
            var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            var export = Button("导出选中数据包", 155);
            var refresh = Button("刷新记录", 90);
            var updateMessages = Button("更新消息", 100);
            updateMessages.ToolTip = "合并本店所有扫描记录的消息，按原消息时间和内容去重后更新买家消息列表";
            var close = Button("关闭", 75);
            buttons.Children.Add(export);
            buttons.Children.Add(refresh);
            buttons.Children.Add(updateMessages);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
            var list = new DataGrid
            {
                ItemsSource = data, AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single
            };
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "开始时间（UTC）", Binding = new Binding("StartedAt"),
                Width = new DataGridLength(190)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "状态", Binding = new Binding("Status"), Width = new DataGridLength(85)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "联系人", Binding = new Binding("ContactCount"),
                Width = new DataGridLength(85)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "成功", Binding = new Binding("CompletedContacts"),
                Width = new DataGridLength(65)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "失败", Binding = new Binding("FailedContacts"),
                Width = new DataGridLength(65)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "消息数", Binding = new Binding("ReadMessages"),
                Width = new DataGridLength(75)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "切换", Binding = new Binding("InterruptedContacts"),
                Width = new DataGridLength(65)
            });
            list.Columns.Add(new DataGridTextColumn
            {
                Header = "重试", Binding = new Binding("RetryAttempts"),
                Width = new DataGridLength(65)
            });
            dock.Children.Add(list);
            refresh.Click += delegate { list.ItemsSource = BuyerHistoryScanEvidenceSession.List(_seller); };
            close.Click += delegate { w.Close(); };
            updateMessages.Click += async delegate
            {
                if (!updateMessages.IsEnabled) return;
                if (_scanCancellation != null || ChatHistoryScanService.IsBuyerScanActive(_seller))
                {
                    MessageBox.Show(w, "历史消息扫描正在进行，请完成或取消扫描后再更新消息。",
                        "更新消息", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(w,
                    "将读取当前店铺全部历史扫描记录，包括已完成、部分完成和已取消任务中保存的消息。"
                    + "先合并相同消息事件，再与买家消息库去重，仅新增缺失的消息。"
                    + "不会修改原扫描数据包、直答知识或已发送的 Bot 回复。是否更新？",
                    "确认更新买家消息", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                updateMessages.IsEnabled = false;
                refresh.IsEnabled = false;
                try
                {
                    _status.Text = "正在合并扫描记录并去重更新买家消息...";
                    var result = await System.Threading.Tasks.Task.Run(() =>
                        BuyerHistoryScanMessageMergeService.MergeAll(_seller));
                    RefreshView();
                    var summary = "已读取扫描任务 " + result.ScansRead
                        + " 个，原始消息 " + result.RowsRead
                        + " 条；跨扫描重复 " + result.DuplicateInScans
                        + " 条，消息库已有 " + result.AlreadyArchived
                        + " 条，无效 " + result.InvalidRows
                        + " 条；新增入库 " + result.NewlyArchived + " 条。"
                        + (result.ScansFailed > 0
                            ? "\n有 " + result.ScansFailed
                                + " 个数据包读取失败，请查看运行日志。"
                            : "");
                    _status.Text = "更新消息完成：新增 " + result.NewlyArchived
                        + " 条，扫描记录 " + result.ScansRead + " 个。";
                    MessageBox.Show(w, summary, "更新消息结果",
                        MessageBoxButton.OK,
                        result.ScansFailed > 0 ? MessageBoxImage.Warning
                            : MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _status.Text = "更新扫描消息失败：" + ex.GetType().Name;
                    MessageBox.Show(w, "更新消息失败：" + ex.Message
                        + "\n已成功入库的消息再次运行会自动去重。",
                        "更新消息", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    updateMessages.IsEnabled = true;
                    refresh.IsEnabled = true;
                }
            };
            export.Click += delegate
            {
                var selected = list.SelectedItem as BuyerHistoryScanEvidence;
                if (selected == null || string.IsNullOrWhiteSpace(selected.PackagePath)
                    || !File.Exists(selected.PackagePath))
                {
                    MessageBox.Show(w, "请选择已经生成 ZIP 的扫描记录；未完成的任务可稍后刷新。");
                    return;
                }
                var dialog = new SaveFileDialog
                {
                    Title = "导出历史消息扫描诊断数据包",
                    Filter = "ZIP 数据包 (*.zip)|*.zip",
                    FileName = selected.RunId + ".zip",
                    AddExtension = true, DefaultExt = ".zip"
                };
                if (dialog.ShowDialog(w) != true) return;
                if (string.Equals(Path.GetFullPath(dialog.FileName),
                    Path.GetFullPath(selected.PackagePath), StringComparison.OrdinalIgnoreCase))
                    return;
                try
                {
                    File.Copy(selected.PackagePath, dialog.FileName, true);
                    MessageBox.Show(w, "数据包已导出。");
                }
                catch (Exception ex) { MessageBox.Show(w, "导出失败：" + ex.Message); }
            };
            w.Content = dock; w.ShowDialog();
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
            // Selection now refers to collapsed groups; never silently delete
            // only the representative row while claiming the group was deleted.
            var ids = _list.SelectedItems.OfType<BuyerMessageListItem>()
                .SelectMany(x => x.Messages.Select(m => m.Id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) { _status.Text = "请先选中要删除的消息"; return; }
            if (MessageBox.Show(Window.GetWindow(this),
                "确认从本店匿名归档中删除所选分组包含的 " + ids.Length
                    + " 条原始消息？不会更改原千牛聊天记录。",
                "删除选中消息", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { BuyerMessageArchiveService.Delete(_seller, ids); RefreshView(); }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }

        private void EditDirectAnswer(BuyerMessageListItem item)
        {
            if (item == null) return;
            var editable = BuyerMessageArchiveService.FindConfirmedDirect(_seller,
                item.Messages.Select(x => x.Text));
            if (editable == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "这条消息没有唯一的已审核直答。请先使用“AI分析优化”生成有知识库证据的候选，"
                    + "在审核窗口编辑后确认写入；不会直接创建无条件答案。",
                    "暂无可修改直答", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var text = Prompt(Window.GetWindow(this), "修改直答（人工审核）", editable.Answer);
            if (text == null || text == editable.Answer) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "确认修改当前店铺已审核的直答？\n问题：" + editable.Title
                    + "\n原答案：" + editable.Answer + "\n新答案：" + text
                    + "\n此操作将写入生产知识库，原版本可回滚。",
                "确认修改直答", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var draft = BuyerMessageArchiveService.NewManualDraft(_seller, item.GroupKey,
                    editable.Title, item.GroupCount, editable.Answer, editable.Id, text);
                BuyerMessageArchiveService.Apply(_seller, draft);
                RefreshView();
            }
            catch (Exception ex) { _status.Text = "修改直答失败：" + ex.Message; }
        }

        private void RollbackDirectAnswer(BuyerMessageListItem item)
        {
            if (item == null || item.AppliedRevision == null) return;
            if (MessageBox.Show(Window.GetWindow(this),
                "确定回滚这条消息最近一次已审核直答修改？如果知识库之后被其他人修改，"
                + "系统将拒绝回滚以保护新版本。",
                "确认回滚", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                _status.Text = BuyerMessageArchiveService.Rollback(_seller, item.AppliedRevision);
                RefreshView();
            }
            catch (Exception ex) { _status.Text = "回滚失败：" + ex.Message; }
        }
    }

    internal sealed class BuyerDirectReviewSelection
    {
        public bool Selected { get; set; }
        public BuyerAiOptimizationRow Candidate { get; set; }
        public string Question { get { return Candidate == null ? "" : Candidate.Question; } }
        public string ProposedAnswer { get { return Candidate == null ? "" : Candidate.ProposedAnswer; } }
        public string ExistingAnswer { get { return Candidate == null ? "" : Candidate.ExistingAnswer; } }
        public int Frequency { get { return Candidate == null ? 0 : Candidate.Frequency; } }
    }

    internal sealed class BuyerQuestionOptimizationWindow : Window
    {
        private readonly string _seller;
        private readonly TextBox _days, _top, _question, _answer, _reason;
        private readonly DataGrid _list;
        private readonly TextBlock _status;
        private CancellationTokenSource _cancel;
        private BuyerAiOptimizationRow _selected;

        internal BuyerQuestionOptimizationWindow(Window owner, string seller, bool autoRun = false)
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
                Text = "不再分析分类；基于已有知识记录生成待审核直答。先在预览中勾选需更新项，"
                    + "确认后才会写入正式知识；无可靠知识依据的建议不可应用。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
            });
            DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
            controls.Children.Add(new TextBlock { Text = "时间范围（天）", VerticalAlignment = VerticalAlignment.Center });
            _days = new TextBox { Width = 70, Height = 28, Text = "30", Margin = new Thickness(5, 0, 10, 0) };
            controls.Children.Add(_days);
            controls.Children.Add(new TextBlock { Text = "前N条", VerticalAlignment = VerticalAlignment.Center });
            _top = new TextBox { Width = 65, Height = 28, Text = "10", Margin = new Thickness(5, 0, 10, 0) };
            controls.Children.Add(_top);
            var refresh = new Button { Content = "刷新记录", Width = 85, Height = 29, Margin = new Thickness(8, 0, 0, 0) };
            refresh.Click += delegate { RefreshRecords(); }; controls.Children.Add(refresh);
            var preview = new Button { Content = "预览并确认更新项", Width = 145, Height = 29,
                Margin = new Thickness(8, 0, 0, 0) };
            preview.Click += delegate { PreviewPending(null); };
            controls.Children.Add(preview);
            DockPanel.SetDock(controls, Dock.Top); layout.Children.Add(controls);
            _status = new TextBlock { Text = "打开后生成待审核直答建议；不会对买家消息进行分类。",
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
            Loaded += async delegate
            {
                RefreshRecords();
                if (autoRun) await GenerateAsync();
            };
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
                "AI分析优化", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _cancel = new CancellationTokenSource();
            try
            {
                var count = await BuyerMessageArchiveService.OptimizeTop(_seller,
                    Number(_days, 30, 3650), Number(_top, 10, 100),
                    value => Dispatcher.BeginInvoke(new Action(() => _status.Text = value)), _cancel.Token);
                RefreshRecords();
                _status.Text = "已生成 " + count + " 条候选；请在预览中核查并勾选更新项。";
                var latest = BuyerMessageArchiveService.ReadOptimizations(_seller).Take(count).ToList();
                PreviewPending(latest);
            }
            catch (OperationCanceledException) { _status.Text = "已取消AI分析优化。"; }
            catch (Exception ex) { _status.Text = "AI分析优化失败：" + ex.Message; }
            finally { _cancel.Dispose(); _cancel = null; }
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
        private void PreviewPending(IEnumerable<BuyerAiOptimizationRow> generated)
        {
            var entries = (generated ?? BuyerMessageArchiveService.ReadOptimizations(_seller))
                .Where(x => x.Status == "pending" && !string.IsNullOrWhiteSpace(x.ProposedAnswer)
                    && !string.IsNullOrWhiteSpace(x.KnowledgeId))
                .GroupBy(x => x.GroupKey).Select(x => x.OrderByDescending(y => y.CreatedAtTicks).First())
                .ToList();
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "没有可应用的候选：只有存在明确知识库证据的非空答案才进入审核。"
                    + " 可以选择优化记录，查看原因和关联知识后编辑草稿。",
                    "直答更新预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var w = new Window
            {
                Owner = this, Title = "直答更新预览 · 人工确认",
                Width = 1020, Height = 620, MinWidth = 740, MinHeight = 400,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var root = new DockPanel { Margin = new Thickness(12) };
            var note = new TextBlock
            {
                Text = "下列均为 AI 建议，不会自动更新。请逐项核对问题、已有知识和拟写答案，"
                    + "勾选确认项后应用。来源知识缺失、内容变更、语义不完整或高风险会被服务端再次拒绝。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var apply = new Button { Content = "确认更新选中项", Width = 150, Height = 32,
                Margin = new Thickness(0, 0, 10, 0) };
            var cancel = new Button { Content = "取消，不更新", Width = 130, Height = 32 };
            buttons.Children.Add(apply); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            var rows = entries.Select(x => new BuyerDirectReviewSelection { Candidate = x }).ToList();
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, RowHeaderWidth = 0 };
            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "更新",
                Binding = new Binding("Selected") { Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = 60
            });
            grid.Columns.Add(new DataGridTextColumn { Header = "次数", Width = 65,
                Binding = new Binding("Frequency"), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "问题", Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                Binding = new Binding("Question"), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "知识库原答案", Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                Binding = new Binding("ExistingAnswer"), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "拟更新直答", Width = new DataGridLength(3, DataGridLengthUnitType.Star),
                Binding = new Binding("ProposedAnswer"), IsReadOnly = true });
            root.Children.Add(grid);
            cancel.Click += delegate { w.DialogResult = false; };
            apply.Click += delegate
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                var selected = rows.Where(x => x.Selected).Select(x => x.Candidate).ToList();
                if (selected.Count == 0)
                {
                    MessageBox.Show(w, "请勾选要更新的直答项。", "预览", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                var changes = string.Join(Environment.NewLine,
                    selected.Take(15).Select(x => "问题：" + x.Question + " → " + x.ProposedAnswer));
                if (MessageBox.Show(w, "确认将以下 " + selected.Count
                    + " 项写入当前店铺生产知识库？" + Environment.NewLine + changes
                    + (selected.Count > 15 ? Environment.NewLine + "（更多项请在表格中查看）" : ""),
                    "最后确认 · 更新直答", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                int success = 0; var failed = new List<string>();
                foreach (var item in selected)
                {
                    try { BuyerMessageArchiveService.Apply(_seller, item); success++; }
                    catch (Exception ex) { failed.Add(item.Question + "：" + ex.Message); }
                }
                RefreshRecords();
                _status.Text = "人工审核更新成功 " + success + " 项，失败 "
                    + failed.Count + " 项。";
                MessageBox.Show(w, _status.Text
                    + (failed.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, failed.Take(10))),
                    "直答更新结果", MessageBoxButton.OK,
                    failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                w.DialogResult = true;
            };
            w.Content = root;
            w.ShowDialog();
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
