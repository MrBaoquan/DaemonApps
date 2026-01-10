using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 将大于0的数值转换为Visible，否则为Collapsed
    /// </summary>
    public class GreaterThanZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return Visibility.Collapsed;

            if (value is int intValue)
            {
                return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (value is long longValue)
            {
                return longValue > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (value is double doubleValue)
            {
                return doubleValue > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }
}
