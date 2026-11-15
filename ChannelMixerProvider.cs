// ChannelMixerProvider.cs - умное микширование каналов.
// Поддерживает:
//   - Mono -> Stereo (с Haas-эффектом ~1 мс)
//   - Stereo -> Mono (суммирование с нормализацией)
//   - Многоканал (3-8+) -> Stereo (матричное даунмикширование по ITU-R BS.775)
//   - Многоканал -> Mono
//   - Passthrough
#nullable enable

using System;
using System.Buffers;
using System.Threading;
using NAudio.Wave;

namespace AuroraPlayer
{
    public enum MixMode
    {
        /// <summary>Ничего не менять - выводим как есть.</summary>
        Passthrough = 0,
        /// <summary>Принудительно в Mono.</summary>
        ForceMono = 1,
        /// <summary>Принудительно в Stereo (для mono - с Haas-эффектом, для многоканала - матрица).</summary>
        ForceStereo = 2,
        /// <summary>Stereo -> Mid/Side расширение (увеличивает ширину).</summary>
        WideStero = 3, // Оставлено как есть: используется в других файлах.
        /// <summary>Stereo -> Mid/Side сужение (уменьшает ширину, хорошо для наушников).</summary>
        NarrowStereo = 4,
    }

    /// <summary>
    /// Гибкий микшер каналов. Вставляется первым в цепочку ISampleProvider,
    /// сразу после baseProvider. Всегда выдает ровно <see cref="OutputChannels"/> каналов.
    /// </summary>
    public sealed class ChannelMixerProvider : ISampleProvider
    {
        private ISampleProvider _source;
        private readonly object _sync = new();

        private MixMode _mode;
        private float _width = 1.0f; // Для WideStero / NarrowStereo: 0..2
        private int _inChannels;
        private float _lfeWeight = 0.0f;

        // Кольцевой буфер задержки Haas (~1 мс) — используется только в потоке Read()
        private float[] _haasDelay;
        private int _haasDelayPos;
        private bool _resetHaasDelay;

        public float LfeWeight
        {
            get { lock (_sync) return _lfeWeight; }
            set { lock (_sync) _lfeWeight = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f; }
        }

        public const int OutputChannels = 2; // Всегда stereo на выходе
        public WaveFormat WaveFormat { get; private set; }

        public MixMode Mode
        {
            get { lock (_sync) return _mode; }
            set
            {
                lock (_sync)
                {
                    if (_mode != value)
                    {
                        _mode = value;
                        _resetHaasDelay = true;
                    }
                }
            }
        }

        /// <summary>Ширина stereo для WideStero/NarrowStereo: 0=Mono, 1=Original, 2=Max Wide.</summary>
        public float Width
        {
            get { lock (_sync) return _width; }
            set { lock (_sync) _width = float.IsFinite(value) ? Math.Clamp(value, 0f, 2f) : 1.0f; }
        }

        public int InputChannels => Volatile.Read(ref _inChannels);

