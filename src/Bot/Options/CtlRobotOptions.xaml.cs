using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.ChromeNs;
using Bot.Knowledge;
using BotLib;

namespace Bot.Options
{
    public partial class CtlRobotOptions : UserControl, IOptions
    {
        private string _seller;
        private ObservableCollection<AiEndpointConfig> _endpoints;
        private bool _loadingPrompt;

        public CtlRobotOptions(string seller)
        {
            InitializeComponent();
            InitUI(seller);
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.Robot; }
        }

        public void InitUI(string seller)
        {
            _seller = seller;
            _endpoints = new ObservableCollection<AiEndpointConfig>(AiEndpointStore.GetEndpoints());
            gridEndpoints.ItemsSource = _endpoints;
            SelectStrategy(AiEndpointStore.GetStrategy());
            if (_endpoints.Count > 0)
            {
                gridEndpoints.SelectedIndex = 0;
            }
            txtApiTestResult.Text = "提示：编辑表格后点击保存；新增/导入/测试后也建议保存一次。";
        }

        private void SelectStrategy(string strategy)
        {
            if (string.IsNullOrWhiteSpace(strategy)) strategy = "按优先级顺序调用";
            foreach (ComboBoxItem item in cmbStrategy.Items)
            {
                if ((item.Content ?? string.Empty).ToString() == strategy)
                {
                    cmbStrategy.SelectedItem = item;
                    return;
                }
            }
            cmbStrategy.SelectedIndex = 0;
        }

        private string CurrentStrategy()
        {
            var item = cmbStrategy.SelectedItem as ComboBoxItem;
            return item == null ? "按优先级顺序调用" : (item.Content ?? string.Empty).ToString();
        }

