#nullable enable

using NAudio.Wave;
using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using BiQuadFilter = NAudio.Dsp.BiQuadFilter;

namespace AuroraPlayer
{
    // ─── Surround ────────────────────────────────────────────────────────────────
    public class SurroundProvider : ISampleProvider
    {
        private readonly object _sync = new();
        private ISampleProvider _source;
        private float[] _delayBuffer;
        private int _delayPos;
        private int _delayGen;

        private volatile float _width = 0.5f;
        private volatile bool _enabled = true;

        public WaveFormat WaveFormat { get; private set; }

        public float Width
        {
            get => _width;
            set => _width = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;
        }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public SurroundProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
            int delSampl = (int)(source.WaveFormat.SampleRate * 0.020);
            _delayBuffer = new float[Math.Max(delSampl, 2)];
        }

        public void FlushBuffer()
        {
            lock (_sync)
            {
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
                _delayPos = 0;
                _delayGen++;
            }
        }

        public void ReplaceSource(ISampleProvider newSource)
        {
            if (newSource == null) throw new ArgumentNullException(nameof(newSource));
            int newDelay = Math.Max((int)(newSource.WaveFormat.SampleRate * 0.020), 2);
            lock (_sync)
            {
                _source = newSource;
                if (_delayBuffer.Length != newDelay)
                    _delayBuffer = new float[newDelay];
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSource.WaveFormat.SampleRate, 2);
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
                _delayPos = 0;
                _delayGen++;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count < 2) return 0;

            ISampleProvider src;
            float[] delay;
            lock (_sync)
            {
                src = _source;
                delay = _delayBuffer;
            }

            bool enabled = _enabled;
            float width = _width;
            int channels = src.WaveFormat.Channels;
            if (channels <= 0) return 0;

