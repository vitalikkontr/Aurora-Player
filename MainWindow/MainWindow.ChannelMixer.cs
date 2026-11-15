// MainWindow.ChannelMixer.cs — панель микширования каналов
#nullable enable

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ── Кэшированные замороженные ресурсы WPF ──────────────────────────────
        private static readonly FontFamily SyneFont = new FontFamily("Syne");
        private static readonly FontFamily DmSansFont = new FontFamily("DM Sans");

        private static readonly SolidColorBrush ChipNormalBg = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush ChipHoverBg = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush ChipNormalBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush ChipInactiveFg = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));

        private static readonly SolidColorBrush ResetNormalBg = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush ResetHoverBg = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush ResetBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush ResetFg = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x80, 0x80)));
        private static readonly SolidColorBrush InfoLabelFg = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze) freezable.Freeze();
            return freezable;
        }

        // ── Состояние микшера ──────────────────────────────────────────────────
        private readonly object _mixerSyncLock = new object();
        private readonly List<(Border Chip, TextBlock Label, MixMode Mode)> _mixChips = new();
        private readonly List<Action> _mixerCleanupActions = new();

        private ChannelMixerProvider? _channelMixer;
        private MixMode _mixMode = MixMode.Passthrough;
        private float _mixWidth = 1.0f;
        private float _mixLfeWeight = 0.0f;
        private bool _mixPanelOpen = false;

        private Slider? _widthSlider;
        private Slider? _lfeSlider;
        private TextBlock? _mixInfoLabel;
        private bool _suppressMixerSliderCallbacks;

        private Brush? _chipActiveBg;
        private Brush? _chipActiveBorder;
        private Effect? _chipActiveEffect;

        private EventHandler? _mixLayoutHandler;
        private double _addedMixHeight;

        private static readonly (MixMode Mode, string Label, string Hint)[] MixModes =
        {
            (MixMode.Passthrough,  "AUTO",   "Оригинал без изменений"),
            (MixMode.ForceStereo,  "STEREO", "Принудительно в стерео"),
            (MixMode.ForceMono,    "MONO",   "Суммирование в моно"),
            (MixMode.WideStero,    "WIDE",   "Расширение стерео-базы"),
            (MixMode.NarrowStereo, "NARROW", "Сужение (наушники)"),
        };

        // ── Построение панели ─────────────────────────────────────────────────

        internal void BuildMixerPanel()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(BuildMixerPanel));
                return;
            }
            if (MixerContainer == null) return;

            ClearMixerPanel();

            var chipRow = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 10)
            };

            foreach (var (mode, label, hint) in MixModes)
            {
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(4, 3, 4, 3),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hint,
                    Tag = mode,
                };
                var txt = new TextBlock
                {
                    Text = label,
                    FontFamily = SyneFont,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                chip.Child = txt;
                UpdateMixChipStyle(chip, txt, mode == _mixMode);

                MouseButtonEventHandler onDown = (_, e) =>
                {
                    _mixMode = mode;
                    lock (_mixerSyncLock)
                    {
                        if (_channelMixer != null) _channelMixer.Mode = mode;
                    }
                    RefreshMixerChips();
                    UpdateMixInfoLabel();
                    UpdateChannelInfo();
                    e.Handled = true;
                };
                MouseEventHandler onEnter = (_, _) =>
                {
                    if (chip.Tag is MixMode m && m != _mixMode) chip.Background = ChipHoverBg;
                };
                MouseEventHandler onLeave = (_, _) =>
                {
                    if (chip.Tag is MixMode m && m != _mixMode) chip.Background = ChipNormalBg;
                };

                chip.MouseLeftButtonDown += onDown;
                chip.MouseEnter += onEnter;
                chip.MouseLeave += onLeave;
                _mixerCleanupActions.Add(() =>
                {
                    chip.MouseLeftButtonDown -= onDown;
                    chip.MouseEnter -= onEnter;
                    chip.MouseLeave -= onLeave;
                });

                _mixChips.Add((chip, txt, mode));
                chipRow.Children.Add(chip);
            }

            // ── Кнопка RESET ──────────────────────────────────────────────────
            var resetChip = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(4, 3, 4, 3),
                Cursor = Cursors.Hand,
                ToolTip = "Сбросить все настройки микшера",
                Background = ResetNormalBg,
                BorderBrush = ResetBorder,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "RESET",
                    FontFamily = SyneFont,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = ResetFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            };

            MouseEventHandler onResetEnter = (_, _) => resetChip.Background = ResetHoverBg;
            MouseEventHandler onResetLeave = (_, _) => resetChip.Background = ResetNormalBg;
            MouseButtonEventHandler onResetDown = (_, e) => { ResetMixerSettings(); e.Handled = true; };

            resetChip.MouseEnter += onResetEnter;
            resetChip.MouseLeave += onResetLeave;
            resetChip.MouseLeftButtonDown += onResetDown;
            _mixerCleanupActions.Add(() =>
            {
                resetChip.MouseEnter -= onResetEnter;
                resetChip.MouseLeave -= onResetLeave;
                resetChip.MouseLeftButtonDown -= onResetDown;
            });

            chipRow.Children.Add(resetChip);
            MixerContainer.Children.Add(chipRow);

            // ── Слайдеры ──────────────────────────────────────────────────────
            _widthSlider = AddMixerSlider(
                MixerContainer, "ШИРИНА", 0.0, 2.0, _mixWidth, 1.0,
                v => $"{(int)Math.Round(v * 100.0)}%", _cyan,
                v =>
                {
                    _mixWidth = (float)v;
                    lock (_mixerSyncLock) { if (_channelMixer != null) _channelMixer.Width = _mixWidth; }
                });

            _lfeSlider = AddMixerSlider(
                MixerContainer, "LFE / SUB", 0.0, 1.0, _mixLfeWeight, 0.0,
                v => $"{(int)Math.Round(v * 100.0)}%", _accent2,
                v =>
                {
                    _mixLfeWeight = (float)v;
                    lock (_mixerSyncLock) { if (_channelMixer != null) _channelMixer.LfeWeight = _mixLfeWeight; }
                });

            // ── Инфо-строка ───────────────────────────────────────────────────
            _mixInfoLabel = new TextBlock
            {
                Text = BuildMixInfoText(),
                FontFamily = DmSansFont,
                FontSize = 9,
                Foreground = InfoLabelFg,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 10, 4),
                Name = "MixInfoLabel",
            };
            MixerContainer.Children.Add(_mixInfoLabel);
        }

        private void ClearMixerPanel()
        {
            foreach (var cleanup in _mixerCleanupActions)
                cleanup();

            _mixerCleanupActions.Clear();
            _mixChips.Clear();
            _widthSlider = null;
            _lfeSlider = null;
            _mixInfoLabel = null;
            MixerContainer.Children.Clear();
        }

        private void ResetMixerSettings()
        {
            _mixMode = MixMode.Passthrough;
            _mixWidth = 1.0f;
            _mixLfeWeight = 0.0f;

            lock (_mixerSyncLock)
            {
                if (_channelMixer != null)
                {
                    _channelMixer.Mode = _mixMode;
                    _channelMixer.Width = _mixWidth;
                    _channelMixer.LfeWeight = _mixLfeWeight;
                }
            }

            _suppressMixerSliderCallbacks = true;
            try
            {
                if (_widthSlider != null) _widthSlider.Value = _mixWidth;
                if (_lfeSlider != null) _lfeSlider.Value = _mixLfeWeight;
            }
            finally
            {
                _suppressMixerSliderCallbacks = false;
            }

            RefreshMixerChips();
            UpdateMixInfoLabel();
            UpdateChannelInfo();
        }

        // ── Хелперы построения ─────────────────────────────────────────────────

        private Slider AddMixerSlider(
            Panel parent, string label, double min, double max, double value, double defaultValue,
            Func<double, string> formatFn, Color accentColor, Action<double> onChange)
        {
            var row = new Grid { Margin = new Thickness(8, 0, 8, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var lbl = new TextBlock
            {
                Text = label,
                FontFamily = SyneFont,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Freeze(new SolidColorBrush(Color.FromArgb(0x70, accentColor.R, accentColor.G, accentColor.B))),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                IsMoveToPointEnabled = false,
            };
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);

            var valLbl = new TextBlock
            {
                Text = formatFn(slider.Value),
                FontFamily = DmSansFont,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 34,
            };
            Grid.SetColumn(valLbl, 2);
            row.Children.Add(valLbl);

            MouseButtonEventHandler onDown = (_, e) =>
            {
                if (IsOriginFromThumb(e.OriginalSource as DependencyObject, slider)) return;
                slider.Focus();
                SetSliderValueFromMouse(slider, e, min, max);
                slider.CaptureMouse();
                e.Handled = true;
            };
            MouseEventHandler onMove = (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !slider.IsMouseCaptured) return;
                SetSliderValueFromMouse(slider, e, min, max);
            };
            MouseButtonEventHandler onUp = (_, _) =>
            {
                if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
            };
            RoutedPropertyChangedEventHandler<double> onChanged = (_, _) =>
            {
                valLbl.Text = formatFn(slider.Value);
                if (_suppressMixerSliderCallbacks) return;
                onChange(slider.Value);
                UpdateMixInfoLabel();
            };
            MouseWheelEventHandler onWheel = (_, e) =>
            {
                double step = (max - min) * 0.01;
                slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? step : -step), min, max);
                e.Handled = true;
            };
            MouseButtonEventHandler onRight = (_, e) =>
            {
                slider.Value = Math.Clamp(defaultValue, min, max);
                e.Handled = true;
            };

            slider.PreviewMouseLeftButtonDown += onDown;
            slider.PreviewMouseMove += onMove;
            slider.PreviewMouseLeftButtonUp += onUp;
            slider.ValueChanged += onChanged;
            slider.PreviewMouseWheel += onWheel;
            slider.MouseRightButtonDown += onRight;

            _mixerCleanupActions.Add(() =>
            {
                slider.PreviewMouseLeftButtonDown -= onDown;
                slider.PreviewMouseMove -= onMove;
                slider.PreviewMouseLeftButtonUp -= onUp;
                slider.ValueChanged -= onChanged;
                slider.PreviewMouseWheel -= onWheel;
                slider.MouseRightButtonDown -= onRight;
            });

            parent.Children.Add(row);
            return slider;
        }

        private static bool IsOriginFromThumb(DependencyObject? origin, DependencyObject root)
        {
            DependencyObject? current = origin;
            while (current != null && !ReferenceEquals(current, root))
            {
                if (current is Thumb) return true;
                current = current is Visual
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private static void SetSliderValueFromMouse(Slider slider, MouseEventArgs e, double min, double max)
        {
            double newValue;
            if (slider.Template?.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0.0)
            {
                newValue = track.ValueFromPoint(e.GetPosition(track));
            }
            else
            {
                if (slider.ActualWidth <= 0.0) return;
                double ratio = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0.0, 1.0);
                newValue = min + ratio * (max - min);
            }

            if (double.IsNaN(newValue) || double.IsInfinity(newValue)) return;
            slider.Value = Math.Clamp(newValue, min, max);
        }

        private void EnsureActiveChipResources()
        {
            _chipActiveBg ??= Freeze(new LinearGradientBrush(
                Color.FromArgb(0x60, _cyan.R, _cyan.G, _cyan.B),
                Color.FromArgb(0x60, _accent1.R, _accent1.G, _accent1.B), 0));

            _chipActiveBorder ??= Freeze(new SolidColorBrush(
                Color.FromArgb(0xCC, _cyan.R, _cyan.G, _cyan.B)));

            _chipActiveEffect ??= Freeze(new DropShadowEffect
            {
                Color = _cyan,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.7
            });
        }

        private void UpdateMixChipStyle(Border chip, TextBlock txt, bool active)
        {
            if (active)
            {
                EnsureActiveChipResources();
                chip.Background = _chipActiveBg;
                chip.BorderBrush = _chipActiveBorder;
                chip.Effect = _chipActiveEffect;
                txt.Foreground = Brushes.White;
            }
            else
            {
                chip.Background = ChipNormalBg;
                chip.BorderBrush = ChipNormalBorder;
                chip.Effect = null;
                txt.Foreground = ChipInactiveFg;
            }
        }

        private void RefreshMixerChips()
        {
            foreach (var (chip, txt, mode) in _mixChips)
                UpdateMixChipStyle(chip, txt, mode == _mixMode);
        }

        private void UpdateMixInfoLabel()
        {
            if (_mixInfoLabel != null)
            {
                _mixInfoLabel.Text = BuildMixInfoText();
                return;
            }

            if (MixerContainer == null) return;
            foreach (var child in MixerContainer.Children)
                if (child is TextBlock tb && tb.Name == "MixInfoLabel")
                { tb.Text = BuildMixInfoText(); break; }
        }

        private string BuildMixInfoText()
        {
            int inCh = _channelMixer?.InputChannels ?? 0;
            string inLabel = inCh switch
            {
                0 => "—",
                1 => "Mono",
                2 => "Stereo",
                6 => "5.1",
                8 => "7.1",
                _ => $"{inCh}ch"
            };
            string modeLabel = _mixMode switch
            {
                MixMode.Passthrough => "без изменений",
                MixMode.ForceStereo => "→ Stereo",
                MixMode.ForceMono => "→ Mono",
                MixMode.WideStero => $"Wide ×{_mixWidth:0.0}",
                MixMode.NarrowStereo => $"Narrow ×{_mixWidth:0.0}",
                _ => ""
            };
            return $"Источник: {inLabel}  ·  {modeLabel}  ·  LFE {(int)Math.Round(_mixLfeWeight * 100.0)}%";
        }

        // ── Toggle панели ─────────────────────────────────────────────────────

        private void DetachMixLayoutHandler()
        {
            if (_mixLayoutHandler != null)
            {
                LayoutUpdated -= _mixLayoutHandler;
                _mixLayoutHandler = null;
            }
        }

        private void MixerToggle_Click(object s, RoutedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => MixerToggle_Click(s, e)));
                return;
            }
            if (MixerContainer == null) return;

            _mixPanelOpen = !_mixPanelOpen;
            if (_mixPanelOpen && MixerContainer.Children.Count == 0)
                BuildMixerPanel();

            MixerContainer.Visibility = _mixPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            if (MixerToggleText != null)
                MixerToggleText.Text = _mixPanelOpen ? "MIX ▲" : "MIX ▼";

            DetachMixLayoutHandler();

            if (_mixPanelOpen)
            {
                if (WindowState == WindowState.Normal)
                {
                    int attempts = 0;
                    _mixLayoutHandler = (_, _) =>
                    {
                        if (!_mixPanelOpen || MixerContainer == null || MixerContainer.Visibility != Visibility.Visible)
                        {
                            DetachMixLayoutHandler();
                            return;
                        }

                        double h = MixerContainer.ActualHeight;
                        if (h <= 1.0)
                        {
                            if (++attempts >= 5) DetachMixLayoutHandler();
                            return;
                        }

                        DetachMixLayoutHandler();
                        if (WindowState == WindowState.Normal && _addedMixHeight <= 0.0)
                        {
                            _addedMixHeight = h;
                            Height += h;
                        }
                    };
                    LayoutUpdated += _mixLayoutHandler;
                }
            }
            else
            {
                if (_addedMixHeight > 0.0 && WindowState == WindowState.Normal)
                {
                    double targetHeight = Height - _addedMixHeight;
                    if (targetHeight >= MinHeight && targetHeight > 0.0)
                        Height = targetHeight;
                }
                _addedMixHeight = 0.0;
            }
        }
    }
}

#nullable restore