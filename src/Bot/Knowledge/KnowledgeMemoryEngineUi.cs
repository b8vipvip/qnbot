using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Bot.Knowledge
{
    internal static class KnowledgeMemoryEngineUi
    {
        private static readonly ConditionalWeakTable<KnowledgeCenterWindow, object> Installed =
            new ConditionalWeakTable<KnowledgeCenterWindow, object>();
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(KnowledgeCenterWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded),
                true);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgeCenterWindow;
            if (window == null) return;
            object marker;
            if (Installed.TryGetValue(window, out marker)) return;
            try { Installed.Add(window, new object()); } catch { return; }

            try
            {
                var field = typeof(KnowledgeCenterWindow).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);
                var tabs = field == null ? null : field.GetValue(window) as TabControl;
                if (tabs == null) return;
                if (tabs.Items.OfType<TabItem>().Any(x => string.Equals(Convert.ToString(x.Header), "记忆引擎", StringComparison.Ordinal)))
                    return;
                tabs.Items.Add(new TabItem
                {
                    Header = "记忆引擎",
                    Content = new KnowledgeMemoryEngineControl(window)
                });
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("增加Knowledge Memory管理页失败: " + ex.Message, 10);
            }
        }
    }

    internal sealed class KnowledgeMemoryEngineControl : UserControl
    {
        private readonly KnowledgeCenterWindow _owner;
        private readonly CheckBox _enabled;
        private readonly TextBlock _stats;
        private readonly TextBox _question;
        private readonly TextBox _result;

        public KnowledgeMemoryEngineControl(KnowledgeCenterWindow owner)
        {
            _owner = owner;
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Orientation = Orientation.Vertical };
            header.Children.Add(new TextBlock
            {
                Text = "Knowledge Memory Engine v1",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "权威知识库保持为事实源；系统自动派生业务记忆卡，并结合当前买家 Working Memory、人工纠正/撤回形成的可靠度和冲突检测，在本地优先模式下只对高置信问题零 AI 直答。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12)
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            _enabled = new CheckBox
            {
                Content = "启用 Knowledge Memory Engine",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 18, 0)
            };
            var save = new Button { Content = "保存开关", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var rebuild = new Button { Content = "重建记忆索引", Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var refresh = new Button { Content = "刷新状态", Width = 90, Height = 30 };
            actions.Children.Add(_enabled);
            actions.Children.Add(save);
            actions.Children.Add(rebuild);
            actions.Children.Add(refresh);
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            var queryPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            queryPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            queryPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            queryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            queryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _stats = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetColumnSpan(_stats, 2);
            queryPanel.Children.Add(_stats);
            _question = new TextBox
            {
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = "这个是电视机上的会员服务吗？"
            };
            Grid.SetRow(_question, 1);
            queryPanel.Children.Add(_question);
            var test = new Button { Content = "测试记忆检索", Width = 110, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetRow(test, 1);
            Grid.SetColumn(test, 1);
            queryPanel.Children.Add(test);
            Grid.SetRow(queryPanel, 2);
            root.Children.Add(queryPanel);

            _result = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(_result, 3);
            root.Children.Add(_result);
            Content = root;

            save.Click += delegate
            {
                var seller = ResolveSeller();
                if (seller.Length == 0)
                {
                    MessageBox.Show(_owner, "无法确定当前店铺客服身份。", "记忆引擎", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    KnowledgeMemoryEngine.SetEnabled(seller, _enabled.IsChecked == true);
                    RefreshStatus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(_owner, "保存记忆引擎开关失败：" + ex.Message, "记忆引擎", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            rebuild.Click += delegate
            {
                var seller = ResolveSeller();
                if (seller.Length == 0) return;
                try
                {
                    KnowledgeMemoryEngine.Rebuild(seller);
                    RefreshStatus();
                    _result.Text = "记忆索引已从当前知识库重新派生。原知识问答没有被修改。";
                }
                catch (Exception ex) { _result.Text = "重建失败：" + ex.Message; }
            };
            refresh.Click += delegate { RefreshStatus(); };
            test.Click += delegate { TestQuestion(); };
            Loaded += delegate { RefreshStatus(); };
        }

        private void RefreshStatus()
        {
            var seller = ResolveSeller();
            if (seller.Length == 0)
            {
                _stats.Text = "当前没有可识别的店铺客服实例。";
                return;
            }
            try
            {
                var stats = KnowledgeMemoryEngine.GetStats(seller);
                _enabled.IsChecked = stats.Enabled;
                _stats.Text = "店铺：" + seller
                    + "　记忆卡：" + stats.TotalCards
                    + "　业务事实：" + stats.BusinessFacts
                    + "　流程记忆：" + stats.Procedures
                    + "　安全边界：" + stats.SafetyBoundaries
                    + "　其他：" + stats.Other
                    + "　索引时间：" + (stats.BuiltAt == DateTime.MinValue ? "-" : stats.BuiltAt.ToString("HH:mm:ss"));
            }
            catch (Exception ex) { _stats.Text = "读取记忆状态失败：" + ex.Message; }
        }

        private void TestQuestion()
        {
            var seller = ResolveSeller();
            if (seller.Length == 0)
            {
                _result.Text = "无法确定当前店铺客服身份。";
                return;
            }
            try
            {
                var decision = KnowledgeMemoryEngine.Resolve(seller, "__memory_preview__", _question.Text ?? string.Empty);
                _result.Text = KnowledgeMemoryEngine.FormatDecision(decision);
            }
            catch (Exception ex) { _result.Text = "测试失败：" + ex.Message; }
        }

        private string ResolveSeller()
        {
            try
            {
                var attached = ShopScopedUiBridge.Get(_owner);
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                if (attached != null)
                {
                    foreach (var qn in qns)
                    {
                        if (qn == null || qn.Seller == null || string.IsNullOrWhiteSpace(qn.Seller.Nick)) continue;
                        try
                        {
                            var shop = ShopContextLocator.ResolveBySellerNick(qn.Seller.Nick);
                            if (shop != null && string.Equals(shop.ShopKey, attached.ShopKey, StringComparison.Ordinal))
                                return qn.Seller.Nick.Trim();
                        }
                        catch { }
                    }
                }
                if (QN.CurQN != null && QN.CurQN.Seller != null && !string.IsNullOrWhiteSpace(QN.CurQN.Seller.Nick))
                    return QN.CurQN.Seller.Nick.Trim();
                return qns.FirstOrDefault(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick)) == null
                    ? string.Empty
                    : qns.First(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick)).Seller.Nick.Trim();
            }
            catch { return string.Empty; }
        }
    }
}
