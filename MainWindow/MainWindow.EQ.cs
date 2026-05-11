// MainWindow.EQ.cs — панель эквалайзера: построение, пресеты, drag-бары
using System;
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
        private void BuildEqPanel()
        {
            EqContainer.Children.Clear();

            // ── Preset chips ──────────────────────────────────────────────────────
            var presetRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,6,0,12) };
            for (int p = 0; p < EqPresets.Length; p++)
            {
                int pi       = p;
                var chip     = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14,5,14,5), Margin = new Thickness(4,3,4,3), Cursor = Cursors.Hand, Tag = p };
                var chipText = new TextBlock { Text = EqPresets[p].Name, FontFamily = new FontFamily("Syne"), FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
                chip.Child = chipText;
                UpdateChipStyle(chip, chipText, p == _currentPreset);
                chip.MouseLeftButtonDown += (s, e) => { ApplyPreset(pi); e.Handled = true; };
                chip.MouseEnter += (s, e) => { if ((int)chip.Tag != _currentPreset) chip.Background = new SolidColorBrush(Color.FromArgb(0x28,0xFF,0xFF,0xFF)); };
                chip.MouseLeave += (s, e) => { if ((int)chip.Tag != _currentPreset) chip.Background = new SolidColorBrush(Color.FromArgb(0x12,0xFF,0xFF,0xFF)); };
                presetRow.Children.Add(chip);
            }

            // Reset chip
            var resetChip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14,5,14,5), Margin = new Thickness(4,3,4,3), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(0x12,0xFF,0x60,0x60)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30,0xFF,0x60,0x60)), BorderThickness = new Thickness(1) };
            var resetText = new TextBlock { Text = "RESET", FontFamily = new FontFamily("Syne"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x90,0xFF,0x80,0x80)), HorizontalAlignment = HorizontalAlignment.Center };
            resetChip.Child = resetText;
            resetChip.MouseLeftButtonDown += (s, e) => { _currentPreset = -1; for (int b = 0; b < 5; b++) { _eqValues[b] = 0; _equalizer?.SetGain(b, 0f); } BuildEqPanel(); e.Handled = true; };
            resetChip.MouseEnter += (s, e) => resetChip.Background = new SolidColorBrush(Color.FromArgb(0x28,0xFF,0x60,0x60));
            resetChip.MouseLeave += (s, e) => resetChip.Background = new SolidColorBrush(Color.FromArgb(0x12,0xFF,0x60,0x60));
            presetRow.Children.Add(resetChip);
            EqContainer.Children.Add(presetRow);

            // ── dB scale + bars ───────────────────────────────────────────────────
            var mainGrid = new Grid { Margin = new Thickness(4,0,4,0) };
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition());

            // dB scale canvas
            var dbScale = new Canvas { Width = 22, Height = 110 };
            foreach (var (db, pct) in new[] { (12, 0.0), (6, 0.21), (0, 0.44), (-6, 0.67), (-12, 0.88) })
            {
                var lbl = new TextBlock { Text = db > 0 ? $"+{db}" : $"{db}", FontSize = 8, FontFamily = new FontFamily("DM Sans"), Foreground = new SolidColorBrush(Color.FromArgb(0x45,0xFF,0xFF,0xFF)) };
                Canvas.SetTop(lbl, pct * 100);
                Canvas.SetRight(lbl, 2);
                dbScale.Children.Add(lbl);
            }
            Grid.SetColumn(dbScale, 0);
            mainGrid.Children.Add(dbScale);

            // Bars grid
            var barsGrid = new Grid();
            for (int i = 0; i < 5; i++) barsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            barsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
            barsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

            for (int b = 0; b < 5; b++)
            {
                int  band  = b;
                var  color = GetEqBarColor(b);

                var canvas = new Canvas { Width = 36, Height = 100, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.SizeNS, ClipToBounds = true };

                var track = new Rectangle { Width = 6, Height = 100, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.FromArgb(0x20,0xFF,0xFF,0xFF)) };
                Canvas.SetLeft(track, 15); canvas.Children.Add(track);

                var fill = new Rectangle { Width = 6, RadiusX = 3, RadiusY = 3 };
                fill.Fill = new LinearGradientBrush(Color.FromArgb(0xFF,color.R,color.G,color.B), Color.FromArgb(0x60,color.R,color.G,color.B), new Point(0,0), new Point(0,1));
                Canvas.SetLeft(fill, 15);
                canvas.Children.Add(fill);

                var zeroline = new Rectangle { Width = 14, Height = 1, Fill = new SolidColorBrush(Color.FromArgb(0x35,0xFF,0xFF,0xFF)) };
                Canvas.SetLeft(zeroline, 11); Canvas.SetTop(zeroline, 50);
                canvas.Children.Add(zeroline);

                var thumb = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Colors.White) };
                thumb.Effect = new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
                Canvas.SetLeft(thumb, 11);
                canvas.Children.Add(thumb);

                var valLabel = new TextBlock { FontSize = 9, FontFamily = new FontFamily("DM Sans"), FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center };
                Canvas.SetLeft(valLabel, 0);
                canvas.Children.Add(valLabel);

                _eqBars[b]   = canvas;
                _eqLabels[b] = valLabel;
                RedrawEqBar(band, _eqValues[band], canvas, fill, thumb, valLabel);

                bool drag = false;
                canvas.MouseLeftButtonDown += (s, e) =>
                {
                    _currentPreset = -1; UpdateAllPresetChips();
                    drag = true; canvas.CaptureMouse();
                    double v = YToDb(e.GetPosition(canvas).Y, canvas.Height);
                    _eqValues[band] = v; _equalizer?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel); e.Handled = true;
                };
                canvas.MouseMove += (s, e) =>
                {
                    if (!drag || e.LeftButton != MouseButtonState.Pressed) return;
                    double v = YToDb(e.GetPosition(canvas).Y, canvas.Height);
                    _eqValues[band] = v; _equalizer?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel);
                };
                canvas.MouseLeftButtonUp   += (s, e) => { drag = false; canvas.ReleaseMouseCapture(); };
                canvas.MouseWheel += (s, e) =>
                {
                    double v = Math.Clamp(_eqValues[band] + (e.Delta > 0 ? 1.0 : -1.0), -12, 12);
                    _eqValues[band] = v; _equalizer?.SetGain(band, (float)v);
                    RedrawEqBar(band, v, canvas, fill, thumb, valLabel);
                    _currentPreset = -1; UpdateAllPresetChips(); e.Handled = true;
                };
                canvas.MouseRightButtonDown += (s, e) =>
                {
                    _eqValues[band] = 0; _equalizer?.SetGain(band, 0f);
                    RedrawEqBar(band, 0, canvas, fill, thumb, valLabel);
                    _currentPreset = -1; UpdateAllPresetChips(); e.Handled = true;
                };
                canvas.MouseEnter += (s, e) => thumb.Effect = new DropShadowEffect { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = 1.0 };
                canvas.MouseLeave += (s, e) => thumb.Effect = new DropShadowEffect { Color = color, BlurRadius = 8,  ShadowDepth = 0, Opacity = 0.9 };

                var barCol = new Grid();
                Grid.SetColumn(barCol, b); Grid.SetRow(barCol, 0);
                barCol.Children.Add(canvas);
                barsGrid.Children.Add(barCol);

                var freqLabel = new TextBlock { Text = EqFreqLabels[b], FontSize = 9, FontFamily = new FontFamily("Syne"), FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x70,color.R,color.G,color.B)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var freqCell = new Grid();
                freqCell.Children.Add(freqLabel);
                Grid.SetColumn(freqCell, b); Grid.SetRow(freqCell, 1);
                barsGrid.Children.Add(freqCell);
            }

            Grid.SetColumn(barsGrid, 1);
            mainGrid.Children.Add(barsGrid);
            EqContainer.Children.Add(mainGrid);
        }

        private static double YToDb(double y, double height)
            => Math.Clamp(12.0 - (y / height) * 24.0, -12.0, 12.0);

        private static void RedrawEqBar(int band, double dB,
            Canvas canvas, Rectangle fill, Ellipse thumb, TextBlock valLabel)
        {
            double h      = canvas.Height; // 100px fixed
            double zero   = h / 2;
            double pixels = (dB / 12.0) * (h / 2);

            double thumbY = zero - pixels - 7;
            double fillTop, fillH;
            if (dB >= 0) { fillTop = zero - pixels; fillH =  pixels; }
            else         { fillTop = zero;           fillH = -pixels; }

            Canvas.SetTop(fill, fillTop);
            fill.Height = Math.Max(0, fillH);

            Canvas.SetTop(thumb, Math.Clamp(thumbY, 0, h - 14));

            string txt = dB >= 0.5 ? $"+{dB:F0}" : dB <= -0.5 ? $"{dB:F0}" : "0";
            valLabel.Text = txt;
            Canvas.SetTop(valLabel, Math.Clamp(thumbY - 14, 0, h - 14));
            Canvas.SetLeft(valLabel, (canvas.Width - valLabel.ActualWidth) / 2);
        }

        // ─── Пресеты ─────────────────────────────────────────────────────────────

        private void ApplyPreset(int presetIndex)
        {
            if (presetIndex < 0 || presetIndex >= EqPresets.Length) return;
            _currentPreset = presetIndex;
            UpdateAllPresetChips();
            var gains = EqPresets[presetIndex].Gains;
            for (int b = 0; b < 5; b++)
            {
                _eqValues[b] = gains[b];
                _equalizer?.SetGain(b, gains[b]);
                if (_eqBars[b] != null && _eqBars[b].Children.Count >= 5)
                {
                    var fill     = _eqBars[b].Children[1] as Rectangle;
                    var thumb    = _eqBars[b].Children[3] as Ellipse;
                    var valLabel = _eqBars[b].Children[4] as TextBlock;
                    if (fill != null && thumb != null && valLabel != null)
                        RedrawEqBar(b, gains[b], _eqBars[b], fill, thumb, valLabel);
                }
            }
        }

        private void UpdateChipStyle(Border chip, TextBlock text, bool active)
        {
            if (active)
            {
                chip.Background = new LinearGradientBrush(
                    Color.FromArgb(0x60,_accent1.R,_accent1.G,_accent1.B),
                    Color.FromArgb(0x60,_accent2.R,_accent2.G,_accent2.B), 0);
                chip.BorderBrush     = new SolidColorBrush(Color.FromArgb(0xCC,_accent1.R,_accent1.G,_accent1.B));
                chip.BorderThickness = new Thickness(1);
                chip.Effect          = new DropShadowEffect { Color = _accent1, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.7 };
                text.Foreground      = new SolidColorBrush(Colors.White);
            }
            else
            {
                chip.Background      = new SolidColorBrush(Color.FromArgb(0x12,0xFF,0xFF,0xFF));
                chip.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x22,0xFF,0xFF,0xFF));
                chip.BorderThickness = new Thickness(1);
                chip.Effect          = null;
                text.Foreground      = new SolidColorBrush(Color.FromArgb(0x80,0xFF,0xFF,0xFF));
            }
        }

        private void UpdateAllPresetChips()
        {
            if (EqContainer.Children.Count == 0) return;
            if (EqContainer.Children[0] is WrapPanel row)
                foreach (Border chip in row.Children)
                    if (chip.Child is TextBlock txt && chip.Tag is int idx)
                        UpdateChipStyle(chip, txt, idx == _currentPreset);
        }

        // ─── Toggle EQ panel ─────────────────────────────────────────────────────

        private double _heightBeforeEqOpen;

        private void EqToggle_Click(object s, RoutedEventArgs e)
        {
            _eqPanelOpen = !_eqPanelOpen;
            EqContainer.Visibility = _eqPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            if (EqToggleText != null) EqToggleText.Text = _eqPanelOpen ? "EQ ▲" : "EQ ▼";

            if (_eqPanelOpen)
            {
                // Запоминаем высоту ДО открытия панели
                _heightBeforeEqOpen = ActualHeight;

                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    LayoutUpdated -= handler;
                    double eqH = EqContainer.ActualHeight;
                    if (eqH > 1) Height = _heightBeforeEqOpen + eqH;
                };
                LayoutUpdated -= handler;
                LayoutUpdated += handler;
            }
            else
            {
                // Восстанавливаем высоту окна которая была до открытия EQ
                if (_heightBeforeEqOpen > 0)
                    Height = _heightBeforeEqOpen;
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
