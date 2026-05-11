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
        /// Вызывается из MainWindow перед Show().
        /// </summary>
        public void ApplyColors(Color accent1, Color accent2, Color cyan)
        {
            // Декоративные фоновые пятна
            if (GlowBrush1.GradientStops.Count > 0) GlowBrush1.GradientStops[0].Color = accent1;
            if (GlowBrush2.GradientStops.Count > 0) GlowBrush2.GradientStops[0].Color = accent2;

            // Иконка-бордер — немного затемняем акцент для фона
            IconGrad1.Color  = Darken(accent1, 0.75f);
            IconGrad2.Color  = Darken(accent2, 0.80f);
            IconShadow.Color = accent1;

            // Заголовок "Aurora Player"
            TitleShadow.Color = accent1;

            // Версия
            VersionBorder.Background  = new SolidColorBrush(Color.FromArgb(0x1A, accent1.R, accent1.G, accent1.B));
            VersionBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, accent1.R, accent1.G, accent1.B));
            VersionShadow.Color       = accent1;

            // Разработчик / GitHub / Форматы
            DevShadow.Color     = accent2;
            GithubShadow.Color  = cyan;
            FormatsShadow.Color = accent1;

            // Рамка окна
            BorderGrad1.Color = Color.FromArgb(0x2A, accent1.R, accent1.G, accent1.B);
            BorderGrad2.Color = Color.FromArgb(0x10, accent2.R, accent2.G, accent2.B);
        }

        private static Color Darken(Color c, float factor)
            => Color.FromRgb(
                (byte)(c.R * factor),
                (byte)(c.G * factor),
                (byte)(c.B * factor));

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }
    }
}
