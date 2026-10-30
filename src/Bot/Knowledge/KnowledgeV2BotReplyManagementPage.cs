using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2BotReplyManagementPage : UserControl, IKnowledgeV2Searchable, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private readonly TabControl _tabs = new TabControl();
        private readonly ObservableCollection<BotReplySourceRow> _sourceView = new ObservableCollection<BotReplySourceRow>();
        private readonly ObservableCollection<BotReplyPreset> _presetView = new ObservableCollection<BotReplyPreset>();
        private readonly ObservableCollection<BotReplyKnowledgeResult> _knowledgeView = new ObservableCollection<BotReplyKnowledgeResult>();
        private List<BotConversationHistoryEntity> _sourceAll = new List<BotConversationHistoryEntity>();
        private List<BotReplyPreset> _presets = new List<BotReplyPreset>();
        private string _globalSearch = string.Empty;
        private TextBox _sourceSearch, _sourceQuestion, _sourceAnswer, _sourceEditor;
        private TextBlock _sourceStatus;
        private DataGrid _sourceGrid;
        private BotConversationHistoryEntity _selectedSource;
        private string _pendingSourceRecordId = string.Empty;
        private TextBox _presetSearch, _presetName, _presetText, _presetTrigger, _presetDescription;
        private CheckBox _presetEnabled;
        private TextBlock _presetStatus;
        private DataGrid _presetGrid;
        private BotReplyPreset _selectedPreset;
        private TextBox _kbQuestion, _kbReply, _kbDetail;
        private DataGrid _kbGrid;
        private TextBlock _kbStatus;

        public KnowledgeV2BotReplyManagementPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = (seller ?? string.Empty).Trim();
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void ApplyGlobalSearch(string text)
        {
            _globalSearch = (text ?? string.Empty).Trim();
            FilterSources();
            FilterPresets();
        }

        public void RefreshView()
        {
            RefreshSources();
            RefreshPresets();
        }

        public void OpenSourceById(string recordId, string answer)
        {
            _tabs.SelectedIndex = 0;
            _pendingSourceRecordId = (recordId ?? string.Empty).Trim();
            // Do not filter by answer: identical Bot replies are separate records.
            _sourceSearch.Text = string.Empty;
            RefreshSources();
        }

        public bool OpenPresetById(string presetId)
        {
            _tabs.SelectedIndex = 1;
            presetId = (presetId ?? string.Empty).Trim();
            if (presetId.Length == 0) return false;
            RefreshPresets();
            var exact = _presets.FirstOrDefault(x =>
                string.Equals(x.Id, presetId, StringComparison.Ordinal));
            if (exact == null) return false;
            _presetSearch.Text = string.Empty;
            FilterPresets();
            var row = _presetView.FirstOrDefault(x =>
                string.Equals(x.Id, presetId, StringComparison.Ordinal));
            if (row == null) return false;
            _presetGrid.SelectedItem = row;
            _presetGrid.ScrollIntoView(row);
            LoadPresetEditor(row);
            _presetStatus.Text = "已定位原始话术ID：" + presetId
                + "；编辑并保存会影响后续回复，不会改写历史消息。";
            return true;
        }

        public void OpenSourceSearch(string answer)
        {
            _tabs.SelectedIndex = 0;
            _sourceSearch.Text = answer ?? string.Empty;
            FilterSources();
        }

        public void OpenPresetSearch(string answer)
        {
            _tabs.SelectedIndex = 1;
            _presetSearch.Text = answer ?? string.Empty;
            FilterPresets();
        }

        public void OpenKnowledgeSearch(string question, string answer)
        {
            _kbQuestion.Text = question ?? string.Empty;
            _kbReply.Text = answer ?? string.Empty;
            _tabs.SelectedIndex = 2;
            SearchKnowledge();
        }

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var intro = new TextBlock
            {
                Text = "管理每条已回显 Bot 消息的来源、编辑店铺固定话术，并可直接用 Bot 回复反查知识库中的问题。来源记录与知识记录分开保存。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 2, 4, 10)
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            _tabs.Margin = new Thickness(0, 0, 0, 0);
            _tabs.Items.Add(new TabItem { Header = "Bot消息来源", Content = BuildSourceTab() });
            _tabs.Items.Add(new TabItem { Header = "内置固定话术", Content = BuildPresetTab() });
            _tabs.Items.Add(new TabItem { Header = "用Bot回复检索知识", Content = BuildKnowledgeSearchTab() });
            Grid.SetRow(_tabs, 1);
            root.Children.Add(_tabs);
            Content = root;
        }

        private UIElement BuildSourceTab()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _sourceSearch = new TextBox { Width = 230, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "搜索买家、问题、回复内容或来源" };
            _sourceSearch.TextChanged += delegate { FilterSources(); };
            toolbar.Children.Add(_sourceSearch);
            var refresh = Button("刷新来源", 84); refresh.Click += delegate { RefreshSources(); };
            var save = Button("保存来源", 84); save.Click += delegate { SaveSource(); };
            var find = Button("用该回复检索知识", 140); find.Click += delegate { SearchKnowledgeFromSelected(); };
            toolbar.Children.Add(refresh); toolbar.Children.Add(save); toolbar.Children.Add(find);
            Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

            _sourceGrid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, ItemsSource = _sourceView,
                HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 0, 0, 8)
            };
            _sourceGrid.Columns.Add(Col("发送时间", "Time", 132));
            _sourceGrid.Columns.Add(Col("买家", "Buyer", 110));
            _sourceGrid.Columns.Add(Col("状态", "Status", 75));
            _sourceGrid.Columns.Add(Col("买家问题", "QuestionPreview", 210));
            _sourceGrid.Columns.Add(Col("Bot回复", "AnswerPreview", 250));
            _sourceGrid.Columns.Add(Col("来源", "Source", 220));
            _sourceGrid.SelectionChanged += delegate
            {
                var row = _sourceGrid.SelectedItem as BotReplySourceRow;
                _selectedSource = row == null ? null : row.Entity;
                LoadSourceEditor(_selectedSource);
            };
            Grid.SetRow(_sourceGrid, 1); root.Children.Add(_sourceGrid);

            var editor = new Grid();
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            left.Children.Add(Label("对应买家问题"));
            _sourceQuestion = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 66 };
            left.Children.Add(_sourceQuestion);
            left.Children.Add(Label("Bot实际回复"));
            _sourceAnswer = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 66 };
            left.Children.Add(_sourceAnswer);
            Grid.SetColumn(left, 0); Grid.SetRow(left, 1); editor.Children.Add(left);

            var right = new StackPanel();
            right.Children.Add(Label("来源标签（可修改并持久保存）"));
            _sourceEditor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 66 };
            right.Children.Add(_sourceEditor);
            _sourceStatus = new TextBlock { Text = "选择一条已发送的 Bot 回复后可查看或修改来源。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            right.Children.Add(_sourceStatus);
            Grid.SetColumn(right, 1); Grid.SetRow(right, 1); editor.Children.Add(right);
            Grid.SetRow(editor, 2); root.Children.Add(editor);
            return root;
        }

        private UIElement BuildPresetTab()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _presetSearch = new TextBox { Width = 230, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "筛选话术" };
            _presetSearch.TextChanged += delegate { FilterPresets(); };
            toolbar.Children.Add(_presetSearch);
            var refresh = Button("刷新", 64); refresh.Click += delegate { RefreshPresets(); };
            var add = Button("新增话术", 84); add.Click += delegate { NewPreset(); };
            var save = Button("保存话术", 84); save.Click += delegate { SavePreset(); };
            var delete = Button("删除话术", 84); delete.Click += delegate { DeletePreset(); };
            toolbar.Children.Add(refresh); toolbar.Children.Add(add); toolbar.Children.Add(save); toolbar.Children.Add(delete);
            Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            _presetGrid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, ItemsSource = _presetView,
                HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 0, 8, 0)
            };
            _presetGrid.Columns.Add(Col("名称", "Name", 130));
            _presetGrid.Columns.Add(Col("触发条件", "Trigger", 150));
            _presetGrid.Columns.Add(Col("启用", "Enabled", 55));
            _presetGrid.Columns.Add(Col("话术内容", "Text", 250));
            _presetGrid.SelectionChanged += delegate
            {
                _selectedPreset = _presetGrid.SelectedItem as BotReplyPreset;
                LoadPresetEditor(_selectedPreset);
            };
            Grid.SetColumn(_presetGrid, 0); body.Children.Add(_presetGrid);

            var editor = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            editor.Children.Add(Label("话术名称"));
            _presetName = new TextBox { Height = 30 };
            editor.Children.Add(_presetName);
            editor.Children.Add(Label("触发条件"));
            _presetTrigger = new TextBox { Height = 30 };
            editor.Children.Add(_presetTrigger);
            editor.Children.Add(Label("话术内容"));
            _presetText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 120 };
            editor.Children.Add(_presetText);
            editor.Children.Add(Label("备注"));
            _presetDescription = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55 };
            editor.Children.Add(_presetDescription);
            _presetEnabled = new CheckBox { Content = "启用此话术", Margin = new Thickness(0, 8, 0, 5) };
            editor.Children.Add(_presetEnabled);
            _presetStatus = new TextBlock { Text = "支持按店铺独立编辑、启用/停用、添加或删除；保存后新请求立即生效。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            editor.Children.Add(_presetStatus);
            Grid.SetColumn(editor, 1); body.Children.Add(editor);
            Grid.SetRow(body, 1); root.Children.Add(body);
            var note = new TextBlock
            {
                Text = "说明：固定话术仅影响其触发条件对应的后续新消息；不会修改已经发送给买家的历史消息。",
                Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(note, 2); root.Children.Add(note);
            return root;
        }

        private UIElement BuildKnowledgeSearchTab()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var qrow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            qrow.Children.Add(Label("买家问题"));
            _kbQuestion = new TextBox { Width = 380, Height = 30, Margin = new Thickness(8, 0, 10, 0), ToolTip = "原始买家问题，可选" };
            qrow.Children.Add(_kbQuestion);
            toolbar.Children.Add(qrow);
            var arow = new WrapPanel();
            arow.Children.Add(Label("Bot回复"));
            _kbReply = new TextBox { Width = 520, Height = 30, Margin = new Thickness(8, 0, 10, 0), ToolTip = "粘贴完整 Bot 回复，反查可能对应的知识问题" };
            arow.Children.Add(_kbReply);
            var search = Button("检索对应问题", 110); search.Click += delegate { SearchKnowledge(); };
            arow.Children.Add(search);
            toolbar.Children.Add(arow);
            Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

            _kbGrid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, ItemsSource = _knowledgeView,
                HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 0, 0, 8)
            };
            _kbGrid.Columns.Add(Col("匹配度", "ScoreText", 75));
            _kbGrid.Columns.Add(Col("对应问题/标题", "Title", 260));
            _kbGrid.Columns.Add(Col("类型", "Type", 105));
            _kbGrid.Columns.Add(Col("状态", "Status", 80));
            _kbGrid.Columns.Add(Col("标准答案", "AnswerPreview", 340));
            _kbGrid.SelectionChanged += delegate
            {
                var row = _kbGrid.SelectedItem as BotReplyKnowledgeResult;
                _kbDetail.Text = row == null ? string.Empty
                    : "对应问题：\n" + row.Record.Title
                        + "\n\n知识类型：" + row.Record.Type
                        + "\n来源：" + (row.Record.SourceType ?? string.Empty)
                        + "\n\n标准答案：\n" + (row.Record.Answer ?? string.Empty)
                        + "\n\n匹配依据：" + row.Reason;
            };
            Grid.SetRow(_kbGrid, 1); root.Children.Add(_kbGrid);

            var detailPanel = new StackPanel();
            _kbStatus = new TextBlock { Text = "输入 Bot 回复后检索标准答案相似度，并结合买家问题为候选知识排序。只检索，不会自动修改知识或自动入库。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            detailPanel.Children.Add(_kbStatus);
            _kbDetail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 90 };
            detailPanel.Children.Add(_kbDetail);
            Grid.SetRow(detailPanel, 2); root.Children.Add(detailPanel);
            return root;
        }

        private void RefreshSources()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _sourceStatus.Text = "当前未识别店铺，不能读取店铺级 Bot 回复来源。";
                return;
            }
            _sourceStatus.Text = "正在读取 Bot 回复来源…";
            Task.Run(() => BotConversationHistoryStore.LoadRecentBySeller(_seller, 2000))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _sourceStatus.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _sourceAll = t.Result ?? new List<BotConversationHistoryEntity>();
                    FilterSources();
                    if (!string.IsNullOrWhiteSpace(_pendingSourceRecordId))
                    {
                        var recordId = _pendingSourceRecordId;
                        var exact = _sourceView.FirstOrDefault(x =>
                            string.Equals(x.Entity.EntityId, recordId, StringComparison.Ordinal));
                        _pendingSourceRecordId = string.Empty;
                        if (exact != null)
                        {
                            _sourceGrid.SelectedItem = exact;
                            _sourceGrid.ScrollIntoView(exact);
                            _sourceStatus.Text = "已定位到这条Bot消息的唯一来源记录，可在右侧修改来源标签。";
                        }
                        else
                        {
                            _sourceStatus.Text = "该消息的唯一来源记录未找到或超出90天保留期限，"
                                + "不能自动选择另一条内容相似的消息。";
                        }
                    }
                    else
                    {
                        _sourceStatus.Text = "已加载 " + _sourceAll.Count
                            + " 条已发送/回显记录；历史保留期限为 90 天。";
                    }
                })));
        }

        private void FilterSources()
        {
            if (_sourceView == null) return;
            var query = ((_sourceSearch == null ? string.Empty : _sourceSearch.Text) + " " + _globalSearch).Trim();
            _sourceView.Clear();
            foreach (var entity in (_sourceAll ?? new List<BotConversationHistoryEntity>())
                .Where(x => MatchesSource(x, query))
                .Take(2000))
            {
                _sourceView.Add(new BotReplySourceRow(entity));
            }
        }

        private static bool MatchesSource(BotConversationHistoryEntity x, string query)
        {
            if (x == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var haystack = string.Join(" ", new[]
            {
                x.Buyer, x.Question, x.Answer, x.AnswerSource, x.StatusText, x.StatusDetail
            });
            return query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .All(term => haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LoadSourceEditor(BotConversationHistoryEntity entity)
        {
            _sourceQuestion.Text = entity == null ? string.Empty : entity.Question ?? string.Empty;
            _sourceAnswer.Text = entity == null ? string.Empty : entity.Answer ?? string.Empty;
            _sourceEditor.Text = entity == null ? string.Empty : entity.AnswerSource ?? string.Empty;
            if (_sourceStatus != null && entity != null)
                _sourceStatus.Text = "已选择 " + entity.Buyer + " 的回复记录；修改来源仅调整本地溯源元数据，不会编辑或重发买家已收到的消息。";
        }

        private void SaveSource()
        {
            if (_selectedSource == null)
            {
                MessageBox.Show(_owner, "请先选择一条 Bot 回复记录。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var source = (_sourceEditor.Text ?? string.Empty).Trim();
            if (source.Length == 0)
            {
                MessageBox.Show(_owner, "来源不能为空。未知来源请明确填写“来源未记录（需人工补录）”。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!BotConversationHistoryStore.UpdateAnswerSource(_seller, _selectedSource.EntityId, source))
            {
                MessageBox.Show(_owner, "来源保存失败，请刷新后重试。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // Only the selected transport message is corrected. Future answers
            // must use their own generation evidence instead of this manual label.
            _selectedSource.AnswerSource = source;
            _sourceStatus.Text = "来源已保存至当前消息；其他消息与后续回复不受影响。";
            FilterSources();
        }

        private void SearchKnowledgeFromSelected()
        {
            if (_selectedSource == null)
            {
                MessageBox.Show(_owner, "请先选择一条 Bot 回复记录。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenKnowledgeSearch(_selectedSource.Question, _selectedSource.Answer);
        }

        private void RefreshPresets()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _presetStatus.Text = "当前未识别店铺，固定话术只读。";
                return;
            }
            try
            {
                _presets = BotReplyPresetService.GetPresets(_seller);
                FilterPresets();
                _presetStatus.Text = "已加载 " + _presets.Count + " 条店铺内置话术；更改只影响后续新消息。";
            }
            catch (Exception ex)
            {
                _presetStatus.Text = "读取失败：" + ex.Message;
            }
        }

        private void FilterPresets()
        {
            if (_presetView == null) return;
            var query = ((_presetSearch == null ? string.Empty : _presetSearch.Text) + " " + _globalSearch).Trim();
            _presetView.Clear();
            foreach (var item in (_presets ?? new List<BotReplyPreset>())
                .Where(x => string.IsNullOrWhiteSpace(query)
                    || (x.Name + " " + x.Text + " " + x.Trigger + " " + x.Description).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                _presetView.Add(item);
        }

        private void NewPreset()
        {
            _selectedPreset = new BotReplyPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.Empty,
                Text = string.Empty,
                Trigger = "买家消息包含商品链接",
                Description = string.Empty,
                Enabled = true,
                UpdatedAt = DateTime.Now
            };
            _presetGrid.SelectedItem = null;
            LoadPresetEditor(_selectedPreset);
            _presetName.Focus();
            _presetStatus.Text = "正在新增话术；填写名称和内容后点击“保存话术”。";
        }

        private void LoadPresetEditor(BotReplyPreset item)
        {
            if (item == null) return;
            _presetName.Text = item.Name ?? string.Empty;
            _presetText.Text = item.Text ?? string.Empty;
            _presetTrigger.Text = item.Trigger ?? string.Empty;
            _presetDescription.Text = item.Description ?? string.Empty;
            _presetEnabled.IsChecked = item.Enabled;
        }

        private void SavePreset()
        {
            if (string.IsNullOrWhiteSpace(_seller))
            {
                MessageBox.Show(_owner, "未识别当前店铺，不能保存店铺级固定话术。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_presetName.Text) || string.IsNullOrWhiteSpace(_presetText.Text))
            {
                MessageBox.Show(_owner, "话术名称和话术内容不能为空。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_selectedPreset == null)
                _selectedPreset = new BotReplyPreset { Id = Guid.NewGuid().ToString("N"), UpdatedAt = DateTime.Now };
            _selectedPreset.Name = _presetName.Text.Trim();
            _selectedPreset.Text = _presetText.Text.Trim();
            _selectedPreset.Trigger = string.IsNullOrWhiteSpace(_presetTrigger.Text) ? "买家消息包含商品链接" : _presetTrigger.Text.Trim();
            _selectedPreset.Description = (_presetDescription.Text ?? string.Empty).Trim();
            _selectedPreset.Enabled = _presetEnabled.IsChecked == true;
            _selectedPreset.UpdatedAt = DateTime.Now;

            var index = _presets.FindIndex(x => string.Equals(x.Id, _selectedPreset.Id, StringComparison.Ordinal));
            if (index >= 0) _presets[index] = _selectedPreset;
            else _presets.Add(_selectedPreset);
            try
            {
                BotReplyPresetService.SavePresets(_seller, _presets);
                RefreshPresets();
                _presetGrid.SelectedItem = _presetView.FirstOrDefault(x => x.Id == _selectedPreset.Id);
                _presetStatus.Text = "保存成功，新请求立即使用最新配置；历史消息不会被修改。";
            }
            catch (Exception ex)
            {
                _presetStatus.Text = "保存失败：" + ex.Message;
                MessageBox.Show(_owner, "保存话术失败：" + ex.Message, "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeletePreset()
        {
            if (_selectedPreset == null)
            {
                MessageBox.Show(_owner, "请先选择一条话术。", "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(_owner, "确定删除话术“" + _selectedPreset.Name + "”？已发送消息不会受影响。",
                "删除内置话术", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _presets.RemoveAll(x => string.Equals(x.Id, _selectedPreset.Id, StringComparison.Ordinal));
            try
            {
                BotReplyPresetService.SavePresets(_seller, _presets);
                _selectedPreset = null;
                RefreshPresets();
                _presetName.Text = _presetText.Text = _presetTrigger.Text = _presetDescription.Text = string.Empty;
                _presetEnabled.IsChecked = false;
                _presetStatus.Text = "话术已删除。新消息不再使用此话术。";
            }
            catch (Exception ex)
            {
                _presetStatus.Text = "删除失败：" + ex.Message;
                MessageBox.Show(_owner, "删除话术失败：" + ex.Message, "Bot回复管理", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchKnowledge()
        {
            var question = (_kbQuestion.Text ?? string.Empty).Trim();
            var answer = (_kbReply.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _kbStatus.Text = "当前未识别店铺，不能检索店铺级 Knowledge Center V2。";
                return;
            }
            if (question.Length == 0 && answer.Length == 0)
            {
                _kbStatus.Text = "请粘贴一条 Bot 回复，或同时提供买家问题。";
                return;
            }
            _kbStatus.Text = "正在检索 Knowledge Center V2…";
            _knowledgeView.Clear();
            Task.Run(() => RankKnowledge(_seller, question, answer))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _kbStatus.Text = "检索失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _knowledgeView.Clear();
                    foreach (var item in t.Result) _knowledgeView.Add(item);
                    _kbStatus.Text = "检索完成，共 " + _knowledgeView.Count
                        + " 个候选。优先查看匹配度高、问题标题与买家问题相符的记录；本操作不会自动修改知识。";
                    if (_knowledgeView.Count > 0) _kbGrid.SelectedIndex = 0;
                })));
        }

        private static List<BotReplyKnowledgeResult> RankKnowledge(string seller, string question, string answer)
        {
            var records = KnowledgeEngineV2Repository.LoadAll(seller);
            return records.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title))
                .Select(record =>
                {
                    var titleFields = string.Join(" ", new[]
                    {
                        record.Title, record.Subject, record.Predicate,
                        string.Join(" ", record.Aliases ?? new List<string>()),
                        string.Join(" ", record.Entities ?? new List<string>()),
                        string.Join(" ", record.Conditions ?? new List<string>())
                    });
                    var answerFields = (record.Answer ?? string.Empty) + " " + (record.ShortAnswer ?? string.Empty);
                    var answerScore = Similarity(answer, answerFields);
                    var questionScore = Similarity(question, titleFields);
                    var answerToQuestionScore = Similarity(answer, titleFields);
                    var score = answer.Length == 0
                        ? questionScore
                        : Math.Max(answerScore * 0.68 + questionScore * 0.32,
                            Math.Max(answerScore * 0.78 + answerToQuestionScore * 0.22, questionScore * 0.72));
                    var reason = "答案匹配 " + answerScore.ToString("0.00")
                        + "；问题匹配 " + questionScore.ToString("0.00")
                        + "；回复与标题匹配 " + answerToQuestionScore.ToString("0.00");
                    return new BotReplyKnowledgeResult { Record = record, Score = score, Reason = reason };
                })
                .Where(x => x.Score >= 0.25)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Record.Confidence)
                .Take(100)
                .ToList();
        }

        private static double Similarity(string query, string target)
        {
            var left = NormalizeSearch(query);
            var right = NormalizeSearch(target);
            if (left.Length == 0 || right.Length == 0) return 0;
            if (string.Equals(left, right, StringComparison.Ordinal)) return 1.0;
            if (left.Length >= 3 && right.IndexOf(left, StringComparison.Ordinal) >= 0) return 0.98;
            if (right.Length >= 3 && left.IndexOf(right, StringComparison.Ordinal) >= 0)
                return Math.Min(0.92, 0.70 + (double)right.Length / Math.Max(1, left.Length) * 0.22);

            var leftGrams = CharacterGrams(left);
            var rightGrams = CharacterGrams(right);
            if (leftGrams.Count == 0 || rightGrams.Count == 0) return 0;
            var intersection = leftGrams.Count(x => rightGrams.Contains(x));
            var dice = (2.0 * intersection) / (leftGrams.Count + rightGrams.Count);
            return Math.Max(0, Math.Min(0.90, dice));
        }

        private static HashSet<string> CharacterGrams(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return result;
            if (text.Length == 1) { result.Add(text); return result; }
            for (var i = 0; i < text.Length - 1; i++)
                result.Add(text.Substring(i, 2));
            return result;
        }

        private static string NormalizeSearch(string text)
        {
            text = (text ?? string.Empty).Trim().ToLowerInvariant();
            return Regex.Replace(text, @"[\s\p{P}\p{S}]+", string.Empty);
        }

        private static Button Button(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 3) };
        }

        private static DataGridTextColumn Col(string header, string path, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = width,
                IsReadOnly = true
            };
        }

        private sealed class BotReplySourceRow
        {
            public BotConversationHistoryEntity Entity { get; private set; }
            public string Time { get; private set; }
            public string Buyer { get { return Entity.Buyer ?? string.Empty; } }
            public string Status { get { return Entity.StatusKind ?? string.Empty; } }
            public string QuestionPreview { get { return Clip(Entity.Question, 72); } }
            public string AnswerPreview { get { return Clip(Entity.Answer, 100); } }
            public string Source { get { return Entity.AnswerSource ?? string.Empty; } }

            public BotReplySourceRow(BotConversationHistoryEntity entity)
            {
                Entity = entity;
                DateTime time;
                try { time = entity.CreatedAtTicks > 0 ? new DateTime(entity.CreatedAtTicks) : DateTime.MinValue; }
                catch { time = DateTime.MinValue; }
                Time = time == DateTime.MinValue ? "时间未记录" : time.ToString("yyyy-MM-dd HH:mm:ss");
            }

            private static string Clip(string value, int max)
            {
                value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                return value.Length <= max ? value : value.Substring(0, max) + "…";
            }
        }

        private sealed class BotReplyKnowledgeResult
        {
            public KnowledgeV2Record Record { get; set; }
            public double Score { get; set; }
            public string ScoreText { get { return (Score * 100).ToString("0.0") + "%"; } }
            public string Title { get { return Record == null ? string.Empty : Record.Title ?? string.Empty; } }
            public string Type { get { return Record == null ? string.Empty : Record.Type ?? string.Empty; } }
            public string Status { get { return Record == null ? string.Empty : Record.Status ?? string.Empty; } }
            public string AnswerPreview
            {
                get
                {
                    var value = Record == null ? string.Empty : Record.Answer ?? string.Empty;
                    value = value.Replace("\r", " ").Replace("\n", " ").Trim();
                    return value.Length <= 130 ? value : value.Substring(0, 130) + "…";
                }
            }
            public string Reason { get; set; }
        }
    }

    internal static class BotReplySourceEditorWindow
    {
        public static string Edit(Window owner, string current)
        {
            var window = new Window
            {
                Title = "编辑 Bot 消息来源",
                Width = 560,
                Height = 300,
                MinWidth = 480,
                MinHeight = 250,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
            };
            if (owner != null) window.Owner = owner;
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = "填写真实的生成来源或明确的待核实说明。此操作只修改本地来源元数据，不会修改已发送给买家的消息。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var editor = new TextBox
            {
                Text = current ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(editor, 1); root.Children.Add(editor);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = "保存来源", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "取消", Width = 80, Height = 30 };
            save.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(editor.Text))
                {
                    MessageBox.Show(window, "来源不能为空。", "来源管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                window.DialogResult = true;
            };
            cancel.Click += delegate { window.DialogResult = false; };
            buttons.Children.Add(save); buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2); root.Children.Add(buttons);
            window.Content = root;
            return window.ShowDialog() == true ? (editor.Text ?? string.Empty).Trim() : null;
        }
    }
}
