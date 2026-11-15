using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AuroraPlayer
{
    public class StringToColorConverter : IValueConverter
    {
        private static readonly object TransparentBox = Colors.Transparent;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(s.Trim()) is Color c)
                        return c;
                }
                catch { }
            }
            return TransparentBox;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}