        public ChannelMixerProvider(ISampleProvider source, MixMode mode = MixMode.Passthrough)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _inChannels = source.WaveFormat.Channels;
            _mode = mode;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, OutputChannels);
            _haasDelay = new float[Math.Max(1, source.WaveFormat.SampleRate / 1000)];
        }

        /// <summary>Горячая замена источника (вызывается из ReplaceSource в SurroundProvider).</summary>
        public void ReplaceSource(ISampleProvider newSource)
        {
            if (newSource == null) throw new ArgumentNullException(nameof(newSource));

            lock (_sync)
            {
                _source = newSource;
                Volatile.Write(ref _inChannels, newSource.WaveFormat.Channels);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSource.WaveFormat.SampleRate, OutputChannels);
                _resetHaasDelay = true;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            ISampleProvider src;
            MixMode mode;
            float width;
            float lfe;
            int inCh;
            int sampleRate;
            bool resetDelay;

            lock (_sync)
            {
                src = _source;
                mode = _mode;
                width = _width;
                lfe = _lfeWeight;
                inCh = _inChannels;
                sampleRate = WaveFormat.SampleRate;
                resetDelay = _resetHaasDelay;
                _resetHaasDelay = false;
            }

            if (inCh <= 0 || count < OutputChannels)
                return 0;

            int neededDelay = Math.Max(1, sampleRate / 1000);
            if (_haasDelay.Length != neededDelay)
            {
                _haasDelay = new float[neededDelay];
                _haasDelayPos = 0;
            }
            else if (resetDelay)
            {
                Array.Clear(_haasDelay, 0, _haasDelay.Length);
                _haasDelayPos = 0;
            }

            int frameCount = count / OutputChannels;
            int inSamplesReq = frameCount * inCh;
            if (inSamplesReq <= 0)
                return 0;

            float[] inBuf = ArrayPool<float>.Shared.Rent(inSamplesReq);
            try
            {
                int inRead = src.Read(inBuf, 0, inSamplesReq);
                if (inRead <= 0) return 0;

                int framesRead = inRead / inCh;
                if (framesRead <= 0) return 0;

                int outSamples = framesRead * OutputChannels;

                if (mode == MixMode.Passthrough)
                {
                    if (inCh == 1)
                        MonoToWideStereo(inBuf, buffer, offset, framesRead, 0f);
                    else if (inCh == 2)
                        Buffer.BlockCopy(inBuf, 0, buffer, offset * sizeof(float), outSamples * sizeof(float));
                    else
                        SmartDownmix(inBuf, buffer, offset, framesRead, inCh, 1.0f, lfe);

                    return outSamples;
                }

                switch (mode)
                {
                    case MixMode.ForceMono:
                        MixToMono(inBuf, buffer, offset, framesRead, inCh, lfe);
                        break;

                    case MixMode.ForceStereo:
                        if (inCh == 1)
                            MonoToWideStereo(inBuf, buffer, offset, framesRead, 0.3f);
                        else
                            SmartDownmix(inBuf, buffer, offset, framesRead, inCh, 1.0f, lfe);
                        break;

                    case MixMode.WideStero:
                        if (inCh == 1)
                            MonoToWideStereo(inBuf, buffer, offset, framesRead, width * 0.5f);
                        else if (inCh == 2)
                            MidSideProcess(inBuf, buffer, offset, framesRead, width);
                        else
                            SmartDownmix(inBuf, buffer, offset, framesRead, inCh, width, lfe);
                        break;

                    case MixMode.NarrowStereo:
                        if (inCh == 1)
                            MonoToWideStereo(inBuf, buffer, offset, framesRead, 0.1f);
                        else if (inCh == 2)
                            MidSideProcess(inBuf, buffer, offset, framesRead, Math.Clamp(width * 0.5f, 0f, 1f));
                        else
                            SmartDownmix(inBuf, buffer, offset, framesRead, inCh, Math.Clamp(width * 0.5f, 0f, 1f), lfe);
                        break;
                }

                return outSamples;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(inBuf);
            }
        }

        /// <summary>
        /// Mono -> Stereo с непрерывным Haas-эффектом (~1 мс задержка на R-канале).
        /// </summary>
        private void MonoToWideStereo(float[] src, float[] dst, int offset, int frames, float widthFactor)
        {
            if (widthFactor <= 0f)
            {
                for (int i = 0; i < frames; i++)
                {
                    float direct = Math.Clamp(src[i], -1f, 1f);
                    dst[offset + i * 2] = direct;
                    dst[offset + i * 2 + 1] = direct;
                }
                return;
            }

            int delayLen = _haasDelay.Length;
            float dryMix = 1f - widthFactor * 0.5f;
            float wetMix = widthFactor * 0.5f;

            for (int i = 0; i < frames; i++)
            {
                float s = src[i];
                float delayed = _haasDelay[_haasDelayPos];
                _haasDelay[_haasDelayPos] = s;
                _haasDelayPos++;
                if (_haasDelayPos >= delayLen)
                    _haasDelayPos = 0;

                dst[offset + i * 2] = Math.Clamp(s, -1f, 1f);
                dst[offset + i * 2 + 1] = Math.Clamp(s * dryMix + delayed * wetMix, -1f, 1f);
            }
        }

        /// <summary>Многоканал/Stereo -> Mono с нормализацией.</summary>
        private static void MixToMono(float[] src, float[] dst, int offset, int frames, int inCh, float lfe)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int used = 0;
                int baseIdx = i * inCh;
                for (int ch = 0; ch < inCh && ch < 8; ch++)
                {
                    if (inCh >= 6 && ch == 3)
                    {
                        sum += src[baseIdx + ch] * lfe;
                        continue;
                    }
                    sum += src[baseIdx + ch];
                    used++;
                }
                float mono = Math.Clamp(sum / Math.Max(used, 1), -1f, 1f);
                dst[offset + i * 2] = mono;
                dst[offset + i * 2 + 1] = mono;
            }
        }

        /// <summary>
        /// Матричный даунмикс до stereo по ITU-R BS.775.
        /// widthScale: 1.0 = нейтральный, >1 усиливает разнос каналов.
        /// </summary>
        private static void SmartDownmix(float[] src, float[] dst, int offset, int frames, int inCh, float widthScale, float lfe)
        {
            int cols = Math.Min(inCh, 8);
            for (int i = 0; i < frames; i++)
            {
                float L = 0f;
                float R = 0f;
                int baseIdx = i * inCh;
                for (int ch = 0; ch < cols; ch++)
                {
                    float s = src[baseIdx + ch];
                    if (inCh >= 6 && ch == 3)
                    {
                        L += s * lfe;
                        R += s * lfe;
                        continue;
                    }
                    L += s * DownmixTables.L[ch];
                    R += s * DownmixTables.R[ch];
                }

                float mid = (L + R) * 0.5f;
                float side = (L - R) * 0.5f * widthScale;
                dst[offset + i * 2] = Math.Clamp(mid + side, -1f, 1f);
                dst[offset + i * 2 + 1] = Math.Clamp(mid - side, -1f, 1f);
            }
        }

        /// <summary>Mid/Side ширина для stereo-сигнала.</summary>
        private static void MidSideProcess(float[] src, float[] dst, int offset, int frames, float widthScale)
        {
            for (int i = 0; i < frames; i++)
            {
                float L = src[i * 2];
                float R = src[i * 2 + 1];
                float mid = (L + R) * 0.5f;
                float side = (L - R) * 0.5f * widthScale;
                dst[offset + i * 2] = Math.Clamp(mid + side, -1f, 1f);
                dst[offset + i * 2 + 1] = Math.Clamp(mid - side, -1f, 1f);
            }
        }
    }
}

#nullable restore