// MainWindow.Settings.cs — сохранение и загрузка настроек
using System;
using System.IO;
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
                for (int b = 0; b < 5; b++) gains[b] = (float)_eqValues[b];

                double pos = 0;
                string posSource = "none";

                // _pendingSeekOnNextPlay содержит реальную позицию когда трек загружен
                // с autoPlay=false (восстановление после запуска / CUE / FLAC без Play).
                // В этом случае seek ещё не применён к _audioReader — там лежит 0 или
                // позиция от предыдущего сеанса. Поэтому pendingSeek имеет приоритет.
                if (_pendingSeekOnNextPlay is double pendingPos && pendingPos > 0)
                {
                    pos = pendingPos;
                    posSource = $"pendingSeek={pendingPos:F3}";
                }
                else if (HasReader || (_cachedAudioReader != null && _cachedAudioPath != null))
                {
                    // Для FLAC/WAV ReaderCurrentTime опережает реальное воспроизведение
                    // на размер буфера WaveOut (~200 мс). ProgressSlider.Value обновляется
                    // таймером и точнее отражает последнюю видимую позицию.
                    // Используем максимум из слайдера и ридера как наиболее надёжное значение.
                    double sliderPos = ProgressSlider.Value; // последнее значение таймера (250 мс)

                    double readerPos;
                    if (HasReader)
                        readerPos = Math.Max(0, ReaderCurrentTime.TotalSeconds - _cueStart.TotalSeconds);
                    else
                        readerPos = Math.Max(0, _cachedAudioReader!.CurrentTime.TotalSeconds - _cueStart.TotalSeconds);

                    bool isFfmpegReader = _mfReader is FfmpegDecodeStream;

                    // Если слайдер достоверен (трек играл или пауза после воспроизведения),
                    // предпочитаем его — он не опережает реальный звук на буфер.
                    // Но если слайдер == 0 а ридер > 0 (например после seek без Play) — берём ридер.
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
                FfmpegService.AppendDecodeLog(
                    $"SAVE pos={pos:F3} source={posSource} index={_currentIndex} " +
                    $"isPlaying={_isPlaying} audioReader={_audioReader != null} " +
                    $"mfReader={_mfReader != null} cachedReader={_cachedAudioReader != null}");

                // Сохраняем позицию и размер окна
                // Для мини-плеера сохраняем текущие Left/Top
                // Для полного — тоже Left/Top + Width/Height
                double winLeft   = Left;
                double winTop    = Top;
                double winWidth  = _isMini ? _miniPlayerWidth : Width;
                double winHeight = Height; // одинаково для мини и полного — реальная высота окна

                // Собираем все настройки в один объект — один вызов Save
                var s = new AppSettings
                {
                    LastFolder      = _lastFolder,
                    LastIndex       = _currentIndex,
                    LastPosition    = pos,
                    Volume          = _volume,
                    Shuffle         = _shuffle,
                    Repeat          = _repeat,
                    SurroundOn      = _surroundEnabled,
                    SurroundWidth   = _surroundWidth,
                    EqGains         = gains,
                    EqPreset        = _currentPreset,
                    VizMode         = _vizSettings.Mode,
                    VizLeft         = _vizSettings.Left,
                    VizTop          = _vizSettings.Top,
                    VizWidth        = _vizSettings.Width,
                    VizHeight       = _vizSettings.Height,
                    MiniPlayerWidth = _isMini ? Width : _miniPlayerWidth,
                    MixMode         = (int)_mixMode,
                    MixWidth        = _mixWidth,
                    MixLfeWeight    = _mixLfeWeight,
                    WindowLeft      = winLeft,
                    WindowTop       = winTop,
                    WindowWidth     = winWidth,
                    WindowHeight    = winHeight,
                    IsMini          = _isMini,
                    AudioDeviceName = _audioDeviceName,
                };
                SaveColorsToSettings(s); // добавляем цвета в тот же объект
                SettingsService.Save(s); // единственный вызов Save
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var s = SettingsService.Load();
                if (s == null) return;

                _volume          = Math.Clamp(s.Volume, 0.0, 1.0);
                _surroundEnabled = s.SurroundOn;
                _surroundWidth   = s.SurroundWidth;
                VolumeSlider.Value = Math.Clamp(s.Volume * 100,
                    VolumeSlider.Minimum, VolumeSlider.Maximum);
                UpdateSurroundButton();

                if (s.EqPreset >= 0 && s.EqPreset < EqPresets.Length)
                    ApplyPreset(s.EqPreset);
                else if (s.EqGains?.Length == 5)
                {
                    for (int b = 0; b < 5; b++)
                        _eqValues[b] = Math.Clamp(s.EqGains[b], -12f, 12f);
                    BuildEqPanel();
                }
                else
                {
                    _currentPreset = -1;
                    UpdateAllPresetChips();
                }

                if (s.Shuffle) Shuffle_Click(this, null!);
                if (s.Repeat)  Repeat_Click(this, null!);

                _vizSettings = new VisualizerSettings
                {
                    Mode   = s.VizMode,
                    Left   = s.VizLeft,
                    Top    = s.VizTop,
                    Width  = s.VizWidth  > 0 ? s.VizWidth  : 760,
                    Height = s.VizHeight > 0 ? s.VizHeight : 430,
                };

                if (s.MiniPlayerWidth >= 260)
                    _miniPlayerWidth = s.MiniPlayerWidth;

                // ── Микшер ────────────────────────────────────────────────────
                _mixMode      = (MixMode)s.MixMode;
                _mixWidth     = s.MixWidth > 0 ? s.MixWidth : 1.0f;
                _mixLfeWeight = s.MixLfeWeight;
                if (_channelMixer != null)
                {
                    _channelMixer.Mode      = _mixMode;
                    _channelMixer.Width     = _mixWidth;
                    _channelMixer.LfeWeight = _mixLfeWeight;
                }
                BuildMixerPanel();

                // ── Аудио-устройство ──────────────────────────────────────────
                if (!string.IsNullOrEmpty(s.AudioDeviceName))
                    _audioDeviceName = s.AudioDeviceName;

                // ── Цвета темы ────────────────────────────────────────────────
                LoadColorsFromSettings(s);

                // ── Позиция и режим окна ──────────────────────────────────────
                var wa = SystemParameters.WorkArea;

                if (s.IsMini)
                {
                    // Восстанавливаем мини-плеер
                    if (!_isMini) MiniMode_Click(this, null!);

                    if (s.WindowLeft >= wa.Left && s.WindowLeft < wa.Right &&
                        s.WindowTop  >= wa.Top  && s.WindowTop  < wa.Bottom)
                    {
                        Left = s.WindowLeft;
                        Top  = s.WindowTop;
                        // Запоминаем позицию чтобы при разворачивании и обратном сворачивании
                        // мини-плеер вернулся на то же место
                        _miniPlayerLeft = s.WindowLeft;
                        _miniPlayerTop  = s.WindowTop;
                    }
                }
                else
                {
                    // Восстанавливаем полный плеер
                    if (s.WindowWidth  >= 420) Width  = s.WindowWidth;
                    if (s.WindowHeight >= 300)
                    {
                        Height = s.WindowHeight;
                        _fullPlayerHeight = s.WindowHeight; // запоминаем для мини↔полный
                    }

                    if (s.WindowLeft >= wa.Left && s.WindowLeft < wa.Right &&
                        s.WindowTop  >= wa.Top  && s.WindowTop  < wa.Bottom)
                    {
                        Left = s.WindowLeft;
                        Top  = s.WindowTop;
                    }
                }

                if (!string.IsNullOrEmpty(s.LastFolder) && Directory.Exists(s.LastFolder))
                {
                    int    savedIndex = s.LastIndex;
                    double savedPos   = s.LastPosition;

                    FfmpegService.AppendDecodeLog(
                        $"LOAD savedIndex={savedIndex} savedPos={savedPos:F3} folder='{s.LastFolder}'");

                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        FfmpegService.AppendDecodeLog(
                            $"LOAD_INVOKE start savedIndex={savedIndex} savedPos={savedPos:F3}");
                        await AddFolderAsync(s.LastFolder,
                            initialIndex: savedIndex >= 0 ? savedIndex : 0,
                            initialSeek:  savedPos > 0 ? savedPos : 0);
                    });
                }
            }
            catch { }
        }
    }
}
