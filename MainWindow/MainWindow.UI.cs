// MainWindow.UI.cs — таймеры, прогресс, громкость, surround, shuffle/repeat, chrome
#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Кэшированные ресурсы UI ─────────────────────────────────────────────
        private static readonly int[] EqMeterBinStarts = { 0, 1, 5, 23, 93 };
        private static readonly int[] EqMeterBinEnds = { 1, 5, 23, 93, int.MaxValue };

        private static readonly Geometry VolMutedGeometry = FreezeUi(
            Geometry.Parse("M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M3,3 L21,21"));
        private static readonly Geometry VolActiveGeometry = FreezeUi(
            Geometry.Parse("M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M15.54,8.46 A5,5,0,0,1,15.54,15.54 M19.07,4.93 A10,10,0,0,1,19.07,19.07"));

        private static readonly SolidColorBrush VolMutedStroke = FreezeUi(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x6B, 0x6B)));
        private static readonly SolidColorBrush VolActiveStroke = FreezeUi(new SolidColorBrush(Color.FromArgb(0x80, 0xA8, 0x9F, 0xFF)));
        private static readonly SolidColorBrush HeartInactiveFg = FreezeUi(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));

        private static T FreezeUi<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze)
                freezable.Freeze();
            return freezable;
        }

        // ─── Таймер прогресса ────────────────────────────────────────────────────

        private void Timer_Tick(object? s, EventArgs e)
        {
            if (_isDraggingSlider || !HasReader) return;

            double abs = ReaderCurrentTime.TotalSeconds;
            double readerRelPos = _cueStart > TimeSpan.Zero ? abs - _cueStart.TotalSeconds : abs;
            bool isFfmpegNonCue = _mfReader is FfmpegDecodeStream && _cueEnd <= TimeSpan.Zero;

            if (isFfmpegNonCue && _ffmpegCueSegment != null)
                _ffmpegCueSegment = null;

            double maxPos = double.IsFinite(ProgressSlider.Maximum) && ProgressSlider.Maximum > 0
                ? ProgressSlider.Maximum : 0.0;

            double rawPos;
            if (isFfmpegNonCue)
                rawPos = readerRelPos;
            else if (_cueSegment != null)
                rawPos = _cueSegment.PositionSeconds;
            else if (_ffmpegCueSegment != null)
                rawPos = _ffmpegCueSegment.PositionSeconds;
            else
                rawPos = readerRelPos;

            double relPos = double.IsFinite(rawPos) ? Math.Clamp(rawPos, 0.0, maxPos) : 0.0;

            ProgressSlider.Value = relPos;
            CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(relPos));

            if (_isPlaying && !_isTransitioning && !_trackEndHandled &&
                maxPos > 0.5 && relPos >= maxPos - 0.3)
            {
                AdvanceTrack(System.Threading.Volatile.Read(ref _playSession));
                return;
            }

            if (_isMini)
            {
                MiniTime.Text = FormatTime(TimeSpan.FromSeconds(relPos));
                if (MiniProgressBar != null)
                {
                    double ratio = maxPos > 0 ? Math.Clamp(relPos / maxPos, 0.0, 1.0) : 0.0;
                    double containerW = MiniProgressBar.Parent is Grid g && g.ActualWidth > 0
                        ? g.ActualWidth
                        : MiniPlayer.ActualWidth;
                    if (containerW > 0)
                        MiniProgressBar.Width = containerW * ratio;
                }
            }
        }

        // ─── EQ Meter Tick ────────────────────────────────────────────────────────

        private void EqTimer_Tick(object? s, EventArgs e)
        {
            if (!_isPlaying) return;

            float[]? fft = GetFftData();
            bool hasData = _fftAgg != null && _fftAgg.HasData && fft != null;
            const double maxH = 18.0;

            for (int i = 0; i < 5; i++)
            {
                Rectangle? bar = i switch
                {
                    0 => Eq1,
                    1 => Eq2,
                    2 => Eq3,
                    3 => Eq4,
                    _ => Eq5
                };
                if (bar == null) continue;

                double currH = double.IsFinite(bar.Height) ? bar.Height : 3.0;

                if (hasData && fft != null)
                {
                    int fftLen = fft.Length;
                    float peak = 0f;
                    int end = Math.Min(EqMeterBinEnds[i], fftLen);
                    for (int b = EqMeterBinStarts[i]; b < end; b++)
                    {
                        float v = fft[b];
                        if (float.IsFinite(v) && v > peak) peak = v;
                    }

                    double targetH = Math.Clamp(peak * maxH * 18.0, 3.0, maxH);
                    bar.Height = targetH > currH
                        ? currH + (targetH - currH) * 0.85
                        : currH + (targetH - currH) * 0.35;
                }
                else
                {
                    bar.Height = Math.Max(currH * 0.70, 3.0);
                }
            }
        }

        // ─── Visualizer Tick (встроенные бары) ───────────────────────────────────

        private void VizTimer_Tick(object? s, EventArgs e)
        {
            if (VisualizerCanvas == null || _vizBars.Count == 0) return;

            double h = VisualizerCanvas.ActualHeight > 0 ? VisualizerCanvas.ActualHeight : 36;
            float[]? fft = GetFftData();
            bool hasData = _fftAgg != null && _fftAgg.HasData && _isPlaying && fft != null && fft.Length > 0;
            int count = Math.Min(_vizBars.Count, _vizHeights.Length);

            if (hasData && fft != null)
            {
                int fftLen = fft.Length;
                for (int i = 0; i < count; i++)
                {
                    double tLog = (double)i / count;
                    int bin = Math.Clamp((int)Math.Pow(fftLen, tLog), 0, fftLen - 1);
                    float mag = fft[bin];
                    if (!float.IsFinite(mag)) mag = 0f;

                    double target = Math.Clamp(mag * h * 6.0, 2.0, h);
                    double curr = _vizHeights[i];
                    _vizHeights[i] = target > curr
                        ? curr * 0.2 + target * 0.8
                        : curr * 0.82 + target * 0.18;

                    _vizBars[i].Height = Math.Max(_vizHeights[i], 2.0);
                    Canvas.SetBottom(_vizBars[i], 0);
                }
            }
            else if (!_isPlaying)
            {
                for (int i = 0; i < count; i++)
                {
                    _vizHeights[i] = Math.Max(_vizHeights[i] * 0.88, 2.0);
                    _vizBars[i].Height = _vizHeights[i];
                }
            }
        }

        private void BuildVisualizer()
        {
            if (VisualizerCanvas == null) return;
            VisualizerCanvas.Children.Clear();
            _vizBars.Clear();

            double barW = Math.Max((380.0 - VizBarCount * 2.0) / VizBarCount, 4);
            for (int i = 0; i < VizBarCount; i++)
            {
                double t = (double)i / VizBarCount;
                byte r = (byte)(0x7C + t * (0xFF - 0x7C));
                byte g = (byte)(0x6B * (1 - t) + 0x6B * t);
                byte b = (byte)(0xFF * (1 - t) + 0xB5 * t);
                var bar = new Rectangle
                {
                    Width = barW,
                    Height = 2,
                    RadiusX = 2,
                    RadiusY = 2,
                    Opacity = 0.85,
                    Fill = FreezeUi(new SolidColorBrush(Color.FromRgb(r, g, b))),
                };
                Canvas.SetLeft(bar, i * (barW + 2));
                Canvas.SetBottom(bar, 0);
                VisualizerCanvas.Children.Add(bar);
                _vizBars.Add(bar);
            }
        }

        // ─── Громкость ────────────────────────────────────────────────────────────

        private void Volume_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeSlider == null || VolumeText == null) return;

            _volume = VolumeSlider.Value / 100.0;
            if (_volumeProvider != null)
                _volumeProvider.Volume = (float)_volume;

            VolumeText.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
            if (_isMuted && VolumeSlider.Value > 0)
            {
                _isMuted = false;
                UpdateVolumeIcon();
            }
        }

        private void VolumeIcon_Click(object s, MouseButtonEventArgs e)
        {
            if (_isMuted)
            {
                _isMuted = false;
                VolumeSlider.Value = Math.Clamp(_volumeBeforeMute * 100.0, 0.0, 100.0);
            }
            else
            {
                _volumeBeforeMute = _volume > 0 ? _volume : 0.75;
                _isMuted = true;
                VolumeSlider.Value = 0;
            }
            UpdateVolumeIcon();
        }

        private void UpdateVolumeIcon()
        {
            if (VolumeIcon == null) return;
            if (_isMuted)
            {
                VolumeIcon.Data = VolMutedGeometry;
                VolumeIcon.Stroke = VolMutedStroke;
            }
            else
            {
                VolumeIcon.Data = VolActiveGeometry;
                VolumeIcon.Stroke = VolActiveStroke;
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Handled) return;
            if (TrackList != null && TrackList.IsMouseOver) return;

            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
            e.Handled = true;
        }

        // ─── Surround ────────────────────────────────────────────────────────────

        private void Surround_Click(object s, RoutedEventArgs e)
        {
            _surroundEnabled = !_surroundEnabled;
            if (_surround != null) _surround.Enabled = _surroundEnabled;
            UpdateSurroundButton();
            UpdateChannelInfo();
        }

        private void UpdateSurroundButton()
        {
            if (SurroundPath == null) return;

            var iconGlow = SurroundIconGlow;
            var textGlow = SurroundGlow;
            var sb = TryFindResource("NeonPulseCyan") as Storyboard;

            SurroundPath.Stroke = FreezeUi(new SolidColorBrush(_cyan));

            if (_surroundEnabled)
            {
                SurroundPath.Opacity = 1.0;
                if (iconGlow != null)
                {
                    iconGlow.BlurRadius = 8;
                    iconGlow.Opacity = 0.85;
                }
                if (textGlow != null)
                {
                    textGlow.BlurRadius = 10;
                    textGlow.Opacity = 1.0;
                    if (sb != null && Surround3DText != null)
                        sb.Begin(Surround3DText, true);
                }
            }
            else
            {
                SurroundPath.Opacity = 0.28;
                if (iconGlow != null)
                {
                    iconGlow.BlurRadius = 0;
                    iconGlow.Opacity = 0.0;
                }
                if (textGlow != null)
                {
                    if (sb != null && Surround3DText != null)
                        sb.Stop(Surround3DText);
                    textGlow.BlurRadius = 6;
                    textGlow.Opacity = 0.9;
                }
            }
        }

        private void UpdateChannelInfo()
        {
            var fmt = _audioReader?.WaveFormat ?? _mfReader?.WaveFormat;
            UpdateChannelInfo(fmt);
        }

        private void UpdateChannelInfo(NAudio.Wave.WaveFormat? fmt)
        {
            if (fmt == null || TagGenre == null) return;
            string label = fmt.Channels switch
            {
                1 => "MONO",
                2 => "STEREO",
                3 => "2.1",
                4 => "4.0",
                5 => "5.0",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                9 => "7.1 + SUB",
                10 => "9.1",
                var ch => ch > 0 ? $"{ch}CH" : "UNKNOWN",
            };

            string mixLabel = _mixMode switch
            {
                MixMode.ForceMono => " → MONO",
                MixMode.WideStero => " · WIDE",
                MixMode.NarrowStereo => " · NARROW",
                _ => ""
            };

            TagGenre.Text = _surroundEnabled
                ? $"3D · {label}{mixLabel}"
                : $"{label}{mixLabel}";
        }

        // ─── Shuffle / Repeat / Heart ─────────────────────────────────────────────

        private void Shuffle_Click(object s, RoutedEventArgs e)
        {
            _shuffle = !_shuffle;
            if (ShufflePath != null)
            {
                ShufflePath.Stroke = _shuffle
                    ? FreezeUi(new SolidColorBrush(_cyan))
                    : FreezeUi(new SolidColorBrush(Color.FromArgb(0x45, _cyan.R, _cyan.G, _cyan.B)));
            }
        }

        private void Repeat_Click(object s, RoutedEventArgs e)
        {
            _repeat = !_repeat;
            if (RepeatPath != null)
            {
                RepeatPath.Stroke = _repeat
                    ? FreezeUi(new SolidColorBrush(_accent1))
                    : FreezeUi(new SolidColorBrush(Color.FromArgb(0x40, _accent1.R, _accent1.G, _accent1.B)));
            }
        }

        private void Heart_Click(object s, RoutedEventArgs e)
        {
            _liked = !_liked;
            if (HeartIcon != null)
            {
                HeartIcon.Text = _liked ? "\u2665" : "\u2661";
                HeartIcon.Foreground = _liked
                    ? FreezeUi(new SolidColorBrush(_accent2))
                    : HeartInactiveFg;
            }
        }

        // ─── Window Chrome ────────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (_isPseudoMaximized)
            {
                _isPseudoMaximized = false;
                if (PresentationSource.FromVisual(this) is PresentationSource src &&
                    src.CompositionTarget != null)
                {
                    Point cursorDip = src.CompositionTarget.TransformFromDevice
                        .Transform(PointToScreen(e.GetPosition(this)));
                    Width = _preMaxWidth;
                    Height = _preMaxHeight;
                    Left = cursorDip.X - _preMaxWidth / 2.0;
                    Top = cursorDip.Y - 20.0;
                    ClampWindowToWorkArea(_preMaxWidth, _preMaxHeight);
                }
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object s, RoutedEventArgs e) => Close();
        private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void About_Click(object s, RoutedEventArgs e)
        {
            var w = new AboutWindow { Owner = this };
            w.ApplyColors(_accent1, _accent2, _cyan);
            w.ShowDialog();
        }

        // ─── Win32: ограничение максимизации рабочей зоной ───────────────────────

        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        // ─── Visualizer (внешнее окно) ───────────────────────────────────────────

        public void ToggleVisualizer()
        {
            if (_visualizerWindow == null || !_visualizerWindow.IsLoaded)
            {
                if (_visualizerWindow != null)
                    CleanupVisualizerWindow(_visualizerWindow);

                var viz = new VisualizerWindow(this)
                {
                    Owner = this,
                    CurrentMode = (VisualizerMode)Math.Clamp(_vizSettings.Mode, 0, 10),
                    Width = double.IsFinite(_vizSettings.Width) && _vizSettings.Width > 0 ? _vizSettings.Width : 760,
                    Height = double.IsFinite(_vizSettings.Height) && _vizSettings.Height > 0 ? _vizSettings.Height : 430,
                };

                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                if (double.IsFinite(_vizSettings.Left) && double.IsFinite(_vizSettings.Top) &&
                    _vizSettings.Left >= vsLeft - 100 && _vizSettings.Left < vsRight - 40 &&
                    _vizSettings.Top >= vsTop - 40 && _vizSettings.Top < vsBottom - 40)
                {
                    viz.Left = _vizSettings.Left;
                    viz.Top = _vizSettings.Top;
                }

                viz.LocationChanged += OnVisualizerLocationChanged;
                viz.SizeChanged += OnVisualizerSizeChanged;
                viz.ModeChanged += OnVisualizerModeChanged;
                viz.Closed += OnVisualizerClosed;

                _visualizerWindow = viz;
            }

            if (_visualizerWindow.IsVisible)
            {
                _visualizerWindow.Hide();
            }
            else
            {
                if (_visualizerWindow.WindowState == WindowState.Minimized)
                    _visualizerWindow.WindowState = WindowState.Normal;
                _visualizerWindow.Show();
                _visualizerWindow.Activate();
            }
        }

        private void OnVisualizerLocationChanged(object? sender, EventArgs e)
        {
            if (sender is VisualizerWindow viz &&
                viz.WindowState == WindowState.Normal &&
                double.IsFinite(viz.Left) && double.IsFinite(viz.Top))
            {
                _vizSettings.Left = viz.Left;
                _vizSettings.Top = viz.Top;
            }
        }

        private void OnVisualizerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is VisualizerWindow viz &&
                viz.WindowState == WindowState.Normal &&
                double.IsFinite(viz.Width) && viz.Width > 0 &&
                double.IsFinite(viz.Height) && viz.Height > 0)
            {
                _vizSettings.Width = viz.Width;
                _vizSettings.Height = viz.Height;
            }
        }

        private void OnVisualizerModeChanged(VisualizerMode mode)
        {
            _vizSettings.Mode = (int)mode;
        }

        private void OnVisualizerClosed(object? sender, EventArgs e)
        {
            if (sender is VisualizerWindow viz)
                CleanupVisualizerWindow(viz);
        }

        private void CleanupVisualizerWindow(VisualizerWindow viz)
        {
            viz.LocationChanged -= OnVisualizerLocationChanged;
            viz.SizeChanged -= OnVisualizerSizeChanged;
            viz.ModeChanged -= OnVisualizerModeChanged;
            viz.Closed -= OnVisualizerClosed;

            if (ReferenceEquals(_visualizerWindow, viz))
                _visualizerWindow = null;
        }

        private void VizBtn_Click(object s, RoutedEventArgs e) => ToggleVisualizer();
    }
}

#nullable restore