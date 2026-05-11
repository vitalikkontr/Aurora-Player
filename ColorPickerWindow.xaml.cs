using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AuroraPlayer
{
    public partial class ColorPickerWindow : Window
    {
        // ── Состояние ─────────────────────────────────────────────────────────
        private string  _activeSlot = "accent1";
        private Color   _accent1, _accent2, _cyan;
        private double  _brightness = 1.0;   // 0..1
        private Color   _wheelColor = Colors.White; // цвет выбранный кругом (без brightness)
        private bool    _draggingWheel;

        // Размер круга
        private const double R = 100.0;

        // Bitmap с нарисованным кругом
        private WriteableBitmap? _wheelBitmap;

        public event Action<Color, Color, Color>? ColorsConfirmed;

        // ── Конструктор ───────────────────────────────────────────────────────
        public ColorPickerWindow(Color accent1, Color accent2, Color cyan)
        {
            InitializeComponent();
            _accent1 = accent1;
            _accent2 = accent2;
            _cyan    = cyan;

            Loaded += (_, _) =>
            {
                DrawWheel();
                SelectSlot("accent1");
                UpdateAllPreviews();
            };
        }

        // ── Рисование цветового круга ─────────────────────────────────────────
        private void DrawWheel()
        {
            int size = (int)(R * 2);
            _wheelBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            int stride = size * 4;
            byte[] pixels = new byte[size * size * 4];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x - R;
                    double dy = y - R;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist > R)
                    {
                        // Прозрачно за пределами круга
                        int idx = (y * size + x) * 4;
                        pixels[idx + 3] = 0;
                        continue;
                    }

                    double hue        = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * 360.0;
                    double saturation = dist / R;
                    double value      = 1.0;

                    var c = HsvToRgb(hue, saturation, value);
                    int i = (y * size + x) * 4;
                    pixels[i]     = c.B;
                    pixels[i + 1] = c.G;
                    pixels[i + 2] = c.R;
                    pixels[i + 3] = 255;
                }
            }

            _wheelBitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);

            var img = new Image
            {
                Width  = size,
                Height = size,
                Source = _wheelBitmap,
                Stretch = Stretch.None,
            };
            WheelCanvas.Children.Insert(0, img);

            // Курсор-кружок поверх
            var cursor = new Ellipse
            {
                Width  = 14,
                Height = 14,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.7 },
            };
            Canvas.SetLeft(cursor, R - 7);
            Canvas.SetTop(cursor,  R - 7);
            WheelCanvas.Children.Add(cursor);
            WheelCanvas.Tag = cursor; // храним ссылку
        }

        // ── Пикинг цвета из круга ─────────────────────────────────────────────
        private void PickColorAtPoint(Point p)
        {
            double dx   = p.X - R;
            double dy   = p.Y - R;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // Ограничиваем точку границей круга
            if (dist > R)
            {
                dx = dx / dist * (R - 1);
                dy = dy / dist * (R - 1);
                dist = R - 1;
            }

            double hue = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * 360.0;
            double sat = dist / R;

            _wheelColor = HsvToRgb(hue, sat, 1.0);

            // Обновляем позицию курсора
            if (WheelCanvas.Tag is Ellipse cur)
            {
                Canvas.SetLeft(cur, R + dx - 7);
                Canvas.SetTop(cur,  R + dy - 7);
            }

            // Обновляем слайдер яркости — показываем чистый цвет справа
            BrightnessColor.Color = _wheelColor;

            ApplyCurrentColor();
        }

        private void ApplyCurrentColor()
        {
            var c = ApplyBrightness(_wheelColor, _brightness);
            switch (_activeSlot)
            {
                case "accent1": _accent1 = c; break;
                case "accent2": _accent2 = c; break;
                case "cyan":    _cyan    = c; break;
            }
            UpdateAllPreviews();
        }

        // ── Мышь на колесе ───────────────────────────────────────────────────
        private void Wheel_MouseDown(object s, MouseButtonEventArgs e)
        {
            _draggingWheel = true;
            WheelCanvas.CaptureMouse();
            PickColorAtPoint(e.GetPosition(WheelCanvas));
        }

        private void Wheel_MouseMove(object s, MouseEventArgs e)
        {
            if (!_draggingWheel) return;
            PickColorAtPoint(e.GetPosition(WheelCanvas));
        }

        private void Wheel_MouseUp(object s, MouseButtonEventArgs e)
        {
            _draggingWheel = false;
            WheelCanvas.ReleaseMouseCapture();
        }

        private void Wheel_MouseLeave(object s, MouseEventArgs e)
        {
            if (_draggingWheel) return;
            _draggingWheel = false;
        }

        // ── Слайдер яркости ───────────────────────────────────────────────────
        private void Brightness_DragDelta(object s, DragDeltaEventArgs e)
        {
            double trackWidth = BrightnessTrack.ActualWidth;
            if (trackWidth <= 0) return;

            double current = _brightness * trackWidth;
            double newPos  = Math.Clamp(current + e.HorizontalChange, 0, trackWidth);
            _brightness    = newPos / trackWidth;

            // Двигаем ползунок
            double margin = _brightness * trackWidth - 9;
            BrightnessThumb.Margin = new Thickness(margin, 0, 0, 0);

            ApplyCurrentColor();
        }

        // ── Выбор слота ──────────────────────────────────────────────────────
        private void SlotClick(object s, MouseButtonEventArgs e)
        {
            if (s is FrameworkElement fe && fe.Tag is string slot)
                SelectSlot(slot);
        }

        private void SelectSlot(string slot)
        {
            _activeSlot = slot;

            // Подсвечиваем активный слот-чип
            var dimBg    = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
            var activeBg = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            SlotA1.Background   = slot == "accent1" ? activeBg : dimBg;
            SlotA2.Background   = slot == "accent2" ? activeBg : dimBg;
            SlotCyan.Background = slot == "cyan"    ? activeBg : dimBg;

            // Обновляем колесо: ставим курсор на позицию текущего цвета этого слота
            var cur = slot switch { "accent1" => _accent1, "accent2" => _accent2, _ => _cyan };
            PositionCursorForColor(cur);
        }

        private void PositionCursorForColor(Color c)
        {
            // Переводим RGB → HSV
            RgbToHsv(c, out double h, out double s, out double v);
            _brightness = v;
            _wheelColor = HsvToRgb(h, s, 1.0);
            BrightnessColor.Color = _wheelColor;

            // Позиция курсора
            double angle = h * Math.PI / 180.0;
            double dx    = Math.Cos(angle) * s * (R - 1);
            double dy    = Math.Sin(angle) * s * (R - 1);

            if (WheelCanvas.Tag is Ellipse cur)
            {
                Canvas.SetLeft(cur, R + dx - 7);
                Canvas.SetTop(cur,  R + dy - 7);
            }

            // Позиция ползунка яркости
            double trackWidth = BrightnessTrack.ActualWidth > 0
                ? BrightnessTrack.ActualWidth : 232;
            BrightnessThumb.Margin = new Thickness(_brightness * trackWidth - 9, 0, 0, 0);
        }

        // ── Обновление превью ─────────────────────────────────────────────────
        private void UpdateAllPreviews()
        {
            SetPreview(PreviewA1,    P1Glow, _accent1);
            SetPreview(PreviewA2,    P2Glow, _accent2);
            SetPreview(PreviewCyan,  P3Glow, _cyan);

            // Текущий активный цвет
            var cur = _activeSlot switch { "accent1" => _accent1, "accent2" => _accent2, _ => _cyan };
            CurrentColorPreview.Background = new SolidColorBrush(cur);
            HexLabel.Text = $"#{cur.R:X2}{cur.G:X2}{cur.B:X2}";
        }

        private static void SetPreview(Border b, DropShadowEffect glow, Color c)
        {
            b.Background = new SolidColorBrush(c);
            glow.Color   = c;
        }

        // ── Кнопки ───────────────────────────────────────────────────────────
        private void Ok_Click(object s, MouseButtonEventArgs e)
        {
            ColorsConfirmed?.Invoke(_accent1, _accent2, _cyan);
            Close();
        }

        private void Reset_Click(object s, MouseButtonEventArgs e)
        {
            _accent1 = MainWindow.DefaultAccent1;
            _accent2 = MainWindow.DefaultAccent2;
            _cyan    = MainWindow.DefaultCyan;
            SelectSlot(_activeSlot);
            UpdateAllPreviews();
        }

        private void Close_Click(object s, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        // ── Конвертеры цвета ─────────────────────────────────────────────────
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c  = v * s;
            double x  = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m  = v - c;
            double r, g, b;
            if      (h < 60)  { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else               { r = c; g = 0; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static void RgbToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            v = max;
            s = max < 1e-6 ? 0 : delta / max;
            if (delta < 1e-6) { h = 0; return; }
            if      (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else               h = 60 * ((r - g) / delta + 4);
            if (h < 0) h += 360;
        }

        private static Color ApplyBrightness(Color c, double v)
            => Color.FromRgb(
                (byte)(c.R * v),
                (byte)(c.G * v),
                (byte)(c.B * v));
    }
}
