// MainWindow.FfmpegStream.cs — потоковый декодер через ffmpeg pipe
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    internal enum FfmpegInputHint { Auto, Ape, Wavpack }

    // ─── FfmpegDecodeStream ───────────────────────────────────────────────────────
    // WaveStream читающий float-сэмплы из stdout ffmpeg-процесса.
    // Поддерживает APE, WV и любой формат понятный ffmpeg.
    internal sealed class FfmpegDecodeStream : WaveStream
    {
        private Process?   _proc;
        private Stream?    _stdout;
        private Thread?    _stdinThread;  // поток записи файла в stdin ffmpeg (для не-ASCII путей)
        private readonly WaveFormat       _fmt;
        private long       _bytesRead;
        private long       _startBytes;
        private readonly string           _ffmpegPath;
        private readonly string           _inputPath;
        private readonly FfmpegInputHint  _inputHint;
        private readonly int              _skipInitialBytes;
        private readonly string?          _deletePathOnDispose;
        private string?                   _extraDeletePath;
        private readonly object           _errLock = new();
        private readonly StringBuilder    _stderr  = new();
        private byte[] _prefetched       = Array.Empty<byte>();
        private int    _prefetchedOffset;
        private int    _prefetchedCount;

        public override WaveFormat WaveFormat => _fmt;
        public override long Length   => -1;
        public override long Position
        {
            get => _startBytes + _bytesRead;
            set { if (value < 0) value = 0; RestartAtBytes(value); }
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
            // 48000 Hz — совпадает с AudioOutputEngine(sampleRate:48000).
            // Двойной ресэмпл (APE→44100 в ffmpeg, потом 44100→48000) снижает качество.
            _fmt              = WaveFormat.CreateIeeeFloatWaveFormat(
                                    MainWindow.FfmpegOutputSampleRate,
                                    MainWindow.FfmpegOutputChannels);
            _ffmpegPath          = ffmpeg;
            _inputPath           = input;
            _inputHint           = inputHint;
            _skipInitialBytes    = Math.Max(0, skipInitialBytes);
            _deletePathOnDispose = deletePathOnDispose;
            StartProcess(seekTo);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;

            if (_prefetchedCount > 0)
            {
                int take = Math.Min(_prefetchedCount, count);
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, buffer, offset, take);
                _prefetchedOffset += take;
                _prefetchedCount  -= take;
                totalRead         += take;
                if (_prefetchedCount == 0) { _prefetched = Array.Empty<byte>(); _prefetchedOffset = 0; }
            }

            while (totalRead < count)
            {
                int got = _stdout!.Read(buffer, offset + totalRead, count - totalRead);
                if (got == 0) break;
                totalRead += got;
            }
            _bytesRead += totalRead;
            return totalRead;
        }

        public void PushBack(byte[] data, int count)
        {
            if (count <= 0) return;
            var copy = new byte[count + _prefetchedCount];
            Buffer.BlockCopy(data, 0, copy, 0, count);
            if (_prefetchedCount > 0)
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, copy, count, _prefetchedCount);
            _prefetched       = copy;
            _prefetchedOffset = 0;
            _prefetchedCount  = copy.Length;
        }

        public void RegisterDeleteOnDispose(string path) => _extraDeletePath = path;

        private void StartProcess(TimeSpan offset)
        {
            _startBytes = (long)(_fmt.AverageBytesPerSecond * offset.TotalSeconds);
            string seconds  = offset.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string inputFmt = _inputHint switch
            {
                FfmpegInputHint.Ape     => "-f ape ",
                FfmpegInputHint.Wavpack => "-f wavpack ",
                _                       => ""
            };
            string skipArg = _skipInitialBytes > 0 ? $"-skip_initial_bytes {_skipInitialBytes} " : "";
            string seekArg = offset > TimeSpan.Zero ? $"-ss {seconds} " : "";

            // Named pipe gives a non-seekable linear input.
            // APE/WV may seek backward while decoding; over a pipe this can
            // lead to mid-track decode errors ("Invalid data...").
            // For APE/WV keep direct file input even for non-ASCII paths.
            bool pathHasNonAscii = FfmpegService.PathHasNonAscii(_inputPath);
            bool formatNeedsSeekableInput =
                _inputHint == FfmpegInputHint.Ape || _inputHint == FfmpegInputHint.Wavpack;
            bool useNamedPipe = pathHasNonAscii && !formatNeedsSeekableInput;

            string ffmpegInputPath;
            NamedPipeServerStream? namedPipe = null;

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
            }
            else
            {
                ffmpegInputPath = _inputPath;
            }

            string inputArg = $"{inputFmt}{skipArg}-i \"{ffmpegInputPath}\" {seekArg}";

            string args = $"-loglevel error -nostats -hide_banner -nostdin " +
                          inputArg +
                          $"-vn -sn -dn -sample_fmt flt -f f32le " +
                          $"-ar {MainWindow.FfmpegOutputSampleRate} -ac {MainWindow.FfmpegOutputChannels} pipe:1";

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(_ffmpegPath, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                },
                EnableRaisingEvents = true,
            };
            lock (_errLock) _stderr.Clear();
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_errLock) { if (_stderr.Length < 4000) _stderr.AppendLine(e.Data); }
            };
            _proc.Start();
            _proc.BeginErrorReadLine();
            _stdout           = _proc.StandardOutput.BaseStream;
            _bytesRead        = 0;
            _prefetched       = Array.Empty<byte>();
            _prefetchedOffset = 0;
            _prefetchedCount  = 0;

            // Фоновый поток: ждём подключения ffmpeg к pipe, затем пишем файл
            if (namedPipe != null)
            {
                string pathToFeed   = _inputPath;
                long   skipBytes    = _skipInitialBytes > 0 ? _skipInitialBytes : 0;
                var    pipeToFeed   = namedPipe;
                _stdinThread = new Thread(() =>
                {
                    try
                    {
                        pipeToFeed.WaitForConnection();
                        using var fs = new FileStream(pathToFeed, FileMode.Open,
                            FileAccess.Read, FileShare.Read, 65536);
                        if (skipBytes > 0) fs.Seek(skipBytes, SeekOrigin.Begin);
                        fs.CopyTo(pipeToFeed, 65536);
                    }
                    catch { }
                    finally
                    {
                        try { pipeToFeed.Dispose(); } catch { }
                    }
                })
                { IsBackground = true, Name = "ffmpeg-pipe-feeder" };
                _stdinThread.Start();
            }
        }

        private void KillProcess()
        {
            try { _stdout?.Dispose(); }  catch { }
            try { _proc?.Kill(); }       catch { }
            try { _proc?.Dispose(); }    catch { }
            // stdin-feeder завершится сам когда stdin закроется при Kill процесса,
            // но на всякий случай ждём не дольше 200мс
            try { _stdinThread?.Join(200); } catch { }
            _stdinThread = null;
        }

        private void RestartAtBytes(long absoluteBytes)
        {
            KillProcess();
            StartProcess(TimeSpan.FromSeconds(absoluteBytes / (double)_fmt.AverageBytesPerSecond));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            KillProcess();
            if (_deletePathOnDispose != null) try { File.Delete(_deletePathOnDispose); } catch { }
            if (_extraDeletePath     != null) try { File.Delete(_extraDeletePath); }     catch { }
        }
    }

    // ─── FfmpegCueSegment ────────────────────────────────────────────────────────
    // Аналог CueSegmentProvider но для потоков ffmpeg (APE/WV).
    // Работает с любым WaveStream: FfmpegDecodeStream и ApeRingBufferStream.
    // Физически читает и пропускает сэмплы до start — seek в ffmpeg не работает надёжно.
    internal sealed class FfmpegCueSegment : ISampleProvider
    {
        private readonly WaveStream          _stream;
        private readonly FfmpegFloatProvider _provider;
        private readonly long   _startSample;
        private readonly long   _endSample;
        private long            _position;
        private volatile bool   _skipDone;
        private volatile bool   _skipRunning;
        private readonly double _seekOffsetSeconds;

        public WaveFormat WaveFormat => _provider.WaveFormat;

        public double PositionSeconds =>
            _seekOffsetSeconds + (double)_position / WaveFormat.Channels / WaveFormat.SampleRate;

        // Принимает любой WaveStream (FfmpegDecodeStream или ApeRingBufferStream).
        public FfmpegCueSegment(WaveStream stream, TimeSpan start, TimeSpan end,
                                double seekOffsetSeconds = 0.0)
        {
            _stream            = stream;
            _provider          = new FfmpegFloatProvider(stream);
            _seekOffsetSeconds = seekOffsetSeconds;
            int ch             = _provider.WaveFormat.Channels;
            int sr             = _provider.WaveFormat.SampleRate;
            _startSample = (long)(start.TotalSeconds * sr) * ch;
            _endSample   = end > start ? (long)(end.TotalSeconds * sr) * ch : 0;
        }

        /// <summary>Skip уже выполнен внешним средством (например -ss в ffmpeg).</summary>
        public void MarkSkipDone() { _skipDone = true; _skipRunning = true; }

        /// <summary>Запускает skip в фоне, чтобы не блокировать аудио-поток NAudio.</summary>
        public void StartSkipAsync()
        {
            if (_startSample == 0) { _skipDone = true; return; }
            if (_skipRunning || _skipDone) return;
            _skipRunning = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long toSkip = _startSample;
                var  tmp    = new float[8192];
                while (toSkip > 0)
                {
                    int got = _provider.Read(tmp, 0, (int)Math.Min(toSkip, tmp.Length));
                    if (got == 0) break;
                    toSkip -= got;
                }
                _skipDone = true;
            });
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Пока skip не завершён — возвращаем тишину, не блокируем аудио-поток
            if (!_skipDone) { Array.Clear(buffer, offset, count); return count; }

            int toRead = count;
            if (_endSample > 0)
            {
                long remaining = (_endSample - _startSample) - _position;
                if (remaining <= 0) return 0;
                toRead = (int)Math.Min(count, remaining);
            }
            int read = _provider.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }
    }
}
