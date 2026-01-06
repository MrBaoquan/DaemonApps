using System;
using System.Globalization;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 字符串相等性转换器，用于 RadioButton 绑定
    /// </summary>
    public sealed class StringEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            return value
                .ToString()
                .Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (value is bool isChecked && isChecked && parameter != null)
            {
                return parameter.ToString();
            }

            return Binding.DoNothing;
        }
    }
}
