// MainWindow.Seek.cs — перемотка и ffmpeg-seek
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using NAudio.Wave;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Прогресс-слайдер ────────────────────────────────────────────────────

        private void ProgressSlider_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!HasReader) return;
            if (e.OriginalSource is System.Windows.Shapes.Ellipse) return;
            _isDraggingSlider     = true;
            _seekHandledByMouseUp = false;
            ProgressSlider.CaptureMouse();
            double pos = CalcSliderPos(e);
            ProgressSlider.Value = pos;
            CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(pos));
            e.Handled = true;
        }

        private void ProgressSlider_MouseUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isDraggingSlider) return;
            _isDraggingSlider     = false;
            _seekHandledByMouseUp = true;
            ProgressSlider.ReleaseMouseCapture();
            SeekTo(CalcSliderPos(e));
        }

        private void ProgressSlider_ValueChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSlider)
                CurrentTime.Text = FormatTime(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void ProgressSlider_DragStarted(object s, DragStartedEventArgs e)
        {
            _isDraggingSlider     = true;
            _seekHandledByMouseUp = false;
        }

        private void ProgressSlider_DragCompleted(object s, DragCompletedEventArgs e)
        {
            if (_seekHandledByMouseUp) { _seekHandledByMouseUp = false; return; }
            _isDraggingSlider = false;
            ProgressSlider.ReleaseMouseCapture();
            if (HasReader) SeekTo(ProgressSlider.Value);
        }

        private double CalcSliderPos(System.Windows.Input.MouseEventArgs e)
        {
            double x     = e.GetPosition(ProgressSlider).X;
            double ratio = Math.Clamp(x / ProgressSlider.ActualWidth, 0.0, 1.0);
            return ProgressSlider.Minimum + ratio * (ProgressSlider.Maximum - ProgressSlider.Minimum);
        }

        // ─── Универсальный Seek ───────────────────────────────────────────────────

        private void SeekTo(double relativeSeconds)
        {
            if (!HasReader) return;
            _pendingSeekOnNextPlay = null;

            if (_mfReader is FfmpegDecodeStream oldFfmpeg)
            {
                SeekFfmpegAsync(oldFfmpeg, relativeSeconds);
                return;
            }

            // ApeRingBufferStream: перезапускаем ffmpeg с нужным смещением,
            // аналогично SeekFfmpegAsync, но через Position (RestartAtBytes внутри).
            if (_mfReader is ApeRingBufferStream ringStream)
            {
                SeekRingBufferAsync(ringStream, relativeSeconds);
                return;
            }

            if (_cueSegment != null && _audioReader != null)
            {
                var seekAbs = TimeSpan.FromSeconds(_cueStart.TotalSeconds + relativeSeconds);
                if (_cueEnd > TimeSpan.Zero && seekAbs >= _cueEnd)
                    seekAbs = _cueEnd - TimeSpan.FromSeconds(0.5);

                _audioOutput.Pause();
                _cueSegment.SeekTo(seekAbs);
                _surround?.FlushBuffer();
                if (_isPlaying) _audioOutput.Play();
                return;
            }

            if (_audioReader != null)
            {
                _audioReader.CurrentTime = TimeSpan.FromSeconds(relativeSeconds);
                return;
            }

            if (_mfReader != null)
                _mfReader.CurrentTime = TimeSpan.FromSeconds(relativeSeconds);
        }

        // ─── Ring buffer seek (не блокирует UI) ──────────────────────────────────

        private void SeekRingBufferAsync(ApeRingBufferStream oldRing, double relativeSeconds)
        {
            bool wasPlaying    = _isPlaying;
            var absoluteSeek   = TimeSpan.FromSeconds(_cueStart.TotalSeconds + relativeSeconds);
            var cueEndSnapshot = _cueEnd;
            var surroundRef    = _surround;
            var volumeRef      = _volumeProvider;

            Interlocked.Increment(ref _playSession);
            int capturedSeekSession = _playSession;

            _audioOutput.Pause();

            // Перезапускаем ring buffer с новой позицией (убивает старый ffmpeg процесс,
            // запускает новый с -ss seekOffset — без записи на диск).
            oldRing.Position = (long)(absoluteSeek.TotalSeconds * oldRing.WaveFormat.AverageBytesPerSecond);

            // Даём буферу наполниться в фоне, чтобы не подвешивать UI.
            Task.Run(() =>
            {
                Thread.Sleep(400); // ~400 мс достаточно для первого чанка APE

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_playSession != capturedSeekSession) return;
                    if (_mfReader != oldRing) return; // трек сменился

                    ISampleProvider seekProvider;
                    if (cueEndSnapshot > TimeSpan.Zero)
                    {
                        double remaining = cueEndSnapshot.TotalSeconds - absoluteSeek.TotalSeconds;
                        var seg = new FfmpegCueSegment(oldRing,
                            start: TimeSpan.Zero,
                            end:   remaining > 0 ? TimeSpan.FromSeconds(remaining) : TimeSpan.Zero,
                            seekOffsetSeconds: relativeSeconds);
                        seg.MarkSkipDone();
                        _ffmpegCueSegment = seg;
                        seekProvider      = seg;
                    }
                    else
                    {
                        _ffmpegCueSegment = null;
                        seekProvider      = new FfmpegFloatProvider(oldRing);
                    }

                    if (surroundRef != null && volumeRef != null
                        && _surround == surroundRef && _volumeProvider == volumeRef)
                    {
                        surroundRef.ReplaceSource(seekProvider);
                        _audioOutput.SetSource(volumeRef);
                        bool shouldPlay = wasPlaying || _pendingPlayAfterFfmpegSeek;
                        _pendingPlayAfterFfmpegSeek = false;
                        if (shouldPlay) { _audioOutput.Play(); SetPlaying(true); }
                    }
                    else
                    {
                        _pendingPlayAfterFfmpegSeek = false;
                    }
                }));
            });
        }

        // ─── Ffmpeg seek в Task.Run (не блокирует UI) ────────────────────────────

        private void SeekFfmpegAsync(FfmpegDecodeStream oldFfmpeg, double relativeSeconds)
        {
            var track = _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex] : null;
            if (track == null) return;
            string? ffmpeg = FfmpegService.FindFfmpeg();
            if (ffmpeg == null) return;

            bool wasPlaying       = _isPlaying;
            var absoluteSeek      = TimeSpan.FromSeconds(_cueStart.TotalSeconds + relativeSeconds);
            var cueEndSnapshot    = _cueEnd;
            var trackSnapshot     = track;
            var surroundRef       = _surround;
            var volumeRef         = _volumeProvider;

            Interlocked.Increment(ref _playSession);
            int capturedSeekSession = _playSession;

            // Pause а не Stop — Stop без _manualStop может вызвать ложный TrackEnded
            _audioOutput.Pause();
            oldFfmpeg.Dispose();
            _mfReader = null;

            Task.Run(() =>
            {
                var pathVariants = FfmpegService.BuildInputPathVariants(trackSnapshot.Path);
                FfmpegDecodeStream? newStream = null;

                foreach (var (pathVar, deleteAfter) in pathVariants)
                {
                    var hints = new List<FfmpegInputHint> { FfmpegInputHint.Auto };
                    string ext2 = IOPath.GetExtension(trackSnapshot.Path);
                    if (ext2.Equals(".ape", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Ape);
                    else if (ext2.Equals(".wv", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Wavpack);

                    foreach (var hint in hints)
                    {
                        try
                        {
                            var candidate = new FfmpegDecodeStream(ffmpeg, pathVar, hint,
                                seekTo: absoluteSeek,
                                deletePathOnDispose: deleteAfter ? pathVar : null);

                            // Зондируем первые байты
                            var probe = new byte[Math.Max(candidate.WaveFormat.BlockAlign * 16, 128)];
                            int got = 0, tries = FfmpegService.ProbeRetryCount * 2;  // 60*2=120, но через константу
                            while (got == 0 && tries-- > 0)
                            {
                                try { got = candidate.Read(probe, 0, probe.Length); } catch { break; }
                                if (got == 0) System.Threading.Thread.Sleep(FfmpegService.ProbeRetryDelayMs);
                            }
                            if (got > 0)
                            {
                                candidate.PushBack(probe, got);
                                newStream = candidate;
                                break;
                            }
                            candidate.Dispose();
                        }
                        catch { }
                    }
                    if (newStream != null) break;
                    if (deleteAfter) try { File.Delete(pathVar); } catch { }
                }

                // Fallback: с начала файла
                if (newStream == null)
                    try { newStream = CreateReadableFfmpegStream(ffmpeg, trackSnapshot.Path); } catch { }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_playSession != capturedSeekSession) { newStream?.Dispose(); return; }
                    if (newStream == null) return;

                    _mfReader = newStream;
                    ISampleProvider seekProvider;

                    if (cueEndSnapshot > TimeSpan.Zero)
                    {
                        double remaining = cueEndSnapshot.TotalSeconds - absoluteSeek.TotalSeconds;
                        var seg = new FfmpegCueSegment(newStream,
                            start: TimeSpan.Zero,
                            end:   remaining > 0 ? TimeSpan.FromSeconds(remaining) : TimeSpan.Zero,
                            seekOffsetSeconds: relativeSeconds);
                        seg.MarkSkipDone();
                        _ffmpegCueSegment = seg;
                        seekProvider      = seg;
                    }
                    else
                    {
                        _ffmpegCueSegment = null;
                        seekProvider      = new FfmpegFloatProvider(newStream);
                    }

                    // Проверяем что _surround и _volumeProvider не сменились
                    // пока Task.Run работал (пользователь мог переключить трек).
                    if (surroundRef != null && volumeRef != null && _surround == surroundRef && _volumeProvider == volumeRef)
                    {
                        surroundRef.ReplaceSource(seekProvider);
                        _audioOutput.SetSource(volumeRef);
                        bool shouldPlay = wasPlaying || _pendingPlayAfterFfmpegSeek;
                        _pendingPlayAfterFfmpegSeek = false;
                        if (shouldPlay) { _audioOutput.Play(); SetPlaying(true); }
                    }
                    else
                    {
                        _pendingPlayAfterFfmpegSeek = false;
                        // Цепочка уже другая — выбрасываем устаревший поток
                        newStream?.Dispose();
                    }
                }));
            });
        }

        // ─── Вспомогательные ffmpeg-методы (нужны Playback и Seek) ──────────────

        private static FfmpegDecodeStream CreateReadableFfmpegStream(string ffmpeg, string path, TimeSpan seekTo = default)
        {
            // Не-ASCII пути (кириллица и т.п.) обрабатываются внутри FfmpegDecodeStream
            // через stdin pipe — файл подаётся в ffmpeg напрямую, без копирования.
            string workingPath = path;

            string ext2  = IOPath.GetExtension(path);
            bool   isApe = ext2.Equals(".ape", StringComparison.OrdinalIgnoreCase);
            bool   isWv  = ext2.Equals(".wv",  StringComparison.OrdinalIgnoreCase);

            var attempts = new List<(FfmpegInputHint hint, int skip)> { (FfmpegInputHint.Auto, 0) };
            if (isApe)
            {
                attempts.Add((FfmpegInputHint.Ape, 0));
                if (FfmpegService.TryFindApeMacHeaderOffset(workingPath, out int macOffset) && macOffset > 0)
                {
                    attempts.Add((FfmpegInputHint.Ape,  macOffset));
                    attempts.Add((FfmpegInputHint.Auto, macOffset));
                }
            }
            else if (isWv)
                attempts.Add((FfmpegInputHint.Wavpack, 0));

            Exception? last = null;
            foreach (var (hint, skip) in attempts)
            {
                try
                {
                    var stream = new FfmpegDecodeStream(ffmpeg, workingPath, hint,
                        seekTo: seekTo,
                        skipInitialBytes: skip);
                    EnsureFfmpegStreamReadable(stream, path);
                    return stream;
                }
                catch (Exception ex) { last = ex; }
            }

            throw last ?? new Exception($"Не удалось открыть {IOPath.GetFileName(path)} через ffmpeg.");
        }

        private static void EnsureFfmpegStreamReadable(FfmpegDecodeStream stream, string path)
        {
            int probeSize = Math.Max(stream.WaveFormat.BlockAlign * 32, 256);
            var probe     = new byte[probeSize];
            int got = 0, retries = FfmpegService.ProbeRetryCount;
            while (got == 0 && retries-- > 0)
            {
                try { got = stream.Read(probe, 0, probe.Length); } catch { break; }
                if (got == 0) System.Threading.Thread.Sleep(FfmpegService.ProbeRetryDelayMs);
            }
            if (got <= 0)
            {
                System.Threading.Thread.Sleep(200);
                string details = stream.LastError;
                string name    = IOPath.GetFileName(path);
                throw new Exception(string.IsNullOrWhiteSpace(details)
                    ? $"FFmpeg не смог декодировать файл {name}."
                    : $"FFmpeg не смог декодировать файл {name}: {details}");
            }
            stream.PushBack(probe, got);
        }

        // TryDecodeEntireFileToWavUsingFfmpeg и RunFfmpegDecodeToWavFile удалены.
        // APE/WV теперь декодируется через ApeRingBufferStream (ring buffer в памяти).
        // Запись temp WAV на диск C: исключена полностью.

    }
}
