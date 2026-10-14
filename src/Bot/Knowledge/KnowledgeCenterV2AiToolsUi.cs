using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2SmartImportPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private TextBox _text;
        private TextBox _timeout;
        private TextBlock _summary;
        private TextBlock _status;
        private ListBox _media;
        private Button _start;
        private Button _cancel;
        private ClipboardKnowledgeData _data;
        private CancellationTokenSource _cts;
        private SmartImportCancelSource _cancelSource;

        public KnowledgeV2SmartImportPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            _data = new ClipboardKnowledgeData();
            Build();
            PreviewKeyDown += OnKeyDown;
            Unloaded += delegate
            {
                if (_cts == null) return;
                _cancelSource = SmartImportCancelSource.WindowClosed;
                _cts.Cancel();
            };
        }

        public void RefreshView()
        {
            RefreshSummary();
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "智能导入",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "沿用旧版智能导入的文字/图片 AI 整理能力，但结果直接去重后写入 Knowledge Center V2。旧版知识库不会作为本功能的数据源。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 4, 0, 0)
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var read = Button("从剪贴板读取", 120);
            read.Click += delegate { ReadClipboard(); };
            var clear = Button("清空", 70);
            clear.Click += delegate { Clear(); };
            buttons.Children.Add(read);
            buttons.Children.Add(clear);
            buttons.Children.Add(new TextBlock
            {
                Text = "AI分析超时：",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0)
            });
            _timeout = new TextBox
            {
                Width = 58,
                Height = 28,
                Text = BotFeatureStore.GetSmartImportTimeoutSeconds().ToString()
            };
            buttons.Children.Add(_timeout);
            buttons.Children.Add(new TextBlock
            {
                Text = " 秒",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0)
            });
            _start = Button("开始智能导入", 130);
            _start.Click += async delegate { await StartImport(); };
            buttons.Children.Add(_start);
            _cancel = Button("取消", 70);
            _cancel.IsEnabled = false;
            _cancel.Click += delegate
            {
                _cancelSource = SmartImportCancelSource.UserCancel;
                if (_cts != null) _cts.Cancel();
            };
            buttons.Children.Add(_cancel);
            DockPanel.SetDock(buttons, Dock.Top);
            root.Children.Add(buttons);

            _summary = new TextBlock
            {
                Text = "已识别：文字：0 字，图片：0 张，视频：0 个",
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.Bold
            };
            DockPanel.SetDock(_summary, Dock.Top);
            root.Children.Add(_summary);

            _status = new TextBlock
            {
                Foreground = Brushes.RoyalBlue,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(grid);

            _text = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Microsoft YaHei"),
                AllowDrop = true
            };
            _text.TextChanged += delegate { RefreshSummary(); };
            Grid.SetColumn(_text, 0);
            grid.Children.Add(_text);

            var side = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(side, 1);
            grid.Children.Add(side);
            var sideTitle = new TextBlock
            {
                Text = "媒体列表（选中后可删除）",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(sideTitle, Dock.Top);
            side.Children.Add(sideTitle);
            var del = Button("删除选中媒体", 120);
            del.Click += delegate { DeleteSelected(); };
            DockPanel.SetDock(del, Dock.Bottom);
            side.Children.Add(del);
            _media = new ListBox { Margin = new Thickness(0, 6, 0, 8) };
            side.Children.Add(_media);
        }

        private static Button Button(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
            {
                ReadClipboard();
                e.Handled = true;
            }
        }

        private void ReadClipboard()
        {
            try
            {
                _status.Text = "正在读取剪贴板...";
                _data = KnowledgeClipboardParser.ReadClipboard();
                _text.Text = _data.Text;
                RefreshSummary();
                _status.Text = "剪贴板读取完成，尚未写入新版知识库。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "读取剪贴板失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Clear()
        {
            _data = new ClipboardKnowledgeData();
            _text.Clear();
            RefreshSummary();
            _status.Text = "已清空。";
        }

        private void RefreshSummary()
        {
            if (_data == null) _data = new ClipboardKnowledgeData();
            if (_text != null) _data.Text = _text.Text;
            if (_summary != null)
            {
                _summary.Text = string.Format("已识别：文字：{0:N0} 字，图片：{1} 张，视频：{2} 个",
                    (_data.Text ?? string.Empty).Length,
                    _data.Images == null ? 0 : _data.Images.Count,
                    _data.Videos == null ? 0 : _data.Videos.Count);
            }
            if (_media != null)
            {
                var list = new ObservableCollection<KnowledgeMediaItem>();
                if (_data.Images != null) foreach (var item in _data.Images) list.Add(item);
                if (_data.Videos != null) foreach (var item in _data.Videos) list.Add(item);
                _media.ItemsSource = list;
            }
        }

        private void DeleteSelected()
        {
            var item = _media == null ? null : _media.SelectedItem as KnowledgeMediaItem;
            if (item == null || _data == null) return;
            if (_data.Images != null) _data.Images.Remove(item);
            if (_data.Videos != null) _data.Videos.Remove(item);
            RefreshSummary();
        }

        private bool ConfirmSkipVideo()
        {
            var count = _data == null || _data.Videos == null ? 0 : _data.Videos.Count;
            return MessageBox.Show(
                "检测到 " + count + " 个视频文件。\n\n当前 AI 接口不支持直接视频理解。是否跳过视频，仅分析文字和图片并写入新版知识库？",
                "检测到视频", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private async Task StartImport()
        {
            RefreshSummary();
            if (string.IsNullOrWhiteSpace(_seller))
            {
                MessageBox.Show("当前未识别店铺，不能执行智能导入。", "智能导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int timeout;
            if (!int.TryParse(_timeout.Text, out timeout)) timeout = 600;
            timeout = KnowledgeAiService.ClampTimeout(timeout);
            _timeout.Text = timeout.ToString();
            BotFeatureStore.SaveSmartImportTimeoutSeconds(timeout);
            if (_data == null || !_data.HasAnalyzableContent)
            {
                MessageBox.Show("没有检测到可导入的文字、图片或媒体内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var service = new KnowledgeV2SmartImportService();
            if (_data.Videos != null && _data.Videos.Count > 0 && !service.SupportsDirectVideo && !ConfirmSkipVideo()) return;
            if (_cts != null)
            {
                _cancelSource = SmartImportCancelSource.ReplacedByNewTask;
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = new CancellationTokenSource();
            _cancelSource = SmartImportCancelSource.None;
            _start.IsEnabled = false;
            _cancel.IsEnabled = true;

            try
            {
                var result = await service.ImportAsync(_seller, _data, timeout, _cts.Token,
                    () => _cancelSource,
                    progressText => Dispatcher.Invoke(() => _status.Text = progressText));
                _status.Text = "智能导入完成，结果已写入 Knowledge Center V2。";
                var resultMessage = string.Format(
                    "新版知识库智能导入成功\n\n本次分析：\n文字：{0:N0} 字\n图片：{1} 张\n跳过视频：{2} 个\n\nAI生成问答：{3} 条\n成功写入V2：{4} 条\n重复跳过：{5} 条",
                    result.TextChars, result.ImageCount, result.VideoSkipped, result.AiGenerated,
                    result.Added, result.DuplicateSkipped);
                if (result.UnsupportedImageSkipped > 0)
                    resultMessage += "\n\n有 " + result.UnsupportedImageSkipped + " 张图片因当前 AI 接口不支持图片理解而未参与分析。";
                resultMessage += "\n\n本次操作已经写入“AI优化记录”。";
                MessageBox.Show(resultMessage, "智能导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (SmartImportException ex)
            {
                if (ex.Source != SmartImportCancelSource.WindowClosed)
                    MessageBox.Show(ex.Message, "智能导入停止", MessageBoxButton.OK, MessageBoxImage.Warning);
                _status.Text = ex.Message;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "智能导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                _status.Text = "导入失败：" + ex.Message;
            }
            finally
            {
                _start.IsEnabled = true;
                _cancel.IsEnabled = false;
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }
    }

    internal sealed class KnowledgeV2AiOptimizationHistoryPage : UserControl, IKnowledgeV2Refreshable
    {
        private sealed class HistoryItem
        {
            public string Time { get; set; }
            public string Type { get; set; }
            public string Target { get; set; }
            public string Summary { get; set; }
            public string Result { get; set; }
            public DateTime SortAt { get; set; }
        }

        private readonly string _seller;
        private readonly DataGrid _grid;
        private readonly ComboBox _filter;
        private readonly TextBlock _status;
        private List<KnowledgeV2GovernanceAuditEntry> _all = new List<KnowledgeV2GovernanceAuditEntry>();
        private List<ConversationSessionLearningReportView> _sessions = new List<ConversationSessionLearningReportView>();

        public KnowledgeV2AiOptimizationHistoryPage(Window owner, string seller)
        {
            _seller = seller ?? string.Empty;
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "AI优化记录",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "汇总 V2 智能导入、AI 修订和本店对话自动学习的实际状态、修改数量及失败原因；自动学习记录来自独立审计表。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 4, 0, 0)
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _filter = new ComboBox { Width = 150, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            _filter.Items.Add("全部AI记录");
            _filter.Items.Add("智能导入");
            _filter.Items.Add("AI修订优化");
            _filter.Items.Add("会话自动学习");
            _filter.SelectedIndex = 0;
            _filter.SelectionChanged += delegate { ApplyFilter(); };
            tools.Children.Add(_filter);
            var refresh = new Button { Content = "刷新", Width = 74, Height = 30 };
            refresh.Click += delegate { RefreshView(); };
            tools.Children.Add(refresh);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            _status = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_status, Dock.Top);
            root.Children.Add(_status);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "时间", Width = 145, Binding = new Binding("Time") });
            _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Width = 110, Binding = new Binding("Type") });
            _grid.Columns.Add(new DataGridTextColumn { Header = "对象", Width = 180, Binding = new Binding("Target") });
            _grid.Columns.Add(new DataGridTextColumn { Header = "结果", Width = 70, Binding = new Binding("Result") });
            _grid.Columns.Add(new DataGridTextColumn { Header = "记录", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Summary") });
            root.Children.Add(_grid);

            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _all = new List<KnowledgeV2GovernanceAuditEntry>();
                _sessions = new List<ConversationSessionLearningReportView>();
                _grid.ItemsSource = null;
                _status.Text = "当前未识别店铺，无法读取 AI 优化记录。";
                return;
            }
            try
            {
                _all = KnowledgeEngineV2GovernanceAuditService.GetEntries(_seller, 800)
                    .Where(IsAiHistory)
                    .ToList();
                _sessions = ConversationSessionLearningService.GetReports(1000)
                    .Where(x => x != null && string.Equals(x.Seller, _seller, StringComparison.Ordinal))
                    .ToList();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                _grid.ItemsSource = null;
                _status.Text = "读取 AI 优化记录失败：" + ex.Message;
            }
        }

        private void ApplyFilter()
        {
            if (_grid == null || _filter == null) return;
            var mode = _filter.SelectedItem as string ?? "全部AI记录";
            IEnumerable<KnowledgeV2GovernanceAuditEntry> source = _all;
            if (mode == "智能导入")
                source = source.Where(x => string.Equals(x.ActionType, "ai_smart_import", StringComparison.OrdinalIgnoreCase));
            else if (mode == "AI修订优化")
                source = source.Where(x => !string.Equals(x.ActionType, "ai_smart_import", StringComparison.OrdinalIgnoreCase));
            else if (mode == "会话自动学习")
                source = Enumerable.Empty<KnowledgeV2GovernanceAuditEntry>();

            var items = source.Select(x => new HistoryItem
            {
                Time = x.CreatedAtText,
                Type = ActionText(x.ActionType),
                Target = string.IsNullOrWhiteSpace(x.TargetTitle) ? "-" : x.TargetTitle,
                Summary = x.Summary ?? string.Empty,
                Result = string.Equals(x.Result, "success", StringComparison.OrdinalIgnoreCase) ? "成功" : (x.Result ?? string.Empty),
                SortAt = x.CreatedAt
            }).ToList();
            if (mode == "全部AI记录" || mode == "会话自动学习")
            {
                items.AddRange(_sessions.Select(x => new HistoryItem
                {
                    SortAt = x.CompletedAt != DateTime.MinValue ? x.CompletedAt : x.LastBuyerAt,
                    Time = (x.CompletedAt != DateTime.MinValue ? x.CompletedAt : x.LastBuyerAt).ToString("yyyy-MM-dd HH:mm:ss"),
                    Type = "会话自动学习",
                    Target = x.Buyer ?? "-",
                    Summary = (x.Summary ?? string.Empty) + " [应用 " + x.AppliedCount
                        + " / 跳过 " + x.SkippedCount + "] "
                        + (string.IsNullOrWhiteSpace(x.Error) ? string.Empty : "原因：" + x.Error),
                    Result = x.Status ?? "-"
                }));
            }
            _grid.ItemsSource = items.OrderByDescending(x => x.SortAt).Take(800).ToList();
            _status.Text = "共 " + items.Count + " 条记录（显示最近800条）；记录按本店隔离查询。";
        }

        private static bool IsAiHistory(KnowledgeV2GovernanceAuditEntry entry)
        {
            if (entry == null) return false;
            var action = (entry.ActionType ?? string.Empty).Trim().ToLowerInvariant();
            return action == "ai_smart_import"
                || action == "generate_revision_candidates"
                || action == "apply_revision"
                || action == "reject_revision"
                || action == "rollback_revision";
        }

        private static string ActionText(string action)
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ai_smart_import": return "智能导入";
                case "generate_revision_candidates": return "AI生成修订";
                case "apply_revision": return "应用修订";
                case "reject_revision": return "驳回修订";
                case "rollback_revision": return "回滚修订";
                default: return action ?? string.Empty;
            }
        }
    }
}
