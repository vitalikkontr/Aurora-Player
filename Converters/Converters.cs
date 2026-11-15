// Converters.cs — вспомогательные конвертеры для биндинга
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AuroraPlayer
{
    /// <summary>null / UnsetValue → Visible, не-null → Collapsed (для заглушки)</summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        private static readonly object VisibleBox = Visibility.Visible;
        private static readonly object CollapsedBox = Visibility.Collapsed;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value == null || ReferenceEquals(value, DependencyProperty.UnsetValue)
                ? VisibleBox
                : CollapsedBox;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>не-null → Visible, null / UnsetValue → Collapsed (для обложки)</summary>
    public class NotNullToVisibilityConverter : IValueConverter
    {
        private static readonly object VisibleBox = Visibility.Visible;
        private static readonly object CollapsedBox = Visibility.Collapsed;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null && !ReferenceEquals(value, DependencyProperty.UnsetValue)
                ? VisibleBox
                : CollapsedBox;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}