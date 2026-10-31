using Bot.ChromeNs;
using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bot.Options
{
    /// <summary>
    /// Reuses the existing feature-settings implementation inside the unified settings window.
    /// The legacy Window remains available for compatibility, while its content is detached and
    /// hosted here so users no longer have to navigate through separate popup windows.
    /// </summary>
    internal sealed class FeatureSettingsOptionsControl : UserControl, IOptions
    {
        private static readonly string[] OrderTemplatePlaceholders =
        {
            "{客服}", "{买家}", "{订单号}", "{时间}", "{商品}", "{sku}",
            "{数量}", "{金额}", "{实付}", "{订单状态}", "{买家备注}", "{分段符}"
        };

        private readonly FeatureSettingsWindow _legacyWindow;
        private readonly TabControl _tabs;
        private readonly MethodInfo _saveAllMethod;
        private ComboBox _replyMode;
        private TextBox _botMessageSuffix;
        private CheckBox _firstInquiryFixedReplyEnabled;
        private TextBox _firstInquiryFixedReplyAnswer;
        private CheckBox _offHoursEnabled;
        private TextBox _offHoursStartTime;
        private TextBox _offHoursEndTime;
        private TextBox _offHoursFixedAnswer;
        private CheckBox _legacyWorkHoursEnabled;
        private TextBox _legacyWorkStartTime;
        private TextBox _legacyWorkEndTime;
        private ComboBox _legacyOffHoursMode;
        private TextBox _legacyOffHoursFixedText;
        private string _currentPage;

        public FeatureSettingsOptionsControl(string seller)
        {
            Seller = seller ?? string.Empty;
            _legacyWindow = new FeatureSettingsWindow("知识库");
            _tabs = GetPrivateField<TabControl>(_legacyWindow, "_tabs");
            _saveAllMethod = typeof(FeatureSettingsWindow).GetMethod(
                "SaveAll",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_tabs == null || _saveAllMethod == null)
            {
                throw new InvalidOperationException("无法初始化功能设置页面。请重新安装完整版本。 ");
            }

            CaptureLegacyOffHoursControls();
            RemoveMeaninglessLicensePage();
            ReplaceKnowledgePageWithEmbeddedLauncher();
            OrganizeAutoReplyRulesPage();
            RemoveLegacyOffHoursFromNotificationPage();
            OrganizeHandoffStrategyPage();
            HideLegacyTabHeaders();

            var hosted = _legacyWindow.Content as UIElement;
            if (hosted == null)
            {
                throw new InvalidOperationException("功能设置内容为空。 ");
            }
            _legacyWindow.Content = null;
            PrepareHostedLayout(hosted);
            Content = hosted;

            // The unified settings window detaches FeatureSettingsWindow.Content and never shows
            // the legacy Window itself. Therefore Loaded hooks registered on FeatureSettingsWindow
            // are not a reliable way to enhance this page. Rewrite the real embedded auto-reply
            // content here, after its final structure has been constructed.
            EnhanceEmbeddedOrderTemplateHint();
            NavigateTo("知识库");
        }

        public string Seller { get; private set; }

        public OptionEnum OptionType
        {
            get { return OptionEnum.FeatureSettings; }
        }

        public void InitUI(string seller)
        {
            Seller = seller ?? Seller;
        }

        public void NavigateTo(string pageTitle)
        {
            pageTitle = (pageTitle ?? string.Empty).Trim();
            if (pageTitle.Length == 0) return;

            // Compatibility aliases for old callers/bookmarks and the new reply-mode entry.
            if (string.Equals(pageTitle, "消息通知", StringComparison.Ordinal))
                pageTitle = "转人工策略";

            foreach (TabItem tab in _tabs.Items)
            {
                var header = Convert.ToString(tab.Header) ?? string.Empty;
                if (string.Equals(header, pageTitle, StringComparison.Ordinal)
                    || header.IndexOf(pageTitle, StringComparison.Ordinal) >= 0
                    || pageTitle.IndexOf(header, StringComparison.Ordinal) >= 0)
                {
                    _tabs.SelectedItem = tab;
                    _currentPage = header;
                    if (string.Equals(header, "自动回复规则", StringComparison.Ordinal))
                    {
                        // Idempotent: protects against future layout code recreating the legacy hint.
                        EnhanceEmbeddedOrderTemplateHint();
                    }
                    return;
                }
            }

            throw new InvalidOperationException("找不到设置页面：" + pageTitle);
        }

        public void Save(string seller)
        {
            try
            {
                SyncOffHoursToLegacyControls();
                _saveAllMethod.Invoke(_legacyWindow, null);
                var effectiveSeller = seller ?? Seller;
                if (_replyMode != null)
                {
                    ReplyModeService.Save(
                        effectiveSeller,
                        _replyMode.SelectedIndex == 1 ? BotReplyMode.LocalFirst : BotReplyMode.AiFirst);
                }
                if (_botMessageSuffix != null)
                {
                    BotMessageSuffixService.Save(effectiveSeller, _botMessageSuffix.Text ?? string.Empty);
                }
                if (_firstInquiryFixedReplyEnabled != null && _firstInquiryFixedReplyAnswer != null)
                {
                    FirstInquiryFixedReplyService.Save(
                        effectiveSeller,
                        _firstInquiryFixedReplyEnabled.IsChecked == true,
                        _firstInquiryFixedReplyAnswer.Text ?? string.Empty);
                }
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        public void RestoreDefault()
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "该页面包含知识、规则、通知和策略等业务数据，为避免误清空，暂不提供一键恢复默认。请在当前页面逐项修改后保存。",
                "保护业务数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        public void NavHelp()
        {
            var page = string.IsNullOrWhiteSpace(_currentPage) ? "当前页面" : _currentPage;
            if (string.Equals(page, "知识库", StringComparison.Ordinal))
            {
                KnowledgeCenterHelpWindow.MyShow(Window.GetWindow(this), Seller);
                return;
            }
            MessageBox.Show(
                Window.GetWindow(this),
                page + "的修改会按当前 ShopKey 独立保存。完成修改后点击设置窗口右下角的“保存设置”。",
                "设置帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CaptureLegacyOffHoursControls()
        {
            _legacyWorkHoursEnabled = GetPrivateField<CheckBox>(_legacyWindow, "_workHoursEnabled");
            _legacyWorkStartTime = GetPrivateField<TextBox>(_legacyWindow, "_workStartTime");
            _legacyWorkEndTime = GetPrivateField<TextBox>(_legacyWindow, "_workEndTime");
            _legacyOffHoursMode = GetPrivateField<ComboBox>(_legacyWindow, "_offHoursMode");
            _legacyOffHoursFixedText = GetPrivateField<TextBox>(_legacyWindow, "_offHoursFixedText");
        }

        private void RemoveMeaninglessLicensePage()
        {
            var licenseTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "账号与授权", StringComparison.Ordinal));
            if (licenseTab != null)
            {
                _tabs.Items.Remove(licenseTab);
            }

            SetPrivateField(_legacyWindow, "_licensee", null);
            SetPrivateField(_legacyWindow, "_licenseKey", null);
            SetPrivateField(_legacyWindow, "_expireDate", null);
            SetPrivateField(_legacyWindow, "_offlineAuth", null);
        }

        private void ReplaceKnowledgePageWithEmbeddedLauncher()
        {
            var knowledgeTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "知识库", StringComparison.Ordinal));
            if (knowledgeTab == null) return;

            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(new TextBlock
            {
                Text = "知识库中心",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });
            panel.Children.Add(new TextBlock
            {
                Text = "知识库使用独立管理界面，支持智能导入、搜索、编辑、分类整理和 JSON 导入导出。数据仍按当前店铺独立保存。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)),
                Margin = new Thickness(0, 10, 0, 18)
            });
            var actions = new WrapPanel();
            var button = new Button
            {
                Content = "打开知识库中心",
                Width = 160,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += delegate
            {
                KnowledgeCenterWindow.MyShow(Window.GetWindow(this));
            };
            var help = new Button
            {
                Content = "使用帮助",
                Width = 110,
                Height = 36,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(147, 172, 222)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            help.Click += delegate
            {
                KnowledgeCenterHelpWindow.MyShow(Window.GetWindow(this), Seller);
            };
            var legacyPreview = new Button
            {
                Content = "旧版知识库预览",
                Width = 150,
                Height = 36,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "只读查看当前店铺的旧版知识库，不启用旧版功能"
            };
            legacyPreview.Click += delegate
            {
                LegacyKnowledgePreviewWindow.MyShow(Window.GetWindow(this), Seller);
            };
            actions.Children.Add(button);
            actions.Children.Add(help);
            actions.Children.Add(legacyPreview);
            panel.Children.Add(actions);
            knowledgeTab.Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(8),
                Child = panel
            };
        }

        private void OrganizeAutoReplyRulesPage()
        {
            var autoReplyTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
            if (autoReplyTab == null) return;

            var legacyContent = DetachLegacyScrollHost(autoReplyTab.Content as UIElement);
            autoReplyTab.Content = null;

            var firstSettings = FirstInquiryFixedReplyService.Load(Seller)
                ?? new FirstInquiryFixedReplySettings();
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var replyMode = ReplyModeService.GetMode(Seller);

            _replyMode = new ComboBox
            {
                Height = 30,
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
                SelectedIndex = replyMode == BotReplyMode.LocalFirst ? 1 : 0
            };
            _replyMode.Items.Add("AI优先");
            _replyMode.Items.Add("本地优先");

            _botMessageSuffix = new TextBox
            {
                Text = BotMessageSuffixService.GetSuffix(Seller),
                Width = 180,
                Height = 26,
                MaxLength = BotMessageSuffixService.MaxSuffixLength,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 3, 6, 3)
            };

            _firstInquiryFixedReplyEnabled = new CheckBox
            {
                Content = "启用首条咨询固定回复",
                IsChecked = firstSettings.Enabled,
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold
            };
            _firstInquiryFixedReplyAnswer = new TextBox
            {
                Text = firstSettings.Answer ?? string.Empty,
                MinHeight = 64,
                MaxHeight = 96,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 6, 8, 6)
            };

            _offHoursEnabled = new CheckBox
            {
                Content = "启用下班自动回复",
                IsChecked = cfg != null && cfg.EnableWorkHours,
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold
            };
            _offHoursStartTime = new TextBox
            {
                Text = cfg == null || string.IsNullOrWhiteSpace(cfg.WorkStartTime) ? "09:00" : cfg.WorkStartTime,
                Width = 80,
                Height = 26
            };
            _offHoursEndTime = new TextBox
            {
                Text = cfg == null || string.IsNullOrWhiteSpace(cfg.WorkEndTime) ? "18:00" : cfg.WorkEndTime,
                Width = 80,
                Height = 26
            };
            _offHoursFixedAnswer = new TextBox
            {
                Text = cfg == null ? string.Empty : (cfg.OffHoursFixedText ?? string.Empty),
                MinHeight = 68,
                MaxHeight = 110,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 6, 8, 6)
            };

            var inserted = BuildDeterministicRuleControls();
            var legacyStack = FindPrimaryRuleStack(legacyContent);
            if (legacyStack != null)
            {
                for (var i = inserted.Count - 1; i >= 0; i--)
                {
                    legacyStack.Children.Insert(0, inserted[i]);
                }
                autoReplyTab.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    CanContentScroll = false,
                    Content = legacyContent
                };
                return;
            }

            // Compatibility fallback for an older settings layout.
            var body = new StackPanel { Margin = new Thickness(12, 8, 12, 14) };
            foreach (var control in inserted) body.Children.Add(control);
            if (legacyContent != null) body.Children.Add(legacyContent);
            autoReplyTab.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = body
            };
        }

        private List<UIElement> BuildDeterministicRuleControls()
        {
            var result = new List<UIElement>();

            result.Add(MakeLabeledControl(
                "回复模式",
                _replyMode,
                "AI优先：本地知识作为上下文，由AI生成最终答案。 本地优先：先检索本地知识库，高置信命中时直接回复且不调用AI；未达到高置信阈值时再调用AI。买家5分钟无新消息后，会把本轮买家、Bot和人工客服完整消息交给AI复盘，生成去重后的可复用问答进入本地知识库。"));
            result.Add(MakeLabeledControl(
                "Bot消息后缀",
                _botMessageSuffix,
                "每条 Bot 自动发送的文本消息末尾附加此后缀，默认 [AI]；留空表示不添加后缀。"));
            result.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 6, 0, 14)
            });

            result.Add(MakeSectionTitle("首条咨询固定回复"));
            result.Add(_firstInquiryFixedReplyEnabled);
            result.Add(MakeLabeledControl(
                "固定答案",
                _firstInquiryFixedReplyAnswer,
                "新买家或超过 10 分钟未互动后再次来询可重新触发。真实发送成功后才记录本轮已回复。"));

            result.Add(MakeSectionTitle("下班自动回复"));
            result.Add(_offHoursEnabled);

            var workRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            workRow.Children.Add(new TextBlock
            {
                Text = "工作时间",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center
            });
            workRow.Children.Add(_offHoursStartTime);
            workRow.Children.Add(new TextBlock
            {
                Text = "  至  ",
                VerticalAlignment = VerticalAlignment.Center
            });
            workRow.Children.Add(_offHoursEndTime);
            workRow.Children.Add(new TextBlock
            {
                Text = "   HH:mm；支持跨夜，例如 18:00-09:00",
                Margin = new Thickness(8, 4, 0, 0),
                Foreground = Brushes.Gray
            });
            result.Add(workRow);
            result.Add(MakeLabeledControl(
                "固定答案",
                _offHoursFixedAnswer,
                "支持 {工作时间} 占位符。下班自动回复直接本地发送，不调用也不等待 AI 接口。"));

            result.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 10, 0, 14)
            });
            return result;
        }

        private static TextBlock MakeSectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 8, 0, 10)
            };
        }

        private static UIElement MakeLabeledControl(
            string label,
            Control control,
            string hint)
        {
            var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 8, 0)
            };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(control, 1);
            row.Children.Add(labelBlock);
            row.Children.Add(control);
            wrapper.Children.Add(row);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                wrapper.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(90, 4, 0, 0)
                });
            }
            return wrapper;
        }

        private void EnhanceEmbeddedOrderTemplateHint()
        {
            try
            {
                var autoReplyTab = _tabs.Items
                    .OfType<TabItem>()
                    .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
                var root = autoReplyTab == null ? null : autoReplyTab.Content as UIElement;
                if (root == null) return;

                var hints = EnumerateElements(root)
                    .OfType<TextBlock>()
                    .Where(x => IsOrderTemplateHint(x.Text))
                    .ToList();

                // Some historical layouts used Label/ContentControl rather than a direct TextBlock.
                // Normalize those to a TextBlock so the clickable placeholder UI is deterministic.
                foreach (var contentControl in EnumerateElements(root).OfType<ContentControl>().ToList())
                {
                    var raw = contentControl.Content as string;
                    if (!IsOrderTemplateHint(raw)) continue;
                    var normalized = new TextBlock
                    {
                        Text = raw,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray
                    };
                    contentControl.Content = normalized;
                    hints.Add(normalized);
                }

                foreach (var hint in hints.Distinct())
                {
                    RewriteOrderTemplateHint(hint);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("嵌入式下单模板占位符提示修复失败：" + ex.Message, 10);
            }
        }

        private static bool IsOrderTemplateHint(string text)
        {
            text = text ?? string.Empty;
            return text.IndexOf("支持 {客服}", StringComparison.Ordinal) >= 0
                && text.IndexOf("接口失败", StringComparison.Ordinal) >= 0;
        }

        private void RewriteOrderTemplateHint(TextBlock hint)
        {
            if (hint == null || !IsOrderTemplateHint(hint.Text)) return;

            hint.Inlines.Clear();
            hint.TextWrapping = TextWrapping.Wrap;
            hint.ToolTip = "点击蓝色变量插入到“预设/兜底答案”的当前光标位置";
            hint.Inlines.Add(new Run("支持 "));
            var blue = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            for (var i = 0; i < OrderTemplatePlaceholders.Length; i++)
            {
                if (i > 0) hint.Inlines.Add(new Run("、"));
                var token = OrderTemplatePlaceholders[i];
                var link = new Hyperlink(new Run(token))
                {
                    Foreground = blue,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    TextDecorations = null,
                    ToolTip = "点击插入 " + token
                };
                link.Click += delegate { InsertOrderTemplateToken(token); };
                hint.Inlines.Add(link);
            }
            hint.Inlines.Add(new Run("。点击蓝色变量可插入到当前光标处；{分段符} 会拆成多条千牛消息依次发送；接口失败时也会使用这段话兜底。"));
        }

        private void InsertOrderTemplateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var box = FindOrderTemplateReplyTextBox();
            if (box == null)
            {
                Log.ErrorWithMaxCount("无法定位“买家下单后自动发送”的预设/兜底答案输入框，未插入占位符。", 10);
                return;
            }

            var value = box.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(box.SelectionStart, value.Length));
            var length = Math.Max(0, Math.Min(box.SelectionLength, value.Length - start));
            box.Text = value.Remove(start, length).Insert(start, token);
            box.SelectionStart = start + token.Length;
            box.SelectionLength = 0;
            box.Focus();
        }

        private TextBox FindOrderTemplateReplyTextBox()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            var configured = cfg == null ? string.Empty : (cfg.OrderPlacedReplyText ?? string.Empty);

            // Prefer the legacy window's actual private TextBox field. This remains stable even after
            // its Content is detached and hosted in WndOption.
            var fields = _legacyWindow.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var fieldBoxes = new List<KeyValuePair<string, TextBox>>();
            foreach (var field in fields)
            {
                if (!typeof(TextBox).IsAssignableFrom(field.FieldType)) continue;
                var box = field.GetValue(_legacyWindow) as TextBox;
                if (box != null) fieldBoxes.Add(new KeyValuePair<string, TextBox>(field.Name ?? string.Empty, box));
            }

            if (configured.Length > 0)
            {
                var exact = fieldBoxes.Select(x => x.Value)
                    .FirstOrDefault(x => x.AcceptsReturn && string.Equals(x.Text ?? string.Empty, configured, StringComparison.Ordinal));
                if (exact != null) return exact;
            }

            var named = fieldBoxes.FirstOrDefault(x =>
                x.Value != null
                && x.Value.AcceptsReturn
                && x.Key.IndexOf("order", StringComparison.OrdinalIgnoreCase) >= 0
                && (x.Key.IndexOf("reply", StringComparison.OrdinalIgnoreCase) >= 0
                    || x.Key.IndexOf("placed", StringComparison.OrdinalIgnoreCase) >= 0
                    || x.Key.IndexOf("answer", StringComparison.OrdinalIgnoreCase) >= 0));
            if (named.Value != null) return named.Value;

            var autoReplyTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
            var root = autoReplyTab == null ? null : autoReplyTab.Content as UIElement;
            if (root == null) return null;
            var candidates = EnumerateElements(root).OfType<TextBox>().Where(x => x.AcceptsReturn).ToList();
            if (configured.Length > 0)
            {
                var exact = candidates.FirstOrDefault(x => string.Equals(x.Text ?? string.Empty, configured, StringComparison.Ordinal));
                if (exact != null) return exact;
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static IEnumerable<DependencyObject> EnumerateElements(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;

            var panel = root as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                    foreach (var nested in EnumerateElements(child)) yield return nested;
                yield break;
            }

            var border = root as Border;
            if (border != null)
            {
                foreach (var nested in EnumerateElements(border.Child)) yield return nested;
                yield break;
            }

            var scroll = root as ScrollViewer;
            if (scroll != null)
            {
                foreach (var nested in EnumerateElements(scroll.Content as DependencyObject)) yield return nested;
                yield break;
            }

            var content = root as ContentControl;
            if (content != null && content.Content is DependencyObject)
            {
                foreach (var nested in EnumerateElements((DependencyObject)content.Content)) yield return nested;
            }
        }

        private static StackPanel FindPrimaryRuleStack(UIElement element)
        {
            if (element == null) return null;
            var stack = element as StackPanel;
            if (stack != null && stack.Children.Count >= 3) return stack;

            var border = element as Border;
            if (border != null) return FindPrimaryRuleStack(border.Child);

            var scroll = element as ScrollViewer;
            if (scroll != null) return FindPrimaryRuleStack(scroll.Content as UIElement);

            var panel = element as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                {
                    var found = FindPrimaryRuleStack(child);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void RemoveLegacyOffHoursFromNotificationPage()
        {
            var notificationTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "消息通知", StringComparison.Ordinal));
            if (notificationTab == null) return;

            foreach (var stack in EnumerateStacks(notificationTab.Content as UIElement))
            {
                var start = -1;
                var end = -1;
                for (var i = 0; i < stack.Children.Count; i++)
                {
                    var text = stack.Children[i] as TextBlock;
                    var value = text == null ? string.Empty : (text.Text ?? string.Empty).Trim();
                    if (start < 0 && value == "人工客服工作时间与下班回复")
                    {
                        start = i;
                        continue;
                    }
                    if (start >= 0 && value == "转人工通知")
                    {
                        end = i;
                        break;
                    }
                }

                if (start < 0) continue;
                if (end < 0) end = stack.Children.Count;
                while (end > start)
                {
                    stack.Children.RemoveAt(start);
                    end--;
                }
                Log.Info("设置界面已将“人工客服工作时间与下班回复”迁移为自动回复规则中的“下班自动回复”。");
                return;
            }
        }

        private void OrganizeHandoffStrategyPage()
        {
            var notificationTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x =>
                    string.Equals(Convert.ToString(x.Header), "消息通知", StringComparison.Ordinal)
                    || string.Equals(Convert.ToString(x.Header), "转人工策略", StringComparison.Ordinal));
            if (notificationTab == null) return;

            // Rename the real source tab during construction. Do not rely on a Loaded/VisualTree
            // patch because WndOption builds its navigation before this control may ever be Loaded.
            notificationTab.Header = "转人工策略";

            var autoReplyTab = _tabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(Convert.ToString(x.Header), "自动回复规则", StringComparison.Ordinal));
            if (autoReplyTab == null) return;

            var rulesEnabled = GetPrivateField<CheckBox>(_legacyWindow, "_rulesEnabled");
            var manualKeywords = GetPrivateField<TextBox>(_legacyWindow, "_manualKeywords");
            var noAutoKeywords = GetPrivateField<TextBox>(_legacyWindow, "_noAutoKeywords");
            var handoffText = GetPrivateField<TextBox>(_legacyWindow, "_handoffText");
            if (rulesEnabled == null) return;

            var moved = new List<UIElement>();
            AddDetachedContainingBlock(autoReplyTab.Content as UIElement, rulesEnabled, moved);
            AddDetachedContainingBlock(autoReplyTab.Content as UIElement, manualKeywords, moved);
            AddDetachedContainingBlock(autoReplyTab.Content as UIElement, noAutoKeywords, moved);
            AddDetachedContainingBlock(autoReplyTab.Content as UIElement, handoffText, moved);
            if (moved.Count == 0) return;

            var destination = FindPrimaryRuleStack(notificationTab.Content as UIElement);
            if (destination == null)
            {
                // A future/older legacy layout may not expose the expected StackPanel. Put the
                // moved controls in a stable host rather than silently losing them.
                var body = new StackPanel { Margin = new Thickness(12, 8, 12, 14) };
                body.Children.Add(MakeSectionTitle("转人工规则"));
                foreach (var element in moved) body.Children.Add(element);
                if (notificationTab.Content is UIElement oldContent) body.Children.Add(oldContent);
                notificationTab.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    CanContentScroll = false,
                    Content = body
                };
                Log.Info("设置界面已直接构造“转人工策略”页面并迁移转人工规则（兼容布局）。");
                return;
            }

            destination.Children.Insert(0, new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 8, 0, 14)
            });
            for (var i = moved.Count - 1; i >= 0; i--)
                destination.Children.Insert(0, moved[i]);
            destination.Children.Insert(0, MakeSectionTitle("转人工规则"));
            Log.Info("设置界面已在构造阶段将“启用转人工规则”及关键词/话术移动到“转人工策略”。");
        }

        private static void AddDetachedContainingBlock(
            UIElement root,
            DependencyObject target,
            IList<UIElement> output)
        {
            if (root == null || target == null || output == null) return;
            UIElement detached;
            if (TryDetachContainingBlock(root, target, out detached)
                && detached != null
                && !output.Contains(detached))
            {
                output.Add(detached);
            }
        }

        private static bool TryDetachContainingBlock(
            UIElement root,
            DependencyObject target,
            out UIElement detached)
        {
            detached = null;
            if (root == null || target == null) return false;

            var panel = root as Panel;
            if (panel != null)
            {
                foreach (var child in panel.Children.Cast<UIElement>().ToList())
                {
                    if (ReferenceEquals(child, target) || ContainsElement(child, target))
                    {
                        panel.Children.Remove(child);
                        detached = child;
                        return true;
                    }
                }
                foreach (var child in panel.Children.Cast<UIElement>().ToList())
                {
                    if (TryDetachContainingBlock(child, target, out detached)) return true;
                }
            }

            var border = root as Border;
            if (border != null)
                return TryDetachContainingBlock(border.Child, target, out detached);

            var scroll = root as ScrollViewer;
            if (scroll != null)
                return TryDetachContainingBlock(scroll.Content as UIElement, target, out detached);

            var content = root as ContentControl;
            if (content != null)
                return TryDetachContainingBlock(content.Content as UIElement, target, out detached);

            return false;
        }

        private static bool ContainsElement(DependencyObject root, DependencyObject target)
        {
            if (root == null || target == null) return false;
            if (ReferenceEquals(root, target)) return true;

            var panel = root as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                    if (ContainsElement(child, target)) return true;
            }

            var border = root as Border;
            if (border != null && ContainsElement(border.Child, target)) return true;

            var scroll = root as ScrollViewer;
            if (scroll != null && ContainsElement(scroll.Content as DependencyObject, target)) return true;

            var content = root as ContentControl;
            if (content != null && ContainsElement(content.Content as DependencyObject, target)) return true;

            return false;
        }

        private static IEnumerable<StackPanel> EnumerateStacks(UIElement element)
        {
            if (element == null) yield break;
            var stack = element as StackPanel;
            if (stack != null) yield return stack;

            var border = element as Border;
            if (border != null)
            {
                foreach (var child in EnumerateStacks(border.Child)) yield return child;
                yield break;
            }

            var scroll = element as ScrollViewer;
            if (scroll != null)
            {
                foreach (var child in EnumerateStacks(scroll.Content as UIElement)) yield return child;
                yield break;
            }

            var panel = element as Panel;
            if (panel == null) yield break;
            foreach (UIElement childElement in panel.Children)
            {
                foreach (var child in EnumerateStacks(childElement)) yield return child;
            }
        }

        private void SyncOffHoursToLegacyControls()
        {
            if (_legacyWorkHoursEnabled != null && _offHoursEnabled != null)
                _legacyWorkHoursEnabled.IsChecked = _offHoursEnabled.IsChecked == true;
            if (_legacyWorkStartTime != null && _offHoursStartTime != null)
                _legacyWorkStartTime.Text = _offHoursStartTime.Text ?? string.Empty;
            if (_legacyWorkEndTime != null && _offHoursEndTime != null)
                _legacyWorkEndTime.Text = _offHoursEndTime.Text ?? string.Empty;
            if (_legacyOffHoursFixedText != null && _offHoursFixedAnswer != null)
                _legacyOffHoursFixedText.Text = _offHoursFixedAnswer.Text ?? string.Empty;
            if (_legacyOffHoursMode != null)
            {
                const string fixedMode = "固定预设答案";
                if (!_legacyOffHoursMode.Items.Contains(fixedMode))
                    _legacyOffHoursMode.Items.Add(fixedMode);
                _legacyOffHoursMode.SelectedItem = fixedMode;
            }
        }

        private static UIElement DetachLegacyScrollHost(UIElement content)
        {
            if (content == null) return null;
            var scroll = content as ScrollViewer;
            if (scroll != null)
            {
                var child = scroll.Content as UIElement;
                scroll.Content = null;
                return child ?? content;
            }

            var border = content as Border;
            if (border != null)
            {
                var nested = border.Child as ScrollViewer;
                if (nested != null)
                {
                    var child = nested.Content as UIElement;
                    nested.Content = null;
                    border.Child = child;
                }
            }
            return content;
        }

        private void HideLegacyTabHeaders()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(
                ContentPresenter.ContentProperty,
                new Binding("SelectedContent")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
            var template = new ControlTemplate(typeof(TabControl));
            template.VisualTree = presenter;
            _tabs.Template = template;
            _tabs.BorderThickness = new Thickness(0);
            _tabs.Background = Brushes.Transparent;
        }

        private static void PrepareHostedLayout(UIElement hosted)
        {
            var root = hosted as DockPanel;
            if (root == null) return;
            root.Margin = new Thickness(0);

            foreach (var footer in root.Children.OfType<DockPanel>().ToList())
            {
                if (DockPanel.GetDock(footer) != Dock.Bottom) continue;
                foreach (var buttonPanel in footer.Children.OfType<StackPanel>().ToList())
                {
                    if (buttonPanel.Orientation == Orientation.Horizontal)
                    {
                        buttonPanel.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private static T GetPrivateField<T>(object target, string name) where T : class
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) field.SetValue(target, value);
        }
    }
}
