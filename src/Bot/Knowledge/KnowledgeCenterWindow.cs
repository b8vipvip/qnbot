using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Bot.ChromeNs;
using BotLib;
using BotLib.Db.Sqlite;
using DbEntity.Response;
using log4net;
using log4net.Appender;
using log4net.Filter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Knowledge
{
    public class KnowledgeCenterWindow : Window
    {
        private readonly TabControl _tabs;
        private readonly KnowledgeImportControl _import;
        private readonly KnowledgeManagerControl _manager;
        private readonly AiOptimizationHistoryControl _optimizationHistory;

        public KnowledgeCenterWindow()
        {
            Title = "AI客服 - 知识库";
            Width = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel();
            Content = root;
            var toolbar = new WrapPanel { Margin = new Thickness(10, 10, 10, 4) };
            DockPanel.SetDock(toolbar, Dock.Top);
            var importPackage = new Button { Content = "导入知识库完整包", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var exportPackage = new Button { Content = "导出知识库完整包", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            toolbar.Children.Add(importPackage);
            toolbar.Children.Add(exportPackage);
            root.Children.Add(toolbar);
            _tabs = new TabControl();
            root.Children.Add(_tabs);
            _manager = new KnowledgeManagerControl();
            _import = new KnowledgeImportControl(delegate { ShowManager(); });
            _optimizationHistory = new AiOptimizationHistoryControl();
            _tabs.Items.Add(new TabItem { Header = "智能导入", Content = _import });
            _tabs.Items.Add(new TabItem { Header = "问答管理", Content = _manager });
            _tabs.Items.Add(new TabItem { Header = "AI优化记录", Content = _optimizationHistory });
            importPackage.Click += (s, e) =>
            {
                if (RulePolicyImportExportUi.ImportKnowledgePackage(this))
                {
                    _manager.RefreshData();
                    ShowManager();
                }
            };
            exportPackage.Click += (s, e) => RulePolicyImportExportUi.ExportKnowledgePackage(this);
        }

        public void ShowManager()
        {
            _manager.RefreshData();
            _tabs.SelectedIndex = 1;
        }

        public void ShowOptimizationHistory()
        {
            _optimizationHistory.RefreshData();
            _tabs.SelectedIndex = 2;
        }

        public void NavigateToManager(string seller, string buyer, string question, string answer)
        {
            ShowManager();
            if (_manager.LocateEntry(seller, buyer, question, answer)) return;
            MessageBox.Show(this,
                "没有找到完全对应的知识条目，已按当前问题或答案显示搜索结果。",
                "知识库定位", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_import != null) _import.CancelForWindowClose();
            base.OnClosed(e);
        }

        public static void MyShow(Window owner)
        {
            var wnd = new KnowledgeCenterWindow();
            if (owner != null) wnd.Owner = owner;
            wnd.Show();
        }

        public static void ShowManagerAndLocate(Window owner, string seller, string buyer, string question, string answer)
        {
            var wnd = new KnowledgeCenterWindow();
            if (owner != null) wnd.Owner = owner;
            wnd.Show();
            wnd.NavigateToManager(seller, buyer, question, answer);
        }
    }

    internal sealed class AiOptimizationHistoryControl : UserControl
    {
        private sealed class Row
        {
            public DateTime SortTime { get; set; }
            public string Time { get; set; }
            public string Type { get; set; }
            public string Buyer { get; set; }
            public string Status { get; set; }
            public string Accuracy { get; set; }
            public string Applied { get; set; }
            public string Summary { get; set; }
            public object Source { get; set; }
        }

        private readonly DataGrid _grid = new DataGrid();
        private readonly TextBox _detail = new TextBox();
        private readonly TextBlock _summary = new TextBlock();
        private bool _subscribed;

        public AiOptimizationHistoryControl()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var refresh = new Button { Content = "刷新记录", Width = 90, Height = 30 };
            refresh.Click += delegate { RefreshData(); };
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);
            _summary.Text = "人工接管即时对比 + 接待结束整轮复盘";
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _summary.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(_summary);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _grid.IsReadOnly = true;
            _grid.AutoGenerateColumns = false;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = false;
            _grid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new System.Windows.Data.Binding("Time"), Width = 120 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding("Type"), Width = 135 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "买家", Binding = new System.Windows.Data.Binding("Buyer"), Width = 145 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new System.Windows.Data.Binding("Status"), Width = 95 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "AI准确度", Binding = new System.Windows.Data.Binding("Accuracy"), Width = 85 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "知识应用", Binding = new System.Windows.Data.Binding("Applied"), Width = 85 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "摘要/问题", Binding = new System.Windows.Data.Binding("Summary"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.SelectionChanged += OnSelectionChanged;
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            _detail.IsReadOnly = true;
            _detail.AcceptsReturn = true;
            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _detail.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _detail.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(_detail, 2);
            root.Children.Add(_detail);
            Content = root;

            Loaded += delegate
            {
                if (!_subscribed)
                {
                    AiManualReplyOptimizationService.RecordsChanged += OnRecordsChanged;
                    ConversationSessionLearningService.ReportsChanged += OnRecordsChanged;
                    _subscribed = true;
                }
                RefreshData();
            };
            Unloaded += delegate
            {
                if (!_subscribed) return;
                AiManualReplyOptimizationService.RecordsChanged -= OnRecordsChanged;
                ConversationSessionLearningService.ReportsChanged -= OnRecordsChanged;
                _subscribed = false;
            };
        }

        public void RefreshData()
        {
            try
            {
                var rows = new List<Row>();
                rows.AddRange(AiManualReplyOptimizationService.GetRecords(500).Select(x => new Row
                {
                    SortTime = x.CreatedAt,
                    Time = x.CreatedAtText,
                    Type = "人工接管即时对比",
                    Buyer = x.Buyer,
                    Status = x.Status,
                    Accuracy = x.AccuracyText,
                    Applied = x.AppliedCount + "/" + (x.AppliedCount + x.SkippedCount),
                    Summary = Short(x.Question, 120),
                    Source = x
                }));
                rows.AddRange(ConversationSessionLearningService.GetReports(500).Select(x => new Row
                {
                    SortTime = x.CompletedAt == DateTime.MinValue ? x.LastBuyerAt : x.CompletedAt,
                    Time = x.CompletedAtText,
                    Type = "接待结束复盘",
                    Buyer = x.Buyer,
                    Status = x.Status,
                    Accuracy = "-",
                    Applied = x.AppliedCount + "/" + (x.AppliedCount + x.SkippedCount),
                    Summary = Short(x.Summary, 120),
                    Source = x
                }));
                rows = rows.OrderByDescending(x => x.SortTime).Take(1000).ToList();
                _grid.ItemsSource = rows;
                _summary.Text = "AI优化记录：共 " + rows.Count + " 条；即时记录可查看AI对照答案、人工实际回复、准确度、人工回复原因和知识策略。";
                if (rows.Count > 0) _grid.SelectedIndex = 0;
                else _detail.Text = "暂无AI优化记录。";
            }
            catch (Exception ex) { _detail.Text = "读取AI优化记录失败：" + ex.Message; }
        }

        private void OnRecordsChanged()
        {
            try { Dispatcher.BeginInvoke(new Action(RefreshData)); }
            catch { }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = _grid.SelectedItem as Row;
            if (row == null) { _detail.Text = string.Empty; return; }
            var immediate = row.Source as AiOptimizationRecordView;
            if (immediate != null)
            {
                _detail.Text = AiManualReplyOptimizationService.FormatRecord(immediate);
                return;
            }
            var session = row.Source as ConversationSessionLearningReportView;
            _detail.Text = session == null ? string.Empty : ConversationSessionLearningService.FormatReport(session);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _manualReplyAiComparisonBootstrap = ChromeNs.ManualReplyAiComparisonBridge.InitializeForApp();
        private readonly object _runtimeIngressReconciliationBootstrap = ChromeNs.RuntimeIngressReconciliationBridge.InitializeForApp();
        private readonly object _runtimeLogNoiseFilterBootstrap = ChromeNs.RuntimeLogNoiseFilterBootstrap.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal sealed class AiOptimizationRecordEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string AiAnswer { get; set; }
        public string HumanAnswer { get; set; }
        public double AccuracyScore { get; set; }
        public string AccuracyAnalysis { get; set; }
        public string HumanReplyReason { get; set; }
        public string KnowledgeStrategy { get; set; }
        public string SuggestionsJson { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class AiOptimizationRecordView
    {
        public string Id { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string AiAnswer { get; set; }
        public string HumanAnswer { get; set; }
        public double AccuracyScore { get; set; }
        public string AccuracyAnalysis { get; set; }
        public string HumanReplyReason { get; set; }
        public string KnowledgeStrategy { get; set; }
        public string SuggestionsJson { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedAtText { get { return CreatedAt == DateTime.MinValue ? string.Empty : CreatedAt.ToString("MM-dd HH:mm:ss"); } }
        public string AccuracyText { get { return AccuracyScore <= 0 ? "-" : AccuracyScore.ToString("0") + "%"; } }
    }

    internal static class AiManualReplyOptimizationService
    {
        private static readonly object DbSync = new object();
        private const int ManualTurnReservationMinutes = 10;
        private static readonly ConcurrentDictionary<string, DateTime> TurnReservations =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static int _schemaReady;
        public const int RetentionDays = 180;
        public const int MaxRecords = 3000;
        public static event Action RecordsChanged;

        public static void StartForManualReply(string seller, string buyer, string humanAnswer, DateTime manualAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            humanAnswer = StripAi(Clean(humanAnswer, 2200));
            if (seller.Length == 0 || buyer.Length == 0 || humanAnswer.Length == 0) return;
            Task.Run(async () =>
            {
                try { await RunAsync(seller, buyer, humanAnswer, manualAt).ConfigureAwait(false); }
                catch (Exception ex) { Log.ErrorWithMaxCount("人工接管AI即时优化失败: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message, 20); }
            });
        }

        private static string BuildManualTurnKey(string seller, string buyer, string question)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + Normalize(question);
        }

        private static bool TryReserveManualTurn(string key, DateTime now)
        {
            foreach (var pair in TurnReservations.Where(x => x.Value <= now).Take(64).ToList())
            {
                DateTime ignored;
                TurnReservations.TryRemove(pair.Key, out ignored);
            }

            var until = now.AddMinutes(ManualTurnReservationMinutes);
            DateTime existing;
            if (!TurnReservations.TryGetValue(key, out existing))
                return TurnReservations.TryAdd(key, until);
            if (existing > now) return false;
            return TurnReservations.TryUpdate(key, until, existing);
        }

        public static List<AiOptimizationRecordView> GetRecords(int maxCount)
        {
            EnsureSchema();
            var take = Math.Max(1, Math.Min(MaxRecords, maxCount <= 0 ? 300 : maxCount));
            try
            {
                lock (DbSync)
                {
                    return (DbHelper.Db.Select(typeof(AiOptimizationRecordEntity), "order by UpdatedAtTicks desc limit " + take) ?? new List<object>())
                        .OfType<AiOptimizationRecordEntity>().Where(x => x != null).Select(ToView).ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取AI优化记录失败：" + ex.Message, 10);
                return new List<AiOptimizationRecordView>();
            }
        }

        public static string FormatRecord(AiOptimizationRecordView x)
        {
            if (x == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("AI优化记录");
            sb.AppendLine("时间：" + (x.CreatedAt == DateTime.MinValue ? string.Empty : x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine("客服：" + x.Seller);
            sb.AppendLine("买家：" + x.Buyer);
            sb.AppendLine("状态：" + x.Status);
            sb.AppendLine("AI准确度：" + x.AccuracyText);
            sb.AppendLine(); sb.AppendLine("买家问题："); sb.AppendLine(x.Question ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("后台AI对照答案（未发送）："); sb.AppendLine(x.AiAnswer ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("人工客服实际回复："); sb.AppendLine(x.HumanAnswer ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("AI回复准确性分析："); sb.AppendLine(x.AccuracyAnalysis ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("人工为什么这样回复："); sb.AppendLine(x.HumanReplyReason ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("知识库/策略建议："); sb.AppendLine(x.KnowledgeStrategy ?? string.Empty);
            sb.AppendLine(); sb.AppendLine("自动应用：" + x.AppliedCount + " 条；跳过：" + x.SkippedCount + " 条");
            if (!string.IsNullOrWhiteSpace(x.SuggestionsJson))
            {
                sb.AppendLine(); sb.AppendLine("结构化优化建议：");
                try { sb.AppendLine(JToken.Parse(x.SuggestionsJson).ToString(Formatting.Indented)); }
                catch { sb.AppendLine(x.SuggestionsJson); }
            }
            if (!string.IsNullOrWhiteSpace(x.Error)) sb.AppendLine("异常：" + x.Error);
            return sb.ToString().Trim();
        }

        private static async Task RunAsync(string seller, string buyer, string humanAnswer, DateTime manualAt)
        {
            if (manualAt == DateTime.MinValue) manualAt = DateTime.Now;
            await Task.Delay(120).ConfigureAwait(false);
            var from = manualAt.AddMinutes(-12);
            var turns = ConversationSessionLearningRuntimeBridge.GetTurnsBetween(seller, buyer, from, manualAt.AddSeconds(1), 100, true);
            var cards = BotConversationHistoryStore.LoadRange(seller, buyer, from, manualAt.AddSeconds(1), 100);
            var question = BuildRecentBuyerQuestion(turns, cards, manualAt);
            if (string.IsNullOrWhiteSpace(question)) return;
            var key = BuildManualTurnKey(seller, buyer, question);
            if (!TryReserveManualTurn(key, DateTime.Now)) return;
            var transcript = BuildTranscriptBeforeHuman(turns, cards, humanAnswer, manualAt);

            var shadowQuestion = question
                + "\n\n【后台AI对比任务：客服已经人工接管。请忽略聊天记录中刚刚出现的客服人工回复，只根据它之前的买家消息、店铺知识和规则独立回答原问题。不要提到本说明。】";
            var aiAnswer = await Task.Run(() => MyOpenAI.GetAnswer(seller, buyer, shadowQuestion, true)).ConfigureAwait(false);
            aiAnswer = StripAi(Clean(aiAnswer, 2400));
            if (string.IsNullOrWhiteSpace(aiAnswer) || aiAnswer.StartsWith("错误：", StringComparison.Ordinal)) return;
            ShowCompareOnlyAnswer(seller, buyer, question, aiAnswer);

            var entity = new AiOptimizationRecordEntity
            {
                EntityId = Guid.NewGuid().ToString("N"), Seller = seller, Buyer = buyer,
                Question = Redact(question), AiAnswer = Redact(aiAnswer), HumanAnswer = Redact(humanAnswer),
                Status = "正在分析", CreatedAtTicks = DateTime.Now.Ticks, UpdatedAtTicks = DateTime.Now.Ticks
            };
            Save(entity); NotifyChanged();

            try
            {
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你是电商客服AI回复质量审计与知识库优化器。比较后台AI对照答案与人工客服实际回复，并结合人工回复之前的聊天记录。只输出JSON："
                            + "{\"accuracy_score\":0-100,\"accuracy_analysis\":\"AI哪里正确、错误、遗漏\",\"human_reply_reason\":\"人工为什么这样回复\","
                            + "\"knowledge_strategy\":\"是否应新增或修改知识库/策略以及原因\",\"suggestions\":[{\"action\":\"add|update|skip\","
                            + "\"question\":\"通用问题\",\"answer\":\"可复用完整答案\",\"old_answer\":\"旧答案\",\"category\":\"分类\","
                            + "\"keywords\":[\"关键词\"],\"confidence\":0.0,\"evidence_type\":\"manual_reply|manual_correction|insufficient\",\"evidence\":\"证据\",\"reason\":\"原因\"}]}。"
                            + "人工最终有效回复优先作为纠错证据；Bot答案不能作为新增事实的唯一证据；高风险或一次性结论不得自动固化；没有可靠人工事实证据必须skip。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = "买家问题：" + Redact(question) + "\n后台AI对照答案：" + Redact(aiAnswer)
                            + "\n人工客服实际回复：" + Redact(humanAnswer) + "\n\n人工回复之前的聊天时间线：\n" + transcript
                    }
                };
                var result = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 2400, 0.05, 50, CancellationToken.None)).ConfigureAwait(false);
                if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
                    throw new Exception(result == null ? "AI对比结果为空" : result.Error);
                var analysis = ParseObject(result.Answer);
                entity.AccuracyScore = ParseScore(analysis["accuracy_score"]);
                entity.AccuracyAnalysis = Redact(Clean(Convert.ToString(analysis["accuracy_analysis"]), 1800));
                entity.HumanReplyReason = Redact(Clean(Convert.ToString(analysis["human_reply_reason"]), 1800));
                entity.KnowledgeStrategy = Redact(Clean(Convert.ToString(analysis["knowledge_strategy"]), 1800));

                var persisted = new JArray();
                var applied = 0;
                var skipped = 0;
                var suggestions = analysis["suggestions"] as JArray ?? new JArray();
                foreach (var token in suggestions.OfType<JObject>().Take(8))
                {
                    var item = (JObject)token.DeepClone();
                    var action = Clean(Convert.ToString(item["action"]), 20).ToLowerInvariant();
                    var q = Redact(Clean(Convert.ToString(item["question"]), 500));
                    var a = StripAi(Redact(Clean(Convert.ToString(item["answer"]), 1500)));
                    var category = Clean(Convert.ToString(item["category"]), 100);
                    var evidenceType = Clean(Convert.ToString(item["evidence_type"]), 80).ToLowerInvariant();
                    var confidence = ParseConfidence(item["confidence"]);
                    var keywords = item["keywords"] is JArray
                        ? string.Join(",", ((JArray)item["keywords"]).Select(v => Clean(Convert.ToString(v), 80)).Where(v => v.Length > 0))
                        : Clean(Convert.ToString(item["keywords"]), 500);
                    var safe = (action == "add" || action == "update") && confidence >= 0.88
                        && (evidenceType == "manual_reply" || evidenceType == "manual_correction")
                        && q.Length > 0 && a.Length > 0 && !ContainsHighRisk(q + " " + a);
                    var wasApplied = false;
                    string applyMessage;
                    if (safe)
                    {
                        var write = ReviewedKnowledgeLearningService.ApplyReviewedKnowledge(q, a, category, keywords, "人工即时对比", confidence, evidenceType);
                        wasApplied = write != null && write.Success && (write.Added || write.Updated);
                        applyMessage = write == null ? "知识写入结果为空" : write.Message;
                    }
                    else applyMessage = action == "skip" ? "AI建议跳过" : "未达到人工证据/置信度/安全边界";
                    if (wasApplied) applied++; else skipped++;
                    item["question"] = q; item["answer"] = a; item["confidence"] = confidence;
                    item["applied"] = wasApplied; item["apply_message"] = applyMessage;
                    persisted.Add(item);
                }
                entity.SuggestionsJson = persisted.ToString(Formatting.None);
                entity.AppliedCount = applied; entity.SkippedCount = skipped; entity.Status = "分析完成";
                entity.Error = string.Empty; entity.UpdatedAtTicks = DateTime.Now.Ticks;
                Save(entity); Cleanup(); NotifyChanged();
                Log.Info("人工接管AI即时优化完成: seller=" + seller + ", buyer=" + buyer
                    + ", accuracy=" + entity.AccuracyScore.ToString("0") + ", applied=" + applied + ", skipped=" + skipped);
            }
            catch (Exception ex)
            {
                entity.Status = "分析失败"; entity.Error = Clean(ex.Message, 1600); entity.UpdatedAtTicks = DateTime.Now.Ticks;
                Save(entity); NotifyChanged(); throw;
            }
        }

        private static string BuildRecentBuyerQuestion(List<ConversationContextTurn> turns, List<BotConversationHistoryEntity> cards, DateTime manualAt)
        {
            var buyerTurns = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && x.Role == "user" && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .Where(x => x.Timestamp == DateTime.MinValue || x.Timestamp <= manualAt.AddSeconds(1))
                .Where(x => !IsPlatformNoise(x.Text)).OrderBy(x => x.Timestamp).ToList();
            if (buyerTurns.Count > 0)
            {
                var last = buyerTurns[buyerTurns.Count - 1];
                var floor = last.Timestamp == DateTime.MinValue ? DateTime.MinValue : last.Timestamp.AddSeconds(-18);
                var recent = buyerTurns.Where(x => floor == DateTime.MinValue || x.Timestamp == DateTime.MinValue || x.Timestamp >= floor).ToList();
                if (recent.Count > 5) recent = recent.Skip(recent.Count - 5).ToList();
                var selected = recent.Select(x => Clean(x.Text, 600)).Where(x => x.Length > 0).Distinct().ToList();
                if (selected.Count > 0) return Clean(string.Join("\n", selected), 1800);
            }
            var card = (cards ?? new List<BotConversationHistoryEntity>()).OrderByDescending(x => x.CreatedAtTicks).FirstOrDefault();
            return card == null ? string.Empty : Clean(card.Question, 1800);
        }

        private static string BuildTranscriptBeforeHuman(List<ConversationContextTurn> turns, List<BotConversationHistoryEntity> cards, string humanAnswer, DateTime manualAt)
        {
            var sb = new StringBuilder();
            foreach (var turn in (turns ?? new List<ConversationContextTurn>()).OrderBy(x => x.Timestamp))
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                if (turn.Timestamp != DateTime.MinValue && turn.Timestamp > manualAt.AddSeconds(1)) continue;
                if (turn.Role == "assistant" && Normalize(StripAi(turn.Text)) == Normalize(StripAi(humanAnswer))) continue;
                var role = turn.Role == "user" ? "买家" : (turn.Withdrawn ? "客服-已撤回" : (IsBotTurn(turn, cards) ? "Bot" : "人工客服"));
                var time = turn.Timestamp == DateTime.MinValue ? "时间未知" : turn.Timestamp.ToString("HH:mm:ss");
                sb.Append('[').Append(time).Append(' ').Append(role).Append("] ").AppendLine(Redact(Clean(turn.Text, 1400)));
            }
            return sb.Length == 0 ? "（未读取到更早聊天记录）" : sb.ToString().Trim();
        }

        private static bool IsBotTurn(ConversationContextTurn turn, List<BotConversationHistoryEntity> cards)
        {
            var text = turn == null ? string.Empty : (turn.Text ?? string.Empty).Trim();
            if (HasAiMarker(text)) return true;
            var normalized = Normalize(StripAi(text));
            return normalized.Length > 0 && cards != null && cards.Any(x => Normalize(StripAi(x.Answer)) == normalized);
        }

        private static bool IsPlatformNoise(string value)
        {
            var text = Normalize(value);
            return text.StartsWith("当前用户来自") || text.StartsWith("该用户来自") || text.StartsWith("平台提示") || text.StartsWith("系统提示");
        }

        private static void ShowCompareOnlyAnswer(string seller, string buyer, string question, string answer)
        {
            if (Application.Current == null) return;
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var desk = Desk.FindExistingBySellerNick(seller);
                        if (desk == null) return;
                        var ctl = desk.AddConversation(seller, buyer, question,
                            BotOutboundMessageFormatter.EnsureAiMarker(answer), false, "人工接管后的AI优化对比");
                        if (ctl != null) ctl.SetStatus("客服已人工回复；这是后台AI对照答案，仅用于准确性与知识库优化，绝不会发送给买家", true);
                    }
                    catch (Exception ex) { Log.ErrorWithMaxCount("显示人工接管AI对照答案失败：" + ex.Message, 10); }
                }));
            }
            catch { }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute("create table if not exists AiOptimizationRecordEntity ("
                    + "EntityId text primary key not null,Seller text,Buyer text,Question text,AiAnswer text,HumanAnswer text,"
                    + "AccuracyScore real not null default 0,AccuracyAnalysis text,HumanReplyReason text,KnowledgeStrategy text,"
                    + "SuggestionsJson text,Status text,Error text,AppliedCount integer not null default 0,SkippedCount integer not null default 0,"
                    + "CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_AiOptimizationRecord_Updated on AiOptimizationRecordEntity(UpdatedAtTicks)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static void Save(AiOptimizationRecordEntity entity)
        {
            EnsureSchema();
            lock (DbSync) DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
        }

        private static void Cleanup()
        {
            try
            {
                EnsureSchema();
                lock (DbSync)
                {
                    DbHelper.Db.Execute("delete from AiOptimizationRecordEntity where UpdatedAtTicks < ?", DateTime.Now.AddDays(-RetentionDays).Ticks);
                    DbHelper.Db.Execute("delete from AiOptimizationRecordEntity where EntityId not in (select EntityId from AiOptimizationRecordEntity order by UpdatedAtTicks desc limit " + MaxRecords + ")");
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("清理AI优化记录失败：" + ex.Message, 10); }
        }

        private static AiOptimizationRecordView ToView(AiOptimizationRecordEntity x)
        {
            return new AiOptimizationRecordView
            {
                Id = x.EntityId, Seller = x.Seller, Buyer = x.Buyer, Question = x.Question,
                AiAnswer = x.AiAnswer, HumanAnswer = x.HumanAnswer, AccuracyScore = x.AccuracyScore,
                AccuracyAnalysis = x.AccuracyAnalysis, HumanReplyReason = x.HumanReplyReason,
                KnowledgeStrategy = x.KnowledgeStrategy, SuggestionsJson = x.SuggestionsJson,
                Status = x.Status, Error = x.Error, AppliedCount = x.AppliedCount, SkippedCount = x.SkippedCount,
                CreatedAt = TicksToDate(x.CreatedAtTicks)
            };
        }

        private static JObject ParseObject(string value)
        {
            value = (value ?? string.Empty).Trim();
            var fenced = Regex.Match(value, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
            if (fenced.Success) value = fenced.Groups[1].Value;
            var start = value.IndexOf('{'); var end = value.LastIndexOf('}');
            if (start >= 0 && end > start) value = value.Substring(start, end - start + 1);
            return JObject.Parse(value);
        }

        private static DateTime TicksToDate(long ticks)
        {
            try { return ticks > 0 ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static double ParseScore(JToken token)
        {
            double value; if (!double.TryParse(Convert.ToString(token), out value)) return 0;
            if (value <= 1) value *= 100;
            return Math.Max(0, Math.Min(100, value));
        }

        private static double ParseConfidence(JToken token)
        {
            double value; if (!double.TryParse(Convert.ToString(token), out value)) return 0;
            if (value > 1) value /= 100.0;
            return Math.Max(0, Math.Min(1, value));
        }

        private static bool ContainsHighRisk(string value)
        {
            var terms = new[] { "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡", "验证码", "密码", "订单隐私", "订单号", "手机号", "账号安全", "封号", "解封", "法律", "报警" };
            return terms.Any(x => (value ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripAi(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s*(?:\[AI\]|【AI】|［AI］)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private static string Redact(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1[3-9]\d{9}(?!\d)", "[手机号已隐藏]");
            value = Regex.Replace(value, @"(?<!\d)\d{14,20}(?!\d)", "[长数字已隐藏]");
            return Regex.Replace(value, @"(?i)(验证码|code)\s*[:：]?\s*\d{4,8}", "$1：[已隐藏]");
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\0", string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static void NotifyChanged()
        {
            try { var handler = RecordsChanged; if (handler != null) handler(); }
            catch { }
        }
    }

    internal static class ManualReplyAiComparisonBridge
    {
        private static readonly ConcurrentDictionary<QN, byte> Attached = new ConcurrentDictionary<QN, byte>();
        private static Timer _timer;
        private static int _started;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _timer = new Timer(_ => Attach(), null, 1200, 1500);
                Log.Info("人工接管AI即时对比已启动：人工回复后生成后台AI对照答案，只用于学习，不发送给买家。");
            }
            return new object();
        }

        private static void Attach()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || !Attached.TryAdd(qn, 0)) continue;
                    qn.EvRecieveNewMessage += OnReceive;
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("绑定人工接管AI即时对比失败：" + ex.Message, 10); }
        }

        private static void OnReceive(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message) || qn.Seller == null) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                foreach (var message in response.result.Where(x => x != null && x.fromid != null && x.toid != null))
                {
                    if (!string.Equals((message.fromid.nick ?? string.Empty).Trim(), seller, StringComparison.Ordinal)) continue;
                    var buyer = (message.toid.nick ?? string.Empty).Trim();
                    if (buyer.Length == 0) continue;
                    var variants = ExtractTexts(message);
                    if (variants.Count == 0 || variants.Any(HasAiMarker) || MatchesRecentBotAnswer(seller, buyer, variants)) continue;
                    AiManualReplyOptimizationService.StartForManualReply(seller, buyer, variants[0], DateTime.Now);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("人工回复即时对比识别失败：" + ex.Message, 10); }
        }

        private static List<string> ExtractTexts(QNChatMessage message)
        {
            var values = new List<string>();
            try { if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text)) values.Add(message.originalData.text.Trim()); }
            catch { }
            if (!string.IsNullOrWhiteSpace(message.summary)) values.Add(message.summary.Trim());
            return values.Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool MatchesRecentBotAnswer(string seller, string buyer, IEnumerable<string> variants)
        {
            var values = new HashSet<string>(variants.Select(Normalize).Where(x => x.Length > 0), StringComparer.Ordinal);
            try { return BotConversationHistoryStore.LoadRecent(seller, buyer, 30).Any(x => x != null && values.Contains(Normalize(StripAi(x.Answer)))); }
            catch { return false; }
        }

        private static bool HasAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripAi(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s*(?:\[AI\]|【AI】|［AI］)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }
    }

    internal static class RuntimeIngressReconciliationBridge
    {
        private static readonly ConcurrentDictionary<QN, DateTime> NextProbeAt = new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, DateTime> NextPanelProbeAt = new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, byte> Running = new ConcurrentDictionary<QN, byte>();
        private static Timer _timer;
        private static int _started;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _timer = new Timer(_ => Tick(), null, 3500, 2500);
                Log.Info("业务入站主动核对已启动：连接正常但业务推送漏事件时，低频核对当前买家远端历史与订单面板。");
            }
            return new object();
        }

        private static void Tick()
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null || qn.CDP == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick)) continue;
                    DateTime next;
                    if (NextProbeAt.TryGetValue(qn, out next) && next > now) continue;
                    NextProbeAt[qn] = now.AddSeconds(30);
                    if (!Running.TryAdd(qn, 0)) continue;
                    Task.Run(async () =>
                    {
                        try { await ProbeAsync(qn).ConfigureAwait(false); }
                        finally { byte ignored; Running.TryRemove(qn, out ignored); }
                    });
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("业务入站主动核对调度失败：" + ex.Message, 10); }
        }

        private static async Task ProbeAsync(QN qn)
        {
            try
            {
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                var response = await qn.GetCurrentConversationID().ConfigureAwait(false);
                var current = response == null ? null : response.Result;
                if (seller.Length == 0 || current == null || string.IsNullOrWhiteSpace(current.Nick)) return;
                var buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, current.Nick);
                if (string.IsNullOrWhiteSpace(buyer)) buyer = current.Nick.Trim();
                var recovered = await qn.ReconcileActiveConversationIngressAsync(seller, buyer, 90).ConfigureAwait(false);
                if (recovered > 0)
                {
                    BotConnectionDiagnostics.RecordCdpStatus(true, "业务推送曾漏事件，已由主动核对恢复" + recovered + "条", seller, buyer);
                    Log.Error("检测到连接存活但业务入站漏事件，已主动补回: seller=" + seller + ", buyer=" + buyer + ", count=" + recovered);
                }

                DateTime panelNext;
                var now = DateTime.UtcNow;
                if (!NextPanelProbeAt.TryGetValue(qn, out panelNext) || panelNext <= now)
                {
                    NextPanelProbeAt[qn] = now.AddSeconds(60);
                    await qn.TryRecoverVisibleOrderPanelForBackgroundProbeAsync(
                        seller, buyer, "runtimePassiveIngressWatchdog", DateTime.Now.AddSeconds(-60), false).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("业务入站主动核对失败：" + ex.Message, 10); }
        }
    }

    public partial class QN
    {
        internal async Task<int> ReconcileActiveConversationIngressAsync(string seller, string buyer, int lookbackSeconds)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (seller.Length == 0 || buyer.Length == 0 || cdp == null || !Params.Robot.CanUseRobotReal) return 0;
            var lookback = Math.Max(30, Math.Min(180, lookbackSeconds));
            if (HasBuyerMessageAfter(seller, buyer, DateTime.Now.AddSeconds(-lookback))) return 0;

            DbEntity.Conversation current;
            try { var response = await GetCurrentConversationID().ConfigureAwait(false); current = response == null ? null : response.Result; }
            catch { return 0; }
            if (current == null || string.IsNullOrWhiteSpace(current.Nick)
                || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer)
                || string.IsNullOrWhiteSpace(current.Ccode)) return 0;

            JObject history;
            try
            {
                history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = current.Ccode, type = 1 }, count = 30, gohistory = 1, msgid = "-1", msgtime = "-1"
                }).ConfigureAwait(false);
            }
            catch { return 0; }
            if (history == null) return 0;

            var threshold = DateTime.Now.AddSeconds(-lookback).Ticks;
            var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>() ?? new List<QNChatMessage>();
            var candidates = messages.Where(m => m != null)
                .Where(m => (IsBuyerMessage(m) && m.fromid != null && BuyerIdentityAliasService.AreEquivalent(seller, m.fromid.nick, buyer)) || IsPotentialRecoveredOrderCard(m))
                .Where(m => { var sort = IncomingMessageSafety.GetSortValue(m); return sort > 0 && sort >= threshold; })
                .OrderBy(IncomingMessageSafety.GetSortValue).ToList();
            if (candidates.Count == 0) return 0;
            var processed = 0;
            foreach (var message in candidates)
            {
                string claimKey;
                if (!ConversationIngressRecoveryLedger.TryClaim(seller, message, string.Empty, out claimKey))
                    continue;
                try
                {
                    await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer, false).ConfigureAwait(false);
                    // Order/system recovery remains useful, but the bridge's return value means
                    // "buyer business ingress was actually recovered". Do not count seller echoes
                    // that downstream dedupe correctly ignores.
                    if (IsBuyerMessage(message)) processed++;
                }
                catch
                {
                    ConversationIngressRecoveryLedger.Release(claimKey);
                    throw;
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
            return processed;
        }
    }

    internal static class RuntimeLogNoiseFilterBootstrap
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<AppenderSkeleton> Filtered = new HashSet<AppenderSkeleton>();
        private static Timer _timer;
        private static int _started;
        private static int _reported;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) _timer = new Timer(_ => Apply(), null, 2500, 10000);
            return new object();
        }

        private static void Apply()
        {
            try
            {
                lock (Sync)
                {
                    var phrases = new[]
                    {
                        "设置界面已将“人工客服工作时间与下班回复”迁移",
                        "设置界面已在构造阶段将“启用转人工规则”",
                        "设置界面已直接构造“转人工策略”页面并迁移转人工规则",
                        "UIA控件刷新成功:",
                        "收到千牛WebSocket事件: type=qnbotStatus",
                        "千牛注入状态:",
                        "检测到卖家重复千牛WebSocket页面，保留已稳定的权威CDP会话",
                        "RPA已绑定卖家专属千牛窗口:",
                        "IMSDK璋冪敤璺熻釜:",
                        "SendForGetText",
                        "后台订单面板延迟兜底订单已由其他通道处理/去重"
                    };
                    var pattern = string.Join("|", phrases.Select(Regex.Escape));
                    foreach (var appender in LogManager.GetRepository().GetAppenders().OfType<AppenderSkeleton>())
                    {
                        if (!Filtered.Add(appender)) continue;
                        var filter = new RegexFilter { RegexToMatch = pattern, AcceptOnMatch = false };
                        filter.ActivateOptions();
                        appender.AddFilter(filter);
                    }
                }
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                    Log.Info("运行日志降噪已启用：保留故障、恢复、真实消息和发送结果，过滤高频成功探测与已完成设置迁移提示。");
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _reported, 1) == 0) Log.ErrorWithMaxCount("运行日志降噪初始化失败：" + ex.Message, 3);
            }
        }
    }
}
