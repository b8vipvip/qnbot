using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal static class StorePromptProfileUi
    {
        public static void Show(Window owner)
        {
            ShopContext shop = ShopSettingsScope.Current ?? ShopScopedUiBridge.Get(owner);
            if (shop == null)
            {
                try
                {
                    var shopKey = ActiveShopSessionRegistry.GetActiveShopKey();
                    if (!string.IsNullOrWhiteSpace(shopKey))
                        shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    if (shop == null)
                    {
                        var seller = ActiveShopSessionRegistry.GetActiveSellerNick();
                        if (!string.IsNullOrWhiteSpace(seller))
                            shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                    }
                }
                catch { shop = null; }
            }

            if (shop == null || !shop.HasStableSellerId)
            {
                MessageBox.Show(owner,
                    "当前设置窗口未绑定稳定的店铺身份。请从对应千牛店铺的 Bot 设置重新打开店铺规则中心。",
                    "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                var window = new StorePromptProfileWindow(shop) { Owner = owner };
                ShopScopedUiBridge.Attach(window, shop);
                window.ShowDialog();
            }
        }
    }

    internal sealed class StorePromptProfileWindow : Window
    {
        private readonly ShopContext _shop;
        private readonly TextBox _raw;
        private readonly TextBox _core;
        private readonly TextBox _rules;
        private readonly TextBlock _status;
        private readonly TextBlock _summary;
        private readonly Button _generate;
        private readonly Button _save;
        private CancellationTokenSource _generationCts;

        public StorePromptProfileWindow(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            _shop = shop;

            Title = "店铺规则中心 · " + (string.IsNullOrWhiteSpace(shop.DisplayName) ? shop.ShopKey : shop.DisplayName);
            Width = 940;
            Height = 760;
            MinWidth = 760;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var intro = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(242, 247, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(196, 216, 245)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = "当前仅编辑本店规则（ShopKey：" + shop.ShopKey + "）。AI会把原始资料拆成：① 每次携带的短核心规则；② 文本对话按当前场景选取的Top 3规则；③ 图片分析使用的高优先级视觉规则卡。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DimGray
                }
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _summary = new TextBlock
            {
                Text = string.Empty,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_summary, Dock.Left);
            header.Children.Add(_summary);
            _status = new TextBlock
            {
                Text = string.Empty,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_status, Dock.Right);
            header.Children.Add(_status);
            Grid.SetRow(header, 1);
            root.Children.Add(header);

            var tabs = new TabControl();
            _raw = CreateEditor();
            _core = CreateEditor();
            _rules = CreateEditor();

            tabs.Items.Add(CreateTab(
                "1. 原始店铺资料",
                "可以粘贴完整资料，长度可以较长；运行时不会把这里的原文直接发给AI。",
                _raw));
            tabs.Items.Add(CreateTab(
                "2. 核心规则",
                "只保留所有场景都必须遵守的店铺定位、保密边界、通用判断原则和回复原则，建议控制在1500字以内。",
                _core));
            tabs.Items.Add(CreateTab(
                "3. 场景规则卡",
                "JSON数组。文本回复只选最相关Top 3；视觉回复最多携带8条高优先级视觉规则。可手动修改priority、scope、triggers和content。",
                _rules));
            Grid.SetRow(tabs, 2);
            root.Children.Add(tabs);

            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var note = new TextBlock
            {
                Text = "scope：text=文字场景，vision=图片场景，both=两者；priority越高，视觉无文字线索时越优先携带。",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(note, 0);
            footer.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _generate = new Button
            {
                Content = "AI生成结构化规则",
                Width = 150,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _generate.Click += async (s, e) => await GenerateAsync();
            buttons.Children.Add(_generate);

            _save = new Button
            {
                Content = "保存",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _save.Click += (s, e) => SaveAndClose();
            buttons.Children.Add(_save);

            var close = new Button { Content = "关闭", Width = 80, Height = 30 };
            close.Click += (s, e) => Close();
            buttons.Children.Add(close);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            LoadProfile();
            Closing += (s, e) =>
            {
                if (_generationCts != null)
                {
                    try { _generationCts.Cancel(); } catch { }
                }
            };
        }

        private static TextBox CreateEditor()
        {
            return new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                Margin = new Thickness(8)
            };
        }

        private static TabItem CreateTab(string title, string help, TextBox editor)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var helpText = new TextBlock
            {
                Text = help,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(8, 8, 8, 0)
            };
            Grid.SetRow(helpText, 0);
            grid.Children.Add(helpText);
            Grid.SetRow(editor, 1);
            grid.Children.Add(editor);
            return new TabItem { Header = title, Content = grid };
        }

        private void LoadProfile()
        {
            using (ShopSettingsScope.Enter(_shop))
            {
                var profile = StorePromptProfileService.GetProfile();
                _raw.Text = profile.RawInput ?? string.Empty;
                _core.Text = !string.IsNullOrWhiteSpace(profile.CorePrompt)
                    ? profile.CorePrompt
                    : profile.StandardPrompt ?? string.Empty;
                _rules.Text = StorePromptProfileService.SerializeRules(profile.Rules);
                UpdateSummary(profile);
                if (StorePromptProfileService.NeedsStructuredMigration(profile))
                {
                    _status.Text = "检测到旧版整段提示词，请点击AI生成结构化规则";
                    _status.Foreground = Brushes.DarkOrange;
                }
                else
                {
                    _status.Text = string.IsNullOrWhiteSpace(profile.UpdatedAt)
                        ? "尚未配置"
                        : "最后更新：" + profile.UpdatedAt;
                    _status.Foreground = Brushes.DimGray;
                }
            }
        }

        private void UpdateSummary(StorePromptProfile profile)
        {
            var rules = profile == null || profile.Rules == null ? 0 : profile.Rules.Count;
            var coreLength = profile == null
                ? 0
                : (!string.IsNullOrWhiteSpace(profile.CorePrompt)
                    ? profile.CorePrompt.Length
                    : (profile.StandardPrompt ?? string.Empty).Length);
            _summary.Text = "核心规则 " + coreLength + " 字 · 场景规则 " + rules + " 条";
        }

        private async System.Threading.Tasks.Task GenerateAsync()
        {
            if (string.IsNullOrWhiteSpace(_raw.Text))
            {
                MessageBox.Show("请先填写原始店铺资料。", "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _generationCts = new CancellationTokenSource();
            _generate.IsEnabled = false;
            _save.IsEnabled = false;
            _generate.Content = "正在拆分规则...";
            _status.Text = "AI正在生成核心规则和场景规则卡";
            _status.Foreground = Brushes.DimGray;
            try
            {
                StorePromptProfile profile;
                using (ShopSettingsScope.Enter(_shop))
                {
                    profile = await StorePromptProfileService.GenerateStructuredProfileAsync(
                        _raw.Text,
                        _generationCts.Token);
                }
                _core.Text = profile.CorePrompt ?? string.Empty;
                _rules.Text = StorePromptProfileService.SerializeRules(profile.Rules);
                UpdateSummary(profile);
                _status.Text = "已生成并保存 · " + profile.UpdatedAt;
                MessageBox.Show(
                    "本店结构化规则已生成并保存。后续只会用于当前 ShopKey，并会随本店云备份/同步处理。",
                    "生成完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "生成已取消";
            }
            catch (Exception ex)
            {
                _status.Text = "生成失败";
                _status.Foreground = Brushes.Firebrick;
                MessageBox.Show("生成结构化规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_generationCts != null)
                {
                    _generationCts.Dispose();
                    _generationCts = null;
                }
                _generate.IsEnabled = true;
                _save.IsEnabled = true;
                _generate.Content = "AI生成结构化规则";
            }
        }

        private void SaveAndClose()
        {
            try
            {
                using (ShopSettingsScope.Enter(_shop))
                {
                    var rules = StorePromptProfileService.ParseRulesJson(_rules.Text);
                    StorePromptProfileService.SaveStructured(_raw.Text, _core.Text, rules);
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
