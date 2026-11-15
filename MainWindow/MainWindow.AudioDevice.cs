// MainWindow.AudioDevice.cs — выбор и горячее переключение устройства вывода звука
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ══════════════════════════════════════════════════════════════════════════
        // ВЫБОР УСТРОЙСТВА ВЫВОДА ЗВУКА
        //
        // Кнопка в заголовке окна открывает меню со списком WaveOut-устройств.
        // Переключение происходит мгновенно без остановки воспроизведения.
        // Выбранное устройство сохраняется в settings.json по имени.
        // ══════════════════════════════════════════════════════════════════════════

        private readonly object _audioDeviceSwitchLock = new object();
        private ContextMenu? _audioDeviceMenu;
        private long _audioDeviceMenuClosedTimestamp;
        private int? _activeAudioDeviceNumber;
        private bool _isSwitchingAudioDevice;

        private void AudioDevice_Click(object s, RoutedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AudioDevice_Click(s, e)));
                return;
            }

            if (_audioDeviceMenu != null && _audioDeviceMenu.IsOpen)
            {
                _audioDeviceMenu.IsOpen = false;
                return;
            }

            // Защита от повторного открытия: при клике на кнопку WPF закрывает
            // открытое меню на MouseDown, а затем на MouseUp снова вызывает Click.
            if (_audioDeviceMenuClosedTimestamp != 0)
            {
                double elapsedMs = (Stopwatch.GetTimestamp() - _audioDeviceMenuClosedTimestamp)
                                   * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < 200.0)
                    return;
            }

            ShowAudioDeviceMenu();
        }

        private void ShowAudioDeviceMenu()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowAudioDeviceMenu));
                return;
            }

            if (_audioDeviceMenu != null)
            {
                _audioDeviceMenu.IsOpen = false;
                CleanupAudioDeviceMenu(_audioDeviceMenu);
                _audioDeviceMenu = null;
            }

            var rawDevices = AudioOutputEngine.EnumerateDevices();
            var devices = rawDevices != null
                ? rawDevices.Where(d => d != null).ToList()
                : new List<AudioDeviceInfo>();

            var menu = new ContextMenu();
            menu.Closed += AudioDeviceMenu_Closed;

            if (devices.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "(Устройства вывода не найдены)",
                    IsEnabled = false
                });
            }
            else
            {
                bool isDefaultSelected = string.IsNullOrWhiteSpace(_audioDeviceName);

                bool hasMatchingExplicitDevice = !isDefaultSelected && devices.Any(d =>
                    d.DeviceNumber != -1 &&
                    string.Equals(d.Name, _audioDeviceName, StringComparison.OrdinalIgnoreCase));

                bool canMatchByActiveNumber =
                    hasMatchingExplicitDevice &&
                    _activeAudioDeviceNumber.HasValue &&
                    _activeAudioDeviceNumber.Value != -1 &&
                    devices.Any(d =>
                        d.DeviceNumber == _activeAudioDeviceNumber.Value &&
                        string.Equals(d.Name, _audioDeviceName, StringComparison.OrdinalIgnoreCase));

                bool matchedExplicit = false;

                foreach (var device in devices)
                {
                    bool isCurrent;
                    if (device.DeviceNumber == -1)
                    {
                        // Показываем системное устройство активным, если оно выбрано явно
                        // либо сохранённое устройство отсутствует (было отключено).
                        isCurrent = isDefaultSelected || !hasMatchingExplicitDevice;
                    }
                    else if (!isDefaultSelected &&
                             string.Equals(device.Name, _audioDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        isCurrent = canMatchByActiveNumber
                            ? !matchedExplicit && device.DeviceNumber == _activeAudioDeviceNumber!.Value
                            : !matchedExplicit;

                        if (isCurrent)
                            matchedExplicit = true;
                    }
                    else
                    {
                        isCurrent = false;
                    }

                    var item = new MenuItem
                    {
                        Header = string.IsNullOrWhiteSpace(device.Name)
                            ? $"Устройство #{device.DeviceNumber}"
                            : device.Name,
                        IsCheckable = true,
                        IsChecked = isCurrent,
                        Tag = device
                    };

                    item.Click += AudioDeviceMenuItem_Click;
                    menu.Items.Add(item);
                }
            }

            if (AudioDeviceBtn != null)
            {
                menu.PlacementTarget = AudioDeviceBtn;
                menu.Placement = PlacementMode.Bottom;
            }
            else
            {
                menu.Placement = PlacementMode.MousePoint;
            }

            _audioDeviceMenu = menu;
            menu.IsOpen = true;
        }

        private void AudioDeviceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is AudioDeviceInfo device)
            {
                SwitchAudioDevice(device);
            }
        }

        private void AudioDeviceMenu_Closed(object? sender, RoutedEventArgs e)
        {
            _audioDeviceMenuClosedTimestamp = Stopwatch.GetTimestamp();

            if (sender is ContextMenu menu)
            {
                CleanupAudioDeviceMenu(menu);
                if (ReferenceEquals(_audioDeviceMenu, menu))
                    _audioDeviceMenu = null;
            }
        }

        private void CleanupAudioDeviceMenu(ContextMenu menu)
        {
            menu.Closed -= AudioDeviceMenu_Closed;

            foreach (var obj in menu.Items)
            {
                if (obj is MenuItem item)
                {
                    item.Click -= AudioDeviceMenuItem_Click;
                    item.Tag = null;
                }
            }

            menu.Items.Clear();
            menu.PlacementTarget = null;
        }

        private void SwitchAudioDevice(AudioDeviceInfo? device)
        {
            if (device == null)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SwitchAudioDevice(device)));
                return;
            }

            if (_isSwitchingAudioDevice)
                return;

            var audioOutput = _audioOutput;
            if (audioOutput == null)
                return;

            _isSwitchingAudioDevice = true;
            try
            {
                lock (_audioDeviceSwitchLock)
                {
                    int targetDeviceNumber;
                    string? targetDeviceName;
                    bool hasDuplicateName = false;

                    if (device.DeviceNumber == -1)
                    {
                        targetDeviceNumber = -1;
                        targetDeviceName = null;
                    }
                    else
                    {
                        // Повторно опрашиваем устройства в момент клика (защита от сдвига
                        // индексов WaveOut, если USB/Bluetooth-устройство подключили/отключили).
                        var freshDevices = AudioOutputEngine.EnumerateDevices()?
                            .Where(d => d != null)
                            .ToList();

                        AudioDeviceInfo? resolved = null;
                        if (freshDevices != null)
                        {
                            resolved = freshDevices.FirstOrDefault(d =>
                                d.DeviceNumber == device.DeviceNumber &&
                                string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase));

                            resolved ??= freshDevices.FirstOrDefault(d =>
                                d.DeviceNumber != -1 &&
                                string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase));

                            if (resolved != null)
                            {
                                hasDuplicateName = freshDevices.Count(d =>
                                    d.DeviceNumber != -1 &&
                                    string.Equals(d.Name, resolved.Name, StringComparison.OrdinalIgnoreCase)) > 1;
                            }
                        }

                        if (resolved == null)
                            return;

                        targetDeviceNumber = resolved.DeviceNumber;
                        targetDeviceName = resolved.Name;
                    }

                    bool isAlreadyActive = targetDeviceNumber == -1
                        ? string.IsNullOrWhiteSpace(_audioDeviceName)
                          && (!_activeAudioDeviceNumber.HasValue || _activeAudioDeviceNumber.Value == -1)
                        : !string.IsNullOrWhiteSpace(_audioDeviceName)
                          && string.Equals(targetDeviceName, _audioDeviceName, StringComparison.OrdinalIgnoreCase)
                          && (_activeAudioDeviceNumber.HasValue
                              ? _activeAudioDeviceNumber.Value == targetDeviceNumber
                              : !hasDuplicateName);

                    if (isAlreadyActive)
                        return;

                    // Обновляем состояние только после успешного переключения устройства
                    audioOutput.SwitchDevice(targetDeviceNumber);

                    _audioDeviceName = targetDeviceName;
                    _activeAudioDeviceNumber = targetDeviceNumber;

                    // TODO: вызовите ваш метод сохранения настроек в settings.json,
                    // если он не вызывается в другом месте (например: SaveSettings();).
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioDevice] Не удалось переключить устройство: {ex}");
            }
            finally
            {
                _isSwitchingAudioDevice = false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // АРХИТЕКТУРНАЯ ЗАГЛУШКА ДЛЯ ПЛАГИНОВ
        //
        // Не реализовано. Интерфейс ниже зарезервирован для будущего.
        //
        // Когда добавлять:
        //   Только при конкретной потребности (DSP, scrobbler и т.д.) и только
        //   если реализация не требует глубокого рефакторинга ядра плеера.
        //
        // Минимальный контракт плагина:
        //   interface IAuroraPlugin {
        //     string Name    { get; }
        //     string Version { get; }
        //     void   Initialize(IPlayerContext ctx);
        //     void   Shutdown();
        //   }
        //
        // Загрузка: Assembly.LoadFrom из папки Plugins\ рядом с exe.
        // Изоляция: AssemblyLoadContext (опционально, добавлять по необходимости).
        // ══════════════════════════════════════════════════════════════════════════
    }
}

#nullable restore