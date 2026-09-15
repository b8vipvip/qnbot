using Bot.Automation.ChatDeskNs;
using BotLib;
using BotLib.Misc;
using BotLib.Wpf.Extensions;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Bot.AssistWindow
{
    public partial class WndAssist
    {
        private static readonly object AttachedPerformanceBootstrap = InitializeAttachedPerformance();
        private static int _lowChurnTimerInstalled;
        private bool _lowChurnTrackingInstalled;
        private bool _normalZOrderInitialized;
        private DateTime _lastZOrderFollowAt = DateTime.MinValue;
        private Point? _lastRightPanelPoint;
        private Point? _lastShowButtonPoint;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoSendChanging = 0x0400;
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        private static object InitializeAttachedPerformance()
        {
            EventManager.RegisterClassHandler(
                typeof(WndAssist),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AttachedWindowLoadedForPerformance),
                true);
            return new object();
        }

        private static void AttachedWindowLoadedForPerformance(object sender, RoutedEventArgs e)
        {
            var wnd = sender as WndAssist;
            if (wnd == null) return;

            // Let the historical Loaded handler finish first, then replace the high-churn
            // tracking hooks. This keeps all existing controls and business logic intact.
            wnd.Dispatcher.BeginInvoke(new Action(delegate
            {
                wnd.InstallLowChurnTracking();
            }));

            if (Interlocked.Exchange(ref _lowChurnTimerInstalled, 1) == 0)
            {
                try
                {
                    var oldTimer = _timer;
                    _timer = new NoReEnterTimer(SafePeriodicTrack, 5000, 1500);
                    if (oldTimer != null) oldTimer.Stop();
                    Log.Info("贴窗Bot已切换千牛崩溃保护跟随：5秒仅做几何兜底，不再周期抢Topmost，也不再主动移动/缩放千牛窗口。" );
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
            }
        }

        private void InstallLowChurnTracking()
        {
            if (_lowChurnTrackingInstalled || Desk == null) return;
            _lowChurnTrackingInstalled = true;

            // Replace event paths that historically called Track() or directly changed the
            // host Qianniu window. In particular Desk_EvMaximize called Desk.SetRect(), and
            // the first Track() called SetDeskLocation(). Both become cross-process window
            // mutations against Qt's qwindows backend and can race with Qt window teardown.
            Desk.EvShow -= Desk_EvShow;
            Desk.EvNormalize -= Desk_EvNormalize;
            Desk.EvMaximize -= Desk_EvMaximize;
            Desk.EvMoved -= Desk_EvMoved;
            Desk.EvResized -= Desk_EvResized;
            Desk.EvGetForeground -= Desk_EvGetForeground;

            Desk.EvShow += SafeDesk_EvShow;
            Desk.EvNormalize += SafeDesk_EvNormalize;
            Desk.EvMaximize += SafeDesk_EvMaximize;
            Desk.EvMoved += SafeDesk_EvMoved;
            Desk.EvResized += SafeDesk_EvResized;
            Desk.EvGetForeground += SafeDesk_EvGetForeground;
            Closed += SafeTrackingClosed;

            NormalizeAttachedWindowZOrderOnce();
            SafeTrackGeometry(false, false);
        }

        private void SafeTrackingClosed(object sender, EventArgs e)
        {
            try
            {
                if (Desk == null) return;
                Desk.EvShow -= SafeDesk_EvShow;
                Desk.EvNormalize -= SafeDesk_EvNormalize;
                Desk.EvMaximize -= SafeDesk_EvMaximize;
                Desk.EvMoved -= SafeDesk_EvMoved;
                Desk.EvResized -= SafeDesk_EvResized;
                Desk.EvGetForeground -= SafeDesk_EvGetForeground;
            }
            catch { }
        }

        private static void SafePeriodicTrack()
        {
            try
            {
                foreach (var wnd in AssistBag.ToArray())
                {
                    if (wnd == null || wnd.IsClosed) continue;
                    wnd.SafeTrackGeometry(false, true);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void SafeDesk_EvShow(object sender, DeskEventArgs e)
        {
            DispatcherEx.xInvoke(delegate
            {
                if (Desk == null || Desk.IsMinimized || !Desk.IsVisible) return;
                SafeShowAssist();
            });
        }

        private void SafeDesk_EvNormalize(object sender, DeskEventArgs e)
        {
            WakeUp();
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvMaximize(object sender, DeskEventArgs e)
        {
            // Never force the host back to normal or resize it from qnbot. The historical
            // handler called Desk.ShowNormal()/Desk.SetRect() while Qt was processing the
            // maximize transition. Keep the host-owned transition intact and only follow it.
            WakeUp();
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvMoved(object sender, DeskEventArgs e)
        {
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvResized(object sender, DeskEventArgs e)
        {
            SafeTrackGeometry(false, false);
        }

        private void SafeDesk_EvGetForeground(object sender, DeskEventArgs e)
        {
            DispatcherEx.xInvoke(delegate
            {
                SafeShowAssist();
                FollowDeskZOrderWithoutActivation();
            });
        }

        private void SafeShowAssist()
        {
            if (!IsVisible) Show();
            SafeTrackGeometry(false, false);
            WakeUpAssist();
            if (Desk != null && Desk.IsForeground)
                FollowDeskZOrderWithoutActivation();
        }

        private void SafeTrackGeometry(bool adjustDeskLocation, bool periodic)
        {
            if (_isTracking || IsClosed) return;
            _isTracking = true;
            try
            {
                DispatcherEx.xInvoke(delegate
                {
                    try
                    {
                        if (!IsLoaded || IsHidden())
                        {
                            // Avoid repeated Hide calls; changing WPF visibility unnecessarily is
                            // one of the main causes of the attached panel flashing behind Qianniu.
                            if (IsVisible) Hide();
                            return;
                        }

                        if (!IsVisible && Desk.GetVisiblePercent(true) > 0.0)
                            Show();
                        if (!IsVisible) return;

                        SetPanelsSize();

                        // Crash guard: qnbot follows Qianniu; it must not reposition Qianniu.
                        // The old first-track path called SetDeskLocation() -> Desk.SetLocation()
                        // -> SetWindowPlacement() in the AliWorkbench process. Qt 5.15.2 receives
                        // those changes in qwindows.dll while its QWindow objects can be changing.
                        // Keep the parameter for call-site compatibility but intentionally ignore
                        // requests to move the host window.
                        if (_isFirstTrack)
                            _isFirstTrack = false;

                        // Do not call the historical SetRightPanelPosition here. Its MoveUIElement
                        // implementation hides a visible control, moves it, then shows it again.
                        // Small Win32 rectangle jitter therefore becomes a visible flash. Update
                        // Canvas coordinates directly and only when the position really changed.
                        SetRightPanelPositionWithoutVisibilityToggle();

                        // Z-order is only corrected on real foreground/move/show events.
                        // The periodic fallback never raises the Bot window.
                        if (!periodic && Desk.IsForeground)
                            FollowDeskZOrderWithoutActivation();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                });
            }
            finally
            {
                _isTracking = false;
            }
        }

        private void SetRightPanelPositionWithoutVisibilityToggle()
        {
            try
            {
                if (Desk == null) return;
                if (IsShowRightPanel)
                {
                    var point = PointFromScreen(new Point(
                        Desk.Rect.Left + Desk.Rect.Width + 6,
                        Desk.Rect.Top));
                    if (HasMeaningfulPositionChange(_lastRightPanelPoint, point))
                    {
                        Canvas.SetLeft(ctlRightPanel, point.X);
                        Canvas.SetTop(ctlRightPanel, point.Y);
                        _lastRightPanelPoint = point;
                    }
                    if (ctlRightPanel.Visibility != Visibility.Visible)
                        ctlRightPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    var point = PointFromScreen(new Point(
                        Desk.Rect.Left + Desk.Rect.Width - (int)btnShowRight.Width - 5,
                        Desk.Rect.Top - (int)btnShowRight.Height - 5));
                    if (HasMeaningfulPositionChange(_lastShowButtonPoint, point))
                    {
                        Canvas.SetLeft(btnShowRight, point.X);
                        Canvas.SetTop(btnShowRight, point.Y);
                        _lastShowButtonPoint = point;
                    }
                    if (btnShowRight.Visibility != Visibility.Visible)
                        btnShowRight.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static bool HasMeaningfulPositionChange(Point? previous, Point current)
        {
            if (!previous.HasValue) return true;
            return Math.Abs(previous.Value.X - current.X) >= 0.75
                || Math.Abs(previous.Value.Y - current.Y) >= 0.75;
        }

        private void NormalizeAttachedWindowZOrderOnce()
        {
            if (_normalZOrderInitialized) return;
            try
            {
                if (!IsLoaded || Handle == 0) return;
                _normalZOrderInitialized = true;
                SetWindowPos(
                    new IntPtr(Handle),
                    HwndNotTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoSendChanging);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void FollowDeskZOrderWithoutActivation()
        {
            try
            {
                if (!IsLoaded || Desk == null || Desk.Hwnd == null || Desk.Hwnd.Handle == 0) return;
                if (!Desk.IsForeground) return;
                if (DateTime.Now - _lastZOrderFollowAt < TimeSpan.FromMilliseconds(150)) return;
                _lastZOrderFollowAt = DateTime.Now;

                NormalizeAttachedWindowZOrderOnce();
                SetWindowPos(
                    new IntPtr(Handle),
                    new IntPtr(Desk.Hwnd.Handle),
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoSendChanging);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
