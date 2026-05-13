// ApeRingBufferStream.cs — потоковый декодер APE/WV через ring buffer в памяти.
// Заменяет WAV-fallback (TryDecodeEntireFileToWavUsingFfmpeg).
// Нет записи на диск. Нет temp-файлов. SSD не изнашивается.
//
// Архитектура:
//   Producer-поток  → ffmpeg stdout → ring buffer (≈12 МБ, ~30 сек стерео 48 кГц)
//   Consumer (NAudio audio thread) ← ring buffer
//   Если буфер полон — producer спит (SemaphoreSlim).
//   Если буфер пуст — consumer спит (SemaphoreSlim).
//
// Seek: перезапускает ffmpeg с -ss, точно как FfmpegDecodeStream.RestartAtBytes.
// Кириллические пути: именованный Windows pipe, точно как FfmpegDecodeStream.
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
    internal sealed class ApeRingBufferStream : WaveStream
    {
        // ─── Ring buffer ──────────────────────────────────────────────────────────
        // ~12 МБ = ~30 сек стерео float 48 кГц. Достаточно чтобы скрыть latency
        // декодера APE и дать аудио-потоку комфортный запас.
        private const int RingCapacity = 12 * 1024 * 1024;

        private readonly byte[]        _ring = new byte[RingCapacity];
        private int  _writePos;
        private int  _readPos;
        private int  _available;          // байт готово для чтения
        private bool _producerDone;       // producer завершил запись (EOF или ошибка)
        private readonly object _ringLock = new();

        private readonly SemaphoreSlim _dataReady  = new(0, int.MaxValue); // producer → consumer
        private readonly SemaphoreSlim _spaceReady = new(0, int.MaxValue); // consumer → producer

        // ─── State ────────────────────────────────────────────────────────────────
        private readonly WaveFormat _fmt;
        private long  _position;          // байт прочитано потребителем
        private bool  _disposed;

        private Thread?  _producerThread;
        private volatile bool _producerCancel; // просим producer остановиться
        private Process? _producerProc;        // ссылка для Kill из StopProducer

        // Текущие параметры запуска (нужны для seek — перезапуска)
        private readonly string _ffmpegPath;
        private readonly string _inputPath;
        private readonly FfmpegInputHint _inputHint;
        private readonly int  _skipInitialBytes;

        // Последняя ошибка ffmpeg stderr
        private readonly object        _errLock = new();
        private readonly StringBuilder _stderr  = new();
        public string LastError { get { lock (_errLock) return _stderr.ToString().Trim(); } }

        // ─── WaveStream overrides ─────────────────────────────────────────────────
        public override WaveFormat WaveFormat => _fmt;

        // Length неизвестна заранее (декодируем на лету).
        // Возвращаем long.MaxValue а не -1, иначе Math.Clamp(bytes, 0, Length) при seek
        // схлопывается в -1 и сбрасывает позицию в начало файла (→ звук пропадает).
        public override long Length => long.MaxValue;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0) value = 0;
                RestartAtBytes(value);
            }
        }

        public TimeSpan CurrentPosition =>
            TimeSpan.FromSeconds((double)_position / _fmt.AverageBytesPerSecond);

        // ─── Конструктор ──────────────────────────────────────────────────────────
        public ApeRingBufferStream(
            string ffmpegPath,
            string inputPath,
            FfmpegInputHint inputHint    = FfmpegInputHint.Ape,
            int skipInitialBytes         = 0,
            TimeSpan seekTo              = default)
        {
            _fmt = WaveFormat.CreateIeeeFloatWaveFormat(
                       MainWindow.FfmpegOutputSampleRate,
                       MainWindow.FfmpegOutputChannels);

            _ffmpegPath       = ffmpegPath;
            _inputPath        = inputPath;
            _inputHint        = inputHint;
            _skipInitialBytes = Math.Max(0, skipInitialBytes);

            _position     = (long)(_fmt.AverageBytesPerSecond * seekTo.TotalSeconds);
            StartProducer(seekTo);
        }

        // ─── Read (audio thread) ──────────────────────────────────────────────────
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) return 0;

            int totalRead = 0;

            while (totalRead < count && !_disposed)
            {
                int got;
                lock (_ringLock)
                {
                    got = Math.Min(count - totalRead, _available);
                    if (got > 0)
                    {
                        // Читаем из кольца с wrap-around
                        int part1 = Math.Min(got, RingCapacity - _readPos);
                        Buffer.BlockCopy(_ring, _readPos, buffer, offset + totalRead, part1);
                        if (got > part1)
                            Buffer.BlockCopy(_ring, 0, buffer, offset + totalRead + part1, got - part1);
                        _readPos    = (_readPos + got) % RingCapacity;
                        _available -= got;
                        totalRead  += got;
                    }
                }

                if (got > 0)
                {
                    // Разбудить producer — появилось свободное место
                    _spaceReady.Release();
                    continue;
                }

                // Буфер пуст — проверяем завершение producer
                bool done;
                lock (_ringLock) done = _producerDone && _available == 0;
                if (done)
                {
                    // Producer завершился и данных больше нет — настоящий EOF.
                    // Возвращаем только если уже есть хоть что-то, иначе ждём ещё
                    // (защита от race: _producerDone выставляется до Release(16))
                    if (totalRead > 0)
                    {
                        FfmpegService.AppendDecodeLog(
                            $"RING EOF partial totalRead={totalRead} count={count} pos={_position} file='{_inputPath}'");
                        break;
                    }
                    // Ждём финальный Release(16) из finally producer-а
                    _dataReady.Wait(20);
                    lock (_ringLock) done = _producerDone && _available == 0;
                    if (done)
                    {
                        FfmpegService.AppendDecodeLog(
                            $"RING EOF zero totalRead=0 count={count} pos={_position} file='{_inputPath}'");
                        break;
                    }
                    continue;
                }

                // Данных пока нет — ждём сигнала от producer (максимум 100 мс)
                _dataReady.Wait(100);
            }

            _position += totalRead;
            return totalRead;
        }

        // ─── Seek ─────────────────────────────────────────────────────────────────
        private void RestartAtBytes(long absoluteBytes)
        {
            StopProducer();
            ResetRing();
            _position = absoluteBytes;
            StartProducer(TimeSpan.FromSeconds(absoluteBytes / (double)_fmt.AverageBytesPerSecond));
        }

        // ─── Producer ─────────────────────────────────────────────────────────────
        private void StartProducer(TimeSpan seekOffset)
        {
            _producerCancel = false;
            lock (_errLock) _stderr.Clear();

            _producerThread = new Thread(() => ProducerLoop(seekOffset))
            {
                IsBackground = true,
                Name         = "ape-ring-producer",
            };
            _producerThread.Start();
        }

        private void StopProducer()
        {
            _producerCancel = true;
            // Убиваем ffmpeg-процесс сразу — иначе stdout.Read блокирует поток
            // и Join(800) истекает раньше чем APE-декодер завершается.
            // Без этого старый producer продолжает писать в кольцо после ResetRing.
            try { _producerProc?.Kill(); } catch { }
            // Разбудить producer если он ждёт места в кольце
            _spaceReady.Release(16);
            _producerThread?.Join(2000);
            _producerThread = null;
            _producerProc   = null;
            lock (_ringLock) _producerDone = false;
        }

        private void ResetRing()
        {
            lock (_ringLock)
            {
                _writePos  = 0;
                _readPos   = 0;
                _available = 0;
                _producerDone = false;
            }
            // Сбросить семафоры
            while (_dataReady.CurrentCount  > 0) _dataReady.Wait(0);
            while (_spaceReady.CurrentCount > 0) _spaceReady.Wait(0);
        }

        private void ProducerLoop(TimeSpan seekOffset)
        {
            Process? proc         = null;
            NamedPipeServerStream? namedPipe = null;
            Thread?  pipeFeeder   = null;

            try
            {
                // ── Аргументы ffmpeg ───────────────────────────────────────────
                string inputFmt = _inputHint switch
                {
                    FfmpegInputHint.Ape     => "-f ape ",
                    FfmpegInputHint.Wavpack => "-f wavpack ",
                    _                       => "",
                };
                string skipArg = _skipInitialBytes > 0
                    ? $"-skip_initial_bytes {_skipInitialBytes} " : "";
                string seekArg = seekOffset > TimeSpan.Zero
                    ? $"-ss {seekOffset.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " : "";

                // ── Путь к файлу: именованный pipe для кириллицы ───────────────
                // Named pipe is a linear non-seekable stream.
                // APE/WV demuxers can seek backward during decode; over pipe this
                // causes "Invalid data found when processing input" on long tracks.
                // So for APE/WV always keep direct seekable file input.
                bool pathHasNonAscii = FfmpegService.PathHasNonAscii(_inputPath);
                bool formatNeedsSeekableInput =
                    _inputHint == FfmpegInputHint.Ape || _inputHint == FfmpegInputHint.Wavpack;
                bool useNamedPipe = pathHasNonAscii && !formatNeedsSeekableInput;
                string ffmpegInput = _inputPath;

                if (useNamedPipe)
                {
                    string pipeName = $"aurora_{Guid.NewGuid():N}";
                    ffmpegInput     = $@"\\.\pipe\{pipeName}";
                    namedPipe       = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.Out,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize:  0,
                        outBufferSize: 65536);

                    string   feedPath = _inputPath;
                    int      skipBytes = _skipInitialBytes;
                    var      feedPipe  = namedPipe;
                    pipeFeeder = new Thread(() =>
                    {
                        try
                        {
                            feedPipe.WaitForConnection();
                            using var fs = new FileStream(feedPath, FileMode.Open,
                                FileAccess.Read, FileShare.Read, 65536);
                            if (skipBytes > 0) fs.Seek(skipBytes, SeekOrigin.Begin);
                            fs.CopyTo(feedPipe, 65536);
                        }
                        catch { }
                        finally { try { feedPipe.Dispose(); } catch { } }
                    })
                    { IsBackground = true, Name = "ape-ring-pipe-feeder" };
                    pipeFeeder.Start();
                }

                string args = $"-loglevel error -nostats -hide_banner -nostdin " +
                              $"{inputFmt}{skipArg}{seekArg}" +
                              $"-i \"{ffmpegInput}\" " +
                              $"-vn -sn -dn -sample_fmt flt -f f32le " +
                              $"-ar {MainWindow.FfmpegOutputSampleRate} " +
                              $"-ac {MainWindow.FfmpegOutputChannels} pipe:1";

                proc = new Process
                {
                    StartInfo = new ProcessStartInfo(_ffmpegPath, args)
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true,
                    }
                };
                lock (_errLock) _stderr.Clear();
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (_errLock) { if (_stderr.Length < 4000) _stderr.AppendLine(e.Data); }
                };
                proc.Start();
                _producerProc = proc;
                proc.BeginErrorReadLine();

                // ── Читаем stdout и пишем в ring ───────────────────────────────
                var   stdout = proc.StandardOutput.BaseStream;
                var   chunk  = new byte[65536];

                while (!_producerCancel)
                {
                    int got;
                    try { got = stdout.Read(chunk, 0, chunk.Length); }
                    catch { break; }
                    if (got == 0) break; // EOF

                    int written = 0;
                    while (written < got && !_producerCancel)
                    {
                        int toWrite;
                        lock (_ringLock)
                        {
                            int free  = RingCapacity - _available;
                            toWrite   = Math.Min(got - written, free);
                            if (toWrite > 0)
                            {
                                int part1 = Math.Min(toWrite, RingCapacity - _writePos);
                                Buffer.BlockCopy(chunk, written, _ring, _writePos, part1);
                                if (toWrite > part1)
                                    Buffer.BlockCopy(chunk, written + part1, _ring, 0, toWrite - part1);
                                _writePos   = (_writePos + toWrite) % RingCapacity;
                                _available += toWrite;
                                written    += toWrite;
                            }
                        }

                        if (toWrite > 0)
                        {
                            _dataReady.Release();  // разбудить consumer
                        }
                        else
                        {
                            // Кольцо полное — ждём пока consumer освободит место
                            _spaceReady.Wait(100);
                        }
                    }
                }
            }
            catch { /* штатное завершение при Dispose/seek */ }
            finally
            {
                try { proc?.Kill(); }    catch { }
                try { proc?.Dispose(); } catch { }
                _producerProc = null;
                pipeFeeder?.Join(300);

                lock (_ringLock) _producerDone = true;
                FfmpegService.AppendDecodeLog(
                    $"RING producer done pos={_position} cancel={_producerCancel} stderr='{LastError}' file='{_inputPath}'");
                // Разбудить consumer — данных больше не будет
                _dataReady.Release(16);
            }
        }

        // ─── Dispose ──────────────────────────────────────────────────────────────
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            base.Dispose(disposing);
            if (!disposing) return;
            StopProducer();
        }
    }
}
