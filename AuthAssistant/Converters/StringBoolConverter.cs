using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace AuthAssistant.Converters
{
    public class StringBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string strValue)
            {
                return strValue != string.Empty;
            }

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "true" : string.Empty;
            }

            return string.Empty;
        }
    }
}

