using Bot.ShopScope;
using Bot.ChromeNs;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
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
            _question = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Text = "这个是电视机上的会员服务吗？" };
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
            tabs.Items.Add(new TabItem { Header = "模拟对话", Content = new KnowledgeV2SandboxConversation(_seller) });
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
        public string SourceShopKey { get; set; }
        public List<KnowledgeV2FeedbackEventRow> FeedbackEvents { get; set; }

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
        private readonly string _shopKey;
        private TextBlock _status;

        public KnowledgeV2ImportExportPage(Window owner, string seller, string shopKey)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            _shopKey = (shopKey ?? string.Empty).Trim();
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            try
            {
                using (ShopSettingsScope.Enter(RequireShop()))
                {
                    var stats = KnowledgeEngineV2Service.GetStats(_seller);
                    _status.Text = "当前 V2 知识 " + stats.Total + " 条；数据库：" + stats.DatabasePath;
                }
            }
            catch (Exception ex) { _status.Text = "读取状态失败：" + ex.Message; }
        }

        private ShopContext RequireShop()
        {
            if (string.IsNullOrWhiteSpace(_shopKey))
                throw new InvalidOperationException("知识包操作缺少精确 ShopKey。");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, _shopKey, StringComparison.Ordinal)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, _seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, _seller)))
                throw new InvalidOperationException("知识包操作的店铺身份不匹配。");
            return shop;
        }

        private void InShop(Action action)
        {
            try
            {
                using (ShopSettingsScope.Enter(RequireShop())) action();
            }
            catch (Exception ex) { MessageBox.Show(_owner, "知识库操作失败：" + ex.Message); }
        }

        private void Build()
        {
            var root = new StackPanel { Margin = new Thickness(6) };
            root.Children.Add(new TextBlock { Text = "导入导出", FontSize = 20, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = "完整包包含 V2 结构化知识、检索设置及本店反馈事件（可能包含买家消息，请妥善保管）。反馈仅恢复到原店铺，按事件ID合并去重。跨店迁移请使用仅导出结构化知识。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 14)
            });
            var actions = new WrapPanel();
            var export = Btn("导出V2完整包", 124); export.Click += delegate { InShop(ExportPackage); };
            var import = Btn("导入V2完整包", 124); import.Click += delegate { InShop(ImportPackage); };
            var exportRecords = Btn("仅导出结构化知识", 142); exportRecords.Click += delegate { InShop(ExportRecords); };
            var rebuild = Btn("重建V2检索索引", 140); rebuild.Click += delegate { InShop(RebuildIndex); };
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
                var package = ParseValidatedPackage(
                    File.ReadAllText(dialog.FileName, System.Text.Encoding.UTF8));
                if (package.FeedbackEvents != null && package.FeedbackEvents.Count > 0
                    && !string.Equals(package.SourceShopKey, _shopKey, StringComparison.Ordinal))
                    throw new InvalidDataException("该完整包含其他店铺反馈事件，禁止跨店恢复。");
                if (package.FeedbackEvents != null)
                {
                    var shop = RequireShop();
                    if (package.FeedbackEvents.Any(x => !DirectOrderIdentityResolver.IdentityEquals(x.Seller, shop.SellerNick)
                        && !DirectOrderIdentityResolver.IdentityEquals(x.Seller, shop.DisplayName)))
                        throw new InvalidDataException("知识包反馈卖家身份不匹配。");
                }
                if (MessageBox.Show(_owner,
                    "将用导入包的 " + package.Records.Count + " 条记录替换店铺 " + _seller
                    + "（ShopKey: " + _shopKey + "）的 V2 结构化知识。"
                    + (package.Records.Count == 0 ? "注意：该包为空，将清空当前知识。" : string.Empty)
                    + "未包含的旧记录将写入删除标记，已删除的ID不会自动复活。导入前会自动备份现有数据。是否继续？",
                    "导入完整包", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                var before = BuildPackage();
                var expectedFingerprint = KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(before.Records);
                var replacement = BuildImportReplacement(before.Records, package.Records);
                var backup = CreateAutomaticBackup(before);
                if (!KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(_seller, expectedFingerprint, replacement))
                    throw new InvalidOperationException("备份期间知识库已被自动学习或其他设备修改；未覆盖任何知识，请重新导入。");
                if (package.Settings != null)
                    KnowledgeEngineV2Service.SetSettings(_seller, package.Settings.Enabled, package.Settings.Mode,
                        package.Settings.DirectThreshold, package.Settings.MinConfidence);
                var restoredFeedback = package.FeedbackEvents == null || package.FeedbackEvents.Count == 0 ? 0
                    : KnowledgeEngineV2FeedbackService.MergeBackupEvents(_seller, package.SourceShopKey, package.FeedbackEvents);
                KnowledgeEngineV2Service.Warm(_seller);
                _status.Text = "导入完成，共 " + package.Records.Count + " 条；新增反馈 " + restoredFeedback + " 条。导入前备份：" + backup;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "导入失败：" + ex.Message); }
        }

        internal static List<KnowledgeV2Record> BuildImportReplacement(
            IEnumerable<KnowledgeV2Record> current, IEnumerable<KnowledgeV2Record> incoming)
        {
            var rows = (incoming ?? Enumerable.Empty<KnowledgeV2Record>())
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
            foreach (var old in current ?? Enumerable.Empty<KnowledgeV2Record>())
            {
                if (old == null || string.IsNullOrWhiteSpace(old.Id)) continue;
                if (string.Equals(old.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                {
                    // Explicit deletion remains authoritative even when an older package is imported.
                    rows[old.Id] = old;
                    continue;
                }
                if (rows.ContainsKey(old.Id)) continue;
                var tombstone = JsonConvert.DeserializeObject<KnowledgeV2Record>(JsonConvert.SerializeObject(old));
                tombstone.Enabled = false;
                tombstone.Status = "deleted";
                tombstone.UpdatedAt = DateTime.Now;
                rows.Add(tombstone.Id, tombstone);
            }
            // A previously deleted human-evidence source must not return under another random ID.
            var deletedEvidence = new HashSet<string>(rows.Values
                .Where(x => string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase)
                    && KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(x.SourceType))
                .Select(x => (x.SourceId ?? string.Empty).Trim()).Where(x => x.Length > 0), StringComparer.Ordinal);
            foreach (var row in rows.Values)
            {
                if (!KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(row.SourceType)
                    || !deletedEvidence.Contains((row.SourceId ?? string.Empty).Trim())) continue;
                row.Enabled = false;
                row.Status = "deleted";
            }
            return rows.Values.ToList();
        }

        internal static KnowledgeV2PortablePackage ParseValidatedPackage(string json)
        {
            // Constructor defaults must not turn arbitrary JSON into an empty replacement.
            var root = JObject.Parse(json);
            var version = root.GetValue("SchemaVersion", StringComparison.OrdinalIgnoreCase);
            var rows = root.GetValue("Records", StringComparison.OrdinalIgnoreCase) as JArray;
            if (version == null || version.Type != JTokenType.Integer
                || version.Value<long>() != KnowledgeEngineV2Constants.SchemaVersion || rows == null)
                throw new InvalidDataException("文件缺少受支持的 V2 版本或 Records 数组。");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in rows)
            {
                var row = token as JObject;
                var id = row == null ? null : row.GetValue("Id", StringComparison.OrdinalIgnoreCase);
                var title = row == null ? null : row.GetValue("Title", StringComparison.OrdinalIgnoreCase);
                var answer = row == null ? null : row.GetValue("Answer", StringComparison.OrdinalIgnoreCase);
                if (id == null || id.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)id)
                    || !ids.Add(((string)id).Trim())
                    || title == null || title.Type != JTokenType.String
                    || answer == null || answer.Type != JTokenType.String)
                    throw new InvalidDataException("知识包包含无效记录或重复 ID，未替换现有知识。");
            }

            var package = root.ToObject<KnowledgeV2PortablePackage>();
            foreach (var record in package.Records)
                record.Id = record.Id.Trim();
            var feedback = root.GetValue("FeedbackEvents", StringComparison.OrdinalIgnoreCase);
            if (feedback != null && feedback.Type != JTokenType.Null && !(feedback is JArray))
                throw new InvalidDataException("反馈事件必须为数组。");
            KnowledgeEngineV2FeedbackService.ValidateBackupEvents(package.FeedbackEvents, package.Records);
            var settings = root.GetValue("Settings", StringComparison.OrdinalIgnoreCase);
            if (settings == null || settings.Type == JTokenType.Null)
                package.Settings = null;
            else
            {
                var obj = settings as JObject;
                if (obj == null || obj.GetValue("DirectThreshold", StringComparison.OrdinalIgnoreCase) == null
                    || obj.GetValue("MinConfidence", StringComparison.OrdinalIgnoreCase) == null
                    || package.Settings == null
                    || double.IsNaN(package.Settings.DirectThreshold) || double.IsInfinity(package.Settings.DirectThreshold)
                    || double.IsNaN(package.Settings.MinConfidence) || double.IsInfinity(package.Settings.MinConfidence))
                    throw new InvalidDataException("知识包包含无效的 V2 检索阈值。");
            }
            return package;
        }

        private void RebuildIndex()
        {
            try
            {
                var backup = CreateAutomaticBackup();
                KnowledgeEngineV2Service.Invalidate(_seller);
                KnowledgeEngineV2Service.Warm(_seller);
                var stats = KnowledgeEngineV2Service.GetStats(_seller);
                _status.Text = "已重建 Knowledge V2 检索索引；当前知识 " + stats.Total
                    + " 条。数据未清空。操作前备份：" + backup;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "重建V2索引失败：" + ex.Message); }
        }

        private KnowledgeV2PortablePackage BuildPackage()
        {
            var records = KnowledgeEngineV2Repository.LoadAll(_seller);
            var ids = new HashSet<string>(records.Select(x => x.Id), StringComparer.Ordinal);
            return new KnowledgeV2PortablePackage
            {
                SchemaVersion = KnowledgeEngineV2Constants.SchemaVersion,
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Records = records,
                Settings = KnowledgeEngineV2Service.GetSettingsView(_seller),
                SourceShopKey = _shopKey,
                FeedbackEvents = KnowledgeEngineV2FeedbackService.ExportEvents(_seller).Where(x => ids.Contains(x.KnowledgeId)).ToList()
            };
        }

        private string CreateAutomaticBackup(KnowledgeV2PortablePackage snapshot = null)
        {
            var dbPath = KnowledgeEngineV2Repository.GetDatabasePath(_seller);
            var root = Path.Combine(Path.GetDirectoryName(dbPath) ?? string.Empty, "backups");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "knowledge-center-v2-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot ?? BuildPackage(), Formatting.Indented), new System.Text.UTF8Encoding(false));
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
        private readonly string _shopKey;
        private ComboBox _mode;
        private TextBox _threshold;
        private TextBox _confidence;
        private TextBlock _stats;

        public KnowledgeV2SettingsPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = seller ?? string.Empty;
            _shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);
            Build();
            Loaded += delegate { RefreshView(); };
        }

        private T InShop<T>(Func<T> action)
        {
            if (string.IsNullOrWhiteSpace(_shopKey))
                throw new InvalidOperationException("知识设置缺少精确 ShopKey。");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, _shopKey, StringComparison.Ordinal)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, _seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, _seller)))
                throw new InvalidOperationException("知识设置店铺身份不匹配。");
            using (ShopSettingsScope.Enter(shop)) return action();
        }

        public void RefreshView()
        {
            if (string.IsNullOrWhiteSpace(_seller)) return;
            try
            {
                var snapshot = InShop(() => new { Settings = KnowledgeEngineV2Service.GetSettingsView(_seller), Stats = KnowledgeEngineV2Service.GetStats(_seller) });
                var settings = snapshot.Settings;
                var stats = snapshot.Stats;
                _mode.Text = KnowledgeEngineV2Constants.ModeProduction;
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
                Text = "Knowledge V2 是唯一知识库和唯一检索引擎，始终启用。未命中可靠知识时交由 AI 结合当前对话处理；高风险、冲突和订单流程保护始终生效。", 
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 14)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "运行状态：始终启用（不可关闭）",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            _mode = new ComboBox { Visibility = Visibility.Collapsed };
            _mode.Items.Add(KnowledgeEngineV2Constants.ModeProduction);
            _mode.SelectedIndex = 0;
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
                _stats.Text = "正在预热本店索引。";
                Task.Run(() => InShop(() => { KnowledgeEngineV2Service.Warm(_seller); return true; }))
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (t.IsFaulted) _stats.Text = "预热失败：" + t.Exception.GetBaseException().Message;
                        else RefreshView();
                    })));
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
                InShop(() =>
                {
                    KnowledgeEngineV2Service.SetSettings(_seller, true, KnowledgeEngineV2Constants.ModeProduction, threshold, confidence);
                    return true;
                });
                RefreshView();
                _stats.Text = "设置已保存。" + _stats.Text;
            }
            catch (Exception ex) { MessageBox.Show(_owner, "保存失败：" + ex.Message); }
        }
    }
}
