using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Bot.ChromeNs;
using BotLib;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private DispatcherTimer _diagnosticsTimer;
        private DataDeskWindow _dataDeskWindow;
        private DateTime _badConnectionSince = DateTime.MinValue;

        public bool IsDataDeskVisible
        {
            get { return _dataDeskWindow != null && _dataDeskWindow.IsVisible; }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            _diagnosticsTimer = new DispatcherTimer();
            _diagnosticsTimer.Interval = TimeSpan.FromSeconds(2);
            _diagnosticsTimer.Tick += (s, args) => RefreshStatusDiagnostics();
            Loaded += CtlRobot_DiagnosticsLoaded;
            Unloaded += CtlRobot_DiagnosticsUnloaded;
        }

        private void CtlRobot_DiagnosticsLoaded(object sender, RoutedEventArgs e)
        {
            if (_diagnosticsTimer != null) _diagnosticsTimer.Start();
            RefreshStatusDiagnostics();
        }

        private void CtlRobot_DiagnosticsUnloaded(object sender, RoutedEventArgs e)
        {
            if (_diagnosticsTimer != null) _diagnosticsTimer.Stop();
        }

        public void ShowDataDesk(Window owner)
        {
            try
            {
                if (_dataDeskWindow != null && _dataDeskWindow.IsVisible)
                {
                    _dataDeskWindow.Activate();
                    _dataDeskWindow.DockToOwner();
                    return;
                }

                var anchor = _rightPanel as FrameworkElement;
                _dataDeskWindow = new DataDeskWindow(owner, anchor ?? this);
                _dataDeskWindow.Closed += (s, e) => _dataDeskWindow = null;
                _dataDeskWindow.Show();
                _dataDeskWindow.DockToOwner();
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        public void CloseDataDesk()
        {
            try
            {
                if (_dataDeskWindow != null)
                {
                    _dataDeskWindow.Close();
                    _dataDeskWindow = null;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private string BuildApiStatus(RuntimeStatsSnapshot snapshot)
        {
            try
            {
                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints == null || endpoints.Count < 1) return "未配置";
                if (snapshot != null && snapshot.ApiUsages != null && snapshot.ApiUsages.Count > 0)
                {
                    var first = snapshot.ApiUsages.FirstOrDefault();
                    if (first != null && !string.IsNullOrWhiteSpace(first.LastStatus))
                    {
                        return first.LastStatus + "｜今日" + first.TodayCalls + "次";
                    }
                }
                return "已配置" + endpoints.Count + "个";
            }
            catch
            {
                return "检测失败";
            }
        }

        private string BuildConnectionDetail(ConnectionDiagnosticsSnapshot diag)
        {
            if (diag == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("WS连接：" + diag.WebSocketStatus);
            sb.AppendLine("注入状态：" + diag.InjectionStatus);
            sb.AppendLine("语言状态：" + diag.LanguageStatus);
            sb.AppendLine("千牛参数：" + diag.QnParamStatus);
            sb.AppendLine("客服ID：" + (string.IsNullOrWhiteSpace(diag.Seller) ? "未识别" : diag.Seller));
            sb.AppendLine("买家ID：" + (string.IsNullOrWhiteSpace(diag.Buyer) ? "未识别" : diag.Buyer));
            sb.AppendLine("无障碍/可访问UI：" + diag.AccessibilityStatus);
            sb.AppendLine("发送按钮识别：" + diag.ButtonStatus);
            sb.AppendLine("最近发送结果：" + diag.SendStatus);
            return sb.ToString().Trim();
        }

        private SolidColorBrush StatusBrush(bool ok)
        {
            return new SolidColorBrush(ok ? Color.FromRgb(39, 174, 96) : Color.FromRgb(242, 153, 74));
        }

        private bool ShouldRecoverQianniu(ConnectionDiagnosticsSnapshot diag, string summary)
        {
            if (diag == null) return false;
            if (!Params.Robot.CanUseRobotReal) return false;
            if (diag.WebSocketSessionCount < 1) return false;
            if (summary == "连接正常") return false;
            if (string.IsNullOrWhiteSpace(summary)) return false;
            return summary.Contains("客服ID未识别") || summary.Contains("千牛参数未获取");
        }

        private void MaybeRecoverQianniu(ConnectionDiagnosticsSnapshot diag, string summary)
        {
            try
            {
                if (!ShouldRecoverQianniu(diag, summary))
                {
                    _badConnectionSince = DateTime.MinValue;
                    return;
                }

                if (_badConnectionSince == DateTime.MinValue)
                {
                    _badConnectionSince = DateTime.Now;
                    return;
                }

                if ((DateTime.Now - _badConnectionSince).TotalSeconds >= 45)
                {
                    QianniuRecoveryManager.RequestRecover(summary);
                    _badConnectionSince = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void RefreshStatusDiagnostics()
        {
            try
            {
                var diag = BotConnectionDiagnostics.GetSnapshot();
                var stats = BotRuntimeStats.GetSnapshot();
                var connectionOk = diag != null && diag.Summary == "连接正常";
                var summary = diag == null || string.IsNullOrWhiteSpace(diag.Summary) ? "正在检测" : diag.Summary;
                var detail = BuildConnectionDetail(diag);

                if (diag != null)
                {
                    if (!string.IsNullOrWhiteSpace(diag.Seller) && (txtSeller == null || string.IsNullOrWhiteSpace(txtSeller.Text) || txtSeller.Text == "..."))
                    {
                        txtSeller.Text = diag.Seller;
                    }
                    if (!string.IsNullOrWhiteSpace(diag.Buyer) && (txtBuyer == null || string.IsNullOrWhiteSpace(txtBuyer.Text) || txtBuyer.Text == "..."))
                    {
                        txtBuyer.Text = diag.Buyer;
                    }
                }

                if (txtStatusSummary != null)
                {
                    txtStatusSummary.Text = summary;
                    txtStatusSummary.Foreground = StatusBrush(connectionOk);
                    txtStatusSummary.ToolTip = detail;
                }
                if (txtConnectionStatus != null)
                {
                    txtConnectionStatus.Text = "连接状态：" + summary;
                    txtConnectionStatus.Foreground = StatusBrush(connectionOk);
                    txtConnectionStatus.ToolTip = detail;
                }
                if (txtStatusApi != null)
                {
                    txtStatusApi.Text = "API连接：" + BuildApiStatus(stats);
                }
                if (txtLanguageStatus != null)
                {
                    txtLanguageStatus.Text = diag == null || string.IsNullOrWhiteSpace(diag.LanguageStatus) ? "语言：正在检测" : diag.LanguageStatus;
                    txtLanguageStatus.Foreground = StatusBrush(diag != null && diag.LanguageOk);
                    txtLanguageStatus.ToolTip = diag == null ? string.Empty : diag.LanguageDetail;
                }

                MaybeRecoverQianniu(diag, summary);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }

    internal class DataDeskWindow : Window
    {
        private readonly Window _owner;
        private readonly FrameworkElement _anchor;
        private readonly DispatcherTimer _timer;
        private TextBlock _txtTotalReception;
        private TextBlock _txtTodayReception;
        private TextBlock _txtTotalAutoReply;
        private TextBlock _txtTodayAutoReply;
        private TextBlock _txtTotalAiCall;
        private TextBlock _txtTodayAiCall;
        private TextBlock _txtTotalTokens;
        private TextBlock _txtTodayTokens;
        private TextBlock _txtAvgLatency;
        private TextBlock _txtLastError;
        private StackPanel _panelApiStats;

        public DataDeskWindow(Window owner, FrameworkElement anchor)
        {
            _owner = owner;
            _anchor = anchor;
            Title = "数据台";
            Width = 335;
            MinWidth = 300;
            Height = anchor != null && anchor.ActualHeight > 200 ? anchor.ActualHeight : 680;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
            Content = BuildContent();

            if (_owner != null)
            {
                Owner = _owner;
                _owner.LocationChanged += OwnerChanged;
                _owner.SizeChanged += OwnerChanged;
            }
            if (_anchor != null)
            {
                _anchor.SizeChanged += OwnerChanged;
                _anchor.LayoutUpdated += Anchor_LayoutUpdated;
            }
            Closed += DataDeskWindow_Closed;
            Loaded += (s, e) => DockToOwner();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += (s, e) => RefreshStats();
            _timer.Start();
            RefreshStats();
        }

        private void Anchor_LayoutUpdated(object sender, EventArgs e)
        {
            DockToOwner();
        }

        private void DataDeskWindow_Closed(object sender, EventArgs e)
        {
            if (_timer != null) _timer.Stop();
            if (_owner != null)
            {
                _owner.LocationChanged -= OwnerChanged;
                _owner.SizeChanged -= OwnerChanged;
            }
            if (_anchor != null)
            {
                _anchor.SizeChanged -= OwnerChanged;
                _anchor.LayoutUpdated -= Anchor_LayoutUpdated;
            }
        }

        private void OwnerChanged(object sender, EventArgs e)
        {
            DockToOwner();
        }

        public void DockToOwner()
        {
            try
            {
                double anchorLeft = _owner == null ? SystemParameters.WorkArea.Right - Width : _owner.Left;
                double anchorTop = _owner == null ? SystemParameters.WorkArea.Top : _owner.Top;
                double anchorWidth = _owner == null ? 0 : _owner.ActualWidth;
                double anchorHeight = _owner != null && _owner.ActualHeight > 200 ? _owner.ActualHeight : 680;

                if (_anchor != null && _anchor.IsVisible && _anchor.ActualWidth > 0 && _anchor.ActualHeight > 0)
                {
                    var p1 = _anchor.PointToScreen(new Point(0, 0));
                    var p2 = _anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight));
                    anchorLeft = p1.X;
                    anchorTop = p1.Y;
                    anchorWidth = Math.Max(0, p2.X - p1.X);
                    anchorHeight = Math.Max(200, p2.Y - p1.Y);
                }

                Height = anchorHeight > 200 ? anchorHeight : Height;
                var work = SystemParameters.WorkArea;
                var rightLeft = anchorLeft + anchorWidth;
                var leftLeft = anchorLeft - Width;
                double targetLeft;
                if (rightLeft + Width <= work.Right)
                {
                    targetLeft = rightLeft;
                }
                else if (leftLeft >= work.Left)
                {
                    targetLeft = leftLeft;
                }
                else
                {
                    targetLeft = Math.Max(work.Left, Math.Min(rightLeft, work.Right - Width));
                }

                var targetTop = Math.Max(work.Top, Math.Min(anchorTop, work.Bottom - Height));
                Left = targetLeft;
                Top = targetTop;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private UIElement BuildContent()
        {
            var root = new DockPanel { Margin = new Thickness(10) };
            var title = new TextBlock
            {
                Text = "数据台",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(title, Dock.Top);
            root.Children.Add(title);

            var scv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scv);
            var sp = new StackPanel();
            scv.Content = sp;

            sp.Children.Add(Card(BuildReceptionPanel()));
            sp.Children.Add(Card(BuildAiPanel()));
            sp.Children.Add(Card(BuildApiPanel()));
            return root;
        }

        private Border Card(UIElement child)
        {
            return new Border
            {
                Child = child,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private UIElement BuildReceptionPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("接待概览"));
            var grid = new UniformGrid { Columns = 2 };
            _txtTotalReception = AddMetric(grid, "总接待");
            _txtTodayReception = AddMetric(grid, "今日接待");
            _txtTotalAutoReply = AddMetric(grid, "总自动回复");
            _txtTodayAutoReply = AddMetric(grid, "今日自动回复");
            sp.Children.Add(grid);
            return sp;
        }

        private UIElement BuildAiPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("AI调用"));
            var grid = new UniformGrid { Columns = 2 };
            _txtTotalAiCall = AddMetric(grid, "总调用");
            _txtTodayAiCall = AddMetric(grid, "今日调用");
            _txtTotalTokens = AddMetric(grid, "总Token");
            _txtTodayTokens = AddMetric(grid, "今日Token");
            sp.Children.Add(grid);
            _txtAvgLatency = SmallText("平均耗时：0ms");
            _txtAvgLatency.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(_txtAvgLatency);
            _txtLastError = SmallText(string.Empty);
            _txtLastError.Foreground = new SolidColorBrush(Color.FromRgb(235, 87, 87));
            _txtLastError.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(_txtLastError);
            return sp;
        }

        private UIElement BuildApiPanel()
        {
            var sp = new StackPanel();
            sp.Children.Add(SectionTitle("各API接口消耗"));
            _panelApiStats = new StackPanel();
            sp.Children.Add(_panelApiStats);
            return sp;
        }

        private TextBlock SectionTitle(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)), Margin = new Thickness(0, 0, 0, 8) };
        }

        private TextBlock SmallText(string text)
        {
            return new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)) };
        }

        private TextBlock AddMetric(Panel parent, string label)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            sp.Children.Add(SmallText(label));
            var value = new TextBlock { Text = "0", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)) };
            sp.Children.Add(value);
            parent.Children.Add(sp);
            return value;
        }

        private string FormatNum(long value)
        {
            if (value >= 1000000) return (value / 10000.0).ToString("0.0") + "万";
            return value.ToString();
        }

        private void RefreshStats()
        {
            try
            {
                var snapshot = BotRuntimeStats.GetSnapshot();
                _txtTotalReception.Text = FormatNum(snapshot.TotalReceptionCount);
                _txtTodayReception.Text = FormatNum(snapshot.TodayReceptionCount);
                _txtTotalAutoReply.Text = FormatNum(snapshot.TotalAutoReplies);
                _txtTodayAutoReply.Text = FormatNum(snapshot.TodayAutoReplies);
                _txtTotalAiCall.Text = FormatNum(snapshot.TotalAiCalls);
                _txtTodayAiCall.Text = FormatNum(snapshot.TodayAiCalls);
                _txtTotalTokens.Text = FormatNum(snapshot.TotalTokens);
                _txtTodayTokens.Text = FormatNum(snapshot.TodayTokens);
                _txtAvgLatency.Text = "平均耗时：" + snapshot.AvgLatencyMs + "ms，失败：" + snapshot.TodayAiFailedCalls + "/今日";
                _txtLastError.Text = string.IsNullOrWhiteSpace(snapshot.LastError) ? string.Empty : "最近错误：" + snapshot.LastError;

                _panelApiStats.Children.Clear();
                if (snapshot.ApiUsages == null || snapshot.ApiUsages.Count < 1)
                {
                    _panelApiStats.Children.Add(SmallText("暂无API调用数据"));
                    return;
                }

                foreach (var api in snapshot.ApiUsages)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = api.EndpointName, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)) });
                    sp.Children.Add(SmallText("今日调用 " + api.TodayCalls + " 次｜今日Token " + api.TodayTokens));
                    sp.Children.Add(SmallText("总Token " + api.TotalTokens + "｜失败 " + api.FailedCalls + "｜均耗时 " + api.AvgLatencyMs + "ms"));
                    border.Child = sp;
                    _panelApiStats.Children.Add(border);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
