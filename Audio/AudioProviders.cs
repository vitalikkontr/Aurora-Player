using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Numerics;
using System.Buffers;
using BiQuadFilter = NAudio.Dsp.BiQuadFilter;

namespace AuroraPlayer
{
    // ─── Surround ────────────────────────────────────────────────────────────────
    // _sync защищает _source и _delayBuffer от гонки между аудио-потоком (Read)
    // и UI-потоком (ReplaceSource/FlushBuffer).
    public class SurroundProvider : ISampleProvider
    {
        private readonly object _sync       = new();
        private ISampleProvider _source;
        private float[]         _delayBuffer;
        private int             _delayPos;  // доступ только внутри _sync или аудио-потока

        public WaveFormat WaveFormat { get; private set; }
        public float Width   { get; set; } = 0.5f;
        public bool  Enabled { get; set; } = true;

        public SurroundProvider(ISampleProvider source)
        {
            _source      = source;
            WaveFormat   = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
            int delSampl = (int)(source.WaveFormat.SampleRate * 0.020); // моно-слоты
            _delayBuffer = new float[Math.Max(delSampl, 2)];
        }

        public void FlushBuffer()
        {
            lock (_sync)
            {
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
                _delayPos = 0;
            }
        }

        public void ReplaceSource(ISampleProvider newSource)
        {
            int newDelay = Math.Max((int)(newSource.WaveFormat.SampleRate * 0.020), 2);
            lock (_sync)
            {
                _source = newSource;
                if (_delayBuffer.Length != newDelay)
                    _delayBuffer = new float[newDelay];
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSource.WaveFormat.SampleRate, 2);
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
                _delayPos = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            ISampleProvider src;
            float[]         delay;
            lock (_sync) { src = _source; delay = _delayBuffer; }

            int channels = src.WaveFormat.Channels;

            if (channels == 1)
            {
                int monoCount = count / 2;
                var mono      = ArrayPool<float>.Shared.Rent(monoCount);
                try
                {
                    int monoRead = src.Read(mono, 0, monoCount);
                    int read     = monoRead * 2;
                    for (int i = 0; i < monoRead; i++)
                    {
                        buffer[offset + i * 2]     = mono[i];
                        buffer[offset + i * 2 + 1] = mono[i];
                    }
                    if (!Enabled || Width < 0.01f) return read;
                    ApplySurround(buffer, offset, read, delay);
                    return read;
                }
                finally { ArrayPool<float>.Shared.Return(mono); }
            }
            else if (channels == 2)
            {
                int read = src.Read(buffer, offset, count);
                if (!Enabled || Width < 0.01f) return read;
                ApplySurround(buffer, offset, read, delay);
                return read;
            }
            else
            {
                // Многоканал (5.1/7.1) → Стерео через ITU-R BS.775 даунмикс
                int inSamplesReq = (count / 2) * channels;
                var tmp = ArrayPool<float>.Shared.Rent(inSamplesReq);
                try
                {
                    int inRead     = src.Read(tmp, 0, inSamplesReq);
                    int frames     = inRead / channels;
                    int outSamples = frames * 2;
                    int cols       = Math.Min(channels, 8);
                    for (int i = 0; i < frames; i++)
                    {
                        float L = 0f, R = 0f;
                        for (int ch = 0; ch < cols; ch++)
                        {
                            float s = tmp[i * channels + ch];
                            // LFE (канал 3 в 5.1/7.1) — отбрасываем (стандарт для headphone downmix)
                            if (channels >= 6 && ch == 3) continue;
                            L += s * DownmixTables.L[ch];
                            R += s * DownmixTables.R[ch];
                        }
                        buffer[offset + i * 2]     = Math.Clamp(L, -1f, 1f);
                        buffer[offset + i * 2 + 1] = Math.Clamp(R, -1f, 1f);
                    }
                    if (!Enabled || Width < 0.01f) return outSamples;
                    ApplySurround(buffer, offset, outSamples, delay);
                    return outSamples;
                }
                finally { ArrayPool<float>.Shared.Return(tmp); }
            }
        }

