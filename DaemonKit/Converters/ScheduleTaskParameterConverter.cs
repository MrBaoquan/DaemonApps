using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using DaemonKit.Core;

namespace DaemonKit.Converters
{
    public class ScheduleTaskDescriptionConverter : IMultiValueConverter
    {
        public object Convert(
            object[] values,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (values == null || values.Length < 8)
            {
                return "-";
            }

            if (values[0] is not ScheduleTriggerType trigger)
            {
                return "-";
            }

            var dailyTime = values[1] as string ?? "";
            var delaySeconds = SafeToInt(values[2]);

            if (values[3] is not ScheduleTaskAction action)
            {
                return "-";
            }

            var targetNodeName = values[4] as string ?? string.Empty;
            var clickX = SafeToInt(values[5]);
            var clickY = SafeToInt(values[6]);
            var maxExecute = SafeToInt(values[7]);

            string triggerText = trigger switch
            {
                ScheduleTriggerType.Daily => $"每天定时在 {dailyTime}",
                ScheduleTriggerType.OncePerDayAfterStart => $"每天首次启动后延迟 {delaySeconds} 秒",
                ScheduleTriggerType.EveryStartupAfterDelay => $"每次启动后延迟 {delaySeconds} 秒",
                ScheduleTriggerType.IntervalAfterStartup => $"启动后每间隔 {delaySeconds} 秒",
                _ => string.Empty
            };

            string actionText = action switch
            {
                ScheduleTaskAction.StartProcess => $"启动进程 ({FormatTarget(targetNodeName)})",
                ScheduleTaskAction.StopProcess => $"停止进程 ({FormatTarget(targetNodeName)})",
                ScheduleTaskAction.RestartProcess => $"重启进程 ({FormatTarget(targetNodeName)})",
                ScheduleTaskAction.ShutdownSystem => "关闭电脑",
                ScheduleTaskAction.RestartSystem => "重启电脑",
                ScheduleTaskAction.TakeScreenshot => "执行全屏截图",
                ScheduleTaskAction.ClickMouse => $"鼠标点击坐标 ({clickX}, {clickY})",
                _ => string.Empty
            };

            string executeLimit =
                trigger == ScheduleTriggerType.IntervalAfterStartup && maxExecute > 0
                    ? $"，最多执行 {maxExecute} 次"
                    : string.Empty;

            return string.Join(
                    "，",
                    new[] { triggerText, actionText }.Where(s => !string.IsNullOrWhiteSpace(s))
                ) + executeLimit;
        }

        public object[] ConvertBack(
            object value,
            Type[] targetTypes,
            object parameter,
            CultureInfo culture
        ) => throw new NotImplementedException();

        private static int SafeToInt(object value)
        {
            try
            {
                return System.Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatTarget(string targetNodeName)
        {
            return string.IsNullOrWhiteSpace(targetNodeName) ? "根节点" : targetNodeName;
        }
    }

    public class IndexToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return "-";
            }

            if (int.TryParse(value.ToString(), out var idx))
            {
                return (idx + 1).ToString();
            }

            return "-";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        ) => throw new NotImplementedException();
    }
}
