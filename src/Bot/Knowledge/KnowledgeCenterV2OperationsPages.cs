using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2ConflictPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private readonly ObservableCollection<KnowledgeV2Conflict> _conflicts = new ObservableCollection<KnowledgeV2Conflict>();
        private readonly ObservableCollection<KnowledgeV2Record> _records = new ObservableCollection<KnowledgeV2Record>();
        private DataGrid _conflictGrid;
        private DataGrid _recordGrid;
        private TextBlock _status;

        public KnowledgeV2ConflictPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            _status.Text = "正在检查同一 Subject / Predicate 下的事实冲突...";
            Task.Run(() => KnowledgeEngineV2Service.GetConflicts(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _conflicts.Clear();
                    _records.Clear();
                    if (t.IsFaulted)
                    {
                        _status.Text = "冲突检查失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    foreach (var item in t.Result ?? new List<KnowledgeV2Conflict>()) _conflicts.Add(item);
                    _status.Text = _conflicts.Count == 0
                        ? "未发现事实冲突。只有相同 Subject + Predicate 的不同答案才会被视为冲突。"
                        : "发现 " + _conflicts.Count + " 组事实冲突；请选择冲突组和要保留的答案。";
                })));
        }

        private void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var refresh = Btn("重新检查", 90);
            refresh.Click += delegate { RefreshView(); };
            var keep = Btn("保留所选答案", 110);
            keep.Click += delegate { KeepSelected(); };
            var review = Btn("其余转学习候选", 124);
            review.Click += delegate { MarkOthersCandidate(); };
            header.Children.Add(refresh);
            header.Children.Add(keep);
            header.Children.Add(review);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
            Grid.SetRow(main, 1);
            root.Children.Add(main);

            _conflictGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _conflicts,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _conflictGrid.Columns.Add(new DataGridTextColumn { Header = "Subject", Binding = new Binding("Subject"), Width = 160 });
            _conflictGrid.Columns.Add(new DataGridTextColumn { Header = "Predicate", Binding = new Binding("Predicate"), Width = 150 });
            _conflictGrid.Columns.Add(new DataGridTextColumn { Header = "事实键", Binding = new Binding("FactKey"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _conflictGrid.SelectionChanged += delegate { LoadConflictRecords(); };
            main.Children.Add(_conflictGrid);

            _recordGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _records
            };
            _recordGrid.Columns.Add(new DataGridTextColumn { Header = "标题", Binding = new Binding("Title"), Width = 180 });
            _recordGrid.Columns.Add(new DataGridTextColumn { Header = "答案", Binding = new Binding("Answer"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _recordGrid.Columns.Add(new DataGridTextColumn { Header = "可信度", Binding = new Binding("ConfidenceText"), Width = 72 });
            _recordGrid.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding("SourceType"), Width = 110 });
            Grid.SetColumn(_recordGrid, 1);
            main.Children.Add(_recordGrid);

            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_status, 2);
            root.Children.Add(_status);
            Content = root;
        }

        private void LoadConflictRecords()
        {
            _records.Clear();
            var conflict = _conflictGrid.SelectedItem as KnowledgeV2Conflict;
            if (conflict == null) return;
            foreach (var record in conflict.Records.OrderByDescending(x => x.Confidence)) _records.Add(record);
            if (_records.Count > 0) _recordGrid.SelectedIndex = 0;
        }

        private void KeepSelected()
        {
            var conflict = _conflictGrid.SelectedItem as KnowledgeV2Conflict;
            var selected = _recordGrid.SelectedItem as KnowledgeV2Record;
            if (conflict == null || selected == null) return;
            if (MessageBox.Show(_owner,
                "将保留所选知识为 active，并停用同一事实键下的其他冲突知识。是否继续？",
                "解决知识冲突", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Task.Run(() =>
            {
                foreach (var record in conflict.Records)
                {
                    record.Enabled = record.Id == selected.Id;
                    record.Status = record.Id == selected.Id ? "active" : "disabled";
                    KnowledgeEngineV2Repository.Save(_seller, record);
                }
                KnowledgeEngineV2Service.Warm(_seller);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) MessageBox.Show(_owner, "解决冲突失败：" + t.Exception.GetBaseException().Message);
                RefreshView();
            })));
        }

        private void MarkOthersCandidate()
        {
            var conflict = _conflictGrid.SelectedItem as KnowledgeV2Conflict;
            var selected = _recordGrid.SelectedItem as KnowledgeV2Record;
            if (conflict == null || selected == null) return;
            Task.Run(() =>
            {
                foreach (var record in conflict.Records)
                {
                    if (record.Id == selected.Id)
                    {
                        record.Status = "active";
                        record.Enabled = true;
                    }
                    else
                    {
                        record.Status = "candidate";
                        record.Type = "learning_candidate";
                        record.Enabled = true;
                    }
                    KnowledgeEngineV2Repository.Save(_seller, record);
                }
                KnowledgeEngineV2Service.Warm(_seller);
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) MessageBox.Show(_owner, "更新冲突状态失败：" + t.Exception.GetBaseException().Message);
                RefreshView();
            })));
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }
    }

    internal sealed class KnowledgeV2DebuggerPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private TextBox _question;
        private TextBox _result;
        private TextBlock _status;

        public KnowledgeV2DebuggerPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            Build();
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            Task.Run(() => KnowledgeEngineV2Service.Warm(_seller));
            _status.Text = "索引预热已提交。测试检索不会触发定时全量重建。";
        }

        private void Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock { Text = "知识调试台", FontSize = 20, FontWeight = FontWeights.SemiBold });
            _status = new TextBlock
            {
                Text = "显示消息理解 → 候选召回 → 精排 → 决策的独立耗时；目标：热查询 P95 ≤ 50ms。",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 4, 0, 0)
            };
            header.Children.Add(_status);
            root.Children.Add(header);

            var query = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            query.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            query.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            query.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _question = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Text = "这个是电视机酷狗音乐会员吗？" };
            query.Children.Add(_question);
            var test = new Button { Content = "测试检索", Width = 90, Height = 34, Margin = new Thickness(8, 0, 0, 0) };
            test.Click += async delegate { await RunOneAsync(); };
            Grid.SetColumn(test, 1);
            query.Children.Add(test);
            var bench = new Button { Content = "30次性能测试", Width = 110, Height = 34, Margin = new Thickness(8, 0, 0, 0) };
            bench.Click += async delegate { await RunBenchmarkAsync(); };
            Grid.SetColumn(bench, 2);
            query.Children.Add(bench);
            Grid.SetRow(query, 1);
            root.Children.Add(query);

            _result = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas")
            };
            Grid.SetRow(_result, 2);
            root.Children.Add(_result);
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "客服对话沙盒", Content = new KnowledgeV2SandboxConversation(_seller) });
            tabs.Items.Add(new TabItem { Header = "知识检索与性能", Content = root });
            Content = tabs;
        }

        private async Task RunOneAsync()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            _result.Text = "正在测试...";
            try
            {
                var message = _question.Text ?? string.Empty;
                var buyer = "__knowledge_v2_debug__" + Guid.NewGuid().ToString("N");
                var decision = await Task.Run(() => KnowledgeEngineV2Service.Resolve(_seller, buyer, message));
                _result.Text = Format(decision);
            }
            catch (Exception ex) { _result.Text = "测试失败：" + ex.Message; }
        }

        private async Task RunBenchmarkAsync()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            _result.Text = "正在预热并执行 30 次热查询...";
            try
            {
                var message = _question.Text ?? string.Empty;
                var samples = await Task.Run(() =>
                {
                    KnowledgeEngineV2Service.Warm(_seller);
                    var list = new List<long>();
                    for (var i = 0; i < 30; i++)
                    {
                        var decision = KnowledgeEngineV2Service.Resolve(
                            _seller, "__knowledge_v2_benchmark__" + i, message);
                        list.Add(decision.TotalMs);
                    }
                    return list;
                });
                samples.Sort();
                var p50 = Percentile(samples, 0.50);
                var p95 = Percentile(samples, 0.95);
                var max = samples.Count == 0 ? 0 : samples[samples.Count - 1];
                _result.Text = "30次 Knowledge Engine V2 热查询性能\r\n"
                    + "P50: " + p50 + " ms\r\n"
                    + "P95: " + p95 + " ms\r\n"
                    + "MAX: " + max + " ms\r\n"
                    + "目标: P95 ≤ 50 ms\r\n\r\n"
                    + (p95 <= 50 ? "结果：达到当前性能目标。" : "结果：未达到性能目标，请检查索引规模与候选召回。")
                    + "\r\n样本: " + string.Join(", ", samples);
            }
            catch (Exception ex) { _result.Text = "性能测试失败：" + ex.Message; }
        }

        private static long Percentile(List<long> values, double p)
        {
            if (values == null || values.Count == 0) return 0;
            var index = (int)Math.Ceiling(values.Count * p) - 1;
            index = Math.Max(0, Math.Min(values.Count - 1, index));
            return values[index];
        }

        private static string Format(KnowledgeV2Decision d)
        {
            if (d == null) return "没有返回决策。";
            var q = d.Query;
            var text = "总耗时: " + d.TotalMs + " ms\r\n"
                + "解析: " + d.ParseMs + " ms\r\n"
                + "召回: " + d.RecallMs + " ms\r\n"
                + "精排: " + d.RankMs + " ms\r\n"
                + "决策: " + d.DecisionMs + " ms\r\n"
                + "候选召回数: " + d.CandidateCount + "\r\n"
                + "可本地直答: " + (d.CanDirectReply ? "是" : "否") + "\r\n"
                + "事实冲突: " + (d.HasConflict ? "是" : "否") + "\r\n"
                + "原因: " + d.Reason + "\r\n\r\n";
            if (q != null)
            {
                text += "【消息理解】\r\n"
                    + "Intent: " + q.Intent + "\r\n"
                    + "Subject: " + q.Subject + "\r\n"
                    + "Predicate: " + q.Predicate + "\r\n"
                    + "Entities: " + string.Join(", ", q.Entities ?? new List<string>()) + "\r\n"
                    + "ContextDependent: " + q.ContextDependent + "\r\n"
                    + "WorkingMemory: " + q.WorkingMemoryReason + "\r\n\r\n";
            }
            text += "【Top Matches】\r\n";
            var i = 0;
            foreach (var match in d.Matches ?? new List<KnowledgeV2Match>())
            {
                i++;
                text += "#" + i + " score=" + match.Score.ToString("0.000")
                    + " confidence=" + match.ConfidenceScore.ToString("0.000")
                    + "\r\nQ: " + (match.Record == null ? "" : match.Record.Title)
                    + "\r\nA: " + (match.Record == null ? "" : match.Record.Answer)
                    + "\r\nFact: " + (match.Record == null ? "" : KnowledgeEngineV2Semantics.FactKey(match.Record))
                    + "\r\nReason: " + match.Reason + "\r\n\r\n";
            }
            if (d.CanDirectReply) text += "【最终本地答案】\r\n" + d.Answer;
            return text;
        }
    }

    internal sealed class KnowledgeV2PortablePackage
    {
        public int SchemaVersion { get; set; }
        public string ExportedAt { get; set; }
        public List<KnowledgeV2Record> Records { get; set; }
        public KnowledgeV2Settings Settings { get; set; }

        public KnowledgeV2PortablePackage()
        {
            Records = new List<KnowledgeV2Record>();
            Settings = new KnowledgeV2Settings();
        }
    }

    internal sealed class KnowledgeV2ImportExportPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private TextBlock _status;

        public KnowledgeV2ImportExportPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            try
            {
                var stats = KnowledgeEngineV2Service.GetStats(_seller);
                _status.Text = "当前 V2 知识 " + stats.Total + " 条；数据库：" + stats.DatabasePath;
            }
            catch (Exception ex) { _status.Text = "读取状态失败：" + ex.Message; }
        }

        private void Build()
        {
            var root = new StackPanel { Margin = new Thickness(6) };
            root.Children.Add(new TextBlock { Text = "导入导出", FontSize = 20, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = "完整包包含 V2 全部结构化知识及 Knowledge Engine V2 设置。导入前会自动备份现有 V2 数据。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 14)
            });
            var actions = new WrapPanel();
            var export = Btn("导出V2完整包", 124); export.Click += delegate { ExportPackage(); };
            var import = Btn("导入V2完整包", 124); import.Click += delegate { ImportPackage(); };
            var exportRecords = Btn("仅导出结构化知识", 142); exportRecords.Click += delegate { ExportRecords(); };
            var rebuild = Btn("从旧知识重新迁移", 140); rebuild.Click += delegate { RebuildLegacy(); };
            actions.Children.Add(export); actions.Children.Add(import); actions.Children.Add(exportRecords); actions.Children.Add(rebuild);
            root.Children.Add(actions);
            _status = new TextBlock { Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(_status);
            Content = root;
        }

        private void ExportPackage()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出 Knowledge Center V2 完整包",
                    FileName = "qianniu-knowledge-center-v2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(_owner) != true) return;
                File.WriteAllText(dialog.FileName,
                    JsonConvert.SerializeObject(BuildPackage(), Formatting.Indented),
                    new System.Text.UTF8Encoding(false));
                _status.Text = "完整包已导出：" + dialog.FileName;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "导出失败：" + ex.Message); }
        }

        private void ExportRecords()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出结构化知识",
                    FileName = "qianniu-knowledge-v2-records.json",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(_owner) != true) return;
                File.WriteAllText(dialog.FileName,
                    JsonConvert.SerializeObject(KnowledgeEngineV2Repository.LoadAll(_seller), Formatting.Indented),
                    new System.Text.UTF8Encoding(false));
                _status.Text = "结构化知识已导出：" + dialog.FileName;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "导出失败：" + ex.Message); }
        }

        private void ImportPackage()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入 Knowledge Center V2 完整包",
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(_owner) != true) return;
                var package = JsonConvert.DeserializeObject<KnowledgeV2PortablePackage>(
                    File.ReadAllText(dialog.FileName, System.Text.Encoding.UTF8));
                if (package == null || package.Records == null)
                    throw new InvalidDataException("文件不是有效的 Knowledge Center V2 完整包。");
                if (package.SchemaVersion > KnowledgeEngineV2Constants.SchemaVersion)
                    throw new InvalidDataException("该知识包版本高于当前客户端，请先升级客户端。");
                if (MessageBox.Show(_owner,
                    "将用导入包替换当前 V2 结构化知识。导入前会自动备份现有数据。是否继续？",
                    "导入完整包", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                var backup = CreateAutomaticBackup();
                KnowledgeEngineV2Repository.ReplaceAll(_seller, package.Records);
                if (package.Settings != null)
                    KnowledgeEngineV2Service.SetSettings(_seller, package.Settings.Enabled, package.Settings.Mode,
                        package.Settings.DirectThreshold, package.Settings.MinConfidence);
                KnowledgeEngineV2Service.Warm(_seller);
                _status.Text = "导入完成，共 " + package.Records.Count + " 条。导入前备份：" + backup;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "导入失败：" + ex.Message); }
        }

        private void RebuildLegacy()
        {
            if (MessageBox.Show(_owner,
                "将清空 V2 数据并从当前旧知识库重新迁移。建议先导出 V2 完整包。是否继续？",
                "重新迁移", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var backup = CreateAutomaticBackup();
                KnowledgeEngineV2Service.RebuildFromLegacy(_seller);
                _status.Text = "已从旧知识重新迁移。操作前备份：" + backup;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "重新迁移失败：" + ex.Message); }
        }

        private KnowledgeV2PortablePackage BuildPackage()
        {
            return new KnowledgeV2PortablePackage
            {
                SchemaVersion = KnowledgeEngineV2Constants.SchemaVersion,
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Records = KnowledgeEngineV2Repository.LoadAll(_seller),
                Settings = KnowledgeEngineV2Service.GetSettingsView(_seller)
            };
        }

        private string CreateAutomaticBackup()
        {
            var dbPath = KnowledgeEngineV2Repository.GetDatabasePath(_seller);
            var root = Path.Combine(Path.GetDirectoryName(dbPath) ?? string.Empty, "backups");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "knowledge-center-v2-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(BuildPackage(), Formatting.Indented), new System.Text.UTF8Encoding(false));
            return path;
        }

        private static Button Btn(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 32, Margin = new Thickness(0, 0, 8, 8) };
        }
    }

    internal sealed class KnowledgeV2SettingsPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private CheckBox _enabled;
        private ComboBox _mode;
        private TextBox _threshold;
        private TextBox _confidence;
        private TextBlock _stats;

        public KnowledgeV2SettingsPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            try
            {
                var settings = KnowledgeEngineV2Service.GetSettingsView(_seller);
                var stats = KnowledgeEngineV2Service.GetStats(_seller);
                _enabled.IsChecked = settings.Enabled;
                _mode.Text = settings.Mode;
                _threshold.Text = settings.DirectThreshold.ToString("0.00");
                _confidence.Text = settings.MinConfidence.ToString("0.00");
                _stats.Text = "知识 " + stats.Total + " 条；商品绑定 " + stats.ProductBound + " 条；学习候选 "
                    + stats.LearningCandidates + " 条；冲突 " + stats.Conflicts + " 组；索引时间 "
                    + stats.SnapshotBuiltAt.ToString("HH:mm:ss.fff") + "。";
            }
            catch (Exception ex) { _stats.Text = "读取设置失败：" + ex.Message; }
        }

        private void Build()
        {
            var panel = new StackPanel { Margin = new Thickness(8), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new TextBlock { Text = "Knowledge Engine V2 设置", FontSize = 20, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "Production：高置信结构化知识可直接本地发送；Shadow：只计算并记录 V2 结果，继续使用兼容回复链路。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 14)
            });
            _enabled = new CheckBox { Content = "启用 Knowledge Engine V2", Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(_enabled);
            panel.Children.Add(new TextBlock { Text = "运行模式", FontWeight = FontWeights.SemiBold });
            _mode = new ComboBox { Height = 30, Margin = new Thickness(0, 4, 0, 10), Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            _mode.Items.Add(KnowledgeEngineV2Constants.ModeProduction);
            _mode.Items.Add(KnowledgeEngineV2Constants.ModeShadow);
            panel.Children.Add(_mode);
            panel.Children.Add(new TextBlock { Text = "本地直答匹配阈值（0.70~0.96）", FontWeight = FontWeights.SemiBold });
            _threshold = new TextBox { Width = 220, Height = 30, Margin = new Thickness(0, 4, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(_threshold);
            panel.Children.Add(new TextBlock { Text = "最低知识可信度（0.50~0.95）", FontWeight = FontWeights.SemiBold });
            _confidence = new TextBox { Width = 220, Height = 30, Margin = new Thickness(0, 4, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(_confidence);
            var actions = new WrapPanel();
            var save = new Button { Content = "保存设置", Width = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += delegate { Save(); };
            var warm = new Button { Content = "立即预热索引", Width = 110, Height = 32 };
            warm.Click += delegate
            {
                Task.Run(() => KnowledgeEngineV2Service.Warm(_seller));
                _stats.Text = "索引预热已提交。";
            };
            actions.Children.Add(save); actions.Children.Add(warm);
            panel.Children.Add(actions);
            _stats = new TextBlock { Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_stats);
            panel.Children.Add(new TextBlock
            {
                Text = "性能目标：普通热查询 P95 ≤ 50ms；买家消息不触发定时全量索引重建；知识编辑后只使当前店铺快照失效并按需重建。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 14, 0, 0)
            });
            Content = panel;
        }

        private void Save()
        {
            double threshold;
            double confidence;
            if (!double.TryParse(_threshold.Text, out threshold) || !double.TryParse(_confidence.Text, out confidence))
            {
                MessageBox.Show(_owner, "阈值必须是数字。", "Knowledge Engine V2", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                KnowledgeEngineV2Service.SetSettings(_seller, _enabled.IsChecked == true, _mode.Text, threshold, confidence);
                RefreshView();
                _stats.Text = "设置已保存。" + _stats.Text;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "保存失败：" + ex.Message); }
        }
    }
}
