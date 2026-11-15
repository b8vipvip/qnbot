using Bot.Automation.ChatDeskNs;
using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Windows;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private static readonly object ControlPlaneStatusBootstrap = InitializeControlPlaneStatus();
        private bool _controlPlaneStatusInstalled;

        private static object InitializeControlPlaneStatus()
        {
            EventManager.RegisterClassHandler(
                typeof(CtlRobot),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(ControlPlaneStatusLoaded),
                true);
            return new object();
        }

        private static void ControlPlaneStatusLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as CtlRobot;
            if (control == null) return;
            control.Dispatcher.BeginInvoke(new Action(delegate
            {
                control.InstallControlPlaneStatus();
            }));
        }

        private void InstallControlPlaneStatus()
        {
            if (_controlPlaneStatusInstalled) return;
            _controlPlaneStatusInstalled = true;

            // Reduce duplicate UI refresh churn. Runtime business timers are untouched;
            // these two timers only repaint diagnostics/statistics in the attached panel.
            if (_diagnosticsTimer != null)
            {
                _diagnosticsTimer.Interval = TimeSpan.FromSeconds(5);
                _diagnosticsTimer.Tick += ControlPlaneStatusTick;
            }
            if (_statsTimer != null)
            {
                _statsTimer.Interval = TimeSpan.FromSeconds(10);
            }

            RefreshControlPlaneStatus();
        }

        private void ControlPlaneStatusTick(object sender, EventArgs e)
        {
            RefreshControlPlaneStatus();
        }

        private void RefreshControlPlaneStatus()
        {
            if (txtStatusApi == null) return;
            try
            {
                var shop = ResolveAttachedShop();
                var status = ShopTokenBindingService.GetStatusText(shop);
                txtStatusApi.Text = "服务端：" + status;
                txtStatusApi.ToolTip = ShopTokenBindingService.GetStatusToolTip(shop);
            }
            catch (Exception ex)
            {
                txtStatusApi.Text = "服务端：检测失败";
                txtStatusApi.ToolTip = ex.Message;
            }
        }

        private ShopContext ResolveAttachedShop()
        {
            // Prefer the exact context attached to this WPF window. The desk title
            // and seller nickname are display identifiers and may be ambiguous.
            var attached = ShopScopedUiBridge.Get(Window.GetWindow(this));
            if (attached != null) return attached;
            if (_desk == null) return ShopSettingsScope.Current;

            var seller = DeskSellerBindingRegistry.GetSeller(_desk);
            if (string.IsNullOrWhiteSpace(seller)) seller = _desk.WndTitle;
            seller = (seller ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller)
                || string.Equals(seller, "千牛接待台", StringComparison.Ordinal))
            {
                return ShopSettingsScope.Current;
            }

            var activeSeller = ActiveShopSessionRegistry.GetActiveSellerNick();
            var activeShopKey = ActiveShopSessionRegistry.GetActiveShopKey();
            if (activeShopKey.Length > 0
                && string.Equals(activeSeller, seller, StringComparison.Ordinal))
                return ShopContextLocator.ResolveRuntimeByShopKey(activeShopKey);

            return ShopContextLocator.ResolveRuntimeBySellerNick(seller);
        }
    }
}
