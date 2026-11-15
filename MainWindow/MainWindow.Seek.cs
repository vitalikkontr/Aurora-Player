// MainWindow.Seek.cs — перемотка и ffmpeg-seek
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        private bool _isDraggingTrack;
        private bool _isDraggingThumb;

        // ─── Удержание слайдера на время асинхронного seek ───────────────────────
        // Пока ffmpeg / ring-buffer перематывается (сотни мс), таймер воспроизведения
        // не должен перезаписывать ProgressSlider.Value старой позицией — иначе
        // ползунок прыгает назад, а потом вперёд. Таймер уже проверяет _isDraggingSlider,
        // поэтому на время seek мы просто держим этот флаг.
        private int _seekHoldToken;
        private bool _ffmpegSeekInFlight;
        private double? _queuedSeekSeconds;

        private int BeginSeekHold()
        {
            int token = Interlocked.Increment(ref _seekHoldToken);
            if (!_isDraggingTrack && !_isDraggingThumb)
                _isDraggingSlider = true;

            // Страховка: если seek по какой-то причине не завершится — отпускаем через 4 c
            _ = Task.Delay(4000).ContinueWith(_ =>
            {
                try { Dispatcher.BeginInvoke(new Action(() => EndSeekHold(token))); }
                catch { }
            });
            return token;
        }

        private void EndSeekHold(int token)
        {
            // Более новый seek уже перехватил удержание
            if (token != Volatile.Read(ref _seekHoldToken)) return;
            // Пользователь снова тянет ползунок
            if (_isDraggingTrack || _isDraggingThumb) return;
            _isDraggingSlider = false;
        }

        // ─── Прогресс-слайдер ────────────────────────────────────────────────────

        private void ProgressSlider_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !HasReader) return;
            if (IsOriginFromSliderThumb(e.OriginalSource as DependencyObject, ProgressSlider)) return;

            _isDraggingSlider = true;
            _isDraggingTrack = true;
            _isDraggingThumb = false;
            _seekHandledByMouseUp = false;

            ProgressSlider.MouseMove -= OnProgressTrackMouseMove;
            ProgressSlider.LostMouseCapture -= OnProgressLostMouseCapture;
            ProgressSlider.MouseMove += OnProgressTrackMouseMove;
            ProgressSlider.LostMouseCapture += OnProgressLostMouseCapture;

            ProgressSlider.CaptureMouse();
            double pos = CalcSliderPos(e);
            ProgressSlider.Value = pos;
            CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(pos));
            e.Handled = true;
        }

        private void OnProgressTrackMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingTrack || e.LeftButton != MouseButtonState.Pressed || !ProgressSlider.IsMouseCaptured)
                return;

            double pos = CalcSliderPos(e);
            ProgressSlider.Value = pos;
            CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(pos));
        }

        private void ProgressSlider_MouseUp(object s, MouseButtonEventArgs e)
        {
            // Если перетаскивали сам Thumb — завершение обработает ProgressSlider_DragCompleted
            if (_isDraggingThumb || !_isDraggingTrack)
                return;

            DetachProgressTrackHandlers();
            _isDraggingSlider = false;
            _isDraggingTrack = false;
            _seekHandledByMouseUp = true;

            if (ProgressSlider.IsMouseCaptured)
                ProgressSlider.ReleaseMouseCapture();

            double pos = CalcSliderPos(e);
            ProgressSlider.Value = pos;
            SeekTo(pos);
        }

        private void OnProgressLostMouseCapture(object sender, MouseEventArgs e)
        {
            DetachProgressTrackHandlers();
            _isDraggingTrack = false;
            _isDraggingThumb = false;
            // _isDraggingSlider не трогаем: если сейчас идёт seek, его удерживает BeginSeekHold
        }

        private void DetachProgressTrackHandlers()
        {
            ProgressSlider.MouseMove -= OnProgressTrackMouseMove;
            ProgressSlider.LostMouseCapture -= OnProgressLostMouseCapture;
        }

        private void ProgressSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider)
                CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void ProgressSlider_DragStarted(object s, DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
            _isDraggingThumb = true;
            _isDraggingTrack = false;
            _seekHandledByMouseUp = false;
        }

        private void ProgressSlider_DragCompleted(object s, DragCompletedEventArgs e)
        {
            DetachProgressTrackHandlers();
            _isDraggingSlider = false;
            _isDraggingThumb = false;
            _isDraggingTrack = false;

            if (_seekHandledByMouseUp)
            {
                _seekHandledByMouseUp = false;
                return;
            }

            if (HasReader)
                SeekTo(ProgressSlider.Value);
        }

        private static bool IsOriginFromSliderThumb(DependencyObject? origin, DependencyObject root)
        {
            DependencyObject? current = origin;
            while (current != null && !ReferenceEquals(current, root))
            {
                if (current is Thumb) return true;
                current = current is Visual
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private double CalcSliderPos(MouseEventArgs e)
        {
            double min = ProgressSlider.Minimum;
            double max = ProgressSlider.Maximum;
            if (max <= min) return min;

            // Считаем позицию только по координате мыши, без Track.ValueFromPoint:
            // он отсчитывает от центра Thumb по последнему layout и текущему Value,
            // поэтому при быстрых событиях мыши (Down → Move → Up) значение "уплывает".
            double width, x, thumbW = 0.0;
            if (ProgressSlider.Template?.FindName("PART_Track", ProgressSlider) is Track track &&
                track.ActualWidth > 0.0)
            {
                width = track.ActualWidth;
                x = e.GetPosition(track).X;
                thumbW = track.Thumb?.ActualWidth ?? 0.0;
            }
            else
            {
                width = ProgressSlider.ActualWidth;
                x = e.GetPosition(ProgressSlider).X;
            }

            double usable = width - thumbW;
            if (usable <= 0.0 || double.IsNaN(x) || double.IsInfinity(x))
                return Math.Clamp(ProgressSlider.Value, min, max);

            // центр Thumb ходит в диапазоне [thumbW/2 ... width - thumbW/2]
            double ratio = Math.Clamp((x - thumbW / 2.0) / usable, 0.0, 1.0);
            return min + ratio * (max - min);
        }

        // ─── Универсальный Seek ───────────────────────────────────────────────────

        private void SeekTo(double relativeSeconds)
        {
            if (!HasReader) return;
            if (double.IsNaN(relativeSeconds) || double.IsInfinity(relativeSeconds) || relativeSeconds < 0)
                relativeSeconds = 0;

            // Предыдущий ffmpeg-seek ещё не закончился (_mfReader == null) —
            // запоминаем последний запрос и выполним его сразу после завершения.
            if (_ffmpegSeekInFlight)
            {
                _queuedSeekSeconds = relativeSeconds;
                BeginSeekHold();
                return;
            }

            _pendingSeekOnNextPlay = null;
            _trackEndHandled = false;

            if (_mfReader is FfmpegDecodeStream oldFfmpeg)
            {
                SeekFfmpegAsync(oldFfmpeg, relativeSeconds);
                return;
            }

            if (_mfReader is ApeRingBufferStream ringStream)
            {
                SeekRingBufferAsync(ringStream, relativeSeconds);
                return;
            }

            if (_cueSegment != null && _audioReader != null)
            {
                var seekAbs = TimeSpan.FromSeconds(_cueStart.TotalSeconds + relativeSeconds);
                if (_cueEnd > _cueStart && seekAbs >= _cueEnd)
                    seekAbs = TimeSpan.FromSeconds(Math.Max(_cueStart.TotalSeconds, _cueEnd.TotalSeconds - 0.5));

                bool wasPlaying = _isPlaying;
                if (wasPlaying) _audioOutput.Pause();
                _cueSegment.SeekTo(seekAbs);
                _surround?.FlushBuffer();
                if (wasPlaying) _audioOutput.Play();
                return;
            }

            if (_audioReader != null)
            {
                bool wasPlaying = _isPlaying;
                if (wasPlaying) _audioOutput.Pause();
                var target = TimeSpan.FromSeconds(relativeSeconds);
                if (_audioReader.TotalTime > TimeSpan.Zero && target > _audioReader.TotalTime)
                    target = _audioReader.TotalTime;
                _audioReader.CurrentTime = target;
                _surround?.FlushBuffer();
                if (wasPlaying) _audioOutput.Play();
                return;
            }

            if (_mfReader != null)
            {
                bool wasPlaying = _isPlaying;
                if (wasPlaying) _audioOutput.Pause();
                var target = TimeSpan.FromSeconds(relativeSeconds);
                if (_mfReader.TotalTime > TimeSpan.Zero && target > _mfReader.TotalTime)
                    target = _mfReader.TotalTime;
                _mfReader.CurrentTime = target;
                _surround?.FlushBuffer();
                if (wasPlaying) _audioOutput.Play();
            }
        }

        // ─── Ring buffer seek (не блокирует UI) ──────────────────────────────────

        private void SeekRingBufferAsync(ApeRingBufferStream oldRing, double relativeSeconds)
        {
            bool wasPlaying = _isPlaying;
            var cueEndSnapshot = _cueEnd;
            var absoluteSeek = TimeSpan.FromSeconds(_cueStart.TotalSeconds + relativeSeconds);

            if (cueEndSnapshot > _cueStart && absoluteSeek >= cueEndSnapshot)
                absoluteSeek = TimeSpan.FromSeconds(Math.Max(_cueStart.TotalSeconds, cueEndSnapshot.TotalSeconds - 0.5));

            double clampedRel = Math.Max(0.0, absoluteSeek.TotalSeconds - _cueStart.TotalSeconds);
            var surroundRef = _surround;
            var volumeRef = _volumeProvider;

            int capturedSeekSession = Interlocked.Increment(ref _playSession);
            int holdToken = BeginSeekHold();

            _audioOutput.Pause();
            _ffmpegCueSegment?.CancelSkip();

            long rawBytes = (long)(absoluteSeek.TotalSeconds * oldRing.WaveFormat.AverageBytesPerSecond);
            int align = Math.Max(1, oldRing.WaveFormat.BlockAlign);
            oldRing.Position = Math.Max(0, rawBytes - (rawBytes % align));

            Task.Run(() =>
            {
                for (int i = 0; i < 8; i++)
                {
                    if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSeekSession)
                    {
                        try { Dispatcher.BeginInvoke(new Action(() => EndSeekHold(holdToken))); } catch { }
                        return;
                    }
                    Thread.Sleep(50);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosingHandled || Volatile.Read(ref _playSession) != capturedSeekSession)
                    {
                        EndSeekHold(holdToken);
                        return;
                    }
                    if (_mfReader != oldRing)
                    {
                        EndSeekHold(holdToken);
                        return;
                    }

                    ISampleProvider seekProvider;
                    if (cueEndSnapshot > _cueStart)
                    {
                        double remaining = Math.Max(0.1, cueEndSnapshot.TotalSeconds - absoluteSeek.TotalSeconds);
                        var seg = new FfmpegCueSegment(oldRing,
                            start: TimeSpan.Zero,
                            end: TimeSpan.FromSeconds(remaining),
                            seekOffsetSeconds: clampedRel);
                        seg.MarkSkipDone();
                        _ffmpegCueSegment = seg;
                        seekProvider = seg;
                    }
                    else
                    {
                        _ffmpegCueSegment = null;
                        seekProvider = new FfmpegFloatProvider(oldRing);
                    }

                    if (surroundRef != null && volumeRef != null &&
                        _surround == surroundRef && _volumeProvider == volumeRef)
                    {
                        surroundRef.ReplaceSource(seekProvider);
                        _audioOutput.SetSource(volumeRef);
                        bool shouldPlay = wasPlaying || _pendingPlayAfterFfmpegSeek;
                        _pendingPlayAfterFfmpegSeek = false;
                        if (shouldPlay)
                        {
                            _audioOutput.Play();
                            SetPlaying(true);
                        }
                    }
                    else
                    {
                        _pendingPlayAfterFfmpegSeek = false;
                    }

                    EndSeekHold(holdToken);
                }));
            });
        }

        // ─── Ffmpeg seek в Task.Run (не блокирует UI) ────────────────────────────

        private void SeekFfmpegAsync(FfmpegDecodeStream oldFfmpeg, double relativeSeconds)
        {
            var track = _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex] : null;
            string? ffmpeg = FfmpegService.FindFfmpeg();
            if (track == null || ffmpeg == null)
            {
                _pendingPlayAfterFfmpegSeek = false;
                return;
            }

            bool wasPlaying = _isPlaying;
            var cueStartSnap = _cueStart;
            var cueEndSnapshot = _cueEnd;
            var absoluteSeek = TimeSpan.FromSeconds(cueStartSnap.TotalSeconds + relativeSeconds);

            if (cueEndSnapshot > cueStartSnap && absoluteSeek >= cueEndSnapshot)
                absoluteSeek = TimeSpan.FromSeconds(Math.Max(cueStartSnap.TotalSeconds, cueEndSnapshot.TotalSeconds - 0.5));

            double clampedRel = Math.Max(0.0, absoluteSeek.TotalSeconds - cueStartSnap.TotalSeconds);
            var trackSnapshot = track;
            var surroundRef = _surround;
            var volumeRef = _volumeProvider;

            int capturedSeekSession = Interlocked.Increment(ref _playSession);
            int holdToken = BeginSeekHold();

            _audioOutput.Pause();
            _ffmpegCueSegment?.CancelSkip();
            _ffmpegCueSegment = null;
            oldFfmpeg.Dispose();
            _mfReader = null;

            _ffmpegSeekInFlight = true;
            _queuedSeekSeconds = null;

            Task.Run(() =>
            {
                bool Aborted() => _isClosingHandled || Volatile.Read(ref _playSession) != capturedSeekSession;

                FfmpegDecodeStream? newStream = null;
                bool fellBackToStart = false;

                try
                {
                    var pathVariants = FfmpegService.BuildInputPathVariants(trackSnapshot.Path);

                    foreach (var (pathVar, deleteAfter) in pathVariants)
                    {
                        if (Aborted())
                        {
                            if (deleteAfter) try { File.Delete(pathVar); } catch { }
                            continue;
                        }

                        var hints = new List<FfmpegInputHint> { FfmpegInputHint.Auto };
                        string ext2 = IOPath.GetExtension(trackSnapshot.Path);
                        if (ext2.Equals(".ape", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Ape);
                        else if (ext2.Equals(".wv", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Wavpack);

                        foreach (var hint in hints)
                        {
                            if (Aborted()) break;

                            FfmpegDecodeStream? candidate = null;
                            try
                            {
                                candidate = new FfmpegDecodeStream(ffmpeg, pathVar, hint,
                                    seekTo: absoluteSeek,
                                    deletePathOnDispose: deleteAfter ? pathVar : null);

                                var probe = new byte[Math.Max(candidate.WaveFormat.BlockAlign * 16, 128)];
                                int got = 0, tries = FfmpegService.ProbeRetryCount * 2;
                                while (got == 0 && tries-- > 0 && !Aborted())
                                {
                                    try { got = candidate.Read(probe, 0, probe.Length); } catch { break; }
                                    if (got == 0) Thread.Sleep(FfmpegService.ProbeRetryDelayMs);
                                }

                                if (!Aborted() && got > 0)
                                {
                                    candidate.PushBack(probe, got);
                                    newStream = candidate;
                                    candidate = null;
                                    break;
                                }

                                candidate.Dispose();
                                candidate = null;
                            }
                            catch
                            {
                                candidate?.Dispose();
                            }
                        }

                        if (newStream != null) break;
                        if (deleteAfter) try { File.Delete(pathVar); } catch { }
                    }

                    // Fallback: с начала файла
                    if (newStream == null && !Aborted())
                    {
                        try
                        {
                            newStream = CreateReadableFfmpegStream(ffmpeg, trackSnapshot.Path, shouldAbort: Aborted);
                            fellBackToStart = true;
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Любая неожиданная ошибка: всё равно дойдём до Dispatcher и снимем флаги
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ffmpegSeekInFlight = false;
                    double? queued = _queuedSeekSeconds;
                    _queuedSeekSeconds = null;

                    if (Aborted())
                    {
                        newStream?.Dispose();
                        EndSeekHold(holdToken);
                        return;
                    }

                    if (newStream == null)
                    {
                        _pendingPlayAfterFfmpegSeek = false;
                        SetPlaying(false);
                        EndSeekHold(holdToken);
                        return;
                    }

                    if (surroundRef == null || volumeRef == null ||
                        _surround != surroundRef || _volumeProvider != volumeRef)
                    {
                        _pendingPlayAfterFfmpegSeek = false;
                        newStream.Dispose();
                        EndSeekHold(holdToken);
                        return;
                    }

                    _mfReader = newStream;
                    ISampleProvider seekProvider;

                    if (cueEndSnapshot > cueStartSnap)
                    {
                        if (fellBackToStart)
                        {
                            var seg = new FfmpegCueSegment(newStream, cueStartSnap, cueEndSnapshot, seekOffsetSeconds: 0.0);
                            seg.StartSkipAsync();
                            _ffmpegCueSegment = seg;
                            seekProvider = seg;
                            ProgressSlider.Value = 0;
                            CurrentTime.Text = "0:00";
                        }
                        else
                        {
                            double remaining = Math.Max(0.1, cueEndSnapshot.TotalSeconds - absoluteSeek.TotalSeconds);
                            var seg = new FfmpegCueSegment(newStream,
                                start: TimeSpan.Zero,
                                end: TimeSpan.FromSeconds(remaining),
                                seekOffsetSeconds: clampedRel);
                            seg.MarkSkipDone();
                            _ffmpegCueSegment = seg;
                            seekProvider = seg;
                        }
                    }
                    else
                    {
                        _ffmpegCueSegment = null;
                        seekProvider = new FfmpegFloatProvider(newStream);
                        if (fellBackToStart)
                        {
                            ProgressSlider.Value = 0;
                            CurrentTime.Text = "0:00";
                        }
                    }

                    surroundRef.ReplaceSource(seekProvider);
                    _audioOutput.SetSource(volumeRef);
                    bool shouldPlay = wasPlaying || _pendingPlayAfterFfmpegSeek;
                    _pendingPlayAfterFfmpegSeek = false;
                    if (shouldPlay)
                    {
                        _audioOutput.Play();
                        SetPlaying(true);
                    }

                    EndSeekHold(holdToken);

                    // Пока шёл seek, пользователь кликнул ещё раз — выполняем последний запрос
                    if (queued.HasValue)
                        SeekTo(queued.Value);
                }));
            });
        }

        // ─── Вспомогательные ffmpeg-методы (нужны Playback и Seek) ──────────────

        private static FfmpegDecodeStream CreateReadableFfmpegStream(
            string ffmpeg, string path, TimeSpan seekTo = default, Func<bool>? shouldAbort = null)
        {
            string ext2 = IOPath.GetExtension(path);
            bool isApe = ext2.Equals(".ape", StringComparison.OrdinalIgnoreCase);
            bool isWv = ext2.Equals(".wv", StringComparison.OrdinalIgnoreCase);

            var attempts = new List<(FfmpegInputHint hint, int skip)> { (FfmpegInputHint.Auto, 0) };
            if (isApe)
            {
                attempts.Add((FfmpegInputHint.Ape, 0));
                if (FfmpegService.TryFindApeMacHeaderOffset(path, out int macOffset) && macOffset > 0)
                {
                    attempts.Add((FfmpegInputHint.Ape, macOffset));
                    attempts.Add((FfmpegInputHint.Auto, macOffset));
                }
            }
            else if (isWv)
            {
                attempts.Add((FfmpegInputHint.Wavpack, 0));
            }

            Exception? last = null;
            foreach (var (hint, skip) in attempts)
            {
                if (shouldAbort != null && shouldAbort())
                    throw new OperationCanceledException();

                FfmpegDecodeStream? stream = null;
                try
                {
                    stream = new FfmpegDecodeStream(ffmpeg, path, hint,
                        seekTo: seekTo,
                        skipInitialBytes: skip);
                    EnsureFfmpegStreamReadable(stream, path, shouldAbort);
                    return stream;
                }
                catch (Exception ex)
                {
                    stream?.Dispose();
                    last = ex;
                }
            }

            throw last ?? new Exception($"Не удалось открыть {IOPath.GetFileName(path)} через ffmpeg.");
        }

        private static void EnsureFfmpegStreamReadable(
            FfmpegDecodeStream stream, string path, Func<bool>? shouldAbort = null)
        {
            int probeSize = Math.Max(stream.WaveFormat.BlockAlign * 32, 256);
            var probe = new byte[probeSize];
            int got = 0, retries = FfmpegService.ProbeRetryCount;
            while (got == 0 && retries-- > 0)
            {
                if (shouldAbort != null && shouldAbort())
                    throw new OperationCanceledException();

                try { got = stream.Read(probe, 0, probe.Length); } catch { break; }
                if (got == 0) Thread.Sleep(FfmpegService.ProbeRetryDelayMs);
            }
            if (got <= 0)
            {
                Thread.Sleep(200);
                string details = stream.LastError;
                string name = IOPath.GetFileName(path);
                throw new Exception(string.IsNullOrWhiteSpace(details)
                    ? $"FFmpeg не смог декодировать файл {name}."
                    : $"FFmpeg не смог декодировать файл {name}: {details}");
            }
            stream.PushBack(probe, got);
        }
    }
}

#nullable restore
