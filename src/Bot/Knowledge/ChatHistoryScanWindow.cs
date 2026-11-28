using Bot.ShopScope;
using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2ChatHistoryPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly Window _owner;
        private readonly string _seller;
        private readonly string _shopKey;
        private RadioButton _all;
        private RadioButton _range;
        private DatePicker _start;
        private DatePicker _end;
        private Button _startButton;
        private Button _cancelButton;
        private TextBox _progress;
        private TextBlock _summary;
        private CancellationTokenSource _cts;
        private bool _running;

        public KnowledgeV2ChatHistoryPage(Window owner, string seller)
        {
            _owner = owner;
            _seller = (seller ?? string.Empty).Trim();
            _shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);
            Build();
            Unloaded += delegate
            {
                if (_running && _cts != null) _cts.Cancel();
            };
        }

        public void RefreshView()
        {
            RefreshRangeState();
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock
            {
                Text = "历史聊天整理",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });
            header.Children.Add(new TextBlock
            {
                Text = "从千牛历史聊天中提取买家已提问、客服已回答的有效轮次。整理结果统一转换为当前 Knowledge Center V2 字段、按 V2 去重并写入当前店铺；AI分析结果直接使用 Knowledge V2 结构化记录，只有当前店铺的 V2 数据库会接收写入。不会向买家发送消息。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                Margin = new Thickness(0, 5, 0, 0)
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            _startButton = MakeButton("开始整理", 100, Start_Click);
            _cancelButton = MakeButton("取消任务", 90, Cancel_Click);
            _cancelButton.IsEnabled = false;
            footer.Children.Add(_startButton);
            footer.Children.Add(_cancelButton);

            var body = new StackPanel();
            root.Children.Add(body);
            var optionsBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };
            body.Children.Add(optionsBorder);
            var options = new StackPanel();
            optionsBorder.Child = options;

            _all = new RadioButton { Content = "全部扫描", IsChecked = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            _range = new RadioButton { Content = "按时间段扫描", FontWeight = FontWeights.SemiBold };
            _all.Checked += (s, e) => RefreshRangeState();
            _range.Checked += (s, e) => RefreshRangeState();
            options.Children.Add(_all);
            options.Children.Add(_range);

            var dates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 8, 0, 0) };
            options.Children.Add(dates);
            dates.Children.Add(new TextBlock { Text = "开始日期：", VerticalAlignment = VerticalAlignment.Center });
            _start = new DatePicker { Width = 150, SelectedDate = DateTime.Today.AddDays(-30), Margin = new Thickness(0, 0, 16, 0) };
            dates.Children.Add(_start);
            dates.Children.Add(new TextBlock { Text = "结束日期：", VerticalAlignment = VerticalAlignment.Center });
            _end = new DatePicker { Width = 150, SelectedDate = DateTime.Today };
            dates.Children.Add(_end);
            RefreshRangeState();

            body.Children.Add(new TextBlock { Text = "处理说明", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 5) });
            body.Children.Add(new TextBlock
            {
                Text = "• 联系人优先从聊天界面左侧“全部买家”列表读取，消息管理器和千牛联系人接口作为补充。\n" +
                       "• 每个买家通过会话编号分页读取历史消息，扫描结束后恢复原聊天窗口。\n" +
                       "• 聊天记录先整理成问答轮次，再统一映射到当前 V2 的 type / intent / subject / predicate / entities / aliases / answer 等字段。\n" +
                       "• 手机号、长订单号及 API Key 会先脱敏；系统提示、撤回提示和未回答问题不会进入知识库。\n" +
                       "• V2 写入成功后清除本次旧格式临时结果，旧版知识库不再承载本次历史整理结果。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99))
            });

            _summary = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_seller) ? "当前未识别店铺，暂不能开始。" : "尚未开始。结果将写入当前店铺 Knowledge V2。",
                Margin = new Thickness(0, 14, 0, 6),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235))
            };
            body.Children.Add(_summary);
            _progress = new TextBox
            {
                Height = 230,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Padding = new Thickness(10)
            };
            body.Children.Add(_progress);
        }

        private static Button MakeButton(string text, double width, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, Width = width, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            button.Click += handler;
            return button;
        }

        private void RefreshRangeState()
        {
            var enabled = !_running && _range != null && _range.IsChecked == true;
            if (_start != null) _start.IsEnabled = enabled;
            if (_end != null) _end.IsEnabled = enabled;
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            if (string.IsNullOrWhiteSpace(_seller) || string.IsNullOrWhiteSpace(_shopKey))
            {
                MessageBox.Show(_owner, "无法确认历史聊天所属店铺的 ShopKey，已阻止写入 Knowledge V2。", "历史聊天整理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var options = BuildOptions();
            if (options == null) return;

            var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
            if (shop == null || !shop.HasStableSellerId || shop.ShopKey != _shopKey)
            {
                MessageBox.Show(_owner, "无法绑定历史学习所属店铺。");
                return;
            }
            options.LearnKnowledge = true;
            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                var prior = BuyerHistoryScanCheckpointStore.Latest(shop.SellerNick, true);
                if (prior != null && prior.State != "completed")
                {
                    var decision = MessageBox.Show(_owner, "发现未完成的历史学习断点。是：继续原任务；否：重新扫描；取消：退出。",
                        "历史学习断点", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (decision == MessageBoxResult.Cancel) return;
                    if (decision == MessageBoxResult.Yes)
                    {
                        options.ResumeCheckpointId = prior.Id;
                        options.ScanAll = prior.ScanAll; options.MaxContacts = prior.MaxContacts;
                        options.StartTime = prior.StartTime; options.EndTime = prior.EndTime;
                    }
                }
            }
            }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "读取历史学习断点失败：" + ex.Message);
                return;
            }
            _running = true;
            _cts = new CancellationTokenSource();
            SetRunningState(true);
            _summary.Text = "正在准备扫描...";
            _progress.Text = string.Empty;

            try
            {
                var service = new ChatHistoryScanService();
                ChatHistoryScanResult scan;
                using (ShopSettingsScope.Enter(shop))
                scan = await service.ScanBuyerMessagesAsync(
                    shop.SellerNick,
                    options,
                    p => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _progress.Text = p == null ? string.Empty : p.ToString();
                        _progress.ScrollToEnd();
                    })),
                    _cts.Token);

                _summary.Text = "本轮历史学习结束，已完成联系人的 V2 写入及断点已保存。";
                var sb = new StringBuilder();
                sb.Append("整理完成：联系人 ").Append(scan.ScannedContacts).Append("/").Append(scan.ContactCount);
                sb.Append("，有效聊天消息 ").Append(scan.ObservedTurns);
                sb.Append("，有效问答轮次 ").Append(scan.AnalyzedHumanPairs);
                sb.Append("，模型精确引用证据轮次 ").Append(scan.ModelReferencedPairs);
                sb.Append("，补充候选 ").Append(scan.SupplementedPairs);
                sb.Append("，不可复用未引用轮次 ").Append(scan.NonReusablePairs);
                sb.Append("，V2发布 ").Append(scan.PublishedKnowledge);
                sb.Append("，V2候选 ").Append(scan.CandidateKnowledge);
                sb.Append("，V2重复跳过 ").Append(scan.DuplicateKnowledge);
                sb.Append("。").Append(scan.CoverageWarning);
                if (scan.FailedContacts > 0) sb.Append("，读取失败 ").Append(scan.FailedContacts);
                _summary.Text = sb.ToString();
                _progress.Text = sb + Environment.NewLine
                    + "V2字段：title / type / intent / subject / predicate / entities / aliases / answer / short_answer / conditions / exclusions / required_context / product_ids / risk_level / confidence / authority / status"
                    + Environment.NewLine
                    + "联系人来源：全部买家列表 " + scan.ChatBuyerListContactCount
                    + "，消息管理器 " + scan.MessageManagerContactCount + "，接口 " + scan.ApiContactCount
                    + Environment.NewLine
                    + "消息管理器：" + (scan.MessageManagerOpened ? "已作为兜底自动打开" : (scan.ChatBuyerListContactCount > 0 ? "未打开（已从左侧全部买家列表读取）" : "未找到入口，已使用接口或当前会话兜底"))
                    + (string.IsNullOrWhiteSpace(scan.Diagnostics) ? string.Empty : Environment.NewLine + Environment.NewLine + "诊断信息：" + Environment.NewLine + scan.Diagnostics);
            }
            catch (OperationCanceledException)
            {
                _summary.Text = "历史聊天整理已取消；已经完成的 V2 写入不会回滚。";
                _progress.AppendText(Environment.NewLine + "任务已取消。");
            }
            catch (SmartImportException ex)
            {
                _summary.Text = "历史聊天整理未完成：" + ex.Message;
                _progress.AppendText(Environment.NewLine + ex.Message);
            }
            catch (Exception ex)
            {
                _summary.Text = "历史聊天整理失败：" + ex.Message;
                _progress.AppendText(Environment.NewLine + "失败：" + ex.Message);
            }
            finally
            {
                _running = false;
                SetRunningState(false);
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        private ChatHistoryScanOptions BuildOptions()
        {
            var all = _all.IsChecked == true;
            if (!all && (!_start.SelectedDate.HasValue || !_end.SelectedDate.HasValue))
            {
                MessageBox.Show(_owner, "请选择开始日期和结束日期。", "历史聊天整理", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (!all && _start.SelectedDate.Value.Date > _end.SelectedDate.Value.Date)
            {
                MessageBox.Show(_owner, "开始日期不能晚于结束日期。", "历史聊天整理", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return new ChatHistoryScanOptions
            {
                ScanAll = all,
                StartTime = all ? (DateTime?)null : _start.SelectedDate.Value.Date,
                EndTime = all ? (DateTime?)null : _end.SelectedDate.Value.Date,
                MaxContacts = 5000
            };
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void SetRunningState(bool running)
        {
            _startButton.IsEnabled = !running;
            _cancelButton.IsEnabled = running;
            _all.IsEnabled = !running;
            _range.IsEnabled = !running;
            RefreshRangeState();
        }
    }

    // Compatibility wrapper for any older entry point that still opens a standalone window.
    // The actual implementation is the same V2 page used by the Knowledge Center left navigation.
    public sealed class ChatHistoryScanWindow : Window
    {
        public ChatHistoryScanWindow()
        {
            Title = "历史聊天整理 - Knowledge Center V2";
            Width = 760;
            Height = 650;
            MinWidth = 680;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.White;
            var seller = KnowledgeCenterV2Context.ResolveSeller(this);
            Content = new KnowledgeV2ChatHistoryPage(this, seller);
        }
    }
}
