using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Bot.Automation.ChatDeskNs;
using BotLib;
using BotLib.Wpf.Extensions;
using Bot.Options;
using Bot.Knowledge;
using Bot.AssistWindow.Widget.Robot;

namespace Bot.AssistWindow.Widget
{
    public partial class RightPanel : UserControl, IWakable
    {
        private WndAssist _wndDontUse;
        private bool _isWiden;
        private bool _isHighden;
        private bool _isSouthEast;
        public enum TabTypeEnum
        {
            Unknown,
            Logis,
            ShortCut,
            Robot,
            Goods,
            Order,
            Coupon,
            Test
        }

        private WndAssist Wnd
        {
            get
            {
                if (_wndDontUse == null)
                {
                    WndAssist wnd = this.xFindParentWindow() as WndAssist;
                    Init(wnd);
                    Util.Assert(_wndDontUse != null);
                }
                return _wndDontUse;
            }
            set
            {
                _wndDontUse = value;
            }
        }

        public RightPanel()
        {
            _isWiden = false;
            _isHighden = false;
            _isSouthEast = false;
            InitializeComponent();
            Loaded += RightPanel_Loaded;
        }

        private void RightPanel_Loaded(object sender, RoutedEventArgs e)
        {
            // The header is independent from the dynamically-created tab content. Recover
            // initialization if the host did not finish the normal Init path.
            if (_wndDontUse == null)
            {
                var parent = this.xFindParentWindow() as WndAssist;
                if (parent != null) Init(parent);
            }

            // Recover the Bot tab if a stale/empty persisted tab order left the panel body blank.
            if (tabControl.Items.Count == 0 && _wndDontUse != null)
            {
                Log.ErrorWithMaxCount("右侧Bot面板没有任何有效Tab，Loaded阶段自动恢复工作台。", 10);
                EnsureRobotTab("Loaded");
            }
            if (tabControl.Items.Count > 0 && tabControl.SelectedIndex < 0)
                tabControl.SelectedIndex = 0;
            RefreshSwitches();
        }

        public void Init(WndAssist wnd)
        {
            if (_wndDontUse != null) return;
            if (wnd == null)
            {
                Log.ErrorWithMaxCount("右侧Bot面板初始化失败：WndAssist为空。", 10);
                return;
            }

            Wnd = wnd;
            BuildConfiguredTabs(false);
            tabControl.SelectionChanged -= tabControl_SelectionChanged;
            tabControl.SelectionChanged += tabControl_SelectionChanged;
            if (tabControl.Items.Count > 0 && tabControl.SelectedIndex < 0)
                tabControl.SelectedIndex = 0;
            RefreshSwitches();
        }

        public void ReShowAfterChangePanelOption()
        {
            Util.Assert(_wndDontUse != null);
            BuildConfiguredTabs(true);
            tabControl.SelectionChanged -= tabControl_SelectionChanged;
            tabControl.SelectionChanged += tabControl_SelectionChanged;
            if (tabControl.Items.Count > 0 && tabControl.SelectedIndex < 0)
                tabControl.SelectedIndex = 0;
        }

