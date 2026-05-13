// MainWindow.UI.cs — таймеры, прогресс, громкость, surround, shuffle/repeat, chrome
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
        // ─── Таймер прогресса ────────────────────────────────────────────────────

        private void Timer_Tick(object? s, EventArgs e)
        {
            if (_isDraggingSlider || !HasReader) return;

            double abs = ReaderCurrentTime.TotalSeconds;
            double readerRelPos = _cueStart > TimeSpan.Zero ? abs - _cueStart.TotalSeconds : abs;
            bool isFfmpegNonCue = _mfReader is FfmpegDecodeStream && _cueEnd <= TimeSpan.Zero;

            // Для воспроизведения ffmpeg без CUE источником является время ридера.
            // Это предотвращает скачки интерфейса, когда устаревший сегментный провайдер сообщает относительное время.
            if (isFfmpegNonCue && _ffmpegCueSegment != null)
                _ffmpegCueSegment = null;

            double relPos;
            if (isFfmpegNonCue)
                relPos = Math.Clamp(readerRelPos, 0, ProgressSlider.Maximum);
            else if (_cueSegment != null)
                relPos = Math.Clamp(_cueSegment.PositionSeconds, 0, ProgressSlider.Maximum);
            else if (_ffmpegCueSegment != null)
                relPos = Math.Clamp(_ffmpegCueSegment.PositionSeconds, 0, ProgressSlider.Maximum);
            else
                relPos = Math.Clamp(readerRelPos, 0, ProgressSlider.Maximum);

            ProgressSlider.Value = relPos;
            CurrentTime.Text     = FormatTime(TimeSpan.FromSeconds(relPos));

            // Запасная детекция конца — на случай если TrackEnded не сработал.
            // Пропускаем если _trackEndHandled=true (ffmpeg seek в процессе) —
            // в этом случае позиция ещё не актуальна и детектор даст ложное срабатывание.
            if (_isPlaying && !_trackEndHandled && ProgressSlider.Maximum > 0
                && relPos >= ProgressSlider.Maximum - 0.3)
            {
                AdvanceTrack(_playSession);
                return;
            }

            // Мини-плеер
            if (_isMini)
            {
                MiniTime.Text = FormatTime(TimeSpan.FromSeconds(relPos));
                if (MiniProgressBar != null)
                {
                    double trackLen = ProgressSlider.Maximum;
                    double ratio    = trackLen > 0 ? relPos / trackLen : 0;
                    MiniProgressBar.Width = MiniProgressBar.Parent is Grid g
                        ? g.ActualWidth * ratio
                        : MiniPlayer.ActualWidth * ratio;
                }
            }
        }

        // ─── EQ Meter Tick ────────────────────────────────────────────────────────

        private void EqTimer_Tick(object? s, EventArgs e)
        {
            if (!_isPlaying) return;
            var bars = new[] { Eq1, Eq2, Eq3, Eq4, Eq5 };

            float[]? fft = GetFftData();
            bool hasData = _fftAgg != null && _fftAgg.HasData && fft != null;

            if (hasData && fft != null)
            {
                int fftLen = fft.Length;
                int[] binStarts = { 0,  1,  5,  23, 93 };
                int[] binEnds   = { 1,  5,  23, 93, fftLen };
                double maxH = 18.0;

                for (int i = 0; i < 5; i++)
                {
                    float peak = 0f;
                    for (int b = binStarts[i]; b < Math.Min(binEnds[i], fftLen); b++)
                        if (fft[b] > peak) peak = fft[b];
                    double targetH = Math.Clamp(peak * maxH * 18.0, 3.0, maxH);
                    double currH   = bars[i].Height;
                    // Быстрый подъём (0.85), плавный спад — попадает в ритм
                    bars[i].Height = targetH > currH
                        ? currH + (targetH - currH) * 0.85
                        : currH + (targetH - currH) * 0.35;
                }
            }
            else
            {
                foreach (var r in bars)
                    r.Height = Math.Max(r.Height * 0.70, 3.0);
            }
        }

        // ─── Visualizer Tick (встроенные бары) ───────────────────────────────────

        private void VizTimer_Tick(object? s, EventArgs e)
        {
            double h = VisualizerCanvas.ActualHeight > 0 ? VisualizerCanvas.ActualHeight : 36;
            float[]? fft = GetFftData();
            bool hasData = _fftAgg != null && _fftAgg.HasData && _isPlaying && fft != null;

            if (hasData && fft != null)
            {
                int fftLen = fft.Length;
                for (int i = 0; i < _vizBars.Count; i++)
                {
                    double tLog   = (double)i / _vizBars.Count;
                    int    bin    = Math.Clamp((int)Math.Pow(fftLen, tLog), 0, fftLen - 1);
                    float  mag    = fft[bin];
                    double target = Math.Clamp(mag * h * 6.0, 2, h);
                    double curr   = _vizHeights[i];
                    _vizHeights[i] = target > curr ? curr * 0.2 + target * 0.8 : curr * 0.82 + target * 0.18;
                    _vizBars[i].Height = Math.Max(_vizHeights[i], 2);
                    Canvas.SetBottom(_vizBars[i], 0);
                }
            }
            else if (!_isPlaying)
            {
                for (int i = 0; i < _vizBars.Count; i++)
                {
                    _vizHeights[i] = Math.Max(_vizHeights[i] * 0.88, 2);
                    _vizBars[i].Height = _vizHeights[i];
                }
            }
        }

        private void BuildVisualizer()
        {
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
                    Width = barW, Height = 2, RadiusX = 2, RadiusY = 2, Opacity = 0.85,
                    Fill  = new SolidColorBrush(Color.FromRgb(r, g, b)),
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
            if (VolumeText == null) return;
            _volume = VolumeSlider.Value / 100.0;
            if (_volumeProvider != null) _volumeProvider.Volume = (float)_volume;
            VolumeText.Text = $"{(int)VolumeSlider.Value}%";
            if (_isMuted && VolumeSlider.Value > 0) { _isMuted = false; UpdateVolumeIcon(); }
        }

        private void VolumeIcon_Click(object s, MouseButtonEventArgs e)
        {
            if (_isMuted)
            {
                _isMuted = false;
                VolumeSlider.Value = _volumeBeforeMute * 100;
            }
            else
            {
                _volumeBeforeMute  = _volume > 0 ? _volume : 0.75;
                _isMuted           = true;
                VolumeSlider.Value = 0;
            }
            UpdateVolumeIcon();
        }

        private void UpdateVolumeIcon()
        {
            if (VolumeIcon == null) return;
            if (_isMuted)
            {
                VolumeIcon.Data   = Geometry.Parse("M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M3,3 L21,21");
                VolumeIcon.Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x6B, 0x6B));
            }
            else
            {
                VolumeIcon.Data   = Geometry.Parse("M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M15.54,8.46 A5,5,0,0,1,15.54,15.54 M19.07,4.93 A10,10,0,0,1,19.07,19.07");
                VolumeIcon.Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xA8, 0x9F, 0xFF));
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
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

            // Находим элементы кнопки
            var iconGlow = SurroundIconGlow;   // DropShadowEffect на иконке
            var textGlow = SurroundGlow;       // DropShadowEffect на тексте "3D"

            if (_surroundEnabled)
            {
                // ── ВКЛЮЧЕНО: иконка и надпись ярко светятся ────────────────────
                SurroundPath.Opacity = 1.0;
                SurroundPath.Stroke  = new SolidColorBrush(_cyan);
                if (iconGlow != null)
                {
                    iconGlow.BlurRadius = 8;
                    iconGlow.Opacity    = 0.85;
                }
                if (textGlow != null)
                {
                    textGlow.BlurRadius = 10;
                    textGlow.Opacity    = 1.0;
                }
                // Пульс на свечении текста
                if (textGlow != null)
                {
                    var sb = (System.Windows.Media.Animation.Storyboard)FindResource("NeonPulseCyan");
                    sb.Begin(Surround3DText, true);
                }
            }
            else
            {
                // ── ВЫКЛЮЧЕНО: только надпись "3D" слабо светится, иконка тусклая ─
                SurroundPath.Opacity = 0.28;
                SurroundPath.Stroke  = new SolidColorBrush(_cyan);
                if (iconGlow != null)
                {
                    iconGlow.BlurRadius = 0;
                    iconGlow.Opacity    = 0.0;
                }
                if (textGlow != null)
                {
                    var sb = (System.Windows.Media.Animation.Storyboard)FindResource("NeonPulseCyan");
                    sb.Stop(Surround3DText);
                    textGlow.BlurRadius = 6;
                    textGlow.Opacity    = 0.9;
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
            if (fmt == null) return;
            string label = fmt.Channels switch
            {
                1  => "MONO",
                2  => "STEREO",
                3  => "2.1",
                4  => "4.0",
                5  => "5.0",
                6  => "5.1",
                7  => "6.1",
                8  => "7.1",
                9  => "7.1 + SUB",
                10 => "9.1",
                var ch => ch > 0 ? $"{ch}CH" : "UNKNOWN",
            };

            string mixLabel = _mixMode switch
            {
                MixMode.ForceMono    => " → MONO",
                MixMode.WideStero    => " · WIDE",
                MixMode.NarrowStereo => " · NARROW",
                _                    => ""
            };

            TagGenre.Text = _surroundEnabled
                ? $"3D · {label}{mixLabel}"
                : $"{label}{mixLabel}";
        }

        // ─── Shuffle / Repeat / Heart ─────────────────────────────────────────────

        private void Shuffle_Click(object s, RoutedEventArgs e)
        {
            _shuffle = !_shuffle;
            ShufflePath.Stroke = _shuffle
                ? new SolidColorBrush(_cyan)
                : new SolidColorBrush(Color.FromArgb(0x45, _cyan.R, _cyan.G, _cyan.B));
        }

        private void Repeat_Click(object s, RoutedEventArgs e)
        {
            _repeat = !_repeat;
            RepeatPath.Stroke = _repeat
                ? new SolidColorBrush(_accent1)
                : new SolidColorBrush(Color.FromArgb(0x40, _accent1.R, _accent1.G, _accent1.B));
        }

        private void Heart_Click(object s, RoutedEventArgs e)
        {
            _liked = !_liked;
            HeartIcon.Text       = _liked ? "\u2665" : "\u2661";
            HeartIcon.Foreground = _liked
                ? new SolidColorBrush(_accent2)
                : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        }

        // ─── Window Chrome ────────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // Если окно в псевдо-максимизированном состоянии — восстанавливаем
            // прежний размер и центрируем под курсором перед началом перетаскивания
            if (_isPseudoMaximized)
            {
                _isPseudoMaximized = false;
                var cursor = PointToScreen(e.GetPosition(this));
                Width  = _preMaxWidth;
                Height = _preMaxHeight;
                // Ставим окно так чтобы курсор был примерно по центру тайтлбара
                Left = cursor.X - _preMaxWidth / 2;
                Top  = cursor.Y - 20;
            }

            DragMove();
        }

        private void Close_Click(object s, RoutedEventArgs e)    => Close();
        private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void About_Click(object s, RoutedEventArgs e)
        {
            var w = new AboutWindow { Owner = this };
            w.ApplyColors(_accent1, _accent2, _cyan);
            w.ShowDialog();
        }

        // ─── Win32: ограничение максимизации рабочей зоной ───────────────────────

        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)] private struct POINT    { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        // ─── Visualizer (внешнее окно) ───────────────────────────────────────────

        public void ToggleVisualizer()
        {
            if (_visualizerWindow == null || !_visualizerWindow.IsLoaded)
            {
                _visualizerWindow       = new VisualizerWindow(this) { Owner = this };
                _visualizerWindow.CurrentMode = (VisualizerMode)Math.Clamp(_vizSettings.Mode, 0, 10);
                _visualizerWindow.Width  = _vizSettings.Width;
                _visualizerWindow.Height = _vizSettings.Height;
                if (_vizSettings.Left >= 0 && _vizSettings.Top >= 0)
                { _visualizerWindow.Left = _vizSettings.Left; _visualizerWindow.Top = _vizSettings.Top; }

                _visualizerWindow.LocationChanged += (_, _) =>
                { _vizSettings.Left = _visualizerWindow.Left; _vizSettings.Top = _visualizerWindow.Top; };
                _visualizerWindow.SizeChanged += (_, _) =>
                { _vizSettings.Width = _visualizerWindow.Width; _vizSettings.Height = _visualizerWindow.Height; };
                _visualizerWindow.ModeChanged += mode => _vizSettings.Mode = (int)mode;
            }

            if (_visualizerWindow.IsVisible) _visualizerWindow.Hide();
            else                             _visualizerWindow.Show();
        }

        private void VizBtn_Click(object s, RoutedEventArgs e) => ToggleVisualizer();
    }
}
