using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib.Db.Sqlite;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    internal sealed class AutoDeliveryConfig
    {
        public bool Enabled { get; set; }
        public int DelayMinutes { get; set; }
    }

    internal static class AutoDeliverySettings
    {
        private const string EnabledKey = "auto_delivery.enabled";
        private const string DelayKey = "auto_delivery.delay_minutes";
        private const string LegacyScope = "feature";
        private const string LegacyEnabledKey = "EnableVirtualGoodsAutoDelivery";
        private const string LegacyDelayKey = "VirtualGoodsAutoDeliveryDelayMinutes";
        internal const int DefaultDelayMinutes = 10;
        internal const int MinDelayMinutes = 0;
        internal const int MaxDelayMinutes = 1440;

        public static AutoDeliveryConfig Load(string seller)
        {
            var result = new AutoDeliveryConfig
            {
                Enabled = false,
                DelayMinutes = DefaultDelayMinutes
            };

            try
            {
                var store = TryCreateShopStore(seller);
                if (store != null)
                {
                    string enabled;
                    string delay;
                    if (store.TryGetString(EnabledKey, out enabled))
                        result.Enabled = ParseBool(enabled, false);
                    if (store.TryGetString(DelayKey, out delay))
                        result.DelayMinutes = ParseDelay(delay, DefaultDelayMinutes);
                    return result;
                }
            }
            catch
            {
            }

            var legacyEnabled = PersistentParams.GetParam2Key(LegacyEnabledKey, LegacyScope, "0");
            var legacyDelay = PersistentParams.GetParam2Key(
                LegacyDelayKey,
                LegacyScope,
                DefaultDelayMinutes.ToString());
            result.Enabled = ParseBool(legacyEnabled, false);
            result.DelayMinutes = ParseDelay(legacyDelay, DefaultDelayMinutes);
            return result;
        }

        public static void Save(string seller, bool enabled, int delayMinutes)
        {
            delayMinutes = Clamp(delayMinutes);
            var store = TryCreateShopStore(seller);
            if (store != null)
            {
                store.SetString(EnabledKey, enabled ? "1" : "0");
                store.SetString(DelayKey, delayMinutes.ToString());
            }
            else
            {
                PersistentParams.TrySaveParam2Key(LegacyEnabledKey, LegacyScope, enabled ? "1" : "0");
                PersistentParams.TrySaveParam2Key(LegacyDelayKey, LegacyScope, delayMinutes.ToString());
            }
        }

        private static ShopScopedSettingsStore TryCreateShopStore(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return null;
            try
            {
                var shop = ShopContextLocator.ResolveBySellerNick(seller);
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return null;
                return new ShopScopedSettingsStore(shop, new ShopScopedPathProvider());
            }
            catch
            {
                return null;
            }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            value = (value ?? string.Empty).Trim();
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static int ParseDelay(string value, int fallback)
        {
            int parsed;
            if (!int.TryParse((value ?? string.Empty).Trim(), out parsed)) parsed = fallback;
            return Clamp(parsed);
        }

        internal static int Clamp(int value)
        {
            return Math.Max(MinDelayMinutes, Math.Min(MaxDelayMinutes, value));
        }
    }

    internal sealed class AutoDeliveryOptionsControl : UserControl, IOptions
    {
        private readonly string _seller;
        private readonly CheckBox _enabled;
        private readonly TextBox _delayMinutes;
        private readonly TextBlock _summary;

        public AutoDeliveryOptionsControl(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text = "虚拟商品自动发货",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "仅用于确认不需要物流的虚拟商品。达到设定延迟后，Bot 会在当前买家的“近3个月订单”中严格匹配订单号和“待发货”状态，再执行：发货 → 无需物流 → 确认发货。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(83, 94, 113)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            var warning = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(245, 166, 35)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(255, 249, 235)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16)
            };
            warning.Child = new TextBlock
            {
                Text = "注意：这是会真实改变订单状态的操作。实物商品、需要快递单号或需要人工核验后再发货的商品，请不要启用。程序不会自动选择“在线寄件”或“自己联系”。",
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(warning);

            _enabled = new CheckBox
            {
                Content = "启用自动发货（无需物流）",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            };
            _enabled.Checked += delegate { UpdateEnabledState(); };
            _enabled.Unchecked += delegate { UpdateEnabledState(); };
            root.Children.Add(_enabled);

            var delayRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            delayRow.Children.Add(new TextBlock
            {
                Text = "新订单在",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _delayMinutes = new TextBox
            {
                Width = 72,
                Height = 28,
                Padding = new Thickness(6, 3, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            delayRow.Children.Add(_delayMinutes);
            delayRow.Children.Add(new TextBlock
            {
                Text = "分钟后尝试自动发货",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            root.Children.Add(delayRow);

            root.Children.Add(new TextBlock
            {
                Text = "允许 0–1440 分钟；0 表示订单已进入“待发货”后尽快执行。若买家尚未付款、订单面板未加载、客服正在输入或当前有其它 Bot 任务，程序会延后重试，不会强行点击。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 18)
            });

            var flow = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 224, 233)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            flow.Child = new TextBlock
            {
                Text = "安全确认链：准确订单号 + 待发货 → 点击该订单“发货” → 选择并核验“无需物流”已选中 → 写入持久防重屏障 → 点击“确认发货” → 再次核验明确显示“已发货 / 交易成功 / 已完成”。任一步无法唯一确认都会停止，不会猜测点击，也不会重复确认。",
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(flow);

            _summary = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(62, 86, 120))
            };
            root.Children.Add(_summary);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root
            };
            InitUI(_seller);
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.AutoDelivery; }
        }

        public void Save(string seller)
        {
            int delay;
            if (!int.TryParse((_delayMinutes.Text ?? string.Empty).Trim(), out delay)
                || delay < AutoDeliverySettings.MinDelayMinutes
                || delay > AutoDeliverySettings.MaxDelayMinutes)
            {
                throw new InvalidOperationException("自动发货延迟必须是 0–1440 之间的整数分钟。");
            }

            var targetSeller = string.IsNullOrWhiteSpace(seller) ? _seller : seller.Trim();
            var enabled = _enabled.IsChecked == true;

            // Enabling is ordered cursor-first, setting-second. That closes the tiny race where an
            // order could arrive after the setting became visible to the seed timer but before the
            // non-retroactive cursor was advanced, causing the genuinely new order to be skipped.
            // Disabling is ordered setting-first so no new irreversible task can enter after the
            // operator has asked the feature to stop.
            if (enabled)
            {
                AutoDeliveryOrderEventSeed.MarkConfiguration(targetSeller, true);
                AutoDeliverySettings.Save(targetSeller, true, delay);
            }
            else
            {
                AutoDeliverySettings.Save(targetSeller, false, delay);
                AutoDeliveryOrderEventSeed.MarkConfiguration(targetSeller, false);
            }

            AutoDeliveryCoordinator.ReconfigureSeller(targetSeller, enabled, delay);
            _delayMinutes.Text = delay.ToString();
            UpdateSummary(enabled, delay);
        }

        public void RestoreDefault()
        {
            _enabled.IsChecked = false;
            _delayMinutes.Text = AutoDeliverySettings.DefaultDelayMinutes.ToString();
            UpdateEnabledState();
            UpdateSummary(false, AutoDeliverySettings.DefaultDelayMinutes);
        }

        public void NavHelp()
        {
            MessageBox.Show(
                "自动发货只面向无需物流的虚拟商品。\n\n"
                + "程序只会处理能同时确认以下条件的订单：\n"
                + "1. 当前会话买家与新订单买家一致；\n"
                + "2. 右侧订单卡片包含完全一致的订单号；\n"
                + "3. 该订单明确显示“待发货”；\n"
                + "4. 发货弹窗明确出现“无需物流”和“确认发货”；\n"
                + "5. “无需物流”必须读取到已选中状态，确认动作前必须成功写入持久防重屏障。\n\n"
                + "客服正在输入、会话正在切换或 Bot 正在执行其它任务时会自动延后。实物商品请保持关闭。",
                "自动发货帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        public void InitUI(string seller)
        {
            var cfg = AutoDeliverySettings.Load(string.IsNullOrWhiteSpace(seller) ? _seller : seller.Trim());
            _enabled.IsChecked = cfg.Enabled;
            _delayMinutes.Text = cfg.DelayMinutes.ToString();
            UpdateEnabledState();
            UpdateSummary(cfg.Enabled, cfg.DelayMinutes);
        }

        private void UpdateEnabledState()
        {
            _delayMinutes.IsEnabled = _enabled.IsChecked == true;
            _delayMinutes.Opacity = _delayMinutes.IsEnabled ? 1.0 : 0.6;
        }

        private void UpdateSummary(bool enabled, int delay)
        {
            _summary.Text = enabled
                ? "当前状态：已启用。新订单将在约 " + delay + " 分钟后进入自动发货确认流程。"
                : "当前状态：未启用，不会自动改变任何订单的发货状态。";
        }
    }
}
