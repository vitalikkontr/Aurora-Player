// ChannelMixerProvider.cs - умное микширование каналов.
// Поддерживает:
//   - Mono -> Stereo (c Haas-эффектом ~1 мс)
//   - Stereo -> Mono (суммирование с нормализацией)
//   - Многоканал (3-8+) -> Stereo (матричное даунмикширование по ITU-R BS.775)
//   - Многоканал -> Mono
//   - Passthrough
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

        // Вес LFE (саббас).
        // 0.0 = LFE полностью отбрасывается (стандарт для headphone downmix)
        // 0.5 = половина LFE подмешивается в оба канала
        private float _lfeWeight = 0.0f;
        public float LfeWeight
        {
            get { lock (_sync) return _lfeWeight; }
            set { lock (_sync) _lfeWeight = Math.Clamp(value, 0f, 1f); }
        }

        public const int OutputChannels = 2; // Всегда stereo на выходе
        public WaveFormat WaveFormat { get; private set; }

        public MixMode Mode
        {
            get { lock (_sync) return _mode; }
            set { lock (_sync) _mode = value; }
        }

        /// <summary>Ширина stereo для WideStero/NarrowStereo: 0=Mono, 1=Original, 2=Max Wide.</summary>
        public float Width
        {
            get { lock (_sync) return _width; }
            set { lock (_sync) _width = Math.Clamp(value, 0f, 2f); }
        }

        public int InputChannels => _inChannels;

        public ChannelMixerProvider(ISampleProvider source, MixMode mode = MixMode.Passthrough)
        {
            _source = source;
            _inChannels = source.WaveFormat.Channels;
            _mode = mode;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, OutputChannels);
        }

        /// <summary>Горячая замена источника (вызывается из ReplaceSource в SurroundProvider).</summary>
        public void ReplaceSource(ISampleProvider newSource)
        {
            lock (_sync)
            {
                _source = newSource;
                _inChannels = newSource.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(newSource.WaveFormat.SampleRate, OutputChannels);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            ISampleProvider src;
            MixMode mode;
            float width;
            float lfe;
            int inCh;

            lock (_sync)
            {
                src = _source;
                mode = _mode;
                width = _width;
                lfe = _lfeWeight;
                inCh = _inChannels;
            }

            int frameCount = count / OutputChannels;
            int inSamplesReq = frameCount * inCh;

            float[] inBuf = System.Buffers.ArrayPool<float>.Shared.Rent(inSamplesReq);
            try
            {
                int inRead = src.Read(inBuf, 0, inSamplesReq);
                int framesRead = inRead / inCh;
                int outSamples = framesRead * OutputChannels;

                if (mode == MixMode.Passthrough)
                {
                    if (inCh == 1)
                        MonoToWideStereo(inBuf, buffer, offset, framesRead, 0f); // dual-mono в AUTO
                    else if (inCh == 2)
                        Buffer.BlockCopy(inBuf, 0, buffer, offset * sizeof(float), inRead * sizeof(float));
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
                            SmartDownmix(inBuf, buffer, offset, framesRead, inCh, 0.5f, lfe);
                        break;
                }

                return outSamples;
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(inBuf);
            }
        }

        /// <summary>
        /// Mono -> Stereo с Haas-эффектом (~1 мс задержка на R-канале).
        /// widthFactor: 0 = идентичные каналы, 0.5 = заметная ширина.
        /// </summary>
        private static void MonoToWideStereo(float[] src, float[] dst, int offset, int frames, float widthFactor)
        {
            const int DelayFrames = 48; // ~1 мс при 48 kHz
            for (int i = 0; i < frames; i++)
            {
                float s = src[i];
                float delayed = i >= DelayFrames ? src[i - DelayFrames] : 0f;
                float direct = Math.Clamp(s, -1f, 1f);
                float haas = Math.Clamp(s * (1f - widthFactor * 0.5f) + delayed * widthFactor * 0.5f, -1f, 1f);
                dst[offset + i * 2] = direct;
                dst[offset + i * 2 + 1] = haas;
            }
        }

        /// <summary>Многоканал/Stereo -> Mono с нормализацией.</summary>
        private static void MixToMono(float[] src, float[] dst, int offset, int frames, int inCh, float lfe)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int used = 0;
                for (int ch = 0; ch < inCh && ch < 8; ch++)
                {
                    // LFE (канал 3 в 5.1) подмешиваем по коэффициенту lfe.
                    if (inCh >= 6 && ch == 3)
                    {
                        sum += src[i * inCh + ch] * lfe;
                        continue;
                    }
                    sum += src[i * inCh + ch];
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
                for (int ch = 0; ch < cols; ch++)
                {
                    float s = src[i * inCh + ch];
                    if (inCh >= 6 && ch == 3)
                    {
                        L += s * lfe;
                        R += s * lfe;
                        continue;
                    }
                    L += s * DownmixTables.L[ch];
                    R += s * DownmixTables.R[ch];
                }

                // Mid/Side расширение / сужение.
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
