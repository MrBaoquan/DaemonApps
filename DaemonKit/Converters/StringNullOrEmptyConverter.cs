using System;
using System.Globalization;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 检查字符串是否为 null 或空
    /// </summary>
    public class StringNullOrEmptyConverter : IValueConverter
    {
        public static readonly StringNullOrEmptyConverter Instance =
            new StringNullOrEmptyConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !string.IsNullOrEmpty(value as string);
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
