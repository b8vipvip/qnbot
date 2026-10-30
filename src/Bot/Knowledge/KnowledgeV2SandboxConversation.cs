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

        private async Task RunAsync(string forcedEvent = null)
        {
            if (_busy) return;
            var scenes = new HashSet<string>(
                _scenarios.Where(x => x.Value.IsChecked == true).Select(x => x.Key),
                StringComparer.Ordinal);
            var isOrder = !string.IsNullOrWhiteSpace(forcedEvent);
            var message = (_input.Text ?? "").Trim();
            if (isOrder && !scenes.Contains(forcedEvent))
            {
                _status.Text = "该模拟事件未勾选；如需测试请先启用对应场景。";
                return;
            }
            if (!isOrder && (message.Length == 0 || message.Length > 3000))
            {
                _status.Text = "请输入 1～3000 字的模拟买家消息。";
                return;
            }
            if (string.IsNullOrWhiteSpace(_seller))
            {
                _status.Text = "无法确定店铺，已拒绝测试。";
                return;
            }
            _busy = true;
            _send.IsEnabled = false;
            _saveDirect.IsEnabled = false;
            _status.Text = "正在模拟路由（不会发送千牛消息）...";
            var selectedOrder = (_orderId.Text ?? "").Trim();
            var amount = (_orderAmount.Text ?? "").Trim();
            var product = (_product.Text ?? "").Trim();
            var first = !_firstInquiryUsed;
            var enableAi = _enableAi.IsChecked == true;
            var history = _conversation.Skip(Math.Max(0, _conversation.Count - 12)).ToList();
            AddBubble(isOrder ? "模拟事件" : "买家", isOrder ? forcedEvent : message,
                isOrder ? "独立事件" : "模拟买家");
            try
            {
                var result = await Task.Run(() => Preview(
                    forcedEvent, scenes, message, selectedOrder, amount, product, first));
                if (!isOrder)
                {
                    _firstInquiryUsed = _firstInquiryUsed || !IsOnlyProductLink(scenes, message);
                    _turns++;
                }

                if (result.NeedsAi && enableAi && !isOrder)
                {
                    _status.Text = "正在调用当前店铺已配置的 AI 接口（只发送模拟对话文本）...";
                    var ai = await Task.Run(() => GenerateAiPreview(
                        message, history, result.Detail, result.KnowledgeCandidates));
                    result.Detail += "\nAI调用：" + ai.Detail;
                    if (ai.Success)
                    {
                        result.Reply = (string.IsNullOrWhiteSpace(result.Prefix) ? ""
                            : result.Prefix + "\n\n") + ai.Answer;
                        result.Source = "AI接口 · 模拟生成（未经人工核实）";
                        result.Outcome = "模拟AI已生成";
                        result.SuggestedAnswer = ai.Answer;
                    }
                    else
                    {
                        result.Reply = (string.IsNullOrWhiteSpace(result.Prefix) ? ""
                            : result.Prefix + "\n\n") + "AI生成失败：" + ai.Detail;
                        result.Source = "模拟AI失败";
                        result.Outcome = "AI失败";
                    }
                }
                else if (result.NeedsAi && !enableAi)
                {
                    result.Reply = (string.IsNullOrWhiteSpace(result.Prefix) ? ""
                        : result.Prefix + "\n\n")
                        + "当前未命中安全直答，AI回退已关闭。";
                }
                if (result.OrderDedupKey.Length > 0)
                    _orderPreviewed.Add(result.OrderDedupKey);

                AddBubble("Bot", result.Reply, result.Source);
                _diagnostics.Text = result.Detail;
                _status.Text = "沙盒完成（未向千牛发送） · " + result.Outcome;
                if (!isOrder)
                {
                    _conversation.Add(new KeyValuePair<string, string>("buyer", message));
                    if (!string.IsNullOrWhiteSpace(result.SuggestedAnswer))
                        _conversation.Add(new KeyValuePair<string, string>("assistant",
                            result.SuggestedAnswer));
                }
                _lastMissQuestion = result.IsKnowledgeMiss && !isOrder ? message : string.Empty;
                _lastMissAnswer = result.SuggestedAnswer;
                _lastMissWasHighRisk = result.IsHighRisk;
                _lastMissHadConflict = result.HasConflict;
                _saveDirect.IsEnabled = _lastMissQuestion.Length > 0;
                _directAnswerDraft.Text = _lastMissQuestion.Length > 0
                    ? (result.SuggestedAnswer ?? string.Empty) : string.Empty;
                if (!isOrder) _input.Clear();
            }
            catch (Exception ex)
            {
                _status.Text = "模拟失败：" + ex.Message;
                _diagnostics.Text = "沙盒异常，未发送千牛消息或订单操作。";
            }
            finally
            {
                _busy = false;
                _send.IsEnabled = true;
            }
        }

        private sealed class PreviewResult
        {
            public string Reply = string.Empty;
            public string Prefix = string.Empty;
            public string Source = string.Empty;
            public string Outcome = string.Empty;
            public string Detail = string.Empty;
            public string OrderDedupKey = string.Empty;
            public string SuggestedAnswer = string.Empty;
            public string KnowledgeCandidates = string.Empty;
            public bool NeedsAi;
            public bool IsKnowledgeMiss;
            public bool IsHighRisk;
            public bool HasConflict;
        }

        private PreviewResult Reply(string text, string source, string detail, string outcome = "预览命中")
        {
            return new PreviewResult
            {
                Reply = string.IsNullOrWhiteSpace(text) ? "（没有可用的自动回复）" : text.Trim(),
                Source = source, Detail = detail, Outcome = outcome
            };
        }

        private static bool IsOnlyProductLink(HashSet<string> scenes, string message)
        {
            return scenes.Contains("商品链接") && (message.IndexOf("taobao.com", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("tmall.com", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private PreviewResult Preview(string eventScene, HashSet<string> scenes, string message,
            string orderId, string amount, string product, bool isFirst)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(_seller);
            if (shop == null)
                return Reply("当前店铺身份无法确认，已阻止沙盒。", "安全拒绝", "shop_not_found", "未执行");
            using (ShopSettingsScope.Enter(shop))
            {
                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (!string.IsNullOrWhiteSpace(eventScene))
                    return PreviewOrder(cfg, eventScene, orderId, amount, product);

                if (scenes.Contains("下班回复"))
                {
                    var off = PreviewOffHours(cfg, false);
                    if (off != null) return off;
                }

                if (IsOnlyProductLink(scenes, message))
                {
                    var presets = BotReplyPresetService.GetEnabledPresetsForPreview(_seller);
                    var preset = presets.FirstOrDefault();
                    if (preset != null)
                        return Reply(preset.Text, BotReplyPresetService.BuildSourceLabel(preset),
                            "商品链接被识别为非实质咨询；首条咨询名额未消耗。仅预览首个启用话术，"
                            + "生产可能按消息ID选出另一条话术。");
                }

                var prefix = string.Empty;
                if (isFirst && scenes.Contains("首条咨询"))
                {
                    var firstReply = FirstInquiryFixedReplyService.Load(_seller);
                    if (firstReply != null && firstReply.Enabled
                        && !string.IsNullOrWhiteSpace(firstReply.Answer))
                        prefix = firstReply.Answer.Trim();
                }

                if (scenes.Contains("特定固定规则"))
                {
                    var manual = BotFeatureStore.EvaluateAutoReplyRule(message);
                    if (manual != null && manual.Matched)
                    {
                        var answer = ReadRuleReplyPreview(manual);
                        return Reply(prefix + (prefix.Length > 0 ? "\n\n" : "")
                            + (answer.Length > 0 ? answer : "固定规则命中，但无法读取可预览的文本答案。"),
                            "特定固定规则", "按真实店铺规则匹配；未发送。"
                            + (answer.Length == 0 ? "请在规则页面检查配置。" : ""),
                            answer.Length > 0 ? "规则命中" : "规则答案待确认");
                    }
                }

                if (scenes.Contains("本地短消息"))
                {
                    string answer, phrase;
                    if (LocalShortReplyService.TryResolve(_seller, message, out answer, out phrase)
                        && prefix.Length == 0)
                        return Reply(answer, "本地短消息回复", "命中已启用的短语：" + phrase);
                }

                if (scenes.Contains("普通咨询 / 知识V2"))
                {
                    var decision = KnowledgeEngineV2Service.Resolve(_seller, _syntheticBuyer, message);
                    var candidates = decision.Matches == null ? ""
                        : string.Join("\n", decision.Matches.Where(x => x != null && x.Record != null)
                            .Take(3).Select(x => "ID:" + x.Record.Id + " 问:" + x.Record.Title
                                + " 答:" + x.Record.Answer));
                    var trace = "V2决策：" + (decision.CanDirectReply ? "安全直答" : "不直答")
                        + "\n原因：" + decision.Reason
                        + "\n冲突：" + decision.HasConflict
                        + "\n候选数：" + decision.CandidateCount
                        + "\n耗时：" + decision.TotalMs + "ms"
                        + "\n候选摘要：\n" + candidates;
                    if (decision.CanDirectReply)
                        return Reply(prefix + (prefix.Length > 0 ? "\n\n" : "")
                            + decision.Answer, prefix.Length > 0
                            ? "首条咨询 + Knowledge V2" : "Knowledge V2", trace);
                    var miss = Reply(prefix.Length > 0 ? prefix : "知识V2未能安全直答。",
                        prefix.Length > 0 ? "首条咨询固定回复" : "Knowledge V2",
                        trace, "进入AI回退或人工审核");
                    miss.Prefix = prefix;
                    miss.NeedsAi = true;
                    miss.IsKnowledgeMiss = true;
                    miss.IsHighRisk = KnowledgeEngineV2Semantics.IsHighRisk(message);
                    miss.HasConflict = decision.HasConflict;
                    miss.KnowledgeCandidates = candidates;
                    return miss;
                }
                if (prefix.Length > 0) return Reply(prefix, "首条咨询固定回复", "已命中首条咨询预设。");
                return Reply("没有勾选匹配当前买家消息的有效场景。", "沙盒",
                    "所有选中的策略均未生成回复。", "无命中");
            }
        }

        private sealed class SandboxAiResult
        {
            public bool Success;
            public string Answer;
            public string Detail;
        }

        private SandboxAiResult GenerateAiPreview(string question,
            List<KeyValuePair<string, string>> history, string routeDetail, string candidates)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(_seller);
            if (shop == null) return new SandboxAiResult { Detail = "店铺身份不明确，已阻止AI调用。" };
            using (ShopSettingsScope.Enter(shop))
            {
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你是客服模拟沙盒中的应答助手。仅依据已经给出的知识候选和聊天上下文回复。"
                            + "不要声称任何模拟订单已真实下单、付款、发货、退款或履约；"
                            + "不得索取密码、验证码或真实订单信息。"
                            + "知识证据不足时明确提出需人工确认，不要编造政策。"
                            + "这次请求仅生成模拟文本，不要执行工具或任何外部业务操作。"
                    }
                };
                foreach (var turn in (history ?? new List<KeyValuePair<string, string>>()).Take(12))
                {
                    messages.Add(new JObject
                    {
                        ["role"] = turn.Key == "assistant" ? "assistant" : "user",
                        ["content"] = turn.Value ?? ""
                    });
                }
                messages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = "模拟买家最新问题：" + question
                        + "\n本地知识决策：" + (routeDetail ?? "").Substring(
                            0, Math.Min(1500, (routeDetail ?? "").Length))
                        + "\n已召回知识（不一定适用，需判断）："
                        + (candidates ?? "").Substring(0, Math.Min(2400, (candidates ?? "").Length))
                });
                var response = MyOpenAI.CallStructuredChat(
                    messages, 650, 0.2, 45, CancellationToken.None, true);
                return new SandboxAiResult
                {
                    Success = response != null && response.Success
                        && !string.IsNullOrWhiteSpace(response.Answer),
                    Answer = response == null ? "" : (response.Answer ?? "").Trim(),
                    Detail = response == null ? "AI接口未返回结果" : response.Success
                        ? "调用成功，耗时" + response.LatencyMs + "ms（非真实发送）"
                        : "调用失败：" + response.Error
                };
            }
        }

        private async Task LoadHistoricalSampleAsync()
        {
            if (_busy) return;
            _status.Text = "只读扫描当前店铺已有历史订单快照...";
            try
            {
                var snapshots = await Task.Run(() => OrderEventHub.ReadSandboxSamples(_seller, 150));
                if (snapshots == null || snapshots.Count == 0)
                {
                    _status.Text = "当前店铺本机的订单事件缓存无可用样本，可手工填写SIM-订单。"
                        + "不会查询生产订单HTTP，也不会跨店读取。";
                    return;
                }
                var selected = snapshots[new Random(Guid.NewGuid().GetHashCode()).Next(snapshots.Count)];
                _orderId.Text = selected.OrderId;
                _orderAmount.Text = (selected.PaidAmount ?? selected.TotalAmount ?? 0m)
                    .ToString("0.00", CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(selected.ItemTitle))
                    _product.Text = selected.ItemTitle;
                _status.Text = "已随机读取当前店铺的历史订单素材并去除真实订单号、买家身份和备注。"
                    + "订单状态仅供模拟，未更改实际订单。"
                    + " 历史状态：" + (selected.TradeStatus ?? "未记录");
            }
            catch (Exception ex) { _status.Text = "历史订单素材读取失败：" + ex.Message; }
        }

        private async Task LoadProductSampleAsync()
        {
            if (_busy) return;
            _status.Text = "正在从当前店铺的历史订单和商品知识只读选择素材...";
            try
            {
                var names = await Task.Run(() =>
                {
                    var result = OrderEventHub.ReadSandboxSamples(_seller, 150)
                        .Where(x => !string.IsNullOrWhiteSpace(x.ItemTitle))
                        .Select(x => x.ItemTitle.Trim()).ToList();
                    var records = KnowledgeEngineV2Repository.LoadAll(_seller);
                    result.AddRange(records.Where(x => x != null && x.Enabled
                            && x.Type == "product_knowledge"
                            && !string.IsNullOrWhiteSpace(x.Title))
                        .Select(x => x.Title.Trim()).Take(150));
                    return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                });
                if (names.Count == 0)
                {
                    _status.Text = "当前店铺本机暂无可读取的商品素材。不会凭空宣称商品来自生产店铺。";
                    return;
                }
                _product.Text = names[new Random(Guid.NewGuid().GetHashCode()).Next(names.Count)];
                _status.Text = "已选择店铺本机历史订单/商品知识中的一个商品名称（只读模拟）。";
            }
            catch (Exception ex) { _status.Text = "商品素材读取失败：" + ex.Message; }
        }

        private void SaveDirectAnswer()
        {
            if (_busy || string.IsNullOrWhiteSpace(_lastMissQuestion))
            {
                _status.Text = "只有最近一次知识未直答的问题才允许新增知识。";
                return;
            }
            var question = _lastMissQuestion.Trim();
            var answer = (_directAnswerDraft.Text ?? "").Trim();
            if (question.Length < 5 || question.Length > 500
                || answer.Length < 2 || answer.Length > 4000)
            {
                _status.Text = "问题需5～500字，审核答案需2～4000字。";
                return;
            }
            if (KnowledgeEngineV2Semantics.IsHighRisk(question + " " + answer)
                || _lastMissWasHighRisk || _lastMissHadConflict)
            {
                _status.Text = "此问题或答案属于敏感风险/冲突知识，不允许直接发布直答。"
                    + "请进入知识中心审核、处理冲突后再启用。";
                return;
            }
            var confirmation = MessageBox.Show(Window.GetWindow(this),
                "是否将这条人工审核后的答案新增为当前店铺 Knowledge V2 可用直答知识？"
                + "\n\n问题：" + question
                + "\n\n答案：" + answer
                + "\n\n该操作将真实写入知识库，并影响后续生产接待；不是沙盒预览。"
                + "\nAI生成的内容必须由你核实事实及适用条件。",
                "确认新增直答知识", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
            _busy = true;
            _send.IsEnabled = false;
            _saveDirect.IsEnabled = false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(_seller);
                if (shop == null) throw new InvalidOperationException("无法确认当前店铺");
                using (ShopSettingsScope.Enter(shop))
                {
                    var previous = KnowledgeEngineV2Repository.LoadAll(_seller);
                    var duplicate = previous.FirstOrDefault(x => x != null && x.Enabled
                        && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase)
                        && (KnowledgeEngineV2Semantics.TextSimilarity(x.Title, question) >= 0.78
                            || (x.Aliases ?? new List<string>()).Any(a =>
                                KnowledgeEngineV2Semantics.TextSimilarity(a, question) >= 0.85)));
                    if (duplicate != null)
                    {
                        _status.Text = "发现相近的现有知识（ID：" + duplicate.Id
                            + "），已阻止重复新增。请从知识中心编辑原记录并处理冲突。";
                        return;
                    }
                    var parsed = KnowledgeEngineV2Semantics.Parse(question, null);
                    if (parsed.ContextDependent || string.IsNullOrWhiteSpace(parsed.Subject))
                    {
                        _status.Text = "该问题缺少独立主体或依赖聊天上下文，"
                            + "不适合发布为无条件直答；请在知识中心补充适用条件。";
                        return;
                    }
                    var now = DateTime.Now;
                    var knowledge = new KnowledgeV2Record
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Type = "business_fact",
                        Title = question,
                        Answer = answer,
                        ShortAnswer = answer,
                        Aliases = new List<string> { question },
                        Entities = parsed.Entities == null
                            ? new List<string>() : parsed.Entities.ToList(),
                        Intent = parsed.Intent,
                        Subject = parsed.Subject,
                        Predicate = parsed.Predicate,
                        RiskLevel = "normal",
                        SourceType = "sandbox_manual_review",
                        SourceId = "sandbox-manual-" + Guid.NewGuid().ToString("N"),
                        Enabled = true,
                        Status = "active",
                        Authority = 0.98,
                        Confidence = 0.96,
                        CreatedAt = now,
                        UpdatedAt = now,
                        LastVerifiedAt = now
                    };
                    KnowledgeEngineV2Repository.Save(_seller, knowledge);
                    _status.Text = "已新增人工审核直答知识 ID：" + knowledge.Id
                        + "；检索索引已更新。最终是否直答仍受语义、冲突和风险规则控制。";
                    _diagnostics.Text += "\n新增V2知识ID：" + knowledge.Id
                        + "\n来源：测试台人工确认（非自动AI学习）";
                    _lastMissQuestion = string.Empty;
                    _lastMissAnswer = string.Empty;
                    AddBubble("系统", "人工审核知识已写入当前店铺知识V2（不会向真实买家发送）。",
                        "V2人工入库");
                }
            }
            catch (Exception ex)
            {
                _status.Text = "未确认知识保存成功：" + ex.Message;
            }
            finally
            {
                _busy = false;
                _send.IsEnabled = true;
                _saveDirect.IsEnabled = _lastMissQuestion.Length > 0;
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

        private PreviewResult PreviewOffHours(object cfg, bool force)
        {
            if (!GetSetting(cfg, "EnableWorkHours", false)) return null;
            var start = GetSetting(cfg, "WorkStartTime", "");
            var end = GetSetting(cfg, "WorkEndTime", "");
            TimeSpan s, e;
            if (!TimeSpan.TryParse(start, out s) || !TimeSpan.TryParse(end, out e))
                return null;
            var now = DateTime.Now.TimeOfDay;
            var within = s == e || (s < e ? now >= s && now < e : now >= s || now < e);
            if (within && !force) return null;
            var hours = s.ToString(@"hh\:mm") + "-" + e.ToString(@"hh\:mm");
            var body = GetSetting(cfg, "OffHoursFixedText", "");
            if (string.IsNullOrWhiteSpace(body))
                return null;
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
