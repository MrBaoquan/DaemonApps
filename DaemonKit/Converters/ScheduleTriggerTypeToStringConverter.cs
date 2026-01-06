using System;
using System.Globalization;
using System.Windows.Data;
using DaemonKit.Core;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 将新的ScheduleTriggerType转换为UI显示的字符串
    /// </summary>
    public class ScheduleTriggerTypeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ScheduleTriggerType triggerType)
            {
                return triggerType switch
                {
                    ScheduleTriggerType.Daily => "每天定时",
                    ScheduleTriggerType.EveryStartupAfterDelay => "每天启动后",
                    ScheduleTriggerType.OncePerDayAfterStart => "每天首次启动后",
                    ScheduleTriggerType.IntervalAfterStartup => "每间隔",
                    _ => "未知"
                };
            }

            return "未知";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (value is string str)
            {
                return str switch
                {
                    "每天定时" => ScheduleTriggerType.Daily,
                    "每天启动后" => ScheduleTriggerType.EveryStartupAfterDelay,
                    "每天首次启动后" => ScheduleTriggerType.OncePerDayAfterStart,
                    "每间隔" => ScheduleTriggerType.IntervalAfterStartup,
                    _ => ScheduleTriggerType.Daily
                };
            }

            return ScheduleTriggerType.Daily;
        }
    }
}
