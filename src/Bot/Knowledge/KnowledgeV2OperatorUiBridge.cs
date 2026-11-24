using BotLib;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.Knowledge
{
    /// <summary>
    /// Operator-facing Knowledge V2 additions for one-sentence AI entry creation.
    /// History-chat organization is now a first-class page in the V2 left navigation rather than
    /// a dynamically injected header button. Manual structured editing remains available for
    /// existing rows, while new rows can be generated through AI.
    /// </summary>
    internal static class KnowledgeV2OperatorUiBridge
    {
        private static readonly ConditionalWeakTable<KnowledgeV2RecordsPage, object> Pages = new ConditionalWeakTable<KnowledgeV2RecordsPage, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(typeof(KnowledgeV2RecordsPage), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnRecordsPageLoaded), true);
            KnowledgeV2SettingsOperationAudit.Initialize();
        }

        private static void OnRecordsPageLoaded(object sender, RoutedEventArgs e)
        {
            var page = sender as KnowledgeV2RecordsPage; if (page == null) return;
            object marker; if (Pages.TryGetValue(page, out marker)) return;
            try { Pages.Add(page, new object()); } catch { return; }
            page.Dispatcher.BeginInvoke(new Action(() => InjectAiCreate(page)));
        }

        private static void InjectAiCreate(KnowledgeV2RecordsPage page)
        {
            var toolbar = FindToolbar(page);
            if (toolbar == null) return;
            var legacyAdd = toolbar.Children.OfType<Button>().FirstOrDefault(x => string.Equals(Convert.ToString(x.Content), "新增知识", StringComparison.Ordinal));
            if (legacyAdd == null) return;
            legacyAdd.Visibility = Visibility.Collapsed;

            var input = new TextBox
            {
                Width = 330, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "只需输入一句业务知识，例如：电视端会员支持某应用登录，购买后在电视端使用手机号登录即可。AI会自动生成标题、类型、Intent、Subject、Predicate、实体、同义问法、标准答案、条件、风险和可信度等字段。"
            };
            var create = new Button { Content = "AI一句话新增", Width = 112, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var index = toolbar.Children.IndexOf(legacyAdd);
            toolbar.Children.Insert(index, input);
            toolbar.Children.Insert(index + 1, create);

            create.Click += async delegate
            {
                var sentence = (input.Text ?? string.Empty).Trim();
                Log.Info("KnowledgeV2 AI一句话新增按钮点击: inputChars=" + sentence.Length);
                if (sentence.Length == 0)
                {
                    Log.Info("KnowledgeV2 AI一句话新增已阻止: reason=empty_input");
                    MessageBox.Show(Window.GetWindow(page), "请输入一句要新增的知识。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Information);
                    input.Focus(); return;
                }
                var seller = KnowledgeCenterV2Context.ResolveSeller(Window.GetWindow(page));
                if (string.IsNullOrWhiteSpace(seller))
                {
                    Log.Info("KnowledgeV2 AI一句话新增已阻止: reason=seller_unresolved");
                    MessageBox.Show(Window.GetWindow(page), "未识别当前店铺，无法新增知识。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Warning); return;
                }
                create.IsEnabled = false; input.IsEnabled = false; create.Content = "AI生成中...";
                try
                {
                    var mode = ResolveMode(page);
                    Log.Info("KnowledgeV2 AI一句话新增开始生成: seller=" + Safe(seller, 80) + ", mode=" + mode + ", inputChars=" + sentence.Length);
                    var record = await KnowledgeV2NaturalLanguageService.GenerateAsync(sentence, mode, CancellationToken.None);
                    Log.Info("KnowledgeV2 AI一句话新增生成成功，准备写库: seller=" + Safe(seller, 80)
                        + ", type=" + Safe(record == null ? string.Empty : record.Type, 80)
                        + ", status=" + Safe(record == null ? string.Empty : record.Status, 40)
                        + ", answerChars=" + (record == null || record.Answer == null ? 0 : record.Answer.Length));
                    await Task.Run(() =>
                    {
                        KnowledgeEngineV2Repository.Save(seller, record);
                        KnowledgeEngineV2Service.Warm(seller);
                    });
                    Log.Info("KnowledgeV2 AI一句话新增写库成功: seller=" + Safe(seller, 80)
                        + ", recordId=" + Safe(record == null ? string.Empty : record.Id, 64));
                    input.Clear(); page.RefreshView();
                    MessageBox.Show(Window.GetWindow(page), "已由 AI 自动生成全部结构化字段并加入 Knowledge Center V2。\n\n标题：" + record.Title + "\n类型：" + record.Type + "\n标准答案：" + record.Answer, "新增完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Log.Info("KnowledgeV2 AI一句话新增失败且未写库: seller=" + Safe(seller, 80)
                        + ", error=" + Safe(ex.Message, 300));
                    MessageBox.Show(Window.GetWindow(page), "AI 新增失败：" + ex.Message + "\n\n本次没有写入知识库。", "AI一句话新增", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    create.Content = "AI一句话新增"; create.IsEnabled = true; input.IsEnabled = true;
                    Log.Info("KnowledgeV2 AI一句话新增操作结束: seller=" + Safe(seller, 80));
                }
            };
        }

        private static KnowledgeV2RecordsPageMode ResolveMode(KnowledgeV2RecordsPage page)
        {
            var field = typeof(KnowledgeV2RecordsPage).GetField("_mode", BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? KnowledgeV2RecordsPageMode.All : (KnowledgeV2RecordsPageMode)field.GetValue(page);
        }

        private static WrapPanel FindToolbar(DependencyObject root)
        {
            if (root == null) return null;
            var wrap = root as WrapPanel;
            if (wrap != null && wrap.Children.OfType<Button>().Any(x => string.Equals(Convert.ToString(x.Content), "新增知识", StringComparison.Ordinal))) return wrap;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindToolbar(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        internal static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    /// <summary>
    /// Central routed-event audit for every operator action exposed by the Knowledge Center V2
    /// Settings page. It intentionally records only setting names, numeric/mode values and result
    /// status; no API keys, passwords, prompt bodies or complete knowledge content are logged.
    /// </summary>
    internal static class KnowledgeV2SettingsOperationAudit
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), ButtonBase.ClickEvent,
                new RoutedEventHandler(OnButtonClick), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), ToggleButton.CheckedEvent,
                new RoutedEventHandler(OnToggleChanged), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), ToggleButton.UncheckedEvent,
                new RoutedEventHandler(OnToggleChanged), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), Selector.SelectionChangedEvent,
                new SelectionChangedEventHandler(OnSelectionChanged), true);
            EventManager.RegisterClassHandler(typeof(KnowledgeV2SettingsPage), TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(OnTextChanged), true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var page = sender as KnowledgeV2SettingsPage;
            if (page == null || !ReferenceEquals(e.OriginalSource, page)) return;
            Log.Info("KnowledgeV2 设置页打开: seller=" + Seller(page));
            ScheduleSnapshot(page, "loaded");
        }

        private static void OnButtonClick(object sender, RoutedEventArgs e)
        {
            var page = sender as KnowledgeV2SettingsPage;
            if (page == null) return;
            var button = e.Source as Button;
            if (button == null) return;
            var action = Convert.ToString(button.Content) ?? string.Empty;
            Log.Info("KnowledgeV2 设置操作: action=button_click, control=" + KnowledgeV2OperatorUiBridge.Safe(action, 80)
                + ", seller=" + Seller(page) + ", values=" + Values(page));
            ScheduleSnapshot(page, "button_result:" + action);
        }

        private static void OnToggleChanged(object sender, RoutedEventArgs e)
        {
            var page = sender as KnowledgeV2SettingsPage;
            var check = e.Source as CheckBox;
            if (page == null || check == null) return;
            Log.Info("KnowledgeV2 设置操作: action=toggle, control="
                + KnowledgeV2OperatorUiBridge.Safe(Convert.ToString(check.Content), 80)
                + ", value=" + (check.IsChecked == true ? "true" : "false")
                + ", seller=" + Seller(page));
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var page = sender as KnowledgeV2SettingsPage;
            var combo = e.Source as ComboBox;
            if (page == null || combo == null || !ReferenceEquals(combo, Field<ComboBox>(page, "_mode"))) return;
            Log.Info("KnowledgeV2 设置操作: action=selection_changed, control=运行模式, value="
                + KnowledgeV2OperatorUiBridge.Safe(combo.Text, 80) + ", seller=" + Seller(page));
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var page = sender as KnowledgeV2SettingsPage;
            var text = e.Source as TextBox;
            if (page == null || text == null) return;
            var field = ReferenceEquals(text, Field<TextBox>(page, "_threshold")) ? "本地直答匹配阈值"
                : ReferenceEquals(text, Field<TextBox>(page, "_confidence")) ? "最低知识可信度"
                : string.Empty;
            if (field.Length == 0) return;
            Log.Info("KnowledgeV2 设置操作: action=text_changed, control=" + field
                + ", value=" + KnowledgeV2OperatorUiBridge.Safe(text.Text, 40)
                + ", seller=" + Seller(page));
        }

        private static void ScheduleSnapshot(KnowledgeV2SettingsPage page, string stage)
        {
            if (page == null) return;
            page.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    var stats = Field<TextBlock>(page, "_stats");
                    Log.Info("KnowledgeV2 设置状态: stage=" + KnowledgeV2OperatorUiBridge.Safe(stage, 100)
                        + ", seller=" + Seller(page)
                        + ", values=" + Values(page)
                        + ", result=" + KnowledgeV2OperatorUiBridge.Safe(stats == null ? string.Empty : stats.Text, 260));
                }
                catch (Exception ex)
                {
                    Log.Info("KnowledgeV2 设置状态日志失败: stage=" + KnowledgeV2OperatorUiBridge.Safe(stage, 100)
                        + ", error=" + KnowledgeV2OperatorUiBridge.Safe(ex.Message, 200));
                }
            }));
        }

        private static string Values(KnowledgeV2SettingsPage page)
        {
            var enabled = Field<CheckBox>(page, "_enabled");
            var mode = Field<ComboBox>(page, "_mode");
            var threshold = Field<TextBox>(page, "_threshold");
            var confidence = Field<TextBox>(page, "_confidence");
            return "enabled=" + (enabled != null && enabled.IsChecked == true ? "true" : "false")
                + ", mode=" + KnowledgeV2OperatorUiBridge.Safe(mode == null ? string.Empty : mode.Text, 50)
                + ", threshold=" + KnowledgeV2OperatorUiBridge.Safe(threshold == null ? string.Empty : threshold.Text, 30)
                + ", minConfidence=" + KnowledgeV2OperatorUiBridge.Safe(confidence == null ? string.Empty : confidence.Text, 30);
        }

        private static string Seller(KnowledgeV2SettingsPage page)
        {
            var value = FieldValue(page, "_seller");
            return KnowledgeV2OperatorUiBridge.Safe(Convert.ToString(value), 80);
        }

        private static T Field<T>(KnowledgeV2SettingsPage page, string name) where T : class
        {
            return FieldValue(page, name) as T;
        }

        private static object FieldValue(KnowledgeV2SettingsPage page, string name)
        {
            if (page == null) return null;
            var field = typeof(KnowledgeV2SettingsPage).GetField(name, PrivateInstance);
            return field == null ? null : field.GetValue(page);
        }
    }
}
