// MainWindow.EQ.cs — панель эквалайзера: построение, пресеты, drag-бары
#nullable enable

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ── Кэшированные замороженные ресурсы EQ ─────────────────────────────────
        private static readonly FontFamily EqSyneFont = new FontFamily("Syne");
        private static readonly FontFamily EqDmSansFont = new FontFamily("DM Sans");

        private static readonly SolidColorBrush EqChipNormalBg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush EqChipHoverBg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush EqChipNormalBorder = FreezeEq(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush EqChipInactiveFg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));

        private static readonly SolidColorBrush EqResetNormalBg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush EqResetHoverBg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush EqResetBorder = FreezeEq(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x60, 0x60)));
        private static readonly SolidColorBrush EqResetFg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x80, 0x80)));

        private static readonly SolidColorBrush EqDbScaleFg = FreezeEq(new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush EqTrackFill = FreezeEq(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush EqZeroLineFill = FreezeEq(new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)));

        private static T FreezeEq<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze)
                freezable.Freeze();
            return freezable;
        }

        private readonly List<Action> _eqCleanupActions = new();
        private Brush? _eqChipActiveBg;
        private Brush? _eqChipActiveBorder;
        private Effect? _eqChipActiveEffect;
        private EventHandler? _eqLayoutHandler;
        private double _addedEqHeight;

        private void BuildEqPanel()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(BuildEqPanel));
                return;
            }
            if (EqContainer == null) return;

            ClearEqPanel();

            // Сбрасываем кэш активного чипа на случай смены цветовой темы
            _eqChipActiveBg = null;
            _eqChipActiveBorder = null;
            _eqChipActiveEffect = null;

            // ── Preset chips ──────────────────────────────────────────────────────
            var presetRow = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 12)
            };

            for (int p = 0; p < EqPresets.Length; p++)
            {
                int pi = p;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(4, 3, 4, 3),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = p
                };
                var chipText = new TextBlock
                {
                    Text = EqPresets[p].Name,
                    FontFamily = EqSyneFont,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                chip.Child = chipText;
                UpdateChipStyle(chip, chipText, p == _currentPreset);

                MouseButtonEventHandler onDown = (_, e) => { ApplyPreset(pi); e.Handled = true; };
                MouseEventHandler onEnter = (_, _) => { if (chip.Tag is int idx && idx != _currentPreset) chip.Background = EqChipHoverBg; };
                MouseEventHandler onLeave = (_, _) => { if (chip.Tag is int idx && idx != _currentPreset) chip.Background = EqChipNormalBg; };

                chip.MouseLeftButtonDown += onDown;
                chip.MouseEnter += onEnter;
                chip.MouseLeave += onLeave;
                _eqCleanupActions.Add(() =>
                {
                    chip.MouseLeftButtonDown -= onDown;
                    chip.MouseEnter -= onEnter;
                    chip.MouseLeave -= onLeave;
                });

                presetRow.Children.Add(chip);
            }

            // Reset chip
            var resetChip = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(4, 3, 4, 3),
                Cursor = Cursors.Hand,
                Background = EqResetNormalBg,
                BorderBrush = EqResetBorder,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "RESET",
                    FontFamily = EqSyneFont,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = EqResetFg,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            MouseButtonEventHandler onResetDown = (_, e) => { ResetEqSettings(); e.Handled = true; };
            MouseEventHandler onResetEnter = (_, _) => resetChip.Background = EqResetHoverBg;
            MouseEventHandler onResetLeave = (_, _) => resetChip.Background = EqResetNormalBg;

            resetChip.MouseLeftButtonDown += onResetDown;
            resetChip.MouseEnter += onResetEnter;
            resetChip.MouseLeave += onResetLeave;
            _eqCleanupActions.Add(() =>
            {
                resetChip.MouseLeftButtonDown -= onResetDown;
                resetChip.MouseEnter -= onResetEnter;
                resetChip.MouseLeave -= onResetLeave;
            });

            presetRow.Children.Add(resetChip);
            EqContainer.Children.Add(presetRow);

            // ── dB scale + bars ───────────────────────────────────────────────────
            var mainGrid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition());

            var dbScale = new Canvas { Width = 22, Height = 110 };
            foreach (var (db, pct) in new[] { (12, 0.0), (6, 0.21), (0, 0.44), (-6, 0.67), (-12, 0.88) })
            {
                var lbl = new TextBlock
                {
                    Text = db > 0 ? $"+{db}" : $"{db}",
                    FontSize = 8,
                    FontFamily = EqDmSansFont,
                    Foreground = EqDbScaleFg
                };
                Canvas.SetTop(lbl, pct * 100);
                Canvas.SetRight(lbl, 2);
                dbScale.Children.Add(lbl);
            }
            Grid.SetColumn(dbScale, 0);
            mainGrid.Children.Add(dbScale);

            var barsGrid = new Grid();
            for (int i = 0; i < 5; i++) barsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            barsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
            barsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

            for (int b = 0; b < 5; b++)
            {
                int band = b;
                var color = GetEqBarColor(b);

                var canvas = new Canvas
                {
                    Width = 36,
                    Height = 100,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = Cursors.SizeNS,
                    ClipToBounds = true,
                    Background = Brushes.Transparent
                };

                var track = new Rectangle
                {
                    Width = 6,
                    Height = 100,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = EqTrackFill
                };
                Canvas.SetLeft(track, 15);
                canvas.Children.Add(track);

                var fillBrush = FreezeEq(new LinearGradientBrush(
                    Color.FromArgb(0xFF, color.R, color.G, color.B),
                    Color.FromArgb(0x60, color.R, color.G, color.B),
                    new Point(0, 0), new Point(0, 1)));
                var fill = new Rectangle { Width = 6, RadiusX = 3, RadiusY = 3, Fill = fillBrush };
                Canvas.SetLeft(fill, 15);
                canvas.Children.Add(fill);

                var zeroline = new Rectangle { Width = 14, Height = 1, Fill = EqZeroLineFill };
                Canvas.SetLeft(zeroline, 11);
                Canvas.SetTop(zeroline, 50);
                canvas.Children.Add(zeroline);

                var normalEffect = FreezeEq(new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 });
                var hoverEffect = FreezeEq(new DropShadowEffect { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = 1.0 });

                var thumb = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, Effect = normalEffect };
                Canvas.SetLeft(thumb, 11);
                canvas.Children.Add(thumb);

                var valLabel = new TextBlock
                {
                    Width = canvas.Width,
                    FontSize = 9,
                    FontFamily = EqDmSansFont,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(valLabel, 0);
                canvas.Children.Add(valLabel);

                _eqBars[b] = canvas;
                _eqLabels[b] = valLabel;
                _eqDragging[b] = false;
                RedrawEqBar(band, _eqValues[band], canvas, fill, thumb, valLabel);

                MouseButtonEventHandler onBarDown = (_, e) =>
                {
                    _currentPreset = -1;
                    UpdateAllPresetChips();
                    _eqDragging[band] = true;
                    canvas.CaptureMouse();

                    double v = YToDb(e.GetPosition(canvas).Y, canvas.Height);
                    _eqValues[band] = v;
                    var eq = _equalizer;
                    eq?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel);
                    e.Handled = true;
                };

                MouseEventHandler onBarMove = (_, e) =>
                {
                    if (!_eqDragging[band] || e.LeftButton != MouseButtonState.Pressed || !canvas.IsMouseCaptured)
                        return;

                    double v = YToDb(e.GetPosition(canvas).Y, canvas.Height);
                    _eqValues[band] = v;
                    var eq = _equalizer;
                    eq?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel);
                };

                MouseButtonEventHandler onBarUp = (_, _) =>
                {
                    _eqDragging[band] = false;
                    if (canvas.IsMouseCaptured)
                        canvas.ReleaseMouseCapture();
                };

                MouseEventHandler onBarLostCapture = (_, _) =>
                {
                    _eqDragging[band] = false;
                };

                MouseWheelEventHandler onBarWheel = (_, e) =>
                {
                    double v = Math.Clamp(_eqValues[band] + (e.Delta > 0 ? 1.0 : -1.0), -12.0, 12.0);
                    _eqValues[band] = v;
                    var eq = _equalizer;
                    eq?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel);
                    _currentPreset = -1;
                    UpdateAllPresetChips();
                    e.Handled = true;
                };

                MouseButtonEventHandler onBarRight = (_, e) =>
                {
                    _eqValues[band] = 0;
                    var eq = _equalizer;
                    eq?.SetGain(band, 0f);
                    RedrawEqBar(band, 0, canvas, fill, thumb, valLabel);
                    _currentPreset = -1;
                    UpdateAllPresetChips();
                    e.Handled = true;
                };

                MouseEventHandler onBarEnter = (_, _) => thumb.Effect = hoverEffect;
                MouseEventHandler onBarLeave = (_, _) => thumb.Effect = normalEffect;

                canvas.MouseLeftButtonDown += onBarDown;
                canvas.MouseMove += onBarMove;
                canvas.MouseLeftButtonUp += onBarUp;
                canvas.LostMouseCapture += onBarLostCapture;
                canvas.MouseWheel += onBarWheel;
                canvas.MouseRightButtonDown += onBarRight;
                canvas.MouseEnter += onBarEnter;
                canvas.MouseLeave += onBarLeave;

                _eqCleanupActions.Add(() =>
                {
                    canvas.MouseLeftButtonDown -= onBarDown;
                    canvas.MouseMove -= onBarMove;
                    canvas.MouseLeftButtonUp -= onBarUp;
                    canvas.LostMouseCapture -= onBarLostCapture;
                    canvas.MouseWheel -= onBarWheel;
                    canvas.MouseRightButtonDown -= onBarRight;
                    canvas.MouseEnter -= onBarEnter;
                    canvas.MouseLeave -= onBarLeave;
                });

                var barCol = new Grid();
                Grid.SetColumn(barCol, b);
                Grid.SetRow(barCol, 0);
                barCol.Children.Add(canvas);
                barsGrid.Children.Add(barCol);

                var freqBrush = FreezeEq(new SolidColorBrush(Color.FromArgb(0x70, color.R, color.G, color.B)));
                var freqLabel = new TextBlock
                {
                    Text = EqFreqLabels[b],
                    FontSize = 9,
                    FontFamily = EqSyneFont,
                    FontWeight = FontWeights.Bold,
                    Foreground = freqBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var freqCell = new Grid();
                freqCell.Children.Add(freqLabel);
                Grid.SetColumn(freqCell, b);
                Grid.SetRow(freqCell, 1);
                barsGrid.Children.Add(freqCell);
            }

            Grid.SetColumn(barsGrid, 1);
            mainGrid.Children.Add(barsGrid);
            EqContainer.Children.Add(mainGrid);
        }

        private void ClearEqPanel()
        {
            foreach (var cleanup in _eqCleanupActions)
                cleanup();
            _eqCleanupActions.Clear();

            Array.Clear(_eqDragging, 0, _eqDragging.Length);
            EqContainer.Children.Clear();
        }

        private void ResetEqSettings()
        {
            _currentPreset = -1;
            var eq = _equalizer;
            for (int b = 0; b < 5; b++)
            {
                _eqValues[b] = 0;
                eq?.SetGain(b, 0f);
                RedrawEqBarByIndex(b, 0);
            }
            UpdateAllPresetChips();
        }

        private static double YToDb(double y, double height)
        {
            if (height <= 0 || double.IsNaN(height) || double.IsInfinity(height))
                return 0.0;
            return Math.Clamp(12.0 - (y / height) * 24.0, -12.0, 12.0);
        }

        private static void RedrawEqBar(int band, double dB,
            Canvas canvas, Rectangle fill, Ellipse thumb, TextBlock valLabel)
        {
            double h = canvas.Height > 0 ? canvas.Height : 100.0;
            double zero = h / 2.0;
            double pixels = (Math.Clamp(dB, -12.0, 12.0) / 12.0) * (h / 2.0);

            double thumbY = zero - pixels - 7.0;
            double fillTop, fillH;
            if (dB >= 0) { fillTop = zero - pixels; fillH = pixels; }
            else { fillTop = zero; fillH = -pixels; }

            Canvas.SetTop(fill, fillTop);
            fill.Height = Math.Max(0, fillH);

            double clampedThumbTop = Math.Clamp(thumbY, 0, h - 14.0);
            Canvas.SetTop(thumb, clampedThumbTop);

            int rounded = (int)Math.Round(dB, MidpointRounding.AwayFromZero);
            valLabel.Text = rounded > 0 ? $"+{rounded}" : rounded.ToString();

            double labelTop = clampedThumbTop >= 14.0
                ? clampedThumbTop - 13.0
                : Math.Clamp(clampedThumbTop + 15.0, 0, h - 12.0);
            Canvas.SetTop(valLabel, labelTop);
        }

        private void RedrawEqBarByIndex(int band, double dB)
        {
            var canvas = _eqBars[band];
            if (canvas != null && canvas.Children.Count >= 5 &&
                canvas.Children[1] is Rectangle fill &&
                canvas.Children[3] is Ellipse thumb &&
                canvas.Children[4] is TextBlock valLabel)
            {
                RedrawEqBar(band, dB, canvas, fill, thumb, valLabel);
            }
        }

        // ─── Пресеты ─────────────────────────────────────────────────────────────

        private void ApplyPreset(int presetIndex)
        {
            if (presetIndex < 0 || presetIndex >= EqPresets.Length)
                return;

            _currentPreset = presetIndex;
            UpdateAllPresetChips();

            var gains = EqPresets[presetIndex].Gains;
            var eq = _equalizer;
            for (int b = 0; b < 5; b++)
            {
                _eqValues[b] = gains[b];
                eq?.SetGain(b, gains[b]);
                RedrawEqBarByIndex(b, gains[b]);
            }
        }

        private void EnsureActiveEqChipResources()
        {
            _eqChipActiveBg ??= FreezeEq(new LinearGradientBrush(
                Color.FromArgb(0x60, _accent1.R, _accent1.G, _accent1.B),
                Color.FromArgb(0x60, _accent2.R, _accent2.G, _accent2.B), 0));

            _eqChipActiveBorder ??= FreezeEq(new SolidColorBrush(
                Color.FromArgb(0xCC, _accent1.R, _accent1.G, _accent1.B)));

            _eqChipActiveEffect ??= FreezeEq(new DropShadowEffect
            {
                Color = _accent1,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.7
            });
        }

        private void UpdateChipStyle(Border chip, TextBlock text, bool active)
        {
            if (active)
            {
                EnsureActiveEqChipResources();
                chip.Background = _eqChipActiveBg;
                chip.BorderBrush = _eqChipActiveBorder;
                chip.Effect = _eqChipActiveEffect;
                text.Foreground = Brushes.White;
            }
            else
            {
                chip.Background = EqChipNormalBg;
                chip.BorderBrush = EqChipNormalBorder;
                chip.Effect = null;
                text.Foreground = EqChipInactiveFg;
            }
        }

        private void UpdateAllPresetChips()
        {
            if (EqContainer == null || EqContainer.Children.Count == 0)
                return;

            if (EqContainer.Children[0] is WrapPanel row)
            {
                foreach (var child in row.Children)
                {
                    if (child is Border chip && chip.Child is TextBlock txt && chip.Tag is int idx)
                        UpdateChipStyle(chip, txt, idx == _currentPreset);
                }
            }
        }

        // ─── Toggle EQ panel ─────────────────────────────────────────────────────

        private void DetachEqLayoutHandler()
        {
            if (_eqLayoutHandler != null)
            {
                LayoutUpdated -= _eqLayoutHandler;
                _eqLayoutHandler = null;
            }
        }

        private void EqToggle_Click(object s, RoutedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => EqToggle_Click(s, e)));
                return;
            }
            if (EqContainer == null) return;

            _eqPanelOpen = !_eqPanelOpen;
            if (_eqPanelOpen && EqContainer.Children.Count == 0)
                BuildEqPanel();

            EqContainer.Visibility = _eqPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            if (EqToggleText != null)
                EqToggleText.Text = _eqPanelOpen ? "EQ ▲" : "EQ ▼";

            DetachEqLayoutHandler();

            if (_eqPanelOpen)
            {
                if (WindowState == WindowState.Normal)
                {
                    int attempts = 0;
                    _eqLayoutHandler = (_, _) =>
                    {
                        if (!_eqPanelOpen || EqContainer == null || EqContainer.Visibility != Visibility.Visible)
                        {
                            DetachEqLayoutHandler();
                            return;
                        }

                        double eqH = EqContainer.ActualHeight;
                        if (eqH <= 1.0)
                        {
                            if (++attempts >= 5) DetachEqLayoutHandler();
                            return;
                        }

                        DetachEqLayoutHandler();
                        if (WindowState == WindowState.Normal && _addedEqHeight <= 0.0)
                        {
                            _addedEqHeight = eqH;
                            Height += eqH;
                        }
                    };
                    LayoutUpdated += _eqLayoutHandler;
                }
            }
            else
            {
                if (_addedEqHeight > 0.0 && WindowState == WindowState.Normal)
                {
                    double targetHeight = Height - _addedEqHeight;
                    if (targetHeight >= MinHeight && targetHeight > 0.0)
                        Height = targetHeight;
                }
                _addedEqHeight = 0.0;
            }
        }

        private void EqPanel_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Border || e.OriginalSource is StackPanel || e.OriginalSource is Grid)
                e.Handled = true;
        }

        private void EqPanel_MouseMove(object s, MouseEventArgs e) { }

        private void EqPanel_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Border || e.OriginalSource is StackPanel || e.OriginalSource is Grid)
                e.Handled = true;
        }
    }
}

#nullable restore