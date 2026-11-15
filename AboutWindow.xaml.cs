using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AuroraPlayer
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Применяет акцентные цвета из главного окна.
        /// Вызывается из MainWindow перед ShowDialog().
        /// </summary>
        public void ApplyColors(Color accent1, Color accent2, Color cyan)
        {
            // Декоративные фоновые пятна
            if (GlowBrush1.GradientStops.Count > 0) GlowBrush1.GradientStops[0].Color = accent1;
            if (GlowBrush2.GradientStops.Count > 0) GlowBrush2.GradientStops[0].Color = accent2;

            // Иконка-бордер — немного затемняем акцент для фона
            IconGrad1.Color = Darken(accent1, 0.75f);
            IconGrad2.Color = Darken(accent2, 0.80f);
            IconShadow.Color = accent1;

            // Заголовок "Aurora Player"
            TitleShadow.Color = accent1;

            // Версия
            var verBg = new SolidColorBrush(Color.FromArgb(0x1A, accent1.R, accent1.G, accent1.B));
            verBg.Freeze();
            var verBorder = new SolidColorBrush(Color.FromArgb(0x60, accent1.R, accent1.G, accent1.B));
            verBorder.Freeze();

            VersionBorder.Background = verBg;
            VersionBorder.BorderBrush = verBorder;
            VersionShadow.Color = accent1;

            // Разработчик / GitHub / Форматы
            DevShadow.Color = accent2;
            GithubShadow.Color = cyan;
            FormatsShadow.Color = accent1;

            // Рамка окна
            BorderGrad1.Color = Color.FromArgb(0x2A, accent1.R, accent1.G, accent1.B);
            BorderGrad2.Color = Color.FromArgb(0x10, accent2.R, accent2.G, accent2.B);
        }

        private static Color Darken(Color c, float factor)
            => Color.FromRgb(
                (byte)Math.Clamp(c.R * factor, 0f, 255f),
                (byte)Math.Clamp(c.G * factor, 0f, 255f),
                (byte)Math.Clamp(c.B * factor, 0f, 255f));

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }
    }
}