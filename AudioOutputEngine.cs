#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
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
        public int DeviceNumber { get; }   // -1 = системное по умолчанию
        public string Name { get; }

        public AudioDeviceInfo(int deviceNumber, string name)
        {
            DeviceNumber = deviceNumber;
            Name = name;
        }

        public override string ToString() => Name;
    }

    internal sealed class AudioOutputEngine : IDisposable
    {
        private sealed class SwappableSampleProvider : ISampleProvider
        {
            private readonly object _sync = new();
            private readonly object _readLock = new();
            private readonly ManualResetEventSlim _readIdle = new(true);

            private ISampleProvider? _source;
            private int _activeReads;
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

            public void WaitForIdle(int timeoutMs = 500)
            {
                try { _readIdle.Wait(timeoutMs); }
                catch (ObjectDisposedException) { }
            }

            public void DisposeWaitHandle()
            {
                try { _readIdle.Dispose(); }
                catch { }
            }

            public event EventHandler? SourceReturnedZero;

            public int Read(float[] buffer, int offset, int count)
            {
                lock (_readLock)
                {
                    _activeReads++;
                    try { _readIdle.Reset(); } catch (ObjectDisposedException) { }
                }

                try
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
                        bool shouldNotify = false;
                        lock (_sync)
                        {
                            // Игнорируем 0 от уже заменённого источника
                            if (!ReferenceEquals(_source, source))
                                return 0;

                            _zeroReadStreak++;
                            if (_zeroReadStreak < EndOfStreamZeroReads)
                            {
                                Array.Clear(buffer, offset, count);
                                return count;
                            }

                            _zeroReadStreak = 0;
                            shouldNotify = true;
                        }

                        if (shouldNotify)
                            SourceReturnedZero?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_source, source))
                                _zeroReadStreak = 0;
                        }
                    }

                    return read;
                }
                finally
                {
                    lock (_readLock)
                    {
                        _activeReads--;
                        if (_activeReads <= 0)
                        {
                            _activeReads = 0;
                            try { _readIdle.Set(); } catch (ObjectDisposedException) { }
                        }
                    }
                }
            }
        }

        private readonly object _sync = new();
        private readonly WaveFormat _outputFormat;
        private readonly SwappableSampleProvider _router;
        private readonly EventHandler _sourceZeroHandler;
        private WaveOutEvent? _output;
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
                    string name = caps.ProductName?.TrimEnd('\0', ' ') ?? $"Устройство #{i}";
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Устройство #{i}";
                    list.Add(new AudioDeviceInfo(i, name));
                }
                catch { }
            }
            return list;
        }

        /// <summary>Находит DeviceNumber по сохранённому имени. Возвращает -1, если не найдено.</summary>
        public static int FindDeviceByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            foreach (var d in EnumerateDevices())
                if (d.DeviceNumber != -1 && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
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
                int channels = mix.Channels > 0 ? Math.Clamp(mix.Channels, 1, 2) : fallbackChannels;
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
            get
            {
                lock (_sync)
                {
                    if (_disposed || _output == null)
                        return PlaybackState.Stopped;
                    return _output.PlaybackState;
                }
            }
        }

        /// <param name="deviceNumber">Номер WaveOut-устройства (-1 = системное по умолчанию).</param>
        public AudioOutputEngine(int sampleRate = 48000, int channels = 2,
                                 int desiredLatencyMs = 200, int deviceNumber = -1)
        {
            _desiredLatencyMs = Math.Max(20, desiredLatencyMs);
            var detected = DetectOutputFormat(sampleRate, channels);
            _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(detected.sampleRate, detected.channels);
            _router = new SwappableSampleProvider(_outputFormat);

            _sourceZeroHandler = (_, _) =>
            {
                if (!_manualStop && !_disposed)
                    TrackEnded?.Invoke(this, EventArgs.Empty);
            };
            _router.SourceReturnedZero += _sourceZeroHandler;

            _output = InitWaveOutWithFallback(deviceNumber);
        }

        private WaveOutEvent? InitWaveOutWithFallback(int deviceNumber)
        {
            WaveOutEvent? output = null;
            try
            {
                output = CreateWaveOut(deviceNumber);
                output.Init(_router);
                return output;
            }
            catch
            {
                try { output?.Dispose(); } catch { }
                if (deviceNumber != -1)
                {
                    try
                    {
                        var fallback = CreateWaveOut(-1);
                        fallback.Init(_router);
                        return fallback;
                    }
                    catch { }
                }
                return null;
            }
        }

        // ── Горячая замена устройства ─────────────────────────────────────────────

        /// <summary>
        /// Переключает вывод на другое устройство без перезапуска плеера.
        /// Воспроизведение возобновляется автоматически, если было активно.
        /// </summary>
        public void SwitchDevice(int newDeviceNumber)
        {
            lock (_sync)
            {
                if (_disposed) return;

                bool wasPlaying = false;
                if (_output != null)
                {
                    try { wasPlaying = _output.PlaybackState == PlaybackState.Playing; } catch { }
                }

                _manualStop = true;
                try
                {
                    if (_output != null)
                    {
                        try { _output.Stop(); } catch { }
                        _router.WaitForIdle(500);
                        try { _output.Dispose(); } catch { }
                        _output = null;
                    }

                    WaveOutEvent? next = null;
                    try
                    {
                        next = CreateWaveOut(newDeviceNumber);
                        next.Init(_router);
                        _output = next;
                        next = null;

                        if (wasPlaying)
                            _output.Play();
                    }
                    catch
                    {
                        try { next?.Dispose(); } catch { }
                        // Пытаемся восстановить вывод на системное устройство, чтобы движок остался рабочим
                        _output = InitWaveOutWithFallback(-1);
                        throw;
                    }
                }
                finally
                {
                    _manualStop = false;
                }
            }
        }

        private WaveOutEvent CreateWaveOut(int deviceNumber)
        {
            return new WaveOutEvent
            {
                DeviceNumber = Math.Max(deviceNumber, -1),
                DesiredLatency = _desiredLatencyMs,
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
                if (_disposed || _output == null) return;
                _output.Play();
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (_disposed || _output == null) return;
                _output.Pause();
            }
        }

        public void Stop()
        {
            _manualStop = true;
            try
            {
                lock (_sync)
                {
                    if (!_disposed && _output != null)
                    {
                        try { _output.Stop(); } catch { }
                    }
                }
            }
            finally
            {
                _manualStop = false;
            }
        }

        public void StopAndWait()
        {
            _manualStop = true;
            try
            {
                _router.SetSource(null);
                lock (_sync)
                {
                    if (!_disposed && _output != null)
                    {
                        try { _output.Stop(); } catch { }
                    }
                }
                _router.WaitForIdle(500);
            }
            finally
            {
                _manualStop = false;
            }
        }

        private ISampleProvider EnsureOutputFormat(ISampleProvider source)
        {
            ISampleProvider current = source;

            // Сначала приводим число каналов к целевому (до ресэмплинга),
            // чтобы не тратить CPU на ресэмплинг лишних каналов и не падать в ToStereo() на >2ch.
            if (_outputFormat.Channels == 1)
            {
                if (current.WaveFormat.Channels > 2)
                    current = new MultiplexingSampleProvider(new[] { current }, 2);

                if (current.WaveFormat.Channels == 2)
                    current = current.ToMono(0.5f, 0.5f);
            }
            else
            {
                if (current.WaveFormat.Channels == 1)
                    current = new MonoToStereoSampleProvider(current);
                else if (current.WaveFormat.Channels > 2)
                    current = new MultiplexingSampleProvider(new[] { current }, 2);
            }

            if (current.WaveFormat.SampleRate != _outputFormat.SampleRate)
                current = new WdlResamplingSampleProvider(current, _outputFormat.SampleRate);

            return current;
        }

        public void Dispose()
        {
            WaveOutEvent? output;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _manualStop = true;
                output = _output;
                _output = null;
            }

            _router.SourceReturnedZero -= _sourceZeroHandler;
            _router.SetSource(null);

            if (output != null)
            {
                try { output.Stop(); } catch { }
                _router.WaitForIdle(500);
                try { output.Dispose(); } catch { }
            }

            _router.DisposeWaitHandle();
            TrackEnded = null;
        }
    }
}

#nullable restore