// MainWindow.Playback.cs — движок воспроизведения, загрузка треков, управление
#nullable enable

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TagFile = TagLib.File;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Движок ──────────────────────────────────────────────────────────────

        private void StopEngine()
        {
            Interlocked.Increment(ref _playSession);
            _ffmpegCueSegment?.CancelSkip();
            _audioOutput.StopAndWait();

            _cueSegment = null;
            _ffmpegCueSegment = null;
            _channelMixer = null;
            _surround = null;
            _equalizer = null;
            _compressor = null;
            _fftAgg = null;
            _volumeProvider = null;
            _pendingSeekOnNextPlay = null;
            _pendingPlayAfterFfmpegSeek = false;

            if (_audioReader != null && _audioReader != _cachedAudioReader)
                _audioReader.Dispose();
            _audioReader = null;

            _mfReader?.Dispose();
            _mfReader = null;
        }

        private void DisposeCachedReader()
        {
            _cachedAudioReader?.Dispose();
            _cachedAudioReader = null;
            _cachedAudioPath = null;
        }

        private int InvalidateAlbumArt(bool clearSources)
        {
            int session = Interlocked.Increment(ref _albumArtSession);

            AlbumImage.Opacity = 0;
            MiniAlbumImage.Opacity = 0;
            AlbumIcon.Visibility = Visibility.Visible;
            MiniAlbumIcon.Visibility = Visibility.Visible;

            if (clearSources)
            {
                AlbumImage.Source = null;
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

        // ─── Синхронная загрузка (WAV/OGG/MP3/M4A…) ─────────────────────────────

        private void LoadTrackInternal(int index, bool autoPlay, double seekSeconds = 0)
        {
            if (index < 0 || index >= Playlist.Count) return;
            _currentIndex = index;
            var t = Playlist[index];

            _trackEndHandled = false;
            _isPlaying = false;
            StopEngine();
            int capturedSession = Volatile.Read(ref _playSession);

            try
            {
                _cueStart = t.IsCue ? t.CueStart : TimeSpan.Zero;
                _cueEnd = t.IsCue ? t.CueEnd : TimeSpan.Zero;

                string ext = IOPath.GetExtension(t.Path);
                ISampleProvider baseProvider;
                TimeSpan totalTime;

                if (VorbisExt.Contains(ext))
                {
                    DisposeCachedReader();
                    var vorbis = new VorbisWaveReader(t.Path);
                    _mfReader = vorbis;
                    _audioReader = null;
                    totalTime = vorbis.TotalTime;
                    var vorbisFloat = new WaveToSampleProvider(vorbis);
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
                    if (_cachedAudioReader == null ||
                        !string.Equals(_cachedAudioPath, t.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        DisposeCachedReader();
                        _cachedAudioReader = new AudioFileReader(t.Path);
                        _cachedAudioPath = t.Path;
                    }
                    _audioReader = _cachedAudioReader;
                    totalTime = _audioReader.TotalTime;

                    if (t.IsCue)
                    {
                        _cueSegment = new CueSegmentProvider(_audioReader, _cueStart, _cueEnd);
                        _cueSegment.SeekTo(_cueStart);
                        baseProvider = _cueSegment;
                    }
                    else
                    {
                        _cueSegment = null;
                        baseProvider = _audioReader;
                    }
                }
                else
                {
                    DisposeCachedReader();
                    _audioReader = new AudioFileReader(t.Path);
                    totalTime = _audioReader.TotalTime;
                    if (t.IsCue)
                    {
                        _cueSegment = new CueSegmentProvider(_audioReader, _cueStart, _cueEnd);
                        _cueSegment.SeekTo(_cueStart);
                        baseProvider = _cueSegment;
                    }
                    else
                    {
                        _cueSegment = null;
                        _audioReader.CurrentTime = TimeSpan.Zero;
                        baseProvider = _audioReader;
                    }
                }

                FinishLoadTrack(index, t, baseProvider, totalTime, autoPlay, seekSeconds);
            }
            catch (Exception ex)
            {
                StopEngine();
                FfmpegService.AppendDecodeLog($"OPEN fail file='{t.Path}' err='{ex.Message}'");
                string msg = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                int failedIndex = index;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSession)
                        return;

                    MessageBox.Show($"Ошибка открытия:\n{msg}", "Aurora Player",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

                    if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSession)
                        return;

                    if (Playlist.Count > 1)
                    {
                        int next = (failedIndex + 1) % Playlist.Count;
                        if (next != failedIndex) LoadTrack(next, autoPlay);
                    }
                }), DispatcherPriority.Background);
            }
        }

        // ─── Асинхронная загрузка (APE/WV/FLAC/OPUS через ffmpeg) ────────────────

        private void LoadTrackAsync(int index, bool autoPlay, double seekSeconds = 0)
        {
            if (index < 0 || index >= Playlist.Count) return;
            var t = Playlist[index];

            _trackEndHandled = false;
            _isPlaying = false;
            StopEngine();
            int capturedSession = Volatile.Read(ref _playSession);

            _cueStart = t.IsCue ? t.CueStart : TimeSpan.Zero;
            _cueEnd = t.IsCue ? t.CueEnd : TimeSpan.Zero;

            string? ffmpeg = FfmpegService.FindFfmpeg();
            if (ffmpeg == null)
            {
                FfmpegService.AppendDecodeLog($"LOAD ffmpeg missing file='{t.Path}', trying MediaFoundation fallback");
                bool mfOk = false;
                try
                {
                    using var r = new MediaFoundationReader(t.Path);
                    mfOk = r.TotalTime.TotalSeconds > 0;
                }
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

                FfmpegService.AppendDecodeLog($"LOAD MediaFoundation fallback ok file='{t.Path}'");
                _isTransitioning = true;
                try
                {
                    _currentIndex = index;
                    var mfReader = new MediaFoundationReader(t.Path);
                    _mfReader = mfReader;
                    ISampleProvider baseProvider;
                    if (t.IsCue)
                    {
                        try { mfReader.CurrentTime = _cueStart; } catch { }
                        baseProvider = new CueLimitProvider(mfReader.ToSampleProvider(), mfReader, _cueStart, _cueEnd);
                    }
                    else
                    {
                        baseProvider = mfReader.ToSampleProvider();
                    }

                    FinishLoadTrack(index, t, baseProvider, mfReader.TotalTime, autoPlay, seekSeconds);
                }
                catch (Exception ex)
                {
                    StopEngine();
                    FfmpegService.AppendDecodeLog($"OPEN mf-fallback fail file='{t.Path}' err='{ex.Message}'");
                    string msg = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
                    MessageBox.Show($"Ошибка открытия:\n{msg}", "Aurora Player",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { _isTransitioning = false; }
                return;
            }
            else
            {
                FfmpegService.AppendDecodeLog($"LOAD ffmpeg selected file='{t.Path}' ffmpeg='{ffmpeg}'");
            }

            Task.Run(() =>
            {
                if (Volatile.Read(ref _playSession) != capturedSession || _isClosingHandled)
                    return;

                FfmpegDecodeStream? ffStream = null;
                ApeRingBufferStream? ringStream = null;
                Exception? openError = null;
                TimeSpan tagDuration = TimeSpan.Zero;

                string trackExt = IOPath.GetExtension(t.Path);
                bool isApe = trackExt.Equals(".ape", StringComparison.OrdinalIgnoreCase);
                bool isWv = trackExt.Equals(".wv", StringComparison.OrdinalIgnoreCase);
                bool isApeOrWv = isApe || isWv;

                bool tryRingDirect = isApeOrWv && FfmpegService.PathHasNonAscii(t.Path);

                if (!tryRingDirect)
                {
                    try { ffStream = CreateReadableFfmpegStream(ffmpeg, t.Path); }
                    catch (Exception exPipe)
                    {
                        FfmpegService.AppendDecodeLog(
                            $"PIPE fail file='{t.Path}' err='{exPipe.Message}'");
                        if (!isApeOrWv)
                            openError = new Exception(
                                $"Не удалось открыть {trackExt.TrimStart('.').ToUpperInvariant()} (pipe).\n" +
                                exPipe.Message, exPipe);
                    }
                }

                if ((tryRingDirect || (isApeOrWv && ffStream == null)) &&
                    openError == null &&
                    Volatile.Read(ref _playSession) == capturedSession &&
                    !_isClosingHandled)
                {
                    string ringReason = tryRingDirect ? "non-ascii-direct" : "pipe-fail-fallback";
                    try
                    {
                        var hint = isApe ? FfmpegInputHint.Ape : FfmpegInputHint.Wavpack;
                        int macOffset = 0;
                        if (isApe)
                            FfmpegService.TryFindApeMacHeaderOffset(t.Path, out macOffset);

                        ringStream = new ApeRingBufferStream(
                            ffmpegPath: ffmpeg,
                            inputPath: t.Path,
                            inputHint: hint,
                            skipInitialBytes: macOffset > 0 ? macOffset : 0);

                        // Прерываем ожидание сразу, если пользователь переключил трек или закрывает окно
                        for (int w = 0; w < 12; w++)
                        {
                            if (Volatile.Read(ref _playSession) != capturedSession || _isClosingHandled)
                                break;
                            Thread.Sleep(50);
                        }

                        if (Volatile.Read(ref _playSession) != capturedSession || _isClosingHandled)
                        {
                            ringStream.Dispose();
                            ringStream = null;
                            ffStream?.Dispose();
                            return;
                        }

                        var probe = new byte[Math.Max(ringStream.WaveFormat.BlockAlign * 16, 256)];
                        int got = 0;
                        for (int tries = 30; tries > 0 && got == 0; tries--)
                        {
                            if (Volatile.Read(ref _playSession) != capturedSession || _isClosingHandled)
                                break;
                            got = ringStream.Read(probe, 0, probe.Length);
                            if (got == 0) Thread.Sleep(50);
                        }

                        if (Volatile.Read(ref _playSession) != capturedSession || _isClosingHandled)
                        {
                            ringStream.Dispose();
                            ringStream = null;
                            ffStream?.Dispose();
                            return;
                        }

                        if (got > 0)
                        {
                            FfmpegService.AppendDecodeLog(
                                $"RING ok reason='{ringReason}' file='{t.Path}' hint='{hint}'");
                        }
                        else
                        {
                            string ringErr = ringStream.LastError;
                            ringStream.Dispose();
                            ringStream = null;
                            openError = new Exception(
                                $"Не удалось открыть {trackExt.TrimStart('.').ToUpperInvariant()} (ring).\n" +
                                (!string.IsNullOrEmpty(ringErr) ? ringErr : "ffmpeg не выдал данных"));
                        }
                    }
                    catch (Exception exRing)
                    {
                        ringStream?.Dispose();
                        ringStream = null;
                        openError = exRing;
                    }
                }

                // Читаем длительность из тегов в фоне, не блокируя UI-поток
                if (openError == null &&
                    (ffStream != null || ringStream != null) &&
                    Volatile.Read(ref _playSession) == capturedSession &&
                    !_isClosingHandled)
                {
                    try
                    {
                        using var tag = TagFile.Create(t.Path);
                        tagDuration = tag.Properties.Duration;
                    }
                    catch { }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSession)
                    {
                        ffStream?.Dispose();
                        ringStream?.Dispose();
                        return;
                    }

                    if (openError != null)
                    {
                        ffStream?.Dispose();
                        ringStream?.Dispose();
                        FfmpegService.AppendDecodeLog($"OPEN fail file='{t.Path}' err='{openError.Message}'");
                        string msg = openError.Message.Length > 120
                            ? openError.Message[..120] + "…" : openError.Message;
                        MessageBox.Show($"Ошибка открытия:\n{msg}", "Aurora Player",
                            MessageBoxButton.OK, MessageBoxImage.Warning);

                        if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSession)
                            return;

                        if (Playlist.Count > 1)
                        {
                            int nx = (index + 1) % Playlist.Count;
                            if (nx != index) LoadTrack(nx, autoPlay);
                        }
                        return;
                    }

                    if (index < 0 || index >= Playlist.Count)
                    {
                        ffStream?.Dispose();
                        ringStream?.Dispose();
                        return;
                    }

                    _isTransitioning = true;
                    try
                    {
                        _currentIndex = index;
                        ISampleProvider baseProvider;

                        if (ffStream != null)
                        {
                            _mfReader = ffStream;
                            if (t.IsCue)
                            {
                                var seg = new FfmpegCueSegment(ffStream, _cueStart, _cueEnd);
                                seg.StartSkipAsync();
                                _ffmpegCueSegment = seg;
                                _cueSegment = null;
                                baseProvider = seg;
                            }
                            else
                            {
                                _ffmpegCueSegment = null;
                                _cueSegment = null;
                                baseProvider = new FfmpegFloatProvider(ffStream);
                            }
                        }
                        else if (ringStream != null)
                        {
                            _mfReader = ringStream;
                            _audioReader = null;
                            if (t.IsCue)
                            {
                                var seg = new FfmpegCueSegment(ringStream, _cueStart, _cueEnd);
                                seg.StartSkipAsync();
                                _ffmpegCueSegment = seg;
                                _cueSegment = null;
                                baseProvider = seg;
                            }
                            else
                            {
                                _ffmpegCueSegment = null;
                                _cueSegment = null;
                                baseProvider = new FfmpegFloatProvider(ringStream);
                            }
                        }
                        else
                        {
                            return;
                        }

                        FinishLoadTrack(index, t, baseProvider, tagDuration, autoPlay, seekSeconds);
                    }
                    catch (Exception ex)
                    {
                        StopEngine();
                        FfmpegService.AppendDecodeLog($"FINISH fail file='{t.Path}' err='{ex.Message}'");
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

            if (Math.Abs((stream.CurrentTime - target).TotalSeconds) <= 0.05)
                return;

            try
            {
                long bytes = (long)(target.TotalSeconds * stream.WaveFormat.AverageBytesPerSecond);
                int blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
                bytes -= bytes % blockAlign;
                if (stream.Length > 0)
                    bytes = Math.Clamp(bytes, 0, stream.Length);
                stream.Position = Math.Max(0, bytes);
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
                TotalTime.Text = FormatTime(trackLen);
            }
            else
            {
                ProgressSlider.Maximum = Math.Max(0, totalTime.TotalSeconds);
                TotalTime.Text = FormatTime(totalTime);
            }
            ProgressSlider.Minimum = 0;

            // Цепочка: baseProvider → ChannelMixer → Surround → EQ → Compressor → FFT → Volume
            _channelMixer = new ChannelMixerProvider(baseProvider, _mixMode);
            _channelMixer.Width = _mixWidth;
            _channelMixer.LfeWeight = _mixLfeWeight;
            UpdateMixInfoLabel();

            _surround = new SurroundProvider(_channelMixer) { Width = _surroundWidth, Enabled = _surroundEnabled };
            _equalizer = new EqualizerProvider(_surround);
            _compressor = new CompressorProvider(_equalizer) { Enabled = true };
            _fftAgg = new FftAggregator(_compressor);
            _volumeProvider = new VolumeProvider(_fftAgg) { Volume = (float)_volume };

            if (_currentPreset >= 0 && _currentPreset < EqPresets.Length)
            {
                var gains = EqPresets[_currentPreset].Gains;
                for (int b = 0; b < 5; b++) _equalizer.SetGain(b, gains[b]);
            }
            else
            {
                for (int b = 0; b < 5; b++) _equalizer.SetGain(b, (float)_eqValues[b]);
            }

            if (t.IsCue) _surround.FlushBuffer();

            bool isFfmpegPipeStream = _mfReader is FfmpegDecodeStream;
            bool isRingStream = _mfReader is ApeRingBufferStream;
            bool isFfmpegStream = isFfmpegPipeStream || isRingStream;

            if (seekSeconds > 0 && !isFfmpegPipeStream)
            {
                ApplyTrackSeek(t, seekSeconds);
                FfmpegService.AppendDecodeLog(
                    $"LOAD pre-seek file='{t.Path}' seek='{seekSeconds:F3}' ring='{isRingStream}'");
            }

            _audioOutput.SetSource(_volumeProvider);
            ProgressSlider.IsEnabled = true;

            if (seekSeconds > 0)
            {
                if (isFfmpegPipeStream)
                {
                    _pendingSeekOnNextPlay = seekSeconds;
                }
                else
                {
                    _pendingSeekOnNextPlay = autoPlay ? null : seekSeconds;
                }

                ProgressSlider.Value = ProgressSlider.Maximum > 0
                    ? Math.Min(seekSeconds, ProgressSlider.Maximum)
                    : 0;
                CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(seekSeconds));
            }
            else
            {
                if (!t.IsCue && _audioReader != null)
                    _audioReader.CurrentTime = TimeSpan.Zero;
                _pendingSeekOnNextPlay = null;
                ProgressSlider.Value = 0;
                CurrentTime.Text = "0:00";
            }

            _isProgrammaticSelection = true;
            TrackList.SelectedIndex = index;
            TrackList.ScrollIntoView(t);
            _isProgrammaticSelection = false;

            SongTitle.Text = t.Title; SongArtist.Text = t.Artist;
            MiniTitle.Text = t.Title; MiniArtist.Text = t.Artist;
            TagBitrate.Text = t.Format; FormatBadge.Text = t.Format;
            UpdateChannelInfo();

            // Загрузка обложки в фоне
            string artPath = t.Path;
            int capturedArtSession = _albumArtSession;
            int capturedPlaySession = Volatile.Read(ref _playSession);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                BitmapImage? img = null;
                try
                {
                    using var tag2 = TagFile.Create(artPath);
                    var pic2 = tag2.Tag.Pictures?.Length > 0 ? tag2.Tag.Pictures[0] : null;
                    if (pic2?.Data?.Data is { Length: > 0 } picBytes)
                    {
                        using var ms = new MemoryStream(picBytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = ms;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        img = bmp;
                    }
                }
                catch { }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosingHandled) return;
                    if (capturedArtSession != _albumArtSession) return;
                    if (_currentIndex < 0 || _currentIndex >= Playlist.Count) return;
                    if (!Playlist[_currentIndex].Path.Equals(artPath, StringComparison.OrdinalIgnoreCase)) return;

                    if (img != null)
                    {
                        AlbumImage.Source = img; AlbumImage.Opacity = 1;
                        MiniAlbumImage.Source = img; MiniAlbumImage.Opacity = 1;
                        AlbumIcon.Visibility = Visibility.Collapsed;
                        MiniAlbumIcon.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        AlbumImage.Opacity = 0; MiniAlbumImage.Opacity = 0;
                        AlbumIcon.Visibility = Visibility.Visible;
                        MiniAlbumIcon.Visibility = Visibility.Visible;
                    }

                    // Корректируем позицию, только если сессия та же, трек на паузе (не играет)
                    // и позицию действительно сбросило около 0.
                    if (seekSeconds > 0 &&
                        !isFfmpegStream &&
                        !_isPlaying &&
                        Volatile.Read(ref _playSession) == capturedPlaySession &&
                        _pendingSeekOnNextPlay.HasValue)
                    {
                        double expectedPos = t.IsCue ? _cueStart.TotalSeconds + seekSeconds : seekSeconds;
                        double actualPos = _audioReader?.CurrentTime.TotalSeconds
                                          ?? _mfReader?.CurrentTime.TotalSeconds
                                          ?? -1;
                        if (actualPos < 0 || Math.Abs(actualPos - expectedPos) > 0.5)
                        {
                            FfmpegService.AppendDecodeLog(
                                $"LOAD post-art reseek file='{artPath}' " +
                                $"expected='{expectedPos:F3}' actual='{actualPos:F3}'");
                            ApplyTrackSeek(t, seekSeconds);
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
            if (_currentIndex < 0)
            {
                if (Playlist.Count > 0) LoadTrack(0);
                return;
            }

            if (_isPlaying)
            {
                _audioOutput.Pause();
                SetPlaying(false);
            }
            else
            {
                if (_pendingSeekOnNextPlay is double pendingSeek &&
                    pendingSeek > 0 &&
                    _currentIndex >= 0 &&
                    _currentIndex < Playlist.Count)
                {
                    var currentTrack = Playlist[_currentIndex];

                    if (_mfReader is FfmpegDecodeStream oldFfmpeg)
                    {
                        _pendingSeekOnNextPlay = null;
                        _pendingPlayAfterFfmpegSeek = true;
                        FfmpegService.AppendDecodeLog(
                            $"PLAY pending-seek ffmpeg-async file='{currentTrack.Path}' seek='{pendingSeek:F3}'");
                        SeekFfmpegAsync(oldFfmpeg, pendingSeek);
                        return;
                    }

                    if (_mfReader is ApeRingBufferStream)
                    {
                        if (_volumeProvider != null)
                            _audioOutput.SetSource(_volumeProvider);
                        _pendingSeekOnNextPlay = null;
                        FfmpegService.AppendDecodeLog(
                            $"PLAY pending-seek ring-rebind file='{currentTrack.Path}' seek='{pendingSeek:F3}' " +
                            $"readerPos='{ReaderCurrentTime.TotalSeconds:F3}'");
                    }
                    else
                    {
                        bool shouldRebindSource = _volumeProvider != null;
                        ApplyTrackSeek(currentTrack, pendingSeek);
                        if (shouldRebindSource) _audioOutput.SetSource(_volumeProvider);
                        _pendingSeekOnNextPlay = null;
                        FfmpegService.AppendDecodeLog(
                            $"PLAY pending-seek consumed file='{currentTrack.Path}' seek='{pendingSeek:F3}' " +
                            $"readerPos='{ReaderCurrentTime.TotalSeconds:F3}' rebind='{shouldRebindSource}'");
                    }
                }

                _audioOutput.Play();
                SetPlaying(true);
            }
        }

        private int GetNextShuffleIndex()
        {
            if (Playlist.Count <= 1) return 0;
            int next = _currentIndex;
            while (next == _currentIndex)
                next = _rng.Next(Playlist.Count);
            return next;
        }

        private void Prev_Click(object s, RoutedEventArgs e)
        {
            if (Playlist.Count == 0) return;

            double relPos = _ffmpegCueSegment != null
                ? _ffmpegCueSegment.PositionSeconds
                : (HasReader ? ReaderCurrentTime.TotalSeconds - _cueStart.TotalSeconds : 0);

            if ((_ffmpegCueSegment != null || HasReader) && relPos > 3.0)
            {
                SeekTo(0);
                return;
            }

            LoadTrack(_shuffle
                ? GetNextShuffleIndex()
                : (_currentIndex - 1 + Playlist.Count) % Playlist.Count);
        }

        private void Next_Click(object s, RoutedEventArgs e)
        {
            if (Playlist.Count == 0) return;
            LoadTrack(_shuffle
                ? GetNextShuffleIndex()
                : (_currentIndex + 1) % Playlist.Count);
        }

        // ─── Конец трека ─────────────────────────────────────────────────────────
        private void OnTrackEnded(object? sender, EventArgs e)
        {
            int capturedSession = Volatile.Read(ref _playSession);
            Dispatcher.BeginInvoke(new Action(() => AdvanceTrack(capturedSession)));
        }

        private void AdvanceTrack(int capturedSession)
        {
            if (_isClosingHandled || _trackEndHandled ||
                capturedSession != Volatile.Read(ref _playSession) || !_isPlaying)
                return;

            _trackEndHandled = true;

            if (Playlist.Count == 0)
            {
                SetPlaying(false);
                return;
            }

            if (_shuffle)
            {
                LoadTrack(GetNextShuffleIndex());
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

            if (PlayIcon != null)
            {
                PlayIcon.Children.Clear();
                if (playing)
                {
                    var r1 = new Rectangle { Width = 5, Height = 18, RadiusX = 2, RadiusY = 2, Fill = Brushes.White };
                    var r2 = new Rectangle { Width = 5, Height = 18, RadiusX = 2, RadiusY = 2, Fill = Brushes.White };
                    Canvas.SetLeft(r1, 5); Canvas.SetTop(r1, 3);
                    Canvas.SetLeft(r2, 14); Canvas.SetTop(r2, 3);
                    PlayIcon.Children.Add(r1);
                    PlayIcon.Children.Add(r2);
                }
                else
                {
                    var poly = new Polygon { Fill = Brushes.White };
                    poly.Points.Add(new Point(6, 3));
                    poly.Points.Add(new Point(21, 12));
                    poly.Points.Add(new Point(6, 21));
                    PlayIcon.Children.Add(poly);
                }
            }

            if (MiniPlayIcon != null)
            {
                MiniPlayIcon.Children.Clear();
                if (playing)
                {
                    var p1 = new Rectangle { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1, Fill = Brushes.White };
                    var p2 = new Rectangle { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1, Fill = Brushes.White };
                    Canvas.SetLeft(p1, 3); Canvas.SetTop(p1, 2);
                    Canvas.SetLeft(p2, 10); Canvas.SetTop(p2, 2);
                    MiniPlayIcon.Children.Add(p1);
                    MiniPlayIcon.Children.Add(p2);
                }
                else
                {
                    var tri = new Polygon { Fill = Brushes.White };
                    tri.Points.Add(new Point(4, 2));
                    tri.Points.Add(new Point(14, 8));
                    tri.Points.Add(new Point(4, 14));
                    MiniPlayIcon.Children.Add(tri);
                }
            }

            if (playing)
            {
                _timer.Start();
                _eqTimer.Start();
                if (VisualizerCanvas != null && VisualizerCanvas.IsVisible)
                    _vizTimer.Start();
            }
            else
            {
                _timer.Stop();
                _eqTimer.Stop();
                _vizTimer.Stop();
                foreach (var bar in new[] { Eq1, Eq2, Eq3, Eq4, Eq5 })
                {
                    if (bar != null)
                        bar.Height = 3.0;
                }
            }
        }
    }
}

#nullable restore