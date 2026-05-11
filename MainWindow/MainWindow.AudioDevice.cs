// MainWindow.AudioDevice.cs — выбор и горячее переключение устройства вывода звука
using System;
using System.Windows;
using System.Windows.Controls;

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

        private void AudioDevice_Click(object s, RoutedEventArgs e)
        {
            ShowAudioDeviceMenu();
        }

        private void ShowAudioDeviceMenu()
        {
            var devices = AudioOutputEngine.EnumerateDevices();
            var menu    = new ContextMenu();

            foreach (var device in devices)
            {
                bool isCurrent = device.DeviceNumber == -1
                    ? _audioDeviceName == null
                    : string.Equals(device.Name, _audioDeviceName,
                                    StringComparison.OrdinalIgnoreCase);

                var item = new MenuItem
                {
                    Header      = device.Name,
                    IsCheckable = true,
                    IsChecked   = isCurrent,
                };

                var d = device; // захватываем для лямбды
                item.Click += (_, _) => SwitchAudioDevice(d);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = AudioDeviceBtn;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen          = true;
        }

        private void SwitchAudioDevice(AudioDeviceInfo device)
        {
            // Сохраняем имя (null = системное по умолчанию)
            _audioDeviceName = device.DeviceNumber == -1 ? null : device.Name;

            // Горячая замена — воспроизведение не прерывается
            _audioOutput.SwitchDevice(device.DeviceNumber);
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
