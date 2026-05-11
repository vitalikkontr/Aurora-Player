using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace AuroraPlayer
{
    /// <summary>
    /// Описание WaveOut-устройства для отображения в UI.
    /// </summary>
    public sealed class AudioDeviceInfo
    {
        public int    DeviceNumber { get; }   // -1 = системное по умолчанию
        public string Name        { get; }

        public AudioDeviceInfo(int deviceNumber, string name)
        {
            DeviceNumber = deviceNumber;
            Name         = name;
        }

        public override string ToString() => Name;
    }

    internal sealed class AudioOutputEngine : IDisposable
    {
        private sealed class SwappableSampleProvider : ISampleProvider
        {
            private readonly object _sync = new();
            private ISampleProvider? _source;
            private int _zeroReadStreak;
            private const int EndOfStreamZeroReads = 3;

            public WaveFormat WaveFormat { get; }

            public SwappableSampleProvider(WaveFormat waveFormat)
            {
                WaveFormat = waveFormat;
            }

            public void SetSource(ISampleProvider? source)
            {
                lock (_sync)
                {
                    _source = source;
                    _zeroReadStreak = 0;
                }
            }

            public event EventHandler? SourceReturnedZero;

            public int Read(float[] buffer, int offset, int count)
            {
                ISampleProvider? source;
                lock (_sync)
                {
                    source = _source;
                }

                if (source == null) return 0;
                int read = source.Read(buffer, offset, count);
                if (read == 0)
                {
                    // Некоторые источники потоков (особенно декодеры процессов/конвейеров) могут ненадолго возвращать 0
                    // Во время запуска. Подтверждайте конец потока только после нескольких чтений нулей.
                    _zeroReadStreak++;
                    if (_zeroReadStreak < EndOfStreamZeroReads)
                    {
                        Array.Clear(buffer, offset, count);
                        return count;
                    }

                    _zeroReadStreak = 0;
                    SourceReturnedZero?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    _zeroReadStreak = 0;
                }
                return read;
            }
        }

        private readonly object _sync = new();
        private readonly WaveFormat _outputFormat;
        private readonly SwappableSampleProvider _router;
        private WaveOutEvent _output;
        private bool _disposed;
        private volatile bool _manualStop;
        private readonly int _desiredLatencyMs;

        // ── Перечисление устройств ────────────────────────────────────────────────

        /// <summary>
        /// Возвращает список доступных WaveOut-устройств.
        /// Первый элемент всегда — «По умолчанию» (DeviceNumber = -1).
        /// </summary>
        public static IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
        {
            var list = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo(-1, "По умолчанию (системное)")
            };
            int count = WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var caps = WaveOut.GetCapabilities(i);
                    list.Add(new AudioDeviceInfo(i, caps.ProductName));
                }
                catch { }
            }
            return list;
        }

        /// <summary>Находит DeviceNumber по сохранённому имени. Возвращает -1 если не найдено.</summary>
        public static int FindDeviceByName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            foreach (var d in EnumerateDevices())
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                    return d.DeviceNumber;
            return -1;
        }

        private static (int sampleRate, int channels) DetectOutputFormat(int fallbackSampleRate, int fallbackChannels)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mix = device?.AudioClient?.MixFormat;
                if (mix == null)
                    return (fallbackSampleRate, fallbackChannels);

                int sampleRate = mix.SampleRate > 0 ? mix.SampleRate : fallbackSampleRate;
                int channels = mix.Channels > 0 ? mix.Channels : fallbackChannels;
                channels = Math.Clamp(channels, 1, 2);
                return (sampleRate, channels);
            }
            catch
            {
                return (fallbackSampleRate, fallbackChannels);
            }
        }

        /// <summary>Срабатывает, когда источник возвращает 0 естественным образом (не из Stop/StopAndWait).</summary>
        public event EventHandler? TrackEnded;

        public PlaybackState PlaybackState
        {
            get { lock (_sync) { return _disposed ? PlaybackState.Stopped : _output.PlaybackState; } }
        }

        /// <param name="deviceNumber">Номер WaveOut-устройства (-1 = системное по умолчанию).</param>
        public AudioOutputEngine(int sampleRate = 48000, int channels = 2,
                                 int desiredLatencyMs = 200, int deviceNumber = -1)
        {
            _desiredLatencyMs = desiredLatencyMs;
            var detected = DetectOutputFormat(sampleRate, channels);
            _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(detected.sampleRate, detected.channels);
            _router = new SwappableSampleProvider(_outputFormat);

            _output = CreateWaveOut(deviceNumber);
            _output.Init(_router);
            _router.SourceReturnedZero += (s, e) =>
            {
                if (!_manualStop) TrackEnded?.Invoke(this, EventArgs.Empty);
            };
        }

        // ── Горячая замена устройства ─────────────────────────────────────────────

        /// <summary>
        /// Переключает вывод на другое устройство без перезапуска плеера.
        /// Воспроизведение возобновляется автоматически если было активно.
        /// </summary>
        public void SwitchDevice(int newDeviceNumber)
        {
            lock (_sync)
            {
                if (_disposed) return;

                bool wasPlaying = _output.PlaybackState == PlaybackState.Playing;

                _manualStop = true;
                try { _output.Stop(); } catch { }
                _output.Dispose();
                _manualStop = false;

                _output = CreateWaveOut(newDeviceNumber);
                _output.Init(_router);

                if (wasPlaying) _output.Play();
            }
        }

        private WaveOutEvent CreateWaveOut(int deviceNumber)
        {
            return new WaveOutEvent
            {
                DeviceNumber    = Math.Max(deviceNumber, -1),
                DesiredLatency  = _desiredLatencyMs,
                NumberOfBuffers = 3,
            };
        }

        public void SetSource(ISampleProvider? source)
        {
            if (_disposed) return;
            _router.SetSource(source == null ? null : EnsureOutputFormat(source));
        }

        public void Play()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _output.Play();
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _output.Pause();
            }
        }

        public void Stop()
        {
            _manualStop = true;
            lock (_sync)
            {
                if (!_disposed) _output.Stop();
            }
            _manualStop = false;
        }

        public void StopAndWait()
        {
            _manualStop = true;
            SetSource(null);
            lock (_sync)
            {
                if (!_disposed) _output.Stop();
            }
            _manualStop = false;
        }

        private ISampleProvider EnsureOutputFormat(ISampleProvider source)
        {
            ISampleProvider current = source;

            if (current.WaveFormat.SampleRate != _outputFormat.SampleRate)
                current = new WdlResamplingSampleProvider(current, _outputFormat.SampleRate);

            if (_outputFormat.Channels == 1)
            {
                if (current.WaveFormat.Channels > 2)
                    current = current.ToStereo();

                if (current.WaveFormat.Channels == 2)
                    current = current.ToMono(0.5f, 0.5f);
            }
            else
            {
                if (current.WaveFormat.Channels == 1)
                    current = new MonoToStereoSampleProvider(current);
                else if (current.WaveFormat.Channels != 2)
                    current = current.ToStereo();
            }

            return current;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _router.SetSource(null);
            try { _output.Stop(); } catch { }
            _output.Dispose();
        }
    }
}
