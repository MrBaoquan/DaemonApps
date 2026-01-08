using System;
using System.Globalization;
using System.Windows.Data;
using DaemonKit.Models;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 将ScheduleTaskAction转换为UI显示的字符串
    /// </summary>
    public class ScheduleTaskActionToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ScheduleTaskAction action)
            {
                return action switch
                {
                    ScheduleTaskAction.StartProcess => "启动进程",
                    ScheduleTaskAction.StopProcess => "停止进程",
                    ScheduleTaskAction.RestartProcess => "重启进程",
                    ScheduleTaskAction.ShutdownSystem => "关闭电脑",
                    ScheduleTaskAction.RestartSystem => "重启电脑",
                    ScheduleTaskAction.TakeScreenshot => "全屏截图",
                    ScheduleTaskAction.ClickMouse => "鼠标点击",
                    ScheduleTaskAction.EnterPowerSaving => "开启节能模式",
                    ScheduleTaskAction.ExitPowerSaving => "退出节能模式",
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
                    "启动进程" => ScheduleTaskAction.StartProcess,
                    "停止进程" => ScheduleTaskAction.StopProcess,
                    "重启进程" => ScheduleTaskAction.RestartProcess,
                    "关闭电脑" => ScheduleTaskAction.ShutdownSystem,
                    "重启电脑" => ScheduleTaskAction.RestartSystem,
                    "全屏截图" => ScheduleTaskAction.TakeScreenshot,
                    "鼠标点击" => ScheduleTaskAction.ClickMouse,
                    "开启节能模式" => ScheduleTaskAction.EnterPowerSaving,
                    "退出节能模式" => ScheduleTaskAction.ExitPowerSaving,
                    _ => ScheduleTaskAction.StartProcess
                };
            }

            return ScheduleTaskAction.StartProcess;
        }
    }
}
