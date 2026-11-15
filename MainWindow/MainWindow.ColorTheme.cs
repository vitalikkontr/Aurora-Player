// MainWindow.ColorTheme.cs — пользовательская цветовая тема Aurora Player
// Три акцентных цвета управляют всем оформлением плеера.
// Выбор через встроенный пикер, сохранение в settings.json.
#nullable enable

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ── Цвета по умолчанию ────────────────────────────────────────────────────
        public static readonly Color DefaultAccent1 = Color.FromRgb(0x7C, 0x6B, 0xFF); // фиолетовый
        public static readonly Color DefaultAccent2 = Color.FromRgb(0xFF, 0x6B, 0xB5); // розовый
        public static readonly Color DefaultCyan = Color.FromRgb(0x00, 0xE5, 0xCC); // бирюзовый

        // ── Текущие цвета (живые) ─────────────────────────────────────────────────
        private Color _accent1 = DefaultAccent1;
        private Color _accent2 = DefaultAccent2;
        private Color _cyan = DefaultCyan;

        // Окно выбора цветов
        private ColorPickerWindow? _colorPickerWindow;

        // ── Публичный доступ к цветам (используется из UI.cs, EQ.cs и др.) ───────
        public Color Accent1 => _accent1;
        public Color Accent2 => _accent2;
        public Color Cyan => _cyan;

        // ── Хелперы заморозки Freezable ───────────────────────────────────────────

        private static T FreezeTheme<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze)
                freezable.Freeze();
            return freezable;
        }

        private static SolidColorBrush FrozenBrush(Color c, double opacity = 1.0)
        {
            var brush = new SolidColorBrush(c);
            if (opacity < 1.0)
                brush.Opacity = opacity;
            return FreezeTheme(brush);
        }

        private static LinearGradientBrush FrozenGradient(Color c1, Color c2, Point start, Point end)
        {
            var grad = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            grad.GradientStops.Add(new GradientStop(c1, 0));
            grad.GradientStops.Add(new GradientStop(c2, 1));
            return FreezeTheme(grad);
        }

        // ── Сохранение / загрузка ─────────────────────────────────────────────────

        private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color? HexToColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;

            string s = hex.Trim().TrimStart('#');
            if (s.Length == 6 &&
                byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return Color.FromRgb(r, g, b);
            }

            if (s.Length == 8 &&
                byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a) &&
                byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(s.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return Color.FromArgb(a, r, g, b);
            }

            return null;
        }

        public void SaveColorsToSettings(AppSettings? s)
        {
            if (s == null) return;
            s.ColorAccent1 = _accent1 == DefaultAccent1 ? null : ColorToHex(_accent1);
            s.ColorAccent2 = _accent2 == DefaultAccent2 ? null : ColorToHex(_accent2);
            s.ColorCyan = _cyan == DefaultCyan ? null : ColorToHex(_cyan);
        }

        public void LoadColorsFromSettings(AppSettings? s)
        {
            if (s == null) return;
            _accent1 = HexToColor(s.ColorAccent1) ?? DefaultAccent1;
            _accent2 = HexToColor(s.ColorAccent2) ?? DefaultAccent2;
            _cyan = HexToColor(s.ColorCyan) ?? DefaultCyan;
            ApplyAccentColors();
        }

        // ── Применение цветов ко всему UI ────────────────────────────────────────

        public void ApplyAccentColors()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyAccentColors));
                return;
            }

            ApplyXamlResources();
            ApplyCodeBehindColors();
            RefreshEqBarColors();
            RefreshMixerColors();
        }

        // Обновляем DynamicResource-ресурсы — XAML-стили подхватят автоматически
        private void ApplyXamlResources()
        {
            SetRes("Accent1Brush", FrozenBrush(_accent1));
            SetRes("Accent2Brush", FrozenBrush(_accent2));
            SetRes("CyanBrush", FrozenBrush(_cyan));

            SetRes("Accent1BrushDim", FrozenBrush(_accent1, 0.27));
            SetRes("Accent2BrushDim", FrozenBrush(_accent2, 0.27));
            SetRes("CyanBrushDim", FrozenBrush(_cyan, 0.27));
            SetRes("Accent1BrushSel", FrozenBrush(_accent1, 0.25));

            SetRes("PlayGradient", FrozenGradient(_accent1, _accent2, new Point(0, 0), new Point(1, 1)));
            SetRes("SliderGradient", FrozenGradient(_accent1, _accent2, new Point(0, 0), new Point(1, 0)));

            SetRes("Accent1Color", _accent1);
            SetRes("Accent2Color", _accent2);
            SetRes("CyanColor", _cyan);
        }

        // Обновляем элементы, заданные кодом
        private void ApplyCodeBehindColors()
        {
            UpdateSurroundButton();

            if (ShufflePath != null)
                ShufflePath.Stroke = _shuffle
                    ? FrozenBrush(_cyan)
                    : FrozenBrush(WithAlpha(_cyan, 0x45));

            if (RepeatPath != null)
                RepeatPath.Stroke = _repeat
                    ? FrozenBrush(_accent1)
                    : FrozenBrush(WithAlpha(_accent1, 0x40));

            if (HeartIcon != null && _liked)
                HeartIcon.Foreground = FrozenBrush(_accent2);

            if (TagGenre != null)
            {
                TagGenre.Foreground = FrozenBrush(_cyan);
                if (TagGenre.Parent is Border genreBorder)
                {
                    var dimCyan = FrozenBrush(_cyan, 0.27);
                    genreBorder.Background = dimCyan;
                    genreBorder.BorderBrush = dimCyan;
                }
            }

            if (TagBitrate != null)
            {
                TagBitrate.Foreground = FrozenBrush(_accent2);
                if (TagBitrate.Parent is Border bitrateBorder)
                {
                    var dimA2 = FrozenBrush(_accent2, 0.27);
                    bitrateBorder.Background = dimA2;
                    bitrateBorder.BorderBrush = dimA2;
                }
            }

            if (EqToggleText != null)
            {
                EqToggleText.Foreground = FrozenBrush(_accent1);
                EqToggleText.Effect = MakeGlow(_accent1);
            }
            if (MixerToggleText != null)
            {
                MixerToggleText.Foreground = FrozenBrush(_cyan);
                MixerToggleText.Effect = MakeGlow(_cyan);
            }
            if (VizBtnText != null)
            {
                VizBtnText.Foreground = FrozenBrush(_accent2);
                VizBtnText.Effect = MakeGlow(_accent2);
            }

            var accent2Brush = FrozenBrush(_accent2);
            var accent1Brush = FrozenBrush(_accent1);
            if (VizBar1 != null) VizBar1.Fill = accent2Brush;
            if (VizBar2 != null) VizBar2.Fill = accent2Brush;
            if (VizBar3 != null) VizBar3.Fill = accent1Brush;
            if (VizBar4 != null) VizBar4.Fill = accent1Brush;

            UpdatePlayButton();
        }

        private void RefreshEqBarColors()
        {
            if (!_eqPanelOpen) return;
            BuildEqPanel();
        }

        private void RefreshMixerColors()
        {
            if (!_mixPanelOpen && (MixerContainer == null || MixerContainer.Children.Count == 0))
                return;

            BuildMixerPanel();
        }

        private void SetRes(string key, object value)
        {
            // Присваивание нового экземпляра напрямую обновляет DynamicResource
            // без промежуточного состояния UnsetValue, как при Remove + Add.
            Resources[key] = value;
        }

        private static Color WithAlpha(Color c, byte alpha)
            => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static Ellipse? VisualFindEllipse(DependencyObject? parent)
        {
            if (parent is not Visual)
                return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Ellipse el)
                    return el;

                var found = VisualFindEllipse(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static DropShadowEffect MakeGlow(Color color, double blur = 6, double opacity = 0.85)
            => FreezeTheme(new DropShadowEffect
            {
                Color = color,
                BlurRadius = blur,
                ShadowDepth = 0,
                Opacity = opacity
            });

        /// <summary>
        /// Обновляет Play кнопку, прогресс и слайдер громкости через Template.FindName.
        /// </summary>
        private void UpdatePlayButton()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded && PresentationSource.FromVisual(this) == null)
                    return;

                if (PlayBtn != null)
                {
                    PlayBtn.ApplyTemplate();
                    if (PlayBtn.Template?.FindName("bd", PlayBtn) is Border bd)
                    {
                        bd.Background = FrozenGradient(_accent1, _accent2, new Point(0, 0), new Point(1, 1));
                        bd.Effect = MakeGlow(_accent1, 18, 0.6);
                    }
                }

                if (ProgressSlider != null)
                {
                    ProgressSlider.ApplyTemplate();
                    if (ProgressSlider.Template?.FindName("PART_SelectionRange", ProgressSlider) is Border fill)
                    {
                        fill.Background = FrozenGradient(_accent1, _accent2, new Point(0, 0), new Point(1, 0));
                    }
                }

                if (VolumeSlider != null)
                {
                    VolumeSlider.ApplyTemplate();
                    if (VolumeSlider.Template?.FindName("PART_Track", VolumeSlider) is Track vTrack &&
                        vTrack.Thumb != null)
                    {
                        vTrack.Thumb.ApplyTemplate();
                        var el = VisualFindEllipse(vTrack.Thumb);
                        if (el != null)
                        {
                            el.Fill = FrozenBrush(_cyan);
                            el.Effect = MakeGlow(_cyan, 6, 0.7);
                        }
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        // ── Кнопка открытия пикера ────────────────────────────────────────────────

        private void ColorTheme_Click(object s, RoutedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ColorTheme_Click(s, e)));
                return;
            }

            if (_colorPickerWindow != null)
            {
                if (_colorPickerWindow.IsVisible)
                {
                    if (_colorPickerWindow.WindowState == WindowState.Minimized)
                        _colorPickerWindow.WindowState = WindowState.Normal;
                    _colorPickerWindow.Activate();
                    return;
                }

                CleanupColorPickerWindow(_colorPickerWindow);
                _colorPickerWindow = null;
            }

            OpenColorPicker();
        }

        // ── Построение и управление пикером ──────────────────────────────────────

        private void OpenColorPicker()
        {
            var picker = new ColorPickerWindow(_accent1, _accent2, _cyan)
            {
                Owner = this,
            };

            PositionColorPicker(picker);

            picker.ColorsConfirmed += OnColorPickerConfirmed;
            picker.Closed += OnColorPickerClosed;

            _colorPickerWindow = picker;
            picker.Show();
        }

        private void PositionColorPicker(ColorPickerWindow picker)
        {
            if (ColorThemeBtn == null ||
                PresentationSource.FromVisual(ColorThemeBtn) is not PresentationSource source ||
                source.CompositionTarget == null)
            {
                picker.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            Point screenPx = ColorThemeBtn.PointToScreen(new Point(0, ColorThemeBtn.ActualHeight + 4));
            Point dipPos = source.CompositionTarget.TransformFromDevice.Transform(screenPx);

            double w = !double.IsNaN(picker.Width) && picker.Width > 0 ? picker.Width : 300;
            double h = !double.IsNaN(picker.Height) && picker.Height > 0 ? picker.Height : 360;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            picker.WindowStartupLocation = WindowStartupLocation.Manual;
            picker.Left = Math.Clamp(dipPos.X, vsLeft, Math.Max(vsLeft, vsRight - w));
            picker.Top = Math.Clamp(dipPos.Y, vsTop, Math.Max(vsTop, vsBottom - h));
        }

        private void OnColorPickerConfirmed(Color a1, Color a2, Color cy)
        {
            _accent1 = a1;
            _accent2 = a2;
            _cyan = cy;
            ApplyAccentColors();

            // TODO: вызовите ваш метод сохранения настроек в settings.json,
            // если он не вызывается автоматически в другом месте (например: SaveSettings();).
        }

        private void OnColorPickerClosed(object? sender, EventArgs e)
        {
            if (sender is ColorPickerWindow picker)
                CleanupColorPickerWindow(picker);
        }

        private void CleanupColorPickerWindow(ColorPickerWindow picker)
        {
            picker.ColorsConfirmed -= OnColorPickerConfirmed;
            picker.Closed -= OnColorPickerClosed;

            if (ReferenceEquals(_colorPickerWindow, picker))
                _colorPickerWindow = null;
        }
    }
}

#nullable restore