        private void BuildConfiguredTabs(bool clearExisting)
        {
            if (clearExisting) tabControl.Items.Clear();

            var tabCsv = string.Empty;
            try
            {
                tabCsv = Params.Panel.GetRightPanelCompOrderCsv(Wnd.Desk.WndTitle) ?? string.Empty;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }

            var added = 0;
            var tabs = (tabCsv ?? string.Empty).Split(
                new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawName in tabs)
            {
                var tabName = (rawName ?? string.Empty).Trim();
                if (tabName.Length == 0) continue;

                var tabType = GetTabType(tabName);
                if (tabType == TabTypeEnum.Unknown)
                {
                    Log.Info("忽略无法识别的右侧面板标签配置: " + tabName);
                    continue;
                }

                try
                {
                    var tabItem = CreateTabItem(tabType);
                    if (tabItem == null) continue;
                    if (clearExisting)
                        tabItem.xIsVisible(Params.Panel.GetPanelOptionVisible(Wnd.Desk.WndTitle, tabName));
                    AddTabItem(tabItem, tabType);
                    added++;
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
            }

            var robotTab = GetTabItem(TabTypeEnum.Robot);
            if (robotTab == null)
            {
                Log.ErrorWithMaxCount(
                    "右侧面板配置未生成Bot工作台，自动补回默认工作台。configured="
                    + (tabCsv.Length > 80 ? tabCsv.Substring(0, 80) : tabCsv), 10);
                EnsureRobotTab("InvalidOrEmptyConfiguration");
                robotTab = GetTabItem(TabTypeEnum.Robot);
            }
            // A previous build treated the persisted name "工作台" as an unknown
            // visibility option and collapsed the only Bot tab. Restore it whenever
            // the actual Robot visibility setting says the tab should be shown.
            if (robotTab != null && robotTab.Visibility != Visibility.Visible
                && Params.Panel.GetRobotIsVisible(Wnd.Desk.WndTitle))
            {
                robotTab.Visibility = Visibility.Visible;
                Log.ErrorWithMaxCount("检测到Bot工作台被旧版标签别名逻辑隐藏，已按RobotIsVisible配置恢复显示。", 10);
            }

            if (tabControl.Items.Count > 0 && tabControl.SelectedIndex < 0)
                tabControl.SelectedIndex = 0;

            Log.Info("右侧Bot面板初始化完成: configuredTabs="
                + (tabCsv.Length > 80 ? tabCsv.Substring(0, 80) : tabCsv)
                + ", createdTabs=" + added
                + ", actualTabs=" + tabControl.Items.Count);
        }

        private void EnsureRobotTab(string reason)
        {
            if (GetTabItem(TabTypeEnum.Robot) != null) return;
            try
            {
                Log.Info("正在恢复右侧Bot工作台，reason=" + (reason ?? string.Empty));
                var tabItem = TabRobot();
                AddTabItem(tabItem, TabTypeEnum.Robot);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                // Even if the Robot control itself cannot be constructed, never leave
                // the panel as a silent white rectangle. Surface a readable error.
                var fallback = new TabItem
                {
                    Header = "状态",
                    Tag = TabTypeEnum.Robot,
                    Content = CreateRobotInitializationErrorContent(ex)
                };
                tabControl.Items.Add(fallback);
            }
        }

        private UIElement CreateRobotInitializationErrorContent(Exception ex)
        {
            var message = ex == null ? "未知初始化错误" : ex.GetBaseException().Message;
            return new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Bot工作台初始化失败",
                            FontSize = 16,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.DarkRed,
                            Margin = new Thickness(0, 0, 0, 8)
                        },
                        new TextBlock
                        {
                            Text = "界面没有继续保持空白。请查看客户端诊断日志中的 RightPanel 初始化异常。",
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 8)
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brushes.Gray
                        }
                    }
                }
            };
        }

        public void ChangeSeller(string seller)
        {
            var robot = GetRobotControl();
            if (robot != null)
            {
                robot.ChangeSeller(seller);
            }
        }

        private TabItem CreateTabItem(TabTypeEnum tabType)
        {
            TabItem tabItem = null;
            switch (tabType)
            {
                case TabTypeEnum.Robot:
                    tabItem = TabRobot();
                    break;
                default:
                    break;
            }
            return tabItem;
        }

        private TabTypeEnum GetTabType(string typeName)
        {
            var tabType = TabTypeEnum.Unknown;
            switch (typeName)
            {
                case "商品":
                    tabType = TabTypeEnum.Goods;
                    break;
                case "工作台":
                case "机器人":
                case "Bot":
                case "Robot":
                case "状态":
                    tabType = TabTypeEnum.Robot;
                    break;
            }
            return tabType;
        }

        private void tabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource == tabControl)
            {
                SetTabStyle(e);
                ActivateTab(e);
            }
        }

        private void SetTabStyle(SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                var tabIt = e.AddedItems[0] as TabItem;
                if (tabIt != null)
                {
                    var tb = tabIt.Header as TextBlock;
                    if (tb != null) tb.Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237));
                }
            }
            if (e.RemovedItems.Count > 0)
            {
                var tabIt = e.RemovedItems[0] as TabItem;
                if (tabIt != null)
                {
                    var tb = tabIt.Header as TextBlock;
                    if (tb != null) tb.Foreground = Brushes.Black;
                }
            }
        }

        private TabItem TabRobot()
        {
            var tabItem = new TabItem { Header = "状态" };
            try
            {
                tabItem.Content = new CtlRobot(Wnd.Desk, this);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                tabItem.Content = CreateRobotInitializationErrorContent(ex);
            }
            return tabItem;
        }

        private void AddTabItem(TabItem tabItem, TabTypeEnum tabType)
        {
            tabItem.Tag = tabType;
            if (!(tabItem.Header is TextBlock))
            {
                tabItem.Header = TextBlockEx.Create(tabItem.Header.ToString(), new object[0]);
            }
            tabItem.Style = (Style)FindResource("tabRightPanel");
            tabControl.Items.Add(tabItem);
        }

        public TabItem GetTabItem(TabTypeEnum tabType)
        {
            TabItem tabItem = null;
            foreach (TabItem item in tabControl.Items)
            {
                TabTypeEnum ty = (TabTypeEnum)item.Tag;
                if (ty == tabType)
                {
                    tabItem = item;
                    break;
                }
            }
            return tabItem;
        }

        private CtlRobot GetRobotControl()
        {
            var tab = GetTabItem(TabTypeEnum.Robot);
            return tab == null ? null : tab.Content as CtlRobot;
        }

        private void RefreshSwitches()
        {
            if (cboxPanelBotEnabled != null) cboxPanelBotEnabled.IsChecked = Params.Robot.CanUseRobotReal;
            if (cboxPanelAuto != null)
            {
                cboxPanelAuto.IsChecked = Params.Robot.GetIsAutoReply();
                cboxPanelAuto.IsEnabled = Params.Robot.CanUseRobotReal;
                cboxPanelAuto.Opacity = Params.Robot.CanUseRobotReal ? 1.0 : 0.55;
            }
            var robot = GetRobotControl();
            if (robot != null)
            {
                robot.RefreshSwitchState();
                if (menuDataDesk != null) menuDataDesk.IsChecked = robot.IsDataDeskVisible;
            }
        }

        private void rectWiden_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isWiden = true;
        }

        private void rectWiden_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isWiden)
            {
                _isWiden = false;
                Rectangle rectangle = sender as Rectangle;
                rectangle.ReleaseMouseCapture();
                int width = (int)e.GetPosition(this).X + 5;
                SetRightPanelWidth(width, 0);
            }
        }

        private void rectWiden_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isWiden)
            {
                if (e.LeftButton == MouseButtonState.Released)
                {
                    _isWiden = false;
                    var captured = Mouse.Captured;
                    if (captured != null)
                    {
                        captured.ReleaseMouseCapture();
                    }
                }
                else
                {
                    var rectangle = sender as Rectangle;
                    rectangle.CaptureMouse();
                    int width = (int)e.GetPosition(this).X + 5;
                    SetRightPanelWidth(width, 5);
                }
            }
        }

        private void SetRightPanelWidth(int width, int minVal = 5)
        {
            if (width < 365)
            {
                width = 365;
            }
            if (Math.Abs(ActualWidth - (double)width) > (double)minVal)
            {
                this.xSetWidth(width);
                WndAssist.WaParams.SetRightPanelWidth(Wnd.Desk.WndTitle, width);
            }
        }

        private void rectHighden_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isHighden)
            {
                if (e.LeftButton == MouseButtonState.Released)
                {
                    _isHighden = false;
                    var captured = Mouse.Captured;
                    if (captured != null)
                    {
                        captured.ReleaseMouseCapture();
                    }
                }
                else
                {
                    var rectangle = sender as Rectangle;
                    rectangle.CaptureMouse();
                    int height = (int)e.GetPosition(this).Y + 5;
                }
            }
        }

        public TabItem GetSelectedTabItem(TabTypeEnum tabType)
        {
            TabItem tabItem = null;
            foreach (TabItem tab in tabControl.Items)
            {
                var tabTypeEnum = (TabTypeEnum)tab.Tag;
                if (tabTypeEnum == tabType)
                {
                    tabItem = tab;
                    break;
                }
            }
            return tabItem;
        }

        private void rectHighden_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isHighden = false;
            Rectangle rectangle = sender as Rectangle;
            rectangle.ReleaseMouseCapture();
            int height = (int)e.GetPosition(this).Y + 5;
        }

        private void rectHighden_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isHighden = true;
        }

        private void rectCorner_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isSouthEast)
            {
                if (e.LeftButton == MouseButtonState.Released)
                {
                    _isHighden = false;
                    var captured = Mouse.Captured;
                    if (captured != null)
                    {
                        captured.ReleaseMouseCapture();
                    }
                }
                else
                {
                    Rectangle rectangle = sender as Rectangle;
                    rectangle.CaptureMouse();
                    int width = (int)e.GetPosition(this).X + 5;
                    int height = (int)e.GetPosition(this).Y + 5;
                    SetRightPanelWidth(width, 5);
                }
            }
        }

        private void rectCorner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSouthEast = false;
            var rectangle = sender as Rectangle;
            rectangle.ReleaseMouseCapture();
            int width = (int)e.GetPosition(this).X + 5;
            int height = (int)e.GetPosition(this).Y + 5;
            SetRightPanelWidth(width, 0);
        }

        private void rectCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSouthEast = true;
        }

        public void WakeUp()
        {
            RefreshSwitches();
            IWakable wakable = tabControl.SelectedContent as IWakable;
            if (wakable != null)
            {
                wakable.WakeUp();
            }
        }

        public void Sleep()
        {
            IWakable wakable = tabControl.SelectedContent as IWakable;
            if (wakable != null)
            {
                wakable.Sleep();
            }
        }

        private void ActivateTab(SelectionChangedEventArgs e)
        {
            if (e.AddedItems != null)
            {
                foreach (TabItem tabItem in e.AddedItems)
                {
                    if (tabItem != null)
                    {
                        IWakable wakable = tabItem.Content as IWakable;
                        if (wakable != null)
                        {
                            wakable.WakeUp();
                        }
                    }
                }
            }
            if (e.RemovedItems != null)
            {
                foreach (TabItem tabItem in e.RemovedItems)
                {
                    if (tabItem != null)
                    {
                        IWakable wakable = tabItem.Content as IWakable;
                        if (wakable != null)
                        {
                            wakable.Sleep();
                        }
                    }
                }
            }
        }

        private void btnHide_Click(object sender, RoutedEventArgs e)
        {
            var robot = GetRobotControl();
            if (robot != null) robot.CloseDataDesk();
            Wnd.HidePanelRight();
        }

        private void btnOption_Click(object sender, RoutedEventArgs e)
        {
            var robot = GetRobotControl();
            if (menuDataDesk != null) menuDataDesk.IsChecked = robot != null && robot.IsDataDeskVisible;
            btnOption.ContextMenu.PlacementTarget = btnOption;
            btnOption.ContextMenu.IsOpen = true;
        }

        private void menuDataDesk_Click(object sender, RoutedEventArgs e)
        {
            var robot = GetRobotControl();
            if (robot == null) return;
            if (robot.IsDataDeskVisible)
            {
                robot.CloseDataDesk();
            }
            else
            {
                robot.ShowDataDesk(Wnd);
            }
            if (menuDataDesk != null) menuDataDesk.IsChecked = robot.IsDataDeskVisible;
        }

        private void menuWorkbench_Click(object sender, RoutedEventArgs e)
        {
            menuDataDesk_Click(sender, e);
        }

        private void menuApi_Click(object sender, RoutedEventArgs e)
        {
            WndOption.MyShow(Wnd.Desk.WndTitle, Wnd, OptionEnum.Robot);
        }

        private void menuFeatureSettings_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            var page = "知识库";
            if (item != null)
            {
                page = (item.Tag ?? item.Header ?? page).ToString();
            }
            if (page == "知识库") KnowledgeCenterWindow.MyShow(Wnd);
            else FeatureSettingsWindow.MyShow(Wnd, page);
        }

        private void menuKnowledge_Click(object sender, RoutedEventArgs e)
        {
            KnowledgeCenterWindow.MyShow(Wnd);
        }

        private void menuRule_Click(object sender, RoutedEventArgs e)
        {
            FeatureSettingsWindow.MyShow(Wnd, "自动回复规则");
        }

        private void menuMessagePolicy_Click(object sender, RoutedEventArgs e)
        {
            FeatureSettingsWindow.MyShow(Wnd, "消息策略");
        }

        private void menuLogs_Click(object sender, RoutedEventArgs e)
        {
            FeatureSettingsWindow.MyShow(Wnd, "日志与调试");
        }

        private void menuLicense_Click(object sender, RoutedEventArgs e)
        {
            FeatureSettingsWindow.MyShow(Wnd, "账号与授权");
        }

        private void menuHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("使用前请确认：1. 千牛已开启无障碍/讲述人模式；2. Bot 已启用；3. API接口测试通过；4. 自动回复开关按需开启；5. 高风险问题建议在【自动回复规则】里转人工。", "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void menuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("AI客服 v2\n定位：千牛客服辅助回复工具。\n已接入：多API、知识库、自动回复规则、消息策略、日志、授权信息、商业化合规清单、独立数据台。", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void cboxPanelBotEnabled_Click(object sender, RoutedEventArgs e)
        {
            Params.Robot.CanUseRobot = cboxPanelBotEnabled.IsChecked ?? true;
            RefreshSwitches();
            Log.Info("Bot总开关=" + (Params.Robot.CanUseRobotReal ? "启用" : "停用"));
        }

        private void cboxPanelAuto_Click(object sender, RoutedEventArgs e)
        {
            Params.Robot.SetIsAutoReply(cboxPanelAuto.IsChecked ?? false);
            RefreshSwitches();
            Log.Info("自动回复=" + ((cboxPanelAuto.IsChecked ?? false) ? "开启" : "关闭"));
        }
    }
}