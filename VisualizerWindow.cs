// VisualizerWindow.cs — Aurora Visualizer v4 ★ ULTRA EDITION ★
// Режимы: SpectrumBars · SpectrumRings · Aurora · LiquidWave
//         RibbonWaves · CircleEQ · DancerSpectrum · MagmaFlow
// Управление: двойной клик / F11 fullscreen · M смена режима · Esc закрыть · перетаскивание мышью

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
        bool     IsPlaying         { get; }
        string   CurrentTrackTitle { get; }
        string   CurrentArtist     { get; }
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
        // ── Win32 для истинного fullscreen поверх панели задач ────────────────────
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        private static readonly IntPtr HWND_TOP   = new IntPtr(0);
        private const uint SWP_SHOWWINDOW          = 0x0040;
        private const uint ES_CONTINUOUS           = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED     = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED      = 0x00000001;

        private readonly IVisualizerDataProvider _provider;
        private Canvas         _canvas  = null!;
        private VisualizerMode _mode    = VisualizerMode.SpectrumBars;
        private bool           _fullscreen;
        private double         _prevLeft, _prevTop, _prevWidth, _prevHeight;

        public event Action<VisualizerMode>? ModeChanged;
        public VisualizerMode CurrentMode
        {
            get => _mode;
            set { _mode = value; ApplyModeVisibility(); _modeLabel.Text = ModeName(_mode); }
        }

        // ── Анимация ──────────────────────────────────────────────────────────────
        private double _time;
        private readonly float[] _smoothFft = new float[512];
        private readonly float[] _peakFft   = new float[512];
        private float _bassEnergy, _midEnergy, _highEnergy;
        private readonly Random _rng = new();

        // ── SpectrumBars ──────────────────────────────────────────────────────────
        private const int BarCount = 90;
        private readonly Rectangle[] _bars       = new Rectangle[BarCount];
        private readonly Rectangle[] _peakDots   = new Rectangle[BarCount];
        private readonly Rectangle[] _mirrorBars = new Rectangle[BarCount];
        private readonly double[]    _barH       = new double[BarCount];
        private readonly double[]    _peakH      = new double[BarCount];

        // ── SpectrumRings ─────────────────────────────────────────────────────────
        private const int RingCount = 36;
        private readonly Ellipse[] _rings     = new Ellipse[RingCount];
        private readonly Ellipse[] _ringGlows = new Ellipse[RingCount];

        // ── Aurora ────────────────────────────────────────────────────────────────
        private const int AuroraLineCount  = 28;
        private const int AuroraPointCount = 120;
        private readonly Polyline[] _auroraLines = new Polyline[AuroraLineCount];
        private bool _auroraBuilt;

        // ── LiquidWave ────────────────────────────────────────────────────────────
        private const int LiquidWaveCount  = 18;
        private const int LiquidWavePts    = 140;
        private readonly Polyline[] _liquidLines = new Polyline[LiquidWaveCount];
        private bool _liquidBuilt;

        // ── RibbonWaves ───────────────────────────────────────────────────────────
        private const int RibbonCount = 22;
        private const int RibbonPts   = 160;
        private readonly Polyline[] _ribbonLines = new Polyline[RibbonCount];
        private bool _ribbonBuilt;

        // ── CircleEQ ──────────────────────────────────────────────────────────────
        private const int CircleBarCount  = 144;
        private const int CircleRingLines = 4;
        private readonly Line[]    _circBars      = new Line[CircleBarCount];
        private readonly Ellipse[] _circRings     = new Ellipse[CircleRingLines];
        private readonly Ellipse[] _circRingGlows = new Ellipse[CircleRingLines];
        private readonly Ellipse   _circCenter    = new();
        private bool _circleBuilt;
        private readonly double[] _circBarH = new double[CircleBarCount];

        // ── SpectrumBars: огибающая волна ─────────────────────────────────────────
        private readonly Polyline _barEnvTop = new();
        private readonly Polyline _barEnvBot = new();

        // ──────────────────────────────────────────────────────────────────────────
        // ★★★ DANCER SPECTRUM — Танцор + двойной спектр ★★★
        // ──────────────────────────────────────────────────────────────────────────
        private bool   _dancerBuilt;

        // Спектр слева (низкие частоты — холодные)
        private const int DancerBarL = 64;
        private readonly Rectangle[] _dBarsL    = new Rectangle[DancerBarL];
        private readonly Rectangle[] _dPeaksL   = new Rectangle[DancerBarL];
        private readonly double[]    _dBarHL     = new double[DancerBarL];
        private readonly double[]    _dPeakHL    = new double[DancerBarL];

        // Спектр справа (высокие частоты — тёплые)
        private const int DancerBarR = 64;
        private readonly Rectangle[] _dBarsR    = new Rectangle[DancerBarR];
        private readonly Rectangle[] _dPeaksR   = new Rectangle[DancerBarR];
        private readonly double[]    _dBarHR     = new double[DancerBarR];
        private readonly double[]    _dPeakHR    = new double[DancerBarR];

        // Силуэт танцора — PathGeometry части тела
        private readonly Path   _dancerBody   = new();   // туловище + ноги + руки
        private readonly Path   _dancerGlow   = new();   // glow-копия
        private readonly Ellipse _dancerHead  = new();   // голова
        private readonly Ellipse _dancerHeadG = new();   // glow головы

        // Частицы вокруг танцора
        private readonly List<DancerParticle> _dParticles = new(120);

        // Лучи от танцора
        private const int DancerRayCount = 24;
        private readonly Line[] _dancerRays = new Line[DancerRayCount];

        // Волна под ногами
        private readonly Polyline _dancerGroundWave = new();

        // ── Кэш BlurEffect для Dancer и Aura — не создаём new каждый кадр ─────
        private readonly BlurEffect _dancerGlowBlur  = new BlurEffect { Radius = 10 };
        private readonly BlurEffect _dancerHeadGBlur = new BlurEffect { Radius = 8  };
        private readonly BlurEffect[] _dancerAuraBlurs = new BlurEffect[DancerAuraCount];

        // ── Кэш SolidColorBrush для горячих путей ────────────────────────────────
        // Переиспользуем один объект, меняем только .Color — ноль аллокаций
        private readonly SolidColorBrush _scbRing     = new SolidColorBrush();
        private readonly SolidColorBrush _scbRingGlow = new SolidColorBrush();
        private readonly SolidColorBrush _scbAurora   = new SolidColorBrush();
        private readonly SolidColorBrush _scbDancerHead  = new SolidColorBrush();
        private readonly SolidColorBrush _scbDancerHeadG = new SolidColorBrush();
        private readonly SolidColorBrush _scbDancerAura  = new SolidColorBrush();
        private readonly SolidColorBrush _scbNeonLg   = new SolidColorBrush();
        private readonly SolidColorBrush _scbNeonLb   = new SolidColorBrush();
        private readonly SolidColorBrush _scbNeonScan = new SolidColorBrush();
        private readonly SolidColorBrush _scbCircBar  = new SolidColorBrush();
        private readonly SolidColorBrush _scbCircGlow = new SolidColorBrush();
        private readonly SolidColorBrush _scbCircRing = new SolidColorBrush();

        // ── Кэш LinearGradientBrush для Dancer (тело, голова-glow, лучи, волна) ──
        private readonly LinearGradientBrush _lgbDancerGlow = new LinearGradientBrush(
            new GradientStopCollection { new GradientStop(), new GradientStop() },
            new Point(0,0), new Point(1,1));
        private readonly LinearGradientBrush _lgbDancerBody = new LinearGradientBrush(
            new GradientStopCollection { new GradientStop(), new GradientStop() },
            new Point(0,0), new Point(1,1));
        private readonly LinearGradientBrush[] _lgbDancerRays = new LinearGradientBrush[DancerRayCount];
        private readonly LinearGradientBrush   _lgbGroundWave = new LinearGradientBrush(
            new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop() },
            new Point(0,0), new Point(1,0));

        // ── Кэш LinearGradientBrush для LiquidWave ────────────────────────────────
        private readonly LinearGradientBrush[] _lgbLiquid  = new LinearGradientBrush[LiquidWaveCount];
        // ── Кэш LinearGradientBrush для RibbonWaves ──────────────────────────────
        private readonly LinearGradientBrush[] _lgbRibbon  = new LinearGradientBrush[RibbonCount];
        // ── Кэш LinearGradientBrush для MagmaFlow ────────────────────────────────
        private readonly LinearGradientBrush[] _lgbMagma   = new LinearGradientBrush[MagmaLineCount];
        // ── Кэш LinearGradientBrush для NeonCity слой 1 ──────────────────────────
        private readonly LinearGradientBrush[] _lgbNeonMid = new LinearGradientBrush[NeonLineCount];

        // ── Кэш PointCollection для Polyline-режимов ─────────────────────────────
        // Aurora
        private readonly PointCollection[] _auroraPoints  = new PointCollection[AuroraLineCount];
        // LiquidWave
        private readonly PointCollection[] _liquidPoints  = new PointCollection[LiquidWaveCount];
        // RibbonWaves
        private readonly PointCollection[] _ribbonPoints  = new PointCollection[RibbonCount];
        // MagmaFlow
        private readonly PointCollection[] _magmaPoints   = new PointCollection[MagmaLineCount];
        // DancerGroundWave
        private readonly PointCollection _groundWavePoints = new PointCollection(60);

        // ── MagmaFlow ─────────────────────────────────────────────────────────────
        private const int MagmaLineCount = 24;
        private const int MagmaPts       = 180;
        private readonly Polyline[] _magmaLines = new Polyline[MagmaLineCount];
        private bool _magmaBuilt;

        // ── NeonCity ──────────────────────────────────────────────────────────────
        private const int NeonLineCount    = 18;  // вертикальные колонны
        private const int NeonScanCount    = 6;   // горизонтальные сканлайны
        private readonly Line[]    _neonLines    = new Line[NeonLineCount * 3]; // 3 слоя glow
        private readonly Line[]    _neonScans    = new Line[NeonScanCount];
        private readonly Ellipse[] _neonGlows    = new Ellipse[NeonLineCount];
        // Кэш BlurEffect для NeonCity
        private readonly BlurEffect[] _neonGlowBlurs = new BlurEffect[NeonLineCount];
        private readonly BlurEffect[] _neonMidBlurs  = new BlurEffect[NeonLineCount];
        private readonly BlurEffect[] _neonGelBlurs  = new BlurEffect[NeonLineCount];
        private bool _neonBuilt;



        // Аура (несколько колец вокруг танцора)
        private const int DancerAuraCount = 5;
        private readonly Ellipse[] _dancerAuras = new Ellipse[DancerAuraCount];

        // Позы танцора (ключевые кадры суставов)
        // Суставы: [0]=шея [1]=плечоL [2]=плечоR [3]=локотьL [4]=локотьR
        //          [5]=кистьL [6]=кистьR [7]=бедроL [8]=бедроR
        //          [9]=коленоL [10]=коленоR [11]=стопаL [12]=стопаR
        private double[] _jointX  = new double[13];
        private double[] _jointY  = new double[13];
        private double[] _jointX2 = new double[13]; // сглаженные
        private double[] _jointY2 = new double[13];
        private double _posePhase; // фаза позы

        // ── UI ────────────────────────────────────────────────────────────────────
        private TextBlock       _trackLabel   = null!;
        private TextBlock       _artistLabel  = null!;
        private TextBlock       _modeLabel    = null!;
        private Border          _controlBar   = null!;
        private DispatcherTimer _hideBarTimer = null!;
        private bool            _barVisible   = true;
        private Point           _lastMousePos = new(double.NaN, double.NaN);

        // ── Фон ───────────────────────────────────────────────────────────────────
        private Rectangle              _bgRect  = null!;
        private readonly LinearGradientBrush _bgBrush;

        // ── Drag-to-move ──────────────────────────────────────────────────────────
        private bool   _dragging;
        private Point  _dragOriginScreen;
        private double _dragOriginLeft, _dragOriginTop;

        // ─────────────────────────────────────────────────────────────────────────
        public VisualizerWindow(IVisualizerDataProvider provider)
        {
            _provider          = provider;
            Title              = "Aurora Visualizer";
            Width              = 760;
            Height             = 430;
            MinWidth           = 400;
            MinHeight          = 260;
            Background         = Brushes.Black;
            WindowStyle        = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode         = ResizeMode.CanResize;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            _bgBrush = new LinearGradientBrush
            {
                StartPoint    = new Point(0, 0),
                EndPoint      = new Point(1, 1),
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
            // Инициализируем кэш BlurEffect для аур танцора
            for (int i = 0; i < DancerAuraCount; i++)
                _dancerAuraBlurs[i] = new BlurEffect { Radius = 3 };
            // Инициализируем кэш LinearGradientBrush для лучей танцора
            for (int i = 0; i < DancerRayCount; i++)
                _lgbDancerRays[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0,0), new Point(1,0));
            // Инициализируем кэш PointCollection для полилиний
            for (int i = 0; i < AuroraLineCount;  i++) _auroraPoints[i] = new PointCollection(AuroraPointCount);
            for (int i = 0; i < LiquidWaveCount;  i++) _liquidPoints[i] = new PointCollection(LiquidWavePts);
            for (int i = 0; i < RibbonCount;      i++) _ribbonPoints[i] = new PointCollection(RibbonPts);
            for (int i = 0; i < MagmaLineCount;   i++) _magmaPoints[i]  = new PointCollection(MagmaPts);
            // Инициализируем кэш LGB для волн (LiquidWave, Ribbon, Magma, NeonMid)
            for (int i = 0; i < LiquidWaveCount; i++)
                _lgbLiquid[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0,0), new Point(1,0));
            for (int i = 0; i < RibbonCount; i++)
                _lgbRibbon[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop() },
                    new Point(0,0), new Point(1,0));
            for (int i = 0; i < MagmaLineCount; i++)
                _lgbMagma[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop() },
                    new Point(0,0), new Point(1,0));
            for (int i = 0; i < NeonLineCount; i++)
                _lgbNeonMid[i] = new LinearGradientBrush(
                    new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop() },
                    new Point(0,0), new Point(0,1));
            BuildLiquidWave();
            BuildRibbonWaves();
            BuildCircleEQ();
            BuildMagmaFlow();
            ApplyModeVisibility();

            // CompositionTarget.Rendering подключается/отключается через IsVisibleChanged
            // (см. ниже) — это позволяет полностью останавливать рендер когда окно скрыто.
            KeyDown += OnKeyDown;

            PreviewMouseLeftButtonDown += OnDragStart;
            PreviewMouseMove           += OnDragMove;
            PreviewMouseLeftButtonUp   += OnDragEnd;

            MouseDoubleClick += (_, e) =>
            {
                if (!IsChildOfControlBar(e.OriginalSource as DependencyObject))
                    ToggleFullscreen();
            };

            _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hideBarTimer.Tick += (_, _) =>
            {
                HideControlBar();
                // Скрываем курсор только в полноэкранном режиме
                if (_fullscreen)
                    Cursor = Cursors.None;
            };
            MouseMove += (_, e) =>
            {
                // Визуальные элементы анимируются и могут генерировать MouseMove
                // даже когда курсор физически не двигался. Игнорируем такие события,
                // иначе таймер автоскрытия будет бесконечно перезапускаться.
                var pos = e.GetPosition(this);
                if (!double.IsNaN(_lastMousePos.X) &&
                    Math.Abs(pos.X - _lastMousePos.X) < 0.5 &&
                    Math.Abs(pos.Y - _lastMousePos.Y) < 0.5)
                {
                    return;
                }
                _lastMousePos = pos;

                // Восстанавливаем курсор только если он был скрыт (т.е. в fullscreen)
                if (_fullscreen)
                    Cursor = null;
                ShowControlBar();
                _hideBarTimer.Stop();
                _hideBarTimer.Start();
            };

            Closing += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRender;
                SetThreadExecutionState(ES_CONTINUOUS);
                Cursor = null;
            };
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    // Окно показано: сбрасываем таймер и возобновляем рендеринг
                    _lastRenderTime = DateTime.MinValue;
                    _lastRenderingTime = TimeSpan.Zero;
                    _lastMousePos = new Point(double.NaN, double.NaN);
                    CompositionTarget.Rendering += OnRender;
                }
                else
                {
                    // Окно скрыто: полностью останавливаем цикл рендеринга
                    // FFT продолжает собираться в аудио-потоке (это дёшево),
                    // но OnRender не вызывается — нагрузка на UI-поток = 0.
                    CompositionTarget.Rendering -= OnRender;
                    _hideBarTimer.Stop();
                    Cursor = null;
                }
            };
        }

        // ─── Drag-to-move ─────────────────────────────────────────────────────────
        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (IsChildOfControlBar(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2) return;
            _dragging         = true;
            _dragOriginScreen = PointToScreen(e.GetPosition(this));
            _dragOriginLeft   = Left;
            _dragOriginTop    = Top;
            CaptureMouse();
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var cur = PointToScreen(e.GetPosition(this));
            Left = _dragOriginLeft + (cur.X - _dragOriginScreen.X);
            Top  = _dragOriginTop  + (cur.Y - _dragOriginScreen.Y);
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
        }

        private bool IsChildOfControlBar(DependencyObject? el)
        {
            while (el != null)
            {
                if (ReferenceEquals(el, _controlBar)) return true;
                el = VisualTreeHelper.GetParent(el);
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
                Orientation         = Orientation.Vertical,
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(20, 16, 0, 0),
                IsHitTestVisible    = false,
            };
            _artistLabel = new TextBlock
            {
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xA8, 0x9F, 0xFF)),
            };
            _trackLabel = new TextBlock
            {
                FontFamily = new FontFamily("Syne, Segoe UI"),
                FontSize   = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            };
            trackPanel.Children.Add(_artistLabel);
            trackPanel.Children.Add(_trackLabel);
            root.Children.Add(trackPanel);

            _modeLabel = new TextBlock
            {
                FontFamily          = new FontFamily("Syne, Segoe UI"),
                FontSize            = 10,
                FontWeight          = FontWeights.Bold,
                Foreground          = new SolidColorBrush(Color.FromArgb(0x50, 0xA8, 0x9F, 0xFF)),
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 16, 20, 0),
                Text                = ModeName(_mode),
                IsHitTestVisible    = false,
            };
            root.Children.Add(_modeLabel);
            Content = root;
        }

        private Border BuildControlBar()
        {
            var bar = new Border
            {
                VerticalAlignment   = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new LinearGradientBrush(
                    Color.FromArgb(0xD0, 0x02, 0x00, 0x0C),
                    Color.FromArgb(0x00, 0x02, 0x00, 0x0C),
                    new Point(0, 1), new Point(0, 0)),
                Padding = new Thickness(16, 10, 16, 14),
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MakeCtrlBtn("◈ РЕЖИМ",           () => CycleMode()));
            panel.Children.Add(MakeCtrlBtn("⛶ FULLSCREEN  F11", () => ToggleFullscreen()));
            panel.Children.Add(MakeCtrlBtn("✕ ЗАКРЫТЬ",          () => Hide()));

            var hint = new TextBlock
            {
                Text              = "Двойной клик / F11 — полный экран  ·  M — режим  ·  Esc — закрыть",
                FontFamily        = new FontFamily("Syne, Segoe UI"),
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
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
                Content         = text,
                Padding         = new Thickness(14, 6, 14, 6),
                Margin          = new Thickness(4, 0, 4, 0),
                Background      = new SolidColorBrush(Color.FromArgb(0x35, 0x7C, 0x6B, 0xFF)),
                Foreground      = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, 0x7C, 0x6B, 0xFF)),
                BorderThickness = new Thickness(1),
                FontFamily      = new FontFamily("Syne, Segoe UI"),
                FontSize        = 10,
                FontWeight      = FontWeights.Bold,
                Cursor          = Cursors.Hand,
            };
            btn.Click += (_, _) => action();
            return btn;
        }

        private void ShowControlBar()
        {
            if (_barVisible) return;
            _barVisible = true;
            foreach (var el in new UIElement[] { _controlBar, _artistLabel, _trackLabel, _modeLabel })
            { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; }
        }

        private void HideControlBar()
        {
            if (!_barVisible) return;
            _barVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.9));
            foreach (var el in new UIElement[] { _controlBar, _artistLabel, _trackLabel, _modeLabel })
                el.BeginAnimation(OpacityProperty, fade);
        }

        // ─── Построение визуальных объектов ──────────────────────────────────────
        private void BuildBars()
        {
            for (int i = 0; i < BarCount; i++)
            {
                double t = (double)i / BarCount;
                var bar = new Rectangle { RadiusX = 2, RadiusY = 2 };
                bar.Fill = new LinearGradientBrush(
                    HsvToColor((0.70 + t * 0.32 + 0.18) % 1.0, 0.95, 0.45),
                    HsvToColor((0.70 + t * 0.32) % 1.0, 0.80, 1.0),
                    new Point(0, 1), new Point(0, 0));
                _canvas.Children.Add(bar);
                _bars[i] = bar;

                var mirror = new Rectangle { RadiusX = 2, RadiusY = 2 };
                mirror.Fill = new LinearGradientBrush(
                    HsvToColor((0.70 + t * 0.32) % 1.0, 0.75, 0.35),
                    HsvToColor((0.70 + t * 0.32) % 1.0, 0.75, 0.0),
                    new Point(0, 0), new Point(0, 1));
                _canvas.Children.Add(mirror);
                _mirrorBars[i] = mirror;

                var peak = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)) };
                _canvas.Children.Add(peak);
                _peakDots[i] = peak;
            }

            _barEnvTop.StrokeThickness = 2.0;
            _barEnvTop.StrokeLineJoin  = PenLineJoin.Round;
            _barEnvTop.Opacity         = 0.75;
            _canvas.Children.Add(_barEnvTop);

            _barEnvBot.StrokeThickness = 1.2;
            _barEnvBot.StrokeLineJoin  = PenLineJoin.Round;
            _barEnvBot.Opacity         = 0.35;
            _canvas.Children.Add(_barEnvBot);
        }

        private void BuildRings()
        {
            for (int i = 0; i < RingCount; i++)
            {
                var glow = new Ellipse { StrokeThickness = 12, Fill = Brushes.Transparent, Opacity = 0.10 };
                _canvas.Children.Add(glow);
                _ringGlows[i] = glow;

                var el = new Ellipse { StrokeThickness = 1.2, Fill = Brushes.Transparent };
                _canvas.Children.Add(el);
                _rings[i] = el;
            }
        }

        private void BuildLiquidWave()
        {
            for (int li = 0; li < LiquidWaveCount; li++)
            {
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round };
                _canvas.Children.Add(pl);
                _liquidLines[li] = pl;
            }
            _liquidBuilt = true;
        }

        private void BuildRibbonWaves()
        {
            for (int li = 0; li < RibbonCount; li++)
            {
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round };
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
                var ln = new Line { StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _canvas.Children.Add(ln);
                _circBars[i] = ln;
            }
            for (int i = 0; i < CircleRingLines; i++)
            {
                var glow = new Ellipse { Fill = Brushes.Transparent, StrokeThickness = 8, Opacity = 0.12 };
                _canvas.Children.Add(glow);
                _circRingGlows[i] = glow;

                var ring = new Ellipse { Fill = Brushes.Transparent, StrokeThickness = 1.0 };
                _canvas.Children.Add(ring);
                _circRings[i] = ring;
            }
            _circCenter.Fill = new RadialGradientBrush(
                Color.FromArgb(0xC0, 0x9B, 0x59, 0xFF),
                Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            _canvas.Children.Add(_circCenter);
        }

        // ★★★ Построение DancerSpectrum ★★★ ─────────────────────────────────────
        private void BuildDancerSpectrum()
        {
            _dancerBuilt = true;

            // Левый спектр (низкие)
            for (int i = 0; i < DancerBarL; i++)
            {
                var b = new Rectangle { RadiusX = 2, RadiusY = 2 };
                _canvas.Children.Add(b);
                _dBarsL[i] = b;

                var p = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)) };
                _canvas.Children.Add(p);
                _dPeaksL[i] = p;
            }

            // Правый спектр (высокие)
            for (int i = 0; i < DancerBarR; i++)
            {
                var b = new Rectangle { RadiusX = 2, RadiusY = 2 };
                _canvas.Children.Add(b);
                _dBarsR[i] = b;

                var p = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)) };
                _canvas.Children.Add(p);
                _dPeaksR[i] = p;
            }

            // Ауры вокруг танцора
            for (int i = 0; i < DancerAuraCount; i++)
            {
                var aura = new Ellipse { Fill = Brushes.Transparent, StrokeThickness = 1.0 };
                _canvas.Children.Add(aura);
                _dancerAuras[i] = aura;
            }

            // Лучи
            for (int i = 0; i < DancerRayCount; i++)
            {
                var ray = new Line { StrokeThickness = 0.8 };
                _canvas.Children.Add(ray);
                _dancerRays[i] = ray;
            }

            // Волна под ногами танцора
            _dancerGroundWave.StrokeLineJoin = PenLineJoin.Round;
            _dancerGroundWave.StrokeThickness = 2.5;
            _canvas.Children.Add(_dancerGroundWave);

            // Glow силуэт (рисуем ниже основного)
            _dancerGlow.StrokeLineJoin  = PenLineJoin.Round;
            _dancerGlow.StrokeThickness = 14;
            _dancerGlow.Opacity         = 0.0; // будем обновлять в render
            _canvas.Children.Add(_dancerGlow);

            // Основной силуэт
            _dancerBody.StrokeLineJoin  = PenLineJoin.Round;
            _dancerBody.StrokeThickness = 3.5;
            _dancerBody.Fill            = Brushes.Transparent;
            _canvas.Children.Add(_dancerBody);

            // Glow головы
            _dancerHeadG.Fill            = Brushes.Transparent;
            _dancerHeadG.StrokeThickness = 10;
            _canvas.Children.Add(_dancerHeadG);

            // Голова
            _dancerHead.Fill            = Brushes.Transparent;
            _dancerHead.StrokeThickness = 3.0;
            _canvas.Children.Add(_dancerHead);

            // Инициализируем суставы
            for (int j = 0; j < 13; j++) { _jointX[j] = 0; _jointY[j] = 0; _jointX2[j] = 0; _jointY2[j] = 0; }
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
            VisualizerMode.SpectrumBars   => "SPECTRUM BARS",
            VisualizerMode.SpectrumRings  => "SPECTRUM RINGS",
            VisualizerMode.Aurora         => "AURORA",
            VisualizerMode.LiquidWave     => "LIQUID WAVE",
            VisualizerMode.RibbonWaves    => "RIBBON WAVES",
            VisualizerMode.CircleEQ       => "CIRCLE EQ",
            VisualizerMode.DancerSpectrum => "✦ DANCER SPECTRUM ✦",
            VisualizerMode.MagmaFlow      => "🔥 MAGMA FLOW",
            _                             => "",
        };

        private void ApplyModeVisibility()
        {
            bool bars   = _mode == VisualizerMode.SpectrumBars;
            bool rings  = _mode == VisualizerMode.SpectrumRings;
            bool dancer = _mode == VisualizerMode.DancerSpectrum;

            var v = (bool on) => on ? Visibility.Visible : Visibility.Collapsed;

            foreach (var b in _bars)       b.Visibility = v(bars);
            foreach (var b in _mirrorBars) b.Visibility = v(bars);
            foreach (var p in _peakDots)   p.Visibility = v(bars);
            _barEnvTop.Visibility = v(bars);
            _barEnvBot.Visibility = v(bars);

            foreach (var r in _rings)     r.Visibility = v(rings);
            foreach (var r in _ringGlows) r.Visibility = v(rings);

            if (_auroraBuilt)
                foreach (var pl in _auroraLines)
                    pl.Visibility = v(_mode == VisualizerMode.Aurora);
            if (_neonBuilt)
                foreach (var ln in _neonLines)
                    ln.Visibility = Visibility.Collapsed;
            if (_liquidBuilt)
                foreach (var pl in _liquidLines)
                    pl.Visibility = v(_mode == VisualizerMode.LiquidWave);
            if (_ribbonBuilt)
                foreach (var pl in _ribbonLines)
                    pl.Visibility = v(_mode == VisualizerMode.RibbonWaves);
            if (_circleBuilt)
            {
                bool cEQ = _mode == VisualizerMode.CircleEQ;
                foreach (var ln in _circBars)      ln.Visibility = v(cEQ);
                foreach (var el in _circRings)     el.Visibility = v(cEQ);
                foreach (var el in _circRingGlows) el.Visibility = v(cEQ);
                _circCenter.Visibility = v(cEQ);
            }
            if (_dancerBuilt)
            {
                foreach (var b in _dBarsL)    b.Visibility = v(dancer);
                foreach (var b in _dBarsR)    b.Visibility = v(dancer);
                foreach (var p in _dPeaksL)   p.Visibility = v(dancer);
                foreach (var p in _dPeaksR)   p.Visibility = v(dancer);
                foreach (var a in _dancerAuras) a.Visibility = v(dancer);
                foreach (var r in _dancerRays)  r.Visibility = v(dancer);
                _dancerBody.Visibility       = v(dancer);
                _dancerGlow.Visibility       = v(dancer);
                _dancerHead.Visibility       = v(dancer);
                _dancerHeadG.Visibility      = v(dancer);
                _dancerGroundWave.Visibility = v(dancer);
            }
            if (_magmaBuilt)
                foreach (var pl in _magmaLines)
                    pl.Visibility = v(_mode == VisualizerMode.MagmaFlow);

            // Очищаем динамические частицы при смене режима
            var toRemove = _canvas.Children.OfType<UIElement>()
                .Where(c => c is Ellipse el
                    && !_rings.Contains(el) && !_ringGlows.Contains(el)
                    && (!_circleBuilt  || !_circRings.Contains(el))
                    && (!_circleBuilt  || !_circRingGlows.Contains(el))
                    && (!_circleBuilt  || el != _circCenter)
                    && (!_dancerBuilt  || !_dancerAuras.Contains(el))
                    && (!_dancerBuilt  || el != _dancerHead)
                    && (!_dancerBuilt  || el != _dancerHeadG))
                .ToList();
            foreach (var r in toRemove) _canvas.Children.Remove(r);

            if (_mode != VisualizerMode.DancerSpectrum)
                _dParticles.Clear();
        }

        // ─── Fullscreen ───────────────────────────────────────────────────────────
        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _prevLeft = Left; _prevTop = Top; _prevWidth = Width; _prevHeight = Height;

                var hwnd    = new WindowInteropHelper(this).Handle;
                var monitor = MonitorFromWindow(hwnd, 0x00000002);
                var mi      = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref mi);

                var    src = PresentationSource.FromVisual(this);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

                double mLeft   = mi.rcMonitor.Left   / dpi;
                double mTop    = mi.rcMonitor.Top    / dpi;
                double mWidth  = (mi.rcMonitor.Right  - mi.rcMonitor.Left) / dpi;
                double mHeight = (mi.rcMonitor.Bottom - mi.rcMonitor.Top)  / dpi;

                WindowState = WindowState.Normal;
                ResizeMode  = ResizeMode.NoResize;
                Topmost     = true;
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
                _lastMousePos = new Point(double.NaN, double.NaN);
            }
            else
            {
                WindowState = WindowState.Normal;
                Topmost     = false;
                ResizeMode  = ResizeMode.CanResize;
                Left = _prevLeft; Top = _prevTop; Width = _prevWidth; Height = _prevHeight;
                _fullscreen = false;
                SetThreadExecutionState(ES_CONTINUOUS);
                Cursor = null;
                ShowControlBar();
                _hideBarTimer.Stop();
                _lastMousePos = new Point(double.NaN, double.NaN);
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Hide();            break;
                case Key.F11:    ToggleFullscreen(); break;
                case Key.M:      CycleMode();        break;
            }
        }

        private DateTime _lastRenderTime = DateTime.MinValue;
        private TimeSpan _lastRenderingTime = TimeSpan.Zero;
        // ── FPS-лимитер: рендерим до 60 кадров/сек (~16.7 мс между кадрами) ──
        private const double TargetFrameMs = 1000.0 / 60.0; // 16.67 ms

        // ─── Главный цикл рендеринга ──────────────────────────────────────────────
        private void OnRender(object? sender, EventArgs e)
        {
            if (!IsVisible) return;

            // Реальная дельта времени — анимация не зависит от FPS.
            // Берём RenderingTime (композиционный таймлайн WPF) чтобы снизить джиттер.
            double elapsedMs;
            if (e is RenderingEventArgs re)
            {
                elapsedMs = _lastRenderingTime == TimeSpan.Zero
                    ? TargetFrameMs
                    : (re.RenderingTime - _lastRenderingTime).TotalMilliseconds;
                _lastRenderingTime = re.RenderingTime;
            }
            else
            {
                var now = DateTime.UtcNow;
                elapsedMs = _lastRenderTime == DateTime.MinValue
                    ? TargetFrameMs
                    : (now - _lastRenderTime).TotalMilliseconds;
                _lastRenderTime = now;
            }

            // Пропускаем кадр если прошло меньше целевого интервала
            if (elapsedMs < TargetFrameMs) return;

            double dt = Math.Min(elapsedMs / 1000.0, 0.033); // cap 33ms (30 fps min)
            _time += dt;

            double W = _canvas.ActualWidth;
            double H = _canvas.ActualHeight;
            if (W < 1 || H < 1) return;

            _trackLabel.Text  = _provider.CurrentTrackTitle;
            _artistLabel.Text = _provider.CurrentArtist.ToUpperInvariant();

            UpdateFft();
            UpdateBackground(W, H);

            switch (_mode)
            {
                case VisualizerMode.SpectrumBars:   RenderBars(W, H);           break;
                case VisualizerMode.SpectrumRings:  RenderSpectrumRings(W, H);  break;
                case VisualizerMode.Aurora:         RenderAurora(W, H);         break;
                case VisualizerMode.LiquidWave:     RenderLiquidWave(W, H);     break;
                case VisualizerMode.RibbonWaves:    RenderRibbonWaves(W, H);    break;
                case VisualizerMode.CircleEQ:       RenderCircleEQ(W, H);       break;
                case VisualizerMode.DancerSpectrum: RenderDancerSpectrum(W, H); break;
                case VisualizerMode.MagmaFlow:      RenderMagmaFlow(W, H);      break;
            }
        }

        // ─── FFT ──────────────────────────────────────────────────────────────────
        private void UpdateFft()
        {
            var  raw    = _provider.GetFftData();
            bool active = _provider.IsPlaying && raw != null;
            int  len    = Math.Min(_smoothFft.Length, raw?.Length ?? 0);

            for (int i = 0; i < _smoothFft.Length; i++)
            {
                float v = active && i < len ? raw![i] : 0f;
                _smoothFft[i] = v > _smoothFft[i]
                    ? _smoothFft[i] * 0.25f + v * 0.75f
                    : _smoothFft[i] * 0.86f + v * 0.14f;
                _peakFft[i] = Math.Max(_peakFft[i] * 0.982f, _smoothFft[i]);
            }
            _bassEnergy = BandEnergy(0,  6);
            _midEnergy  = BandEnergy(6,  60);
            _highEnergy = BandEnergy(60, 200);
        }

        private float BandEnergy(int from, int to)
        {
            float s = 0;
            int   e = Math.Min(to, _smoothFft.Length);
            for (int i = from; i < e; i++) s += _smoothFft[i];
            return s / Math.Max(1, e - from);
        }

        // ─── Фон ──────────────────────────────────────────────────────────────────
        private void UpdateBackground(double W, double H)
        {
            _bgRect.Width  = W;
            _bgRect.Height = H;
            float b = Math.Clamp(_bassEnergy * 55f, 0f, 1f);
            float m = Math.Clamp(_midEnergy  * 30f, 0f, 1f);
            _bgBrush.GradientStops[0].Color = Color.FromRgb(
                (byte)(0x03 + b * 0x1A), 0x00, (byte)(0x0E + b * 0x1C + m * 0x0A));
            _bgBrush.GradientStops[1].Color = Color.FromRgb(
                (byte)(0x08 + b * 0x10), 0x00, (byte)(0x1C + b * 0x18));
            _bgBrush.GradientStops[2].Color = Color.FromRgb(
                0x00, (byte)(m * 0x08), (byte)(0x10 + b * 0x14));
        }

        // ─── 1. SPECTRUM BARS ─────────────────────────────────────────────────────
        private void RenderBars(double W, double H)
        {
            double gap   = 1.5;
            double barW  = Math.Max(2, (W - gap * (BarCount + 1)) / BarCount);
            double maxH  = H * 0.88;
            double floor = H - 4;
            double hueShift = (_time * 0.025) % 1.0;

            _barEnvTop.Points.Clear();
            _barEnvBot.Points.Clear();

            for (int i = 0; i < BarCount; i++)
            {
                double t   = (double)i / BarCount;
                int    bin = Math.Clamp((int)(Math.Pow(_smoothFft.Length * 0.65, t)), 0, _smoothFft.Length - 1);
                float  mag = _smoothFft[bin];

                double target = Math.Clamp(mag * maxH * 8.5, 1, maxH);
                _barH[i] = _barH[i] < target
                    ? _barH[i] * 0.20 + target * 0.80
                    : _barH[i] * 0.83 + target * 0.17;

                if (target > _peakH[i]) _peakH[i] = target;
                else _peakH[i] = Math.Max(1, _peakH[i] - 0.7);

                double x  = gap + i * (barW + gap);
                double bh = Math.Max(1, _barH[i]);
                double cx = x + barW * 0.5;

                var bar = _bars[i];
                double barHue = (t * 0.70 + hueShift) % 1.0;
                bar.Fill = new LinearGradientBrush(
                    HsvToColor((barHue + 0.18) % 1.0, 0.95, 0.45),
                    HsvToColor(barHue, 0.80, 1.0),
                    new Point(0, 1), new Point(0, 0));
                bar.Width  = barW; bar.Height = bh;
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, floor - bh);
                bar.Opacity = Math.Clamp(0.55 + mag * 9, 0.40, 1.0);

                double mh     = Math.Min(bh * 0.18, H * 0.10);
                var    mirror = _mirrorBars[i];
                mirror.Width  = barW; mirror.Height = mh;
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
            _barEnvTop.Stroke = new LinearGradientBrush(
                new GradientStopCollection {
                    new GradientStop(HsvToColor(envHue, 0.70, 1.0), 0.0),
                    new GradientStop(HsvToColor((envHue+0.25)%1.0, 0.80, 1.0), 0.33),
                    new GradientStop(HsvToColor((envHue+0.50)%1.0, 0.85, 1.0), 0.66),
                    new GradientStop(HsvToColor((envHue+0.75)%1.0, 0.70, 1.0), 1.0),
                }, new Point(0,0), new Point(1,0));
            _barEnvBot.Stroke = new LinearGradientBrush(
                new GradientStopCollection {
                    new GradientStop(HsvToColor((envHue+0.5)%1.0, 0.60, 0.9), 0.0),
                    new GradientStop(HsvToColor((envHue+0.75)%1.0, 0.70, 0.9), 1.0),
                }, new Point(0,0), new Point(1,0));
        }

        // ─── 4. SPECTRUM RINGS ────────────────────────────────────────────────────
        private void RenderSpectrumRings(double W, double H)
        {
            double cx   = W * 0.5, cy = H * 0.5;
            double maxR = Math.Min(W, H) * 0.46;

            for (int i = 0; i < RingCount; i++)
            {
                double t   = (double)i / RingCount;
                int    bin = Math.Clamp((int)(Math.Pow(220.0, t)), 0, _smoothFft.Length - 1);
                float  mag = _smoothFft[bin];
                double r   = Math.Max(0, maxR * t
                    + mag * maxR * 0.40
                    + Math.Sin(_time * 0.8 + t * Math.PI * 1.5) * _bassEnergy * maxR * 0.06);

                double hue = (t * 0.7 + _time * 0.05) % 1.0;
                var    col = HsvToColor(hue, 0.8, 0.6 + mag * 4.0);
                byte   al  = (byte)(40 + t * 160 + mag * 220);

                var glow = _ringGlows[i];
                glow.Width = r * 2 + 20; glow.Height = r * 2 + 20;
                Canvas.SetLeft(glow, cx - r - 10); Canvas.SetTop(glow, cy - r - 10);
                _scbRingGlow.Color   = Color.FromArgb((byte)(al / 4), col.R, col.G, col.B);
                glow.Stroke          = _scbRingGlow;
                glow.StrokeThickness = 14 + mag * 22;

                var el = _rings[i];
                el.Width = r * 2; el.Height = r * 2;
                Canvas.SetLeft(el, cx - r); Canvas.SetTop(el, cy - r);
                _scbRing.Color     = Color.FromArgb(al, col.R, col.G, col.B);
                el.Stroke          = _scbRing;
                el.StrokeThickness = 1.0 + mag * 7.0;
            }
        }

        // ─── 7. AURORA ────────────────────────────────────────────────────────────
        private void EnsureAurora()
        {
            if (_auroraBuilt) return;
            _auroraBuilt = true;
            for (int i = 0; i < AuroraLineCount; i++)
            {
                var pl = new Polyline { StrokeLineJoin = PenLineJoin.Round };
                _canvas.Children.Add(pl);
                _auroraLines[i] = pl;
            }
        }

        private void RenderAurora(double W, double H)
        {
            EnsureAurora();
            foreach (var pl in _auroraLines) pl.Visibility = Visibility.Visible;

            double bassBoost = 1.0 + _bassEnergy * 7.0;
            double midBoost  = 1.0 + _midEnergy  * 5.0;

            for (int li = 0; li < AuroraLineCount; li++)
            {
                double layerT = (double)li / AuroraLineCount;
                double baseY  = H * (0.10 + layerT * 0.65);
                double hue    = (0.35 + layerT * 0.35 + _time * 0.02) % 1.0;
                double amp    = H * (0.035 + layerT * 0.12) * bassBoost;
                double freq   = 1.5 + layerT * 3.0;
                double phase  = _time * (0.35 + layerT * 0.55) + li * 0.9;
                double sat    = 0.55 + layerT * 0.30;
                double bright = 0.7 + _bassEnergy * 0.3;
                byte   alpha  = (byte)(18 + layerT * 110 + _bassEnergy * 95);

                int    fftBin = Math.Clamp((int)(layerT * 140), 0, _smoothFft.Length - 1);
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
                var pl  = _auroraLines[li];
                pl.Points          = pts;
                _scbAurora.Color   = Color.FromArgb(alpha, col.R, col.G, col.B);
                pl.Stroke          = _scbAurora;
                pl.StrokeThickness = 3.0 + layerT * 10.0 + _bassEnergy * 8.0;
            }
        }

        // ─── 8. VORTEX ────────────────────────────────────────────────────────────
        // ─── 9. LIQUID WAVE ───────────────────────────────────────────────────────
        private void RenderLiquidWave(double W, double H)
        {
            foreach (var pl in _liquidLines) pl.Visibility = Visibility.Visible;

            for (int li = 0; li < LiquidWaveCount; li++)
            {
                double layerT  = (double)li / LiquidWaveCount;
                double baseY   = H * (0.25 + layerT * 0.55);
                double hue     = (layerT * 0.70 + _time * 0.03) % 1.0;
                double amp     = H * (0.05 + layerT * 0.15) * (1 + _bassEnergy * 5.0);
                double freq    = 1.0 + layerT * 2.5;
                double phase   = _time * (0.40 + layerT * 0.60) + li * 1.1;
                int    fftBin  = Math.Clamp((int)(layerT * 180), 0, _smoothFft.Length - 1);
                double fftAmp  = _smoothFft[fftBin] * H * 0.35 * (1 + _midEnergy * 4.0);
                double thick   = 1.5 + layerT * 6.0 + _smoothFft[fftBin] * 12.0;
                byte   alpha   = (byte)(25 + layerT * 140 + _bassEnergy * 80);

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

                var col  = HsvToColor(hue, 0.88, 1.0);
                var col2 = HsvToColor((hue + 0.15) % 1.0, 0.80, 0.9);
                var pl   = _liquidLines[li];
                pl.Points = pts;
                // Обновляем кешированный LGB — ноль аллокаций
                var lgb = _lgbLiquid[li];
                lgb.GradientStops[0].Color = Color.FromArgb(alpha, col.R,  col.G,  col.B);
                lgb.GradientStops[1].Color = Color.FromArgb(alpha, col2.R, col2.G, col2.B);
                pl.Stroke = lgb;
                pl.StrokeThickness = Math.Max(0.6, thick);
            }
        }

        // ─── 10. RIBBON WAVES ─────────────────────────────────────────────────────
        private void RenderRibbonWaves(double W, double H)
        {
            foreach (var pl in _ribbonLines) pl.Visibility = Visibility.Visible;

            for (int li = 0; li < RibbonCount; li++)
            {
                double layerT  = (double)li / RibbonCount;
                double baseY   = H * (0.15 + layerT * 0.72);
                double baseHue = (layerT * 0.60 + _time * 0.025) % 1.0;
                double amp     = H * (0.04 + layerT * 0.12) * (1 + _bassEnergy * 6.0);
                double freq    = 1.2 + layerT * 3.5;
                double phase   = _time * (0.30 + layerT * 0.65) + li * 0.75;
                int    fftBin  = Math.Clamp((int)(layerT * 200), 0, _smoothFft.Length - 1);
                double fftAmp  = _smoothFft[fftBin] * H * 0.38 * (1 + _midEnergy * 3.0);
                double thick   = 1.0 + layerT * 5.5 + _smoothFft[fftBin] * 10.0;
                byte   alpha   = (byte)(20 + layerT * 150 + _bassEnergy * 85);

                var pts = _ribbonPoints[li];
                pts.Clear();
                for (int xi = 0; xi < RibbonPts; xi++)
                {
                    double t = (double)xi / (RibbonPts - 1);
                    double x = t * W;
                    double y = baseY
                        + Math.Sin(t * Math.PI * freq         + phase)        * amp
                        + Math.Sin(t * Math.PI * freq * 2.2   + phase * 1.6)  * amp * 0.40
                        + Math.Sin(t * Math.PI * freq * 0.51  + phase * 0.7)  * amp * 0.22
                        + _smoothFft[Math.Clamp((int)(t * 180), 0, _smoothFft.Length - 1)]
                          * fftAmp * Math.Sin(t * Math.PI);
                    pts.Add(new Point(x, y));
                }

                var colL = HsvToColor(baseHue, 0.90, 1.0);
                var colM = HsvToColor((baseHue + 0.12) % 1, 0.85, 1.0);
                var colR = HsvToColor((baseHue + 0.24) % 1, 0.80, 1.0);

                var pl = _ribbonLines[li];
                pl.Points = pts;
                var lgbR = _lgbRibbon[li];
                lgbR.GradientStops[0].Color = Color.FromArgb(alpha, colL.R, colL.G, colL.B);
                lgbR.GradientStops[1].Color = Color.FromArgb(alpha, colM.R, colM.G, colM.B);
                pl.Stroke = lgbR;
                pl.StrokeThickness = Math.Max(0.6, thick);
            }
        }

        // ─── 11. CIRCLE EQ ────────────────────────────────────────────────────────
        private void RenderCircleEQ(double W, double H)
        {
            foreach (var ln in _circBars)      ln.Visibility = Visibility.Visible;
            foreach (var el in _circRings)     el.Visibility = Visibility.Visible;
            foreach (var el in _circRingGlows) el.Visibility = Visibility.Visible;
            _circCenter.Visibility = Visibility.Visible;

            double cx       = W * 0.5;
            double cy       = H * 0.5;
            double minDim   = Math.Min(W, H);
            double baseR    = minDim * 0.26;
            double maxBarL  = minDim * 0.22;
            double bassBoost = 1.0 + _bassEnergy * 5.0;

            for (int i = 0; i < CircleBarCount; i++)
            {
                double t     = (double)i / CircleBarCount;
                double angle = t * Math.PI * 2 - Math.PI / 2;

                int fftBin = Math.Clamp((int)(Math.Pow(_smoothFft.Length * 0.70, t)), 0, _smoothFft.Length - 1);
                float mag  = _smoothFft[fftBin];

                double targetH = Math.Clamp(mag * maxBarL * 9.0, 1.0, maxBarL) * bassBoost;
                _circBarH[i] = _circBarH[i] < targetH
                    ? _circBarH[i] * 0.18 + targetH * 0.82
                    : _circBarH[i] * 0.84 + targetH * 0.16;
                double barLen = Math.Max(1.5, _circBarH[i]);

                double cosA  = Math.Cos(angle);
                double sinA  = Math.Sin(angle);
                double innerR = baseR;
                double outerR = baseR + barLen;

                var ln   = _circBars[i];
                ln.X1 = cx + cosA * innerR; ln.Y1 = cy + sinA * innerR;
                ln.X2 = cx + cosA * outerR; ln.Y2 = cy + sinA * outerR;

                double hue = (t * 0.65 + _time * 0.04 + _bassEnergy * 0.2) % 1.0;
                double sat = 0.80 + mag * 0.18;
                double val = Math.Clamp(0.6 + mag * 5.0, 0, 1.0);
                var    col = HsvToColor(hue, sat, val);
                byte   al  = (byte)(80 + mag * 220 + _bassEnergy * 80);
                ln.Stroke          = _scbCircBar;
                _scbCircBar.Color  = Color.FromArgb(al, col.R, col.G, col.B);
                ln.StrokeThickness = Math.Max(1.2, 2.0 + mag * 4.0);
            }

            double[] ringR = { baseR * 0.72, baseR * 1.02, baseR + maxBarL * 0.45, baseR + maxBarL * 0.90 };
            for (int ri = 0; ri < CircleRingLines; ri++)
            {
                double r   = ringR[ri] + Math.Sin(_time * 1.5 + ri * 1.1) * _bassEnergy * baseR * 0.05;
                double hue = (ri * 0.25 + _time * 0.05) % 1.0;
                var    col = HsvToColor(hue, 0.80, 0.9);
                byte   al  = (byte)(60 + ri * 35 + _bassEnergy * 100);

                var glow  = _circRingGlows[ri];
                glow.Width  = r * 2 + 16; glow.Height = r * 2 + 16;
                Canvas.SetLeft(glow, cx - r - 8); Canvas.SetTop(glow, cy - r - 8);
                _scbCircGlow.Color   = Color.FromArgb((byte)(al / 3), col.R, col.G, col.B);
                glow.Stroke          = _scbCircGlow;
                glow.StrokeThickness = 10 + _bassEnergy * 12;

                var ring  = _circRings[ri];
                ring.Width  = r * 2; ring.Height = r * 2;
                Canvas.SetLeft(ring, cx - r); Canvas.SetTop(ring, cy - r);
                _scbCircRing.Color   = Color.FromArgb(al, col.R, col.G, col.B);
                ring.Stroke          = _scbCircRing;
                ring.StrokeThickness = 1.0 + _bassEnergy * 2.0;
            }

            double cr  = baseR * (0.35 + _bassEnergy * 0.18);
            _circCenter.Width  = cr * 2; _circCenter.Height = cr * 2;
            Canvas.SetLeft(_circCenter, cx - cr); Canvas.SetTop(_circCenter, cy - cr);
            double cHue = (_time * 0.06) % 1.0;
            _circCenter.Fill = new RadialGradientBrush(
                Color.FromArgb((byte)(120 + _bassEnergy * 135), HsvToColor(cHue, 0.7, 1.0).R,
                    HsvToColor(cHue, 0.7, 1.0).G, HsvToColor(cHue, 0.7, 1.0).B),
                Color.FromArgb(0, 0, 0, 0));
        }

        // ══════════════════════════════════════════════════════════════════════════
        // ★★★★★  12. DANCER SPECTRUM — Танцующий силуэт + двойной спектр  ★★★★★
        // ══════════════════════════════════════════════════════════════════════════
        private void RenderDancerSpectrum(double W, double H)
        {
            if (!_dancerBuilt) BuildDancerSpectrum();

            // Показать все элементы
            foreach (var b in _dBarsL)    b.Visibility = Visibility.Visible;
            foreach (var b in _dBarsR)    b.Visibility = Visibility.Visible;
            foreach (var p in _dPeaksL)   p.Visibility = Visibility.Visible;
            foreach (var p in _dPeaksR)   p.Visibility = Visibility.Visible;
            foreach (var a in _dancerAuras) a.Visibility = Visibility.Visible;
            foreach (var r in _dancerRays)  r.Visibility = Visibility.Visible;
            _dancerBody.Visibility       = Visibility.Visible;
            _dancerGlow.Visibility       = Visibility.Visible;
            _dancerHead.Visibility       = Visibility.Visible;
            _dancerHeadG.Visibility      = Visibility.Visible;
            _dancerGroundWave.Visibility = Visibility.Visible;

            double cx = W * 0.5;
            double cy = H * 0.5;

            // ─── Ширина зон спектра ─────────────────────────────────────────────
            // Центр отведён танцору (примерно 32% ширины)
            // Левая зона: 0 .. W*0.34
            // Правая зона: W*0.66 .. W
            double specW = W * 0.33;
            double dancerCx = cx;

            // ─── ЛЕВЫЙ СПЕКТР (низкие частоты, холодные оттенки) ────────────────
            {
                double barW  = Math.Max(2, specW / DancerBarL);
                double gap   = 1.0;
                double maxH  = H * 0.85;
                double floor = H - 6;
                double hueShift = (_time * 0.02) % 1.0;

                for (int i = 0; i < DancerBarL; i++)
                {
                    // Зеркально — правый бар ближе к центру
                    double t   = (double)(DancerBarL - 1 - i) / DancerBarL;
                    int    bin = Math.Clamp((int)(t * 80), 0, _smoothFft.Length - 1);
                    float  mag = _smoothFft[bin];

                    double target = Math.Clamp(mag * maxH * 9.0, 1, maxH);
                    _dBarHL[i] = _dBarHL[i] < target
                        ? _dBarHL[i] * 0.18 + target * 0.82
                        : _dBarHL[i] * 0.84 + target * 0.16;
                    double bh = Math.Max(1, _dBarHL[i]);

                    if (target > _dPeakHL[i]) _dPeakHL[i] = target;
                    else _dPeakHL[i] = Math.Max(1, _dPeakHL[i] - 0.65);

                    // Бары идут справа налево (ближайший к центру — i=0)
                    double x = specW - (i + 1) * (barW + gap) + gap;
                    if (x < 0) x = 0;

                    double barHue = (0.55 + t * 0.25 + hueShift) % 1.0; // синий -> голубой -> фиолетовый
                    var bar = _dBarsL[i];
                    bar.Fill = new LinearGradientBrush(
                        HsvToColor((barHue + 0.12) % 1.0, 0.95, 0.35),
                        HsvToColor(barHue, 0.85, 1.0),
                        new Point(0, 1), new Point(0, 0));
                    bar.Width  = barW; bar.Height = bh;
                    Canvas.SetLeft(bar, x); Canvas.SetTop(bar, floor - bh);
                    bar.Opacity = Math.Clamp(0.50 + mag * 10, 0.35, 1.0);

                    var pk = _dPeaksL[i];
                    pk.Width = barW;
                    Canvas.SetLeft(pk, x); Canvas.SetTop(pk, floor - _dPeakHL[i] - 2);
                }
            }

            // ─── ПРАВЫЙ СПЕКТР (высокие частоты, тёплые оттенки) ────────────────
            {
                double barW  = Math.Max(2, specW / DancerBarR);
                double gap   = 1.0;
                double maxH  = H * 0.85;
                double floor = H - 6;
                double offsetX = W * 0.67;
                double hueShift = (_time * 0.02) % 1.0;

                for (int i = 0; i < DancerBarR; i++)
                {
                    double t   = (double)i / DancerBarR;
                    // Высокие частоты: 130..450
                    int    bin = Math.Clamp(130 + (int)(t * 320), 0, _smoothFft.Length - 1);
                    float  mag = _smoothFft[bin];

                    double target = Math.Clamp(mag * maxH * 12.0, 1, maxH);
                    _dBarHR[i] = _dBarHR[i] < target
                        ? _dBarHR[i] * 0.18 + target * 0.82
                        : _dBarHR[i] * 0.84 + target * 0.16;
                    double bh = Math.Max(1, _dBarHR[i]);

                    if (target > _dPeakHR[i]) _dPeakHR[i] = target;
                    else _dPeakHR[i] = Math.Max(1, _dPeakHR[i] - 0.65);

                    double x = offsetX + i * (barW + gap);

                    double barHue = (0.0 + t * 0.25 + hueShift) % 1.0; // красный -> жёлтый -> оранжевый
                    var bar = _dBarsR[i];
                    bar.Fill = new LinearGradientBrush(
                        HsvToColor((barHue + 0.10) % 1.0, 0.95, 0.35),
                        HsvToColor(barHue, 0.90, 1.0),
                        new Point(0, 1), new Point(0, 0));
                    bar.Width  = barW; bar.Height = bh;
                    Canvas.SetLeft(bar, x); Canvas.SetTop(bar, H - 6 - bh);
                    bar.Opacity = Math.Clamp(0.50 + mag * 10, 0.35, 1.0);

                    var pk = _dPeaksR[i];
                    pk.Width = barW;
                    Canvas.SetLeft(pk, x); Canvas.SetTop(pk, H - 6 - _dPeakHR[i] - 2);
                }
            }

            // ─── КИНЕМАТИКА ТАНЦОРА ─────────────────────────────────────────────
            // Масштаб танцора к экрану
            double bodyScale = Math.Min(W, H) * 0.46;
            double headR     = bodyScale * 0.10;
            double torsoLen  = bodyScale * 0.28;
            double armLen    = bodyScale * 0.26;
            double legLen    = bodyScale * 0.32;

            // Базовая точка — шея (чуть выше центра)
            double neckX = dancerCx;
            double neckY = cy - bodyScale * 0.05 + Math.Sin(_time * 2.0) * _bassEnergy * bodyScale * 0.06;

            // Фаза позы ускоряется на бите
            _posePhase += 0.016 * (1.0 + _bassEnergy * 14.0 + _midEnergy * 6.0);

            // Координаты суставов — процедурная анимация
            // Туловище колышется
            double sway    = Math.Sin(_posePhase * 0.8) * 0.15;
            double bounce  = Math.Abs(Math.Sin(_posePhase * 1.6)) * bodyScale * 0.04;

            // Шея (= верх туловища)
            _jointX[0] = neckX + sway * bodyScale * 0.08;
            _jointY[0] = neckY - bounce;

            // Бёдра (= низ туловища)
            double hipX = neckX - sway * bodyScale * 0.06;
            double hipY = neckY + torsoLen;

            // Плечи
            double shoulderSpread = bodyScale * 0.18 + Math.Sin(_posePhase * 0.7) * bodyScale * 0.04;
            _jointX[1] = _jointX[0] - shoulderSpread;  // плечо L
            _jointY[1] = _jointY[0] + bodyScale * 0.04;
            _jointX[2] = _jointX[0] + shoulderSpread;  // плечо R
            _jointY[2] = _jointY[1];

            // Руки — динамичное размахивание
            double armPhase = _posePhase * 1.05;
            double arm1Ang = Math.Sin(armPhase)        * 0.9 + Math.Sin(armPhase * 1.7) * 0.3 - 0.4;
            double arm2Ang = Math.Sin(armPhase + Math.PI) * 0.9 + 0.4;

            // Локоть L
            _jointX[3] = _jointX[1] + Math.Cos(Math.PI + arm1Ang) * armLen * 0.5;
            _jointY[3] = _jointY[1] + Math.Sin(Math.PI + arm1Ang) * armLen * 0.5;
            // Кисть L
            _jointX[5] = _jointX[3] + Math.Cos(Math.PI + arm1Ang + Math.Sin(_posePhase * 1.3) * 0.6) * armLen * 0.55;
            _jointY[5] = _jointY[3] + Math.Sin(Math.PI + arm1Ang + Math.Sin(_posePhase * 1.3) * 0.6) * armLen * 0.55;

            // Локоть R
            _jointX[4] = _jointX[2] + Math.Cos(arm2Ang) * armLen * 0.5;
            _jointY[4] = _jointY[2] + Math.Sin(arm2Ang) * armLen * 0.5;
            // Кисть R
            _jointX[6] = _jointX[4] + Math.Cos(arm2Ang + Math.Sin(_posePhase * 1.4) * 0.7) * armLen * 0.55;
            _jointY[6] = _jointY[4] + Math.Sin(arm2Ang + Math.Sin(_posePhase * 1.4) * 0.7) * armLen * 0.55;

            // Бёдра
            double legPhase = _posePhase * 1.25;
            _jointX[7] = hipX - bodyScale * 0.09;  // бедро L
            _jointY[7] = hipY;
            _jointX[8] = hipX + bodyScale * 0.09;  // бедро R
            _jointY[8] = hipY;

            // Колени
            double legAng1 = Math.Sin(legPhase) * 0.7 + 0.2;
            double legAng2 = Math.Sin(legPhase + Math.PI) * 0.7 - 0.2;
            _jointX[9]  = _jointX[7] + Math.Sin(legAng1) * legLen * 0.5;
            _jointY[9]  = _jointY[7] + Math.Cos(legAng1) * legLen * 0.5;
            _jointX[10] = _jointX[8] + Math.Sin(legAng2) * legLen * 0.5;
            _jointY[10] = _jointY[8] + Math.Cos(legAng2) * legLen * 0.5;

            // Стопы
            _jointX[11] = _jointX[9]  + Math.Sin(legAng1 * 0.7) * legLen * 0.52;
            _jointY[11] = _jointY[9]  + Math.Cos(legAng1 * 0.7) * legLen * 0.52;
            _jointX[12] = _jointX[10] + Math.Sin(legAng2 * 0.7) * legLen * 0.52;
            _jointY[12] = _jointY[10] + Math.Cos(legAng2 * 0.7) * legLen * 0.52;

            // Плавное сглаживание суставов
            for (int j = 0; j < 13; j++)
            {
                _jointX2[j] = _jointX2[j] * 0.55 + _jointX[j] * 0.45;
                _jointY2[j] = _jointY2[j] * 0.55 + _jointY[j] * 0.45;
            }

            // ─── ЦВЕТ ТАНЦОРА ───────────────────────────────────────────────────
            double dancerHue = (_time * 0.07) % 1.0;
            var    dancerCol = HsvToColor(dancerHue, 0.90, 1.0);
            var    dancerCol2 = HsvToColor((dancerHue + 0.4) % 1.0, 0.85, 1.0);
            byte   dancerAlpha = (byte)(180 + _bassEnergy * 75);

            // ─── СТРОИМ PathGeometry для тела ───────────────────────────────────
            // Сегменты: шея→бедро, плечо→локоть→кисть (x2), бедро→колено→стопа (x2)
            var pg = new PathGeometry();

            void AddSegment(int a, int b)
            {
                var seg = new PathFigure { StartPoint = new Point(_jointX2[a], _jointY2[a]) };
                seg.Segments.Add(new LineSegment(new Point(_jointX2[b], _jointY2[b]), true));
                pg.Figures.Add(seg);
            }

            // Позвоночник (шея → бёдра через центр)
            var spine = new PathFigure { StartPoint = new Point(_jointX2[0], _jointY2[0]) };
            spine.Segments.Add(new BezierSegment(
                new Point(hipX + sway * bodyScale * 0.12, neckY + torsoLen * 0.35),
                new Point(hipX - sway * bodyScale * 0.12, neckY + torsoLen * 0.65),
                new Point(hipX, hipY), true));
            pg.Figures.Add(spine);

            // Плечи (L-плечо → R-плечо через шею)
            AddSegment(1, 0); AddSegment(0, 2);

            // Руки
            AddSegment(1, 3); AddSegment(3, 5);  // левая рука
            AddSegment(2, 4); AddSegment(4, 6);  // правая рука

            // Тазовая перемычка
            AddSegment(7, 8);

            // Ноги
            AddSegment(7, 9);  AddSegment(9,  11);  // левая нога
            AddSegment(8, 10); AddSegment(10, 12);  // правая нога

            // Применяем к glow и основному силуэту
            _dancerGlow.Data = pg;
            _lgbDancerGlow.GradientStops[0].Color = Color.FromArgb((byte)(dancerAlpha / 3), dancerCol.R,  dancerCol.G,  dancerCol.B);
            _lgbDancerGlow.GradientStops[1].Color = Color.FromArgb((byte)(dancerAlpha / 3), dancerCol2.R, dancerCol2.G, dancerCol2.B);
            _dancerGlow.Stroke = _lgbDancerGlow;
            _dancerGlow.StrokeThickness = 16 + _bassEnergy * 20;
            _dancerGlow.Opacity = 0.35 + _bassEnergy * 0.45;
            _dancerGlowBlur.Radius = 10 + _bassEnergy * 18;
            _dancerGlow.Effect = _dancerGlowBlur;

            _dancerBody.Data = pg;
            _lgbDancerBody.GradientStops[0].Color = Color.FromArgb(dancerAlpha, dancerCol.R,  dancerCol.G,  dancerCol.B);
            _lgbDancerBody.GradientStops[1].Color = Color.FromArgb(dancerAlpha, dancerCol2.R, dancerCol2.G, dancerCol2.B);
            _dancerBody.Stroke = _lgbDancerBody;
            _dancerBody.StrokeThickness = 3.0 + _bassEnergy * 2.0;

            // ─── ГОЛОВА ─────────────────────────────────────────────────────────
            double hx = _jointX2[0] + Math.Sin(_posePhase * 0.6) * headR * 0.4;
            double hy = _jointY2[0] - headR * 1.1;

            _dancerHeadG.Width  = headR * 3; _dancerHeadG.Height = headR * 3;
            Canvas.SetLeft(_dancerHeadG, hx - headR * 1.5); Canvas.SetTop(_dancerHeadG, hy - headR * 1.5);
            _dancerHeadG.Stroke = new SolidColorBrush(Color.FromArgb((byte)(dancerAlpha / 2),
                dancerCol.R, dancerCol.G, dancerCol.B));
            _dancerHeadG.StrokeThickness = 8 + _bassEnergy * 14;
            _scbDancerHeadG.Color = Color.FromArgb((byte)(dancerAlpha / 2), dancerCol.R, dancerCol.G, dancerCol.B);
            _dancerHeadG.Stroke   = _scbDancerHeadG;
            _dancerHeadGBlur.Radius = 8 + _bassEnergy * 12;
            _dancerHeadG.Effect = _dancerHeadGBlur;

            _dancerHead.Width  = headR * 2; _dancerHead.Height = headR * 2;
            Canvas.SetLeft(_dancerHead, hx - headR); Canvas.SetTop(_dancerHead, hy - headR);
            _scbDancerHead.Color = Color.FromArgb(dancerAlpha, dancerCol.R, dancerCol.G, dancerCol.B);
            _dancerHead.Stroke   = _scbDancerHead;
            _dancerHead.StrokeThickness = 3.0 + _bassEnergy * 2.0;

            // ─── ЛУЧИ от силуэта ────────────────────────────────────────────────
            for (int ri = 0; ri < DancerRayCount; ri++)
            {
                double rayAng = ri * Math.PI * 2 / DancerRayCount + _time * 0.6;
                double rayLen = bodyScale * (0.25 + _bassEnergy * 1.2 + _midEnergy * 0.5)
                              * (0.6 + 0.4 * Math.Sin(_posePhase * 1.1 + ri * 0.7));
                double rayHue = (dancerHue + (double)ri / DancerRayCount * 0.5) % 1.0;
                var    rayCol = HsvToColor(rayHue, 0.90, 1.0);
                byte   rayAl  = (byte)(30 + _bassEnergy * 180 + _midEnergy * 80);

                var ray = _dancerRays[ri];
                ray.X1 = dancerCx; ray.Y1 = cy;
                ray.X2 = dancerCx + Math.Cos(rayAng) * rayLen;
                ray.Y2 = cy       + Math.Sin(rayAng) * rayLen;
                var lgbRay = _lgbDancerRays[ri];
                lgbRay.GradientStops[0].Color = Color.FromArgb(rayAl, rayCol.R, rayCol.G, rayCol.B);
                lgbRay.GradientStops[1].Color = Color.FromArgb(0,     rayCol.R, rayCol.G, rayCol.B);
                ray.Stroke          = lgbRay;
                ray.Opacity         = 0.6 + _bassEnergy * 0.4;
                ray.StrokeThickness = 0.8 + _bassEnergy * 1.5;
            }

            // ─── АУРЫ ───────────────────────────────────────────────────────────
            for (int ai = 0; ai < DancerAuraCount; ai++)
            {
                double af = (double)ai / DancerAuraCount;
                double ar = bodyScale * (0.22 + af * 0.28)
                          + Math.Sin(_time * 2.5 + ai * 1.3) * _bassEnergy * bodyScale * 0.10;
                double aHue = (dancerHue + af * 0.35 + _time * 0.04) % 1.0;
                var    aCol = HsvToColor(aHue, 0.80, 0.95);
                byte   aAl  = (byte)(20 + (1 - af) * 80 + _bassEnergy * 100);

                var aura = _dancerAuras[ai];
                aura.Width  = ar * 2; aura.Height = ar * 2;
                Canvas.SetLeft(aura, dancerCx - ar); Canvas.SetTop(aura, cy - ar);
                _scbDancerAura.Color = Color.FromArgb(aAl, aCol.R, aCol.G, aCol.B);
                aura.Stroke          = _scbDancerAura;
                aura.StrokeThickness = 1.0 + _bassEnergy * 3.0;
                _dancerAuraBlurs[ai].Radius = 3 + _bassEnergy * 8;
                aura.Effect = _dancerAuraBlurs[ai];
            }

            // ─── ВОЛНА ПОД НОГАМИ ───────────────────────────────────────────────
            {
                double groundY = Math.Max(_jointY2[11], _jointY2[12]) + 5;
                groundY = Math.Min(groundY, H - 10);
                double waveW   = W * 0.34;
                double waveX0  = dancerCx - waveW * 0.5;
                double waveAmp = bodyScale * 0.04 + _bassEnergy * bodyScale * 0.12;
                int    wPts    = 60;

                var gwPts = _groundWavePoints;
                gwPts.Clear();
                for (int xi = 0; xi < wPts; xi++)
                {
                    double t   = (double)xi / (wPts - 1);
                    double gx  = waveX0 + t * waveW;
                    double env = Math.Sin(t * Math.PI);
                    double gy  = groundY
                        + Math.Sin(t * Math.PI * 5.0 + _posePhase * 2.0) * waveAmp * env
                        + Math.Sin(t * Math.PI * 9.0 + _posePhase * 3.1) * waveAmp * 0.4 * env;
                    gwPts.Add(new Point(gx, gy));
                }
                _dancerGroundWave.Points = gwPts;
                double gwHue = (dancerHue + 0.55) % 1.0;
                var    gwCol = HsvToColor(gwHue, 0.90, 1.0);
                byte   gwAl  = (byte)(80 + _bassEnergy * 170);
                _lgbGroundWave.GradientStops[0].Color = Color.FromArgb(0,    gwCol.R, gwCol.G, gwCol.B);
                _lgbGroundWave.GradientStops[1].Color = Color.FromArgb(gwAl, gwCol.R, gwCol.G, gwCol.B);
                _lgbGroundWave.GradientStops[2].Color = Color.FromArgb(0,    gwCol.R, gwCol.G, gwCol.B);
                _dancerGroundWave.Stroke = _lgbGroundWave;
            }

            // ─── ЧАСТИЦЫ вокруг танцора ─────────────────────────────────────────
            {
                // Генерация новых частиц от рук/ног/головы
                if (_provider.IsPlaying && _dParticles.Count < 120)
                {
                    // От кистей
                    int[] emitters = { 5, 6, 11, 12, 0 };
                    foreach (int ej in emitters)
                    {
                        if (_rng.NextDouble() < 0.20 + _bassEnergy * 0.6)
                        {
                            double pHue = (_time * 0.08 + (double)ej / 13.0) % 1.0;
                            _dParticles.Add(new DancerParticle
                            {
                                X     = _jointX2[ej],
                                Y     = _jointY2[ej],
                                Vx    = (_rng.NextDouble() - 0.5) * 4.0,
                                Vy    = (_rng.NextDouble() - 0.5) * 4.0 - 1.0,
                                Life  = 1.0,
                                Decay = 0.015 + _rng.NextDouble() * 0.020,
                                Size  = 2.0 + _bassEnergy * 10 + _rng.NextDouble() * 3,
                                Hue   = pHue,
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
                        // Убираем элемент с канваса и возвращаем в пул
                        if (p.El != null) _canvas.Children.Remove(p.El);
                        _dParticles.RemoveAt(pi);
                        continue;
                    }

                    double sz   = p.Size * p.Life;
                    byte   pal  = (byte)(p.Life * 220);
                    var    pcol = HsvToColor(p.Hue, 0.90, 1.0);

                    // Переиспользуем Ellipse вместо new каждый кадр
                    if (p.El == null)
                    {
                        p.El = new Ellipse();
                        _canvas.Children.Add(p.El);
                    }
                    p.El.Width  = sz;
                    p.El.Height = sz;
                    p.El.Fill   = new SolidColorBrush(Color.FromArgb(pal, pcol.R, pcol.G, pcol.B));
                    // BlurEffect только на крупных частицах (дорогая операция)
                    if (sz > 6)
                    {
                        if (p.El.Effect is BlurEffect be) be.Radius = sz * 0.5;
                        else p.El.Effect = new BlurEffect { Radius = sz * 0.5 };
                    }
                    else
                    {
                        p.El.Effect = null;
                    }
                    Canvas.SetLeft(p.El, p.X - sz / 2);
                    Canvas.SetTop(p.El,  p.Y - sz / 2);
                }
            }
        }



        // ─── BuildNeonCity ────────────────────────────────────────────────────────
        private void BuildNeonCity()
        {
            _neonBuilt = true;
            // Вертикальные неоновые колонны — 3 слоя: широкий glow, средний, тонкий яркий
            for (int i = 0; i < NeonLineCount * 3; i++)
            {
                var ln = new Line { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _canvas.Children.Add(ln);
                _neonLines[i] = ln;
            }
            // Горизонтальные сканлайны
            for (int i = 0; i < NeonScanCount; i++)
            {
                var ln = new Line();
                _canvas.Children.Add(ln);
                _neonScans[i] = ln;
            }
            // Светящиеся точки на вершинах колонн
            for (int i = 0; i < NeonLineCount; i++)
            {
                var el = new Ellipse { Fill = Brushes.Transparent };
                _canvas.Children.Add(el);
                _neonGlows[i] = el;
                // Инициализируем кэш BlurEffect
                _neonGlowBlurs[i] = new BlurEffect { Radius = 8  };
                _neonMidBlurs[i]  = new BlurEffect { Radius = 3  };
                _neonGelBlurs[i]  = new BlurEffect { Radius = 5  };
            }
        }

        // ─── NEON CITY ────────────────────────────────────────────────────────────
        // Ночной город: вертикальные неоновые колонны-здания реагируют на спектр.
        // Снизу — отражение в «мокром асфальте». Горизонтальные сканлайны
        // медленно ползут вниз. На бит — вспышка по всей сцене.
        private void RenderNeonCity(double W, double H)
        {
            foreach (var ln in _neonLines) ln.Visibility = Visibility.Visible;
            foreach (var ln in _neonScans) ln.Visibility = Visibility.Visible;
            foreach (var el in _neonGlows) el.Visibility = Visibility.Visible;

            // Тёмный сине-фиолетовый ночной фон
            double flash = _bassEnergy * 0.35;
            _bgBrush.GradientStops[0].Color = Color.FromRgb(
                (byte)(0x02 + flash * 0x28), (byte)(0x00 + flash * 0x10), (byte)(0x12 + flash * 0x30));
            _bgBrush.GradientStops[1].Color = Color.FromRgb(
                (byte)(0x01 + flash * 0x18), (byte)(0x00 + flash * 0x08), (byte)(0x08 + flash * 0x20));
            _bgBrush.GradientStops[2].Color = Color.FromRgb(
                (byte)(0x03 + flash * 0x20), (byte)(0x00 + flash * 0x0C), (byte)(0x18 + flash * 0x28));

            double bassBoost  = 1.0 + _bassEnergy * 7.0;
            double floor      = H * 0.75; // линия горизонта / асфальт
            double colW       = W / NeonLineCount;

            for (int i = 0; i < NeonLineCount; i++)
            {
                double t      = (double)i / NeonLineCount;
                double cx     = (i + 0.5) * colW;

                // Высота колонны: статичная основа здания + динамика от спектра
                int    bin    = Math.Clamp((int)(Math.Pow(_smoothFft.Length * 0.72, t)), 0, _smoothFft.Length - 1);
                float  mag    = _smoothFft[bin];
                // Каждое здание имеет уникальную базовую высоту (силуэт города)
                double baseH  = floor * (0.18 + Math.Sin(i * 1.37 + 0.5) * 0.13 + Math.Cos(i * 0.73) * 0.09);
                double dynH   = mag * floor * 0.95 * bassBoost;
                double colH   = Math.Clamp(Math.Max(baseH, dynH), floor * 0.12, floor * 0.97);
                double top    = floor - colH;

                // Цвет: каждая колонна своего неонового оттенка
                double hue   = (t * 0.75 + _time * 0.018) % 1.0;
                var    col   = HsvToColor(hue, 0.95, 1.0);
                var    colDim = HsvToColor(hue, 0.85, 0.55);
                double bw    = Math.Max(3.0, colW * 0.28 + mag * colW * 0.35);
                byte   al    = (byte)(120 + mag * 135 + _bassEnergy * 80);

                // ── Слой 0: широкий мягкий glow ────────────────────────────────
                var lg = _neonLines[i];
                lg.X1 = cx; lg.Y1 = top;
                lg.X2 = cx; lg.Y2 = floor;
                lg.Stroke          = new SolidColorBrush(Color.FromArgb((byte)(al / 5), col.R, col.G, col.B));
                lg.StrokeThickness = bw * 5.0;
                _neonGlowBlurs[i].Radius = 8 + _bassEnergy * 14;
                lg.Effect          = _neonGlowBlurs[i];

                // ── Слой 1: средний ─────────────────────────────────────────────
                var lm = _neonLines[NeonLineCount + i];
                lm.X1 = cx; lm.Y1 = top;
                lm.X2 = cx; lm.Y2 = floor;
                var lgbNM = _lgbNeonMid[i];
                lgbNM.GradientStops[0].Color = Color.FromArgb((byte)(al * 0.6), col.R, col.G, col.B);
                lgbNM.GradientStops[1].Color = Color.FromArgb(al, col.R, col.G, col.B);
                lgbNM.GradientStops[2].Color = Color.FromArgb((byte)(al * 0.3), colDim.R, colDim.G, colDim.B);
                lm.Stroke = lgbNM;
                lm.StrokeThickness = bw * 2.2;
                _neonMidBlurs[i].Radius = 3 + mag * 5;
                lm.Effect          = _neonMidBlurs[i];

                // ── Слой 2: тонкая яркая линия поверх ──────────────────────────
                var lb = _neonLines[NeonLineCount * 2 + i];
                lb.X1 = cx; lb.Y1 = top;
                lb.X2 = cx; lb.Y2 = floor;
                _scbNeonLb.Color   = Color.FromArgb((byte)Math.Min(255, al + 60), col.R, col.G, col.B);
                lb.Stroke          = _scbNeonLb;
                lb.StrokeThickness = Math.Max(0.8, bw * 0.45);
                lb.Effect          = null;

                // ── Отражение в асфальте (зеркально вниз, быстро затухает) ─────
                // (реализовано через смещение верхней линии вниз с угасанием opacity)
                // Используем lb для отражения нет — оно уже занято.
                // Отражение делаем через glow ellipse внизу.

                // ── Светящаяся точка на вершине колонны ────────────────────────
                double gr  = bw * 1.2 + mag * bw * 2.5 + _bassEnergy * bw * 3.0;
                var    gel = _neonGlows[i];
                gel.Width  = gr * 2; gel.Height = gr * 2;
                Canvas.SetLeft(gel, cx - gr); Canvas.SetTop(gel, top - gr);
                gel.Fill   = new RadialGradientBrush(
                    Color.FromArgb((byte)(al), col.R, col.G, col.B),
                    Color.FromArgb(0, col.R, col.G, col.B));
                _neonGelBlurs[i].Radius = gr * 0.8 + _bassEnergy * 6;
                gel.Effect = _neonGelBlurs[i];
            }

            // ── Горизонтальные сканлайны ──────────────────────────────────────
            for (int si = 0; si < NeonScanCount; si++)
            {
                double scanT = ((double)si / NeonScanCount + _time * 0.04) % 1.0;
                double scanY = scanT * H;
                double scanHue = (_time * 0.03 + si * 0.17) % 1.0;
                var    scanCol = HsvToColor(scanHue, 0.70, 0.90);
                byte   scanAl  = (byte)(8 + (1.0 - Math.Abs(scanT - 0.5) * 2.0) * 22 + _bassEnergy * 15);

                var sln = _neonScans[si];
                sln.X1 = 0;  sln.Y1 = scanY;
                sln.X2 = W;  sln.Y2 = scanY;
                _scbNeonScan.Color  = Color.FromArgb(scanAl, scanCol.R, scanCol.G, scanCol.B);
                sln.Stroke          = _scbNeonScan;
                sln.StrokeThickness = 0.6 + _midEnergy * 1.5;
            }

            // ── Горизонтальная линия асфальта ─────────────────────────────────
            _neonScans[0].X1 = 0;      _neonScans[0].Y1 = floor;
            _neonScans[0].X2 = W;      _neonScans[0].Y2 = floor;
            _scbNeonScan.Color = Color.FromArgb((byte)(40 + _bassEnergy * 60), 0x44, 0x44, 0x88);
            _neonScans[0].Stroke          = _scbNeonScan;
            _neonScans[0].StrokeThickness = 1.0;
        }

        // Кэш BlurEffect для MagmaFlow — не создаём новые объекты каждый кадр
        private readonly BlurEffect[] _magmaBlurEffects = new BlurEffect[MagmaLineCount];

        private void BuildMagmaFlow()
        {
            _magmaBuilt = true;
            for (int i = 0; i < MagmaLineCount; i++)
            {
                var pl = new Polyline
                {
                    StrokeLineJoin     = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Round,
                };
                _canvas.Children.Add(pl);
                _magmaLines[i] = pl;
                // Создаём BlurEffect один раз, обновляем Radius в кадре
                _magmaBlurEffects[i] = new BlurEffect { Radius = 2.0 };
            }
        }

        // ─── 9★. MAGMA FLOW ───────────────────────────────────────────────────────
        // Тёмный фон с потоками расплавленной лавы.
        // Нижние слои — жёлто-оранжевые жилы, верхние — тёмно-красные с пульсом.
        // На удар баса поверхность «взрывается» вверх.
        private void RenderMagmaFlow(double W, double H)
        {
            foreach (var pl in _magmaLines) pl.Visibility = Visibility.Visible;

            // Тёмный фон — перекрашиваем через базовый UpdateBackground (уже вызван)
            // Дополнительно делаем фон глубоко-тёмным для контраста с лавой
            _bgBrush.GradientStops[0].Color = Color.FromRgb(
                (byte)(0x08 + _bassEnergy * 0x18), 0x01, 0x01);
            _bgBrush.GradientStops[1].Color = Color.FromRgb(
                (byte)(0x04 + _bassEnergy * 0x10), 0x00, 0x00);
            _bgBrush.GradientStops[2].Color = Color.FromRgb(
                (byte)(0x06 + _bassEnergy * 0x0C), 0x01, 0x01);

            double bassBoost = 1.0 + _bassEnergy * 8.0;
            double midBoost  = 1.0 + _midEnergy  * 4.0;

            for (int li = 0; li < MagmaLineCount; li++)
            {
                double layerT = (double)li / MagmaLineCount; // 0 = дно, 1 = поверхность

                // Позиция по Y: нижние слои ближе ко дну
                double baseY  = H * (0.55 + layerT * 0.42);

                // Цвет: дно — ярко-жёлтое, поверхность — тёмно-красное/бордовое
                double hue   = 0.08 - layerT * 0.08; // жёлтый → красный → бордовый
                double sat   = 0.85 + layerT * 0.12;
                double bri   = Math.Clamp(1.0 - layerT * 0.55 + _bassEnergy * 0.35, 0.15, 1.0);
                byte   alpha = (byte)(30 + layerT * 160 + _bassEnergy * (80 - layerT * 60));

                // Толщина: нижние — тонкие жилы, верхние — широкие потоки
                double thick = 1.0 + layerT * 14.0 + _smoothFft[
                    Math.Clamp((int)(layerT * 120), 0, _smoothFft.Length - 1)] * 22.0;

                // Амплитуда волны: нижние слои мелко рябят, верхние — большие всплески на бас
                double amp   = H * (0.018 + layerT * 0.06) * bassBoost;
                double freq  = 2.5 - layerT * 1.2; // нижние — высокая частота волн
                double phase = _time * (0.18 + layerT * 0.28) + li * 0.55;

                // Бас-всплеск: только для верхних слоёв (layerT > 0.5)
                double spikeAmp = layerT > 0.5
                    ? H * _bassEnergy * 0.22 * (layerT - 0.5) * 2.0
                    : 0;

                int fftBin = Math.Clamp((int)(layerT * 160), 0, _smoothFft.Length - 1);
                double fftPush = _smoothFft[fftBin] * H * 0.28 * midBoost;

                var pts = _magmaPoints[li];
                pts.Clear();
                for (int xi = 0; xi < MagmaPts; xi++)
                {
                    double t = (double)xi / (MagmaPts - 1);
                    double x = t * W;

                    // Основная волна
                    double y = baseY
                        + Math.Sin(t * Math.PI * freq + phase) * amp
                        + Math.Sin(t * Math.PI * freq * 1.7 + phase * 1.3) * amp * 0.45
                        + Math.Sin(t * Math.PI * freq * 3.1 + phase * 2.1) * amp * 0.20;

                    // FFT-выдавливание вверх
                    int fb = Math.Clamp((int)(t * 200), 0, _smoothFft.Length - 1);
                    y -= _smoothFft[fb] * fftPush * Math.Sin(t * Math.PI);

                    // Бас-всплеск (острые пики вверх)
                    if (spikeAmp > 0)
                    {
                        // Несколько случайных пиков привязанных к FFT
                        for (int sk = 0; sk < 5; sk++)
                        {
                            double spikeX = ((sk * 0.19 + layerT * 0.13 + _time * 0.04 * (sk + 1)) % 1.0);
                            double dist   = Math.Abs(t - spikeX);
                            if (dist < 0.12)
                                y -= spikeAmp * Math.Pow(1.0 - dist / 0.12, 3.0);
                        }
                    }

                    pts.Add(new Point(x, y));
                }

                var col  = HsvToColor(hue, sat, bri);
                var col2 = HsvToColor((hue + 0.04) % 1.0, sat, Math.Min(1.0, bri + 0.2));
                var pl   = _magmaLines[li];
                pl.Points = pts;

                // Градиент вдоль линии — обновляем кешированный LGB
                var lgbM = _lgbMagma[li];
                lgbM.GradientStops[0].Color = Color.FromArgb((byte)(alpha * 0.4), col.R,  col.G,  col.B);
                lgbM.GradientStops[1].Color = Color.FromArgb(alpha,               col2.R, col2.G, col2.B);
                lgbM.GradientStops[2].Color = Color.FromArgb((byte)(alpha * 0.4), col.R,  col.G,  col.B);
                pl.Stroke = lgbM;
                pl.StrokeThickness = Math.Max(0.5, thick);

                // Размытие для нижних жил — обновляем Radius кэшированного эффекта
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
        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 1.0) + 1.0) % 1.0;
            v = Math.Clamp(v, 0, 1); s = Math.Clamp(s, 0, 1);
            int    hi = (int)(h * 6) % 6;
            double f  = h * 6 - Math.Floor(h * 6);
            double p  = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            var (r, g, b) = hi switch
            {
                0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
                3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
            };
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }

    // ★ Частица вокруг танцора
    internal class DancerParticle
    {
        public double X, Y, Vx, Vy, Life, Decay, Size, Hue;
        public Ellipse? El; // переиспользуемый UI-элемент (пул)
    }
}
