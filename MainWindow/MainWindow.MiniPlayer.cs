// MainWindow.MiniPlayer.cs — мини-плеер, side dock, resize grip
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Side Dock state ─────────────────────────────────────────────────────
        private bool   _isSideDocked;
        private bool   _sideVisible  = true;
        private double _sideHiddenLeft;
        private double _sideVisibleLeft;
        private double _sideSlideTarget;

        private readonly DispatcherTimer _sideHideTimer  = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private readonly DispatcherTimer _sideSlideTimer = new() { Interval = TimeSpan.FromMilliseconds(16)  };

        // ─── Mini resize state ───────────────────────────────────────────────────
        private bool   _isMiniResizing;
        private double _miniResizeStartX;
        private double _miniResizeStartWidth;
        private double _miniPlayerWidth = 480; // запомненная ширина мини-плеера

        // ─── Запомненная позиция мини-плеера ────────────────────────────────────
        private double _miniPlayerLeft = double.NaN;
        private double _miniPlayerTop  = double.NaN;

        // ─── Псевдо-максимизация (Aero Snap для полного плеера) ─────────────────
        private double _preMaxWidth       = 420;
        private double _preMaxHeight      = 680;
        private bool   _isPseudoMaximized;

        // ─── Запомненная высота полного плеера (размер плейлиста) ───────────────
        private double _fullPlayerHeight  = 680;

        // ─── Win32 ───────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOZORDER   = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ─── StateChanged — обработчик сворачивания/восстановления ───────────────

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_isMini)
            {
                // Мини-плеер: после Restore восстанавливаем размер
                if (WindowState == WindowState.Normal)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isMini) return;
                        SizeToContent = SizeToContent.Height;
                        if (Width != _miniPlayerWidth) Width = _miniPlayerWidth;
                        ClampWindowToWorkArea();
                        RestoreMinimizeBox();
                    }, DispatcherPriority.Render);
                }
                return;
            }

            // Полный плеер: Aero Snap развернул окно — запрещаем Maximized,
            // вместо этого растягиваем окно вручную на всю рабочую зону.
            // Это позволяет потом просто потянуть тайтлбар и вернуть нормальный размер.
            if (WindowState == WindowState.Maximized)
            {
                // Запоминаем размер ДО разворота
                _preMaxWidth  = ActualWidth  > 100 ? ActualWidth  : 420;
                _preMaxHeight = ActualHeight > 100 ? ActualHeight : 680;
                _isPseudoMaximized = true;

                Dispatcher.BeginInvoke(() =>
                {
                    var wa = SystemParameters.WorkArea;
                    WindowState = WindowState.Normal;
                    Left   = wa.Left;
                    Top    = wa.Top;
                    Width  = wa.Width;
                    Height = wa.Height;
                }, DispatcherPriority.Render);
            }
        }

        // ─── Mini ↔ Full toggle ───────────────────────────────────────────────────

        private void MiniMode_Click(object s, RoutedEventArgs e)
        {
            _isMini = !_isMini;
            if (_isMini)
            {
                // Запоминаем высоту полного плеера перед переходом в мини
                if (ActualHeight > 100)
                    _fullPlayerHeight = ActualHeight;

                // Закрываем EQ перед переходом в мини
                if (_eqPanelOpen)
                {
                    _eqPanelOpen = false;
                    EqContainer.Visibility = Visibility.Collapsed;
                    if (EqToggleText != null) EqToggleText.Text = "EQ ▼";
                }
                if (_isSideDocked) SideUndock();

                FullPlayer.Visibility      = Visibility.Collapsed;
                FullPlayerDecor.Visibility = Visibility.Collapsed;
                MiniPlayer.Visibility      = Visibility.Visible;
                SizeToContent = SizeToContent.Height;
                MinWidth  = 260; MaxWidth  = double.PositiveInfinity;
                MinHeight = 0;   MaxHeight = double.PositiveInfinity;
                Width     = _miniPlayerWidth;
                ResizeMode = ResizeMode.NoResize;
                Topmost    = false;

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Dispatcher.BeginInvoke(() =>
                {
                    // Восстанавливаем позицию мини-плеера если она была сохранена
                    if (!double.IsNaN(_miniPlayerLeft) && !double.IsNaN(_miniPlayerTop))
                    {
                        Left = _miniPlayerLeft;
                        Top  = _miniPlayerTop;
                    }
                    ClampWindowToWorkArea();
                    RestoreMinimizeBox();
                }, DispatcherPriority.Render);
            }
            else
            {
                // Запоминаем текущую позицию мини-плеера перед разворотом
                _miniPlayerLeft = Left;
                _miniPlayerTop  = Top;

                if (_isSideDocked) SideUndock();
                _sideSlideTimer.Stop(); _sideHideTimer.Stop();

                FullPlayer.Visibility      = Visibility.Visible;
                FullPlayerDecor.Visibility = Visibility.Visible;
                MiniPlayer.Visibility      = Visibility.Collapsed;

                SizeToContent = SizeToContent.Manual;
                MinWidth  = 420; MinHeight = 0;
                MaxWidth  = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                ResizeMode = ResizeMode.CanResize;
                Topmost    = false;

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Dispatcher.BeginInvoke(() =>
                {
                    Width  = 420;
                    Height = _fullPlayerHeight;
                    ClampWindowToWorkArea(420, _fullPlayerHeight);
                }, DispatcherPriority.Render);
            }
        }

        // ─── Drag move ───────────────────────────────────────────────────────────

        private void MiniPlayer_DragMove(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (_isSideDocked) SideUndock();

            // Если окно вышло за рабочую зону или развёрнуто — сначала восстанавливаем
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            var wa = SystemParameters.WorkArea;
            bool offScreen = Left + Width < wa.Left || Left > wa.Right
                          || Top + Height < wa.Top  || Top  > wa.Bottom;
            if (offScreen)
            {
                _miniPlayerWidth = 480;
                SizeToContent    = SizeToContent.Height;
                Width            = _miniPlayerWidth;
                // Ставим окно под курсор чтобы DragMove сразу работал
                var cursor = PointToScreen(e.GetPosition(this));
                Left = cursor.X - Width / 2;
                Top  = cursor.Y - 20;
                ClampWindowToWorkArea();
            }

            DragMove();
        }

        // ─── Двойной клик — сброс размера и позиции мини-плеера ─────────────────
        // Если плеер ушёл за край экрана или растянут до неудобного размера,
        // двойной клик возвращает ширину 480px и прижимает окно к рабочей зоне.
        private void MiniPlayer_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (!_isMini || e.ClickCount != 2) return;
            _miniPlayerWidth = 480;
            SizeToContent    = SizeToContent.Height;
            Width            = _miniPlayerWidth;
            Dispatcher.BeginInvoke(() => ClampWindowToWorkArea(),
                System.Windows.Threading.DispatcherPriority.Render);
            e.Handled = true;
        }

        private void MiniPlayer_TrySnap(object? s, EventArgs e) { /* auto-snap disabled */ }

        // ─── Mini resize grip ────────────────────────────────────────────────────

        private void MiniResizeGrip_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isMini) return;
            SizeToContent         = SizeToContent.Manual;
            _isMiniResizing       = true;
            _miniResizeStartX     = PointToScreen(e.GetPosition(this)).X;
            _miniResizeStartWidth = Width;
            ((UIElement)s).CaptureMouse();
            e.Handled = true;
        }

        private void MiniResizeGrip_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isMiniResizing) return;
            var src  = PresentationSource.FromVisual(this);
            double dpi  = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double curX = PointToScreen(e.GetPosition(this)).X;
            double newW = Math.Max(260, _miniResizeStartWidth + (curX - _miniResizeStartX));
            var hwnd    = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                (int)Math.Round(newW * dpi),
                (int)Math.Round(Height * dpi),
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void MiniResizeGrip_MouseUp(object s, MouseEventArgs e)
        {
            if (_isMiniResizing)
                _miniPlayerWidth = Width; // запоминаем новую ширину
            _isMiniResizing = false;
            ((UIElement)s).ReleaseMouseCapture();
        }

        // ─── Mini close/hide ─────────────────────────────────────────────────────

        private void MiniClose_Click(object s, RoutedEventArgs e)
        {
            if (_isSideDocked) SideHide();
            else Close();
        }

        // ─── Side Dock ────────────────────────────────────────────────────────────

        private void SideDockLeft_Click(object s, RoutedEventArgs e)  { /* docking disabled */ }
        private void SideDockRight_Click(object s, RoutedEventArgs e) { /* docking disabled */ }

        private void SideDock(bool leftSide)
        {
            _isSideDocked = true;
            var sc = SystemParameters.WorkArea;
            if (leftSide)
            {
                _sideVisibleLeft = sc.Left;
                _sideHiddenLeft  = sc.Left - Width + 6;
            }
            else
            {
                _sideVisibleLeft = sc.Right - Width;
                _sideHiddenLeft  = sc.Right - 6;
            }
            MouseEnter += SideDock_MouseEnter;
            MouseLeave += SideDock_MouseLeave;
        }

        private void SideUndock()
        {
            _isSideDocked = false;
            _sideVisible  = true;
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
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var src  = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            GetWindowRect(hwnd, out RECT rc);
            double curLeft = rc.Left / dpi;
            double diff    = _sideSlideTarget - curLeft;
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

        // ─── Win32 helpers ────────────────────────────────────────────────────────

        private void SetWindowLeft(double logicalLeft)
        {
            var src  = PresentationSource.FromVisual(this);
            double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(logicalLeft * dpi),
                (int)Math.Round(Top * dpi),
                0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void ClampWindowToWorkArea(double? targetW = null, double? targetH = null)
        {
            var wa     = SystemParameters.WorkArea;
            double w   = targetW ?? (Width  > 0 ? Width  : ActualWidth);
            double h   = targetH ?? (Height > 0 ? Height : ActualHeight);
            double newLeft = Left, newTop = Top;

            if (newLeft + w > wa.Right)  newLeft = wa.Right  - w;
            if (newTop  + h > wa.Bottom) newTop  = wa.Bottom - h;
            if (newLeft < wa.Left)       newLeft = wa.Left;
            if (newTop  < wa.Top)        newTop  = wa.Top;   // фикс: окно ушло за верхний край

            if (newLeft != Left) Left = newLeft;
            if (newTop  != Top)  Top  = newTop;
        }
        // ─── WS_MINIMIZEBOX helper ───────────────────────────────────────────────
        // WPF убирает WS_MINIMIZEBOX когда ResizeMode = NoResize.
        // Без этого флага кнопка «–» в таскбаре не шлёт WM_SYSCOMMAND SC_MINIMIZE.

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_STYLE      = -16;
        private const int WS_MINIMIZEBOX = 0x00020000;

        private void RestoreMinimizeBox()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_MINIMIZEBOX) == 0)
                SetWindowLong(hwnd, GWL_STYLE, style | WS_MINIMIZEBOX);
        }

    }
}
