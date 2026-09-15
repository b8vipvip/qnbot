using Bot.Automation.ChatDeskNs;
using Bot.Automation.ChatDeskNs.Automators;
using Bot.ChromeNs;
using Bot.Options;
using System.Windows;

namespace Bot.AssistWindow.Widget
{
    public partial class RightPanel
    {
        private void btnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var desk = Wnd == null ? null : Wnd.Desk;
            if (desk == null || desk.Hwnd == null) return;

            var seller = DeskSellerBindingRegistry.GetSeller(desk);
            if (string.IsNullOrWhiteSpace(seller))
            {
                seller = QnAccountFinder.ResolveSellerNameForWindow(
                    desk.ProcessId,
                    desk.Hwnd.Handle,
                    desk.WndTitle);
                if (!QnAccountFinder.IsGenericReceptionTitle(seller))
                {
                    var bound = DeskSellerBindingRegistry.BindResolvedSeller(
                        desk, seller, "settings-window-identity");
                    if (bound == null) seller = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(seller) || QnAccountFinder.IsGenericReceptionTitle(seller))
            {
                MessageBox.Show(
                    "当前千牛接待窗口已经检测到，但还没有确认当前可见的客服账号。\n\n"
                    + "请回到这个千牛窗口，切换到目标客服标签并点击一次任意买家会话；Bot 会根据千牛的客服/买家会话切换事件确认当前活动 seller。\n"
                    + "同一个千牛接待窗口可以承载多个客服账号；Bot 只允许当前活动客服使用当前可见输入框，避免跨客服串号。",
                    "正在识别当前客服身份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Clicking Settings from the Bot panel is explicit user interaction with this visible
            // Desk. Make that seller the authoritative settings scope before WndOption opens, so a
            // stale ActiveShopSessionRegistry value from another customer-service tab can never
            // replace the seller selected by the visible native composer.
            var qn = QN.FindExistingBySellerNick(seller);
            if (qn != null && qn.Seller != null)
            {
                ActiveShopSessionRegistry.ActivateFromFocusedWebView(
                    qn,
                    qn.CDP == null ? string.Empty : qn.CDP.SessionId,
                    qn.Seller.TargetId,
                    string.Empty,
                    "settings-visible-desk");
                ActiveShopSessionRegistry.ReassertCurrent("settings-visible-desk");
            }

            WndOption.MyShow(seller, Wnd);
        }
    }
}
