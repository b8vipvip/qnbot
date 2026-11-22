using Bot.Automation.ChatDeskNs;
using Bot.ChromeNs;
using Bot.ShopScope;
using System;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private string _multiShopSessionSeller = string.Empty;
        private string _multiShopSessionBuyer = string.Empty;

        /// <summary>
        /// Refreshes this control from the QN proven to belong to its own native Desk.
        /// It does not record reception statistics and does not request or send any message.
        /// </summary>
        internal void SynchronizeSellerSession(QN qn)
        {
            if (qn == null || qn.Seller == null) return;
            if (!ActiveShopSessionRegistry.IsCurrentSessionForReadOnlyUi(qn)) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return;

            string shopKey;
            try { shopKey = ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey; }
            catch { return; }
            if (_desk != null && !DeskSellerBindingRegistry.IsBoundToDeskForReadOnlyUi(
                _desk, seller, shopKey))
            {
                return;
            }

            var buyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            var sellerChanged = !string.Equals(_multiShopSessionSeller, seller, StringComparison.Ordinal);
            var buyerChanged = !string.Equals(_multiShopSessionBuyer, buyer, StringComparison.Ordinal);
            if (!sellerChanged && !buyerChanged && ReferenceEquals(_preQN, qn)) return;

            _multiShopSessionSeller = seller;
            _multiShopSessionBuyer = buyer;
            _preQN = qn;
            txtSeller.Text = seller;
            txtBuyer.Text = buyer.Length == 0 ? "..." : buyer;
            RefreshConversations();
            RefreshRunStatus();
            RefreshStats();
            if (buyerChanged && buyer.Length > 0)
            {
                RefreshItems();
            }
        }
    }
}