            if (channels == 1)
            {
                int monoCount = count / 2;
                var mono = ArrayPool<float>.Shared.Rent(monoCount);
                try
                {
                    int monoRead = src.Read(mono, 0, monoCount);
                    if (monoRead <= 0) return 0;

                    int read = monoRead * 2;
                    for (int i = 0; i < monoRead; i++)
                    {
                        float s = mono[i];
                        buffer[offset + i * 2] = s;
                        buffer[offset + i * 2 + 1] = s;
                    }
                    if (!enabled || width < 0.01f) return read;
                    ApplySurround(buffer, offset, read, delay, width);
                    return read;
                }
                finally { ArrayPool<float>.Shared.Return(mono); }
            }
            else if (channels == 2)
            {
                int evenCount = count - (count & 1);
                int read = src.Read(buffer, offset, evenCount);
                if (read <= 0) return 0;

                int evenRead = read - (read & 1);
                if (!enabled || width < 0.01f) return evenRead;
                ApplySurround(buffer, offset, evenRead, delay, width);
                return evenRead;
            }
            else
            {
                // Многоканал (5.1/7.1) → Стерео через ITU-R BS.775 даунмикс
                int inSamplesReq = (count / 2) * channels;
                var tmp = ArrayPool<float>.Shared.Rent(inSamplesReq);
                try
                {
                    int inRead = src.Read(tmp, 0, inSamplesReq);
                    if (inRead <= 0) return 0;

                    int frames = inRead / channels;
                    int outSamples = frames * 2;
                    int cols = Math.Min(channels, 8);

                    for (int i = 0; i < frames; i++)
                    {
                        float L = 0f, R = 0f;
                        int baseIdx = i * channels;
                        for (int ch = 0; ch < cols; ch++)
                        {
                            float s = tmp[baseIdx + ch];
                            if (channels >= 6 && ch == 3) continue; // отбрасываем LFE
                            L += s * DownmixTables.L[ch];
                            R += s * DownmixTables.R[ch];
                        }
                        buffer[offset + i * 2] = Math.Clamp(L, -1f, 1f);
                        buffer[offset + i * 2 + 1] = Math.Clamp(R, -1f, 1f);
                    }

                    if (!enabled || width < 0.01f) return outSamples;
                    ApplySurround(buffer, offset, outSamples, delay, width);
                    return outSamples;
                }
                finally { ArrayPool<float>.Shared.Return(tmp); }
            }
        }

        private void ApplySurround(float[] buffer, int offset, int read, float[] delay, float width)
        {
            int dlen = delay.Length;
            int evenRead = read - (read & 1);
            if (dlen == 0 || evenRead <= 0) return;

            int pos;
            int gen;
            lock (_sync)
            {
                if (!ReferenceEquals(delay, _delayBuffer)) return;
                pos = _delayPos;
                gen = _delayGen;
            }

            float sideScale = 0.5f * (1f + width * 2f);
            float dryGain = 1f - width * 0.3f;
            float wetGain = width * 0.3f;

            for (int i = 0; i < evenRead; i += 2)
            {
                float L = buffer[offset + i];
                float R = buffer[offset + i + 1];
                float mid = (L + R) * 0.5f;
                float side = (L - R) * sideScale;
                float wL = mid + side;
                float wR = mid - side;

                float delayed = delay[pos];
                delay[pos] = wR;
                pos = (pos + 1) % dlen;

                buffer[offset + i] = Math.Clamp(wL, -1f, 1f);
                buffer[offset + i + 1] = Math.Clamp(delayed * dryGain + wR * wetGain, -1f, 1f);
            }

            lock (_sync)
            {
                if (_delayGen == gen && ReferenceEquals(delay, _delayBuffer))
                    _delayPos = pos;
            }
        }
    }

    // ─── Volume ──────────────────────────────────────────────────────────────────
    public class VolumeProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private volatile float _volume = 1.0f;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public float Volume
        {
            get => _volume;
            set => _volume = float.IsFinite(value) ? Math.Max(0f, value) : 1.0f;
        }

        public VolumeProvider(ISampleProvider src)
        {
            _source = src ?? throw new ArgumentNullException(nameof(src));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return 0;

            float vol = _volume;
            if (vol == 1.0f) return read;
            if (vol <= 0.0f)
            {
                Array.Clear(buffer, offset, read);
                return read;
            }

            for (int i = 0; i < read; i++)
                buffer[offset + i] *= vol;

            return read;
        }
    }

    // ─── 5-Band Equalizer (BiQuad PeakingEQ) ─────────────────────────────────────
    public class EqualizerProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly object _filterSync = new();
        private readonly BiQuadFilter[,] _filters;
        private readonly float[] _gains;
        private readonly int _channels;
        private static readonly float[] Freqs = { 60f, 250f, 1000f, 4000f, 12000f };

        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _channels = Math.Max(1, source.WaveFormat.Channels);
            _gains = new float[5];
            _filters = new BiQuadFilter[5, _channels];
            RebuildFilters();
        }

        public void SetGain(int band, float dB)
        {
            if (band < 0 || band >= 5) return;
            float clamped = float.IsFinite(dB) ? Math.Clamp(dB, -24f, 24f) : 0f;

            lock (_filterSync)
            {
                _gains[band] = clamped;
                int sr = _source.WaveFormat.SampleRate;
                for (int ch = 0; ch < _channels; ch++)
                {
                    if (_filters[band, ch] == null)
                        _filters[band, ch] = BiQuadFilter.PeakingEQ(sr, Freqs[band], 1.0f, clamped);
                    else
                        _filters[band, ch].SetPeakingEq(sr, Freqs[band], 1.0f, clamped);
                }
            }
        }

        public float GetGain(int band)
        {
            if (band < 0 || band >= 5) return 0f;
            lock (_filterSync)
                return _gains[band];
        }

        private void RebuildFilters()
        {
            lock (_filterSync)
            {
                int sr = _source.WaveFormat.SampleRate;
                for (int b = 0; b < 5; b++)
                    for (int ch = 0; ch < _channels; ch++)
                        _filters[b, ch] = BiQuadFilter.PeakingEQ(sr, Freqs[b], 1.0f, _gains[b]);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return 0;

            lock (_filterSync)
            {
                for (int i = 0; i < read; i++)
                {
                    int ch = i % _channels;
                    float s = buffer[offset + i];
                    if (!float.IsFinite(s)) s = 0f;

                    for (int b = 0; b < 5; b++)
                        s = _filters[b, ch].Transform(s);

                    buffer[offset + i] = float.IsFinite(s) ? Math.Clamp(s, -1f, 1f) : 0f;
                }
            }
            return read;
        }
    }

    // ─── Soft Compressor ─────────────────────────────────────────────────────────
    public class CompressorProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _attackCoef;
        private readonly float _releaseCoef;
        private float _envelope;
        private volatile bool _enabled = true;
        private volatile bool _resetPending;

        private const float Threshold = 0.7f;
        private const float Ratio = 4.0f;
        private const float Attack = 0.003f;
        private const float Release = 0.15f;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public CompressorProvider(ISampleProvider src)
        {
            _source = src ?? throw new ArgumentNullException(nameof(src));
            float sr = Math.Max(1, WaveFormat.SampleRate);
            _attackCoef = MathF.Exp(-1f / (sr * Attack));
            _releaseCoef = MathF.Exp(-1f / (sr * Release));
        }

        /// <summary>Сбрасывает огибающую компрессора — вызывать при seek, чтобы не давить тихий сигнал.</summary>
        public void Reset() => _resetPending = true;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return 0;

            if (_resetPending)
            {
                _envelope = 0f;
                _resetPending = false;
            }

            if (!_enabled)
            {
                _envelope = 0f;
                return read;
            }

            for (int i = 0; i < read; i++)
            {
                float s = buffer[offset + i];
                if (!float.IsFinite(s))
                {
                    buffer[offset + i] = 0f;
                    continue;
                }

                float abs = MathF.Abs(s);
                _envelope = abs > _envelope
                    ? _attackCoef * _envelope + (1f - _attackCoef) * abs
                    : _releaseCoef * _envelope + (1f - _releaseCoef) * abs;

                if (_envelope > Threshold)
                {
                    float target = Threshold + (_envelope - Threshold) / Ratio;
                    float gain = Math.Min(target / Math.Max(_envelope, 1e-6f), 1f);
                    buffer[offset + i] = Math.Clamp(s * gain, -1f, 1f);
                }
                else
                {
                    buffer[offset + i] = Math.Clamp(s, -1f, 1f);
                }
            }
            return read;
        }
    }

    // ─── FFT Aggregator ───────────────────────────────────────────────────────────
    public class FftAggregator : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _fftBuffer;
        private readonly float[] _hannWindow;
        private readonly Complex[] _fftComplex;
        private readonly object _fftLock = new();
        private int _fftPos;
        private int _readCount;

        private const int FftSize = 1024;
        private const int FftSkipBuffers = 0;

        public WaveFormat WaveFormat => _source.WaveFormat;
        public float[] FftData { get; } = new float[FftSize / 2];
        private volatile bool _hasData;
        public bool HasData => _hasData;

        public FftAggregator(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _fftBuffer = new float[FftSize];
            _hannWindow = new float[FftSize];
            _fftComplex = new Complex[FftSize];

            for (int i = 0; i < FftSize; i++)
                _hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return 0;

            _readCount++;
            if (_readCount % (FftSkipBuffers + 1) != 0)
                return read;

            int channels = WaveFormat.Channels;
            if (channels <= 0) return read;

            int limit = read - (read % channels);
            for (int i = 0; i < limit; i += channels)
            {
                float sample = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    float s = buffer[offset + i + ch];
                    if (float.IsFinite(s)) sample += s;
                }
                sample /= channels;

                _fftBuffer[_fftPos] = sample * _hannWindow[_fftPos];
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
                double ang = -2 * Math.PI / len;
                var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = x[i + j];
                        var v = x[i + j + len / 2] * w;
                        x[i + j] = u + v;
                        x[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
        }
    }

    // ─── FfmpegFloatProvider ──────────────────────────────────────────────────────
    public class FfmpegFloatProvider : ISampleProvider
    {
        private readonly WaveStream _source;
        private readonly byte[] _tailBytes;
        private readonly int _frameAlign;
        private int _tailLength;

        public WaveFormat WaveFormat { get; }

        public FfmpegFloatProvider(WaveStream source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = source.WaveFormat;
            _frameAlign = Math.Max(4, WaveFormat.BlockAlign);
            _tailBytes = new byte[_frameAlign];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count <= 0 || offset >= buffer.Length) return 0;

            int maxSamples = buffer.Length - offset;
            int samplesToRead = Math.Min(count, maxSamples);
            int channels = Math.Max(1, WaveFormat.Channels);
            if (samplesToRead >= channels)
                samplesToRead -= samplesToRead % channels;

            int bytesToRead = samplesToRead * 4;
            if (bytesToRead <= 0) return 0;

            byte[] tmp = ArrayPool<byte>.Shared.Rent(bytesToRead);
            int bytesRead = 0;

            try
            {
                if (_tailLength > 0)
                {
                    int take = Math.Min(_tailLength, bytesToRead);
                    Buffer.BlockCopy(_tailBytes, 0, tmp, 0, take);
                    bytesRead = take;
                    if (take < _tailLength)
                        Buffer.BlockCopy(_tailBytes, take, _tailBytes, 0, _tailLength - take);
                    _tailLength -= take;
                }

                while (bytesRead < bytesToRead)
                {
                    int got;
                    try { got = _source.Read(tmp, bytesRead, bytesToRead - bytesRead); }
                    catch (ObjectDisposedException) { break; }
                    if (got == 0) break;
                    bytesRead += got;
                }

                // Если пайп вернул неполный кадр — добираем до целого кадра (frameAlign байт)
                while (bytesRead > 0 && bytesRead < _frameAlign && bytesRead < bytesToRead)
                {
                    int got;
                    try { got = _source.Read(tmp, bytesRead, Math.Min(_frameAlign, bytesToRead) - bytesRead); }
                    catch (ObjectDisposedException) { break; }
                    if (got == 0) break;
                    bytesRead += got;
                }

                int alignedBytes = bytesRead - (bytesRead % _frameAlign);
                int remainder = bytesRead - alignedBytes;
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

    public class CueLimitProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveStream _stream;
        private readonly TimeSpan _trackEnd;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public CueLimitProvider(ISampleProvider source, WaveStream stream, TimeSpan trackStart, TimeSpan trackEnd)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _trackEnd = trackEnd;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            if (_trackEnd > TimeSpan.Zero)
            {
                double remaining = (_trackEnd - _stream.CurrentTime).TotalSeconds;
                if (remaining <= 0) return 0;
                int sr = WaveFormat.SampleRate, ch = WaveFormat.Channels;
                long maxSamples = (long)(remaining * sr) * ch;
                count = (int)Math.Min(count, Math.Max(0, maxSamples));
                if (count == 0) return 0;
            }
            return _source.Read(buffer, offset, count);
        }
    }

    public class CueSegmentProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly TimeSpan _trackStart;
        private readonly TimeSpan _trackEnd;

        public WaveFormat WaveFormat => _reader.WaveFormat;

        public double PositionSeconds =>
            Math.Max(0, _reader.CurrentTime.TotalSeconds - _trackStart.TotalSeconds);

        public CueSegmentProvider(AudioFileReader reader, TimeSpan trackStart, TimeSpan trackEnd)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _trackStart = trackStart;
            _trackEnd = trackEnd;
        }

        public void SeekTo(TimeSpan absolutePosition)
        {
            _reader.CurrentTime = absolutePosition < TimeSpan.Zero ? TimeSpan.Zero : absolutePosition;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
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

#nullable restore