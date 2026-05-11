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
                string? tempCreated = null;

                foreach (var (pathVar, deleteAfter) in pathVariants)
                {
                    var hints = new List<FfmpegInputHint> { FfmpegInputHint.Auto };
                    string ext2 = IOPath.GetExtension(trackSnapshot.Path);
                    if (ext2.Equals(".ape", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Ape);
                    else if (ext2.Equals(".wv", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Wavpack);
                    if (deleteAfter) tempCreated = pathVar;

                    foreach (var hint in hints)
                    {
                        try
                        {
                            var candidate = new FfmpegDecodeStream(ffmpeg, pathVar, hint,
                                seekTo: absoluteSeek,
                                deletePathOnDispose: deleteAfter ? pathVar : null);

                            // Зондируем первые байты
                            var probe = new byte[Math.Max(candidate.WaveFormat.BlockAlign * 16, 128)];
                            int got = 0, tries = 120;
                            while (got == 0 && tries-- > 0)
                            {
                                try { got = candidate.Read(probe, 0, probe.Length); } catch { break; }
                                if (got == 0) System.Threading.Thread.Sleep(FfmpegService.ProbeRetryDelayMs);
                            }
                            if (got > 0)
                            {
                                candidate.PushBack(probe, got);
                                newStream   = candidate;
                                tempCreated = null;
                                break;
                            }
                            candidate.Dispose();
                        }
                        catch { }
                    }
                    if (newStream != null) break;
                    if (tempCreated != null) { try { File.Delete(tempCreated); } catch { } tempCreated = null; }
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
                        if (wasPlaying) _audioOutput.Play();
                    }
                    else
                    {
                        // Цепочка уже другая — выбрасываем устаревший поток
                        newStream?.Dispose();
                    }
                }));
            });
        }

        // ─── Вспомогательные ffmpeg-методы (нужны Playback и Seek) ──────────────

        private static FfmpegDecodeStream CreateReadableFfmpegStream(string ffmpeg, string path, TimeSpan seekTo = default)
        {
            string workingPath = path;
            string? asciiCopy  = null;

            if (FfmpegService.PathHasNonAscii(path))
            {
                try
                {
                    string ext   = IOPath.GetExtension(path);
                    asciiCopy    = IOPath.Combine(FfmpegService.GetSafeAsciiTempPath(),
                                                   $"aurora_{Guid.NewGuid():N}{ext}");
                    File.Copy(path, asciiCopy, true);
                    workingPath  = asciiCopy;
                }
                catch { asciiCopy = null; workingPath = path; }
            }

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
                        skipInitialBytes: skip,
                        deletePathOnDispose: asciiCopy);
                    EnsureFfmpegStreamReadable(stream, path);
                    if (asciiCopy != null) { asciiCopy = null; } // transferred to stream
                    return stream;
                }
                catch (Exception ex) { last = ex; }
            }

            if (asciiCopy != null) try { File.Delete(asciiCopy); } catch { }
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

        private static string? TryDecodeEntireFileToWavUsingFfmpeg(
            string ffmpeg, string originalPath, out string lastError)
        {
            lastError = "";
            string ext    = IOPath.GetExtension(originalPath);
            string safeDir = FfmpegService.GetSafeAsciiTempPath();
            string wavOut  = IOPath.Combine(safeDir, $"aurora_{Guid.NewGuid():N}.wav");
            string lastErr = "";

            var hints = new List<FfmpegInputHint> { FfmpegInputHint.Auto };
            if (ext.Equals(".ape", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Ape);
            else if (ext.Equals(".wv", StringComparison.OrdinalIgnoreCase)) hints.Add(FfmpegInputHint.Wavpack);

            foreach (var (pathVar, deleteAfter) in FfmpegService.BuildInputPathVariants(originalPath))
            {
                try
                {
                    foreach (var hint in hints)
                    {
                        try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
                        if (RunFfmpegDecodeToWavFile(ffmpeg, pathVar, hint, wavOut, 0, out string err))
                            return wavOut;
                        if (!string.IsNullOrEmpty(err))
                        {
                            FfmpegService.AppendDecodeLog(
                                $"WAV fail file='{originalPath}' variant='{pathVar}' hint='{hint}' err='{err}'");
                            lastErr = err;
                        }
                    }
                }
                finally { if (deleteAfter) try { File.Delete(pathVar); } catch { } }
            }

            lastError = lastErr;
            try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
            return null;
        }

        private static bool RunFfmpegDecodeToWavFile(
            string ffmpeg, string inputPath, FfmpegInputHint hint,
            string wavOut, int skipInitialBytes, out string lastError)
        {
            lastError = "";
            string inputFmt = hint switch
            {
                FfmpegInputHint.Ape     => "-f ape ",
                FfmpegInputHint.Wavpack => "-f wavpack ",
                _                       => ""
            };
            string skipArg = skipInitialBytes > 0 ? $"-skip_initial_bytes {skipInitialBytes} " : "";
            string args = "-y -nostdin -hide_banner -loglevel error -nostats " +
                          $"-probesize 100M -analyzeduration 100M " +
                          $"{inputFmt}{skipArg}-i \"{inputPath}\" -vn -sn -dn " +
                          $"-ar {MainWindow.FfmpegOutputSampleRate} -ac {MainWindow.FfmpegOutputChannels} " +
                          $"-c:a pcm_f32le -f wav \"{wavOut}\"";
            try
            {
                var sb = new System.Text.StringBuilder();
                using var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(ffmpeg, args)
                    {
                        UseShellExecute       = false,
                        CreateNoWindow        = true,
                        RedirectStandardError = true,
                        WorkingDirectory      = FfmpegService.GetSafeAsciiTempPath(),
                    },
                };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(600_000)) { try { p.Kill(); } catch { } return false; }
                lastError = sb.ToString().Trim();
                return p.ExitCode == 0 && File.Exists(wavOut) && new System.IO.FileInfo(wavOut).Length > 200;
            }
            catch (Exception ex) { lastError = ex.Message; return false; }
        }

    }
}
