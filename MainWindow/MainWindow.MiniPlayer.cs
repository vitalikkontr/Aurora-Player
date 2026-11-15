// MainWindow.MiniPlayer.cs — мини-плеер, side dock, resize grip
#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Side Dock state ─────────────────────────────────────────────────────
        private bool _isSideDocked;
        private bool _sideVisible = true;
        private double _sideHiddenLeft;
        private double _sideVisibleLeft;
        private double _sideSlideTarget;

        private readonly DispatcherTimer _sideHideTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private readonly DispatcherTimer _sideSlideTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

        // ─── Mini resize state ───────────────────────────────────────────────────
        private bool _isMiniResizing;
        private double _miniResizeStartX;
        private double _miniResizeStartWidth;
        private double _miniPlayerWidth = 480; // запомненная ширина мини-плеера

        // ─── Запомненная позиция мини-плеера ────────────────────────────────────
        private double _miniPlayerLeft = double.NaN;
        private double _miniPlayerTop = double.NaN;

        // ─── Псевдо-максимизация (Aero Snap для полного плеера) ─────────────────
        private double _preMaxWidth = 420;
        private double _preMaxHeight = 680;
        private bool _isPseudoMaximized;

        // ─── Запомненная высота полного плеера (размер плейлиста) ───────────────
        private double _fullPlayerHeight = 680;

        // ─── Win32 ───────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ─── StateChanged — обработчик сворачивания/восстановления ───────────────

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_isMini)
            {
                if (WindowState == WindowState.Normal)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_isMini || _isClosingHandled) return;
                        SizeToContent = SizeToContent.Height;
                        if (Width != _miniPlayerWidth) Width = _miniPlayerWidth;
                        ClampWindowToWorkArea();
                        RestoreMinimizeBox();
                    }), DispatcherPriority.Render);
                }
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                _preMaxWidth = ActualWidth > 100 ? ActualWidth : 420;
                _preMaxHeight = ActualHeight > 100 ? ActualHeight : 680;
                _isPseudoMaximized = true;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isMini || _isClosingHandled) return;
                    var wa = GetCurrentMonitorWorkAreaDip();
                    WindowState = WindowState.Normal;
                    Left = wa.Left;
                    Top = wa.Top;
                    Width = wa.Width;
                    Height = wa.Height;
                }), DispatcherPriority.Render);
            }
        }

        // ─── Mini ↔ Full toggle ───────────────────────────────────────────────────

        private void MiniMode_Click(object s, RoutedEventArgs e)
        {
            _isMini = !_isMini;
            if (_isMini)
            {
                // Закрываем EQ и ChannelMixer до сохранения базовой высоты полного плеера
                if (_eqPanelOpen)
                    EqToggle_Click(this, new RoutedEventArgs());
                if (_mixPanelOpen)
                    MixerToggle_Click(this, new RoutedEventArgs());

                if (WindowState == WindowState.Normal && !_isPseudoMaximized)
                {
                    double h = !double.IsNaN(Height) && Height > 100 ? Height : ActualHeight;
                    if (h > 100)
                        _fullPlayerHeight = h;
                }
                _isPseudoMaximized = false;

                if (_isSideDocked) SideUndock();

                if (FullPlayer != null) FullPlayer.Visibility = Visibility.Collapsed;
                if (FullPlayerDecor != null) FullPlayerDecor.Visibility = Visibility.Collapsed;
                if (MiniPlayer != null) MiniPlayer.Visibility = Visibility.Visible;

                SizeToContent = SizeToContent.Height;
                MinWidth = 260; MaxWidth = double.PositiveInfinity;
                MinHeight = 0; MaxHeight = double.PositiveInfinity;
                Width = _miniPlayerWidth;
                ResizeMode = ResizeMode.NoResize;
                Topmost = false;

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isMini || _isClosingHandled) return;
                    if (!double.IsNaN(_miniPlayerLeft) && !double.IsNaN(_miniPlayerTop))
                    {
                        Left = _miniPlayerLeft;
                        Top = _miniPlayerTop;
                    }
                    ClampWindowToWorkArea();
                    RestoreMinimizeBox();
                }), DispatcherPriority.Render);
            }
            else
            {
                if (WindowState == WindowState.Normal)
                {
                    _miniPlayerLeft = Left;
                    _miniPlayerTop = Top;
                }
                _isPseudoMaximized = false;

                if (_isSideDocked) SideUndock();
                _sideSlideTimer.Stop();
                _sideHideTimer.Stop();

                if (FullPlayer != null) FullPlayer.Visibility = Visibility.Visible;
                if (FullPlayerDecor != null) FullPlayerDecor.Visibility = Visibility.Visible;
                if (MiniPlayer != null) MiniPlayer.Visibility = Visibility.Collapsed;

                SizeToContent = SizeToContent.Manual;
                MinWidth = 420; MinHeight = 0;
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                ResizeMode = ResizeMode.CanResize;
                Topmost = false;

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isMini || _isClosingHandled) return;
                    Width = 420;
                    Height = _fullPlayerHeight;
                    ClampWindowToWorkArea(420, _fullPlayerHeight);
                }), DispatcherPriority.Render);
            }
        }

        // ─── Drag move ───────────────────────────────────────────────────────────

        private void MiniPlayer_DragMove(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (_isSideDocked) SideUndock();

            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            var wa = GetCurrentMonitorWorkAreaDip();
            double curW = ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) ? Width : _miniPlayerWidth);
            double curH = ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) ? Height : 60);

            bool offScreen = Left + curW < wa.Left || Left > wa.Right
                          || Top + curH < wa.Top || Top > wa.Bottom;
            if (offScreen)
            {
                _miniPlayerWidth = 480;
                SizeToContent = SizeToContent.Height;
                Width = _miniPlayerWidth;

                var src = PresentationSource.FromVisual(this);
                var fromDevice = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                Point cursorDip = fromDevice.Transform(PointToScreen(e.GetPosition(this)));

                Left = cursorDip.X - Width / 2.0;
                Top = cursorDip.Y - 20.0;
                ClampWindowToWorkArea();
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        // ─── Двойной клик — сброс размера и позиции мини-плеера ─────────────────
        private void MiniPlayer_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (!_isMini || e.ClickCount != 2) return;
            _miniPlayerWidth = 480;
            SizeToContent = SizeToContent.Height;
            Width = _miniPlayerWidth;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isMini && !_isClosingHandled)
                    ClampWindowToWorkArea();
            }), DispatcherPriority.Render);
            e.Handled = true;
        }

        private void MiniPlayer_TrySnap(object? s, EventArgs e) { /* auto-snap disabled */ }

        // ─── Mini resize grip ────────────────────────────────────────────────────

        private void MiniResizeGrip_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isMini) return;
            if (PresentationSource.FromVisual(this) == null) return;

            SizeToContent = SizeToContent.Manual;
            _isMiniResizing = true;
            _miniResizeStartX = PointToScreen(e.GetPosition(this)).X;
            _miniResizeStartWidth = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;

            if (s is UIElement el)
            {
                el.LostMouseCapture -= MiniResizeGrip_LostMouseCapture;
                el.LostMouseCapture += MiniResizeGrip_LostMouseCapture;
                el.CaptureMouse();
            }
            e.Handled = true;
        }

        private void MiniResizeGrip_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isMiniResizing) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishMiniResize(s as UIElement);
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var src = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (dpi <= 0) dpi = 1.0;

            double curX = PointToScreen(e.GetPosition(this)).X;
            double deltaDip = (curX - _miniResizeStartX) / dpi;
            double newW = Math.Max(260.0, _miniResizeStartWidth + deltaDip);
            double curH = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
            if (curH <= 0 || double.IsNaN(curH)) return;

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                (int)Math.Round(newW * dpi),
                (int)Math.Round(curH * dpi),
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void MiniResizeGrip_MouseUp(object s, MouseButtonEventArgs e)
        {
            FinishMiniResize(s as UIElement);
        }

        private void MiniResizeGrip_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FinishMiniResize(sender as UIElement);
        }

        private void FinishMiniResize(UIElement? grip)
        {
            if (_isMiniResizing)
            {
                double w = ActualWidth > 0 ? ActualWidth : Width;
                if (!double.IsNaN(w) && w >= 260.0)
                    _miniPlayerWidth = w;
                _isMiniResizing = false;
            }

            if (grip != null)
            {
                grip.LostMouseCapture -= MiniResizeGrip_LostMouseCapture;
                if (grip.IsMouseCaptured)
                    grip.ReleaseMouseCapture();
            }
        }

        // ─── Mini close/hide ─────────────────────────────────────────────────────

        private void MiniClose_Click(object s, RoutedEventArgs e)
        {
            if (_isSideDocked) SideHide();
            else Close();
        }

        // ─── Side Dock ────────────────────────────────────────────────────────────

        private void SideDockLeft_Click(object s, RoutedEventArgs e) { /* docking disabled */ }
        private void SideDockRight_Click(object s, RoutedEventArgs e) { /* docking disabled */ }

        private void SideDock(bool leftSide)
        {
            if (_isSideDocked) SideUndock();

            _isSideDocked = true;
            var sc = GetCurrentMonitorWorkAreaDip();
            double w = ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) ? Width : _miniPlayerWidth);
            if (leftSide)
            {
                _sideVisibleLeft = sc.Left;
                _sideHiddenLeft = sc.Left - w + 6;
            }
            else
            {
                _sideVisibleLeft = sc.Right - w;
                _sideHiddenLeft = sc.Right - 6;
            }
            MouseEnter += SideDock_MouseEnter;
            MouseLeave += SideDock_MouseLeave;
        }

        private void SideUndock()
        {
            _isSideDocked = false;
            _sideVisible = true;
            _sideHideTimer.Stop();
            _sideSlideTimer.Stop();
            MouseEnter -= SideDock_MouseEnter;
            MouseLeave -= SideDock_MouseLeave;
        }

        private void SideHide()
        {
            if (!_isSideDocked || !_sideVisible) return;
            _sideVisible = false;
            SideSlideStart(hide: true);
        }

        private void SideShow()
        {
            if (!_isSideDocked || _sideVisible) return;
            _sideVisible = true;
            SideSlideStart(hide: false);
        }

        private void SideSlideStart(bool hide)
        {
            _sideSlideTarget = hide ? _sideHiddenLeft : _sideVisibleLeft;
            _sideSlideTimer.Start();
        }

        private void SideSlide_Tick(object? s, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !_isSideDocked || _isClosingHandled)
            {
                _sideSlideTimer.Stop();
                return;
            }

            var src = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (dpi <= 0) dpi = 1.0;

            if (!GetWindowRect(hwnd, out RECT rc))
            {
                _sideSlideTimer.Stop();
                return;
            }

            double curLeft = rc.Left / dpi;
            double diff = _sideSlideTarget - curLeft;
            if (Math.Abs(diff) < 0.8)
            {
                SetWindowLeft(_sideSlideTarget);
                _sideSlideTimer.Stop();
                return;
            }
            SetWindowLeft(curLeft + diff * 0.28);
        }

        private void SideDock_MouseEnter(object s, MouseEventArgs e) { _sideHideTimer.Stop(); SideShow(); }
        private void SideDock_MouseLeave(object s, MouseEventArgs e) { _sideHideTimer.Stop(); _sideHideTimer.Start(); }

        // ─── Win32 / DPI helpers ─────────────────────────────────────────────────

        private Rect GetCurrentMonitorWorkAreaDip()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(mon, ref info))
                {
                    var src = PresentationSource.FromVisual(this);
                    var matrix = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                    Point tl = matrix.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
                    Point br = matrix.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
                    return new Rect(tl, br);
                }
            }
            return SystemParameters.WorkArea;
        }

        private void SetWindowLeft(double logicalLeft)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var src = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (dpi <= 0) dpi = 1.0;

            double topDip = !double.IsNaN(Top) ? Top : 0.0;
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(logicalLeft * dpi),
                (int)Math.Round(topDip * dpi),
                0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void ClampWindowToWorkArea(double? targetW = null, double? targetH = null)
        {
            if (double.IsNaN(Left) || double.IsNaN(Top))
                return;

            var wa = GetCurrentMonitorWorkAreaDip();
            double w = targetW ?? (!double.IsNaN(Width) && Width > 0 ? Width : ActualWidth);
            double h = targetH ?? (!double.IsNaN(Height) && Height > 0 ? Height : ActualHeight);

            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
                return;

            double newLeft = Left, newTop = Top;

            if (newLeft + w > wa.Right) newLeft = wa.Right - w;
            if (newTop + h > wa.Bottom) newTop = wa.Bottom - h;
            if (newLeft < wa.Left) newLeft = wa.Left;
            if (newTop < wa.Top) newTop = wa.Top;

            if (newLeft != Left) Left = newLeft;
            if (newTop != Top) Top = newTop;
        }

        // ─── WS_MINIMIZEBOX helper ───────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_STYLE = -16;
        private const int WS_MINIMIZEBOX = 0x00020000;

        private void RestoreMinimizeBox()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_MINIMIZEBOX) == 0)
                SetWindowLong(hwnd, GWL_STYLE, style | WS_MINIMIZEBOX);
        }
    }
}

#nullable restore