        public void NavHelp()
        {
            MessageBox.Show("API接口支持 OpenAI 官方和 OpenAI 兼容中转站。建议至少配置一个可用接口，并点击测试选中确认连接正常。", "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void RestoreDefault()
        {
            _endpoints.Clear();
            _endpoints.Add(new AiEndpointConfig());
            gridEndpoints.SelectedIndex = 0;
            SelectStrategy("按优先级顺序调用");
            txtApiTestResult.Text = "已恢复默认，请保存。";
        }

        public void Save(string seller)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            NormalizePriority();
            AiEndpointStore.SetStrategy(CurrentStrategy());
            AiEndpointStore.SaveEndpoints(_endpoints);
            txtApiTestResult.Text = "配置已保存。";
        }

        private void CommitGridEdit()
        {
            try
            {
                gridEndpoints.CommitEdit(DataGridEditingUnit.Cell, true);
                gridEndpoints.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch
            {
            }
        }

        private AiEndpointConfig SelectedEndpoint()
        {
            return gridEndpoints.SelectedItem as AiEndpointConfig;
        }

        private void NormalizePriority()
        {
            var i = 1;
            foreach (var ep in _endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep.Id)) ep.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(ep.Name)) ep.Name = "接口" + i;
                if (string.IsNullOrWhiteSpace(ep.Type)) ep.Type = "OpenAI兼容";
                if (ep.Weight <= 0) ep.Weight = 1;
                if (ep.TimeoutSeconds <= 0) ep.TimeoutSeconds = 35;
                if (ep.Priority <= 0) ep.Priority = i;
                i++;
            }
        }

        private void SyncSelectedPrompt()
        {
            var ep = SelectedEndpoint();
            if (ep != null && !_loadingPrompt)
            {
                ep.SystemPrompt = txtSystemPrompt.Text;
            }
        }

        private void gridEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _loadingPrompt = true;
            try
            {
                var ep = SelectedEndpoint();
                txtSystemPrompt.Text = ep == null ? string.Empty : (ep.SystemPrompt ?? string.Empty);
            }
            finally
            {
                _loadingPrompt = false;
            }
        }

        private void txtSystemPrompt_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingPrompt) return;
            var ep = SelectedEndpoint();
            if (ep != null)
            {
                ep.SystemPrompt = txtSystemPrompt.Text;
            }
        }

        private void btnAddEndpoint_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            var ep = new AiEndpointConfig
            {
                Name = "接口" + (_endpoints.Count + 1),
                Priority = _endpoints.Count + 1,
                Type = "OpenAI兼容",
                Enabled = true,
                TimeoutSeconds = 35,
                Weight = 1
            };
            _endpoints.Add(ep);
            gridEndpoints.SelectedItem = ep;
            txtApiTestResult.Text = "已新增接口，请填写 BaseUrl / ApiKey / Model。";
        }

        private void btnDeleteEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var ep = SelectedEndpoint();
            if (ep == null) return;
            if (_endpoints.Count <= 1)
            {
                txtApiTestResult.Text = "至少保留一个接口。";
                return;
            }
            var idx = _endpoints.IndexOf(ep);
            _endpoints.Remove(ep);
            NormalizePriority();
            gridEndpoints.SelectedIndex = Math.Min(idx, _endpoints.Count - 1);
            txtApiTestResult.Text = "已删除接口，请保存。";
        }

        private void MoveSelected(int offset)
        {
            var ep = SelectedEndpoint();
            if (ep == null) return;
            var oldIndex = _endpoints.IndexOf(ep);
            var newIndex = oldIndex + offset;
            if (newIndex < 0 || newIndex >= _endpoints.Count) return;
            _endpoints.Move(oldIndex, newIndex);
            var p = 1;
            foreach (var item in _endpoints)
            {
                item.Priority = p++;
            }
            gridEndpoints.SelectedItem = ep;
            gridEndpoints.Items.Refresh();
        }

        private void btnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void btnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private async void btnTestSelected_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            var ep = SelectedEndpoint();
            if (ep == null) return;
            btnTestSelected.IsEnabled = false;
            txtApiTestResult.Text = "正在测试 " + ep.Name + " ...";
            try
            {
                var result = await Task.Run(() => MyOpenAI.TestConnection(ep));
                txtApiTestResult.Text = ep.Name + "：" + result;
                gridEndpoints.Items.Refresh();
                Log.Info("AI连接测试结果：" + txtApiTestResult.Text);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestSelected.IsEnabled = true;
            }
        }

        private async void btnTestAll_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            btnTestAll.IsEnabled = false;
            try
            {
                var results = new List<string>();
                foreach (var ep in _endpoints)
                {
                    txtApiTestResult.Text = "正在测试 " + ep.Name + " ...";
                    var result = await Task.Run(() => MyOpenAI.TestConnection(ep));
                    results.Add(ep.Name + "：" + result);
                    gridEndpoints.Items.Refresh();
                }
                txtApiTestResult.Text = string.Join("\n", results);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestAll.IsEnabled = true;
            }
        }

        private JObject BuildConfigJson()
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            return new JObject
            {
                ["version"] = 2,
                ["strategy"] = CurrentStrategy(),
                ["endpoints"] = JArray.FromObject(_endpoints.ToList()),
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private void ApplyConfigJson(string text)
        {
            var token = JToken.Parse(text);
            List<AiEndpointConfig> list;
            if (token.Type == JTokenType.Array)
            {
                list = token.ToObject<List<AiEndpointConfig>>();
            }
            else
            {
                var obj = (JObject)token;
                if (obj["endpoints"] != null)
                {
                    list = obj["endpoints"].ToObject<List<AiEndpointConfig>>();
                    SelectStrategy((obj["strategy"] ?? string.Empty).ToString());
                }
                else
                {
                    list = new List<AiEndpointConfig>
                    {
                        new AiEndpointConfig
                        {
                            Name = "导入接口",
                            BaseUrl = (obj["baseUrl"] ?? obj["BaseUrl"] ?? string.Empty).ToString(),
                            ApiKey = (obj["apiKey"] ?? obj["ApiKey"] ?? string.Empty).ToString(),
                            Model = (obj["model"] ?? obj["Model"] ?? obj["modelName"] ?? string.Empty).ToString(),
                            SystemPrompt = (obj["systemPrompt"] ?? obj["SystemPrompt"] ?? string.Empty).ToString(),
                            Enabled = true,
                            Priority = 1,
                            Type = "OpenAI兼容",
                            TimeoutSeconds = 35,
                            Weight = 1
                        }
                    };
                }
            }

            if (list == null || list.Count < 1) throw new Exception("配置文件中没有接口。 ");
            _endpoints.Clear();
            foreach (var ep in list)
            {
                _endpoints.Add(ep ?? new AiEndpointConfig());
            }
            NormalizePriority();
            gridEndpoints.SelectedIndex = 0;
        }

        private void btnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "导出AI配置",
                    FileName = "qianniu-ai-config.json",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                File.WriteAllText(dlg.FileName, BuildConfigJson().ToString(Formatting.Indented), System.Text.Encoding.UTF8);
                txtApiTestResult.Text = "配置已导出：" + dlg.FileName;
                Log.Info("AI配置已导出：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导出失败：" + ex.Message;
            }
        }

        private void btnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "导入AI配置",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                ApplyConfigJson(File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
                txtApiTestResult.Text = "配置已导入，请点击保存。";
                Log.Info("AI配置已导入：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导入失败：" + ex.Message;
            }
        }
    }

    public class FeatureSettingsWindow : Window
    {
        private readonly TabControl _tabs;
        private ObservableCollection<KnowledgeBaseEntry> _kbItems;
        private ObservableCollection<ComplianceItem> _complianceItems;
        private DataGrid _kbGrid;
        private DataGrid _complianceGrid;
        private TextBox _manualKeywords;
        private TextBox _noAutoKeywords;
        private TextBox _handoffText;
        private CheckBox _rulesEnabled;
        private CheckBox _workHoursEnabled;
        private TextBox _workStartTime;
        private TextBox _workEndTime;
        private ComboBox _offHoursMode;
        private TextBox _offHoursFixedText;
        private CheckBox _notificationEnabled;
        private TextBox _notificationCooldown;
        private CheckBox _notifyWeChat;
        private TextBox _weChatWebhook;
        private CheckBox _notifyQQ;
        private TextBox _qqWebhook;
        private CheckBox _notifyEmail;
        private TextBox _smtpHost;
        private TextBox _smtpPort;
        private CheckBox _smtpSsl;
        private TextBox _smtpUser;
        private PasswordBox _smtpPassword;
        private TextBox _emailTo;
        private CheckBox _notifyFeishu;
        private TextBox _feishuWebhook;
        private CheckBox _notifyDingTalk;
        private TextBox _dingTalkWebhook;
        private CheckBox _orderPlacedReplyEnabled;
        private ComboBox _orderPlacedReplyMode;
        private TextBox _orderPlacedReplyText;
        private TextBox _orderPlacedApiUrl;
        private PasswordBox _orderPlacedApiToken;
        private TextBox _orderPlacedApiTimeout;
        private TextBox _orderPlacedDedupHours;
        private ComboBox _tone;
        private TextBox _maxLength;
        private TextBox _bannedWords;
        private TextBox _requiredPrefix;
        private CheckBox _enableKb;
        private CheckBox _oneSentence;
        private TextBox _licensee;
        private TextBox _licenseKey;
        private TextBox _expireDate;
        private CheckBox _offlineAuth;
        private TextBox _logText;
        private TextBlock _status;

        public FeatureSettingsWindow(string selectedPage)
        {
            Title = "AI客服控制台 - 功能设置";
            Width = 860;
            Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            _status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237)), Margin = new Thickness(4, 0, 0, 0), Text = "修改后请保存。" };
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            footer.Children.Add(_status);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(btns, Dock.Right);
            footer.Children.Add(btns);
            var save = MakeButton("保存全部", 90);
            save.Click += (s, e) => SaveAll();
            btns.Children.Add(save);
            var close = MakeButton("关闭", 70);
            close.Click += (s, e) => Close();
            btns.Children.Add(close);

            _tabs = new TabControl();
            root.Children.Add(_tabs);
            _tabs.Items.Add(new TabItem { Header = "知识库", Content = BuildKnowledgeTab() });
            _tabs.Items.Add(new TabItem { Header = "自动回复规则", Content = BuildRulesTab() });
            _tabs.Items.Add(new TabItem { Header = "消息通知", Content = BuildNotificationTab() });
            _tabs.Items.Add(new TabItem { Header = "消息策略", Content = BuildPolicyTab() });
            _tabs.Items.Add(new TabItem { Header = "日志与调试", Content = BuildLogsTab() });
            _tabs.Items.Add(new TabItem { Header = "账号与授权", Content = BuildLicenseTab() });
            _tabs.Items.Add(new TabItem { Header = "商业化合规清单", Content = BuildComplianceTab() });
            SelectPage(selectedPage);
            SettingsOperationAudit.Attach(this,
                () => (_tabs.SelectedItem as TabItem) == null
                    ? "other" : Convert.ToString((_tabs.SelectedItem as TabItem).Header),
                "feature_settings");
        }

        public static void MyShow(Window owner, string selectedPage)
        {
            var wnd = new FeatureSettingsWindow(selectedPage);
            if (owner != null) wnd.Owner = owner;
            wnd.Show();
        }

        private Button MakeButton(string text, double width)
        {
            return new Button { Content = text, Width = width, Height = 28, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        }

        private Border Card(UIElement child)
        {
            return new Border
            {
                Child = child,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(8)
            };
        }

        private void SelectPage(string selectedPage)
        {
            if (string.IsNullOrWhiteSpace(selectedPage)) return;
            foreach (TabItem tab in _tabs.Items)
            {
                var h = (tab.Header ?? string.Empty).ToString();
                if (h.Contains(selectedPage) || selectedPage.Contains(h))
                {
                    _tabs.SelectedItem = tab;
                    break;
                }
            }
        }

        private UIElement BuildKnowledgeTab()
        {
            var currentKnowledge = BotFeatureStore.GetKnowledgeBase();
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = "知识库已升级为独立中心。智能导入、问答搜索、编辑、JSON导入导出都在新中心中完成，仍然使用同一个 BotFeatureStore 数据源。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock { Text = "知识数量：" + currentKnowledge.Count, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(new TextBlock { Text = "分类数量：" + currentKnowledge.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0).Distinct().Count(), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 16) });
            var open = MakeButton("打开知识库中心", 140);
            open.Click += (s, e) => KnowledgeCenterWindow.MyShow(this);
            panel.Children.Add(open);
            return Card(panel);
        }

        private UIElement BuildRulesTab()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var sp = new StackPanel { Margin = new Thickness(8) };
            _rulesEnabled = new CheckBox { Content = "启用转人工规则", IsChecked = cfg.Enabled, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            sp.Children.Add(_rulesEnabled);
            _manualKeywords = AddLabeledText(sp, "强制转人工关键词", cfg.ManualKeywords, 78, "例：退款、投诉、差评、赔偿、发票、订单隐私。", true);
            _noAutoKeywords = AddLabeledText(sp, "仅人工确认关键词", cfg.NoAutoReplyKeywords, 68, "例：银行卡、身份证、手机号、地址、法律、维权。", true);
            _handoffText = AddLabeledText(sp, "工作时间转人工话术", cfg.HandoffText, 62, "人工在线时命中规则：Bot 不自动发送，只在面板提示并通知人工。", true);

            sp.Children.Add(SectionTitle("买家下单后自动发送"));
            _orderPlacedReplyEnabled = new CheckBox
            {
                Content = "识别到买家新下单后自动发送消息",
                IsChecked = cfg.EnableOrderPlacedReply,
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold
            };
            sp.Children.Add(_orderPlacedReplyEnabled);

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            modeRow.Children.Add(new TextBlock { Text = "回复来源", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedReplyMode = new ComboBox { Width = 190, Height = 26 };
            _orderPlacedReplyMode.Items.Add("固定预设答案");
            _orderPlacedReplyMode.Items.Add("调用HTTP接口");
            _orderPlacedReplyMode.SelectedItem = string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)
                ? "固定预设答案"
                : cfg.OrderPlacedReplyMode;
            modeRow.Children.Add(_orderPlacedReplyMode);
            sp.Children.Add(modeRow);

            _orderPlacedReplyText = AddLabeledText(
                sp,
                "预设/兜底答案",
                cfg.OrderPlacedReplyText,
                72,
                "支持 {客服}、{买家}、{订单号}、{时间}。接口失败时也会使用这段话兜底。",
                true);
            _orderPlacedApiUrl = AddLabeledText(
                sp,
                "HTTP接口",
                cfg.OrderPlacedApiUrl,
                28,
                "POST JSON：event、seller、buyer、orderId、eventTime、message；返回 reply、answer 或 message。",
                false);

            var tokenRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            tokenRow.Children.Add(new TextBlock { Text = "接口令牌", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedApiToken = new PasswordBox { Password = cfg.OrderPlacedApiToken ?? string.Empty, Height = 26, ToolTip = "可留空；填写后使用 Bearer Token" };
            tokenRow.Children.Add(_orderPlacedApiToken);
            sp.Children.Add(tokenRow);

            var runtimeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            runtimeRow.Children.Add(new TextBlock { Text = "接口超时秒", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedApiTimeout = new TextBox { Text = cfg.OrderPlacedApiTimeoutSeconds.ToString(), Width = 70, Height = 26 };
            runtimeRow.Children.Add(_orderPlacedApiTimeout);
            runtimeRow.Children.Add(new TextBlock { Text = "同订单去重小时", Width = 120, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _orderPlacedDedupHours = new TextBox { Text = cfg.OrderPlacedDedupHours.ToString(), Width = 70, Height = 26 };
            runtimeRow.Children.Add(_orderPlacedDedupHours);
            sp.Children.Add(runtimeRow);

            sp.Children.Add(new TextBlock
            {
                Text = "当前仅在 Bot 运行期间识别实时订单卡片，历史订单不会补发。后续兑换码逻辑可由 HTTP 接口按 orderId 返回一次性回复。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement BuildNotificationTab()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var sp = new StackPanel { Margin = new Thickness(8) };

            sp.Children.Add(SectionTitle("人工客服工作时间与下班回复"));
            _workHoursEnabled = new CheckBox { Content = "启用人工客服上下班时间判断", IsChecked = cfg.EnableWorkHours, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(_workHoursEnabled);
            var workRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            workRow.Children.Add(new TextBlock { Text = "上班时间", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _workStartTime = new TextBox { Text = cfg.WorkStartTime, Width = 80, Height = 26 };
            workRow.Children.Add(_workStartTime);
            workRow.Children.Add(new TextBlock { Text = "下班时间", Width = 85, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _workEndTime = new TextBox { Text = cfg.WorkEndTime, Width = 80, Height = 26 };
            workRow.Children.Add(_workEndTime);
            workRow.Children.Add(new TextBlock { Text = "格式 HH:mm；支持跨夜，例如 18:00–09:00。", Margin = new Thickness(12, 4, 0, 0), Foreground = Brushes.Gray });
            sp.Children.Add(workRow);

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            modeRow.Children.Add(new TextBlock { Text = "下班回复方式", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _offHoursMode = new ComboBox { Width = 190, Height = 26 };
            _offHoursMode.Items.Add("AI告知下班时间");
            _offHoursMode.Items.Add("固定预设答案");
            _offHoursMode.SelectedItem = string.IsNullOrWhiteSpace(cfg.OffHoursReplyMode) ? "AI告知下班时间" : cfg.OffHoursReplyMode;
            modeRow.Children.Add(_offHoursMode);
            sp.Children.Add(modeRow);
            _offHoursFixedText = AddLabeledText(sp, "下班固定话术", cfg.OffHoursFixedText, 68, "可使用 {工作时间} 占位符。AI模式调用失败时也使用这段话兜底。", true);

            sp.Children.Add(SectionTitle("转人工通知"));
            _notificationEnabled = new CheckBox { Content = "命中转人工规则时发送通知", IsChecked = cfg.EnableHandoffNotification, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            sp.Children.Add(_notificationEnabled);
            _notificationCooldown = AddLabeledText(sp, "通知去重分钟", cfg.NotificationCooldownMinutes.ToString(), 28, "同一客服、买家和问题在此时间内只通知一次。", false);
            _notifyWeChat = AddChannel(sp, "微信", cfg.NotifyWeChat, "企业微信群机器人 Webhook", cfg.WeChatWebhook, out _weChatWebhook);
            _notifyQQ = AddChannel(sp, "QQ", cfg.NotifyQQ, "QQ机器人 Webhook（需兼容 JSON message/content 字段）", cfg.QQWebhook, out _qqWebhook);
            _notifyFeishu = AddChannel(sp, "飞书", cfg.NotifyFeishu, "飞书群机器人 Webhook", cfg.FeishuWebhook, out _feishuWebhook);
            _notifyDingTalk = AddChannel(sp, "钉钉", cfg.NotifyDingTalk, "钉钉群机器人 Webhook", cfg.DingTalkWebhook, out _dingTalkWebhook);

            var emailHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            _notifyEmail = new CheckBox { Content = "邮箱", IsChecked = cfg.NotifyEmail, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            emailHeader.Children.Add(_notifyEmail);
            emailHeader.Children.Add(new TextBlock { Text = "SMTP 通知", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(emailHeader);
            _smtpHost = AddLabeledText(sp, "SMTP服务器", cfg.SmtpHost, 28, string.Empty, false);
            var smtpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            smtpRow.Children.Add(new TextBlock { Text = "SMTP端口", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _smtpPort = new TextBox { Text = cfg.SmtpPort.ToString(), Width = 70, Height = 26 };
            smtpRow.Children.Add(_smtpPort);
            _smtpSsl = new CheckBox { Content = "SSL", IsChecked = cfg.SmtpEnableSsl, Margin = new Thickness(16, 3, 0, 0) };
            smtpRow.Children.Add(_smtpSsl);
            sp.Children.Add(smtpRow);
            _smtpUser = AddLabeledText(sp, "SMTP账号", cfg.SmtpUser, 28, string.Empty, false);
            var passwordRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            passwordRow.Children.Add(new TextBlock { Text = "SMTP密码", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _smtpPassword = new PasswordBox { Password = cfg.SmtpPassword ?? string.Empty, Height = 26 };
            passwordRow.Children.Add(_smtpPassword);
            sp.Children.Add(passwordRow);
            _emailTo = AddLabeledText(sp, "收件邮箱", cfg.EmailTo, 28, "多个收件人用逗号分隔。", false);

            var test = MakeButton("测试已选通知", 120);
            test.Click += async (s, e) =>
            {
                test.IsEnabled = false;
                _status.Text = "正在发送测试通知...";
                try
                {
                    _status.Text = await HandoffNotificationService.TestAsync(BuildRuleConfigFromUi());
                }
                catch (Exception ex)
                {
                    _status.Text = "测试通知失败：" + ex.Message;
                }
                finally
                {
                    test.IsEnabled = true;
                }
            };
            sp.Children.Add(test);
            sp.Children.Add(new TextBlock
            {
                Text = "企业微信应用消息由 Ubuntu 控制面配置；微信、QQ、飞书、钉钉 Webhook 与 SMTP 密码保存在本机 params.db。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0)
            });
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private TextBlock SectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 14, 0, 9),
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
        }

        private CheckBox AddChannel(StackPanel sp, string name, bool enabled, string label, string value, out TextBox box)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var check = new CheckBox { Content = name, IsChecked = enabled, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(check);
            box = new TextBox { Text = value ?? string.Empty, Height = 26, ToolTip = label };
            row.Children.Add(box);
            sp.Children.Add(row);
            return check;
        }

        private AutoReplyRuleConfig BuildRuleConfigFromUi()
        {
            int cooldown;
            if (!int.TryParse(_notificationCooldown == null ? "10" : _notificationCooldown.Text, out cooldown)) cooldown = 10;
            int smtpPort;
            if (!int.TryParse(_smtpPort == null ? "465" : _smtpPort.Text, out smtpPort)) smtpPort = 465;
            int orderTimeout;
            if (!int.TryParse(_orderPlacedApiTimeout == null ? "10" : _orderPlacedApiTimeout.Text, out orderTimeout)) orderTimeout = 10;
            int orderDedupHours;
            if (!int.TryParse(_orderPlacedDedupHours == null ? "72" : _orderPlacedDedupHours.Text, out orderDedupHours)) orderDedupHours = 72;
            return new AutoReplyRuleConfig
            {
                Enabled = _rulesEnabled == null || (_rulesEnabled.IsChecked ?? true),
                ManualKeywords = _manualKeywords == null ? string.Empty : _manualKeywords.Text,
                NoAutoReplyKeywords = _noAutoKeywords == null ? string.Empty : _noAutoKeywords.Text,
                HandoffText = _handoffText == null ? string.Empty : _handoffText.Text,
                EnableWorkHours = _workHoursEnabled != null && (_workHoursEnabled.IsChecked ?? false),
                WorkStartTime = _workStartTime == null ? "09:00" : _workStartTime.Text,
                WorkEndTime = _workEndTime == null ? "18:00" : _workEndTime.Text,
                OffHoursReplyMode = _offHoursMode == null || _offHoursMode.SelectedItem == null ? "AI告知下班时间" : _offHoursMode.SelectedItem.ToString(),
                OffHoursFixedText = _offHoursFixedText == null ? string.Empty : _offHoursFixedText.Text,
                EnableHandoffNotification = _notificationEnabled != null && (_notificationEnabled.IsChecked ?? false),
                NotificationCooldownMinutes = Math.Max(1, Math.Min(1440, cooldown)),
                NotifyWeChat = _notifyWeChat != null && (_notifyWeChat.IsChecked ?? false),
                WeChatWebhook = _weChatWebhook == null ? string.Empty : _weChatWebhook.Text,
                NotifyQQ = _notifyQQ != null && (_notifyQQ.IsChecked ?? false),
                QQWebhook = _qqWebhook == null ? string.Empty : _qqWebhook.Text,
                NotifyEmail = _notifyEmail != null && (_notifyEmail.IsChecked ?? false),
                SmtpHost = _smtpHost == null ? string.Empty : _smtpHost.Text,
                SmtpPort = smtpPort,
                SmtpEnableSsl = _smtpSsl != null && (_smtpSsl.IsChecked ?? true),
                SmtpUser = _smtpUser == null ? string.Empty : _smtpUser.Text,
                SmtpPassword = _smtpPassword == null ? string.Empty : _smtpPassword.Password,
                EmailTo = _emailTo == null ? string.Empty : _emailTo.Text,
                NotifyFeishu = _notifyFeishu != null && (_notifyFeishu.IsChecked ?? false),
                FeishuWebhook = _feishuWebhook == null ? string.Empty : _feishuWebhook.Text,
                NotifyDingTalk = _notifyDingTalk != null && (_notifyDingTalk.IsChecked ?? false),
                DingTalkWebhook = _dingTalkWebhook == null ? string.Empty : _dingTalkWebhook.Text,
                EnableOrderPlacedReply = _orderPlacedReplyEnabled != null && (_orderPlacedReplyEnabled.IsChecked ?? false),
                OrderPlacedReplyMode = _orderPlacedReplyMode == null || _orderPlacedReplyMode.SelectedItem == null
                    ? "固定预设答案"
                    : _orderPlacedReplyMode.SelectedItem.ToString(),
                OrderPlacedReplyText = _orderPlacedReplyText == null ? string.Empty : _orderPlacedReplyText.Text,
                OrderPlacedApiUrl = _orderPlacedApiUrl == null ? string.Empty : _orderPlacedApiUrl.Text,
                OrderPlacedApiToken = _orderPlacedApiToken == null ? string.Empty : _orderPlacedApiToken.Password,
                OrderPlacedApiTimeoutSeconds = Math.Max(3, Math.Min(60, orderTimeout)),
                OrderPlacedDedupHours = Math.Max(1, Math.Min(720, orderDedupHours))
            };
        }

        private UIElement BuildPolicyTab()
        {
            var cfg = BotFeatureStore.GetMessagePolicy();
            var sp = new StackPanel { Margin = new Thickness(8) };
            _enableKb = new CheckBox { Content = "启用知识库注入", IsChecked = cfg.EnableKnowledgeBase, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(_enableKb);
            _oneSentence = new CheckBox { Content = "强制短句回复，避免长篇解释", IsChecked = cfg.OneSentence, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(_oneSentence);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = "语气风格", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            _tone = new ComboBox { Width = 160, Height = 26 };
            foreach (var t in new[] { "自然亲切", "专业克制", "活泼热情", "简短直接" }) _tone.Items.Add(t);
            _tone.SelectedItem = string.IsNullOrWhiteSpace(cfg.Tone) ? "自然亲切" : cfg.Tone;
            row.Children.Add(_tone);
            sp.Children.Add(row);
            _maxLength = AddLabeledText(sp, "最大字数", cfg.MaxAnswerLength.ToString(), 30, "建议 40 到 80。", false);
            _requiredPrefix = AddLabeledText(sp, "固定前缀", cfg.RequiredPrefix, 30, "可留空；例如“亲，”但不建议每句都加。", false);
            _bannedWords = AddLabeledText(sp, "禁用词", cfg.BannedWords, 80, "逗号分隔。生成结果中出现这些词会被删除。", true);
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement BuildLogsTab()
        {
            var panel = new DockPanel { Margin = new Thickness(8) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(buttons, Dock.Top);
            panel.Children.Add(buttons);
            var refresh = MakeButton("刷新日志", 90);
            refresh.Click += (s, e) => RefreshLogs();
            buttons.Children.Add(refresh);
            var open = MakeButton("打开目录", 90);
            open.Click += (s, e) => OpenLogFolder();
            buttons.Children.Add(open);
            var copy = MakeButton("复制日志", 90);
            copy.Click += (s, e) => Clipboard.SetText(_logText == null ? string.Empty : _logText.Text);
            buttons.Children.Add(copy);
            var test = MakeButton("写入测试日志", 105);
            test.Click += (s, e) =>
            {
                Log.Info("日志与调试页面测试写入：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                Log.Flush();
                RefreshLogs();
            };
            buttons.Children.Add(test);
            var clear = MakeButton("清空日志", 90);
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show("确认清空当前运行日志？", "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Log.Flush();
                Log.Clear();
                Log.Info("日志已由设置页面清空并重新开始记录。");
                Log.Flush();
                RefreshLogs();
            };
            buttons.Children.Add(clear);
            _logText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnly = true, FontFamily = new FontFamily("Consolas") };
            panel.Children.Add(_logText);
            RefreshLogs();
            return Card(panel);
        }

        private UIElement BuildLicenseTab()
        {
            var cfg = BotFeatureStore.GetLicense();
            var sp = new StackPanel { Margin = new Thickness(8) };
            sp.Children.Add(new TextBlock { Text = "当前为本地授权信息管理，不联网验签。商业版可接入服务器授权、设备绑定、到期时间、版本权限。", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)), Margin = new Thickness(0, 0, 0, 10) });
            _licensee = AddLabeledText(sp, "授权对象", cfg.Licensee, 30, "客户公司/店铺/联系人。", false);
            _licenseKey = AddLabeledText(sp, "授权码", cfg.LicenseKey, 30, "商业版可替换为服务端签名授权码。", false);
            _expireDate = AddLabeledText(sp, "到期日期", cfg.ExpireDate, 30, "格式：yyyy-MM-dd。", false);
            _offlineAuth = new CheckBox { Content = "允许离线使用", IsChecked = cfg.AllowOffline, Margin = new Thickness(90, 6, 0, 8) };
            sp.Children.Add(_offlineAuth);
            return new ScrollViewer { Content = Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement BuildComplianceTab()
        {
            _complianceItems = new ObservableCollection<ComplianceItem>(BotFeatureStore.GetComplianceChecklist());
            var panel = new DockPanel();
            var tip = new TextBlock
            {
                Text = "商业化前建议逐项确认。此清单用于内部自查，不构成法律意见；正式上线前建议让律师和平台运营负责人复核。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(8)
            };
            DockPanel.SetDock(tip, Dock.Top);
            panel.Children.Add(tip);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };
            DockPanel.SetDock(buttons, Dock.Top);
            panel.Children.Add(buttons);
            var reset = MakeButton("载入默认清单", 110);
            reset.Click += (s, e) => { _complianceItems.Clear(); foreach (var it in BotFeatureStore.DefaultComplianceChecklist()) _complianceItems.Add(it); };
            buttons.Children.Add(reset);
            var done = MakeButton("选中标记完成", 110);
            done.Click += (s, e) => { var it = _complianceGrid.SelectedItem as ComplianceItem; if (it != null) { it.Status = "已完成"; _complianceGrid.Items.Refresh(); } };
            buttons.Children.Add(done);
            var export = MakeButton("导出清单", 90);
            export.Click += (s, e) => ExportCompliance();
            buttons.Children.Add(export);
            _complianceGrid = new DataGrid { AutoGenerateColumns = true, CanUserAddRows = true, CanUserDeleteRows = true, ItemsSource = _complianceItems, Margin = new Thickness(8), MinHeight = 420 };
            panel.Children.Add(_complianceGrid);
            return panel;
        }

        private TextBox AddLabeledText(StackPanel sp, string label, string text, double height, string tip, bool multi)
        {
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var row = new DockPanel();
            outer.Children.Add(row);
            row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) });
            var tb = new TextBox { Text = text ?? string.Empty, Height = height, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            row.Children.Add(tb);
            if (!string.IsNullOrWhiteSpace(tip)) outer.Children.Add(new TextBlock { Text = tip, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)), FontSize = 11, Margin = new Thickness(90, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(outer);
            return tb;
        }

        private void CommitFeatureGrids()
        {
            try
            {
                if (_kbGrid != null)
                {
                    _kbGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                    _kbGrid.CommitEdit(DataGridEditingUnit.Row, true);
                }
                if (_complianceGrid != null)
                {
                    _complianceGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                    _complianceGrid.CommitEdit(DataGridEditingUnit.Row, true);
                }
            }
            catch
            {
            }
        }

        private void SaveAll()
        {
            try
            {
                CommitFeatureGrids();
                if (_kbItems != null) BotFeatureStore.SaveKnowledgeBase(_kbItems.ToList());
                if (_rulesEnabled != null)
                {
                    BotFeatureStore.SaveAutoReplyRules(BuildRuleConfigFromUi());
                }
                if (_tone != null)
                {
                    int max = 60;
                    int.TryParse(_maxLength == null ? "60" : _maxLength.Text, out max);
                    BotFeatureStore.SaveMessagePolicy(new MessagePolicyConfig
                    {
                        EnableKnowledgeBase = _enableKb != null && (_enableKb.IsChecked ?? true),
                        OneSentence = _oneSentence != null && (_oneSentence.IsChecked ?? true),
                        Tone = (_tone.SelectedItem ?? "自然亲切").ToString(),
                        MaxAnswerLength = max <= 0 ? 60 : max,
                        BannedWords = _bannedWords == null ? string.Empty : _bannedWords.Text,
                        RequiredPrefix = _requiredPrefix == null ? string.Empty : _requiredPrefix.Text
                    });
                }
                if (_licensee != null)
                {
                    BotFeatureStore.SaveLicense(new LicenseConfig
                    {
                        Licensee = _licensee.Text,
                        LicenseKey = _licenseKey.Text,
                        ExpireDate = _expireDate.Text,
                        AllowOffline = _offlineAuth != null && (_offlineAuth.IsChecked ?? true)
                    });
                }
                if (_complianceItems != null) BotFeatureStore.SaveComplianceChecklist(_complianceItems.ToList());
                _status.Text = "已保存：" + DateTime.Now.ToString("HH:mm:ss");
                SettingsOperationAudit.SaveResult("feature_settings",
                    (_tabs.SelectedItem as TabItem) == null ? "other"
                        : Convert.ToString((_tabs.SelectedItem as TabItem).Header),
                    "save_all", true);
                Log.Info("功能设置已保存（值不记录）");
            }
            catch (Exception ex)
            {
                SettingsOperationAudit.SaveResult("feature_settings",
                    (_tabs.SelectedItem as TabItem) == null ? "other"
                        : Convert.ToString((_tabs.SelectedItem as TabItem).Header),
                    "save_all", false, ex);
                Log.Exception(ex);
                _status.Text = "保存失败：" + ex.Message;
            }
        }

        private void ImportKnowledge()
        {
            try
            {
                var dlg = new OpenFileDialog { Title = "导入知识库", Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*" };
                if (dlg.ShowDialog() != true) return;
                var list = JsonConvert.DeserializeObject<List<KnowledgeBaseEntry>>(File.ReadAllText(dlg.FileName, Encoding.UTF8));
                if (list == null) throw new Exception("JSON中没有知识库数据");
                var overwrite = MessageBox.Show("是否覆盖全部知识？\n\n选择“否”将追加导入并按问题去重。", "导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (overwrite == MessageBoxResult.Cancel) return;
                if (overwrite == MessageBoxResult.Yes)
                {
                    _kbItems.Clear();
                    foreach (var it in list) _kbItems.Add(it);
                    _status.Text = "知识库已覆盖导入，请保存。";
                }
                else
                {
                    var seen = new HashSet<string>(_kbItems.Select(x => KnowledgeAiService.NormalizeQuestion(x.Title)));
                    var added = 0;
                    foreach (var it in list)
                    {
                        var key = KnowledgeAiService.NormalizeQuestion(it.Title);
                        if (seen.Contains(key)) continue;
                        _kbItems.Add(it);
                        seen.Add(key);
                        added++;
                    }
                    _status.Text = "知识库已追加导入 " + added + " 条，请保存。";
                }
            }
            catch (Exception ex)
            {
                _status.Text = "导入失败：" + ex.Message;
            }
        }

        private void ExportKnowledge()
        {
            try
            {
                CommitFeatureGrids();
                var dlg = new SaveFileDialog { Title = "导出知识库", FileName = "qianniu-knowledge-base.json", Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*" };
                if (dlg.ShowDialog() != true) return;
                File.WriteAllText(dlg.FileName, JsonConvert.SerializeObject(_kbItems.ToList(), Formatting.Indented), Encoding.UTF8);
                _status.Text = "知识库已导出：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                _status.Text = "导出失败：" + ex.Message;
            }
        }

        private string[] LogSearchRoots()
        {
            return new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.CurrentDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bot"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bot")
            };
        }

        private void RefreshLogs()
        {
            try
            {
                Log.Flush();
                var files = new List<string>();
                var currentLog = Log.CurrentFileName;
                if (!string.IsNullOrWhiteSpace(currentLog) && File.Exists(currentLog)) files.Add(currentLog);
                foreach (var root in LogSearchRoots().Distinct())
                {
                    if (!Directory.Exists(root)) continue;
                    files.AddRange(Directory.GetFiles(root, "*.log", SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.GetFiles(root, "*.txt", SearchOption.TopDirectoryOnly)
                        .Where(f =>
                        {
                            var name = Path.GetFileName(f) ?? string.Empty;
                            return name.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0
                                || name.IndexOf("日志", StringComparison.OrdinalIgnoreCase) >= 0
                                || string.Equals(f, currentLog, StringComparison.OrdinalIgnoreCase);
                        }));
                }
                files = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(File.GetLastWriteTime).Take(8).ToList();
                if (files.Count < 1)
                {
                    _logText.Text = "尚未找到日志文件。预期运行日志：" + (string.IsNullOrWhiteSpace(currentLog) ? "未初始化" : currentLog)
                        + Environment.NewLine + "程序目录：" + AppDomain.CurrentDomain.BaseDirectory;
                    return;
                }
                var sb = new StringBuilder();
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    sb.AppendLine("===== " + file + " =====");
                    sb.AppendLine("大小 " + info.Length + " bytes｜更新时间 " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine(SafeReadTail(file, 30000));
                    sb.AppendLine();
                }
                _logText.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                _logText.Text = "读取日志失败：" + ex.Message;
            }
        }

        private string SafeReadTail(string file, int maxChars)
        {
            foreach (var encoding in new[] { Encoding.GetEncoding(936), Encoding.UTF8, Encoding.Default })
            {
                try
                {
                    var value = File.ReadAllText(file, encoding);
                    if (value.Length > maxChars) value = value.Substring(value.Length - maxChars);
                    return value;
                }
                catch
                {
                }
            }
            return "读取失败：无法用 GBK、UTF-8 或系统默认编码读取该文件。";
        }

        private void OpenLogFolder()
        {
            try
            {
                Log.Flush();
                var file = Log.CurrentFileName;
                var dir = string.IsNullOrWhiteSpace(file) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(file);
                if (string.IsNullOrWhiteSpace(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                _status.Text = "打开目录失败：" + ex.Message;
            }
        }

        private void ExportCompliance()
        {
            try
            {
                CommitFeatureGrids();
                var dlg = new SaveFileDialog { Title = "导出商业化合规清单", FileName = "qianniu-bot-compliance-checklist.md", Filter = "Markdown文件 (*.md)|*.md|所有文件 (*.*)|*.*" };
                if (dlg.ShowDialog() != true) return;
                var sb = new StringBuilder();
                sb.AppendLine("# AI客服控制台商业化合规清单");
                sb.AppendLine();
                sb.AppendLine("> 内部自查文件，不构成法律意见。上线前建议由律师、平台运营负责人和技术负责人共同复核。");
                sb.AppendLine();
                sb.AppendLine("| 分类 | 检查项 | 状态 | 责任人 | 备注 |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var it in _complianceItems)
                {
                    sb.AppendLine("| " + Esc(it.Category) + " | " + Esc(it.Item) + " | " + Esc(it.Status) + " | " + Esc(it.Owner) + " | " + Esc(it.Note) + " |");
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                _status.Text = "合规清单已导出：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                _status.Text = "导出失败：" + ex.Message;
            }
        }

        private string Esc(string text)
        {
            return (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}

namespace Bot.ChromeNs
{
    public class AiEndpointConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public string TextModel { get; set; }
        public string VisionModel { get; set; }
        public bool SupportsVision { get; set; }
        public int MaxImageSizeMb { get; set; }
        public int VisionTimeoutSeconds { get; set; }
        public string SystemPrompt { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public int Weight { get; set; }
        public int TimeoutSeconds { get; set; }
        public int RetryCount { get; set; }
        public string LastStatus { get; set; }
        public long LastLatencyMs { get; set; }
        public DateTime LastTestTime { get; set; }

        public AiEndpointConfig()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "默认接口";
            Type = "OpenAI兼容";
            BaseUrl = string.Empty;
            ApiKey = string.Empty;
            Model = string.Empty;
            TextModel = string.Empty;
            VisionModel = string.Empty;
            SupportsVision = false;
            MaxImageSizeMb = 5;
            VisionTimeoutSeconds = 45;
            SystemPrompt = string.Empty;
            Enabled = true;
            Priority = 1;
            Weight = 1;
            TimeoutSeconds = 35;
            RetryCount = 0;
            LastStatus = "未测试";
            LastLatencyMs = 0;
            LastTestTime = DateTime.MinValue;
        }

        [JsonIgnore]
        public string ApiKeyMasked
        {
            get
            {
                if (string.IsNullOrEmpty(ApiKey)) return string.Empty;
                if (ApiKey.Length <= 10) return "******";
                return ApiKey.Substring(0, 6) + "..." + ApiKey.Substring(ApiKey.Length - 4);
            }
        }

        public void NormalizeVisionDefaults()
        {
            if (string.IsNullOrWhiteSpace(TextModel)) TextModel = Model ?? string.Empty;
            if (string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(TextModel)) Model = TextModel;
            if (MaxImageSizeMb <= 0) MaxImageSizeMb = 5;
            MaxImageSizeMb = Math.Max(1, Math.Min(20, MaxImageSizeMb));
            if (VisionTimeoutSeconds <= 0) VisionTimeoutSeconds = 45;
            VisionTimeoutSeconds = Math.Max(10, Math.Min(180, VisionTimeoutSeconds));
            if (string.IsNullOrWhiteSpace(VisionModel)) VisionModel = string.Empty;
        }

        [JsonIgnore]
        public string VisionStatus
        {
            get
            {
                if (!SupportsVision) return "未启用";
                return string.IsNullOrWhiteSpace(VisionModel) ? "已启用但未配置模型" : "已启用";
            }
        }

        public AiEndpointConfig Clone()
        {
            NormalizeVisionDefaults();
            return new AiEndpointConfig
            {
                Id = string.IsNullOrEmpty(Id) ? Guid.NewGuid().ToString("N") : Id,
                Name = Name,
                Type = Type,
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Model = Model,
                TextModel = TextModel,
                VisionModel = VisionModel,
                SupportsVision = SupportsVision,
                MaxImageSizeMb = MaxImageSizeMb,
                VisionTimeoutSeconds = VisionTimeoutSeconds,
                SystemPrompt = SystemPrompt,
                Enabled = Enabled,
                Priority = Priority,
                Weight = Weight,
                TimeoutSeconds = TimeoutSeconds,
                RetryCount = RetryCount,
                LastStatus = LastStatus,
                LastLatencyMs = LastLatencyMs,
                LastTestTime = LastTestTime
            };
        }
    }

    public static class AiEndpointStore
    {
        private const string EndpointKey = "AiEndpointListJson";
        private const string StrategyKey = "AiDispatchStrategy";
        private const string StoreScope = "ai";

        public static string GetStrategy()
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(StrategyKey, StoreScope, "按优先级顺序调用");
        }

        public static void SetStrategy(string strategy)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(StrategyKey, StoreScope, string.IsNullOrWhiteSpace(strategy) ? "按优先级顺序调用" : strategy.Trim());
        }

        public static string GetEndpointsJson()
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(EndpointKey, StoreScope, string.Empty);
        }

        public static void SaveEndpointsJson(string json)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(EndpointKey, StoreScope, json ?? string.Empty);
        }

        public static List<AiEndpointConfig> GetEndpoints()
        {
            var json = GetEndpointsJson();
            var list = new List<AiEndpointConfig>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { list = JsonConvert.DeserializeObject<List<AiEndpointConfig>>(json) ?? new List<AiEndpointConfig>(); }
                catch { list = new List<AiEndpointConfig>(); }
            }
            if (list.Count < 1) list.Add(CreateLegacyDefaultEndpoint());
            Normalize(list);
            return list;
        }

        public static List<AiEndpointConfig> GetVisionEnabledEndpoints()
        {
            return GetEndpoints()
                .Where(e => e.Enabled && e.SupportsVision && !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.BaseUrl) && !string.IsNullOrWhiteSpace(e.VisionModel))
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Name)
                .ToList();
        }

        public static List<AiEndpointConfig> GetEnabledEndpoints()
        {
            return GetEndpoints()
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.TextModel))
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Name)
                .ToList();
        }

        public static void SaveEndpoints(IEnumerable<AiEndpointConfig> endpoints)
        {
            var list = endpoints == null ? new List<AiEndpointConfig>() : endpoints.Select(e => e == null ? new AiEndpointConfig() : e.Clone()).ToList();
            if (list.Count < 1) list.Add(CreateLegacyDefaultEndpoint());
            Normalize(list);
            SaveEndpointsJson(JsonConvert.SerializeObject(list, Formatting.Indented));
            var primary = list.OrderBy(e => e.Priority).FirstOrDefault();
            if (primary != null)
            {
                Params.Robot.SetBaseUrl(primary.BaseUrl ?? string.Empty);
                Params.Robot.SetApiKey(primary.ApiKey ?? string.Empty);
                Params.Robot.SetModelName(primary.TextModel ?? primary.Model ?? string.Empty);
                Params.Robot.SetSystemPrompt(primary.SystemPrompt ?? string.Empty);
            }
        }

        public static AiEndpointConfig CreateLegacyDefaultEndpoint()
        {
            return new AiEndpointConfig
            {
                Name = "默认接口",
                Type = "OpenAI兼容",
                BaseUrl = Params.Robot.GetBaseUrl() ?? string.Empty,
                ApiKey = Params.Robot.GetApiKey() ?? string.Empty,
                Model = Params.Robot.GetModelName() ?? string.Empty,
                TextModel = Params.Robot.GetModelName() ?? string.Empty,
                SupportsVision = false,
                VisionModel = string.Empty,
                MaxImageSizeMb = 5,
                VisionTimeoutSeconds = 45,
                SystemPrompt = Params.Robot.GetSystemPrompt() ?? string.Empty,
                Enabled = true,
                Priority = 1,
                Weight = 1,
                TimeoutSeconds = 35,
                RetryCount = 0,
                LastStatus = "未测试"
            };
        }

        private static void Normalize(List<AiEndpointConfig> list)
        {
            var p = 1;
            foreach (var endpoint in list.OrderBy(e => e.Priority).ThenBy(e => e.Name).ToList())
            {
                if (string.IsNullOrWhiteSpace(endpoint.Id)) endpoint.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(endpoint.Name)) endpoint.Name = "接口" + p;
                if (string.IsNullOrWhiteSpace(endpoint.Type)) endpoint.Type = "OpenAI兼容";
                if (endpoint.Priority <= 0) endpoint.Priority = p;
                if (endpoint.Weight <= 0) endpoint.Weight = 1;
                if (endpoint.TimeoutSeconds <= 0) endpoint.TimeoutSeconds = 35;
                endpoint.NormalizeVisionDefaults();
                if (endpoint.RetryCount < 0) endpoint.RetryCount = 0;
                p++;
            }
        }
    }

    public class KnowledgeBaseEntry
    {
        public bool Enabled { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Keywords { get; set; }
        public string Answer { get; set; }
        public string UpdatedAt { get; set; }
        public string Id { get; set; }
        public string CreatedAt { get; set; }
        public bool AiGenerated { get; set; }
        public string SourceType { get; set; }

        public KnowledgeBaseEntry()
        {
            Enabled = true;
            Category = "通用";
            Title = string.Empty;
            Keywords = string.Empty;
            Answer = string.Empty;
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            CreatedAt = UpdatedAt;
            Id = string.Empty;
            AiGenerated = false;
            SourceType = string.Empty;
        }
    }

    public class AutoReplyRuleConfig
    {
        public bool Enabled { get; set; }
        public string ManualKeywords { get; set; }
        public string NoAutoReplyKeywords { get; set; }
        public string HandoffText { get; set; }
        public bool EnableWorkHours { get; set; }
        public string WorkStartTime { get; set; }
        public string WorkEndTime { get; set; }
        public string OffHoursReplyMode { get; set; }
        public string OffHoursFixedText { get; set; }
        public bool EnableHandoffNotification { get; set; }
        public int NotificationCooldownMinutes { get; set; }
        public bool NotifyWeChat { get; set; }
        public string WeChatWebhook { get; set; }
        public bool NotifyQQ { get; set; }
        public string QQWebhook { get; set; }
        public bool NotifyEmail { get; set; }
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public bool SmtpEnableSsl { get; set; }
        public string SmtpUser { get; set; }
        public string SmtpPassword { get; set; }
        public string EmailTo { get; set; }
        public bool NotifyFeishu { get; set; }
        public string FeishuWebhook { get; set; }
        public bool NotifyDingTalk { get; set; }
        public string DingTalkWebhook { get; set; }
        public bool EnableOrderPlacedReply { get; set; }
        public string OrderPlacedReplyMode { get; set; }
        public string OrderPlacedReplyText { get; set; }
        public string OrderPlacedApiUrl { get; set; }
        public string OrderPlacedApiToken { get; set; }
        public int OrderPlacedApiTimeoutSeconds { get; set; }
        public int OrderPlacedDedupHours { get; set; }

        public static AutoReplyRuleConfig Default()
        {
            return new AutoReplyRuleConfig
            {
                Enabled = true,
                ManualKeywords = "退款,退货,投诉,差评,赔偿,发票,税票,订单隐私,身份证,银行卡,法律,维权,平台介入",
                NoAutoReplyKeywords = "手机号,地址,隐私,密码,账号,验证码,转账,补偿,客服主管",
                HandoffText = "亲，这个问题需要人工客服核实，我已为您转人工处理。",
                EnableWorkHours = true,
                WorkStartTime = "09:00",
                WorkEndTime = "18:00",
                OffHoursReplyMode = "AI告知下班时间",
                OffHoursFixedText = "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。",
                EnableHandoffNotification = false,
                NotificationCooldownMinutes = 10,
                SmtpPort = 465,
                SmtpEnableSsl = true,
                EnableOrderPlacedReply = false,
                OrderPlacedReplyMode = "固定预设答案",
                OrderPlacedReplyText = "亲，您的订单已收到，我们会尽快为您处理。",
                OrderPlacedApiUrl = string.Empty,
                OrderPlacedApiToken = string.Empty,
                OrderPlacedApiTimeoutSeconds = 10,
                OrderPlacedDedupHours = 72
            };
        }
    }

    public class AutoReplyRuleDecision
    {
        public bool Matched { get; set; }
        public bool AllowAutoReply { get; set; }
        public bool UseAiReply { get; set; }
        public bool IsOffHours { get; set; }
        public string HitKeyword { get; set; }
        public string Reason { get; set; }
        public string ReplyText { get; set; }
        public string WorkHoursText { get; set; }
    }

    public class MessagePolicyConfig
    {
        public bool EnableKnowledgeBase { get; set; }
        public bool OneSentence { get; set; }
        public string Tone { get; set; }
        public int MaxAnswerLength { get; set; }
        public string BannedWords { get; set; }
        public string RequiredPrefix { get; set; }

        public static MessagePolicyConfig Default()
        {
            return new MessagePolicyConfig
            {
                EnableKnowledgeBase = true,
                OneSentence = true,
                Tone = "自然亲切",
                MaxAnswerLength = 60,
                BannedWords = "绝对,肯定百分百,包赔,随便退,一定当天到",
                RequiredPrefix = string.Empty
            };
        }
    }

    public class LicenseConfig
    {
        public string Licensee { get; set; }
        public string LicenseKey { get; set; }
        public string ExpireDate { get; set; }
        public bool AllowOffline { get; set; }

        public static LicenseConfig Default()
        {
            return new LicenseConfig { Licensee = string.Empty, LicenseKey = string.Empty, ExpireDate = string.Empty, AllowOffline = true };
        }
    }

    public class ComplianceItem
    {
        public string Category { get; set; }
        public string Item { get; set; }
        public string Status { get; set; }
        public string Owner { get; set; }
        public string Note { get; set; }

        public ComplianceItem()
        {
            Category = string.Empty;
            Item = string.Empty;
            Status = "未开始";
            Owner = string.Empty;
            Note = string.Empty;
        }
    }

    public static class BotFeatureStore
    {
        private const string Scope = "feature";
        private const string KnowledgeKey = "KnowledgeBaseJson";
        private const string RuleKey = "AutoReplyRulesJson";
        private const string PolicyKey = "MessagePolicyJson";
        private const string LicenseKey = "LicenseJson";
        private const string ComplianceKey = "ComplianceChecklistJson";
        private const string SmartImportTimeoutKey = "SmartImportTimeoutSeconds";

        private static string GetJson(string key)
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(key, Scope, string.Empty);
        }

        private static void SaveJson(string key, string json)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(key, Scope, json ?? string.Empty);
        }

        private static T Read<T>(string key, T def)
        {
            var json = GetJson(key);
            if (string.IsNullOrWhiteSpace(json)) return def;
            try { return JsonConvert.DeserializeObject<T>(json); }
            catch { return def; }
        }

        public static List<KnowledgeBaseEntry> GetKnowledgeBase()
        {
            var list = Read(KnowledgeKey, new List<KnowledgeBaseEntry>());
            if (list == null) list = new List<KnowledgeBaseEntry>();
            return list;
        }

        public static void SaveKnowledgeBase(List<KnowledgeBaseEntry> list)
        {
            SaveJson(KnowledgeKey, JsonConvert.SerializeObject(list ?? new List<KnowledgeBaseEntry>(), Formatting.Indented));
        }

        public static int GetSmartImportTimeoutSeconds()
        {
            var raw = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(SmartImportTimeoutKey, Scope, "600");
            int seconds;
            if (!int.TryParse(raw, out seconds)) seconds = 600;
            return Bot.Knowledge.KnowledgeAiService.ClampTimeout(seconds);
        }

        public static void SaveSmartImportTimeoutSeconds(int seconds)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(SmartImportTimeoutKey, Scope, Bot.Knowledge.KnowledgeAiService.ClampTimeout(seconds).ToString());
        }

        public static AutoReplyRuleConfig GetAutoReplyRules()
        {
            var cfg = Read(RuleKey, AutoReplyRuleConfig.Default()) ?? AutoReplyRuleConfig.Default();
            if (string.IsNullOrWhiteSpace(cfg.WorkStartTime) && string.IsNullOrWhiteSpace(cfg.WorkEndTime))
            {
                cfg.EnableWorkHours = true;
            }
            if (string.IsNullOrWhiteSpace(cfg.WorkStartTime)) cfg.WorkStartTime = "09:00";
            if (string.IsNullOrWhiteSpace(cfg.WorkEndTime)) cfg.WorkEndTime = "18:00";
            if (string.IsNullOrWhiteSpace(cfg.OffHoursReplyMode)) cfg.OffHoursReplyMode = "AI告知下班时间";
            if (string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)) cfg.OffHoursFixedText = AutoReplyRuleConfig.Default().OffHoursFixedText;
            if (cfg.NotificationCooldownMinutes <= 0) cfg.NotificationCooldownMinutes = 10;
            if (cfg.SmtpPort <= 0) cfg.SmtpPort = 465;
            if (string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyMode)) cfg.OrderPlacedReplyMode = "固定预设答案";
            if (string.IsNullOrWhiteSpace(cfg.OrderPlacedReplyText)) cfg.OrderPlacedReplyText = AutoReplyRuleConfig.Default().OrderPlacedReplyText;
            if (cfg.OrderPlacedApiTimeoutSeconds <= 0) cfg.OrderPlacedApiTimeoutSeconds = 10;
            if (cfg.OrderPlacedDedupHours <= 0) cfg.OrderPlacedDedupHours = 72;
            return cfg;
        }

        public static void SaveAutoReplyRules(AutoReplyRuleConfig cfg)
        {
            SaveJson(RuleKey, JsonConvert.SerializeObject(cfg ?? AutoReplyRuleConfig.Default(), Formatting.Indented));
        }

        public static MessagePolicyConfig GetMessagePolicy()
        {
            return Read(PolicyKey, MessagePolicyConfig.Default()) ?? MessagePolicyConfig.Default();
        }

        public static void SaveMessagePolicy(MessagePolicyConfig cfg)
        {
            SaveJson(PolicyKey, JsonConvert.SerializeObject(cfg ?? MessagePolicyConfig.Default(), Formatting.Indented));
        }

        public static LicenseConfig GetLicense()
        {
            return Read(LicenseKey, LicenseConfig.Default()) ?? LicenseConfig.Default();
        }

        public static void SaveLicense(LicenseConfig cfg)
        {
            SaveJson(LicenseKey, JsonConvert.SerializeObject(cfg ?? LicenseConfig.Default(), Formatting.Indented));
        }

        public static List<ComplianceItem> GetComplianceChecklist()
        {
            var list = Read(ComplianceKey, new List<ComplianceItem>());
            if (list == null || list.Count < 1) list = DefaultComplianceChecklist();
            return list;
        }

        public static void SaveComplianceChecklist(List<ComplianceItem> list)
        {
            SaveJson(ComplianceKey, JsonConvert.SerializeObject(list ?? DefaultComplianceChecklist(), Formatting.Indented));
        }

        public static List<ComplianceItem> DefaultComplianceChecklist()
        {
            return new List<ComplianceItem>
            {
                NewCompliance("产品定位", "产品对外定位为客服辅助工具，不承诺替代人工、不承诺完全准确", "未开始", "避免误导宣传；销售页和合同均要写清楚"),
                NewCompliance("平台授权", "确认千牛/淘宝平台规则、服务市场规则、插件分发方式和账号授权边界", "未开始", "避免绕过平台限制或被认定为违规插件"),
                NewCompliance("商家授权", "客户首次使用前签署授权与免责声明，明确由商家控制自动回复开关", "未开始", "保存授权记录和版本号"),
                NewCompliance("隐私合规", "隐私政策说明采集哪些聊天内容、用途、保存期限、第三方API传输", "未开始", "最小化采集，默认不保存敏感字段"),
                NewCompliance("个人信息", "对手机号、地址、身份证、订单隐私等敏感内容配置转人工或脱敏", "未开始", "不要把不必要的个人信息发送给模型"),
                NewCompliance("第三方API", "与模型/中转站供应商确认数据使用、日志保留、跨境传输、API Key安全", "未开始", "优先支持客户自填API Key"),
                NewCompliance("AI透明度", "在使用说明中告知系统会生成AI建议，自动回复由商家自行开启", "未开始", "必要时在店铺客服话术中说明"),
                NewCompliance("转人工规则", "退款、投诉、差评、赔偿、发票、法律维权等高风险问题默认不自动发送", "已内置", "可在自动回复规则中调整"),
                NewCompliance("内容安全", "禁用虚假承诺、绝对化用语、诱导交易、违法违规内容", "未开始", "配置禁用词和审核规则"),
                NewCompliance("订单售后", "订单状态、退款金额、赔偿承诺、物流异常等必须以平台后台为准", "未开始", "AI不能编造订单事实"),
                NewCompliance("日志留存", "保留必要运行日志、错误日志和授权日志，同时避免长期保存完整聊天隐私", "未开始", "设置留存周期和脱敏策略"),
                NewCompliance("安全控制", "提供总开关、自动回复开关、接口熔断、失败切换、人工接管", "部分完成", "当前已有总开关和自动回复开关"),
                NewCompliance("知识产权", "知识库内容、提示词、商品图文素材需要客户确认合法来源", "未开始", "避免复制第三方受保护内容"),
                NewCompliance("合同条款", "商业合同写明服务范围、责任限制、SLA、数据处理、退款和终止条款", "未开始", "建议律师审查"),
                NewCompliance("资质备案", "评估是否涉及生成式AI服务公开提供、算法备案、深度合成标识等监管要求", "待评估", "若只是客户本地自用辅助，风险相对较低；SaaS对外提供需重点评估"),
                NewCompliance("应急预案", "出现错误回复、投诉、封号风险时，可远程停用或指导客户一键关闭", "未开始", "商业版建议接入远程风控"),
                NewCompliance("版本管理", "记录每个客户使用的版本、配置、知识库更新时间和合规清单状态", "未开始", "便于追责和回滚")
            };
        }

        private static ComplianceItem NewCompliance(string category, string item, string status, string note)
        {
            return new ComplianceItem { Category = category, Item = item, Status = status, Owner = string.Empty, Note = note };
        }

        private static IEnumerable<string> SplitWords(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '\n', '\r', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string text, string keywords, out string hit)
        {
            text = text ?? string.Empty;
            foreach (var word in SplitWords(keywords))
            {
                if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = word;
                    return true;
                }
            }
            hit = string.Empty;
            return false;
        }

        public static AutoReplyRuleDecision EvaluateAutoReplyRule(string question)
        {
            var decision = new AutoReplyRuleDecision();
            var cfg = GetAutoReplyRules();
            if (cfg == null || !cfg.Enabled) return decision;

            string hit;
            if (ContainsAny(question, cfg.ManualKeywords, out hit))
            {
                decision.Matched = true;
                decision.HitKeyword = hit;
                decision.Reason = "命中强制转人工关键词：" + hit;
            }
            else if (ContainsAny(question, cfg.NoAutoReplyKeywords, out hit))
            {
                decision.Matched = true;
                decision.HitKeyword = hit;
                decision.Reason = "命中仅人工确认关键词：" + hit;
            }
            if (!decision.Matched) return decision;

            decision.WorkHoursText = GetWorkHoursText(cfg);
            decision.IsOffHours = cfg.EnableWorkHours && !IsHumanServiceOnline(cfg, DateTime.Now);
            if (!decision.IsOffHours)
            {
                decision.AllowAutoReply = false;
                decision.ReplyText = string.IsNullOrWhiteSpace(cfg.HandoffText)
                    ? AutoReplyRuleConfig.Default().HandoffText
                    : cfg.HandoffText.Trim();
                return decision;
            }

            decision.AllowAutoReply = true;
            decision.UseAiReply = string.Equals(
                cfg.OffHoursReplyMode,
                "AI告知下班时间",
                StringComparison.Ordinal);
            var fixedText = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? AutoReplyRuleConfig.Default().OffHoursFixedText
                : cfg.OffHoursFixedText.Trim();
            decision.ReplyText = fixedText.Replace("{工作时间}", decision.WorkHoursText);
            decision.Reason += "；当前为人工客服下班时间";
            return decision;
        }

        public static bool TryMatchManualRule(string question, out string answer, out string reason)
        {
            var decision = EvaluateAutoReplyRule(question);
            answer = decision.ReplyText ?? string.Empty;
            reason = decision.Reason ?? string.Empty;
            return decision.Matched;
        }

        public static bool IsHumanServiceOnline(AutoReplyRuleConfig cfg, DateTime now)
        {
            if (cfg == null || !cfg.EnableWorkHours) return true;
            TimeSpan start;
            TimeSpan end;
            if (!TimeSpan.TryParse(cfg.WorkStartTime, out start)) start = new TimeSpan(9, 0, 0);
            if (!TimeSpan.TryParse(cfg.WorkEndTime, out end)) end = new TimeSpan(18, 0, 0);
            var current = now.TimeOfDay;
            if (start == end) return true;
            if (start < end) return current >= start && current < end;
            return current >= start || current < end;
        }

        public static string GetWorkHoursText(AutoReplyRuleConfig cfg)
        {
            cfg = cfg ?? AutoReplyRuleConfig.Default();
            var start = string.IsNullOrWhiteSpace(cfg.WorkStartTime) ? "09:00" : cfg.WorkStartTime.Trim();
            var end = string.IsNullOrWhiteSpace(cfg.WorkEndTime) ? "18:00" : cfg.WorkEndTime.Trim();
            return "每天 " + start + "–" + end;
        }

        private static List<KnowledgeBaseEntry> MatchKnowledge(string question)
        {
            var list = GetKnowledgeBase();
            var rt = new List<KnowledgeBaseEntry>();
            foreach (var item in list)
            {
                if (item == null || !item.Enabled) continue;
                string hit;
                if (ContainsAny(question, item.Keywords + "," + item.Title, out hit)) rt.Add(item);
                if (rt.Count >= 6) break;
            }
            return rt;
        }

        public static string BuildPromptAddon(string question)
        {
            var policy = GetMessagePolicy();
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("店铺回复策略：");
            sb.AppendLine("- 语气风格：" + policy.Tone + "。");
            if (policy.OneSentence) sb.AppendLine("- 回复尽量控制为一句话，不要长篇解释。");
            if (policy.MaxAnswerLength > 0) sb.AppendLine("- 回复长度不超过 " + policy.MaxAnswerLength + " 个中文字符左右。");
            if (!string.IsNullOrWhiteSpace(policy.RequiredPrefix)) sb.AppendLine("- 必要时使用固定前缀：" + policy.RequiredPrefix);
            if (!string.IsNullOrWhiteSpace(policy.BannedWords)) sb.AppendLine("- 不要使用这些词：" + policy.BannedWords);
            sb.AppendLine("- 价格、库存、物流、订单、退款、赔偿等以平台页面和人工确认为准，不要编造。");

            if (policy.EnableKnowledgeBase)
            {
                var matches = MatchKnowledge(question);
                if (matches.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("命中的店铺知识库：");
                    foreach (var item in matches)
                    {
                        sb.AppendLine("【" + item.Category + "】" + item.Title + "：" + item.Answer);
                    }
                }
            }
            return sb.ToString();
        }

        public static string ApplyOutputPolicy(string answer)
        {
            var policy = GetMessagePolicy();
            answer = (answer ?? string.Empty).Trim();
            foreach (var word in SplitWords(policy.BannedWords))
            {
                answer = answer.Replace(word, string.Empty);
            }
            if (!string.IsNullOrWhiteSpace(policy.RequiredPrefix) && !answer.StartsWith(policy.RequiredPrefix))
            {
                answer = policy.RequiredPrefix + answer;
            }
            if (policy.MaxAnswerLength > 0 && answer.Length > policy.MaxAnswerLength)
            {
                answer = answer.Substring(0, policy.MaxAnswerLength).TrimEnd('，', '。', '；', '、', ' ') + "。";
            }
            return answer;
        }
    }

    public class ApiUsageSnapshot
    {
        public string EndpointId { get; set; }
        public string EndpointName { get; set; }
        public long TotalCalls { get; set; }
        public long TodayCalls { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long FailedCalls { get; set; }
        public long TodayFailedCalls { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastStatus { get; set; }
    }

    public class RuntimeStatsSnapshot
    {
        public long TotalReceptionCount { get; set; }
        public long TodayReceptionCount { get; set; }
        public long TotalAutoReplies { get; set; }
        public long TodayAutoReplies { get; set; }
        public long TotalAiCalls { get; set; }
        public long TodayAiCalls { get; set; }
        public long TotalAiFailedCalls { get; set; }
        public long TodayAiFailedCalls { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastError { get; set; }
        public List<ApiUsageSnapshot> ApiUsages { get; set; }
    }

    public static class BotRuntimeStats
    {
        private class ApiUsageCounter
        {
            public string EndpointId;
            public string EndpointName;
            public long TotalCalls;
            public long TodayCalls;
            public long TotalTokens;
            public long TodayTokens;
            public long FailedCalls;
            public long TodayFailedCalls;
            public long LatencyTotalMs;
            public long LatencyCount;
            public string LastStatus;
        }

        private static readonly object SyncObj = new object();
        private static readonly Dictionary<string, ApiUsageCounter> ApiCounters = new Dictionary<string, ApiUsageCounter>();
        private static DateTime Today = DateTime.Today;
        private static long totalReceptionCount;
        private static long todayReceptionCount;
        private static long totalAutoReplies;
        private static long todayAutoReplies;
        private static long totalAiCalls;
        private static long todayAiCalls;
        private static long totalAiFailedCalls;
        private static long todayAiFailedCalls;
        private static long totalTokens;
        private static long todayTokens;
        private static long latencyTotalMs;
        private static long latencyCount;
        private static string lastError = string.Empty;

        private static void EnsureToday()
        {
            if (Today == DateTime.Today) return;
            Today = DateTime.Today;
            todayReceptionCount = 0;
            todayAutoReplies = 0;
            todayAiCalls = 0;
            todayAiFailedCalls = 0;
            todayTokens = 0;
            foreach (var item in ApiCounters.Values)
            {
                item.TodayCalls = 0;
                item.TodayTokens = 0;
                item.TodayFailedCalls = 0;
            }
        }

        public static void RecordReception()
        {
            lock (SyncObj)
            {
                EnsureToday();
                totalReceptionCount++;
                todayReceptionCount++;
            }
        }

        public static void RecordDisplayedAnswer(bool autoReply)
        {
            if (!autoReply) return;
            lock (SyncObj)
            {
                EnsureToday();
                totalAutoReplies++;
                todayAutoReplies++;
            }
        }

        public static void RecordSendResult(bool success)
        {
            if (success) return;
            lock (SyncObj)
            {
                EnsureToday();
                lastError = "发送失败";
            }
        }

        public static void RecordAiCall(AiEndpointConfig endpoint, int inputTokens, int outputTokens, bool success, long latencyMs, string status)
        {
            lock (SyncObj)
            {
                EnsureToday();
                var endpointId = endpoint == null ? "default" : (endpoint.Id ?? "default");
                var endpointName = endpoint == null ? "默认接口" : (endpoint.Name ?? "默认接口");
                ApiUsageCounter counter;
                if (!ApiCounters.TryGetValue(endpointId, out counter))
                {
                    counter = new ApiUsageCounter { EndpointId = endpointId, EndpointName = endpointName, LastStatus = string.Empty };
                    ApiCounters[endpointId] = counter;
                }
                var tokens = Math.Max(0, inputTokens) + Math.Max(0, outputTokens);
                counter.EndpointName = endpointName;
                counter.TotalCalls++;
                counter.TodayCalls++;
                counter.TotalTokens += tokens;
                counter.TodayTokens += tokens;
                counter.LatencyTotalMs += Math.Max(0, latencyMs);
                counter.LatencyCount++;
                counter.LastStatus = status ?? string.Empty;
                if (!success)
                {
                    counter.FailedCalls++;
                    counter.TodayFailedCalls++;
                    lastError = status ?? string.Empty;
                }

                totalAiCalls++;
                todayAiCalls++;
                totalTokens += tokens;
                todayTokens += tokens;
                latencyTotalMs += Math.Max(0, latencyMs);
                latencyCount++;
                if (!success)
                {
                    totalAiFailedCalls++;
                    todayAiFailedCalls++;
                }
            }
        }

        public static RuntimeStatsSnapshot GetSnapshot()
        {
            lock (SyncObj)
            {
                EnsureToday();
                var apiUsages = ApiCounters.Values
                    .OrderByDescending(a => a.TodayCalls)
                    .ThenBy(a => a.EndpointName)
                    .Select(a => new ApiUsageSnapshot
                    {
                        EndpointId = a.EndpointId,
                        EndpointName = a.EndpointName,
                        TotalCalls = a.TotalCalls,
                        TodayCalls = a.TodayCalls,
                        TotalTokens = a.TotalTokens,
                        TodayTokens = a.TodayTokens,
                        FailedCalls = a.FailedCalls,
                        TodayFailedCalls = a.TodayFailedCalls,
                        AvgLatencyMs = a.LatencyCount <= 0 ? 0 : a.LatencyTotalMs / a.LatencyCount,
                        LastStatus = a.LastStatus ?? string.Empty
                    }).ToList();

                return new RuntimeStatsSnapshot
                {
                    TotalReceptionCount = totalReceptionCount,
                    TodayReceptionCount = todayReceptionCount,
                    TotalAutoReplies = totalAutoReplies,
                    TodayAutoReplies = todayAutoReplies,
                    TotalAiCalls = totalAiCalls,
                    TodayAiCalls = todayAiCalls,
                    TotalAiFailedCalls = totalAiFailedCalls,
                    TodayAiFailedCalls = todayAiFailedCalls,
                    TotalTokens = totalTokens,
                    TodayTokens = todayTokens,
                    AvgLatencyMs = latencyCount <= 0 ? 0 : latencyTotalMs / latencyCount,
                    LastError = lastError ?? string.Empty,
                    ApiUsages = apiUsages
                };
            }
        }
    }
}