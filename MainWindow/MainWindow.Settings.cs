// MainWindow.Settings.cs — сохранение и загрузка настроек
#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        private void SaveSettings()
        {
            try
            {
                var gains = new float[5];
                for (int b = 0; b < 5; b++)
                {
                    double g = _eqValues[b];
                    gains[b] = double.IsFinite(g) ? (float)Math.Clamp(g, -12.0, 12.0) : 0f;
                }

                double pos = 0;
                string posSource = "none";

                if (_pendingSeekOnNextPlay is double pendingPos &&
                    double.IsFinite(pendingPos) && pendingPos > 0)
                {
                    pos = pendingPos;
                    posSource = $"pendingSeek={pendingPos:F3}";
                }
                else if (HasReader || (_cachedAudioReader != null && _cachedAudioPath != null))
                {
                    double sliderPos = double.IsFinite(ProgressSlider.Value) ? ProgressSlider.Value : 0;

                    double readerPos = HasReader
                        ? Math.Max(0, ReaderCurrentTime.TotalSeconds - _cueStart.TotalSeconds)
                        : Math.Max(0, _cachedAudioReader!.CurrentTime.TotalSeconds - _cueStart.TotalSeconds);

                    if (!double.IsFinite(readerPos)) readerPos = 0;

                    bool isFfmpegReader = _mfReader is FfmpegDecodeStream;

                    if (isFfmpegReader && readerPos > 0.5 &&
                        (sliderPos < 0.5 || Math.Abs(readerPos - sliderPos) > 1.0))
                        pos = readerPos;
                    else if (sliderPos > 0.5)
                        pos = sliderPos;
                    else if (readerPos > 0.5)
                        pos = readerPos;
                    else
                        pos = Math.Max(sliderPos, readerPos);

                    string ffmpegHint = isFfmpegReader ? " ffmpeg=True" : "";
                    posSource = $"slider={sliderPos:F3} reader={readerPos:F3} chosen={pos:F3} cueStart={_cueStart.TotalSeconds:F3}{ffmpegHint}";
                }

                if (!double.IsFinite(pos) || pos < 0) pos = 0;

                FfmpegService.AppendDecodeLog(
                    $"SAVE pos={pos:F3} source={posSource} index={_currentIndex} " +
                    $"isPlaying={_isPlaying} audioReader={_audioReader != null} " +
                    $"mfReader={_mfReader != null} cachedReader={_cachedAudioReader != null}");

                // Безопасно получаем геометрию окна (без -32000 при Minimized и без NaN при SizeToContent)
                Rect bounds = WindowState == WindowState.Normal
                    ? new Rect(
                        double.IsFinite(Left) ? Left : 100,
                        double.IsFinite(Top) ? Top : 100,
                        double.IsFinite(Width) && Width > 0 ? Width : ActualWidth,
                        double.IsFinite(Height) && Height > 0 ? Height : ActualHeight)
                    : RestoreBounds;

                double winLeft = double.IsFinite(bounds.Left) ? bounds.Left : 100;
                double winTop = double.IsFinite(bounds.Top) ? bounds.Top : 100;

                double miniW = _isMini
                    ? (ActualWidth >= 260 ? ActualWidth : _miniPlayerWidth)
                    : _miniPlayerWidth;
                if (!double.IsFinite(miniW) || miniW < 260) miniW = 480;

                double winWidth;
                double winHeight;

                if (_isMini)
                {
                    winWidth = miniW;
                    winHeight = double.IsFinite(_fullPlayerHeight) && _fullPlayerHeight >= 300
                        ? _fullPlayerHeight : 680;
                }
                else if (_isPseudoMaximized)
                {
                    winWidth = double.IsFinite(_preMaxWidth) && _preMaxWidth >= 420 ? _preMaxWidth : 420;
                    winHeight = double.IsFinite(_preMaxHeight) && _preMaxHeight >= 300 ? _preMaxHeight : 680;
                }
                else
                {
                    winWidth = double.IsFinite(bounds.Width) && bounds.Width >= 420 ? bounds.Width : 420;
                    double h = double.IsFinite(bounds.Height) && bounds.Height >= 300 ? bounds.Height : _fullPlayerHeight;
                    if (_eqPanelOpen && EqContainer != null && EqContainer.ActualHeight > 1)
                        h -= EqContainer.ActualHeight;
                    if (_mixPanelOpen && MixerContainer != null && MixerContainer.ActualHeight > 1)
                        h -= MixerContainer.ActualHeight;
                    winHeight = double.IsFinite(h) ? Math.Max(300, h) : 680;
                }

                var savedPaths = Playlist.Count > 0
                    ? Playlist.Select(t => t.Path)
                              .Where(p => !string.IsNullOrWhiteSpace(p))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList()
                    : null;

                var s = new AppSettings
                {
                    LastFolder = _lastFolder,
                    SavedPaths = savedPaths,
                    LastIndex = _currentIndex,
                    LastPosition = pos,
                    Volume = double.IsFinite(_volume) ? Math.Clamp(_volume, 0.0, 1.0) : 0.75,
                    Shuffle = _shuffle,
                    Repeat = _repeat,
                    SurroundOn = _surroundEnabled,
                    SurroundWidth = float.IsFinite(_surroundWidth) ? Math.Clamp(_surroundWidth, 0.0f, 1.0f) : 0.5f,
                    EqGains = gains,
                    EqPreset = _currentPreset,
                    VizMode = _vizSettings.Mode,
                    VizLeft = double.IsFinite(_vizSettings.Left) ? _vizSettings.Left : 100,
                    VizTop = double.IsFinite(_vizSettings.Top) ? _vizSettings.Top : 100,
                    VizWidth = double.IsFinite(_vizSettings.Width) && _vizSettings.Width > 0 ? _vizSettings.Width : 760,
                    VizHeight = double.IsFinite(_vizSettings.Height) && _vizSettings.Height > 0 ? _vizSettings.Height : 430,
                    MiniPlayerWidth = miniW,
                    MixMode = (int)_mixMode,
                    MixWidth = float.IsFinite(_mixWidth) ? Math.Clamp(_mixWidth, 0.0f, 2.0f) : 1.0f,
                    MixLfeWeight = float.IsFinite(_mixLfeWeight) ? Math.Clamp(_mixLfeWeight, 0.0f, 1.0f) : 0.0f,
                    WindowLeft = winLeft,
                    WindowTop = winTop,
                    WindowWidth = winWidth,
                    WindowHeight = winHeight,
                    IsMini = _isMini,
                    AudioDeviceName = _audioDeviceName,
                };
                SaveColorsToSettings(s);
                SettingsService.Save(s);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveSettings error: {ex}");
                FfmpegService.AppendDecodeLog($"SAVE_ERR {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                var s = SettingsService.Load();
                if (s == null) return;

                _volume = double.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0.0, 1.0) : 0.75;
                _surroundEnabled = s.SurroundOn;
                _surroundWidth = float.IsFinite(s.SurroundWidth) ? Math.Clamp(s.SurroundWidth, 0.0f, 1.0f) : 0.5f;
                VolumeSlider.Value = Math.Clamp(_volume * 100.0,
                    VolumeSlider.Minimum, VolumeSlider.Maximum);
                UpdateSurroundButton();

                if (s.EqPreset >= 0 && s.EqPreset < EqPresets.Length)
                {
                    ApplyPreset(s.EqPreset);
                }
                else if (s.EqGains?.Length == 5)
                {
                    for (int b = 0; b < 5; b++)
                    {
                        float g = s.EqGains[b];
                        _eqValues[b] = float.IsFinite(g) ? Math.Clamp(g, -12f, 12f) : 0f;
                    }
                    BuildEqPanel();
                }
                else
                {
                    _currentPreset = -1;
                    UpdateAllPresetChips();
                }

                if (s.Shuffle) Shuffle_Click(this, new RoutedEventArgs());
                if (s.Repeat) Repeat_Click(this, new RoutedEventArgs());

                _vizSettings = new VisualizerSettings
                {
                    Mode = s.VizMode,
                    Left = double.IsFinite(s.VizLeft) ? s.VizLeft : 100,
                    Top = double.IsFinite(s.VizTop) ? s.VizTop : 100,
                    Width = double.IsFinite(s.VizWidth) && s.VizWidth > 0 ? s.VizWidth : 760,
                    Height = double.IsFinite(s.VizHeight) && s.VizHeight > 0 ? s.VizHeight : 430,
                };

                if (double.IsFinite(s.MiniPlayerWidth) && s.MiniPlayerWidth >= 260)
                    _miniPlayerWidth = s.MiniPlayerWidth;

                // ── Микшер ────────────────────────────────────────────────────
                _mixMode = Enum.IsDefined(typeof(MixMode), s.MixMode)
                    ? (MixMode)s.MixMode
                    : MixMode.Passthrough;
                _mixWidth = float.IsFinite(s.MixWidth)
                    ? Math.Clamp(s.MixWidth, 0.0f, 2.0f)
                    : 1.0f;
                _mixLfeWeight = float.IsFinite(s.MixLfeWeight)
                    ? Math.Clamp(s.MixLfeWeight, 0.0f, 1.0f)
                    : 0.0f;

                if (_channelMixer != null)
                {
                    _channelMixer.Mode = _mixMode;
                    _channelMixer.Width = _mixWidth;
                    _channelMixer.LfeWeight = _mixLfeWeight;
                }
                BuildMixerPanel();

                // ── Аудио-устройство ──────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(s.AudioDeviceName))
                {
                    _audioDeviceName = s.AudioDeviceName;
                    int devNum = AudioOutputEngine.FindDeviceByName(_audioDeviceName);
                    if (devNum >= 0)
                        _audioOutput.SwitchDevice(devNum);
                }

                // ── Цвета темы ────────────────────────────────────────────────
                LoadColorsFromSettings(s);

                // ── Позиция и режим окна (с поддержкой мультимонитора) ────────
                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                bool hasValidPos =
                    double.IsFinite(s.WindowLeft) && double.IsFinite(s.WindowTop) &&
                    s.WindowLeft >= vsLeft - 100 && s.WindowLeft < vsRight - 40 &&
                    s.WindowTop >= vsTop - 40 && s.WindowTop < vsBottom - 40;

                if (double.IsFinite(s.WindowHeight) && s.WindowHeight >= 300)
                    _fullPlayerHeight = s.WindowHeight;

                if (s.IsMini)
                {
                    if (hasValidPos)
                    {
                        _miniPlayerLeft = s.WindowLeft;
                        _miniPlayerTop = s.WindowTop;
                        Left = s.WindowLeft;
                        Top = s.WindowTop;
                    }

                    if (!_isMini)
                        MiniMode_Click(this, new RoutedEventArgs());
                }
                else
                {
                    if (double.IsFinite(s.WindowWidth) && s.WindowWidth >= 420)
                        Width = s.WindowWidth;
                    if (double.IsFinite(s.WindowHeight) && s.WindowHeight >= 300)
                        Height = s.WindowHeight;

                    if (hasValidPos)
                    {
                        Left = s.WindowLeft;
                        Top = s.WindowTop;
                        ClampWindowToWorkArea();
                    }
                }

                int savedIndex = s.LastIndex;
                double savedPos = double.IsFinite(s.LastPosition) && s.LastPosition > 0
                    ? s.LastPosition
                    : 0;

                if (!string.IsNullOrEmpty(s.LastFolder) && Directory.Exists(s.LastFolder))
                {
                    FfmpegService.AppendDecodeLog(
                        $"LOAD savedIndex={savedIndex} savedPos={savedPos:F3} folder='{s.LastFolder}'");

                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            if (_isClosingHandled) return;
                            FfmpegService.AppendDecodeLog(
                                $"LOAD_INVOKE start savedIndex={savedIndex} savedPos={savedPos:F3}");
                            await AddFolderAsync(s.LastFolder,
                                initialIndex: savedIndex >= 0 ? savedIndex : 0,
                                initialSeek: savedPos);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"LoadSettings folder restore error: {ex}");
                        }
                    });
                }
                else if (s.SavedPaths != null && s.SavedPaths.Count > 0)
                {
                    FfmpegService.AppendDecodeLog(
                        $"LOAD from SavedPaths count={s.SavedPaths.Count} savedIndex={savedIndex} savedPos={savedPos:F3}");

                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (_isClosingHandled) return;

                            foreach (var path in s.SavedPaths)
                            {
                                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                    AddTrack(path);
                            }

                            if (Playlist.Count > 0)
                            {
                                int idx = savedIndex >= 0 && savedIndex < Playlist.Count ? savedIndex : 0;
                                _sessionRestoreDone = true;
                                LoadTrack(idx, autoPlay: false, seekSeconds: savedPos);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"LoadSettings SavedPaths restore error: {ex}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadSettings error: {ex}");
                FfmpegService.AppendDecodeLog($"LOAD_ERR {ex.Message}");
            }
        }
    }
}

#nullable restore