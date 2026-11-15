// ApeRingBufferStream.cs — потоковый декодер APE/WV через ring buffer в памяти.
#nullable enable

using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace AuroraPlayer
{
    internal sealed class ApeRingBufferStream : WaveStream
    {
        // ~12 МБ = ~30 сек стерео float 48 кГц.
        private const int RingCapacity = 12 * 1024 * 1024;

        private readonly byte[] _ring = new byte[RingCapacity];
        private int _writePos;
        private int _readPos;
        private int _available;
        private bool _producerDone;
        private int _producerGen;
        private readonly object _ringLock = new();

        private readonly WaveFormat _fmt;
        private long _position;
        private volatile bool _disposed;

        private readonly object _procSync = new();
        private Thread? _producerThread;
        private CancellationTokenSource? _producerCts;
        private Process? _producerProc;

        private readonly string _ffmpegPath;
        private readonly string _inputPath;
        private readonly FfmpegInputHint _inputHint;
        private readonly int _skipInitialBytes;

        private readonly object _errLock = new();
        private readonly StringBuilder _stderr = new();
        public string LastError { get { lock (_errLock) return _stderr.ToString().Trim(); } }

        public override WaveFormat WaveFormat => _fmt;
        public override long Length => long.MaxValue;

        public override long Position
        {
            get => Interlocked.Read(ref _position);
            set
            {
                if (_disposed) return;
                if (value < 0) value = 0;
                RestartAtBytes(value);
            }
        }

        public TimeSpan CurrentPosition =>
            TimeSpan.FromSeconds((double)Position / _fmt.AverageBytesPerSecond);

        public ApeRingBufferStream(
            string ffmpegPath,
            string inputPath,
            FfmpegInputHint inputHint = FfmpegInputHint.Ape,
            int skipInitialBytes = 0,
            TimeSpan seekTo = default)
        {
            _fmt = WaveFormat.CreateIeeeFloatWaveFormat(
                MainWindow.FfmpegOutputSampleRate,
                MainWindow.FfmpegOutputChannels);

            _ffmpegPath = ffmpegPath;
            _inputPath = inputPath;
            _inputHint = inputHint;
            _skipInitialBytes = Math.Max(0, skipInitialBytes);

            if (seekTo < TimeSpan.Zero) seekTo = TimeSpan.Zero;
            long rawStart = (long)(_fmt.AverageBytesPerSecond * seekTo.TotalSeconds);
            long aligned = rawStart - (rawStart % _fmt.BlockAlign);
            Interlocked.Exchange(ref _position, aligned);

            StartProducer(seekTo);
        }

        // ─── Read (audio thread) ──────────────────────────────────────────────────
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed || count == 0)
                return 0;

            int totalRead = 0;

            lock (_ringLock)
            {
                while (totalRead < count && !_disposed)
                {
                    if (_available > 0)
                    {
                        int got = Math.Min(count - totalRead, _available);
                        int part1 = Math.Min(got, RingCapacity - _readPos);
                        Buffer.BlockCopy(_ring, _readPos, buffer, offset + totalRead, part1);
                        if (got > part1)
                            Buffer.BlockCopy(_ring, 0, buffer, offset + totalRead + part1, got - part1);

                        _readPos = (_readPos + got) % RingCapacity;
                        _available -= got;
                        totalRead += got;

                        Monitor.PulseAll(_ringLock);
                        continue;
                    }

                    if (_producerDone)
                    {
                        FfmpegService.AppendDecodeLog(
                            $"RING EOF totalRead={totalRead} count={count} pos={Interlocked.Read(ref _position)} file='{_inputPath}'");
                        break;
                    }

                    Monitor.Wait(_ringLock, 100);
                }
            }

            Interlocked.Add(ref _position, totalRead);
            return totalRead;
        }

        // ─── Seek ─────────────────────────────────────────────────────────────────
        private void RestartAtBytes(long absoluteBytes)
        {
            StopProducer();
            if (_disposed) return;

            long aligned = absoluteBytes - (absoluteBytes % _fmt.BlockAlign);
            ResetRing();
            Interlocked.Exchange(ref _position, aligned);
            StartProducer(TimeSpan.FromSeconds(aligned / (double)_fmt.AverageBytesPerSecond));
        }

        // ─── Producer ─────────────────────────────────────────────────────────────
        private void StartProducer(TimeSpan seekOffset)
        {
            CancellationTokenSource cts;
            int gen;
            Thread thread;

            lock (_ringLock)
            {
                _producerGen++;
                gen = _producerGen;
            }

            lock (_errLock) _stderr.Clear();

            lock (_procSync)
            {
                if (_disposed) return;
                cts = new CancellationTokenSource();
                _producerCts = cts;
                var token = cts.Token;

                thread = new Thread(() => ProducerLoop(seekOffset, gen, token))
                {
                    IsBackground = true,
                    Name = "ape-ring-producer",
                };
                _producerThread = thread;
            }

            thread.Start();
        }

        private void StopProducer()
        {
            Thread? thread;
            CancellationTokenSource? cts;
            Process? proc;

            lock (_ringLock)
            {
                _producerGen++;
                _producerDone = true;
                Monitor.PulseAll(_ringLock);
            }

            lock (_procSync)
            {
                cts = _producerCts;
                proc = _producerProc;
                thread = _producerThread;

                _producerCts = null;
                _producerProc = null;
                _producerThread = null;
            }

            try { cts?.Cancel(); } catch { }
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }

            try { thread?.Join(2000); } catch { }
            try { cts?.Dispose(); } catch { }
        }

        private void ResetRing()
        {
            lock (_ringLock)
            {
                _writePos = 0;
                _readPos = 0;
                _available = 0;
                _producerDone = false;
                Monitor.PulseAll(_ringLock);
            }
        }

        private void ProducerLoop(TimeSpan seekOffset, int myGen, CancellationToken token)
        {
            Process? proc = null;
            NamedPipeServerStream? namedPipe = null;
            Thread? pipeFeeder = null;
            DataReceivedEventHandler? errHandler = null;

            try
            {
                if (seekOffset < TimeSpan.Zero) seekOffset = TimeSpan.Zero;
                string seekStr = seekOffset.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

                string inputFmt = _inputHint switch
                {
                    FfmpegInputHint.Ape => "-f ape ",
                    FfmpegInputHint.Wavpack => "-f wavpack ",
                    _ => "",
                };

                bool pathHasNonAscii = FfmpegService.PathHasNonAscii(_inputPath);
                bool formatNeedsSeekableInput =
                    _inputHint == FfmpegInputHint.Ape || _inputHint == FfmpegInputHint.Wavpack;
                bool useNamedPipe = pathHasNonAscii && !formatNeedsSeekableInput;
                string ffmpegInput = _inputPath;

                // При pipe пропуск начальных байтов выполняет FileStream.Seek (без дубля в ffmpeg)
                string skipArg = (!useNamedPipe && _skipInitialBytes > 0)
                    ? $"-skip_initial_bytes {_skipInitialBytes} "
                    : "";
                string seekArg = seekOffset > TimeSpan.Zero ? $"-ss {seekStr} " : "";

                if (useNamedPipe)
                {
                    string pipeName = $"aurora_{Guid.NewGuid():N}";
                    ffmpegInput = $@"\\.\pipe\{pipeName}";
                    namedPipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.Out,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 65536);

                    string feedPath = _inputPath;
                    int skipBytes = _skipInitialBytes;
                    var feedPipe = namedPipe;
                    pipeFeeder = new Thread(() =>
                    {
                        try
                        {
                            feedPipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                            using var fs = new FileStream(
                                feedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                65536, useAsync: true);
                            if (skipBytes > 0 && skipBytes < fs.Length)
                                fs.Seek(skipBytes, SeekOrigin.Begin);
                            fs.CopyToAsync(feedPipe, 65536, token).GetAwaiter().GetResult();
                        }
                        catch { }
                        finally { try { feedPipe.Dispose(); } catch { } }
                    })
                    { IsBackground = true, Name = "ape-ring-pipe-feeder" };
                    pipeFeeder.Start();
                }

                string inputPart = useNamedPipe
                    ? $"-i \"{ffmpegInput}\" {seekArg}"
                    : $"{inputFmt}{skipArg}{seekArg}-i \"{ffmpegInput}\" ";

                string args = $"-loglevel error -nostats -hide_banner -nostdin " +
                              inputPart +
                              $"-vn -sn -dn -sample_fmt flt -f f32le " +
                              $"-ar {MainWindow.FfmpegOutputSampleRate} " +
                              $"-ac {MainWindow.FfmpegOutputChannels} pipe:1";

                proc = new Process
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
                errHandler = (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (_errLock)
                    {
                        if (_stderr.Length < 4000)
                            _stderr.AppendLine(e.Data);
                    }
                };
                proc.ErrorDataReceived += errHandler;

                lock (_procSync)
                {
                    if (token.IsCancellationRequested || _disposed)
                        return;

                    proc.Start();
                    _producerProc = proc;
                }

                proc.BeginErrorReadLine();

                var stdout = proc.StandardOutput.BaseStream;
                var chunk = new byte[65536];

                while (!token.IsCancellationRequested && !_disposed)
                {
                    int got;
                    try { got = stdout.Read(chunk, 0, chunk.Length); }
                    catch { break; }
                    if (got == 0) break; // EOF

                    int written = 0;
                    while (written < got && !token.IsCancellationRequested && !_disposed)
                    {
                        lock (_ringLock)
                        {
                            if (_producerGen != myGen || token.IsCancellationRequested || _disposed)
                                break;

                            int free = RingCapacity - _available;
                            if (free > 0)
                            {
                                int toWrite = Math.Min(got - written, free);
                                int part1 = Math.Min(toWrite, RingCapacity - _writePos);
                                Buffer.BlockCopy(chunk, written, _ring, _writePos, part1);
                                if (toWrite > part1)
                                    Buffer.BlockCopy(chunk, written + part1, _ring, 0, toWrite - part1);

                                _writePos = (_writePos + toWrite) % RingCapacity;
                                _available += toWrite;
                                written += toWrite;

                                Monitor.PulseAll(_ringLock);
                                continue;
                            }

                            Monitor.Wait(_ringLock, 100);
                        }
                    }
                }
            }
            catch { /* штатное завершение при Dispose/seek */ }
            finally
            {
                lock (_procSync)
                {
                    if (ReferenceEquals(_producerProc, proc))
                        _producerProc = null;
                }

                try { namedPipe?.Dispose(); } catch { }

                if (proc != null)
                {
                    if (errHandler != null)
                        try { proc.ErrorDataReceived -= errHandler; } catch { }
                    try { proc.CancelErrorRead(); } catch { }
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }

                try { pipeFeeder?.Join(300); } catch { }

                lock (_ringLock)
                {
                    if (_producerGen == myGen)
                    {
                        _producerDone = true;
                        Monitor.PulseAll(_ringLock);
                    }
                }

                FfmpegService.AppendDecodeLog(
                    $"RING producer done pos={Interlocked.Read(ref _position)} cancel={token.IsCancellationRequested} stderr='{LastError}' file='{_inputPath}'");
            }
        }

        // ─── Dispose ──────────────────────────────────────────────────────────────
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

            StopProducer();
        }
    }
}

#nullable restore