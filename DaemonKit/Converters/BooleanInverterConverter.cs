using System;
using System.Globalization;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    public sealed class BooleanInverterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolean)
            {
                return !boolean;
            }

            return true;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (value is bool boolean)
            {
                return !boolean;
            }

            return false;
        }
    }
}
