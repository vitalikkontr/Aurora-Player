// MainWindow.ColorTheme.cs — пользовательская цветовая тема Aurora Player
// Три акцентных цвета управляют всем оформлением плеера.
// Выбор через встроенный пикер, сохранение в settings.json.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ── Цвета по умолчанию ────────────────────────────────────────────────────
        public static readonly Color DefaultAccent1 = Color.FromRgb(0x7C, 0x6B, 0xFF); // фиолетовый
        public static readonly Color DefaultAccent2 = Color.FromRgb(0xFF, 0x6B, 0xB5); // розовый
        public static readonly Color DefaultCyan    = Color.FromRgb(0x00, 0xE5, 0xCC); // бирюзовый

        // ── Текущие цвета (живые) ─────────────────────────────────────────────────
        private Color _accent1 = DefaultAccent1;
        private Color _accent2 = DefaultAccent2;
        private Color _cyan    = DefaultCyan;

        // Окно выбора цветов
        private ColorPickerWindow? _colorPickerWindow;

        // ── Публичный доступ к цветам (используется из UI.cs, EQ.cs и др.) ───────
        public Color Accent1 => _accent1;
        public Color Accent2 => _accent2;
        public Color Cyan    => _cyan;

        // ── Сохранение / загрузка ─────────────────────────────────────────────────

        private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color? HexToColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                    return Color.FromRgb(
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16));
            }
            catch { }
            return null;
        }

        public void SaveColorsToSettings(AppSettings s)
        {
            s.ColorAccent1 = _accent1 == DefaultAccent1 ? null : ColorToHex(_accent1);
            s.ColorAccent2 = _accent2 == DefaultAccent2 ? null : ColorToHex(_accent2);
            s.ColorCyan    = _cyan    == DefaultCyan    ? null : ColorToHex(_cyan);
        }

        public void LoadColorsFromSettings(AppSettings s)
        {
            _accent1 = HexToColor(s.ColorAccent1) ?? DefaultAccent1;
            _accent2 = HexToColor(s.ColorAccent2) ?? DefaultAccent2;
            _cyan    = HexToColor(s.ColorCyan)    ?? DefaultCyan;
            ApplyAccentColors();
        }

        // ── Применение цветов ко всему UI ────────────────────────────────────────

        public void ApplyAccentColors()
        {
            ApplyXamlResources();
            ApplyCodeBehindColors();
            RefreshEqBarColors();
        }

        // Обновляем DynamicResource-ресурсы — XAML-стили подхватят автоматически
        private void ApplyXamlResources()
        {
            // Основные кисти
            SetRes("Accent1Brush",     new SolidColorBrush(_accent1));
            SetRes("Accent2Brush",     new SolidColorBrush(_accent2));
            SetRes("CyanBrush",        new SolidColorBrush(_cyan));

            // Полупрозрачные варианты — через Opacity (XAML-совместимо)
            SetRes("Accent1BrushDim",  new SolidColorBrush(_accent1) { Opacity = 0.27 });
            SetRes("Accent2BrushDim",  new SolidColorBrush(_accent2) { Opacity = 0.27 });
            SetRes("CyanBrushDim",     new SolidColorBrush(_cyan)    { Opacity = 0.27 });
            SetRes("Accent1BrushSel",  new SolidColorBrush(_accent1) { Opacity = 0.25 });

            // Градиент кнопки Play
            var grad = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,1) };
            grad.GradientStops.Add(new GradientStop(_accent1, 0));
            grad.GradientStops.Add(new GradientStop(_accent2, 1));
            SetRes("PlayGradient", grad);

            // Градиент слайдера прогресса
            var sliderGrad = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,0) };
            sliderGrad.GradientStops.Add(new GradientStop(_accent1, 0));
            sliderGrad.GradientStops.Add(new GradientStop(_accent2, 1));
            SetRes("SliderGradient", sliderGrad);

            // Цвет теней
            SetRes("Accent1Color", _accent1);
            SetRes("Accent2Color", _accent2);
            SetRes("CyanColor",    _cyan);
        }

        // Обновляем элементы заданные кодом (кнопки Shuffle/Repeat/Surround)
        private void ApplyCodeBehindColors()
        {
            UpdateSurroundButton();

            // Shuffle / Repeat / Heart
            if (ShufflePath != null)
                ShufflePath.Stroke = _shuffle
                    ? new SolidColorBrush(_cyan)
                    : new SolidColorBrush(WithAlpha(_cyan, 0x45));

            if (RepeatPath != null)
                RepeatPath.Stroke = _repeat
                    ? new SolidColorBrush(_accent1)
                    : new SolidColorBrush(WithAlpha(_accent1, 0x40));

            if (HeartIcon != null && _liked)
                HeartIcon.Foreground = new SolidColorBrush(_accent2);

            // Теги жанра и битрейта — их Border родители нет x:Name, обновляем через TagGenre.Parent
            if (TagGenre != null)
            {
                TagGenre.Foreground = new SolidColorBrush(_cyan);
                if (TagGenre.Parent is System.Windows.Controls.Border genreBorder)
                {
                    var dimCyan = new SolidColorBrush(_cyan) { Opacity = 0.27 };
                    genreBorder.Background  = dimCyan;
                    genreBorder.BorderBrush = dimCyan;
                }
            }
            if (TagBitrate != null)
            {
                TagBitrate.Foreground = new SolidColorBrush(_accent2);
                if (TagBitrate.Parent is System.Windows.Controls.Border bitrateBorder)
                {
                    var dimA2 = new SolidColorBrush(_accent2) { Opacity = 0.27 };
                    bitrateBorder.Background  = dimA2;
                    bitrateBorder.BorderBrush = dimA2;
                }
            }

            // EQ / MIX / VIZ — текст + тень, без рамок
            if (EqToggleText != null)
            {
                EqToggleText.Foreground = new SolidColorBrush(_accent1);
                EqToggleText.Effect     = MakeGlow(_accent1);
            }
            if (MixerToggleText != null)
            {
                MixerToggleText.Foreground = new SolidColorBrush(_cyan);
                MixerToggleText.Effect     = MakeGlow(_cyan);
            }
            if (VizBtnText != null)
            {
                VizBtnText.Foreground = new SolidColorBrush(_accent2);
                VizBtnText.Effect     = MakeGlow(_accent2);
            }
            if (VizBar1 != null) VizBar1.Fill = new SolidColorBrush(_accent2);
            if (VizBar2 != null) VizBar2.Fill = new SolidColorBrush(_accent2);
            if (VizBar3 != null) VizBar3.Fill = new SolidColorBrush(_accent1);
            if (VizBar4 != null) VizBar4.Fill = new SolidColorBrush(_accent1);

            // Play кнопка — обновляем градиент напрямую через FindName
            UpdatePlayButton();
        }

        // EQ-бары: пересоздать с новыми цветами
        private void RefreshEqBarColors()
        {
            if (!_eqPanelOpen) return;
            BuildEqPanel();
        }

        private void SetRes(string key, object value)
        {
            // Remove + Add гарантирует что все DynamicResource-подписчики получат уведомление.
            // Простое Resources[key] = value иногда не триггерит обновление уже отрисованных элементов.
            if (Resources.Contains(key))
                Resources.Remove(key);
            Resources.Add(key, value);
        }

        private static Color WithAlpha(Color c, byte alpha)
            => Color.FromArgb(alpha, c.R, c.G, c.B);

        // Ищет первый Ellipse в визуальном дереве элемента
        private static System.Windows.Shapes.Ellipse? VisualFindEllipse(
            System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Shapes.Ellipse el) return el;
                var found = VisualFindEllipse(child);
                if (found != null) return found;
            }
            return null;
        }

        private static DropShadowEffect MakeGlow(Color color, double blur = 6, double opacity = 0.85)
            => new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };

        /// <summary>
        /// Обновляет Play кнопку, прогресс и слайдер громкости через Template.FindName.
        /// Используем Dispatcher чтобы гарантировать что шаблон уже применён.
        /// </summary>
        private void UpdatePlayButton()
        {
            Dispatcher.InvokeAsync(() =>
            {
                // ── Play кнопка ───────────────────────────────────────────────
                if (PlayBtn != null)
                {
                    PlayBtn.ApplyTemplate();
                    if (PlayBtn.Template?.FindName("bd", PlayBtn) is System.Windows.Controls.Border bd)
                    {
                        var grad = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,1) };
                        grad.GradientStops.Add(new GradientStop(_accent1, 0));
                        grad.GradientStops.Add(new GradientStop(_accent2, 1));
                        bd.Background = grad;
                        bd.Effect     = MakeGlow(_accent1, 18, 0.6);
                    }
                }

                // ── Прогресс-слайдер ──────────────────────────────────────────
                if (ProgressSlider != null)
                {
                    ProgressSlider.ApplyTemplate();
                    if (ProgressSlider.Template?.FindName("PART_SelectionRange", ProgressSlider)
                        is System.Windows.Controls.Border fill)
                    {
                        var grad = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,0) };
                        grad.GradientStops.Add(new GradientStop(_accent1, 0));
                        grad.GradientStops.Add(new GradientStop(_accent2, 1));
                        fill.Background = grad;
                    }
                }

                // ── Слайдер громкости (Thumb → Ellipse) ───────────────────────
                if (VolumeSlider != null)
                {
                    VolumeSlider.ApplyTemplate();
                    if (VolumeSlider.Template?.FindName("PART_Track", VolumeSlider)
                        is System.Windows.Controls.Primitives.Track vTrack && vTrack.Thumb != null)
                    {
                        vTrack.Thumb.ApplyTemplate();
                        var el = VisualFindEllipse(vTrack.Thumb);
                        if (el != null)
                        {
                            el.Fill   = new SolidColorBrush(_cyan);
                            el.Effect = MakeGlow(_cyan, 6, 0.7);
                        }
                    }
                }

            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ── Кнопка открытия пикера ────────────────────────────────────────────────

        private void ColorTheme_Click(object s, RoutedEventArgs e)
        {
            // Если окно уже открыто — выдвигаем его на передний план
            if (_colorPickerWindow != null && _colorPickerWindow.IsVisible)
            {
                _colorPickerWindow.Activate();
                return;
            }
            OpenColorPicker();
        }

        // ── Построение пикера ─────────────────────────────────────────────────────

        private void OpenColorPicker()
        {
            _colorPickerWindow = new ColorPickerWindow(_accent1, _accent2, _cyan)
            {
                Owner = this,
            };

            // Позиционируем рядом с кнопкой
            var btnPos = ColorThemeBtn.PointToScreen(new Point(0, ColorThemeBtn.ActualHeight + 4));
            _colorPickerWindow.Left = btnPos.X;
            _colorPickerWindow.Top  = btnPos.Y;

            _colorPickerWindow.ColorsConfirmed += (a1, a2, cy) =>
            {
                _accent1 = a1;
                _accent2 = a2;
                _cyan    = cy;
                ApplyAccentColors();
            };

            _colorPickerWindow.Show();
        }


    }
}
