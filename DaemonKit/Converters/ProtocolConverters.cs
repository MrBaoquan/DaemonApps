using System;
using System.Globalization;
using System.Windows.Data;
using DaemonKit.PowerSaving;

namespace DaemonKit.Converters;

/// <summary>
/// 将 ProtocolType 转换为显示名称
/// </summary>
public class ProtocolToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ProtocolType protocol)
        {
            return ProtocolInfo.GetInfo(protocol).DisplayName;
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 ProtocolType 转换为协议描述
/// </summary>
public class ProtocolToDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ProtocolType protocol)
        {
            return ProtocolInfo.GetInfo(protocol).Description;
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
