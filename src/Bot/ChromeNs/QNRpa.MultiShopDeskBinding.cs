using Bot.Automation.ChatDeskNs;
using BotLib;
using FlaUI.UIA3;
using System;
using System.Linq;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private readonly object _sellerDeskBindingSync = new object();
        private int _sellerDeskProcessId;
        private int _sellerDeskHwnd;
        private string _sellerDeskBoundSeller = string.Empty;

        internal Desk ResolveSellerDesk()
        {
            var seller = SellerNick;
            if (string.IsNullOrWhiteSpace(seller)) return null;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk != null) return desk;

            var desks = Desk.Snapshot().Where(x => x != null && x.IsAlive).ToList();
            if (desks.Count == 1 && RuntimeSellerCount() <= 1) return desks[0];
            return null;
        }

        private Desk TryRecoverActiveSellerDesk(string seller)
        {
            if (RuntimeSellerCount() <= 1) return null;

            string isolationReason;
            if (!ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)) return null;

            // Production 1.1.1502 showed that the seller->HWND registry can be empty after an
            // attached-window lifecycle transition even though the independently arbitrated
            // active ShopKey/CDP session is still valid and AliWorkbench still has exactly one
            // live reception Desk. In that exact case it is safe to reconstruct the many-to-one
            // mapping. Never choose among multiple native windows and never recover a background
            // seller: both conditions remain fail-closed.
            var desks = Desk.Snapshot().Where(x => x != null && x.IsAlive).ToList();
            if (desks.Count != 1) return null;

            var desk = DeskSellerBindingRegistry.BindResolvedSeller(
                desks[0], seller, "active-session-single-desk-recovery");
            if (desk == null) return null;

            DeskSellerBindingRegistry.MarkActiveSeller(
                desk, seller, "active-session-single-desk-recovery");
            Log.Info("多客服RPA已从活动ShopKey/CDP身份恢复共享千牛窗口绑定: seller=" + seller
                + ", pid=" + desk.ProcessId + ", hwnd=" + desk.Hwnd.Handle);
            return desk;
        }

        internal bool EnsureSellerDeskBinding(bool force = false)
        {
            var seller = SellerNick;
            if (string.IsNullOrWhiteSpace(seller)) return false;

            if (RuntimeSellerCount() > 1)
            {
                string isolationReason;
                if (!ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason))
                {
                    Log.ErrorWithMaxCount("多客服发送安全锁已阻止RPA：目标店铺未通过活动身份校验: seller="
                        + seller + ", activeSeller=" + ActiveShopSessionRegistry.GetActiveSellerNick()
                        + ", reason=" + isolationReason, 30);
                    return false;
                }
            }

            var desk = ResolveSellerDesk();
            if (desk == null) desk = TryRecoverActiveSellerDesk(seller);
            if (desk == null)
            {
                if (Desk.HasMultipleDesks || RuntimeSellerCount() > 1)
                {
                    Log.ErrorWithMaxCount(
                        "多店铺RPA绑定失败，未找到当前seller已登记的千牛窗口，禁止猜测其他窗口: seller=" + seller,
                        20);
                }
                return false;
            }

            if (RuntimeSellerCount() > 1 && !DeskSellerBindingRegistry.IsSellerForDesk(desk, seller))
            {
                Log.ErrorWithMaxCount("多客服RPA已隔离：当前可见千牛输入框不属于目标seller，禁止跨客服写入/发送: seller="
                    + seller + ", activeSeller=" + DeskSellerBindingRegistry.GetSeller(desk)
                    + ", hwnd=" + desk.Hwnd.Handle, 20);
                return false;
            }

            lock (_sellerDeskBindingSync)
            {
                if (automationApplication != null
                    && _sellerDeskProcessId == desk.ProcessId
                    && _sellerDeskHwnd == desk.Hwnd.Handle
                    && string.Equals(_sellerDeskBoundSeller, seller, StringComparison.Ordinal))
                {
                    return true;
                }

                try
                {
                    automationApplication = FlaUI.Core.Application.Attach(desk.ProcessId);
                    if (uia3Automation == null) uia3Automation = new UIA3Automation();
                    _messageInputTextArea = null;
                    _sendMessageButton = null;
                    _closeContactButton = null;
                    _sellerDeskProcessId = desk.ProcessId;
                    _sellerDeskHwnd = desk.Hwnd.Handle;
                    _sellerDeskBoundSeller = seller;
                    Log.Info("RPA已绑定当前活动客服的千牛窗口: seller=" + seller
                        + ", pid=" + desk.ProcessId + ", hwnd=" + desk.Hwnd.Handle);
                    return true;
                }
                catch (Exception ex)
                {
                    _sellerDeskProcessId = 0;
                    _sellerDeskHwnd = 0;
                    _sellerDeskBoundSeller = string.Empty;
                    _messageInputTextArea = null;
                    _sendMessageButton = null;
                    Log.ErrorWithMaxCount("RPA绑定当前活动客服千牛窗口失败: seller=" + seller + ", " + ex.Message, 20);
                    return false;
                }
            }
        }

        internal bool IsSellerDeskBindingReady
        {
            get
            {
                var desk = DeskSellerBindingRegistry.FindSellerDesk(SellerNick);
                if (RuntimeSellerCount() > 1)
                {
                    string isolationReason;
                    if (!ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)) return false;
                }
                return desk != null
                    && DeskSellerBindingRegistry.IsSellerForDesk(desk, SellerNick)
                    && automationApplication != null
                    && _sellerDeskProcessId == desk.ProcessId
                    && _sellerDeskHwnd == desk.Hwnd.Handle
                    && string.Equals(_sellerDeskBoundSeller, SellerNick, StringComparison.Ordinal);
            }
        }

        internal bool TryClearCanceledBotDraft(string buyer, string reason)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            if (string.IsNullOrWhiteSpace(buyer) || _qn == null || _qn.Buyer == null) return false;

            var currentBuyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _qn.Buyer.Nick);
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, currentBuyer, buyer)) return false;

            var expected = (LastSetPlainText ?? string.Empty).Trim();
            if (expected.Length == 0 || !HasExpectedDraft(expected)) return false;

            currentBuyer = _qn.Buyer == null
                ? string.Empty
                : BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _qn.Buyer.Nick);
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, currentBuyer, buyer)) return false;
            if (!FocusEditor()) return false;

            string latestText;
            if (!TryGetEditorText(out latestText) || !EditorMatchesExpectedText(latestText, expected)) return false;

            currentBuyer = _qn.Buyer == null
                ? string.Empty
                : BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _qn.Buyer.Nick);
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, currentBuyer, buyer)) return false;

            PressCtrlA();
            PressBackspace();
            LastSetPlainText = string.Empty;
            LatestSetTextTime = DateTime.MinValue;

            string remaining;
            var cleared = !TryGetEditorText(out remaining) || !EditorMatchesExpectedText(remaining, expected);
            if (cleared)
            {
                Log.Info("已清除已取消的Bot专属草稿: seller=" + SellerNick
                    + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
            }
            return cleared;
        }

        private static int RuntimeSellerCount()
        {
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null && qn.Seller != null && !string.IsNullOrWhiteSpace(qn.Seller.Nick))
                    .Select(qn => qn.Seller.Nick.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
            }
            catch { return 0; }
        }
    }
}
