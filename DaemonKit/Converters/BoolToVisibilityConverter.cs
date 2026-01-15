using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DaemonKit.Converters;

/// <summary>
/// 将布尔值转换为 Visibility (true -> Visible, false -> Collapsed)
/// 支持 ConverterParameter="Inverse" 反转逻辑
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;

        // 检查是否需要反转
        bool inverse =
            parameter?.ToString()?.Equals("Inverse", StringComparison.OrdinalIgnoreCase) == true;

        if (inverse)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool inverse =
            parameter?.ToString()?.Equals("Inverse", StringComparison.OrdinalIgnoreCase) == true;

        if (value is Visibility visibility)
        {
            bool result = visibility == Visibility.Visible;
            return inverse ? !result : result;
        }

        return false;
    }
}