        private void ApplySurround(float[] buffer, int offset, int read, float[] delay)
        {
            int dlen = delay.Length;
            if (dlen == 0) return;

            int pos;
            lock (_sync) { pos = _delayPos; }

            for (int i = 0; i < read; i += 2)
            {
                float L    = buffer[offset + i];
                float R    = buffer[offset + i + 1];
                float mid  = (L + R) * 0.5f;
                float side = (L - R) * 0.5f * (1f + Width * 2f);
                float wL   = mid + side;
                float wR   = mid - side;

                float delayed = delay[pos];
                delay[pos]    = wR;
                pos           = (pos + 1) % dlen;  // ИСПРАВЛЕНО: был +2, delay-буфер моно

                buffer[offset + i]     = Math.Clamp(wL, -1f, 1f);
                buffer[offset + i + 1] = Math.Clamp(delayed * (1f - Width * 0.3f) + wR * Width * 0.3f, -1f, 1f);
            }

            lock (_sync) { _delayPos = pos; }
        }
    }

    // ─── Volume ──────────────────────────────────────────────────────────────────
    public class VolumeProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public WaveFormat WaveFormat => _source.WaveFormat;
        public float Volume { get; set; } = 1.0f;

        public VolumeProvider(ISampleProvider src) => _source = src;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            for (int i = 0; i < read; i++) buffer[offset + i] *= Volume;
            return read;
        }
    }

    // ─── 5-Band Equalizer (BiQuad PeakingEQ) ─────────────────────────────────────
    public class EqualizerProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly object          _filterSync = new();
        private readonly BiQuadFilter[,] _filters;
        private readonly float[]         _gains;
        private readonly int             _channels;
        private static readonly float[]  Freqs = { 60f, 250f, 1000f, 4000f, 12000f };

        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerProvider(ISampleProvider source)
        {
            _source   = source;
            _channels = source.WaveFormat.Channels;
            _gains    = new float[5];
            _filters  = new BiQuadFilter[5, _channels];
            RebuildFilters();
        }

        public void SetGain(int band, float dB)
        {
            if (band < 0 || band >= 5) return;
            lock (_filterSync)
            {
                _gains[band] = dB;
                for (int ch = 0; ch < _channels; ch++)
                    _filters[band, ch] = BiQuadFilter.PeakingEQ(
                        _source.WaveFormat.SampleRate, Freqs[band], 1.0f, dB);
            }
        }

        public float GetGain(int band) => band >= 0 && band < 5 ? _gains[band] : 0f;

        private void RebuildFilters()
        {
            lock (_filterSync)
            {
                for (int b = 0; b < 5; b++)
                    for (int ch = 0; ch < _channels; ch++)
                        _filters[b, ch] = BiQuadFilter.PeakingEQ(
                            _source.WaveFormat.SampleRate, Freqs[b], 1.0f, _gains[b]);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            lock (_filterSync)
            {
                for (int i = 0; i < read; i++)
                {
                    int   ch = i % _channels;
                    float s  = buffer[offset + i];
                    for (int b = 0; b < 5; b++)
                        s = _filters[b, ch].Transform(s);
                    buffer[offset + i] = Math.Clamp(s, -1f, 1f);
                }
            }
            return read;
        }
    }

    // ─── Soft Compressor ─────────────────────────────────────────────────────────
    public class CompressorProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _envelope = 0f;
        private const float Threshold = 0.7f;
        private const float Ratio     = 4.0f;
        private const float Attack    = 0.003f;
        private const float Release   = 0.15f;

        public WaveFormat WaveFormat => _source.WaveFormat;
        public bool Enabled { get; set; } = true;

        public CompressorProvider(ISampleProvider src) => _source = src;

        /// <summary>Сбрасывает огибающую компрессора — вызывать при seek чтобы не давить тихий сигнал.</summary>
        public void Reset() => _envelope = 0f;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!Enabled) return read;
            float sr          = WaveFormat.SampleRate;
            float attackCoef  = MathF.Exp(-1f / (sr * Attack));
            float releaseCoef = MathF.Exp(-1f / (sr * Release));
            for (int i = 0; i < read; i++)
            {
                float abs = MathF.Abs(buffer[offset + i]);
                _envelope = abs > _envelope
                    ? attackCoef  * _envelope + (1f - attackCoef)  * abs
                    : releaseCoef * _envelope + (1f - releaseCoef) * abs;
                float gain = 1f;
                if (_envelope > Threshold)
                    gain = Threshold + (_envelope - Threshold) / Ratio;
                gain = Math.Min(gain / Math.Max(_envelope, 1e-6f), 1f);
                buffer[offset + i] = Math.Clamp(buffer[offset + i] * gain, -1f, 1f);
            }
            return read;
        }
    }

    // ─── FFT Aggregator ───────────────────────────────────────────────────────────
    public class FftAggregator : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[]         _fftBuffer;
        private readonly Complex[]       _fftComplex;
        private readonly object          _fftLock = new();
        private int                      _fftPos;
        private int                      _readCount;
        // Для точного попадания в ритм держим FFT на каждом буфере.
        private const int FftSize        = 1024;
        private const int FftSkipBuffers = 0;

        public WaveFormat WaveFormat => _source.WaveFormat;
        public float[]    FftData    { get; } = new float[FftSize / 2];
        private volatile bool _hasData;
        public bool       HasData    => _hasData;

        public FftAggregator(ISampleProvider source)
        {
            _source     = source;
            _fftBuffer  = new float[FftSize];
            _fftComplex = new Complex[FftSize];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            _readCount++;
            // Пропускаем часть буферов — FFT дорогой, но данные не критичны каждый раз
            if (_readCount % (FftSkipBuffers + 1) != 0)
                return read;

            for (int i = 0; i < read; i += WaveFormat.Channels)
            {
                float sample = 0;
                for (int ch = 0; ch < WaveFormat.Channels; ch++)
                    sample += buffer[offset + i + ch];
                sample /= WaveFormat.Channels;

                float window = 0.5f * (1 - MathF.Cos(2 * MathF.PI * _fftPos / (FftSize - 1)));
                _fftBuffer[_fftPos] = sample * window;
                _fftPos++;
                if (_fftPos >= FftSize)
                {
                    _fftPos = 0;
                    for (int j = 0; j < FftSize; j++)
                        _fftComplex[j] = new Complex(_fftBuffer[j], 0);
                    Fft(_fftComplex);
                    lock (_fftLock)
                    {
                        for (int j = 0; j < FftSize / 2; j++)
                            FftData[j] = (float)_fftComplex[j].Magnitude / FftSize;
                        _hasData = true;
                    }
                }
            }
            return read;
        }

        public bool TryCopyFftData(float[] destination)
        {
            if (destination == null || destination.Length == 0) return false;
            lock (_fftLock)
            {
                if (!_hasData) return false;
                int len = Math.Min(destination.Length, FftData.Length);
                Array.Copy(FftData, destination, len);
                if (destination.Length > len)
                    Array.Clear(destination, len, destination.Length - len);
                return true;
            }
        }

        private static void Fft(Complex[] x)
        {
            int n = x.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (x[i], x[j]) = (x[j], x[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang  = -2 * Math.PI / len;
                var    wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = x[i + j]; var v = x[i + j + len / 2] * w;
                        x[i + j] = u + v; x[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
        }
    }

    // ─── FfmpegFloatProvider ──────────────────────────────────────────────────────
    // Читает float-сэмплы напрямую из потока формата f32le (IEEE float little-endian).
    // WaveToSampleProvider не подходит для IeeeFloat-источников — он делает лишнюю конвертацию.
    public class FfmpegFloatProvider : ISampleProvider
    {
        private readonly WaveStream _source;
        private readonly byte[]     _tailBytes = new byte[3];
        private int                 _tailLength;

        public WaveFormat WaveFormat { get; }

        public FfmpegFloatProvider(WaveStream source)
        {
            _source    = source;
            WaveFormat = source.WaveFormat;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int maxSamples = buffer.Length - offset;
            if (maxSamples <= 0 || count <= 0) return 0;

            int    samplesToRead = Math.Min(count, maxSamples);
            int    bytesToRead   = samplesToRead * 4;
            byte[] tmp           = ArrayPool<byte>.Shared.Rent(bytesToRead);
            int    bytesRead     = 0;

            try
            {
                if (_tailLength > 0)
                {
                    Buffer.BlockCopy(_tailBytes, 0, tmp, 0, _tailLength);
                    bytesRead   = _tailLength;
                    _tailLength = 0;
                }

                while (bytesRead < bytesToRead)
                {
                    int got;
                    try { got = _source.Read(tmp, bytesRead, bytesToRead - bytesRead); }
                    catch (ObjectDisposedException) { break; }
                    if (got == 0) break;
                    bytesRead += got;
                }

                // Если пайп вернул 1-3 байта — пытаемся добрать до целого float
                while (bytesRead > 0 && bytesRead < 4)
                {
                    int got;
                    try { got = _source.Read(tmp, bytesRead, 4 - bytesRead); }
                    catch (ObjectDisposedException) { break; }
                    if (got == 0) break;
                    bytesRead += got;
                }

                int alignedBytes = bytesRead - (bytesRead % 4);
                int remainder    = bytesRead - alignedBytes;
                if (remainder > 0)
                {
                    Buffer.BlockCopy(tmp, alignedBytes, _tailBytes, 0, remainder);
                    _tailLength = remainder;
                }

                if (alignedBytes > 0)
                    Buffer.BlockCopy(tmp, 0, buffer, offset * 4, alignedBytes);

                return alignedBytes / 4;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
    }

    // ─── CUE Segment Providers ───────────────────────────────────────────────────

    /// <summary>
    /// Ограничивает воспроизведение диапазоном [trackStart, trackEnd] для источников,
    /// не совместимых с CueSegmentProvider (например MediaFoundationReader через MF-fallback).
    /// Seek уже выполнен снаружи; здесь только отслеживаем конец сегмента.
    /// </summary>
    public class CueLimitProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveStream      _stream;
        private readonly TimeSpan        _trackEnd;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public CueLimitProvider(ISampleProvider source, WaveStream stream, TimeSpan trackStart, TimeSpan trackEnd)
        {
            _source   = source;
            _stream   = stream;
            _trackEnd = trackEnd;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_trackEnd > TimeSpan.Zero)
            {
                double remaining = (_trackEnd - _stream.CurrentTime).TotalSeconds;
                if (remaining <= 0) return 0;
                int  sr         = WaveFormat.SampleRate, ch = WaveFormat.Channels;
                long maxSamples = (long)(remaining * sr) * ch;
                count = (int)Math.Min(count, Math.Max(0, maxSamples));
                if (count == 0) return 0;
            }
            return _source.Read(buffer, offset, count);
        }
    }

    /// <summary>
    /// Оборачивает AudioFileReader, обрезает поток по [trackStart, trackEnd].
    /// Файл открыт ОДИН РАЗ (_cachedAudioReader).
    /// PositionSeconds считается из _reader.CurrentTime напрямую — не накапливается вручную.
    /// </summary>
    public class CueSegmentProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly TimeSpan        _trackStart;
        private readonly TimeSpan        _trackEnd;

        public WaveFormat WaveFormat => _reader.WaveFormat;

        public double PositionSeconds =>
            Math.Max(0, _reader.CurrentTime.TotalSeconds - _trackStart.TotalSeconds);

        public CueSegmentProvider(AudioFileReader reader, TimeSpan trackStart, TimeSpan trackEnd)
        {
            _reader     = reader;
            _trackStart = trackStart;
            _trackEnd   = trackEnd;
        }

        /// <summary>Seek к абсолютной позиции в файле. Вызывается ДО WaveOut.Init().</summary>
        public void SeekTo(TimeSpan absolutePosition)
        {
            _reader.CurrentTime = absolutePosition;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int toRead = count;
            if (_trackEnd > _trackStart)
            {
                double readerPosSec = _reader.CurrentTime.TotalSeconds;
                double remainingSec = _trackEnd.TotalSeconds - readerPosSec;
                if (remainingSec <= 0) return 0;
                long samp = (long)(remainingSec * _reader.WaveFormat.SampleRate) * _reader.WaveFormat.Channels;
                toRead = (int)Math.Min(count, Math.Max(0, samp));
                if (toRead == 0) return 0;
            }
            return _reader.Read(buffer, offset, toRead);
        }
    }
}
