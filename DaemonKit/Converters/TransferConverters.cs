using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DaemonKit.Models;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 设备状态转换为颜色画刷
    /// </summary>
    public class MachineStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MachineStatus status)
            {
                return status switch
                {
                    MachineStatus.Online => new SolidColorBrush(Color.FromRgb(76, 175, 80)), // 绿色
                    MachineStatus.Offline => new SolidColorBrush(Color.FromRgb(158, 158, 158)), // 灰色
                    MachineStatus.Busy => new SolidColorBrush(Color.FromRgb(255, 152, 0)), // 橙色
                    MachineStatus.Connecting => new SolidColorBrush(Color.FromRgb(33, 150, 243)), // 蓝色
                    _ => new SolidColorBrush(Colors.Gray)
                };
            }
            return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 默认在线（绿色）
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 传输状态转换为显示文本
    /// </summary>
    public class TransferStateToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state switch
                {
                    TransferState.Pending => "等待中",
                    TransferState.Transferring => "传输中",
                    TransferState.Paused => "已暂停",
                    TransferState.Completed => "已完成",
                    TransferState.Failed => "失败",
                    TransferState.Cancelled => "已取消",
                    _ => state.ToString()
                };
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 传输状态转换为颜色
    /// </summary>
    public class TransferStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state switch
                {
                    TransferState.Pending => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
                    TransferState.Transferring => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                    TransferState.Paused => new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                    TransferState.Completed => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    TransferState.Failed => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                    TransferState.Cancelled => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
                    _ => new SolidColorBrush(Colors.Gray)
                };
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 字节数转换为可读格式 (KB, MB, GB)
    /// </summary>
    public class BytesToHumanReadableConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double bytes;
            if (value is long l)
                bytes = l;
            else if (value is double d)
                bytes = d;
            else if (value is int i)
                bytes = i;
            else
                return "0 B";

            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < sizes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {sizes[order]}";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 传输中状态转换为可见性
    /// </summary>
    public class TransferringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state == TransferState.Transferring
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 暂停状态转换为可见性
    /// </summary>
    public class PausedToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state == TransferState.Paused ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 大于零转换为布尔值
    /// </summary>
    public class GreaterThanZeroToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
                return count > 0;
            return false;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 传输方向转换为图标
    /// </summary>
    public class TransferDirectionToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferDirection direction)
            {
                return direction == TransferDirection.Upload ? "Upload" : "Download";
            }
            return "FileTransfer";
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 是否支持P2P转换为可见性
    /// </summary>
    public class SupportsP2PToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool supportsP2P)
            {
                return supportsP2P ? Visibility.Visible : Visibility.Collapsed;
            }
            // 默认支持
            return Visibility.Visible;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 传输状态非"已取消"时返回true（用于禁用已取消任务的按钮）
    /// </summary>
    public class TransferStateNotCancelledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
                return state != TransferState.Cancelled;
            return true;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }
}
