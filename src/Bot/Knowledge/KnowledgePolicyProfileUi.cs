using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class KnowledgePolicyProfileUi
    {
        private static readonly ConditionalWeakTable<KnowledgeManagerControl, object> FuzzySearchAttached =
            new ConditionalWeakTable<KnowledgeManagerControl, object>();
        private static readonly object FuzzySearchAttachSync = new object();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeManagerControl),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnManagerLoaded),
                true);
        }

        private static void OnManagerLoaded(object sender, RoutedEventArgs e)
        {
            var manager = sender as KnowledgeManagerControl;
            if (manager == null) return;
            AttachManagerFuzzySearch(manager);

            var top = FindFirst<WrapPanel>(manager);
            if (top == null) return;
            if (top.Children.OfType<Button>().Any(x => Convert.ToString(x.Tag) == "knowledge-policy-profile")) return;

            var button = new Button
            {
                Content = "知识策略",
                Width = 86,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Tag = "knowledge-policy-profile",
                ToolTip = "设置每条知识的回答模式、适用条件和必要上下文，并查看客服修正/撤回形成的可靠度。"
            };
            button.Click += (s, args) =>
            {
                var owner = Window.GetWindow(manager);
                var shop = ShopSettingsScope.Current ?? ShopScopedUiBridge.Get(owner);
                var window = new KnowledgePolicyProfileWindow(shop)
                {
                    Owner = owner
                };
                if (shop != null) ShopScopedUiBridge.Attach(window, shop);
                window.ShowDialog();
            };
            top.Children.Add(button);
        }

        private static void AttachManagerFuzzySearch(KnowledgeManagerControl manager)
        {
            lock (FuzzySearchAttachSync)
            {
                object marker;
                if (FuzzySearchAttached.TryGetValue(manager, out marker)) return;
                try
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var type = typeof(KnowledgeManagerControl);
                    var searchField = type.GetField("_search", flags);
                    var categoryField = type.GetField("_cat", flags);
                    var allField = type.GetField("_all", flags);
                    var viewField = type.GetField("_view", flags);
                    var countField = type.GetField("_count", flags);
                    var search = searchField == null ? null : searchField.GetValue(manager) as TextBox;
                    var category = categoryField == null ? null : categoryField.GetValue(manager) as ComboBox;
                    if (search == null || category == null || allField == null || viewField == null) return;

                    search.ToolTip = "可搜索问题、答案、关键词和来源；原问题被AI改写后，也会按关键业务词进行智能匹配。";
                    Action apply = () =>
                    {
                        var query = (search.Text ?? string.Empty).Trim();
                        if (query.Length == 0) return;
                        var all = allField.GetValue(manager) as List<KnowledgeBaseEntry>;
                        var view = viewField.GetValue(manager) as ObservableCollection<KnowledgeBaseEntry>;
                        if (all == null || view == null) return;
                        var selectedCategory = category.SelectedItem as string ?? "全部分类";
                        var matches = all
                            .Where(x => x != null
                                && (selectedCategory == "全部分类"
                                    || string.Equals(x.Category ?? string.Empty, selectedCategory, StringComparison.Ordinal))
                                && ManagerMatches(x, query))
                            .ToList();
                        view.Clear();
                        foreach (var item in matches) view.Add(item);
                        var count = countField == null ? null : countField.GetValue(manager) as TextBlock;
                        if (count != null)
                        {
                            count.Text = "共 " + all.Count + " 条知识，当前显示 " + view.Count
                                + " 条（已启用原问题/同义关键词智能搜索）";
                        }
                    };

                    search.TextChanged += (sender, args) => apply();
                    category.SelectionChanged += (sender, args) => apply();
                    var top = FindFirst<WrapPanel>(manager);
                    var searchButton = top == null
                        ? null
                        : top.Children.OfType<Button>()
                            .FirstOrDefault(x => string.Equals(Convert.ToString(x.Content), "搜索", StringComparison.Ordinal));
                    if (searchButton != null) searchButton.Click += (sender, args) => apply();
                    FuzzySearchAttached.Add(manager, new object());
                }
                catch
                {
                    FuzzySearchAttached.Remove(manager);
                }
            }
        }

        private static bool ManagerMatches(KnowledgeBaseEntry item, string query)
        {
            var haystack = string.Join(" ", new[]
            {
                item.Category,
                item.Title,
                item.Answer,
                item.Keywords,
                item.SourceType
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var normalizedHaystack = NormalizeSearch(haystack);
            var normalizedQuery = NormalizeSearch(query);
            if (normalizedQuery.Length > 0 && normalizedHaystack.Contains(normalizedQuery)) return true;
            var terms = BuildSearchTerms(query);
            return terms.Count > 0 && terms.All(normalizedHaystack.Contains);
        }

        internal static List<string> BuildSearchTerms(string query)
        {
            var value = (query ?? string.Empty).ToLowerInvariant();
            foreach (var filler in new[]
            {
                "可以不可以", "能不能", "可不可以", "是否可以", "能否", "是否", "可以",
                "请问", "麻烦", "帮我", "一下", "怎么", "如何", "用到", "使用", "支持",
                "这个", "那个", "上面的", "里面的", "上的", "的吗", "么", "吗", "呢", "亲"
            })
            {
                value = value.Replace(filler, " ");
            }
            return Regex.Matches(value, @"[a-z0-9]+|[\u3400-\u9fff]+", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(x => NormalizeSearch(x.Value))
                .Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        internal static string NormalizeSearch(string value)
        {
            return Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty);
        }

        private static T FindFirst<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var direct = root as T;
            if (direct != null) return direct;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirst<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            if (root is ContentControl)
            {
                var found = FindFirst<T>(((ContentControl)root).Content as DependencyObject);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class KnowledgePolicyProfileWindow : Window
    {
        private readonly DataGrid _grid;
        private readonly TextBox _search;
        private readonly TextBlock _filterStats;
        private readonly ComboBox _mode;
        private readonly TextBox _intent;
        private readonly TextBox _entities;
        private readonly TextBox _applyWhen;
        private readonly TextBox _doNotApplyWhen;
        private readonly TextBox _requiredContext;
        private readonly TextBlock _stats;
        private readonly List<KnowledgeBaseEntry> _knowledge;
        private List<KnowledgePolicyProfile> _profiles;
        private bool _loading;
        private readonly ShopContext _shop;
        private readonly CheckBox _enabled;

        public KnowledgePolicyProfileWindow(ShopContext shop = null)
        {
            _shop = shop ?? ShopSettingsScope.Current;
            Title = "知识策略与可靠度";
            Width = 1080;
            Height = 720;
            MinWidth = 880;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);

            var root = new Grid { Margin = new Thickness(14) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            Content = root;

            var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(left, 0);
            root.Children.Add(left);
            var intro = new TextBlock
            {
                Text = "可靠度会根据人工客服对 Bot/知识答案的保留、修正和撤回行为自动变化。低可靠度知识会自动失去直接回复资格。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(intro, Dock.Top);
            left.Children.Add(intro);

            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(searchRow, Dock.Top);
            left.Children.Add(searchRow);

            var clear = new Button
            {
                Content = "清空",
                Width = 62,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(clear, Dock.Right);
            searchRow.Children.Add(clear);

            _search = new TextBox
            {
                Height = 28,
                ToolTip = "可搜索问题、答案、关键词、分类、来源、意图、业务实体、适用/禁用条件和最近证据。支持按人工修改前的原问题关键词查找。"
            };
            searchRow.Children.Add(_search);
            _search.TextChanged += (s, e) => ApplySearch();
            clear.Click += (s, e) =>
            {
                _search.Text = string.Empty;
                _search.Focus();
            };

            _filterStats = new TextBlock
            {
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(_filterStats, Dock.Top);
            left.Children.Add(_filterStats);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "问题", Binding = new Binding("QuestionSnapshot"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "模式", Binding = new Binding("AnswerModeDisplay"), Width = 110 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "可靠度", Binding = new Binding("ReliabilityDisplay"), Width = 75 });
            _grid.SelectionChanged += (s, e) => LoadSelected();
            left.Children.Add(_grid);

            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            var form = new StackPanel();
            right.Content = form;

            _enabled = new CheckBox
            {
                Content = "启用知识策略与可靠度",
                IsChecked = KnowledgePolicyProfileService.IsEnabled(_shop),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "关闭后不再用每条知识的回答模式、适用/禁用/必要上下文和可靠度限制直答；仍保留基础相关度、上下文依赖与高风险安全判断。"
            };
            _enabled.Click += (s, e) => KnowledgePolicyProfileService.SetEnabled(_shop, _enabled.IsChecked == true);
            form.Children.Add(_enabled);
            form.Children.Add(new TextBlock
            {
                Text = "关闭仅绕过本页策略/可靠度约束，不会关闭知识库，也不会绕过订单流程、上下文或高风险安全校验。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            });

            AddLabel(form, "回答模式");
            _mode = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            _mode.Items.Add(new ComboBoxItem { Content = "自动判断", Tag = KnowledgeAnswerModes.Auto });
            _mode.Items.Add(new ComboBoxItem { Content = "优先直答", Tag = KnowledgeAnswerModes.Direct });
            _mode.Items.Add(new ComboBoxItem { Content = "必须结合上下文", Tag = KnowledgeAnswerModes.Contextual });
            _mode.Items.Add(new ComboBoxItem { Content = "仅作为事实约束", Tag = KnowledgeAnswerModes.Constraint });
            form.Children.Add(_mode);

            AddLabel(form, "意图（可选）");
            _intent = AddBox(form, false, 32, "例如 capability / how_to / troubleshoot；留空使用自动识别。");
            AddLabel(form, "业务实体（逗号分隔）");
            _entities = AddBox(form, false, 32, "例如 电视会员,手机端,酷狗；用于知识重排。");
            AddLabel(form, "适用条件");
            _applyWhen = AddBox(form, true, 74, "每行或分号分隔。满足时提高相关度；未满足时不会直接回复。例：当前商品为电视会员");
            AddLabel(form, "禁用条件");
            _doNotApplyWhen = AddBox(form, true, 74, "命中任一条件时，本条知识完全不参与当前回复。例：当前咨询手机端会员");
            AddLabel(form, "必要上下文");
            _requiredContext = AddBox(form, true, 74, "缺少时强制结合上下文，不允许固定直答。例：已确认购买电视端会员");

            _stats = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.WhiteSmoke,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 12)
            };
            form.Children.Add(_stats);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var importAll = new Button { Content = "导入全部", Width = 82, Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = "knowledge-policies-import-full" };
            importAll.Click += (s, e) => RulePolicyImportExportUi.ImportKnowledgePolicies(this);
            buttons.Children.Add(importAll);
            var exportAll = new Button { Content = "导出全部", Width = 82, Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = "knowledge-policies-export-full" };
            exportAll.Click += (s, e) => RulePolicyImportExportUi.ExportKnowledgePolicies(this);
            buttons.Children.Add(exportAll);
            var save = new Button { Content = "保存策略", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) => SaveSelected();
            buttons.Children.Add(save);
            var close = new Button { Content = "关闭", Width = 80, Height = 30 };
            close.Click += (s, e) => Close();
            buttons.Children.Add(close);
            form.Children.Add(buttons);

            ApplySearch();
        }

        private static void AddLabel(Panel panel, string text)
        {
            panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 5) });
        }

        private static TextBox AddBox(Panel panel, bool multiline, double height, string tooltip)
        {
            var box = new TextBox
            {
                Height = height,
                AcceptsReturn = multiline,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                ToolTip = tooltip,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(box);
            return box;
        }

        private void ApplySearch()
        {
            if (_grid == null || _search == null) return;
            var selected = _grid.SelectedItem as KnowledgePolicyProfile;
            var selectedId = selected == null ? string.Empty : selected.KnowledgeId;
            var query = (_search.Text ?? string.Empty).Trim();
            var filtered = (_profiles ?? new List<KnowledgePolicyProfile>())
                .Where(x => MatchesSearch(x, query))
                .ToList();
            _grid.ItemsSource = filtered;
            _filterStats.Text = "共 " + (_profiles == null ? 0 : _profiles.Count)
                + " 条策略，当前显示 " + filtered.Count + " 条";

            var restore = filtered.FirstOrDefault(x => string.Equals(x.KnowledgeId, selectedId, StringComparison.Ordinal));
            _grid.SelectedItem = restore ?? filtered.FirstOrDefault();
            if (_grid.SelectedItem == null)
            {
                ClearDetails();
            }
        }

        private bool MatchesSearch(KnowledgePolicyProfile profile, string query)
        {
            if (profile == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var entry = FindKnowledge(profile);
            var haystack = string.Join(" ", new[]
            {
                profile.QuestionSnapshot,
                profile.AnswerModeDisplay,
                profile.Intent,
                profile.Entities,
                profile.ApplyWhen,
                profile.DoNotApplyWhen,
                profile.RequiredContext,
                profile.LastEvidenceType,
                entry == null ? string.Empty : entry.Title,
                entry == null ? string.Empty : entry.Answer,
                entry == null ? string.Empty : entry.Keywords,
                entry == null ? string.Empty : entry.Category,
                entry == null ? string.Empty : entry.SourceType
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var normalizedHaystack = KnowledgePolicyProfileUi.NormalizeSearch(haystack);
            var normalizedQuery = KnowledgePolicyProfileUi.NormalizeSearch(query);
            if (normalizedQuery.Length > 0 && normalizedHaystack.Contains(normalizedQuery)) return true;

            var terms = KnowledgePolicyProfileUi.BuildSearchTerms(query);
            return terms.Count > 0 && terms.All(normalizedHaystack.Contains);
        }

        private KnowledgeBaseEntry FindKnowledge(KnowledgePolicyProfile profile)
        {
            if (profile == null) return null;
            var byId = _knowledge.FirstOrDefault(x => x != null
                && string.Equals(x.Id ?? string.Empty, profile.KnowledgeId ?? string.Empty, StringComparison.Ordinal));
            if (byId != null) return byId;
            return _knowledge.FirstOrDefault(x => x != null
                && KnowledgeAiService.NormalizeQuestion(x.Title)
                    == KnowledgeAiService.NormalizeQuestion(profile.QuestionSnapshot));
        }

        private void ClearDetails()
        {
            _loading = true;
            try
            {
                _mode.SelectedIndex = 0;
                _intent.Text = string.Empty;
                _entities.Text = string.Empty;
                _applyWhen.Text = string.Empty;
                _doNotApplyWhen.Text = string.Empty;
                _requiredContext.Text = string.Empty;
                _stats.Text = "没有匹配的知识策略。";
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadSelected()
        {
            if (_loading) return;
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            _loading = true;
            try
            {
                foreach (ComboBoxItem item in _mode.Items)
                {
                    if (string.Equals(Convert.ToString(item.Tag), KnowledgeAnswerModes.Normalize(profile.AnswerMode), StringComparison.Ordinal))
                    {
                        _mode.SelectedItem = item;
                        break;
                    }
                }
                if (_mode.SelectedItem == null) _mode.SelectedIndex = 0;
                _intent.Text = profile.Intent ?? string.Empty;
                _entities.Text = profile.Entities ?? string.Empty;
                _applyWhen.Text = profile.ApplyWhen ?? string.Empty;
                _doNotApplyWhen.Text = profile.DoNotApplyWhen ?? string.Empty;
                _requiredContext.Text = profile.RequiredContext ?? string.Empty;

                var entry = FindKnowledge(profile);
                _stats.Text = "可靠度：" + profile.ReliabilityDisplay
                    + "\n直答选择：" + profile.DirectSelectedCount
                    + "；上下文使用：" + profile.ContextualSelectedCount
                    + "；人工确认：" + profile.AcceptedCount
                    + "；人工修正：" + profile.SellerCorrectionCount
                    + "；Bot撤回后修正：" + profile.SellerWithdrawCount
                    + "\n最近证据：" + (string.IsNullOrWhiteSpace(profile.LastEvidenceType) ? "暂无" : profile.LastEvidenceType)
                    + "；更新时间：" + (profile.UpdatedAt ?? string.Empty)
                    + (entry == null
                        ? "\n对应知识：未找到，请关闭窗口并刷新知识库后重试。"
                        : "\n知识状态：" + (entry.Enabled ? "已启用" : "已停用")
                            + "；分类：" + (entry.Category ?? string.Empty)
                            + "；来源：" + (entry.SourceType ?? string.Empty)
                            + "；知识更新时间：" + (entry.UpdatedAt ?? string.Empty)
                            + "\n知识答案：" + (entry.Answer ?? string.Empty));
            }
            finally
            {
                _loading = false;
            }
        }

        private void SaveSelected()
        {
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            var entry = FindKnowledge(profile);
            if (entry == null)
            {
                MessageBox.Show("未找到对应知识条目，请刷新知识库后重试。", "知识策略", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedMode = _mode.SelectedItem as ComboBoxItem;
            profile.AnswerMode = selectedMode == null ? KnowledgeAnswerModes.Auto : Convert.ToString(selectedMode.Tag);
            profile.Intent = _intent.Text;
            profile.Entities = _entities.Text;
            profile.ApplyWhen = _applyWhen.Text;
            profile.DoNotApplyWhen = _doNotApplyWhen.Text;
            profile.RequiredContext = _requiredContext.Text;
            KnowledgePolicyProfileService.SaveProfile(entry, profile);

            var selectedId = profile.KnowledgeId;
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);
            ApplySearch();
            var current = (_grid.ItemsSource as IEnumerable<KnowledgePolicyProfile>)
                .FirstOrDefault(x => x.KnowledgeId == selectedId);
            if (current != null) _grid.SelectedItem = current;
            LoadSelected();
        }
    }
}
