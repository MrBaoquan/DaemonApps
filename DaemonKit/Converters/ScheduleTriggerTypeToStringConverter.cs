using System;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    public class ScheduleTriggerTypeToStringConverter : IValueConverter
    {
        public object Convert(
            object value,
            Type targetType,
            object parameter,
            System.Globalization.CultureInfo culture
        )
        {
            if (value is Core.TriggerType type)
            {
                if (type == Core.TriggerType.Daily)
                {
                    return "每天";
                }
                else if (type == Core.TriggerType.Interval)
                {
                    return "间隔";
                }
                else if (type == Core.TriggerType.OnAppStart)
                {
                    return "程序启动后";
                }
                else if (type == Core.TriggerType.OnAppStartOnce)
                {
                    return "每天首次启动后";
                }
            }

            return string.Empty;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            System.Globalization.CultureInfo culture
        )
        {
            if (value is string triggerTypeString)
            {
                if (triggerTypeString == "每天")
                {
                    return Core.TriggerType.Daily;
                }
                else if (triggerTypeString == "间隔")
                {
                    return Core.TriggerType.Interval;
                }
                else if (triggerTypeString == "程序启动后")
                {
                    return Core.TriggerType.OnAppStart;
                }
                else if (triggerTypeString == "每天首次启动后")
                {
                    return Core.TriggerType.OnAppStartOnce;
                }
            }

            return Core.TriggerType.Daily;
        }
    }
}
