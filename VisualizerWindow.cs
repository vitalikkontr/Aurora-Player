// VisualizerWindow.cs — Aurora Visualizer v4 ★ ULTRA EDITION ★
// Режимы: SpectrumBars · SpectrumRings · Aurora · LiquidWave
//         RibbonWaves · CircleEQ · DancerSpectrum · MagmaFlow
// Управление: двойной клик / F11 fullscreen · M смена режима · Esc закрыть · перетаскивание мышью
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Shell;

namespace AuroraPlayer
{
    public interface IVisualizerDataProvider
    {
        float[]? GetFftData();
        bool IsPlaying { get; }
        string CurrentTrackTitle { get; }
        string CurrentArtist { get; }
    }

    public enum VisualizerMode
    {
        SpectrumBars,
        SpectrumRings,
        Aurora,
        LiquidWave,
        RibbonWaves,
        CircleEQ,
        DancerSpectrum,
        MagmaFlow,        // ★ Потоки магмы
    }

    public class VisualizerWindow : Window
    {
        private const double DefaultWindowWidth = 760;
        private const double DefaultWindowHeight = 430;

        // ── Win32 для истинного fullscreen поверх панели задач ────────────────────
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out WINPOINT lpPoint);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);

        [StructLayout(LayoutKind.Sequential)] private struct WINPOINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        private readonly IVisualizerDataProvider _provider;
        private Canvas _canvas = null!;
        private VisualizerMode _mode = VisualizerMode.SpectrumBars;
        private bool _fullscreen;
        private double _prevLeft, _prevTop, _prevWidth = DefaultWindowWidth, _prevHeight = DefaultWindowHeight;

        public event Action<VisualizerMode>? ModeChanged;
        public VisualizerMode CurrentMode
        {
            get => _mode;
            set { _mode = value; ApplyModeVisibility(); _modeLabel.Text = ModeName(_mode); }
        }

        // ── Анимация ──────────────────────────────────────────────────────────────
        private double _time;
        private readonly float[] _smoothFft = new float[512];
        private readonly float[] _peakFft = new float[512];
        private float _bassEnergy, _midEnergy, _highEnergy;
        private readonly Random _rng = new();

        // ── SpectrumBars ──────────────────────────────────────────────────────────
        private const int BarCount = 90;
        private readonly Rectangle[] _bars = new Rectangle[BarCount];
        private readonly Rectangle[] _peakDots = new Rectangle[BarCount];
        private readonly Rectangle[] _mirrorBars = new Rectangle[BarCount];
        private readonly LinearGradientBrush[] _lgbBars = new LinearGradientBrush[BarCount];
        private readonly double[] _barH = new double[BarCount];
        private readonly double[] _peakH = new double[BarCount];
        private readonly Polyline _barEnvTop = new();
        private readonly Polyline _barEnvBot = new();
        private readonly LinearGradientBrush _lgbBarEnvTop = new(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.0),
                new GradientStop(Colors.Transparent, 0.33),
                new GradientStop(Colors.Transparent, 0.66),
                new GradientStop(Colors.Transparent, 1.0),
            },
            new Point(0, 0), new Point(1, 0));
        private readonly LinearGradientBrush _lgbBarEnvBot = new(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.0),
                new GradientStop(Colors.Transparent, 1.0),
            },
            new Point(0, 0), new Point(1, 0));

        // ── SpectrumRings ─────────────────────────────────────────────────────────
        private const int RingCount = 36;
        private readonly Ellipse[] _rings = new Ellipse[RingCount];
        private readonly Ellipse[] _ringGlows = new Ellipse[RingCount];
        private readonly SolidColorBrush[] _scbRings = new SolidColorBrush[RingCount];
        private readonly SolidColorBrush[] _scbRingGlows = new SolidColorBrush[RingCount];

        // ── Aurora ────────────────────────────────────────────────────────────────
        private const int AuroraLineCount = 28;
        private const int AuroraPointCount = 120;
        private readonly Polyline[] _auroraLines = new Polyline[AuroraLineCount];
        private readonly SolidColorBrush[] _scbAuroraLines = new SolidColorBrush[AuroraLineCount];
        private bool _auroraBuilt;

        // ── LiquidWave ────────────────────────────────────────────────────────────
        private const int LiquidWaveCount = 18;
        private const int LiquidWavePts = 140;
        private readonly Polyline[] _liquidLines = new Polyline[LiquidWaveCount];
        private bool _liquidBuilt;

        // ── RibbonWaves ───────────────────────────────────────────────────────────
        private const int RibbonCount = 22;
        private const int RibbonPts = 160;
        private readonly Polyline[] _ribbonLines = new Polyline[RibbonCount];
        private bool _ribbonBuilt;

        // ── CircleEQ ──────────────────────────────────────────────────────────────
        private const int CircleBarCount = 144;
        private const int CircleRingLines = 4;
        private readonly Line[] _circBars = new Line[CircleBarCount];
        private readonly SolidColorBrush[] _scbCircBars = new SolidColorBrush[CircleBarCount];
        private readonly Ellipse[] _circRings = new Ellipse[CircleRingLines];
        private readonly Ellipse[] _circRingGlows = new Ellipse[CircleRingLines];
        private readonly SolidColorBrush[] _scbCircRings = new SolidColorBrush[CircleRingLines];
        private readonly SolidColorBrush[] _scbCircRingGlows = new SolidColorBrush[CircleRingLines];
        private readonly Ellipse _circCenter = new();
        private readonly RadialGradientBrush _rgbCircCenter = new(Colors.Transparent, Colors.Transparent);
        private bool _circleBuilt;
        private readonly double[] _circBarH = new double[CircleBarCount];

        // ── DancerSpectrum ────────────────────────────────────────────────────────
        private bool _dancerBuilt;
        private bool _dancerJointsInitialized;

        private const int DancerBarL = 64;
        private readonly Rectangle[] _dBarsL = new Rectangle[DancerBarL];
        private readonly Rectangle[] _dPeaksL = new Rectangle[DancerBarL];
        private readonly LinearGradientBrush[] _lgbDBarsL = new LinearGradientBrush[DancerBarL];
        private readonly double[] _dBarHL = new double[DancerBarL];
        private readonly double[] _dPeakHL = new double[DancerBarL];

        private const int DancerBarR = 64;
        private readonly Rectangle[] _dBarsR = new Rectangle[DancerBarR];
        private readonly Rectangle[] _dPeaksR = new Rectangle[DancerBarR];
        private readonly LinearGradientBrush[] _lgbDBarsR = new LinearGradientBrush[DancerBarR];
        private readonly double[] _dBarHR = new double[DancerBarR];
        private readonly double[] _dPeakHR = new double[DancerBarR];

        private readonly Path _dancerBody = new();
        private readonly Path _dancerGlow = new();
        private readonly Ellipse _dancerHead = new();
        private readonly Ellipse _dancerHeadG = new();

        private readonly List<DancerParticle> _dParticles = new(120);

        private const int DancerRayCount = 24;
        private readonly Line[] _dancerRays = new Line[DancerRayCount];
        private readonly Polyline _dancerGroundWave = new();

        private const int DancerAuraCount = 5;
        private readonly Ellipse[] _dancerAuras = new Ellipse[DancerAuraCount];
        private readonly SolidColorBrush[] _scbDancerAuras = new SolidColorBrush[DancerAuraCount];
        private readonly BlurEffect[] _dancerAuraBlurs = new BlurEffect[DancerAuraCount];

        private readonly BlurEffect _dancerGlowBlur = new() { Radius = 10 };
        private readonly BlurEffect _dancerHeadGBlur = new() { Radius = 8 };

        private readonly SolidColorBrush _scbDancerHead = new();
        private readonly SolidColorBrush _scbDancerHeadG = new();

        private readonly LinearGradientBrush _lgbDancerGlow = new(
            new GradientStopCollection { new GradientStop(), new GradientStop() },
            new Point(0, 0), new Point(1, 1));
        private readonly LinearGradientBrush _lgbDancerBody = new(
            new GradientStopCollection { new GradientStop(), new GradientStop() },
            new Point(0, 0), new Point(1, 1));
        private readonly LinearGradientBrush[] _lgbDancerRays = new LinearGradientBrush[DancerRayCount];
        private readonly LinearGradientBrush _lgbGroundWave = new(
            new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop() },
            new Point(0, 0), new Point(1, 0));

        private readonly LinearGradientBrush[] _lgbLiquid = new LinearGradientBrush[LiquidWaveCount];
        private readonly LinearGradientBrush[] _lgbRibbon = new LinearGradientBrush[RibbonCount];
        private readonly LinearGradientBrush[] _lgbMagma = new LinearGradientBrush[MagmaLineCount];

        private readonly PointCollection[] _auroraPoints = new PointCollection[AuroraLineCount];
        private readonly PointCollection[] _liquidPoints = new PointCollection[LiquidWaveCount];
        private readonly PointCollection[] _ribbonPoints = new PointCollection[RibbonCount];
        private readonly PointCollection[] _magmaPoints = new PointCollection[MagmaLineCount];
        private readonly PointCollection _groundWavePoints = new(60);

        // ── MagmaFlow ─────────────────────────────────────────────────────────────
        private const int MagmaLineCount = 24;
        private const int MagmaPts = 180;
        private readonly Polyline[] _magmaLines = new Polyline[MagmaLineCount];
        private readonly BlurEffect[] _magmaBlurEffects = new BlurEffect[MagmaLineCount];
        private bool _magmaBuilt;

        // ── Позы танцора ──────────────────────────────────────────────────────────
        private readonly double[] _jointX = new double[13];
        private readonly double[] _jointY = new double[13];
        private readonly double[] _jointX2 = new double[13];
        private readonly double[] _jointY2 = new double[13];
        private double _posePhase;

        // ── UI ────────────────────────────────────────────────────────────────────
        private TextBlock _trackLabel = null!;
        private TextBlock _artistLabel = null!;
        private TextBlock _modeLabel = null!;
        private Border _controlBar = null!;
        private UIElement[] _hudElements = Array.Empty<UIElement>();
        private DispatcherTimer _hideBarTimer = null!;
        private bool _barVisible = true;
        private int _lastCursorX = int.MinValue;
        private int _lastCursorY = int.MinValue;

        private static readonly DoubleAnimation FadeOutAnimation = CreateFadeOutAnimation();
        private static readonly SolidColorBrush PeakDotBrush = CreateFrozenBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

        private static DoubleAnimation CreateFadeOutAnimation()
        {
            var anim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.9));
            anim.Freeze();
            return anim;
        }

        private static SolidColorBrush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ── Фон ───────────────────────────────────────────────────────────────────
        private Rectangle _bgRect = null!;
        private readonly LinearGradientBrush _bgBrush;

        // ── Drag-to-move ──────────────────────────────────────────────────────────
        private bool _dragging;
        private Point _dragOriginDip;
        private double _dragOriginLeft, _dragOriginTop;

        public VisualizerWindow(IVisualizerDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Title = "Aurora Visualizer";
            Width = DefaultWindowWidth;
            Height = DefaultWindowHeight;
            MinWidth = 400;
            MinHeight = 260;
            Background = Brushes.Black;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            _bgBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x03, 0x00, 0x0E), 0.0),
                    new GradientStop(Color.FromRgb(0x08, 0x00, 0x1C), 0.5),
                    new GradientStop(Color.FromRgb(0x00, 0x03, 0x10), 1.0),
                }
            };

            BuildUI();
            BuildBars();
            BuildRings();

            for (int i = 0; i < DancerAuraCount; i++)
            {
                _dancerAuraBlurs[i] = new BlurEffect { Radius = 3 };
                _scbDancerAuras[i] = new SolidColorBrush();
            }
            for (int i = 0; i < DancerRayCount; i++)
                _lgbDancerRays[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0, 0), new Point(1, 0));

            for (int i = 0; i < AuroraLineCount; i++) _auroraPoints[i] = new PointCollection(AuroraPointCount);
            for (int i = 0; i < LiquidWaveCount; i++) _liquidPoints[i] = new PointCollection(LiquidWavePts);
            for (int i = 0; i < RibbonCount; i++) _ribbonPoints[i] = new PointCollection(RibbonPts);
            for (int i = 0; i < MagmaLineCount; i++) _magmaPoints[i] = new PointCollection(MagmaPts);

            for (int i = 0; i < LiquidWaveCount; i++)
                _lgbLiquid[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0, 0), new Point(1, 0));
            for (int i = 0; i < RibbonCount; i++)
                _lgbRibbon[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0, 0), new Point(1, 0));
            for (int i = 0; i < MagmaLineCount; i++)
                _lgbMagma[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop() },
                    new Point(0, 0), new Point(1, 0));

            BuildLiquidWave();
            BuildRibbonWaves();
            BuildCircleEQ();
            BuildMagmaFlow();
            ApplyModeVisibility();

            KeyDown += OnKeyDown;

            PreviewMouseLeftButtonDown += OnDragStart;
            PreviewMouseMove += OnDragMove;
            PreviewMouseLeftButtonUp += OnDragEnd;
            LostMouseCapture += OnDragLostCapture;

            MouseDoubleClick += OnWindowDoubleClick;

            _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hideBarTimer.Tick += OnHideBarTimerTick;

            MouseMove += OnWindowMouseMove;
            Closing += OnWindowClosing;
            IsVisibleChanged += OnWindowIsVisibleChanged;
        }

        private void OnWindowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!IsChildOfControlBar(e.OriginalSource as DependencyObject))
                ToggleFullscreen();
        }

        private void OnHideBarTimerTick(object? sender, EventArgs e)
        {
            _hideBarTimer.Stop();
            HideControlBar();
            if (_fullscreen && IsVisible)
                Cursor = Cursors.None;
        }

        private void OnWindowMouseMove(object sender, MouseEventArgs e)
        {
            if (GetCursorPos(out WINPOINT pt))
            {
                if (pt.X == _lastCursorX && pt.Y == _lastCursorY)
                    return;
                _lastCursorX = pt.X;
                _lastCursorY = pt.Y;
            }

            if (_fullscreen)
                Cursor = null;

            ShowControlBar();
            _hideBarTimer.Stop();
            _hideBarTimer.Start();
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnRender;
            _hideBarTimer.Stop();
            _hideBarTimer.Tick -= OnHideBarTimerTick;

            KeyDown -= OnKeyDown;
            PreviewMouseLeftButtonDown -= OnDragStart;
            PreviewMouseMove -= OnDragMove;
            PreviewMouseLeftButtonUp -= OnDragEnd;
            LostMouseCapture -= OnDragLostCapture;
            MouseDoubleClick -= OnWindowDoubleClick;
            MouseMove -= OnWindowMouseMove;
            IsVisibleChanged -= OnWindowIsVisibleChanged;
            Closing -= OnWindowClosing;

            _dParticles.Clear();
            SetThreadExecutionState(ES_CONTINUOUS);
            Cursor = null;
        }

        private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                _lastRenderTime = DateTime.MinValue;
                _lastRenderingTime = TimeSpan.Zero;
                _frameAccumulatorMs = 0.0;
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
                CompositionTarget.Rendering -= OnRender;
                CompositionTarget.Rendering += OnRender;

                if (_fullscreen)
                    SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

                ShowControlBar();
                _hideBarTimer.Stop();
                _hideBarTimer.Start();
            }
            else
            {
                CompositionTarget.Rendering -= OnRender;
                _frameAccumulatorMs = 0.0;
                _hideBarTimer.Stop();
                SetThreadExecutionState(ES_CONTINUOUS);
                Cursor = null;
            }
        }

        // ─── Drag-to-move ─────────────────────────────────────────────────────────
        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (_fullscreen || e.ChangedButton != MouseButton.Left) return;
            if (IsChildOfControlBar(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2) return;
            if (PresentationSource.FromVisual(this) is not PresentationSource src || src.CompositionTarget == null)
                return;

            _dragging = true;
            _dragOriginDip = src.CompositionTarget.TransformFromDevice.Transform(PointToScreen(e.GetPosition(this)));
            _dragOriginLeft = Left;
            _dragOriginTop = Top;
            CaptureMouse();
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _fullscreen || e.LeftButton != MouseButtonState.Pressed) return;
            if (PresentationSource.FromVisual(this) is not PresentationSource src || src.CompositionTarget == null)
                return;

            Point curDip = src.CompositionTarget.TransformFromDevice.Transform(PointToScreen(e.GetPosition(this)));
            Left = _dragOriginLeft + (curDip.X - _dragOriginDip.X);
            Top = _dragOriginTop + (curDip.Y - _dragOriginDip.Y);
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void OnDragLostCapture(object sender, MouseEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
        }

        private bool IsChildOfControlBar(DependencyObject? el)
        {
            while (el != null)
            {
                if (ReferenceEquals(el, _controlBar)) return true;
                el = el is Visual
                    ? VisualTreeHelper.GetParent(el)
                    : LogicalTreeHelper.GetParent(el);
            }
            return false;
        }

        // ─── Построение UI ────────────────────────────────────────────────────────
        private void BuildUI()
        {
            var root = new Grid();
            _bgRect = new Rectangle { Fill = _bgBrush };
            root.Children.Add(_bgRect);

            _canvas = new Canvas { ClipToBounds = true };
            root.Children.Add(_canvas);

            _controlBar = BuildControlBar();
            root.Children.Add(_controlBar);

            var trackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(20, 16, 0, 0),
                IsHitTestVisible = false,
            };
            _artistLabel = new TextBlock
            {
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = CreateFrozenBrush(Color.FromArgb(0x60, 0xA8, 0x9F, 0xFF)),
            };
            _trackLabel = new TextBlock
            {
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = CreateFrozenBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            };
            trackPanel.Children.Add(_artistLabel);
            trackPanel.Children.Add(_trackLabel);
            root.Children.Add(trackPanel);

            _modeLabel = new TextBlock
            {
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = CreateFrozenBrush(Color.FromArgb(0x50, 0xA8, 0x9F, 0xFF)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 20, 0),
                Text = ModeName(_mode),
                IsHitTestVisible = false,
            };
            root.Children.Add(_modeLabel);

            _hudElements = new UIElement[] { _controlBar, _artistLabel, _trackLabel, _modeLabel };
            Content = root;
        }

        private Border BuildControlBar()
        {
            var bg = new LinearGradientBrush(
                Color.FromArgb(0xD0, 0x02, 0x00, 0x0C),
                Color.FromArgb(0x00, 0x02, 0x00, 0x0C),
                new Point(0, 1), new Point(0, 0));
            bg.Freeze();

            var bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = bg,
                Padding = new Thickness(16, 10, 16, 14),
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MakeCtrlBtn("◈ РЕЖИМ", CycleMode));
            panel.Children.Add(MakeCtrlBtn("⛶ FULLSCREEN  F11", ToggleFullscreen));
            panel.Children.Add(MakeCtrlBtn("✕ ЗАКРЫТЬ", CloseOrHideVisualizer));

            var hint = new TextBlock
            {
                Text = "Двойной клик / F11 — полный экран  ·  M — режим  ·  Esc — закрыть",
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize = 10,
                Foreground = CreateFrozenBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(panel, Dock.Right);
            dock.Children.Add(panel);
            dock.Children.Add(hint);
            bar.Child = dock;
            return bar;
        }

        private static Button MakeCtrlBtn(string text, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(4, 0, 4, 0),
                Background = CreateFrozenBrush(Color.FromArgb(0x35, 0x7C, 0x6B, 0xFF)),
                Foreground = CreateFrozenBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                BorderBrush = CreateFrozenBrush(Color.FromArgb(0x55, 0x7C, 0x6B, 0xFF)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
            };
            btn.Click += (_, _) => action();
            return btn;
        }

        private void CloseOrHideVisualizer()
        {
            if (_fullscreen)
                ToggleFullscreen();
            Hide();
        }

        private void ShowControlBar()
        {
            if (_barVisible) return;
            _barVisible = true;
            foreach (var el in _hudElements)
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 1;
            }
        }

        private void HideControlBar()
        {
            if (!_barVisible) return;
            _barVisible = false;
            foreach (var el in _hudElements)
                el.BeginAnimation(OpacityProperty, FadeOutAnimation);
        }

        // ─── Построение визуальных объектов ──────────────────────────────────────
        private void BuildBars()
        {
            for (int i = 0; i < BarCount; i++)
            {
                double t = (double)i / BarCount;
                var lgb = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(HsvToColor((0.70 + t * 0.32 + 0.18) % 1.0, 0.95, 0.45), 0),
                        new GradientStop(HsvToColor((0.70 + t * 0.32) % 1.0, 0.80, 1.0), 1),
                    },
                    new Point(0, 1), new Point(0, 0));
                _lgbBars[i] = lgb;

                var bar = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = lgb };
                _canvas.Children.Add(bar);
                _bars[i] = bar;

                var mirrorFill = new LinearGradientBrush(
                    HsvToColor((0.70 + t * 0.32) % 1.0, 0.75, 0.35),
                    HsvToColor((0.70 + t * 0.32) % 1.0, 0.75, 0.0),
                    new Point(0, 0), new Point(0, 1));
                mirrorFill.Freeze();

                var mirror = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = mirrorFill };
                _canvas.Children.Add(mirror);
                _mirrorBars[i] = mirror;

                var peak = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, Fill = PeakDotBrush };
                _canvas.Children.Add(peak);
                _peakDots[i] = peak;
            }

            _barEnvTop.StrokeThickness = 2.0;
            _barEnvTop.StrokeLineJoin = PenLineJoin.Round;
            _barEnvTop.Opacity = 0.75;
            _barEnvTop.Stroke = _lgbBarEnvTop;
            _canvas.Children.Add(_barEnvTop);

            _barEnvBot.StrokeThickness = 1.2;
            _barEnvBot.StrokeLineJoin = PenLineJoin.Round;
            _barEnvBot.Opacity = 0.35;
            _barEnvBot.Stroke = _lgbBarEnvBot;
            _canvas.Children.Add(_barEnvBot);
        }

        private void BuildRings()
        {
            for (int i = 0; i < RingCount; i++)
            {
                _scbRingGlows[i] = new SolidColorBrush();
                var glow = new Ellipse
                {
                    StrokeThickness = 12,
                    Fill = Brushes.Transparent,
                    Opacity = 0.10,
                    Stroke = _scbRingGlows[i]
                };
                _canvas.Children.Add(glow);
                _ringGlows[i] = glow;

                _scbRings[i] = new SolidColorBrush();
                var el = new Ellipse
                {
                    StrokeThickness = 1.2,
                    Fill = Brushes.Transparent,
                    Stroke = _scbRings[i]
                };
                _canvas.Children.Add(el);
                _rings[i] = el;
            }
        }

        private void BuildLiquidWave()
        {
            for (int li = 0; li < LiquidWaveCount; li++)
            {
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round, Stroke = _lgbLiquid[li] };
                _canvas.Children.Add(pl);
                _liquidLines[li] = pl;
            }
            _liquidBuilt = true;
        }

        private void BuildRibbonWaves()
        {
            for (int li = 0; li < RibbonCount; li++)
            {
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round, Stroke = _lgbRibbon[li] };
                _canvas.Children.Add(pl);
                _ribbonLines[li] = pl;
            }
            _ribbonBuilt = true;
        }

        private void BuildCircleEQ()
        {
            _circleBuilt = true;
            for (int i = 0; i < CircleBarCount; i++)
            {
                _scbCircBars[i] = new SolidColorBrush();
                var ln = new Line
                {
                    StrokeThickness = 2.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Stroke = _scbCircBars[i]
                };
                _canvas.Children.Add(ln);
                _circBars[i] = ln;
            }
            for (int i = 0; i < CircleRingLines; i++)
            {
                _scbCircRingGlows[i] = new SolidColorBrush();
                var glow = new Ellipse
                {
                    Fill = Brushes.Transparent,
                    StrokeThickness = 8,
                    Opacity = 0.12,
                    Stroke = _scbCircRingGlows[i]
                };
                _canvas.Children.Add(glow);
                _circRingGlows[i] = glow;

                _scbCircRings[i] = new SolidColorBrush();
                var ring = new Ellipse
                {
                    Fill = Brushes.Transparent,
                    StrokeThickness = 1.0,
                    Stroke = _scbCircRings[i]
                };
                _canvas.Children.Add(ring);
                _circRings[i] = ring;
            }

            _circCenter.Fill = _rgbCircCenter;
            _canvas.Children.Add(_circCenter);
        }

        private void BuildDancerSpectrum()
        {
            _dancerBuilt = true;

            for (int i = 0; i < DancerBarL; i++)
            {
                _lgbDBarsL[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0, 1), new Point(0, 0));
                var b = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = _lgbDBarsL[i] };
                _canvas.Children.Add(b);
                _dBarsL[i] = b;

                var p = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, Fill = PeakDotBrush };
                _canvas.Children.Add(p);
                _dPeaksL[i] = p;
            }

            for (int i = 0; i < DancerBarR; i++)
            {
                _lgbDBarsR[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0, 1), new Point(0, 0));
                var b = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = _lgbDBarsR[i] };
                _canvas.Children.Add(b);
                _dBarsR[i] = b;

                var p = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, Fill = PeakDotBrush };
                _canvas.Children.Add(p);
                _dPeaksR[i] = p;
            }

            for (int i = 0; i < DancerAuraCount; i++)
            {
                var aura = new Ellipse
                {
                    Fill = Brushes.Transparent,
                    StrokeThickness = 1.0,
                    Stroke = _scbDancerAuras[i],
                    Effect = _dancerAuraBlurs[i]
                };
                _canvas.Children.Add(aura);
                _dancerAuras[i] = aura;
            }

            for (int i = 0; i < DancerRayCount; i++)
            {
                var ray = new Line { StrokeThickness = 0.8, Stroke = _lgbDancerRays[i] };
                _canvas.Children.Add(ray);
                _dancerRays[i] = ray;
            }

            _dancerGroundWave.StrokeLineJoin = PenLineJoin.Round;
            _dancerGroundWave.StrokeThickness = 2.5;
            _dancerGroundWave.Stroke = _lgbGroundWave;
            _canvas.Children.Add(_dancerGroundWave);

            _dancerGlow.StrokeLineJoin = PenLineJoin.Round;
            _dancerGlow.StrokeThickness = 14;
            _dancerGlow.Opacity = 0.0;
            _dancerGlow.Stroke = _lgbDancerGlow;
            _dancerGlow.Effect = _dancerGlowBlur;
            _canvas.Children.Add(_dancerGlow);

            _dancerBody.StrokeLineJoin = PenLineJoin.Round;
            _dancerBody.StrokeThickness = 3.5;
            _dancerBody.Fill = Brushes.Transparent;
            _dancerBody.Stroke = _lgbDancerBody;
            _canvas.Children.Add(_dancerBody);

            _dancerHeadG.Fill = Brushes.Transparent;
            _dancerHeadG.StrokeThickness = 10;
            _dancerHeadG.Stroke = _scbDancerHeadG;
            _dancerHeadG.Effect = _dancerHeadGBlur;
            _canvas.Children.Add(_dancerHeadG);

            _dancerHead.Fill = Brushes.Transparent;
            _dancerHead.StrokeThickness = 3.0;
            _dancerHead.Stroke = _scbDancerHead;
            _canvas.Children.Add(_dancerHead);

            _dancerJointsInitialized = false;
        }

        // ─── Режим ───────────────────────────────────────────────────────────────
        private void CycleMode()
        {
            _mode = (VisualizerMode)(((int)_mode + 1) % Enum.GetValues<VisualizerMode>().Length);
            _modeLabel.Text = ModeName(_mode);
            ApplyModeVisibility();
            ModeChanged?.Invoke(_mode);
        }

        private static string ModeName(VisualizerMode m) => m switch
        {
            VisualizerMode.SpectrumBars => "SPECTRUM BARS",
            VisualizerMode.SpectrumRings => "SPECTRUM RINGS",
            VisualizerMode.Aurora => "AURORA",
            VisualizerMode.LiquidWave => "LIQUID WAVE",
            VisualizerMode.RibbonWaves => "RIBBON WAVES",
            VisualizerMode.CircleEQ => "CIRCLE EQ",
            VisualizerMode.DancerSpectrum => "✦ DANCER SPECTRUM ✦",
            VisualizerMode.MagmaFlow => "🔥 MAGMA FLOW",
            _ => "",
        };

        private void ApplyModeVisibility()
        {
            bool bars = _mode == VisualizerMode.SpectrumBars;
            bool rings = _mode == VisualizerMode.SpectrumRings;
            bool dancer = _mode == VisualizerMode.DancerSpectrum;

            Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

            foreach (var b in _bars) b.Visibility = V(bars);
            foreach (var b in _mirrorBars) b.Visibility = V(bars);
            foreach (var p in _peakDots) p.Visibility = V(bars);
            _barEnvTop.Visibility = V(bars);
            _barEnvBot.Visibility = V(bars);

            foreach (var r in _rings) r.Visibility = V(rings);
            foreach (var r in _ringGlows) r.Visibility = V(rings);

            if (_auroraBuilt)
                foreach (var pl in _auroraLines)
                    pl.Visibility = V(_mode == VisualizerMode.Aurora);
            if (_liquidBuilt)
                foreach (var pl in _liquidLines)
                    pl.Visibility = V(_mode == VisualizerMode.LiquidWave);
            if (_ribbonBuilt)
                foreach (var pl in _ribbonLines)
                    pl.Visibility = V(_mode == VisualizerMode.RibbonWaves);
            if (_circleBuilt)
            {
                bool cEQ = _mode == VisualizerMode.CircleEQ;
                foreach (var ln in _circBars) ln.Visibility = V(cEQ);
                foreach (var el in _circRings) el.Visibility = V(cEQ);
                foreach (var el in _circRingGlows) el.Visibility = V(cEQ);
                _circCenter.Visibility = V(cEQ);
            }
            if (_dancerBuilt)
            {
                foreach (var b in _dBarsL) b.Visibility = V(dancer);
                foreach (var b in _dBarsR) b.Visibility = V(dancer);
                foreach (var p in _dPeaksL) p.Visibility = V(dancer);
                foreach (var p in _dPeaksR) p.Visibility = V(dancer);
                foreach (var a in _dancerAuras) a.Visibility = V(dancer);
                foreach (var r in _dancerRays) r.Visibility = V(dancer);
                _dancerBody.Visibility = V(dancer);
                _dancerGlow.Visibility = V(dancer);
                _dancerHead.Visibility = V(dancer);
                _dancerHeadG.Visibility = V(dancer);
                _dancerGroundWave.Visibility = V(dancer);
            }
            if (_magmaBuilt)
                foreach (var pl in _magmaLines)
                    pl.Visibility = V(_mode == VisualizerMode.MagmaFlow);

            if (!dancer)
            {
                foreach (var p in _dParticles)
                {
                    if (p.El != null) _canvas.Children.Remove(p.El);
                }
                _dParticles.Clear();
                _dancerJointsInitialized = false;
            }
        }

        // ─── Fullscreen ───────────────────────────────────────────────────────────
        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                if (WindowState == WindowState.Normal)
                {
                    _prevLeft = Left;
                    _prevTop = Top;
                    _prevWidth = double.IsFinite(Width) && Width > 0 ? Width : ActualWidth;
                    _prevHeight = double.IsFinite(Height) && Height > 0 ? Height : ActualHeight;
                }
                else
                {
                    _prevLeft = RestoreBounds.Left;
                    _prevTop = RestoreBounds.Top;
                    _prevWidth = RestoreBounds.Width;
                    _prevHeight = RestoreBounds.Height;
                }

                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi))
                    return;

                var src = PresentationSource.FromVisual(this);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                if (dpi <= 0) dpi = 1.0;

                double mLeft = mi.rcMonitor.Left / dpi;
                double mTop = mi.rcMonitor.Top / dpi;
                double mWidth = (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpi;
                double mHeight = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpi;

                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                Left = mLeft; Top = mTop; Width = mWidth; Height = mHeight;

                SetWindowPos(hwnd, HWND_TOP,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    SWP_SHOWWINDOW);

                _fullscreen = true;
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
                _hideBarTimer.Stop();
                HideControlBar();
                Cursor = Cursors.None;
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
            }
            else
            {
                WindowState = WindowState.Normal;
                Topmost = false;
                ResizeMode = ResizeMode.CanResize;
                if (double.IsFinite(_prevLeft)) Left = _prevLeft;
                if (double.IsFinite(_prevTop)) Top = _prevTop;
                Width = double.IsFinite(_prevWidth) && _prevWidth >= MinWidth ? _prevWidth : DefaultWindowWidth;
                Height = double.IsFinite(_prevHeight) && _prevHeight >= MinHeight ? _prevHeight : DefaultWindowHeight;

                _fullscreen = false;
                SetThreadExecutionState(ES_CONTINUOUS);
                Cursor = null;
                ShowControlBar();
                _hideBarTimer.Stop();
                _hideBarTimer.Start();
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: CloseOrHideVisualizer(); break;
                case Key.F11: ToggleFullscreen(); break;
                case Key.M: CycleMode(); break;
            }
        }

        private DateTime _lastRenderTime = DateTime.MinValue;
        private TimeSpan _lastRenderingTime = TimeSpan.Zero;
        private double _frameAccumulatorMs;
        private const double BaseFrameMs = 1000.0 / 60.0;
        private const double PlayingFullscreenFrameMs = 1000.0 / 40.0;
        private const double PlayingWindowedFrameMs = 1000.0 / 30.0;
        private const double HeavyModeWindowedFrameMs = 1000.0 / 22.0;
        private const double SmallWindowFrameMs = 1000.0 / 20.0;
        private const double IdleFrameMs = 1000.0 / 10.0;

        private double GetTargetFrameMs()
        {
            if (!_provider.IsPlaying) return IdleFrameMs;
            if (_fullscreen) return PlayingFullscreenFrameMs;

            double targetMs = PlayingWindowedFrameMs;
            if (_mode is VisualizerMode.DancerSpectrum or VisualizerMode.MagmaFlow)
                targetMs = Math.Max(targetMs, HeavyModeWindowedFrameMs);

            double area = ActualWidth * ActualHeight;
            if (area > 1 && area < 520_000)
                targetMs = Math.Max(targetMs, SmallWindowFrameMs);

            return targetMs;
        }

        // ─── Главный цикл рендеринга ──────────────────────────────────────────────
        private void OnRender(object? sender, EventArgs e)
        {
            if (!IsVisible) return;
            double targetFrameMs = GetTargetFrameMs();

            double elapsedMs;
            if (e is RenderingEventArgs re)
            {
                if (_lastRenderingTime == TimeSpan.Zero)
                {
                    elapsedMs = targetFrameMs;
                }
                else
                {
                    elapsedMs = (re.RenderingTime - _lastRenderingTime).TotalMilliseconds;
                    if (elapsedMs <= 0) return;
                }
                _lastRenderingTime = re.RenderingTime;
            }
            else
            {
                var now = DateTime.UtcNow;
                elapsedMs = _lastRenderTime == DateTime.MinValue
                    ? targetFrameMs
                    : (now - _lastRenderTime).TotalMilliseconds;
                _lastRenderTime = now;
            }

            _frameAccumulatorMs = Math.Min(_frameAccumulatorMs + elapsedMs, targetFrameMs * 4.0);
            if (_frameAccumulatorMs < targetFrameMs) return;

            double frameMs = _frameAccumulatorMs;
            _frameAccumulatorMs %= targetFrameMs;

            double dt = Math.Min(frameMs / 1000.0, 0.05);
            _time += dt;

            double W = _canvas.ActualWidth;
            double H = _canvas.ActualHeight;
            if (W < 1 || H < 1) return;

            _trackLabel.Text = _provider.CurrentTrackTitle;
            _artistLabel.Text = _provider.CurrentArtist.ToUpperInvariant();

            UpdateFft(dt);
            UpdateBackground(W, H);

            switch (_mode)
            {
                case VisualizerMode.SpectrumBars: RenderBars(W, H); break;
                case VisualizerMode.SpectrumRings: RenderSpectrumRings(W, H); break;
                case VisualizerMode.Aurora: RenderAurora(W, H); break;
                case VisualizerMode.LiquidWave: RenderLiquidWave(W, H); break;
                case VisualizerMode.RibbonWaves: RenderRibbonWaves(W, H); break;
                case VisualizerMode.CircleEQ: RenderCircleEQ(W, H); break;
                case VisualizerMode.DancerSpectrum: RenderDancerSpectrum(W, H); break;
                case VisualizerMode.MagmaFlow: RenderMagmaFlow(W, H); break;
            }
        }

        // ─── FFT ──────────────────────────────────────────────────────────────────
        private void UpdateFft(double dt)
        {
            var raw = _provider.GetFftData();
            bool active = _provider.IsPlaying && raw != null;
            int len = Math.Min(_smoothFft.Length, raw?.Length ?? 0);

            float frameScale = (float)Math.Clamp((dt * 1000.0) / BaseFrameMs, 0.25, 4.0);
            float riseKeep = MathF.Pow(0.25f, frameScale);
            float fallKeep = MathF.Pow(0.86f, frameScale);
            float peakKeep = MathF.Pow(0.982f, frameScale);

            for (int i = 0; i < _smoothFft.Length; i++)
            {
                float v = active && i < len && float.IsFinite(raw![i]) ? Math.Max(0f, raw[i]) : 0f;
                _smoothFft[i] = v > _smoothFft[i]
                    ? _smoothFft[i] * riseKeep + v * (1f - riseKeep)
                    : _smoothFft[i] * fallKeep + v * (1f - fallKeep);
                _peakFft[i] = Math.Max(_peakFft[i] * peakKeep, _smoothFft[i]);
            }
            _bassEnergy = BandEnergy(0, 6);
            _midEnergy = BandEnergy(6, 60);
            _highEnergy = BandEnergy(60, 200);
        }

        private float BandEnergy(int from, int to)
        {
            float s = 0;
            int e = Math.Min(to, _smoothFft.Length);
            for (int i = from; i < e; i++) s += _smoothFft[i];
            return s / Math.Max(1, e - from);
        }

        // ─── Фон ──────────────────────────────────────────────────────────────────
        private void UpdateBackground(double W, double H)
        {
            _bgRect.Width = W;
            _bgRect.Height = H;
            float b = Math.Clamp(_bassEnergy * 55f, 0f, 1f);
            float m = Math.Clamp(_midEnergy * 30f, 0f, 1f);
            _bgBrush.GradientStops[0].Color = Color.FromRgb(
                ClampByte(0x03 + b * 0x1A), 0x00, ClampByte(0x0E + b * 0x1C + m * 0x0A));
            _bgBrush.GradientStops[1].Color = Color.FromRgb(
                ClampByte(0x08 + b * 0x10), 0x00, ClampByte(0x1C + b * 0x18));
            _bgBrush.GradientStops[2].Color = Color.FromRgb(
                0x00, ClampByte(m * 0x08), ClampByte(0x10 + b * 0x14));
        }

        // ─── 1. SPECTRUM BARS ─────────────────────────────────────────────────────
        private void RenderBars(double W, double H)
        {
            double gap = 1.5;
            double barW = Math.Max(2, (W - gap * (BarCount + 1)) / BarCount);
            double maxH = H * 0.88;
            double floor = H - 4;
            double hueShift = (_time * 0.025) % 1.0;

            _barEnvTop.Points.Clear();
            _barEnvBot.Points.Clear();

            for (int i = 0; i < BarCount; i++)
            {
                double t = (double)i / BarCount;
                int bin = Math.Clamp((int)Math.Pow(_smoothFft.Length * 0.65, t), 0, _smoothFft.Length - 1);
                float mag = _smoothFft[bin];

                double target = Math.Clamp(mag * maxH * 8.5, 1, maxH);
                _barH[i] = _barH[i] < target
                    ? _barH[i] * 0.20 + target * 0.80
                    : _barH[i] * 0.83 + target * 0.17;

                if (target > _peakH[i]) _peakH[i] = target;
                else _peakH[i] = Math.Max(1, _peakH[i] - 0.7);

                double x = gap + i * (barW + gap);
                double bh = Math.Max(1, _barH[i]);
                double cx = x + barW * 0.5;

                double barHue = (t * 0.70 + hueShift) % 1.0;
                var lgb = _lgbBars[i];
                lgb.GradientStops[0].Color = HsvToColor((barHue + 0.18) % 1.0, 0.95, 0.45);
                lgb.GradientStops[1].Color = HsvToColor(barHue, 0.80, 1.0);

                var bar = _bars[i];
                bar.Width = barW;
                bar.Height = bh;
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, floor - bh);
                bar.Opacity = Math.Clamp(0.55 + mag * 9, 0.40, 1.0);

                double mh = Math.Min(bh * 0.18, H * 0.10);
                var mirror = _mirrorBars[i];
                mirror.Width = barW;
                mirror.Height = mh;
                Canvas.SetLeft(mirror, x);
                Canvas.SetTop(mirror, floor);
                mirror.Opacity = Math.Clamp(0.04 + mag * 1.5, 0.02, 0.15);

                var pk = _peakDots[i];
                pk.Width = barW;
                Canvas.SetLeft(pk, x);
                Canvas.SetTop(pk, floor - _peakH[i] - 3);

                _barEnvTop.Points.Add(new Point(cx, floor - bh));
                _barEnvBot.Points.Add(new Point(cx, floor - bh * 0.25));
            }

            double envHue = (hueShift * 3.0) % 1.0;
            _lgbBarEnvTop.GradientStops[0].Color = HsvToColor(envHue, 0.70, 1.0);
            _lgbBarEnvTop.GradientStops[1].Color = HsvToColor((envHue + 0.25) % 1.0, 0.80, 1.0);
            _lgbBarEnvTop.GradientStops[2].Color = HsvToColor((envHue + 0.50) % 1.0, 0.85, 1.0);
            _lgbBarEnvTop.GradientStops[3].Color = HsvToColor((envHue + 0.75) % 1.0, 0.70, 1.0);

            _lgbBarEnvBot.GradientStops[0].Color = HsvToColor((envHue + 0.50) % 1.0, 0.60, 0.9);
            _lgbBarEnvBot.GradientStops[1].Color = HsvToColor((envHue + 0.75) % 1.0, 0.70, 0.9);
        }

        // ─── 2. SPECTRUM RINGS ────────────────────────────────────────────────────
        private void RenderSpectrumRings(double W, double H)
        {
            double cx = W * 0.5, cy = H * 0.5;
            double maxR = Math.Min(W, H) * 0.46;

            for (int i = 0; i < RingCount; i++)
            {
                double t = (double)i / RingCount;
                int bin = Math.Clamp((int)Math.Pow(220.0, t), 0, _smoothFft.Length - 1);
                float mag = _smoothFft[bin];
                double r = Math.Max(0, maxR * t
                    + mag * maxR * 0.40
                    + Math.Sin(_time * 0.8 + t * Math.PI * 1.5) * _bassEnergy * maxR * 0.06);

                double hue = (t * 0.7 + _time * 0.05) % 1.0;
                var col = HsvToColor(hue, 0.8, 0.6 + mag * 4.0);
                byte al = ClampByte(40 + t * 160 + mag * 220);

                var glow = _ringGlows[i];
                glow.Width = r * 2 + 20; glow.Height = r * 2 + 20;
                Canvas.SetLeft(glow, cx - r - 10); Canvas.SetTop(glow, cy - r - 10);
                _scbRingGlows[i].Color = Color.FromArgb((byte)(al / 4), col.R, col.G, col.B);
                glow.StrokeThickness = 14 + mag * 22;

                var el = _rings[i];
                el.Width = r * 2; el.Height = r * 2;
                Canvas.SetLeft(el, cx - r); Canvas.SetTop(el, cy - r);
                _scbRings[i].Color = Color.FromArgb(al, col.R, col.G, col.B);
                el.StrokeThickness = 1.0 + mag * 7.0;
            }
        }

        // ─── 3. AURORA ────────────────────────────────────────────────────────────
        private void EnsureAurora()
        {
            if (_auroraBuilt) return;
            _auroraBuilt = true;
            for (int i = 0; i < AuroraLineCount; i++)
            {
                _scbAuroraLines[i] = new SolidColorBrush();
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round, Stroke = _scbAuroraLines[i] };
                _canvas.Children.Add(pl);
                _auroraLines[i] = pl;
            }
        }

        private void RenderAurora(double W, double H)
        {
            EnsureAurora();
            foreach (var pl in _auroraLines) pl.Visibility = Visibility.Visible;

            double bassBoost = 1.0 + _bassEnergy * 7.0;
            double midBoost = 1.0 + _midEnergy * 5.0;

            for (int li = 0; li < AuroraLineCount; li++)
            {
                double layerT = (double)li / AuroraLineCount;
                double baseY = H * (0.10 + layerT * 0.65);
                double hue = (0.35 + layerT * 0.35 + _time * 0.02) % 1.0;
                double amp = H * (0.035 + layerT * 0.12) * bassBoost;
                double freq = 1.5 + layerT * 3.0;
                double phase = _time * (0.35 + layerT * 0.55) + li * 0.9;
                double sat = 0.55 + layerT * 0.30;
                double bright = 0.7 + _bassEnergy * 0.3;
                byte alpha = ClampByte(18 + layerT * 110 + _bassEnergy * 95);

                int fftBin = Math.Clamp((int)(layerT * 140), 0, _smoothFft.Length - 1);
                double fftAmp = _smoothFft[fftBin] * H * 0.40 * midBoost;

                var pts = _auroraPoints[li];
                pts.Clear();
                for (int xi = 0; xi < AuroraPointCount; xi++)
                {
                    double t = (double)xi / (AuroraPointCount - 1);
                    double x = t * W;
                    double y = baseY
                        + Math.Sin(t * Math.PI * freq + phase) * amp
                        + Math.Sin(t * Math.PI * freq * 2.1 + phase * 1.5) * amp * 0.45
                        + Math.Sin(t * Math.PI * freq * 0.6 + phase * 0.7) * amp * 0.30
                        + _smoothFft[Math.Clamp((int)(t * 220), 0, _smoothFft.Length - 1)]
                          * fftAmp * Math.Sin(t * Math.PI);
                    pts.Add(new Point(x, y));
                }

                var col = HsvToColor(hue, sat, bright);
                var pl = _auroraLines[li];
                pl.Points = pts;
                _scbAuroraLines[li].Color = Color.FromArgb(alpha, col.R, col.G, col.B);
                pl.StrokeThickness = 3.0 + layerT * 10.0 + _bassEnergy * 8.0;
            }
        }

        // ─── 4. LIQUID WAVE ───────────────────────────────────────────────────────
        private void RenderLiquidWave(double W, double H)
        {
            foreach (var pl in _liquidLines) pl.Visibility = Visibility.Visible;

            for (int li = 0; li < LiquidWaveCount; li++)
            {
                double layerT = (double)li / LiquidWaveCount;
                double baseY = H * (0.25 + layerT * 0.55);
                double hue = (layerT * 0.70 + _time * 0.03) % 1.0;
                double amp = H * (0.05 + layerT * 0.15) * (1 + _bassEnergy * 5.0);
                double freq = 1.0 + layerT * 2.5;
                double phase = _time * (0.40 + layerT * 0.60) + li * 1.1;
                int fftBin = Math.Clamp((int)(layerT * 180), 0, _smoothFft.Length - 1);
                double fftAmp = _smoothFft[fftBin] * H * 0.35 * (1 + _midEnergy * 4.0);
                double thick = 1.5 + layerT * 6.0 + _smoothFft[fftBin] * 12.0;
                byte alpha = ClampByte(25 + layerT * 140 + _bassEnergy * 80);

                var pts = _liquidPoints[li];
                pts.Clear();
                for (int xi = 0; xi < LiquidWavePts; xi++)
                {
                    double t = (double)xi / (LiquidWavePts - 1);
                    double x = t * W;
                    double y = baseY
                        + Math.Sin(t * Math.PI * freq + phase) * amp
                        + Math.Sin(t * Math.PI * freq * 2.3 + phase * 1.4) * amp * 0.35
                        + _smoothFft[Math.Clamp((int)(t * 200), 0, _smoothFft.Length - 1)]
                          * fftAmp * Math.Sin(t * Math.PI);
                    pts.Add(new Point(x, y));
                }

                var col = HsvToColor(hue, 0.88, 1.0);
                var col2 = HsvToColor((hue + 0.15) % 1.0, 0.80, 0.9);
                var pl = _liquidLines[li];
                pl.Points = pts;
                var lgb = _lgbLiquid[li];
                lgb.GradientStops[0].Color = Color.FromArgb(alpha, col.R, col.G, col.B);
                lgb.GradientStops[1].Color = Color.FromArgb(alpha, col2.R, col2.G, col2.B);
                pl.StrokeThickness = Math.Max(0.6, thick);
            }
        }

        // ─── 5. RIBBON WAVES ──────────────────────────────────────────────────────
        private void RenderRibbonWaves(double W, double H)
        {
            foreach (var pl in _ribbonLines) pl.Visibility = Visibility.Visible;

            for (int li = 0; li < RibbonCount; li++)
            {
                double layerT = (double)li / RibbonCount;
                double baseY = H * (0.15 + layerT * 0.72);
                double baseHue = (layerT * 0.60 + _time * 0.025) % 1.0;
                double amp = H * (0.04 + layerT * 0.12) * (1 + _bassEnergy * 6.0);
                double freq = 1.2 + layerT * 3.5;
                double phase = _time * (0.30 + layerT * 0.65) + li * 0.75;
                int fftBin = Math.Clamp((int)(layerT * 200), 0, _smoothFft.Length - 1);
                double fftAmp = _smoothFft[fftBin] * H * 0.38 * (1 + _midEnergy * 3.0);
                double thick = 1.0 + layerT * 5.5 + _smoothFft[fftBin] * 10.0;
                byte alpha = ClampByte(20 + layerT * 150 + _bassEnergy * 85);

                var pts = _ribbonPoints[li];
                pts.Clear();
                for (int xi = 0; xi < RibbonPts; xi++)
                {
                    double t = (double)xi / (RibbonPts - 1);
                    double x = t * W;
                    double y = baseY
                        + Math.Sin(t * Math.PI * freq + phase) * amp
                        + Math.Sin(t * Math.PI * freq * 2.2 + phase * 1.6) * amp * 0.40
                        + Math.Sin(t * Math.PI * freq * 0.51 + phase * 0.7) * amp * 0.22
                        + _smoothFft[Math.Clamp((int)(t * 180), 0, _smoothFft.Length - 1)]
                          * fftAmp * Math.Sin(t * Math.PI);
                    pts.Add(new Point(x, y));
                }

                var colL = HsvToColor(baseHue, 0.90, 1.0);
                var colM = HsvToColor((baseHue + 0.12) % 1.0, 0.85, 1.0);

                var pl = _ribbonLines[li];
                pl.Points = pts;
                var lgbR = _lgbRibbon[li];
                lgbR.GradientStops[0].Color = Color.FromArgb(alpha, colL.R, colL.G, colL.B);
                lgbR.GradientStops[1].Color = Color.FromArgb(alpha, colM.R, colM.G, colM.B);
                pl.StrokeThickness = Math.Max(0.6, thick);
            }
        }

        // ─── 6. CIRCLE EQ ─────────────────────────────────────────────────────────
        private void RenderCircleEQ(double W, double H)
        {
            foreach (var ln in _circBars) ln.Visibility = Visibility.Visible;
            foreach (var el in _circRings) el.Visibility = Visibility.Visible;
            foreach (var el in _circRingGlows) el.Visibility = Visibility.Visible;
            _circCenter.Visibility = Visibility.Visible;

            double cx = W * 0.5;
            double cy = H * 0.5;
            double minDim = Math.Min(W, H);
            double baseR = minDim * 0.26;
            double maxBarL = minDim * 0.22;
            double bassBoost = 1.0 + _bassEnergy * 5.0;

            for (int i = 0; i < CircleBarCount; i++)
            {
                double t = (double)i / CircleBarCount;
                double angle = t * Math.PI * 2 - Math.PI / 2;

                int fftBin = Math.Clamp((int)Math.Pow(_smoothFft.Length * 0.70, t), 0, _smoothFft.Length - 1);
                float mag = _smoothFft[fftBin];

                double targetH = Math.Clamp(mag * maxBarL * 9.0, 1.0, maxBarL) * bassBoost;
                _circBarH[i] = _circBarH[i] < targetH
                    ? _circBarH[i] * 0.18 + targetH * 0.82
                    : _circBarH[i] * 0.84 + targetH * 0.16;
                double barLen = Math.Max(1.5, _circBarH[i]);

                double cosA = Math.Cos(angle);
                double sinA = Math.Sin(angle);
                double innerR = baseR;
                double outerR = baseR + barLen;

                var ln = _circBars[i];
                ln.X1 = cx + cosA * innerR; ln.Y1 = cy + sinA * innerR;
                ln.X2 = cx + cosA * outerR; ln.Y2 = cy + sinA * outerR;

                double hue = (t * 0.65 + _time * 0.04 + _bassEnergy * 0.2) % 1.0;
                double sat = 0.80 + mag * 0.18;
                double val = Math.Clamp(0.6 + mag * 5.0, 0, 1.0);
                var col = HsvToColor(hue, sat, val);
                byte al = ClampByte(80 + mag * 220 + _bassEnergy * 80);

                _scbCircBars[i].Color = Color.FromArgb(al, col.R, col.G, col.B);
                ln.StrokeThickness = Math.Max(1.2, 2.0 + mag * 4.0);
            }

            double[] ringR = { baseR * 0.72, baseR * 1.02, baseR + maxBarL * 0.45, baseR + maxBarL * 0.90 };
            for (int ri = 0; ri < CircleRingLines; ri++)
            {
                double r = ringR[ri] + Math.Sin(_time * 1.5 + ri * 1.1) * _bassEnergy * baseR * 0.05;
                double hue = (ri * 0.25 + _time * 0.05) % 1.0;
                var col = HsvToColor(hue, 0.80, 0.9);
                byte al = ClampByte(60 + ri * 35 + _bassEnergy * 100);

                var glow = _circRingGlows[ri];
                glow.Width = r * 2 + 16; glow.Height = r * 2 + 16;
                Canvas.SetLeft(glow, cx - r - 8); Canvas.SetTop(glow, cy - r - 8);
                _scbCircRingGlows[ri].Color = Color.FromArgb((byte)(al / 3), col.R, col.G, col.B);
                glow.StrokeThickness = 10 + _bassEnergy * 12;

                var ring = _circRings[ri];
                ring.Width = r * 2; ring.Height = r * 2;
                Canvas.SetLeft(ring, cx - r); Canvas.SetTop(ring, cy - r);
                _scbCircRings[ri].Color = Color.FromArgb(al, col.R, col.G, col.B);
                ring.StrokeThickness = 1.0 + _bassEnergy * 2.0;
            }

            double cr = baseR * (0.35 + _bassEnergy * 0.18);
            _circCenter.Width = cr * 2; _circCenter.Height = cr * 2;
            Canvas.SetLeft(_circCenter, cx - cr); Canvas.SetTop(_circCenter, cy - cr);
            double cHue = (_time * 0.06) % 1.0;
            Color cCol = HsvToColor(cHue, 0.7, 1.0);
            _rgbCircCenter.GradientStops[0].Color = Color.FromArgb(ClampByte(120 + _bassEnergy * 135), cCol.R, cCol.G, cCol.B);
            _rgbCircCenter.GradientStops[1].Color = Color.FromArgb(0, cCol.R, cCol.G, cCol.B);
        }

        // ─── 7. DANCER SPECTRUM ───────────────────────────────────────────────────
        private void RenderDancerSpectrum(double W, double H)
        {
            if (!_dancerBuilt) BuildDancerSpectrum();

            foreach (var b in _dBarsL) b.Visibility = Visibility.Visible;
            foreach (var b in _dBarsR) b.Visibility = Visibility.Visible;
            foreach (var p in _dPeaksL) p.Visibility = Visibility.Visible;
            foreach (var p in _dPeaksR) p.Visibility = Visibility.Visible;
            foreach (var a in _dancerAuras) a.Visibility = Visibility.Visible;
            foreach (var r in _dancerRays) r.Visibility = Visibility.Visible;
            _dancerBody.Visibility = Visibility.Visible;
            _dancerGlow.Visibility = Visibility.Visible;
            _dancerHead.Visibility = Visibility.Visible;
            _dancerHeadG.Visibility = Visibility.Visible;
            _dancerGroundWave.Visibility = Visibility.Visible;

            double cx = W * 0.5;
            double cy = H * 0.5;
            double specW = W * 0.33;
            double dancerCx = cx;

            // ─── ЛЕВЫЙ СПЕКТР ────────────────────────────────────────────────────
            {
                double barW = Math.Max(2, specW / DancerBarL);
                double gap = 1.0;
                double maxH = H * 0.85;
                double floor = H - 6;
                double hueShift = (_time * 0.02) % 1.0;

                for (int i = 0; i < DancerBarL; i++)
                {
                    double t = (double)(DancerBarL - 1 - i) / DancerBarL;
                    int bin = Math.Clamp((int)(t * 80), 0, _smoothFft.Length - 1);
                    float mag = _smoothFft[bin];

                    double target = Math.Clamp(mag * maxH * 9.0, 1, maxH);
                    _dBarHL[i] = _dBarHL[i] < target
                        ? _dBarHL[i] * 0.18 + target * 0.82
                        : _dBarHL[i] * 0.84 + target * 0.16;
                    double bh = Math.Max(1, _dBarHL[i]);

                    if (target > _dPeakHL[i]) _dPeakHL[i] = target;
                    else _dPeakHL[i] = Math.Max(1, _dPeakHL[i] - 0.65);

                    double x = Math.Max(0, specW - (i + 1) * (barW + gap) + gap);

                    double barHue = (0.55 + t * 0.25 + hueShift) % 1.0;
                    _lgbDBarsL[i].GradientStops[0].Color = HsvToColor((barHue + 0.12) % 1.0, 0.95, 0.35);
                    _lgbDBarsL[i].GradientStops[1].Color = HsvToColor(barHue, 0.85, 1.0);

                    var bar = _dBarsL[i];
                    bar.Width = barW; bar.Height = bh;
                    Canvas.SetLeft(bar, x); Canvas.SetTop(bar, floor - bh);
                    bar.Opacity = Math.Clamp(0.50 + mag * 10, 0.35, 1.0);

                    var pk = _dPeaksL[i];
                    pk.Width = barW;
                    Canvas.SetLeft(pk, x); Canvas.SetTop(pk, floor - _dPeakHL[i] - 2);
                }
            }

            // ─── ПРАВЫЙ СПЕКТР ───────────────────────────────────────────────────
            {
                double barW = Math.Max(2, specW / DancerBarR);
                double gap = 1.0;
                double maxH = H * 0.85;
                double floor = H - 6;
                double offsetX = W * 0.67;
                double hueShift = (_time * 0.02) % 1.0;

                for (int i = 0; i < DancerBarR; i++)
                {
                    double t = (double)i / DancerBarR;
                    int bin = Math.Clamp(130 + (int)(t * 320), 0, _smoothFft.Length - 1);
                    float mag = _smoothFft[bin];

                    double target = Math.Clamp(mag * maxH * 12.0, 1, maxH);
                    _dBarHR[i] = _dBarHR[i] < target
                        ? _dBarHR[i] * 0.18 + target * 0.82
                        : _dBarHR[i] * 0.84 + target * 0.16;
                    double bh = Math.Max(1, _dBarHR[i]);

                    if (target > _dPeakHR[i]) _dPeakHR[i] = target;
                    else _dPeakHR[i] = Math.Max(1, _dPeakHR[i] - 0.65);

                    double x = offsetX + i * (barW + gap);
                    double barHue = (t * 0.25 + hueShift) % 1.0;
                    _lgbDBarsR[i].GradientStops[0].Color = HsvToColor((barHue + 0.10) % 1.0, 0.95, 0.35);
                    _lgbDBarsR[i].GradientStops[1].Color = HsvToColor(barHue, 0.90, 1.0);

                    var bar = _dBarsR[i];
                    bar.Width = barW; bar.Height = bh;
                    Canvas.SetLeft(bar, x); Canvas.SetTop(bar, floor - bh);
                    bar.Opacity = Math.Clamp(0.50 + mag * 10, 0.35, 1.0);

                    var pk = _dPeaksR[i];
                    pk.Width = barW;
                    Canvas.SetLeft(pk, x); Canvas.SetTop(pk, floor - _dPeakHR[i] - 2);
                }
            }

            // ─── КИНЕМАТИКА ТАНЦОРА ─────────────────────────────────────────────
            double bodyScale = Math.Min(W, H) * 0.46;
            double headR = bodyScale * 0.10;
            double torsoLen = bodyScale * 0.28;
            double armLen = bodyScale * 0.26;
            double legLen = bodyScale * 0.32;

            double neckX = dancerCx;
            double neckY = cy - bodyScale * 0.05 + Math.Sin(_time * 2.0) * _bassEnergy * bodyScale * 0.06;

            _posePhase += 0.016 * (1.0 + _bassEnergy * 14.0 + _midEnergy * 6.0);

            double sway = Math.Sin(_posePhase * 0.8) * 0.15;
            double bounce = Math.Abs(Math.Sin(_posePhase * 1.6)) * bodyScale * 0.04;

            _jointX[0] = neckX + sway * bodyScale * 0.08;
            _jointY[0] = neckY - bounce;

            double hipX = neckX - sway * bodyScale * 0.06;
            double hipY = neckY + torsoLen;

            double shoulderSpread = bodyScale * 0.18 + Math.Sin(_posePhase * 0.7) * bodyScale * 0.04;
            _jointX[1] = _jointX[0] - shoulderSpread;
            _jointY[1] = _jointY[0] + bodyScale * 0.04;
            _jointX[2] = _jointX[0] + shoulderSpread;
            _jointY[2] = _jointY[1];

            double armPhase = _posePhase * 1.05;
            double arm1Ang = Math.Sin(armPhase) * 0.9 + Math.Sin(armPhase * 1.7) * 0.3 - 0.4;
            double arm2Ang = Math.Sin(armPhase + Math.PI) * 0.9 + 0.4;

            _jointX[3] = _jointX[1] + Math.Cos(Math.PI + arm1Ang) * armLen * 0.5;
            _jointY[3] = _jointY[1] + Math.Sin(Math.PI + arm1Ang) * armLen * 0.5;
            _jointX[5] = _jointX[3] + Math.Cos(Math.PI + arm1Ang + Math.Sin(_posePhase * 1.3) * 0.6) * armLen * 0.55;
            _jointY[5] = _jointY[3] + Math.Sin(Math.PI + arm1Ang + Math.Sin(_posePhase * 1.3) * 0.6) * armLen * 0.55;

            _jointX[4] = _jointX[2] + Math.Cos(arm2Ang) * armLen * 0.5;
            _jointY[4] = _jointY[2] + Math.Sin(arm2Ang) * armLen * 0.5;
            _jointX[6] = _jointX[4] + Math.Cos(arm2Ang + Math.Sin(_posePhase * 1.4) * 0.7) * armLen * 0.55;
            _jointY[6] = _jointY[4] + Math.Sin(arm2Ang + Math.Sin(_posePhase * 1.4) * 0.7) * armLen * 0.55;

            double legPhase = _posePhase * 1.25;
            _jointX[7] = hipX - bodyScale * 0.09;
            _jointY[7] = hipY;
            _jointX[8] = hipX + bodyScale * 0.09;
            _jointY[8] = hipY;

            double legAng1 = Math.Sin(legPhase) * 0.7 + 0.2;
            double legAng2 = Math.Sin(legPhase + Math.PI) * 0.7 - 0.2;
            _jointX[9] = _jointX[7] + Math.Sin(legAng1) * legLen * 0.5;
            _jointY[9] = _jointY[7] + Math.Cos(legAng1) * legLen * 0.5;
            _jointX[10] = _jointX[8] + Math.Sin(legAng2) * legLen * 0.5;
            _jointY[10] = _jointY[8] + Math.Cos(legAng2) * legLen * 0.5;

            _jointX[11] = _jointX[9] + Math.Sin(legAng1 * 0.7) * legLen * 0.52;
            _jointY[11] = _jointY[9] + Math.Cos(legAng1 * 0.7) * legLen * 0.52;
            _jointX[12] = _jointX[10] + Math.Sin(legAng2 * 0.7) * legLen * 0.52;
            _jointY[12] = _jointY[10] + Math.Cos(legAng2 * 0.7) * legLen * 0.52;

            if (!_dancerJointsInitialized)
            {
                _dancerJointsInitialized = true;
                for (int j = 0; j < 13; j++)
                {
                    _jointX2[j] = _jointX[j];
                    _jointY2[j] = _jointY[j];
                }
            }
            else
            {
                for (int j = 0; j < 13; j++)
                {
                    _jointX2[j] = _jointX2[j] * 0.55 + _jointX[j] * 0.45;
                    _jointY2[j] = _jointY2[j] * 0.55 + _jointY[j] * 0.45;
                }
            }

            double dancerHue = (_time * 0.07) % 1.0;
            var dancerCol = HsvToColor(dancerHue, 0.90, 1.0);
            var dancerCol2 = HsvToColor((dancerHue + 0.4) % 1.0, 0.85, 1.0);
            byte dancerAlpha = ClampByte(180 + _bassEnergy * 75);

            var pg = new PathGeometry();
            void AddSegment(int a, int b)
            {
                var seg = new PathFigure { StartPoint = new Point(_jointX2[a], _jointY2[a]) };
                seg.Segments.Add(new LineSegment(new Point(_jointX2[b], _jointY2[b]), true));
                pg.Figures.Add(seg);
            }

            var spine = new PathFigure { StartPoint = new Point(_jointX2[0], _jointY2[0]) };
            spine.Segments.Add(new BezierSegment(
                new Point(hipX + sway * bodyScale * 0.12, neckY + torsoLen * 0.35),
                new Point(hipX - sway * bodyScale * 0.12, neckY + torsoLen * 0.65),
                new Point(hipX, hipY), true));
            pg.Figures.Add(spine);

            AddSegment(1, 0); AddSegment(0, 2);
            AddSegment(1, 3); AddSegment(3, 5);
            AddSegment(2, 4); AddSegment(4, 6);
            AddSegment(7, 8);
            AddSegment(7, 9); AddSegment(9, 11);
            AddSegment(8, 10); AddSegment(10, 12);

            _dancerGlow.Data = pg;
            _lgbDancerGlow.GradientStops[0].Color = Color.FromArgb((byte)(dancerAlpha / 3), dancerCol.R, dancerCol.G, dancerCol.B);
            _lgbDancerGlow.GradientStops[1].Color = Color.FromArgb((byte)(dancerAlpha / 3), dancerCol2.R, dancerCol2.G, dancerCol2.B);
            _dancerGlow.StrokeThickness = 16 + _bassEnergy * 20;
            _dancerGlow.Opacity = Math.Clamp(0.35 + _bassEnergy * 0.45, 0.0, 1.0);
            _dancerGlowBlur.Radius = 10 + _bassEnergy * 18;

            _dancerBody.Data = pg;
            _lgbDancerBody.GradientStops[0].Color = Color.FromArgb(dancerAlpha, dancerCol.R, dancerCol.G, dancerCol.B);
            _lgbDancerBody.GradientStops[1].Color = Color.FromArgb(dancerAlpha, dancerCol2.R, dancerCol2.G, dancerCol2.B);
            _dancerBody.StrokeThickness = 3.0 + _bassEnergy * 2.0;

            double hx = _jointX2[0] + Math.Sin(_posePhase * 0.6) * headR * 0.4;
            double hy = _jointY2[0] - headR * 1.1;

            _dancerHeadG.Width = headR * 3; _dancerHeadG.Height = headR * 3;
            Canvas.SetLeft(_dancerHeadG, hx - headR * 1.5); Canvas.SetTop(_dancerHeadG, hy - headR * 1.5);
            _scbDancerHeadG.Color = Color.FromArgb((byte)(dancerAlpha / 2), dancerCol.R, dancerCol.G, dancerCol.B);
            _dancerHeadG.StrokeThickness = 8 + _bassEnergy * 14;
            _dancerHeadGBlur.Radius = 8 + _bassEnergy * 12;

            _dancerHead.Width = headR * 2; _dancerHead.Height = headR * 2;
            Canvas.SetLeft(_dancerHead, hx - headR); Canvas.SetTop(_dancerHead, hy - headR);
            _scbDancerHead.Color = Color.FromArgb(dancerAlpha, dancerCol.R, dancerCol.G, dancerCol.B);
            _dancerHead.StrokeThickness = 3.0 + _bassEnergy * 2.0;

            for (int ri = 0; ri < DancerRayCount; ri++)
            {
                double rayAng = ri * Math.PI * 2 / DancerRayCount + _time * 0.6;
                double rayLen = bodyScale * (0.25 + _bassEnergy * 1.2 + _midEnergy * 0.5)
                              * (0.6 + 0.4 * Math.Sin(_posePhase * 1.1 + ri * 0.7));
                double rayHue = (dancerHue + (double)ri / DancerRayCount * 0.5) % 1.0;
                var rayCol = HsvToColor(rayHue, 0.90, 1.0);
                byte rayAl = ClampByte(30 + _bassEnergy * 180 + _midEnergy * 80);

                var ray = _dancerRays[ri];
                ray.X1 = dancerCx; ray.Y1 = cy;
                ray.X2 = dancerCx + Math.Cos(rayAng) * rayLen;
                ray.Y2 = cy + Math.Sin(rayAng) * rayLen;
                var lgbRay = _lgbDancerRays[ri];
                lgbRay.GradientStops[0].Color = Color.FromArgb(rayAl, rayCol.R, rayCol.G, rayCol.B);
                lgbRay.GradientStops[1].Color = Color.FromArgb(0, rayCol.R, rayCol.G, rayCol.B);
                ray.Opacity = Math.Clamp(0.6 + _bassEnergy * 0.4, 0.0, 1.0);
                ray.StrokeThickness = 0.8 + _bassEnergy * 1.5;
            }

            for (int ai = 0; ai < DancerAuraCount; ai++)
            {
                double af = (double)ai / DancerAuraCount;
                double ar = Math.Max(1.0, bodyScale * (0.22 + af * 0.28)
                            + Math.Sin(_time * 2.5 + ai * 1.3) * _bassEnergy * bodyScale * 0.10);
                double aHue = (dancerHue + af * 0.35 + _time * 0.04) % 1.0;
                var aCol = HsvToColor(aHue, 0.80, 0.95);
                byte aAl = ClampByte(20 + (1 - af) * 80 + _bassEnergy * 100);

                var aura = _dancerAuras[ai];
                aura.Width = ar * 2; aura.Height = ar * 2;
                Canvas.SetLeft(aura, dancerCx - ar); Canvas.SetTop(aura, cy - ar);
                _scbDancerAuras[ai].Color = Color.FromArgb(aAl, aCol.R, aCol.G, aCol.B);
                aura.StrokeThickness = 1.0 + _bassEnergy * 3.0;
                _dancerAuraBlurs[ai].Radius = 3 + _bassEnergy * 8;
            }

            {
                double groundY = Math.Max(_jointY2[11], _jointY2[12]) + 5;
                groundY = Math.Min(groundY, H - 10);
                double waveW = W * 0.34;
                double waveX0 = dancerCx - waveW * 0.5;
                double waveAmp = bodyScale * 0.04 + _bassEnergy * bodyScale * 0.12;
                int wPts = 60;

                var gwPts = _groundWavePoints;
                gwPts.Clear();
                for (int xi = 0; xi < wPts; xi++)
                {
                    double t = (double)xi / (wPts - 1);
                    double gx = waveX0 + t * waveW;
                    double env = Math.Sin(t * Math.PI);
                    double gy = groundY
                        + Math.Sin(t * Math.PI * 5.0 + _posePhase * 2.0) * waveAmp * env
                        + Math.Sin(t * Math.PI * 9.0 + _posePhase * 3.1) * waveAmp * 0.4 * env;
                    gwPts.Add(new Point(gx, gy));
                }
                _dancerGroundWave.Points = gwPts;
                double gwHue = (dancerHue + 0.55) % 1.0;
                var gwCol = HsvToColor(gwHue, 0.90, 1.0);
                byte gwAl = ClampByte(80 + _bassEnergy * 170);
                _lgbGroundWave.GradientStops[0].Color = Color.FromArgb(0, gwCol.R, gwCol.G, gwCol.B);
                _lgbGroundWave.GradientStops[1].Color = Color.FromArgb(gwAl, gwCol.R, gwCol.G, gwCol.B);
                _lgbGroundWave.GradientStops[2].Color = Color.FromArgb(0, gwCol.R, gwCol.G, gwCol.B);
            }

            {
                if (_provider.IsPlaying && _dParticles.Count < 120)
                {
                    int[] emitters = { 5, 6, 11, 12, 0 };
                    foreach (int ej in emitters)
                    {
                        if (_rng.NextDouble() < 0.20 + _bassEnergy * 0.6)
                        {
                            double pHue = (_time * 0.08 + (double)ej / 13.0) % 1.0;
                            _dParticles.Add(new DancerParticle
                            {
                                X = _jointX2[ej],
                                Y = _jointY2[ej],
                                Vx = (_rng.NextDouble() - 0.5) * 4.0,
                                Vy = (_rng.NextDouble() - 0.5) * 4.0 - 1.0,
                                Life = 1.0,
                                Decay = 0.015 + _rng.NextDouble() * 0.020,
                                Size = 2.0 + _bassEnergy * 10 + _rng.NextDouble() * 3,
                                Hue = pHue,
                            });
                        }
                    }
                }

                for (int pi = _dParticles.Count - 1; pi >= 0; pi--)
                {
                    var p = _dParticles[pi];
                    p.X += p.Vx; p.Y += p.Vy;
                    p.Vy *= 0.97; p.Vx *= 0.97;
                    p.Life -= p.Decay;
                    if (p.Life <= 0)
                    {
                        if (p.El != null) _canvas.Children.Remove(p.El);
                        _dParticles.RemoveAt(pi);
                        continue;
                    }

                    double sz = Math.Max(0.5, p.Size * p.Life);
                    byte pal = ClampByte(p.Life * 220);
                    var pcol = HsvToColor(p.Hue, 0.90, 1.0);

                    if (p.El == null)
                    {
                        p.Brush = new SolidColorBrush();
                        p.El = new Ellipse { Fill = p.Brush };
                        _canvas.Children.Add(p.El);
                    }
                    p.El.Width = sz;
                    p.El.Height = sz;
                    p.Brush!.Color = Color.FromArgb(pal, pcol.R, pcol.G, pcol.B);

                    if (sz > 6)
                    {
                        if (p.El.Effect is BlurEffect be) be.Radius = sz * 0.5;
                        else p.El.Effect = new BlurEffect { Radius = sz * 0.5 };
                    }
                    else
                    {
                        p.El.Effect = null;
                    }
                    Canvas.SetLeft(p.El, p.X - sz * 0.5);
                    Canvas.SetTop(p.El, p.Y - sz * 0.5);
                }
            }
        }

        // ─── 8. MAGMA FLOW ────────────────────────────────────────────────────────
        private void BuildMagmaFlow()
        {
            _magmaBuilt = true;
            for (int i = 0; i < MagmaLineCount; i++)
            {
                var pl = new Polyline
                {
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Stroke = _lgbMagma[i],
                };
                _canvas.Children.Add(pl);
                _magmaLines[i] = pl;
                _magmaBlurEffects[i] = new BlurEffect { Radius = 2.0 };
            }
        }

        private void RenderMagmaFlow(double W, double H)
        {
            foreach (var pl in _magmaLines) pl.Visibility = Visibility.Visible;

            _bgBrush.GradientStops[0].Color = Color.FromRgb(
                ClampByte(0x08 + _bassEnergy * 0x18), 0x01, 0x01);
            _bgBrush.GradientStops[1].Color = Color.FromRgb(
                ClampByte(0x04 + _bassEnergy * 0x10), 0x00, 0x00);
            _bgBrush.GradientStops[2].Color = Color.FromRgb(
                ClampByte(0x06 + _bassEnergy * 0x0C), 0x01, 0x01);

            double bassBoost = 1.0 + _bassEnergy * 8.0;
            double midBoost = 1.0 + _midEnergy * 4.0;

            for (int li = 0; li < MagmaLineCount; li++)
            {
                double layerT = (double)li / MagmaLineCount;
                double baseY = H * (0.55 + layerT * 0.42);
                double hue = 0.08 - layerT * 0.08;
                double sat = 0.85 + layerT * 0.12;
                double bri = Math.Clamp(1.0 - layerT * 0.55 + _bassEnergy * 0.35, 0.15, 1.0);
                byte alpha = ClampByte(30 + layerT * 160 + _bassEnergy * (80 - layerT * 60));

                double thick = 1.0 + layerT * 14.0 + _smoothFft[
                    Math.Clamp((int)(layerT * 120), 0, _smoothFft.Length - 1)] * 22.0;

                double amp = H * (0.018 + layerT * 0.06) * bassBoost;
                double freq = 2.5 - layerT * 1.2;
                double phase = _time * (0.18 + layerT * 0.28) + li * 0.55;
                double spikeAmp = layerT > 0.5 ? H * _bassEnergy * 0.22 * (layerT - 0.5) * 2.0 : 0;

                int fftBin = Math.Clamp((int)(layerT * 160), 0, _smoothFft.Length - 1);
                double fftPush = _smoothFft[fftBin] * H * 0.28 * midBoost;

                var pts = _magmaPoints[li];
                pts.Clear();
                for (int xi = 0; xi < MagmaPts; xi++)
                {
                    double t = (double)xi / (MagmaPts - 1);
                    double x = t * W;

                    double y = baseY
                        + Math.Sin(t * Math.PI * freq + phase) * amp
                        + Math.Sin(t * Math.PI * freq * 1.7 + phase * 1.3) * amp * 0.45
                        + Math.Sin(t * Math.PI * freq * 3.1 + phase * 2.1) * amp * 0.20;

                    int fb = Math.Clamp((int)(t * 200), 0, _smoothFft.Length - 1);
                    y -= _smoothFft[fb] * fftPush * Math.Sin(t * Math.PI);

                    if (spikeAmp > 0)
                    {
                        for (int sk = 0; sk < 5; sk++)
                        {
                            double spikeX = (sk * 0.19 + layerT * 0.13 + _time * 0.04 * (sk + 1)) % 1.0;
                            double dist = Math.Abs(t - spikeX);
                            if (dist < 0.12)
                                y -= spikeAmp * Math.Pow(1.0 - dist / 0.12, 3.0);
                        }
                    }

                    pts.Add(new Point(x, y));
                }

                var col = HsvToColor(hue, sat, bri);
                var col2 = HsvToColor((hue + 0.04) % 1.0, sat, Math.Min(1.0, bri + 0.2));
                var pl = _magmaLines[li];
                pl.Points = pts;

                var lgbM = _lgbMagma[li];
                lgbM.GradientStops[0].Color = Color.FromArgb((byte)(alpha * 0.4), col.R, col.G, col.B);
                lgbM.GradientStops[1].Color = Color.FromArgb(alpha, col2.R, col2.G, col2.B);
                lgbM.GradientStops[2].Color = Color.FromArgb((byte)(alpha * 0.4), col.R, col.G, col.B);
                pl.StrokeThickness = Math.Max(0.5, thick);

                if (layerT < 0.4)
                {
                    _magmaBlurEffects[li].Radius = 2.5 + _bassEnergy * 6.0;
                    pl.Effect = _magmaBlurEffects[li];
                }
                else if (layerT > 0.75)
                {
                    _magmaBlurEffects[li].Radius = 1.0 + _bassEnergy * 3.0;
                    pl.Effect = _magmaBlurEffects[li];
                }
                else
                {
                    pl.Effect = null;
                }
            }
        }

        // ─── Цвет ─────────────────────────────────────────────────────────────────
        private static byte ClampByte(double v)
        {
            if (!double.IsFinite(v)) return 0;
            if (v <= 0) return 0;
            if (v >= 255) return 255;
            return (byte)v;
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 1.0) + 1.0) % 1.0;
            v = Math.Clamp(v, 0, 1); s = Math.Clamp(s, 0, 1);
            int hi = (int)(h * 6) % 6;
            double f = h * 6 - Math.Floor(h * 6);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            var (r, g, b) = hi switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }

    // ★ Частица вокруг танцора
    internal class DancerParticle
    {
        public double X, Y, Vx, Vy, Life, Decay, Size, Hue;
        public Ellipse? El;
        public SolidColorBrush? Brush;
    }
}

#nullable restore