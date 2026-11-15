// MainWindow.FfmpegStream.cs — потоковый декодер через ffmpeg pipe
#nullable enable

using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace AuroraPlayer
{
    internal enum FfmpegInputHint { Auto, Ape, Wavpack }

    // ─── FfmpegDecodeStream ───────────────────────────────────────────────────────
    // WaveStream, читающий float-сэмплы из stdout ffmpeg-процесса.
    // Поддерживает APE, WV и любой формат, понятный ffmpeg.
    internal sealed class FfmpegDecodeStream : WaveStream
    {
        private readonly object _procLock = new();
        private Process? _proc;
        private Stream? _stdout;
        private NamedPipeServerStream? _pipe;
        private CancellationTokenSource? _pipeCts;
        private Thread? _stdinThread;

        private readonly WaveFormat _fmt;
        private long _bytesRead;
        private long _startBytes;
        private readonly string _ffmpegPath;
        private readonly string _inputPath;
        private readonly FfmpegInputHint _inputHint;
        private readonly int _skipInitialBytes;
        private readonly List<string> _deletePathsOnDispose = new();
        private readonly object _errLock = new();
        private readonly StringBuilder _stderr = new();
        private DataReceivedEventHandler? _errHandler;

        private byte[] _prefetched = Array.Empty<byte>();
        private int _prefetchedOffset;
        private int _prefetchedCount;
        private volatile bool _disposed;
        private volatile bool _stopping;

        public override WaveFormat WaveFormat => _fmt;
        public override long Length => -1;

        public override long Position
        {
            get => Interlocked.Read(ref _startBytes) + Interlocked.Read(ref _bytesRead);
            set
            {
                if (_disposed) return;
                if (value < 0) value = 0;
                RestartAtBytes(value);
            }
        }

        public TimeSpan CurrentPosition =>
            TimeSpan.FromSeconds((double)Position / _fmt.AverageBytesPerSecond);

        public string LastError
        {
            get { lock (_errLock) return _stderr.ToString().Trim(); }
        }

        public FfmpegDecodeStream(
            string ffmpeg, string input, FfmpegInputHint inputHint,
            TimeSpan seekTo = default, int skipInitialBytes = 0,
            string? deletePathOnDispose = null)
        {
            _fmt = WaveFormat.CreateIeeeFloatWaveFormat(
                MainWindow.FfmpegOutputSampleRate,
                MainWindow.FfmpegOutputChannels);

            _ffmpegPath = ffmpeg;
            _inputPath = input;
            _inputHint = inputHint;
            _skipInitialBytes = Math.Max(0, skipInitialBytes);

            if (!string.IsNullOrWhiteSpace(deletePathOnDispose))
                _deletePathsOnDispose.Add(deletePathOnDispose);

            StartProcess(seekTo);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed || _stopping || count == 0)
                return 0;

            int totalRead = 0;

            if (_prefetchedCount > 0)
            {
                int take = Math.Min(_prefetchedCount, count);
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, buffer, offset, take);
                _prefetchedOffset += take;
                _prefetchedCount -= take;
                totalRead += take;
                if (_prefetchedCount == 0)
                {
                    _prefetched = Array.Empty<byte>();
                    _prefetchedOffset = 0;
                }
            }

            var stdout = _stdout;
            while (totalRead < count && stdout != null && !_disposed && !_stopping)
            {
                int got;
                try
                {
                    got = stdout.Read(buffer, offset + totalRead, count - totalRead);
                }
                catch (Exception) when (_disposed || _stopping)
                {
                    break;
                }
                if (got == 0) break;
                totalRead += got;
            }

            Interlocked.Add(ref _bytesRead, totalRead);
            return totalRead;
        }

        public void PushBack(byte[] data, int count)
        {
            if (data == null || count <= 0) return;
            count = Math.Min(count, data.Length);

            var copy = new byte[count + _prefetchedCount];
            Buffer.BlockCopy(data, 0, copy, 0, count);
            if (_prefetchedCount > 0)
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, copy, count, _prefetchedCount);

            _prefetched = copy;
            _prefetchedOffset = 0;
            _prefetchedCount = copy.Length;
            Interlocked.Add(ref _bytesRead, -count);
        }

        public void RegisterDeleteOnDispose(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_procLock)
            {
                if (!_deletePathsOnDispose.Contains(path))
                    _deletePathsOnDispose.Add(path);
            }
        }

        private void StartProcess(TimeSpan offset)
        {
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;

            long rawStart = (long)(_fmt.AverageBytesPerSecond * offset.TotalSeconds);
            long alignedStart = rawStart - (rawStart % _fmt.BlockAlign);
            Interlocked.Exchange(ref _startBytes, alignedStart);
            Interlocked.Exchange(ref _bytesRead, 0);

            string seconds = offset.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
            string inputFmt = _inputHint switch
            {
                FfmpegInputHint.Ape => "-f ape ",
                FfmpegInputHint.Wavpack => "-f wavpack ",
                _ => ""
            };
            string seekArg = offset > TimeSpan.Zero ? $"-ss {seconds} " : "";

            bool pathHasNonAscii = FfmpegService.PathHasNonAscii(_inputPath);
            bool formatNeedsSeekableInput =
                _inputHint == FfmpegInputHint.Ape || _inputHint == FfmpegInputHint.Wavpack;
            bool useNamedPipe = pathHasNonAscii && !formatNeedsSeekableInput;

            // При чтении через pipe пропуск начальных байтов делает сам FileStream.Seek,
            // поэтому -skip_initial_bytes в аргументы ffmpeg не дублируем.
            string skipArg = (!useNamedPipe && _skipInitialBytes > 0)
                ? $"-skip_initial_bytes {_skipInitialBytes} "
                : "";

            string ffmpegInputPath;
            NamedPipeServerStream? namedPipe = null;
            CancellationTokenSource? pipeCts = null;

            if (useNamedPipe)
            {
                string pipeName = $"aurora_{Guid.NewGuid():N}";
                ffmpegInputPath = $@"\\.\pipe\{pipeName}";
                namedPipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 65536);
                pipeCts = new CancellationTokenSource();
            }
            else
            {
                ffmpegInputPath = _inputPath;
            }

            string inputArg = $"{inputFmt}{skipArg}-i \"{ffmpegInputPath}\" {seekArg}";
            string args = "-loglevel error -nostats -hide_banner -nostdin " +
                          inputArg +
                          "-vn -sn -dn -sample_fmt flt -f f32le " +
                          $"-ar {MainWindow.FfmpegOutputSampleRate} -ac {MainWindow.FfmpegOutputChannels} pipe:1";

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo(_ffmpegPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            lock (_errLock) _stderr.Clear();
            DataReceivedEventHandler errHandler = (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_errLock)
                {
                    if (_stderr.Length < 4000)
                        _stderr.AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += errHandler;

            try
            {
                proc.Start();
                proc.BeginErrorReadLine();
            }
            catch
            {
                proc.ErrorDataReceived -= errHandler;
                try { proc.Dispose(); } catch { }
                try { pipeCts?.Dispose(); } catch { }
                try { namedPipe?.Dispose(); } catch { }
                throw;
            }

            lock (_procLock)
            {
                if (_disposed)
                {
                    proc.ErrorDataReceived -= errHandler;
                    try { proc.CancelErrorRead(); } catch { }
                    try { proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                    try { pipeCts?.Dispose(); } catch { }
                    try { namedPipe?.Dispose(); } catch { }
                    return;
                }

                _stopping = false;
                _proc = proc;
                _errHandler = errHandler;
                _stdout = proc.StandardOutput.BaseStream;
                _pipe = namedPipe;
                _pipeCts = pipeCts;
                _prefetched = Array.Empty<byte>();
                _prefetchedOffset = 0;
                _prefetchedCount = 0;
            }

            if (namedPipe != null && pipeCts != null)
            {
                string pathToFeed = _inputPath;
                long skipBytes = _skipInitialBytes > 0 ? _skipInitialBytes : 0;
                var pipeToFeed = namedPipe;
                var token = pipeCts.Token;

                var feeder = new Thread(() =>
                {
                    try
                    {
                        pipeToFeed.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                        using var fs = new FileStream(
                            pathToFeed, FileMode.Open, FileAccess.Read, FileShare.Read,
                            65536, useAsync: true);
                        if (skipBytes > 0 && skipBytes < fs.Length)
                            fs.Seek(skipBytes, SeekOrigin.Begin);
                        fs.CopyToAsync(pipeToFeed, 65536, token).GetAwaiter().GetResult();
                    }
                    catch { }
                    finally
                    {
                        try { pipeToFeed.Dispose(); } catch { }
                    }
                })
                {
                    IsBackground = true,
                    Name = "ffmpeg-pipe-feeder"
                };

                _stdinThread = feeder;
                feeder.Start();
            }
        }

        private void KillProcess()
        {
            Process? proc;
            Stream? stdout;
            NamedPipeServerStream? pipe;
            CancellationTokenSource? pipeCts;
            Thread? stdinThread;
            DataReceivedEventHandler? errHandler;

            lock (_procLock)
            {
                _stopping = true;
                proc = _proc;
                stdout = _stdout;
                pipe = _pipe;
                pipeCts = _pipeCts;
                stdinThread = _stdinThread;
                errHandler = _errHandler;

                _proc = null;
                _stdout = null;
                _pipe = null;
                _pipeCts = null;
                _stdinThread = null;
                _errHandler = null;
            }

            try { pipeCts?.Cancel(); } catch { }
            try { pipe?.Dispose(); } catch { }
            try { stdout?.Dispose(); } catch { }

            if (proc != null)
            {
                if (errHandler != null)
                    try { proc.ErrorDataReceived -= errHandler; } catch { }
                try { proc.CancelErrorRead(); } catch { }
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { proc.Dispose(); } catch { }
            }

            try { stdinThread?.Join(200); } catch { }
            try { pipeCts?.Dispose(); } catch { }
        }

        private void RestartAtBytes(long absoluteBytes)
        {
            KillProcess();
            if (_disposed) return;

            long aligned = absoluteBytes - (absoluteBytes % _fmt.BlockAlign);
            StartProcess(TimeSpan.FromSeconds(aligned / (double)_fmt.AverageBytesPerSecond));
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            _disposed = true;
            base.Dispose(disposing);
            if (!disposing) return;

            KillProcess();

            List<string> toDelete;
            lock (_procLock)
            {
                toDelete = new List<string>(_deletePathsOnDispose);
                _deletePathsOnDispose.Clear();
            }

            foreach (var path in toDelete)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    // ─── FfmpegCueSegment ────────────────────────────────────────────────────────
    // Аналог CueSegmentProvider, но для потоков ffmpeg (APE/WV).
    internal sealed class FfmpegCueSegment : ISampleProvider
    {
        private readonly FfmpegFloatProvider _provider;
        private readonly long _startSample;
        private readonly long _endSample;
        private long _position;
        private int _skipState; // 0 = не запущен, 1 = выполняется, 2 = завершён, 3 = отменён
        private readonly double _seekOffsetSeconds;

        public WaveFormat WaveFormat => _provider.WaveFormat;

        public double PositionSeconds =>
            _seekOffsetSeconds + (double)Interlocked.Read(ref _position) / WaveFormat.Channels / WaveFormat.SampleRate;

        public FfmpegCueSegment(WaveStream stream, TimeSpan start, TimeSpan end,
                                double seekOffsetSeconds = 0.0)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _provider = new FfmpegFloatProvider(stream);
            _seekOffsetSeconds = seekOffsetSeconds;

            int ch = _provider.WaveFormat.Channels;
            int sr = _provider.WaveFormat.SampleRate;
            _startSample = start > TimeSpan.Zero ? (long)(start.TotalSeconds * sr) * ch : 0;
            _endSample = end > start ? (long)(end.TotalSeconds * sr) * ch : 0;
        }

        /// <summary>Skip уже выполнен внешним средством (например -ss в ffmpeg).</summary>
        public void MarkSkipDone()
        {
            Interlocked.Exchange(ref _skipState, 2);
        }

        /// <summary>Отменяет фоновый skip при смене трека или остановке.</summary>
        public void CancelSkip()
        {
            Interlocked.Exchange(ref _skipState, 3);
        }

        /// <summary>Запускает skip в фоне, чтобы не блокировать аудио-поток NAudio.</summary>
        public void StartSkipAsync()
        {
            if (_startSample <= 0)
            {
                Interlocked.CompareExchange(ref _skipState, 2, 0);
                return;
            }

            if (Interlocked.CompareExchange(ref _skipState, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    long toSkip = _startSample;
                    var tmp = new float[8192];
                    while (toSkip > 0 && Volatile.Read(ref _skipState) == 1)
                    {
                        int got = _provider.Read(tmp, 0, (int)Math.Min(toSkip, tmp.Length));
                        if (got == 0) break;
                        toSkip -= got;
                    }

                    // Переводим в "готово", только если skip не был отменён
                    Interlocked.CompareExchange(ref _skipState, 2, 1);
                }
                catch
                {
                    // Поток был закрыт/освобождён во время skip — останавливаем сегмент безопасно
                    Interlocked.Exchange(ref _skipState, 3);
                }
            });
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int state = Volatile.Read(ref _skipState);
            if (state == 3) return 0; // Отменён / поток закрыт

            // Пока skip не завершён — отдаём тишину, не блокируя аудио-поток
            if (state != 2)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int toRead = count;
            if (_endSample > 0)
            {
                long remaining = (_endSample - _startSample) - Interlocked.Read(ref _position);
                if (remaining <= 0) return 0;
                toRead = (int)Math.Min(count, remaining);
            }

            int read = _provider.Read(buffer, offset, toRead);
            Interlocked.Add(ref _position, read);
            return read;
        }
    }
}

#nullable restore