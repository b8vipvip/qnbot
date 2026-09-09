using BotLib;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using System;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Narrow fallback for the custom-rendered Qianniu v9 saved-account login page.
    /// It is never a standalone recovery loop: QnStartupConnectionSelfHeal calls it only after a
    /// verified updater-authorized one-shot Qianniu restart. The helper never selects an account,
    /// opens account management, reads credentials, or types text.
    /// </summary>
    internal static class QnSavedAccountLoginFallback
    {
        internal static readonly string[] SavedAccountLoginMarkerNames =
        {
            "单账号登录", "添加账号"
        };

        internal static bool TryClickSavedAccountLoginFallback(
            Window window,
            AutomationElement[] descendants)
        {
            if (window == null) return false;
            descendants = descendants ?? new AutomationElement[0];

            var rect = SafeRect(window);
            if (rect.Width < 520 || rect.Width > 980 || rect.Height < 360 || rect.Height > 760)
                return false;

            // Positive page evidence when UIA exposes any surrounding text. The exact primary
            // button itself is known not to be exposed on the field v9 build. If no markers are
            // exposed at all, the compact login-window geometry is the secondary evidence.
            var hasMarker = descendants.Any(x => SavedAccountLoginMarkerNames.Any(marker =>
                string.Equals(SafeName(x), marker, StringComparison.Ordinal)));
            var hasReception = descendants.Any(x =>
                SafeName(x).IndexOf("接待", StringComparison.Ordinal) >= 0);
            if (hasReception) return false;

            // Saved-account chooser field geometry is a compact 765x544-class window. Require the
            // stronger marker evidence outside the tight geometry band.
            var compactLoginGeometry = rect.Width >= 650 && rect.Width <= 860
                && rect.Height >= 470 && rect.Height <= 650;
            if (!hasMarker && !compactLoginGeometry) return false;

            // Field run 1.1.1374 proved that UIA Window.Focus() can fail with Access Denied on the
            // custom-rendered login window even though the window rectangle is readable. Focus is
            // therefore best-effort only. A focus failure must never suppress the screen-coordinate
            // click, because Mouse.Click uses desktop input rather than the UIA focus pattern.
            try
            {
                window.Focus();
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "更新后千牛恢复：激活千牛v9自绘登录窗口失败，继续尝试屏幕坐标点击: type="
                    + ex.GetType().Name + ", " + ex.Message,
                    10);
            }

            // Blue primary 登录 button occupies the lower-right content band on the v9 saved-
            // account page. Use window-relative coordinates rather than machine-fixed pixels.
            // Ratios are intentionally centred inside the button and outside 单账号登录/添加账号.
            var point = new System.Drawing.Point(
                rect.Left + (int)Math.Round(rect.Width * 0.70),
                rect.Top + (int)Math.Round(rect.Height * 0.84));

            try
            {
                Mouse.Click(point);
                Log.Info("更新后千牛恢复UI操作成功: stage=千牛v9主登录, method=saved-account-relative"
                    + ", markerEvidence=" + hasMarker
                    + ", window=" + rect.Width + "x" + rect.Height
                    + ", click=" + point.X + "," + point.Y);
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "更新后千牛恢复自绘登录页屏幕坐标点击失败: type="
                    + ex.GetType().Name + ", " + ex.Message
                    + ", window=" + rect.Width + "x" + rect.Height
                    + ", click=" + point.X + "," + point.Y,
                    10);
                return false;
            }
        }

        private static System.Drawing.Rectangle SafeRect(AutomationElement element)
        {
            try { return element == null ? System.Drawing.Rectangle.Empty : element.BoundingRectangle; }
            catch { return System.Drawing.Rectangle.Empty; }
        }

        private static string SafeName(AutomationElement element)
        {
            try { return element == null ? string.Empty : (element.Name ?? string.Empty).Trim(); }
            catch { return string.Empty; }
        }
    }
}
