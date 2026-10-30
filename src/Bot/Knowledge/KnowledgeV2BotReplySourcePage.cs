using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Search known Bot reply/question relationships, existing Knowledge V2 answers,
    /// and explicitly correct a source claim without rewriting original evidence.
    /// </summary>
    internal sealed class KnowledgeV2BotReplySourcePage : UserControl, IKnowledgeV2Searchable, IKnowledgeV2Refreshable
    {
        private sealed class SearchRow
        {
            public string Id { get; set; }
            public string Category { get; set; }
            public string Reply { get; set; }
            public string Question { get; set; }
            public string Origin { get; set; }
            public string State { get; set; }
            public string At { get; set; }
            public BotReplySourceRow SourceRow { get; set; }
        }

        private readonly string _seller;
        private readonly ObservableCollection<SearchRow> _rows = new ObservableCollection<SearchRow>();
        private readonly DataGrid _grid;
        private readonly TextBox _search;
        private readonly TextBox _details;
        private readonly TextBox _corrected;
        private readonly TextBox _reason;
        private readonly TextBlock _status;

        public KnowledgeV2BotReplySourcePage(string seller)
        {
            _seller = seller ?? string.Empty;
            var root = new Grid { Margin = new Thickness(4) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
            bar.Children.Add(new TextBlock { Text = "Bot 回复反查问题/来源：", VerticalAlignment = VerticalAlignment.Center });
            _search = new TextBox { Width = 320, Height = 30 };
            _search.KeyDown += (s,e) => { if (e.Key == System.Windows.Input.Key.Enter) RefreshView(); };
            bar.Children.Add(_search);
            var go = new Button { Content = "检索", Width = 72, Height = 30, Margin = new Thickness(8, 0, 6, 0) };
            go.Click += (s,e) => RefreshView();
            bar.Children.Add(go);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            _grid = new DataGrid
            {
                ItemsSource = _rows, AutoGenerateColumns = false,
                IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "类别", Binding = new Binding("Category"), Width = 100 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Bot 回复", Binding = new Binding("Reply"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "关联问题", Binding = new Binding("Question"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding("Origin"), Width = 200 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding("State"), Width = 115 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "时间", Binding = new Binding("At"), Width = 148 });
            _grid.SelectionChanged += (s,e) => LoadSelected();
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var editor = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
            editor.Children.Add(new TextBlock { Text = "消息来源详情（历史已发送消息不随更正改变）",
                FontSize = 16, FontWeight = FontWeights.SemiBold });
            _details = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 90
            };
            editor.Children.Add(_details);
            editor.Children.Add(new TextBlock { Text = "人工更正来源（保留原始记录）", Margin = new Thickness(0, 6, 0, 2) });
            _corrected = new TextBox { Height = 28, MaxLength = 200 };
            editor.Children.Add(_corrected);
            editor.Children.Add(new TextBlock { Text = "修改原因（必填）", Margin = new Thickness(0, 6, 0, 2) });
            _reason = new TextBox { Height = 28, MaxLength = 500 };
            editor.Children.Add(_reason);
            var save = new Button
            {
                Content = "保存来源更正", Width = 118, Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0)
            };
            save.Click += (s,e) => SaveCorrection();
            editor.Children.Add(save);
            Grid.SetRow(editor, 2);
            root.Children.Add(editor);

            _status = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);
            Content = root;
            Loaded += delegate { RefreshView(); };
        }

        public void ApplyGlobalSearch(string text)
        {
            _search.Text = text ?? string.Empty;
            RefreshView();
        }

        public void SearchReply(string answer)
        {
            _search.Text = answer ?? string.Empty;
            RefreshView();
        }

        public void RefreshView()
        {
            if (_seller.Length == 0)
            {
                _status.Text = "当前店铺不可用，无法跨店查询来源。";
                return;
            }
            try
            {
                var query = (_search.Text ?? string.Empty).Trim();
                _rows.Clear();
                foreach (var row in BotReplySourceLedger.Search(_seller, query))
                {
                    _rows.Add(new SearchRow
                    {
                        Id = row.Id,
                        Category = "Bot 发送记录",
                        Reply = row.ReplyPreview,
                        Question = row.Question,
                        Origin = row.EffectiveSource,
                        State = row.ConfirmedAtTicks > 0 ? "千牛已回显" : "仅答案已生成",
                        At = SafeTime(row.ConfirmedAtTicks > 0 ? row.ConfirmedAtTicks : row.PreparedAtTicks),
                        SourceRow = row
                    });
                }

                // V2 knowledge is searched independently, not inferred solely by
                // similar-looking answers in historical chat messages.
                if (query.Length > 0)
                {
                    foreach (var record in KnowledgeEngineV2Repository.LoadAll(_seller).Where(x =>
                        x != null && !string.IsNullOrWhiteSpace(x.Answer) &&
                        (x.Answer.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                          || (x.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                          || (x.ShortAnswer ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Take(250))
                    {
                        _rows.Add(new SearchRow
                        {
                            Id = record.Id,
                            Category = "V2 知识",
                            Reply = record.Answer,
                            Question = record.Title,
                            Origin = (record.SourceType ?? "知识库") + " / " + record.Id,
                            State = record.Enabled ? "已启用" : "未启用",
                            At = record.UpdatedAt == DateTime.MinValue ? "" : record.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                    foreach (var preset in BotReplyPresetService.GetPresets(_seller).Where(x =>
                        (x.Text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(100))
                    {
                        _rows.Add(new SearchRow
                        {
                            Id = preset.Id, Category = "内置话术",
                            Reply = preset.Text, Question = preset.Trigger,
                            Origin = BotReplyPresetService.BuildSourceLabel(preset),
                            State = preset.Enabled ? "已启用" : "已停用",
                            At = preset.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                }
                _status.Text = "共找到 " + _rows.Count + " 条（Bot发送记录、已存在的V2知识、内置话术分别展示；仅检索，不自动新增知识）。"
                    + " 历史来源缺失时只能显示未知，不会把相似话术冒充真实命中。";
                if (_rows.Count > 0) _grid.SelectedIndex = 0;
            }
            catch (Exception ex) { _status.Text = "检索失败：" + ex.Message; }
        }

        private static string SafeTime(long ticks)
        {
            try { return ticks <= 0 ? "" : new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return ""; }
        }

        private void LoadSelected()
        {
            var row = _grid.SelectedItem as SearchRow;
            _details.Text = row == null ? ""
                : "类别：" + row.Category
                    + "\n回复：" + row.Reply
                    + "\n对应问题：" + row.Question
                    + "\n原始来源：" + (row.SourceRow == null ? row.Origin : row.SourceRow.Origin)
                    + "\n当前来源：" + row.Origin
                    + "\n记录ID：" + row.Id;
            _corrected.Text = row == null || row.SourceRow == null ? "" : row.SourceRow.CorrectedOrigin ?? "";
            _reason.Text = row == null || row.SourceRow == null ? "" : row.SourceRow.CorrectionReason ?? "";
            _corrected.IsEnabled = row != null && row.SourceRow != null;
            _reason.IsEnabled = _corrected.IsEnabled;
        }

        private void SaveCorrection()
        {
            var row = _grid.SelectedItem as SearchRow;
            if (row == null || row.SourceRow == null)
            {
                _status.Text = "只能更正已记录的 Bot 消息来源。内置话术请到“内置话术”页修改，V2知识请到“知识”页修改。";
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                "仅更正此条 Bot 来源的管理标注，保留生成时的原始来源与已发送消息。继续？",
                "更正来源", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                BotReplySourceLedger.CorrectSource(_seller, row.Id, _corrected.Text, _reason.Text);
                RefreshView();
                _status.Text = "已保留原始来源，并保存人工更正与原因。";
            }
            catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
        }
    }
}
