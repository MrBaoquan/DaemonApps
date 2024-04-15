using System;
using System.Windows.Data;

namespace DaemonKit.Converters
{
    public class ScheduleTaskTypeToStringConverter : IValueConverter
    {
        public object Convert(
            object value,
            Type targetType,
            object parameter,
            System.Globalization.CultureInfo culture
        )
        {
            if (value is ScheduleTaskType type)
            {
                if (type == ScheduleTaskType.Start)
                {
                    return "启动 (进程)";
                }
                else if (type == ScheduleTaskType.Stop)
                {
                    return "停止 (进程)";
                }
                else if (type == ScheduleTaskType.Shutdown)
                {
                    return "关闭 (电脑)";
                }
                else if (type == ScheduleTaskType.Restart)
                {
                    return "重启 (电脑)";
                }
            }
            return "启动";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            System.Globalization.CultureInfo culture
        )
        {
            if (value is string str)
            {
                if (str == "启动 (进程)")
                {
                    return ScheduleTaskType.Start;
                }
                else if (str == "停止 (进程)")
                {
                    return ScheduleTaskType.Stop;
                }
                else if (str == "关闭 (电脑)")
                {
                    return ScheduleTaskType.Shutdown;
                }
                else if (str == "重启 (电脑)")
                {
                    return ScheduleTaskType.Restart;
                }
            }
            return ScheduleTaskType.Start;
        }
    }
}
