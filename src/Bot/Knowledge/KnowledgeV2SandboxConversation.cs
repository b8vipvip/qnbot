using Bot.ChromeNs;
using BotLib;
using System.Security.Cryptography;
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
        private readonly string _sandboxSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
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
            // The conversation is the primary viewport. Optional settings,
            // knowledge teaching and diagnostics are folded away by default,
            // so they never permanently steal the height of the transcript.
            var root = new Grid { Margin = new Thickness(10, 7, 10, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // heading
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // quick actions
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // settings drawer
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = 240
            }); // primary transcript
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // compose
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // evidence drawer
            Action<UIElement, int> attach = (control, row) =>
            {
                Grid.SetRow(control, row);
                root.Children.Add(control);
            };

            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            heading.Children.Add(new TextBlock
            {
                Text = "模拟对话",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold
            });
            heading.Children.Add(new TextBlock
            {
                Text = "模拟规则与 AI 回复，不发送千牛消息或操作订单。仅人工确认新增直答时写入知识库。",
                FontSize = 11,
                Foreground = Brushes.DimGray,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            attach(heading, 0);

            // Keep the most frequently used controls immediately accessible.
            // The 9 checkboxes and order fixture editors live in a foldable drawer.
            var actions = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            _enableAi = new CheckBox
            {
                Content = "未直答时调用 AI（消耗额度）",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 15, 2),
                ToolTip = "仅调用当前店铺 AI 生成模拟答案，不会给真实买家发送消息"
            };
            _enableAi.Checked += delegate { TraceSandbox("ai_toggle", "on", null, null, string.Empty); };
            _enableAi.Unchecked += delegate { TraceSandbox("ai_toggle", "off", null, null, string.Empty); };
            actions.Children.Add(_enableAi);
            var reset = new Button
            {
                Content = "新会话",
                Height = 28,
                MinWidth = 78,
                Margin = new Thickness(0, 0, 12, 0)
            };
            reset.Click += delegate { ResetSession(); };
            actions.Children.Add(reset);
            foreach (var scene in new[] { "模拟下单", "模拟付款", "模拟发货" })
            {
                var item = scene;
                var button = new Button
                {
                    Content = item + "事件",
                    MinWidth = 99,
                    Height = 28,
                    Margin = new Thickness(0, 0, 7, 0)
                };
                button.Click += async delegate { await RunAsync(item); };
                actions.Children.Add(button);
            }
            attach(actions, 1);

            var settings = new Expander
            {
                Header = "场景与订单/商品素材设置（9项默认全选，点击展开）",
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var settingsBody = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
            var choices = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            choices.Children.Add(new TextBlock
            {
                Text = "启用场景：",
                VerticalAlignment = VerticalAlignment.Center
            });
            foreach (var scene in new[]
            {
                "普通咨询 / 知识V2", "首条咨询", "商品链接", "本地短消息",
                "特定固定规则", "下班回复", "模拟下单", "模拟付款", "模拟发货"
            })
            {
                var box = new CheckBox
                {
                    Content = scene,
                    IsChecked = true,
                    Margin = new Thickness(4, 3, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _scenarios[scene] = box;
                var key = scene;
                box.Checked += delegate { TraceSandbox("scene_change", "checked", null, null, "scene=" + key); };
                box.Unchecked += delegate { TraceSandbox("scene_change", "unchecked", null, null, "scene=" + key); };
                choices.Children.Add(box);
            }
            var all = new Button
            {
                Content = "全选",
                Width = 48,
                Height = 25,
                Margin = new Thickness(5, 0, 4, 0)
            };
            all.Click += delegate
            {
                foreach (var box in _scenarios.Values) box.IsChecked = true;
                TraceSandbox("scene_select_all", "success", null, null, string.Empty);
            };
            choices.Children.Add(all);
            var clear = new Button { Content = "全不选", Width = 59, Height = 25 };
            clear.Click += delegate
            {
                foreach (var box in _scenarios.Values) box.IsChecked = false;
                TraceSandbox("scene_clear_all", "success", null, null, string.Empty);
            };
            choices.Children.Add(clear);
            settingsBody.Children.Add(choices);

            var orderFields = new WrapPanel { Margin = new Thickness(0, 1, 0, 5) };
            orderFields.Children.Add(new TextBlock
            {
                Text = "模拟订单号",
                VerticalAlignment = VerticalAlignment.Center
            });
            _orderId = new TextBox
            {
                Width = 145,
                Height = 27,
                Text = "SIM-ORDER-001",
                Margin = new Thickness(6, 0, 12, 0)
            };
            orderFields.Children.Add(_orderId);
            orderFields.Children.Add(new TextBlock
            {
                Text = "金额",
                VerticalAlignment = VerticalAlignment.Center
            });
            _orderAmount = new TextBox
            {
                Width = 72,
                Height = 27,
                Text = "9.80",
                Margin = new Thickness(6, 0, 12, 0)
            };
            orderFields.Children.Add(_orderAmount);
            orderFields.Children.Add(new TextBlock
            {
                Text = "商品",
                VerticalAlignment = VerticalAlignment.Center
            });
            _product = new TextBox
            {
                Width = 270,
                Height = 27,
                Text = "模拟商品（未读取真实素材）",
                Margin = new Thickness(6, 0, 0, 0)
            };
            orderFields.Children.Add(_product);
            settingsBody.Children.Add(orderFields);

            var sampleActions = new WrapPanel();
            var sample = new Button
            {
                Content = "随机读取店铺历史订单",
                Height = 28,
                MinWidth = 153,
                Margin = new Thickness(0, 0, 8, 0)
            };
            sample.Click += async delegate { await LoadHistoricalSampleAsync(); };
            sampleActions.Children.Add(sample);
            var productSample = new Button
            {
                Content = "随机读取店铺商品",
                Height = 28,
                MinWidth = 135
            };
            productSample.Click += async delegate { await LoadProductSampleAsync(); };
            sampleActions.Children.Add(productSample);
            settingsBody.Children.Add(sampleActions);
            settings.Content = settingsBody;
            settings.Expanded += delegate { TraceSandbox("settings_expand", "success", null, null, string.Empty); };
            settings.Collapsed += delegate { TraceSandbox("settings_collapse", "success", null, null, string.Empty); };
            attach(settings, 2);

            _transcript = new StackPanel { Margin = new Thickness(5, 6, 5, 6) };
            _scroll = new ScrollViewer
            {
                Content = _transcript,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false
            };
            var chatFrame = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(215, 223, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 7),
                Child = _scroll
            };
            attach(chatFrame, 3);

            var inputRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            _send = new Button
            {
                Content = "模拟发送",
                Width = 104,
                Height = 66,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _send.Click += async delegate { await RunAsync(); };
            DockPanel.SetDock(_send, Dock.Right);
            inputRow.Children.Add(_send);
            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 66,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ToolTip = "输入模拟买家消息，Ctrl+Enter 模拟发送，不会真正联系买家"
            };
            _input.KeyDown += async (sender, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter
                    && (System.Windows.Input.Keyboard.Modifiers
                        & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    e.Handled = true;
                    await RunAsync();
                }
            };
            inputRow.Children.Add(_input);
            attach(inputRow, 4);

            _status = new TextBlock
            {
                Text = "沙盒未开始。",
                Foreground = Brushes.DimGray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(1, 0, 0, 4)
            };
            attach(_status, 5);

            var evidence = new Expander
            {
                Header = "本轮路由证据（只读，点击展开）",
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 1)
            };
            _diagnostics = new TextBox
            {
                Height = 125,
                Margin = new Thickness(7, 5, 7, 7),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            evidence.Content = _diagnostics;
            evidence.Expanded += delegate { TraceSandbox("evidence_expand", "success", null, null, string.Empty); };
            evidence.Collapsed += delegate { TraceSandbox("evidence_collapse", "success", null, null, string.Empty); };
            attach(evidence, 6);

            Content = root;
            AddBubble("系统", "独立模拟会话已建立，所有订单号均为模拟数据。", "sandbox-only");
            TraceSandbox("session_open", "success", null, null, string.Empty);
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
            _turns = 0;
            _transcript.Children.Clear();
            _diagnostics.Text = string.Empty;
            _status.Text = "已清空模拟会话；真实买家和订单数据未改变。";
            AddBubble("系统", "已重新开始模拟会话（首条咨询状态已重置）。", "sandbox-only");
            TraceSandbox("new_session", "success", null, null, "context_reset");
        }

        private void AddBubble(string role, string message, string source,
            bool isKnowledgeMiss = false, string originalQuestion = null, string suggestedAnswer = null,
            bool isHighRisk = false, bool hasConflict = false)
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
            if (bot && !string.IsNullOrWhiteSpace(originalQuestion))
            {
                var question = originalQuestion;
                var proposedAnswer = suggestedAnswer ?? string.Empty;
                var risk = isHighRisk;
                var conflict = hasConflict;
                var menu = new ContextMenu();
                if (isKnowledgeMiss)
                {
                    var create = new MenuItem { Header = "新增" };
                    create.Click += delegate
                    {
                        TraceSandbox("direct_menu_open", "requested", question, null, "right_click");
                        ShowDirectAnswerDialog(question, proposedAnswer, risk, conflict);
                    };
                    menu.Items.Add(create);
                }
                var modify = new MenuItem { Header = "修改答案" };
                modify.Click += delegate
                {
                    TraceSandbox("direct_edit_open", "requested", question, null, "right_click");
                    ShowDirectAnswerDialog(question,
                        string.IsNullOrWhiteSpace(proposedAnswer) ? message : proposedAnswer,
                        risk, conflict);
                };
                menu.Items.Add(modify);
                surface.ContextMenu = menu;
                surface.ToolTip = isKnowledgeMiss ? "当前未命中安全直答 · 右键新增/修改答案"
                    : "右键修改答案后立即成为已批准直答";
            }
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
            _status.Text = "正在模拟路由（不会发送千牛消息）...";
            TraceSandbox("request_start", "running", message, null, isOrder ? forcedEvent : "chat");
            if (isOrder && string.Equals((_orderId.Text ?? "").Trim(), "SIM-ORDER-001", StringComparison.Ordinal))
                await LoadHistoricalSampleAsync(true); // Reuse a detached sample for this simulated order.
            if ((isOrder || IsOnlyProductLink(scenes, message))
                && (_product.Text ?? "").Contains("未读取真实素材"))
                await LoadProductSampleAsync(true);
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
                    TraceSandbox("ai_start", "running", message, null, "fallback");
                    var ai = await Task.Run(() => GenerateAiPreview(
                        message, history, result.Detail, result.KnowledgeCandidates));
                    result.Detail += "\nAI调用：" + ai.Detail;
                    TraceSandbox("ai_finish", ai.Success ? "success" : "error", message, null,
                        "replyLength=" + (ai.Answer ?? "").Length);
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

                AddBubble("Bot", result.Reply, result.Source,
                    result.IsKnowledgeMiss, isOrder ? null : message, result.SuggestedAnswer,
                    result.IsHighRisk, result.HasConflict);
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
                TraceSandbox("request_finish", result.IsKnowledgeMiss ? "not_direct" : "success",
                    message, null, "source=" + SafeKind(result.Source) + ";mode="
                        + SafeKind(KnowledgeEngineV2Service.GetMode(_seller))
                        + ";candidates=" + SafeCount(result.KnowledgeCandidates));
                if (!isOrder) _input.Clear();
            }
            catch (Exception ex)
            {
                _status.Text = "模拟失败：" + ex.Message;
                TraceSandbox("request_failed", "error", message, null, "exception=" + ex.GetType().Name);
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
                            + "生产可能按消息ID选出另一条话术。"
                            + " 当前店铺只读商品素材：" + product);
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
                    var decision = KnowledgeEngineV2Service.ResolveSandbox(_seller, _syntheticBuyer, message);
                    var candidates = decision.Matches == null ? ""
                        : string.Join("\n", decision.Matches.Where(x => x != null && x.Record != null)
                            .Take(3).Select(x => "ID:" + x.Record.Id + " 问:" + x.Record.Title
                                + " 答:" + x.Record.Answer));
                    var settings = KnowledgeEngineV2Service.GetSettingsView(_seller);
                    var trace = "V2直答判断：" + (decision.CanDirectReply ? "已通过" : "未通过")
                        + "\n运行方式：直接生效；不再使用影子观察模式"
                        + "\n原因：" + decision.Reason
                        + "\n冲突：" + decision.HasConflict
                        + "\n候选数：" + decision.CandidateCount
                        + "\n耗时：" + decision.TotalMs + "ms"
                        + "\n候选摘要：\n" + candidates;
                    if (decision.CanDirectReply)
                        return Reply(prefix + (prefix.Length > 0 ? "\n\n" : "")
                            + decision.Answer, prefix.Length > 0
                            ? "首条咨询 + Knowledge V2 · 沙盒" : "Knowledge V2 · 沙盒", trace);
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

        private async Task LoadHistoricalSampleAsync(bool internalCall = false)
        {
            if (_busy && !internalCall) return;
            _status.Text = "只读扫描当前店铺已有历史订单快照...";
            TraceSandbox("order_sample_start", "running", null, null, "shop_only");
            try
            {
                var snapshots = await Task.Run(() => OrderEventHub.ReadSandboxSamples(_seller, 150));
                if (snapshots == null || snapshots.Count == 0)
                {
                    TraceSandbox("order_sample_finish", "no_data", null, null, string.Empty);
                    _status.Text = "当前店铺本机的订单事件缓存无可用样本，可手工填写SIM-订单。"
                        + "不会查询生产订单HTTP，也不会跨店读取。";
                    return;
                }
                var selected = snapshots[new Random(Guid.NewGuid().GetHashCode()).Next(snapshots.Count)];
                TraceSandbox("order_sample_finish", "success", null, null, "samples=" + snapshots.Count);
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

        private async Task LoadProductSampleAsync(bool internalCall = false)
        {
            if (_busy && !internalCall) return;
            _status.Text = "正在从当前店铺的历史订单和商品知识只读选择素材...";
            TraceSandbox("product_sample_start", "running", null, null, "shop_only");
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
                    TraceSandbox("product_sample_finish", "no_data", null, null, string.Empty);
                    _status.Text = "当前店铺本机暂无可读取的商品素材。不会凭空宣称商品来自生产店铺。";
                    return;
                }
                _product.Text = names[new Random(Guid.NewGuid().GetHashCode()).Next(names.Count)];
                TraceSandbox("product_sample_finish", "success", null, null, "samples=" + names.Count);
                _status.Text = "已选择店铺本机历史订单/商品知识中的一个商品名称（只读模拟）。";
            }
            catch (Exception ex) { _status.Text = "商品素材读取失败：" + ex.Message; }
        }

        private void ShowDirectAnswerDialog(string question, string suggestion,
            bool hasRisk, bool hadConflict)
        {
            if (string.IsNullOrWhiteSpace(question)) return;
            var owner = Window.GetWindow(this);
            var win = new Window
            {
                Title = "新增知识 V2 直答",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 680,
                Height = 380,
                MinWidth = 530,
                MinHeight = 300,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            var grid = new Grid { Margin = new Thickness(14) };
            foreach (var length in new[] { GridLength.Auto, GridLength.Auto,
                new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                grid.RowDefinitions.Add(new RowDefinition { Height = length });
            var lead = new TextBlock
            {
                Text = "当前未命中安全直答 · 请核实事实后添加；保存后影响本店生产知识库。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 9)
            };
            Grid.SetRow(lead, 0);
            grid.Children.Add(lead);
            var prompt = new TextBox
            {
                Text = question,
                IsReadOnly = false,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 35,
                MaxHeight = 65,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "可修改适用问题。图片占位符或省略问法需补全业务主体，人工审核精确匹配优先。"
            };
            Grid.SetRow(prompt, 1);
            grid.Children.Add(prompt);
            var draft = new TextBox
            {
                Text = suggestion ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 90,
                ToolTip = "填写或修改待人工审核的直答答案"
            };
            Grid.SetRow(draft, 2);
            grid.Children.Add(draft);
            var hint = new TextBlock
            {
                Text = "保存前检查重复知识、语义条件、风险及店铺作用域；不会发送模拟消息给真实买家。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 8, 0, 7)
            };
            Grid.SetRow(hint, 3);
            grid.Children.Add(hint);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", Width = 80, Height = 31,
                Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += delegate { win.Close(); };
            buttons.Children.Add(cancel);
            var save = new Button { Content = "保存直答", Width = 98, Height = 31 };
            save.Click += delegate
            {
                if (SaveDirectAnswer(prompt.Text, draft.Text, hasRisk, hadConflict))
                {
                    win.DialogResult = true;
                    win.Close();
                }
            };
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 4);
            grid.Children.Add(buttons);
            win.Content = grid;
            win.ShowDialog();
        }

        private bool SaveDirectAnswer(string question, string rawAnswer,
            bool riskFromDecision, bool conflictFromDecision)
        {
            if (_busy) return false;
            question = (question ?? string.Empty).Trim();
            var answer = (rawAnswer ?? string.Empty).Trim();
            TraceSandbox("direct_save_attempt", "requested", question, null,
                "answerLength=" + answer.Length);
            if (riskFromDecision || conflictFromDecision
                || KnowledgeEngineV2Semantics.IsHighRisk(question)
                || KnowledgeEngineV2Semantics.IsHighRisk(answer))
            {
                _status.Text = "存在高风险或未解决的知识冲突，请在知识中心处理；不能自动直答。";
                TraceSandbox("direct_save_rejected", "risk_or_conflict", question, null, string.Empty);
                MessageBox.Show(_status.Text, "修改直答", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                "确认保存此人工审核答案并立即用于当前店铺的后续直答？"
                + "\n\n问题：" + question + "\n\n答案：" + answer
                + "\n\n生产直答不再等待影子观察；不会向当前模拟买家实际发送消息。",
                "确认即时直答", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
            {
                TraceSandbox("direct_save_cancelled", "cancelled", question, null, string.Empty);
                return false;
            }
            _busy = true;
            _send.IsEnabled = false;
            try
            {
                string status;
                var saved = KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(
                    _seller, question, answer, _sandboxSessionId, out status);
                _status.Text = status;
                if (!saved)
                {
                    TraceSandbox("direct_save_rejected", "repository_rejected", question, null,
                        "answerLength=" + answer.Length);
                    MessageBox.Show(status, "即时直答", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                var decision = KnowledgeEngineV2Service.Resolve(
                    _seller, NewBuyerIdentity(), question);
                _diagnostics.Text += "\n人工确认直答已即时写库；同问法可直答="
                    + decision.CanDirectReply + "；原因：" + decision.Reason;
                TraceSandbox("direct_save_success", "success", question, null,
                    "liveDirect=" + decision.CanDirectReply);
                AddBubble("系统", "人工审核答案已立即写入知识 V2。"
                    + (decision.CanDirectReply ? "相同完整问题已通过本地直答判断。"
                        : "当前问法受安全或知识冲突规则拦截，请查看路由证据。"),
                    "即时直答");
                return true;
            }
            catch (Exception ex)
            {
                _status.Text = "保存直答失败：" + ex.Message;
                TraceSandbox("direct_save_failed", "error", question, null,
                    "exception=" + ex.GetType().Name);
                MessageBox.Show(_status.Text, "即时直答", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _busy = false;
                _send.IsEnabled = true;
            }
        }

        private static string SafeKind(string value)
        {
            var text = (value ?? string.Empty).ToLowerInvariant();
            if (text.Contains("knowledge")) return "knowledge_v2";
            if (text.Contains("ai")) return "ai";
            if (text.Contains("首条")) return "first_inquiry";
            if (text.Contains("订单") || text.Contains("付款") || text.Contains("发货")) return "order";
            if (text.Contains("固定") || text.Contains("短消息")) return "rule";
            if (text == "shadow" || text == "production") return text;
            return "other";
        }

        private static int SafeCount(string candidates)
        {
            return string.IsNullOrWhiteSpace(candidates) ? 0
                : Math.Min(5, candidates.Split(new[] { '\n' }).Length);
        }

        private static string HashTrace(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
        }

        // Privacy-safe event stream in the standard exported runtime log.
        // The question, AI result, buyer identity and order payload never appear.
        private void TraceSandbox(string stage, string outcome, string question, string knowledgeId, string note)
        {
            try
            {
                Log.Info("[KnowledgeV2Sandbox] session=" + _sandboxSessionId
                    + ";turn=" + _turns + ";stage=" + stage + ";result=" + outcome
                    + ";sellerHash=" + HashTrace(_seller)
                    + ";questionHash=" + HashTrace(question)
                    + ";recordHash=" + HashTrace(knowledgeId)
                    + ";note=" + (note ?? string.Empty));
            }
            catch { /* Logging must never change simulator behavior. */ }
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
            var isShipped = scenario == "模拟发货";
            var isPaid = scenario == "模拟付款" || isShipped;
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
                .Replace("{订单状态}", isShipped ? "已发货" : isPaid ? "已付款" : "待付款")
                .Replace("{分段符}", "\n");
            var result = Reply(isShipped
                ? "【发货状态模拟】订单已发货（仅沙盒事件展示；当前生产下单回复规则不保证因发货触发发送）。\n\n"
                    + text : text,
                isShipped ? "发货状态预览 · 非真实发货通知" : "下单自动回复 · 固定预设沙盒",
                "模拟订单事件：" + scenario + (isShipped
                    ? "；生产版没有在本处验证发货事件触发，仅做状态与模板模拟。" : "；") + "实际生产使用订单卡片证据、" +
                "身份校验、去重ledger、模板分段和可选接口模式。本沙盒仅展示配置固定模板，"
                + "不验证HTTP接口结果、真实下单发送或者异步通知。" +
                "\n当前配置模式：" + GetSetting(cfg, "OrderPlacedReplyMode", "固定预设答案") +
                "\n模拟订单号：" + orderId);
            result.OrderDedupKey = key;
            return result;
        }
    }
}
