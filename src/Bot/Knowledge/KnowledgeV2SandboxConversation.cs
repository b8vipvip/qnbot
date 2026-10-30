using Bot.ChromeNs;
using Bot.ShopScope;
using Newtonsoft.Json.Linq;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// In-process, isolated simulation. This class must NEVER call Qianniu send,
    /// order publication, production first-inquiry reservation or learning APIs.
    /// It only reads policy settings, evaluates existing read-only matching
    /// functions and uses a synthetic V2 working-memory identity.
    /// </summary>
    internal sealed class KnowledgeV2SandboxConversation : UserControl
    {
        private readonly string _seller;
        private readonly StackPanel _transcript;
        private readonly ScrollViewer _scroll;
        private readonly TextBox _input;
        private readonly Dictionary<string, CheckBox> _scenarios =
            new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly CheckBox _enableAi;
        private readonly TextBox _directAnswerDraft;
        private readonly Button _saveDirect;
        private readonly List<KeyValuePair<string, string>> _conversation =
            new List<KeyValuePair<string, string>>();
        private string _lastMissQuestion = string.Empty;
        private string _lastMissAnswer = string.Empty;
        private bool _lastMissWasHighRisk;
        private bool _lastMissHadConflict;
        private bool _busy;
        private readonly TextBox _orderId;
        private readonly TextBox _orderAmount;
        private readonly TextBox _product;
        private readonly TextBlock _status;
        private readonly TextBox _diagnostics;
        private readonly Button _send;
        private string _syntheticBuyer;
        private bool _firstInquiryUsed;
        private readonly HashSet<string> _orderPreviewed = new HashSet<string>(StringComparer.Ordinal);
        private int _turns;

        internal KnowledgeV2SandboxConversation(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            _syntheticBuyer = NewBuyerIdentity();
            var root = new DockPanel { Margin = new Thickness(8) };
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock
            {
                Text = "客服对话沙盒 · 可选 AI / 人工审核知识直答",
                FontSize = 17, FontWeight = FontWeights.SemiBold
            });
            top.Children.Add(new TextBlock
            {
                Text = "默认启用全部场景；模拟聊天先执行规则与V2直答，未命中可调用店铺AI接口。"
                    + " 真实千牛发送、下单、发货、订单服务和自动学习始终禁用。"
                    + " 只有点击“审核并新增直答”并确认后才写入当前店铺知识库。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 3, 0, 6)
            });
            var choices = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            choices.Children.Add(new TextBlock { Text = "并行启用场景（默认全选）：", VerticalAlignment = VerticalAlignment.Center });
            foreach (var scene in new[]
            {
                "普通咨询 / 知识V2", "首条咨询", "商品链接", "本地短消息",
                "特定固定规则", "下班回复", "模拟下单", "模拟付款", "模拟发货"
            })
            {
                var box = new CheckBox { Content = scene, IsChecked = true,
                    Margin = new Thickness(5, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center };
                _scenarios[scene] = box;
                choices.Children.Add(box);
            }
            var all = new Button { Content = "全选", Width = 48, Margin = new Thickness(4, 0, 4, 0) };
            all.Click += delegate { foreach (var c in _scenarios.Values) c.IsChecked = true; };
            choices.Children.Add(all);
            var clear = new Button { Content = "全不选", Width = 59 };
            clear.Click += delegate { foreach (var c in _scenarios.Values) c.IsChecked = false; };
            choices.Children.Add(clear);
            top.Children.Add(choices);
            var aiOptions = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            _enableAi = new CheckBox { Content = "知识未直答时调用当前店铺 AI 接口（会产生真实 API 消耗）",
                IsChecked = true, Margin = new Thickness(0, 4, 15, 4) };
            aiOptions.Children.Add(_enableAi);
            top.Children.Add(aiOptions);
            var config = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            config.Children.Add(new TextBlock { Text = "订单与商品模拟素材：", VerticalAlignment = VerticalAlignment.Center });
            config.Children.Add(new TextBlock { Text = "模拟订单号：", VerticalAlignment = VerticalAlignment.Center });
            _orderId = new TextBox { Width = 146, Height = 28, Text = "SIM-ORDER-001", Margin = new Thickness(4, 0, 8, 0) };
            config.Children.Add(_orderId);
            config.Children.Add(new TextBlock { Text = "金额：", VerticalAlignment = VerticalAlignment.Center });
            _orderAmount = new TextBox { Width = 74, Height = 28, Text = "9.80", Margin = new Thickness(4, 0, 6, 0) };
            config.Children.Add(_orderAmount);
            top.Children.Add(config);
            var row2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            row2.Children.Add(new TextBlock { Text = "商品（仅模板预览）：", VerticalAlignment = VerticalAlignment.Center });
            _product = new TextBox { Width = 260, Height = 27, Text = "模拟商品", Margin = new Thickness(4, 0, 8, 0) };
            row2.Children.Add(_product);
            var reset = new Button { Content = "新会话", Height = 29, Width = 85 };
            reset.Click += delegate { ResetSession(); };
            row2.Children.Add(reset);
            var sample = new Button { Content = "随机读取店铺历史订单", Height = 29, MinWidth = 145,
                Margin = new Thickness(8, 0, 5, 0) };
            sample.Click += async delegate { await LoadHistoricalSampleAsync(); };
            row2.Children.Add(sample);
            var productSample = new Button { Content = "随机读取店铺商品", Height = 29, MinWidth = 130 };
            productSample.Click += async delegate { await LoadProductSampleAsync(); };
            row2.Children.Add(productSample);
            top.Children.Add(row2);
            var eventButtons = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
            foreach (var scene in new[] { "模拟下单", "模拟付款", "模拟发货" })
            {
                var item = scene;
                var button = new Button { Content = item + "事件", MinWidth = 93, Height = 27,
                    Margin = new Thickness(0, 0, 8, 0) };
                button.Click += async delegate { await RunAsync(item); };
                eventButtons.Children.Add(button);
            }
            top.Children.Add(eventButtons);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new StackPanel();
            var inputRow = new DockPanel { Margin = new Thickness(0, 7, 0, 5) };
            _send = new Button { Content = "模拟发送", Width = 100, Height = 54, Margin = new Thickness(7, 0, 0, 0) };
            _send.Click += async delegate { await RunAsync(); };
            DockPanel.SetDock(_send, Dock.Right);
            inputRow.Children.Add(_send);
            _input = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 54,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ToolTip = "输入模拟买家消息；Ctrl+Enter 触发沙盒，不会真实发送"
            };
            _input.KeyDown += async (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter
                    && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    e.Handled = true;
                    await RunAsync();
                }
            };
            inputRow.Children.Add(_input);
            bottom.Children.Add(inputRow);
            _status = new TextBlock { Text = "沙盒未开始。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(_status);
            var teaching = new DockPanel { Margin = new Thickness(0, 7, 0, 4) };
            _saveDirect = new Button { Content = "审核并新增直答", Width = 116, Height = 47,
                Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
            _saveDirect.Click += delegate { SaveDirectAnswer(); };
            DockPanel.SetDock(_saveDirect, Dock.Right);
            teaching.Children.Add(_saveDirect);
            _directAnswerDraft = new TextBox { Height = 47, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ToolTip = "只有知识未直答的提问可以在此填写确认答案；点击审核后才写入V2" };
            teaching.Children.Add(_directAnswerDraft);
            bottom.Children.Add(teaching);
            bottom.Children.Add(new TextBlock { Text = "本轮路由证据（只读）", Margin = new Thickness(0, 7, 0, 3), FontWeight = FontWeights.SemiBold });
            _diagnostics = new TextBox
            {
                Height = 105, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            bottom.Children.Add(_diagnostics);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            _transcript = new StackPanel();
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _transcript
            };
            root.Children.Add(_scroll);
            Content = root;
            AddBubble("系统", "独立沙盒会话已建立，所有订单号均为模拟数据。", "sandbox-only");
        }

        private bool Enabled(string scene)
        {
            CheckBox box;
            return _scenarios.TryGetValue(scene, out box) && box.IsChecked == true;
        }

        private string NewBuyerIdentity()
        {
            return "__knowledge_v2_sandbox__" + Guid.NewGuid().ToString("N");
        }

        private void ResetSession()
        {
            _syntheticBuyer = NewBuyerIdentity();
            _firstInquiryUsed = false;
            _orderPreviewed.Clear();
            _conversation.Clear();
            _lastMissQuestion = string.Empty;
            _lastMissAnswer = string.Empty;
            _lastMissWasHighRisk = false;
            _lastMissHadConflict = false;
            _saveDirect.IsEnabled = false;
            _directAnswerDraft.Text = string.Empty;
            _turns = 0;
            _transcript.Children.Clear();
            _diagnostics.Text = string.Empty;
            _status.Text = "已清空模拟会话；真实买家和订单数据未改变。";
            AddBubble("系统", "已重新开始模拟会话（首条咨询状态已重置）。", "sandbox-only");
        }

        private void AddBubble(string role, string message, string source)
        {
            var bot = string.Equals(role, "Bot", StringComparison.Ordinal);
            var surface = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                Margin = bot ? new Thickness(45, 4, 2, 4) : new Thickness(2, 4, 45, 4),
                Background = bot ? new SolidColorBrush(Color.FromRgb(220, 241, 254))
                    : new SolidColorBrush(Color.FromRgb(244, 246, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 224, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = bot ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 680
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm:ss") + " " + role
                    + (string.IsNullOrWhiteSpace(source) ? "" : " · " + source),
                Foreground = Brushes.DimGray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock { Text = message ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            surface.Child = panel;
            _transcript.Children.Add(surface);
            _scroll.ScrollToEnd();
        }

        private async Task RunAsync()
        {
            if (_send == null || !_send.IsEnabled) return;
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _status.Text = "未确定当前店铺，禁止跨店读取任何规则。";
                return;
            }
            var scenario = _scenario.SelectedItem as string ?? "普通咨询 / 知识V2";
            var message = (_input.Text ?? "").Trim();
            if (message.Length > 3000) { _status.Text = "模拟文本不能超过3000字。"; return; }
            if (message.Length == 0 && scenario != "模拟下单" && scenario != "模拟付款")
            {
                _status.Text = "请输入模拟买家消息。";
                return;
            }
            _send.IsEnabled = false;
            _status.Text = "正在沙盒计算，本次不会调用千牛发送...";
            AddBubble(scenario.StartsWith("模拟") ? "模拟事件" : "买家",
                message.Length == 0 ? scenario : message, scenario);
            try
            {
                var order = (_orderId.Text ?? "").Trim();
                var amount = (_orderAmount.Text ?? "").Trim();
                var item = (_product.Text ?? "").Trim();
                var currentFirst = !_firstInquiryUsed;
                // Never touch the production FirstInquiry reservation, real buyer
                // conversation state, order ledgers, or actual sending pipeline.
                var result = await Task.Run(() => Preview(scenario, message, order, amount, item, currentFirst));
                // Product links/cards never consume the first substantive
                // inquiry slot in the production coordinator.
                if (scenario != "模拟下单" && scenario != "模拟付款"
                    && scenario != "商品链接")
                {
                    _firstInquiryUsed = true;
                    _turns++;
                }
                if (result.OrderDedupKey.Length > 0) _orderPreviewed.Add(result.OrderDedupKey);
                AddBubble("Bot", result.Reply, result.Source);
                _diagnostics.Text = result.Detail;
                _status.Text = "沙盒完成（无真实发送） · " + result.Outcome;
                _input.Clear();
            }
            catch (Exception ex)
            {
                _status.Text = "模拟失败：" + ex.Message;
                _diagnostics.Text = "沙盒异常；没有发消息、下单或写入知识。";
            }
            finally { _send.IsEnabled = true; }
        }

        private sealed class PreviewResult
        {
            public string Reply = string.Empty;
            public string Source = string.Empty;
            public string Outcome = string.Empty;
            public string Detail = string.Empty;
            public string OrderDedupKey = string.Empty;
        }

        private PreviewResult Reply(string text, string source, string detail, string outcome = "预览命中")
        {
            return new PreviewResult
            {
                Reply = string.IsNullOrWhiteSpace(text) ? "（没有可用的自动回复）" : text.Trim(),
                Source = source, Detail = detail, Outcome = outcome
            };
        }

        private PreviewResult Preview(string scenario, string message, string orderId,
            string amount, string product, bool isFirst)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(_seller);
            if (shop == null) return Reply("无法确认店铺作用域，已停止预览。", "安全阻止", "shop_not_found", "未执行");
            using (ShopSettingsScope.Enter(shop))
            {
                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (scenario == "模拟下单" || scenario == "模拟付款")
                    return PreviewOrder(cfg, scenario, orderId, amount, product);
                if (scenario == "下班回复")
                    return PreviewOffHours(cfg);

                var first = FirstInquiryFixedReplyService.Load(_seller);
                if (isFirst && first != null && first.Enabled
                    && !string.IsNullOrWhiteSpace(first.Answer)
                    && (scenario == "首条咨询" || scenario == "普通咨询 / 知识V2"))
                {
                    // In production the first reply does not necessarily consume
                    // the substantive question; local V2 may still answer it.
                    var tail = ResolveKnowledge(message);
                    return Reply(first.Answer + (tail.CanDirect ? "\n\n【后续知识直答】\n" + tail.Answer : ""),
                        "首条咨询固定回复" + (tail.CanDirect ? " + Knowledge V2" : ""),
                        "首条咨询配置：已启用。首次真实买家问题可触发前置固定回复。\n"
                        + tail.Diagnostic + "\n注意：真实发送、对话合并与上下文AI回退不在沙盒执行。");
                }
                if (scenario == "首条咨询")
                    return Reply("（未触发首条咨询固定回复）", "首条咨询",
                        isFirst ? "当前店铺首条咨询固定话术未启用或答案为空。"
                            : "当前模拟会话已经出现过买家咨询，不能重复当作首条。", "未触发");

                if (scenario == "商品链接")
                {
                    var presets = BotReplyPresetService.GetEnabledPresetsForPreview(_seller);
                    var preset = presets.FirstOrDefault();
                    return preset == null
                        ? Reply("（当前店铺没有启用的商品链接话术）", "商品链接", "没有可用预设，未触发发送。", "未触发")
                        : Reply(preset.Text, BotReplyPresetService.BuildSourceLabel(preset),
                            "命中商品链接场景；展示启用预设的第一条作为确定性样例。"
                            + "真实客户端按消息身份选择话术，可能使用另一条。");
                }
                if (scenario == "本地短消息")
                {
                    string answer, phrase;
                    return LocalShortReplyService.TryResolve(_seller, message, out answer, out phrase)
                        ? Reply(answer, "本地短消息回复", "复用生产本地短语精确匹配，命中：" + phrase)
                        : Reply("（未命中本地短消息）", "本地短消息回复", "未找到符合的启用短语。", "未触发");
                }
                if (scenario == "特定固定规则")
                {
                    var match = BotFeatureStore.EvaluateAutoReplyRule(message);
                    if (match == null || !match.Matched)
                        return Reply("（没有命中启用的特定固定规则）", "自定义固定规则",
                            "复用当前店铺固定规则评估。", "未触发");
                    var text = ReadRuleReplyPreview(match);
                    return string.IsNullOrWhiteSpace(text)
                        ? Reply("检测到规则命中，但无法安全提取规则的实际答复。请在规则设置中检查。",
                            "自定义固定规则", "规则检测真实命中；没有调用生产发送或外部API。", "需检查")
                        : Reply(text, "自定义固定规则", "读取命中的规则配置答复；未执行真实发送。");
                }

                var normal = ResolveKnowledge(message);
                return Reply(normal.CanDirect ? normal.Answer
                        : "当前不符合 Knowledge V2 直答条件，真实链路会进入后续上下文/AI/人工处理。本沙盒不会伪造AI答案。",
                    normal.CanDirect ? "Knowledge V2 · 本地直答" : "Knowledge V2 · 未直答",
                    normal.Diagnostic,
                    normal.CanDirect ? "预览命中" : "需进入后续链路");
            }
        }

        private sealed class KnowledgePreview
        {
            public bool CanDirect;
            public string Answer;
            public string Diagnostic;
        }

        private KnowledgePreview ResolveKnowledge(string message)
        {
            var decision = KnowledgeEngineV2Service.Resolve(_seller, _syntheticBuyer, message);
            return new KnowledgePreview
            {
                CanDirect = decision.CanDirectReply,
                Answer = decision.Answer ?? string.Empty,
                Diagnostic = "本地知识V2决策："
                    + (decision.CanDirectReply ? "可直答" : "不可直答")
                    + "\n原因：" + (decision.Reason ?? "")
                    + "\n候选数量：" + decision.CandidateCount
                    + "\n事实冲突：" + (decision.HasConflict ? "是" : "否")
                    + "\n耗时：" + decision.TotalMs + " ms"
                    + "\nTop知识ID：" + (decision.Matches == null ? ""
                        : string.Join(", ", decision.Matches.Where(x => x != null && x.Record != null)
                            .Take(3).Select(x => x.Record.Id)))
            };
        }

        private static string ReadRuleReplyPreview(object match)
        {
            // Compatibility-only reflection: rule result shapes have changed
            // between Windows builds. Never invoke methods or write properties.
            var type = match.GetType();
            foreach (var name in new[] { "Reply", "ReplyText", "Answer", "Response", "Text", "Content" })
            {
                var prop = type.GetProperty(name);
                if (prop == null || !prop.CanRead || prop.PropertyType != typeof(string)) continue;
                var value = prop.GetValue(match, null) as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static T GetSetting<T>(object settings, string name, T fallback)
        {
            if (settings == null) return fallback;
            var property = settings.GetType().GetProperty(name);
            if (property == null || !property.CanRead) return fallback;
            var value = property.GetValue(settings, null);
            return value is T ? (T)value : fallback;
        }

        private PreviewResult PreviewOffHours(object cfg)
        {
            if (!GetSetting(cfg, "EnableWorkHours", false))
                return Reply("（未启用下班固定回复）", "下班自动回复",
                    "当前店铺未启用工作时间规则，模拟时不会绕过开关。", "未触发");
            var start = GetSetting(cfg, "WorkStartTime", "");
            var end = GetSetting(cfg, "WorkEndTime", "");
            TimeSpan s, e;
            if (!TimeSpan.TryParse(start, out s) || !TimeSpan.TryParse(end, out e))
                return Reply("（工作时间配置无效）", "下班自动回复", "拒绝猜测工作时间。", "未触发");
            var hours = s.ToString(@"hh\:mm") + "-" + e.ToString(@"hh\:mm");
            var body = GetSetting(cfg, "OffHoursFixedText", "");
            if (string.IsNullOrWhiteSpace(body))
                return Reply("（未设置下班固定话术）", "下班自动回复",
                    "模拟展示固定模板，不调用AI生成模式。", "未触发");
            return Reply(body.Replace("{工作时间}", hours), "下班自动回复",
                "强制选择“下班场景”，只预览当前配置文本；没有更改系统时间或真实营业状态。");
        }

        private PreviewResult PreviewOrder(object cfg, string scenario, string orderId, string amount, string product)
        {
            if (!GetSetting(cfg, "EnableOrderPlacedReply", false))
                return Reply("（当前店铺未启用下单自动回复）", "订单通知沙盒",
                    "未发布订单事件，也没有调用订单服务。", "未触发");
            if (string.IsNullOrWhiteSpace(orderId) || !orderId.StartsWith("SIM-", StringComparison.Ordinal))
                return Reply("（订单号必须使用 SIM- 前缀，禁止粘贴真实订单号）",
                    "订单通知沙盒", "仅接受模拟ID，未执行任何订单动作。", "未触发");
            decimal amountValue;
            if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out amountValue)
                || amountValue < 0 || amountValue > 999999)
                return Reply("（请输入合法模拟金额）", "订单通知沙盒", "金额解析失败。", "未触发");
            var key = orderId + ":" + scenario;
            if (_orderPreviewed.Contains(key))
                return Reply("（同一模拟订单事件已在本会话预览过）", "订单重复事件",
                    "仅测试台内的重复预览提示；生产持久化订单去重未执行。", "预览去重");
            var template = GetSetting(cfg, "OrderPlacedReplyText", "");
            if (string.IsNullOrWhiteSpace(template))
                return Reply("（没有配置下单自动回复模板）", "订单通知沙盒", "模板为空。", "未触发");
            var isPaid = scenario == "模拟付款";
            var text = template.Replace("{客服}", _seller)
                .Replace("{买家}", "模拟买家")
                .Replace("{订单号}", orderId)
                .Replace("{时间}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{商品}", product)
                .Replace("{sku}", "")
                .Replace("{买家备注}", "")
                .Replace("{数量}", "1")
                .Replace("{金额}", amountValue.ToString("0.00", CultureInfo.InvariantCulture))
                .Replace("{实付}", isPaid ? amountValue.ToString("0.00", CultureInfo.InvariantCulture) : "")
                .Replace("{订单状态}", isPaid ? "已付款" : "待付款")
                .Replace("{分段符}", "\n");
            var result = Reply(text, "下单自动回复 · 固定预设沙盒",
                "模拟订单事件：" + scenario + "；实际生产使用订单卡片证据、" +
                "身份校验、去重ledger、模板分段和可选接口模式。本沙盒仅展示配置固定模板，"
                + "不验证HTTP接口结果、真实下单发送或者异步通知。" +
                "\n当前配置模式：" + GetSetting(cfg, "OrderPlacedReplyMode", "固定预设答案") +
                "\n模拟订单号：" + orderId);
            result.OrderDedupKey = key;
            return result;
        }
    }
}
