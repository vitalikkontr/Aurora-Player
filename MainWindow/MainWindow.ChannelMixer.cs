// MainWindow_ChannelMixer.cs — панель микширования каналов
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ── Состояние микшера ──────────────────────────────────────────────────
        private ChannelMixerProvider? _channelMixer;
        private MixMode  _mixMode         = MixMode.Passthrough;
        private float    _mixWidth        = 1.0f;
        private float    _mixLfeWeight    = 0.0f;
        private bool     _mixPanelOpen    = false;

        // ── Описания режимов ───────────────────────────────────────────────────
        private static readonly (MixMode Mode, string Label, string Hint)[] MixModes =
        {
            (MixMode.Passthrough,  "AUTO",    "Оригинал без изменений"),
            (MixMode.ForceStereo,  "STEREO",  "Принудительно в стерео"),
            (MixMode.ForceMono,    "MONO",    "Суммирование в моно"),
            (MixMode.WideStero,    "WIDE",    "Расширение стерео-базы"),
            (MixMode.NarrowStereo, "NARROW",  "Сужение (наушники)"),
        };

        // ── Построение панели ─────────────────────────────────────────────────

        internal void BuildMixerPanel()
        {
            MixerContainer.Children.Clear();

            // ── Chips режимов + кнопка RESET ──────────────────────────────────
            var chipRow = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 10)
            };

            foreach (var (mode, label, hint) in MixModes)
            {
                var m    = mode;
                var chip = new Border
                {
                    CornerRadius     = new CornerRadius(12),
                    Padding          = new Thickness(14, 5, 14, 5),
                    Margin           = new Thickness(4, 3, 4, 3),
                    Cursor           = Cursors.Hand,
                    ToolTip          = hint,
                    Tag              = m,
                };
                var txt = new TextBlock
                {
                    Text            = label,
                    FontFamily      = new FontFamily("Syne"),
                    FontSize        = 10,
                    FontWeight      = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                chip.Child = txt;
                UpdateMixChipStyle(chip, txt, m == _mixMode);

                chip.MouseLeftButtonDown += (s, e) =>
                {
                    _mixMode = m;
                    if (_channelMixer != null) _channelMixer.Mode = m;
                    RefreshMixerChips();
                    UpdateChannelInfo();
                    e.Handled = true;
                };
                chip.MouseEnter += (s, e) =>
                {
                    if ((MixMode)chip.Tag != _mixMode)
                        chip.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                };
                chip.MouseLeave += (s, e) =>
                {
                    if ((MixMode)chip.Tag != _mixMode)
                        chip.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
                };
                chipRow.Children.Add(chip);
            }

            // ── Кнопка RESET ──────────────────────────────────────────────────
            var resetChip = new Border
            {
                CornerRadius    = new CornerRadius(12),
                Padding         = new Thickness(14, 5, 14, 5),
                Margin          = new Thickness(4, 3, 4, 3),
                Cursor          = Cursors.Hand,
                ToolTip         = "Сбросить все настройки микшера",
                Background      = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x60, 0x60)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x60, 0x60)),
                BorderThickness = new Thickness(1),
            };
            var resetTxt = new TextBlock
            {
                Text            = "RESET",
                FontFamily      = new FontFamily("Syne"),
                FontSize        = 10,
                FontWeight      = FontWeights.Bold,
                Foreground      = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x80, 0x80)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            resetChip.Child = resetTxt;
            resetChip.MouseEnter += (s, e) =>
                resetChip.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x60, 0x60));
            resetChip.MouseLeave += (s, e) =>
                resetChip.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x60, 0x60));
            resetChip.MouseLeftButtonDown += (s, e) =>
            {
                _mixMode      = MixMode.Passthrough;
                _mixWidth     = 1.0f;
                _mixLfeWeight = 0.0f;
                if (_channelMixer != null)
                {
                    _channelMixer.Mode      = _mixMode;
                    _channelMixer.Width     = _mixWidth;
                    _channelMixer.LfeWeight = _mixLfeWeight;
                }
                UpdateChannelInfo();
                BuildMixerPanel();
                e.Handled = true;
            };
            chipRow.Children.Add(resetChip);
            MixerContainer.Children.Add(chipRow);

            // ── Слайдер ширины ────────────────────────────────────────────────
            AddMixerSlider(MixerContainer,
                label:    "ШИРИНА",
                min: 0.0, max: 2.0,
                value:    _mixWidth,
                formatFn: v => $"{(int)(v * 100)}%",
                accentColor: _cyan,
                onChange: v =>
                {
                    _mixWidth = (float)v;
                    if (_channelMixer != null) _channelMixer.Width = _mixWidth;
                });

            // ── Слайдер LFE ───────────────────────────────────────────────────
            AddMixerSlider(MixerContainer,
                label:    "LFE / SUB",
                min: 0.0, max: 1.0,
                value:    _mixLfeWeight,
                formatFn: v => $"{(int)(v * 100)}%",
                accentColor: _accent2,
                onChange: v =>
                {
                    _mixLfeWeight = (float)v;
                    if (_channelMixer != null) _channelMixer.LfeWeight = _mixLfeWeight;
                });

            // ── Инфо-строка ───────────────────────────────────────────────────
            var infoRow = new TextBlock
            {
                Text            = BuildMixInfoText(),
                FontFamily      = new FontFamily("DM Sans"),
                FontSize        = 9,
                Foreground      = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin          = new Thickness(0, 6, 10, 4),
                Name            = "MixInfoLabel",
            };
            MixerContainer.Children.Add(infoRow);
        }

        // ── Хелперы построения ─────────────────────────────────────────────────

        private void AddMixerSlider(Panel parent, string label,
            double min, double max, double value,
            Func<double, string> formatFn, Color accentColor,
            Action<double> onChange)
        {
            var row = new Grid { Margin = new Thickness(8, 0, 8, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var lbl = new TextBlock
            {
                Text       = label,
                FontFamily = new FontFamily("Syne"),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x70, accentColor.R, accentColor.G, accentColor.B)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var slider = new Slider
            {
                Minimum              = min,
                Maximum              = max,
                Value                = value,
                VerticalAlignment    = VerticalAlignment.Center,
                Margin               = new Thickness(6, 0, 6, 0),
                IsMoveToPointEnabled = false, // отключаем прыжок к точке клика
            };
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);

            var valLbl = new TextBlock
            {
                Text                = formatFn(value),
                FontFamily          = new FontFamily("DM Sans"),
                FontSize            = 9,
                FontWeight          = FontWeights.Bold,
                Foreground          = new SolidColorBrush(Colors.White),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth            = 34,
            };
            Grid.SetColumn(valLbl, 2);
            row.Children.Add(valLbl);

            // ── Фикс прыгающего ползунка ──────────────────────────────────────
            // WPF Slider при клике по треку сначала вызывает PreviewMouseLeftButtonDown
            // на Track, потом ещё раз обрабатывает через RepeatButton — отсюда двойной
            // прыжок. Перехватываем PreviewMouseLeftButtonDown: вычисляем точную
            // позицию по X клика относительно ширины слайдера и сразу ставим Value.
            // e.Handled = true блокирует дальнейшую обработку WPF.
            slider.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // Если клик попал на Thumb — не мешаем, пусть WPF обрабатывает drag
                if (e.OriginalSource is System.Windows.Shapes.Ellipse or
                    System.Windows.Controls.Primitives.Thumb)
                    return;

                double x     = e.GetPosition(slider).X;
                double ratio = Math.Clamp(x / slider.ActualWidth, 0.0, 1.0);
                slider.Value = min + ratio * (max - min);
                slider.CaptureMouse();
                e.Handled = true;
            };

            // Пока мышь зажата и двигается после PreviewMouseLeftButtonDown — тянем
            slider.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !slider.IsMouseCaptured) return;
                double x     = e.GetPosition(slider).X;
                double ratio = Math.Clamp(x / slider.ActualWidth, 0.0, 1.0);
                slider.Value = min + ratio * (max - min);
            };

            slider.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
            };

            slider.ValueChanged += (s, e) =>
            {
                valLbl.Text = formatFn(slider.Value);
                onChange(slider.Value);
                UpdateMixInfoLabel();
            };

            // Колёсико мыши → шаг 1%
            slider.PreviewMouseWheel += (s, e) =>
            {
                double step = (max - min) * 0.01;
                slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? step : -step), min, max);
                e.Handled = true;
            };

            // ПКМ → сброс к дефолту
            slider.MouseRightButtonDown += (s, e) =>
            {
                slider.Value = label == "ШИРИНА" ? 1.0 : 0.0;
                e.Handled = true;
            };

            parent.Children.Add(row);
        }

        private void UpdateMixChipStyle(Border chip, TextBlock txt, bool active)
        {
            if (active)
            {
                chip.Background = new LinearGradientBrush(
                    Color.FromArgb(0x60, _cyan.R, _cyan.G, _cyan.B),
                    Color.FromArgb(0x60, _accent1.R, _accent1.G, _accent1.B), 0);
                chip.BorderBrush     = new SolidColorBrush(Color.FromArgb(0xCC, _cyan.R, _cyan.G, _cyan.B));
                chip.BorderThickness = new Thickness(1);
                chip.Effect          = new DropShadowEffect
                    { Color = _cyan, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.7 };
                txt.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                chip.Background      = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
                chip.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                chip.BorderThickness = new Thickness(1);
                chip.Effect          = null;
                txt.Foreground       = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            }
        }

        private void RefreshMixerChips()
        {
            if (MixerContainer.Children.Count == 0) return;
            if (MixerContainer.Children[0] is WrapPanel row)
                foreach (Border chip in row.Children)
                    if (chip.Child is TextBlock txt && chip.Tag is MixMode m)
                        UpdateMixChipStyle(chip, txt, m == _mixMode);
        }

        private void UpdateMixInfoLabel()
        {
            foreach (var child in MixerContainer.Children)
                if (child is TextBlock tb && tb.Name == "MixInfoLabel")
                { tb.Text = BuildMixInfoText(); break; }
        }

        private string BuildMixInfoText()
        {
            int inCh = _channelMixer?.InputChannels ?? 0;
            string inLabel = inCh switch
            {
                0 => "—", 1 => "Mono", 2 => "Stereo",
                6 => "5.1", 8 => "7.1", _ => $"{inCh}ch"
            };
            string modeLabel = _mixMode switch
            {
                MixMode.Passthrough  => "без изменений",
                MixMode.ForceStereo  => "→ Stereo",
                MixMode.ForceMono    => "→ Mono",
                MixMode.WideStero    => $"Wide ×{_mixWidth:0.0}",
                MixMode.NarrowStereo => $"Narrow ×{_mixWidth:0.0}",
                _ => ""
            };
            return $"Источник: {inLabel}  ·  {modeLabel}  ·  LFE {(int)(_mixLfeWeight * 100)}%";
        }

        // ── Toggle панели ─────────────────────────────────────────────────────

        private double _heightBeforeMixOpen;

        private void MixerToggle_Click(object s, RoutedEventArgs e)
        {
            _mixPanelOpen = !_mixPanelOpen;
            MixerContainer.Visibility = _mixPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            if (MixerToggleText != null)
                MixerToggleText.Text = _mixPanelOpen ? "MIX ▲" : "MIX ▼";

            if (_mixPanelOpen)
            {
                // Запоминаем высоту ДО открытия панели
                _heightBeforeMixOpen = ActualHeight;

                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    LayoutUpdated -= handler;
                    double h = MixerContainer.ActualHeight;
                    if (h > 1) Height = _heightBeforeMixOpen + h;
                };
                LayoutUpdated -= handler;
                LayoutUpdated += handler;
            }
            else
            {
                // Восстанавливаем высоту которая была до открытия микшера
                if (_heightBeforeMixOpen > 0)
                    Height = _heightBeforeMixOpen;
            }
        }
    }
}

