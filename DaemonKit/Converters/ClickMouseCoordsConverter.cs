using System;
using System.Globalization;
using System.Windows.Data;
using DaemonKit.Models;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 显示点击坐标，仅在操作为 ClickMouse 时返回 "X, Y"，否则返回 "-"。
    /// </summary>
    public class ClickMouseCoordsConverter : IMultiValueConverter
    {
        public object Convert(
            object[] values,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (values.Length < 3)
                return "-";

            if (values[0] is ScheduleTaskAction action && action == ScheduleTaskAction.ClickMouse)
            {
                var x = values[1]?.ToString();
                var y = values[2]?.ToString();
                return string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)
                    ? "-"
                    : $"{x}, {y}";
            }

            return "-";
        }

        public object[] ConvertBack(
            object value,
            Type[] targetTypes,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotSupportedException();
        }
    }
}
