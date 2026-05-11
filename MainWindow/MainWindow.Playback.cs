// MainWindow.Playback.cs — движок воспроизведения, загрузка треков, управление
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TagFile = TagLib.File;
using IOPath  = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Движок ──────────────────────────────────────────────────────────────

        private void StopEngine()
        {
            _audioOutput.StopAndWait();
            _cueSegment       = null;
            _ffmpegCueSegment = null;
            _channelMixer     = null;
            _surround         = null;
            _equalizer        = null;
            _compressor       = null;
            _fftAgg           = null;
            _volumeProvider   = null;
            _pendingSeekOnNextPlay = null;
            if (_audioReader != null && _audioReader != _cachedAudioReader)
                _audioReader.Dispose();
            _audioReader = null;
            _mfReader?.Dispose(); _mfReader = null;
            if (_ffmpegDecodeTempWavPath != null)
            {
                try { File.Delete(_ffmpegDecodeTempWavPath); } catch { }
                _ffmpegDecodeTempWavPath = null;
            }
        }

        private void DisposeCachedReader()
        {
            _cachedAudioReader?.Dispose();
            _cachedAudioReader = null;
            _cachedAudioPath   = null;
        }

        private int InvalidateAlbumArt(bool clearSources)
        {
            int session = Interlocked.Increment(ref _albumArtSession);

            AlbumImage.Opacity       = 0;
            MiniAlbumImage.Opacity   = 0;
            AlbumIcon.Visibility     = Visibility.Visible;
            MiniAlbumIcon.Visibility = Visibility.Visible;

            if (clearSources)
            {
                AlbumImage.Source     = null;
                MiniAlbumImage.Source = null;
            }

            return session;
        }

        // ─── Загрузка трека (точка входа) ────────────────────────────────────────

        private void LoadTrack(int index, bool autoPlay = true, double seekSeconds = 0)
        {
            if (index < 0 || index >= Playlist.Count) return;
            InvalidateAlbumArt(clearSources: false);
            var t = Playlist[index];
            string ext = IOPath.GetExtension(t.Path);
            bool useFfmpegPath = MfOnlyExt.Contains(ext);
            FfmpegService.AppendDecodeLog(
                $"LOAD request file='{t.Path}' ext='{ext}' mode='{(useFfmpegPath ? "ffmpeg-path" : "internal-path")}' seek='{seekSeconds:F3}' autoPlay='{autoPlay}'");

            if (useFfmpegPath)
                LoadTrackAsync(index, autoPlay, seekSeconds);
            else
                LoadTrackInternal(index, autoPlay, seekSeconds);
        }

        // ─── Синхронная загрузка (FLAC/WAV/OGG/MP3/M4A…) ────────────────────────

        private void LoadTrackInternal(int index, bool autoPlay, double seekSeconds = 0)
        {
            if (index < 0 || index >= Playlist.Count) return;
            _currentIndex = index;
            var t = Playlist[index];

            Interlocked.Increment(ref _playSession);
            _trackEndHandled = false;
            _isPlaying       = false;
            StopEngine();

            try
            {
                _cueStart = t.IsCue ? t.CueStart : TimeSpan.Zero;
                _cueEnd   = t.IsCue ? t.CueEnd   : TimeSpan.Zero;

                string ext = IOPath.GetExtension(t.Path);
                ISampleProvider baseProvider;
                TimeSpan        totalTime;

                if (VorbisExt.Contains(ext))
                {
                    DisposeCachedReader();
                    var vorbis  = new VorbisWaveReader(t.Path);
                    _mfReader   = vorbis;
                    _audioReader = null;
                    totalTime   = vorbis.TotalTime;
                    var vorbisFloat = new NAudio.Wave.SampleProviders.WaveToSampleProvider(vorbis);
                    if (t.IsCue)
                    {
                        var limit = new CueLimitProvider(vorbisFloat, vorbis, _cueStart, _cueEnd);
                        vorbis.CurrentTime = _cueStart;
                        baseProvider = limit;
                    }
                    else
                    {
                        _cueSegment = null;
                        vorbis.CurrentTime = TimeSpan.Zero;
                        baseProvider = vorbisFloat;
                    }
                }
                else if (CacheableExt.Contains(ext))
                {
                    // ── FLAC/WAV кеш ──────────────────────────────────────────
                    // Если файл тот же — переиспользуем ридер (как AIMP/foobar).
                    // Позицию НЕ сбрасываем здесь — это сделает FinishLoadTrack
                    // уже ПОСЛЕ SetSource, чтобы NAudio-буфер не съел старые данные.
                    if (_cachedAudioReader == null ||
                        !string.Equals(_cachedAudioPath, t.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        DisposeCachedReader();
                        _cachedAudioReader = new AudioFileReader(t.Path);
                        _cachedAudioPath   = t.Path;
                    }
                    _audioReader = _cachedAudioReader;
                    totalTime    = _audioReader.TotalTime;

                    if (t.IsCue)
                    {
                        _cueSegment = new CueSegmentProvider(_audioReader, _cueStart, _cueEnd);
                        _cueSegment.SeekTo(_cueStart);
                        baseProvider = _cueSegment;
                    }
                    else
                    {
                        _cueSegment = null;
                        // ← Убрал _audioReader.CurrentTime = TimeSpan.Zero здесь.
                        // FinishLoadTrack выставит позицию ПОСЛЕ SetSource,
                        // иначе WaveOut успевает буферизовать данные с позиции 0
                        // до того как seek применится — именно это и был баг на FLAC.
                        baseProvider = _audioReader;
                    }
                }
                else
                {
                    // MP3/M4A/AAC/WMA: COM MediaFoundation — переоткрываем каждый раз
                    DisposeCachedReader();
                    _audioReader = new AudioFileReader(t.Path);
                    totalTime    = _audioReader.TotalTime;
                    if (t.IsCue)
                    {
                        _cueSegment = new CueSegmentProvider(_audioReader, _cueStart, _cueEnd);
                        _cueSegment.SeekTo(_cueStart);
                        baseProvider = _cueSegment;
                    }
                    else
                    {
                        _cueSegment              = null;
                        _audioReader.CurrentTime = TimeSpan.Zero;
                        baseProvider             = _audioReader;
                    }
                }

                FinishLoadTrack(index, t, baseProvider, totalTime, autoPlay, seekSeconds);
            }
            catch (Exception ex)
            {
                FfmpegService.AppendDecodeLog($"OPEN fail file='{t.Path}' err='{ex.Message}'");
                string msg = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                int failedIndex = index;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show($"Ошибка открытия:\n{msg}", "Aurora Player",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (Playlist.Count > 1)
                    {
                        int next = (failedIndex + 1) % Playlist.Count;
                        if (next != failedIndex) LoadTrack(next, autoPlay);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // ─── Асинхронная загрузка (APE/WV через ffmpeg) ──────────────────────────

        private void LoadTrackAsync(int index, bool autoPlay, double seekSeconds = 0)
        {
            if (index < 0 || index >= Playlist.Count) return;
            var t = Playlist[index];

            Interlocked.Increment(ref _playSession);
            _trackEndHandled = false;
            _isPlaying       = false;
            StopEngine();

            _cueStart = t.IsCue ? t.CueStart : TimeSpan.Zero;
            _cueEnd   = t.IsCue ? t.CueEnd   : TimeSpan.Zero;

            string? ffmpeg = FfmpegService.FindFfmpeg();
            if (ffmpeg == null)
            {
                FfmpegService.AppendDecodeLog($"LOAD ffmpeg missing file='{t.Path}', trying MediaFoundation fallback");
                // Пробуем MediaFoundationReader как запасной вариант
                bool mfOk = false;
                try { var r = new MediaFoundationReader(t.Path); mfOk = r.TotalTime.TotalSeconds > 0; r.Dispose(); }
                catch { }

                if (!mfOk)
                {
                    string extLabel = IOPath.GetExtension(t.Path).TrimStart('.').ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(extLabel)) extLabel = "этого формата";
                    MessageBox.Show(
                        $"Для воспроизведения {extLabel} нужен ffmpeg.exe.\n\n" +
                        "Варианты:\n• Скопировать ffmpeg.exe рядом с AuroraPlayer.exe\n" +
                        "• winget install Gyan.FFmpeg\n• https://ffmpeg.org/download.html",
                        "Aurora Player — нужен ffmpeg", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // MF сработал
                FfmpegService.AppendDecodeLog($"LOAD MediaFoundation fallback ok file='{t.Path}'");
                _isTransitioning = true;
                try
                {
                    Interlocked.Increment(ref _playSession);
                    _trackEndHandled = false; _isPlaying = false;
                    StopEngine();
                    _cueStart = t.IsCue ? t.CueStart : TimeSpan.Zero;
                    _cueEnd   = t.IsCue ? t.CueEnd   : TimeSpan.Zero;
                    var mfReader  = new MediaFoundationReader(t.Path);
                    _mfReader     = mfReader;
                    ISampleProvider baseProvider;
                    if (t.IsCue)
                    {
                        try { mfReader.CurrentTime = _cueStart; } catch { }
                        baseProvider = new CueLimitProvider(mfReader.ToSampleProvider(), mfReader, _cueStart, _cueEnd);
                    }
                    else
                        baseProvider = mfReader.ToSampleProvider();
                    FinishLoadTrack(index, t, baseProvider, mfReader.TotalTime, autoPlay, seekSeconds);
                }
                finally { _isTransitioning = false; }
                return;
            }
            else
            {
                FfmpegService.AppendDecodeLog($"LOAD ffmpeg selected file='{t.Path}' ffmpeg='{ffmpeg}'");
            }

            int capturedSession = _playSession;

            Task.Run(() =>
            {
                FfmpegDecodeStream? ffStream    = null;
                string?             wavFallback = null;
                Exception?          openError   = null;

                try { ffStream = CreateReadableFfmpegStream(ffmpeg, t.Path); }
                catch (Exception exPipe)
                {
                    string wavErr = "";
                    try { wavFallback = TryDecodeEntireFileToWavUsingFfmpeg(ffmpeg, t.Path, out wavErr); } catch { }
                    if (wavFallback == null)
                        openError = new Exception(
                            $"Не удалось открыть APE/WV (pipe и WAV-fallback).\n" +
                            (!string.IsNullOrEmpty(wavErr) ? wavErr : exPipe.Message), exPipe);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_playSession != capturedSession) { ffStream?.Dispose(); return; }
                    if (openError != null)
                    {
                        FfmpegService.AppendDecodeLog($"OPEN fail file='{t.Path}' err='{openError.Message}'");
                        string msg = openError.Message.Length > 120
                            ? openError.Message[..120] + "…" : openError.Message;
                        MessageBox.Show($"Ошибка открытия:\n{msg}", "Aurora Player",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (Playlist.Count > 1) { int nx = (index + 1) % Playlist.Count; if (nx != index) LoadTrack(nx, autoPlay); }
                        return;
                    }

                    _isTransitioning = true;
                    try
                    {
                        _currentIndex = index;
                        ISampleProvider baseProvider;
                        TimeSpan        totalTime;

                        if (ffStream != null)
                        {
                            _mfReader = ffStream;
                            totalTime = TimeSpan.Zero;
                            try { using var tag = TagFile.Create(t.Path); totalTime = tag.Properties.Duration; } catch { }
                            if (t.IsCue)
                            {
                                var seg = new FfmpegCueSegment(ffStream, _cueStart, _cueEnd);
                                seg.StartSkipAsync(); _ffmpegCueSegment = seg; _cueSegment = null; baseProvider = seg;
                            }
                            else { _ffmpegCueSegment = null; baseProvider = new FfmpegFloatProvider(ffStream); }
                        }
                        else
                        {
                            _ffmpegDecodeTempWavPath = wavFallback;
                            DisposeCachedReader();
                            _audioReader = new AudioFileReader(wavFallback!);
                            _mfReader    = null;
                            totalTime    = _audioReader.TotalTime;
                            if (t.IsCue)
                            {
                                _cueSegment = new CueSegmentProvider(_audioReader, _cueStart, _cueEnd);
                                _cueSegment.SeekTo(_cueStart); _ffmpegCueSegment = null; baseProvider = _cueSegment;
                            }
                            else
                            {
                                _cueSegment = null; _ffmpegCueSegment = null;
                                _audioReader.CurrentTime = TimeSpan.Zero; baseProvider = _audioReader;
                            }
                        }

                        if (seekSeconds > 0 && _audioReader != null && t.IsCue)
                        {
                            // Для CUE seek делается в FinishLoadTrack через _cueSegment.SeekTo.
                            // Для обычного файла FinishLoadTrack сам выставляет _audioReader.CurrentTime.
                            // Здесь ничего делать не нужно.
                        }

                        FinishLoadTrack(index, t, baseProvider, totalTime, autoPlay, seekSeconds);
                    }
                    finally { _isTransitioning = false; }
                }));
            });
        }

        // ─── FinishLoadTrack: строит цепочку провайдеров и обновляет UI ──────────

        private static void SetWaveStreamTime(WaveStream stream, TimeSpan target)
        {
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;

            try { stream.CurrentTime = target; }
            catch { return; }

            // Некоторые декодеры могут игнорировать первое назначение CurrentTime до тех пор, пока не начнется чтение.
            if (Math.Abs((stream.CurrentTime - target).TotalSeconds) <= 0.05)
                return;

            try
            {
                long bytes = (long)(target.TotalSeconds * stream.WaveFormat.AverageBytesPerSecond);
                int blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
                bytes -= bytes % blockAlign;
                bytes = Math.Clamp(bytes, 0, stream.Length);
                stream.Position = bytes;
            }
            catch { }
        }

        private void ApplyTrackSeek(TrackItem track, double seekSeconds)
        {
            if (seekSeconds <= 0) return;

            var target = TimeSpan.FromSeconds(
                track.IsCue ? _cueStart.TotalSeconds + seekSeconds : seekSeconds);

            if (_cueSegment != null)
                _cueSegment.SeekTo(target);
            else if (_audioReader != null)
                SetWaveStreamTime(_audioReader, target);
            else if (_mfReader != null)
                SetWaveStreamTime(_mfReader, target);

            _surround?.FlushBuffer();
        }

        private void FinishLoadTrack(int index, TrackItem t, ISampleProvider baseProvider,
                                     TimeSpan totalTime, bool autoPlay, double seekSeconds = 0)
        {
            if (t.IsCue)
            {
                var trackLen = (_cueEnd > _cueStart) ? _cueEnd - _cueStart : totalTime - _cueStart;
                if (trackLen < TimeSpan.Zero) trackLen = TimeSpan.Zero;
                ProgressSlider.Maximum = trackLen.TotalSeconds;
                TotalTime.Text         = FormatTime(trackLen);
            }
            else
            {
                ProgressSlider.Maximum = totalTime.TotalSeconds;
                TotalTime.Text         = FormatTime(totalTime);
            }
            ProgressSlider.Minimum = 0;

            // Цепочка: baseProvider → ChannelMixer → Surround → EQ → Compressor → FFT → Volume
            _channelMixer           = new ChannelMixerProvider(baseProvider, _mixMode);
            _channelMixer.Width     = _mixWidth;
            _channelMixer.LfeWeight = _mixLfeWeight;
            Dispatcher.BeginInvoke(() => UpdateMixInfoLabel());

            _surround       = new SurroundProvider(_channelMixer) { Width = _surroundWidth, Enabled = _surroundEnabled };
            _equalizer      = new EqualizerProvider(_surround);
            _compressor     = new CompressorProvider(_equalizer) { Enabled = true };
            _fftAgg         = new FftAggregator(_compressor);
            _volumeProvider = new VolumeProvider(_fftAgg) { Volume = (float)_volume };

            if (_currentPreset >= 0 && _currentPreset < EqPresets.Length)
            {
                var gains = EqPresets[_currentPreset].Gains;
                for (int b = 0; b < 5; b++) _equalizer.SetGain(b, gains[b]);
            }
            else
                for (int b = 0; b < 5; b++) _equalizer.SetGain(b, (float)_eqValues[b]);

            if (t.IsCue) _surround.FlushBuffer();

            // ── Seek ДО SetSource ─────────────────────────────────────────────────
            // WdlResamplingSampleProvider (FLAC 44100→48000) делает prefill при создании:
            // читает первые сэмплы из источника. Если позиция не выставлена ДО SetSource,
            // prefill захватит данные с позиции 0.
            // Для APE/WV (FfmpegDecodeStream) seek на этом этапе невозможен — отложим.
            bool _isFfmpegStream = _mfReader is FfmpegDecodeStream;

            if (seekSeconds > 0 && !_isFfmpegStream)
            {
                // FLAC / WAV / OGG / MP3: применяем seek ДО SetSource
                ApplyTrackSeek(t, seekSeconds);
                FfmpegService.AppendDecodeLog(
                    $"LOAD pre-seek file='{t.Path}' seek='{seekSeconds:F3}'");
            }

            _audioOutput.SetSource(_volumeProvider);
            ProgressSlider.IsEnabled = true;

            if (seekSeconds > 0)
            {
                if (_isFfmpegStream)
                {
                    // APE/WV: откладываем до нажатия Play
                    _pendingSeekOnNextPlay = seekSeconds;
                }
                else
                {
                    // FLAC/WAV: seek уже применён до SetSource.
                    // Повторяем после LoadAlbumArt — TagLib (чтение тегов) может
                    // случайно сдвинуть позицию файла через общий файловый кеш ОС.
                    _pendingSeekOnNextPlay = autoPlay ? null : seekSeconds;
                }
                ProgressSlider.Value = seekSeconds;
                CurrentTime.Text     = FormatTime(TimeSpan.FromSeconds(seekSeconds));
            }
            else
            {
                if (!t.IsCue && _audioReader != null)
                    _audioReader.CurrentTime = TimeSpan.Zero;
                _pendingSeekOnNextPlay = null;
                ProgressSlider.Value = 0;
                CurrentTime.Text     = "0:00";
            }

            _isProgrammaticSelection = true;
            TrackList.SelectedIndex  = index;
            TrackList.ScrollIntoView(t);
            _isProgrammaticSelection = false;

            SongTitle.Text  = t.Title;  SongArtist.Text  = t.Artist;
            MiniTitle.Text  = t.Title;  MiniArtist.Text  = t.Artist;
            TagBitrate.Text = t.Format; FormatBadge.Text = t.Format;
            UpdateChannelInfo();
            // LoadAlbumArt запускаем в фоне чтобы не блокировать UI-поток:
            // TagFile.Create на большом FLAC с embedded art может занять 100–200 мс.
            var _artPath = t.Path;
            int capturedArtSession = _albumArtSession;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                // Читаем обложку в фоне
                System.Windows.Media.Imaging.BitmapImage? img = null;
                try
                {
                    using var tag2 = TagFile.Create(_artPath);
                    var pic2 = tag2.Tag.Pictures?.Length > 0 ? tag2.Tag.Pictures[0] : null;
                    if (pic2 != null)
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = new System.IO.MemoryStream(pic2.Data.Data);
                        bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze(); // обязательно для передачи между потоками
                        img = bmp;
                    }
                }
                catch { }
                // Обновляем UI в главном потоке.
                // Повторный seek выполняем здесь же — строго в UI-потоке — чтобы исключить
                // race condition: прямой вызов ApplyTrackSeek из ThreadPool записывал
                // CurrentTime в AudioFileReader пока NAudio читал из него в аудио-потоке.
                var capturedArtPath   = _artPath;
                var capturedTrack     = t;
                var capturedSeek      = seekSeconds;
                var capturedIsFfmpeg  = _isFfmpegStream;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (capturedArtSession != _albumArtSession) return;
                    if (_currentIndex < 0 || _currentIndex >= Playlist.Count) return;
                    if (!Playlist[_currentIndex].Path.Equals(capturedArtPath, StringComparison.OrdinalIgnoreCase)) return;

                    if (img != null)
                    {
                        AlbumImage.Source        = img; AlbumImage.Opacity       = 1;
                        MiniAlbumImage.Source    = img; MiniAlbumImage.Opacity   = 1;
                        AlbumIcon.Visibility     = System.Windows.Visibility.Collapsed;
                        MiniAlbumIcon.Visibility = System.Windows.Visibility.Collapsed;
                    }
                    else
                    {
                        AlbumImage.Opacity       = 0;  MiniAlbumImage.Opacity   = 0;
                        AlbumIcon.Visibility     = System.Windows.Visibility.Visible;
                        MiniAlbumIcon.Visibility = System.Windows.Visibility.Visible;
                    }

                    // Повторный seek после чтения тегов TagLib: выполняется в UI-потоке,
                    // синхронно с аудио-диспетчером — никакого race condition.
                    // Применяем только если трек не сменился и seek актуален.
                    if (capturedSeek > 0 && !capturedIsFfmpeg)
                    {
                        // Проверяем фактическую позицию: если TagLib сдвинул её, корректируем
                        double expectedPos = capturedTrack.IsCue
                            ? _cueStart.TotalSeconds + capturedSeek
                            : capturedSeek;
                        double actualPos = _audioReader?.CurrentTime.TotalSeconds
                                        ?? _mfReader?.CurrentTime.TotalSeconds
                                        ?? -1;
                        if (actualPos < 0 || Math.Abs(actualPos - expectedPos) > 0.5)
                        {
                            FfmpegService.AppendDecodeLog(
                                $"LOAD post-art reseek file='{capturedArtPath}' " +
                                $"expected='{expectedPos:F3}' actual='{actualPos:F3}'");
                            ApplyTrackSeek(capturedTrack, capturedSeek);
                        }
                    }
                }));
            });

            if (autoPlay)
            {
                _pendingSeekOnNextPlay = null;
                _audioOutput.Play();
                SetPlaying(true);
            }
        }

        // ─── Управление воспроизведением ─────────────────────────────────────────

        private void PlayPause_Click(object s, RoutedEventArgs e)
        {
            if (_currentIndex < 0) { if (Playlist.Count > 0) LoadTrack(0); return; }
            if (_isPlaying) { _audioOutput.Pause(); SetPlaying(false); }
            else
            {
                if (_pendingSeekOnNextPlay is double pendingSeek &&
                    pendingSeek > 0 &&
                    _currentIndex >= 0 &&
                    _currentIndex < Playlist.Count)
                {
                    // Применяем отложенный seek ДО Play, чтобы NAudio не успел
                    // буферизовать данные с неверной позиции.
                    // Двойной вызов SeekTo после Play удалён: он создавал третий seek
                    // (после pre-seek в FinishLoadTrack и этого) и мог сбить позицию
                    // уже начавшегося воспроизведения.
                    var currentTrack = Playlist[_currentIndex];
                    bool shouldRebindSource = _mfReader is not FfmpegDecodeStream && _volumeProvider != null;

                    ApplyTrackSeek(currentTrack, pendingSeek);

                    // For non-ffmpeg sources rebuild output binding after seek.
                    // This drops any prefilled resampler/output buffers and guarantees
                    // first audible samples come from the restored position.
                    if (shouldRebindSource)
                        _audioOutput.SetSource(_volumeProvider);

                    _pendingSeekOnNextPlay = null;
                    FfmpegService.AppendDecodeLog(
                        $"PLAY pending-seek consumed file='{currentTrack.Path}' seek='{pendingSeek:F3}' " +
                        $"readerPos='{ReaderCurrentTime.TotalSeconds:F3}' rebind='{shouldRebindSource}'");
                }

                _audioOutput.Play();
                SetPlaying(true);
            }
        }

        private void Prev_Click(object s, RoutedEventArgs e)
        {
            if (Playlist.Count == 0) return;
            double relPos = HasReader ? ReaderCurrentTime.TotalSeconds - _cueStart.TotalSeconds : 0;
            if (HasReader && relPos > 3) { SeekTo(0); return; }
            LoadTrack(_shuffle ? _rng.Next(Playlist.Count) : (_currentIndex - 1 + Playlist.Count) % Playlist.Count);
        }

        private void Next_Click(object s, RoutedEventArgs e)
        {
            if (Playlist.Count == 0) return;
            LoadTrack(_shuffle ? _rng.Next(Playlist.Count) : (_currentIndex + 1) % Playlist.Count);
        }

        // ─── Конец трека ─────────────────────────────────────────────────────────
        private void OnTrackEnded(object? sender, EventArgs e)
        {
            // Вызывается из аудио-потока NAudio — переходим в UI-поток
            int capturedSession = _playSession;
            Dispatcher.BeginInvoke(() => AdvanceTrack(capturedSession));
        }

        private void AdvanceTrack(int capturedSession)
        {
            if (_trackEndHandled || capturedSession != _playSession || !_isPlaying) return;
            _trackEndHandled = true;

            if (_shuffle)
            {
                // Исключаем текущий трек чтобы shuffle не повторял его сразу
                int next = _currentIndex;
                if (Playlist.Count > 1)
                    while (next == _currentIndex) next = _rng.Next(Playlist.Count);
                LoadTrack(next);
            }
            else
            {
                int next = _currentIndex + 1;
                if (next < Playlist.Count) LoadTrack(next);
                else if (_repeat) LoadTrack(0);
                else SetPlaying(false);
            }
        }

        private void SetPlaying(bool playing)
        {
            _isPlaying = playing;

            // Главная кнопка
            PlayIcon.Children.Clear();
            if (playing)
            {
                var r1 = new System.Windows.Shapes.Rectangle { Width=5,Height=18,RadiusX=2,RadiusY=2,Fill=System.Windows.Media.Brushes.White };
                var r2 = new System.Windows.Shapes.Rectangle { Width=5,Height=18,RadiusX=2,RadiusY=2,Fill=System.Windows.Media.Brushes.White };
                Canvas.SetLeft(r1,5); Canvas.SetTop(r1,3); Canvas.SetLeft(r2,14); Canvas.SetTop(r2,3);
                PlayIcon.Children.Add(r1); PlayIcon.Children.Add(r2);
            }
            else
            {
                var poly = new System.Windows.Shapes.Polygon { Fill=System.Windows.Media.Brushes.White };
                poly.Points.Add(new System.Windows.Point(6,3));
                poly.Points.Add(new System.Windows.Point(21,12));
                poly.Points.Add(new System.Windows.Point(6,21));
                PlayIcon.Children.Add(poly);
            }

            // Мини-кнопка
            MiniPlayIcon.Children.Clear();
            if (playing)
            {
                var p1 = new System.Windows.Shapes.Rectangle { Width=3,Height=12,RadiusX=1,RadiusY=1,Fill=System.Windows.Media.Brushes.White };
                var p2 = new System.Windows.Shapes.Rectangle { Width=3,Height=12,RadiusX=1,RadiusY=1,Fill=System.Windows.Media.Brushes.White };
                Canvas.SetLeft(p1,3); Canvas.SetTop(p1,2); Canvas.SetLeft(p2,10); Canvas.SetTop(p2,2);
                MiniPlayIcon.Children.Add(p1); MiniPlayIcon.Children.Add(p2);
            }
            else
            {
                var tri = new System.Windows.Shapes.Polygon { Fill=System.Windows.Media.Brushes.White };
                tri.Points.Add(new System.Windows.Point(4,2));
                tri.Points.Add(new System.Windows.Point(14,8));
                tri.Points.Add(new System.Windows.Point(4,14));
                MiniPlayIcon.Children.Add(tri);
            }

            if (playing)
            {
                _timer.Start();
                _eqTimer.Start();
                // VizTimer нужен только когда встроенный визуализатор виден в главном окне.
                // Внешнее окно VisualizerWindow управляет CompositionTarget.Rendering самостоятельно.
                if (VisualizerCanvas != null && VisualizerCanvas.IsVisible)
                    _vizTimer.Start();
            }
            else
            {
                _timer.Stop();
                _eqTimer.Stop();
                _vizTimer.Stop();
                // Сбрасываем EQ-бары в минимум сразу, без таймера
                if (Eq1 != null)
                    foreach (var bar in new[] { Eq1, Eq2, Eq3, Eq4, Eq5 })
                        bar.Height = 3.0;
            }
        }

        // LoadAlbumArt удалён — логика перенесена в FinishLoadTrack (async ThreadPool)
    }
}
