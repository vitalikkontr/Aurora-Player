// MainWindow.cs — ядро: поля, конструктор, IVisualizerDataProvider
using Microsoft.Win32;
using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow : Window, IVisualizerDataProvider
    {
        // ─── Константы ────────────────────────────────────────────────────────────
        internal const int FfmpegOutputSampleRate = 48000;
        internal const int FfmpegOutputChannels   = 2;

        // ─── Аудио-движок ─────────────────────────────────────────────────────────
        private readonly AudioOutputEngine _audioOutput;
        private AudioFileReader?    _audioReader;
        private WaveStream?         _mfReader;
        private CueSegmentProvider? _cueSegment;
        private FfmpegCueSegment?   _ffmpegCueSegment;
        private string?             _ffmpegDecodeTempWavPath;

        // Кеш открытого FLAC/WAV файла
        private AudioFileReader? _cachedAudioReader;
        private string?          _cachedAudioPath;

        private SurroundProvider?   _surround;
        private EqualizerProvider?  _equalizer;
        private CompressorProvider? _compressor;
        private FftAggregator?      _fftAgg;
        private VolumeProvider?     _volumeProvider;

        // ─── Визуализатор ─────────────────────────────────────────────────────────
        private VisualizerWindow?  _visualizerWindow;
        private VisualizerSettings _vizSettings = new();

        // ─── Состояние ────────────────────────────────────────────────────────────
        private bool   _surroundEnabled = false;
        private float  _surroundWidth   = 0.5f;
        private bool   _isPlaying, _isDraggingSlider, _isMini, _shuffle, _repeat;
        private bool   _liked, _isLoadingFolder, _isProgrammaticSelection;
        private bool   _seekHandledByMouseUp;
        private bool   _isMuted;
        private double _volumeBeforeMute = 0.75;
        private bool   _isTransitioning;
        private bool   _selectionChangedSuppressed;
        private bool   _trackEndHandled;
        private bool   _sessionRestoreDone; // используется в логике восстановления сессии
        private int    _playSession;
        private int    _albumArtSession;
        private double? _pendingSeekOnNextPlay;

        // ─── Аудио-устройство ─────────────────────────────────────────────────────
        private string? _audioDeviceName = null; // null = системное по умолчанию

        // ─── Плейлист / навигация ─────────────────────────────────────────────────
        private int     _currentIndex = -1;
        private double  _volume       = 0.75;
        private string? _lastFolder;
        private TimeSpan _cueStart = TimeSpan.Zero;
        private TimeSpan _cueEnd   = TimeSpan.Zero;

        public ObservableCollection<TrackItem> Playlist { get; } = new();

        // ─── Таймеры ──────────────────────────────────────────────────────────────
        private readonly DispatcherTimer _timer    = new();
        private readonly DispatcherTimer _vizTimer = new();
        private readonly DispatcherTimer _eqTimer  = new();

        // ─── EQ ───────────────────────────────────────────────────────────────────
        private static readonly (string Name, float[] Gains)[] EqPresets =
        {
            ("ROCK",    new float[] {  4,  3, -1,  2,  4 }),
            ("JAZZ",    new float[] {  3,  2,  0, -1,  2 }),
            ("VOCAL",   new float[] { -2,  0,  4,  3,  1 }),
            ("BASS+",   new float[] {  8,  6,  0, -2, -3 }),
            ("TREBLE+", new float[] { -3, -2,  0,  4,  7 }),
        };
        private static readonly string[] EqBandNames  = { "SUB", "BASS", "MID", "HI-M", "TREB" };
        private static readonly string[] EqFreqLabels = { "60", "250", "1k", "4k", "12k" };
        // EQ-бары: цвета формируются динамически на основе акцентов пользователя
        private System.Windows.Media.Color GetEqBarColor(int band) => band switch
        {
            0 => _accent1,
            1 => System.Windows.Media.Color.FromRgb(
                     (byte)((_accent1.R + _accent2.R) / 2),
                     (byte)((_accent1.G + _accent2.G) / 2),
                     (byte)((_accent1.B + _accent2.B) / 2)),
            2 => _accent2,
            3 => System.Windows.Media.Color.FromRgb(0xFF, 0x9A, 0x5C),
            _ => _cyan,
        };
        private readonly System.Windows.Controls.Canvas[] _eqBars     = new System.Windows.Controls.Canvas[5];
        private readonly double[]    _eqValues   = new double[5];
        private bool[]               _eqDragging = new bool[5];
        private readonly System.Windows.Controls.TextBlock[] _eqLabels = new System.Windows.Controls.TextBlock[5];
        private bool _eqPanelOpen;
        private int  _currentPreset = -1;

        // ─── Форматы файлов ───────────────────────────────────────────────────────
        // FLAC ведем через ffmpeg-path (как APE/WV), потому что на ряде файлов
        // internal AudioFileReader корректно выставляет CurrentTime, но слышимый
        // старт после reopen может идти с начала трека.
        private static readonly HashSet<string> MfOnlyExt =
            new(StringComparer.OrdinalIgnoreCase) { ".ape", ".wv", ".flac" };
        private static readonly HashSet<string> CacheableExt =
            new(StringComparer.OrdinalIgnoreCase) { ".wav", ".aiff", ".aif" };
        private static readonly HashSet<string> VorbisExt =
            new(StringComparer.OrdinalIgnoreCase) { ".ogg", ".opus" };

        // ─── Прочее UI ────────────────────────────────────────────────────────────
        private readonly System.Collections.Generic.List<System.Windows.Shapes.Rectangle> _vizBars = new();
        private readonly Random   _rng       = new();
        private readonly double[] _vizHeights;
        private const int VizBarCount = 42;

        private static readonly string[] Grad1 = { "#1A1A3E","#2D1120","#0D2233","#2A1800","#0D2010","#1E1A00" };
        private static readonly string[] Grad2 = { "#2D1B4E","#4E1A35","#1A3D5C","#4A3000","#1A3D20","#3C3000" };

        // ─── Win32 сообщения ──────────────────────────────────────────────────────
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE   = 0xF020;

        // ─── Конструктор ──────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            // Инициализируем DynamicResource-ресурсы цветов значениями по умолчанию.
            // LoadSettings() позже перезапишет их сохранёнными значениями.
            ApplyAccentColors();
            _vizHeights          = new double[VizBarCount];
            TrackList.ItemsSource = Playlist;

            _timer.Interval    = TimeSpan.FromMilliseconds(250); _timer.Tick    += Timer_Tick;
            _eqTimer.Interval  = TimeSpan.FromMilliseconds(100); _eqTimer.Tick  += EqTimer_Tick;
            _vizTimer.Interval = TimeSpan.FromMilliseconds(50);  _vizTimer.Tick += VizTimer_Tick;

            _audioOutput = new AudioOutputEngine(
                deviceNumber: AudioOutputEngine.FindDeviceByName(_audioDeviceName));
            _audioOutput.TrackEnded += OnTrackEnded;

            BuildVisualizer();
            BuildEqPanel();
            BuildMixerPanel();

            // VolumeSlider: двойной клик → сброс на 75%
            VolumeSlider.MouseDoubleClick += (s, e) => { VolumeSlider.Value = 75; e.Handled = true; };

            // ProgressSlider: колёсико → ±5 сек, двойной клик → в начало
            ProgressSlider.PreviewMouseWheel += (s, e) =>
            {
                if (!HasReader) return;
                double pos = Math.Clamp(ProgressSlider.Value + (e.Delta > 0 ? 5.0 : -5.0),
                                        0, ProgressSlider.Maximum);
                SeekTo(pos); ProgressSlider.Value = pos; e.Handled = true;
            };
            ProgressSlider.MouseDoubleClick += (s, e) =>
            {
                if (!HasReader) return;
                // Pause перед записью CurrentTime: AudioFileReader.CurrentTime меняет
                // Position потока без синхронизации — запись из UI-потока пока NAudio
                // читает из аудио-потока небезопасна без явной паузы.
                bool wasPlaying = _isPlaying;
                if (wasPlaying) _audioOutput.Pause();
                ReaderCurrentTime    = _cueStart;
                ProgressSlider.Value = 0;
                if (wasPlaying) _audioOutput.Play();
                e.Handled = true;
            };

            AllowDrop = true;
            Drop     += Window_Drop;
            UpdatePlaylistHeader();
            LocationChanged += MiniPlayer_TrySnap;
            SizeChanged     += MiniPlayer_TrySnap;

            _sideHideTimer.Tick  += (_, _) => { _sideHideTimer.Stop(); SideSlideStart(hide: true); };
            _sideSlideTimer.Tick += SideSlide_Tick;

            Loaded       += OnLoaded;
            Closing      += (_, _) => OnClosing();
            StateChanged += MainWindow_StateChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string iconPath = IOPath.Combine(
                    IOPath.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "app.ico");
                if (File.Exists(iconPath)) Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
            }
            catch { }

            // Диагностика ffmpeg
            string? ffmpeg = FfmpegService.FindFfmpeg();
            string msg = $"EXE: {Environment.ProcessPath}\nFFmpeg: {ffmpeg ?? "НЕ НАЙДЕН"}";
            FfmpegService.AppendDecodeLog("STARTUP " + msg.Replace("\n", " | "));
            if (ffmpeg == null)
                MessageBox.Show(msg, "Aurora — диагностика ffmpeg", MessageBoxButton.OK, MessageBoxImage.Warning);

            // WM hook — WM_GETMINMAXINFO + перехват SC_MINIMIZE для мини-плеера
            var hwndSrc = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            hwndSrc?.AddHook(WndProc);

            // Аргументы командной строки
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
            {
                string arg = args[1];
                if (Directory.Exists(arg))
                    _ = AddFolderAsync(arg);
                else if (IOPath.GetExtension(arg).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    AddCueFile(arg);
                else
                    AddTrack(arg);
                if (Playlist.Count > 0) { LoadTrack(0); return; }
            }
            LoadSettings();
        }

        private void OnClosing()
        {
            SaveSettings();
            StopEngine();
            DisposeCachedReader();
            _audioOutput.Dispose();
            FfmpegService.CleanupApeCache();
            _visualizerWindow?.Close();
        }

        // ─── WndProc — перехват Win32 сообщений ──────────────────────────────────
        //
        // ResizeMode = NoResize снимает кнопку сворачивания с chrome окна, поэтому
        // стандартный SC_MINIMIZE при клике на значок таскбара WPF не обрабатывает.
        // Решение: ShowWindow(SW_MINIMIZE/SW_RESTORE) напрямую через Win32.

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE  = 9;
        private const int SC_RESTORE  = 0xF120;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int  cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                // Ограничиваем максимальную высоту окна рабочей областью монитора,
                // чтобы список треков не уходил за панель задач
                var mmi     = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr mon  = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(mon, ref info))
                {
                    int maxW = info.rcWork.Right  - info.rcWork.Left;
                    int maxH = info.rcWork.Bottom - info.rcWork.Top;
                    mmi.ptMaxSize.X      = maxW;
                    mmi.ptMaxSize.Y      = maxH;
                    mmi.ptMaxPosition.X  = info.rcWork.Left;
                    mmi.ptMaxPosition.Y  = info.rcWork.Top;
                    mmi.ptMaxTrackSize.X = maxW;
                    mmi.ptMaxTrackSize.Y = maxH;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND && _isMini)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;

                if (cmd == SC_MINIMIZE)
                {
                    ShowWindow(hwnd, SW_MINIMIZE);
                    handled = true;
                    return IntPtr.Zero;
                }

                if (cmd == SC_RESTORE)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_isMini)
                        {
                            WindowState   = WindowState.Normal;
                            SizeToContent = SizeToContent.Height;
                            Width         = _miniPlayerWidth;
                            ClampWindowToWorkArea();
                        }
                    }, DispatcherPriority.Render);
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        // ─── IVisualizerDataProvider ──────────────────────────────────────────────
        public float[]? GetFftData() => _fftAgg?.FftData;
        public bool     IsPlaying   => _isPlaying;

        public string CurrentTrackTitle =>
            _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex].Title : "";

        public string CurrentArtist =>
            _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex].Artist : "";

        // ─── Общие свойства доступа к ридеру ─────────────────────────────────────
        private TimeSpan ReaderCurrentTime
        {
            get => _audioReader?.CurrentTime ?? _mfReader?.CurrentTime ?? TimeSpan.Zero;
            set
            {
                if (_audioReader != null) _audioReader.CurrentTime = value;
                else if (_mfReader != null) _mfReader.CurrentTime  = value;
            }
        }

        private bool HasReader => _audioReader != null || _mfReader != null;

        // ─── Открытие файла/папки от второго экземпляра (single-instance IPC) ──────
        public void OpenFromCommandLine(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return;

            if (Directory.Exists(arg))
            {
                _ = AddFolderAsync(arg, clearFirst: true);
            }
            else if (IOPath.GetExtension(arg).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                StopEngine(); DisposeCachedReader(); SetPlaying(false);
                Playlist.Clear(); _currentIndex = -1;
                AddCueFile(arg);
                if (Playlist.Count > 0) LoadTrack(0, autoPlay: true);
            }
            else
            {
                // Одиночный файл — очищаем плейлист и сразу играем
                StopEngine(); DisposeCachedReader(); SetPlaying(false);
                Playlist.Clear(); _currentIndex = -1;
                AddTrack(arg);
                if (Playlist.Count > 0) LoadTrack(0, autoPlay: true);
            }
        }

        // ─── Форматирование времени ───────────────────────────────────────────────
        private static string FormatTime(TimeSpan ts)
            => $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";

        // ─── Исправление кодировки тегов ─────────────────────────────────────────
        private static string FixEncoding(string input)
            => PlaylistService.FixEncoding(input);
    }
}
