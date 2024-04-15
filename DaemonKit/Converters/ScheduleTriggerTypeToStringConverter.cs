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
                if (Enum.TryParse(triggerTypeString, out Core.TriggerType triggerType))
                {
                    return triggerType;
                }
            }

            return Core.TriggerType.Daily;
        }
    }
}
