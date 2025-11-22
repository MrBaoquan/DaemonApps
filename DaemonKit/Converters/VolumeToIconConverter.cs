using System;
using System.Globalization;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;

namespace DaemonKit
{
    /// <summary>
    /// 将音量值转换为对应的图标类型
    /// </summary>
    public class VolumeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double volume)
            {
                // 根据音量大小返回不同的图标
                if (volume <= 0)
                {
                    return PackIconKind.VolumeMute; // 静音
                }
                else if (volume < 33)
                {
                    return PackIconKind.VolumeLow; // 低音量
                }
                else if (volume < 66)
                {
                    return PackIconKind.VolumeMedium; // 中等音量
                }
                else
                {
                    return PackIconKind.VolumeHigh; // 高音量
                }
            }

            return PackIconKind.VolumeHigh;
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
    /// 将音量值和静音状态转换为对应的图标类型（支持多值绑定）
    /// </summary>
    public class VolumeWithMuteToIconConverter : IMultiValueConverter
    {
        public object Convert(
            object[] values,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            if (values.Length >= 2 && values[0] is double volume && values[1] is bool isMuted)
            {
                // 如果静音，直接返回静音图标
                if (isMuted)
                {
                    return PackIconKind.VolumeOff;
                }

                // 根据音量大小返回不同的图标
                if (volume <= 0)
                {
                    return PackIconKind.VolumeMute; // 音量为0
                }
                else if (volume < 33)
                {
                    return PackIconKind.VolumeLow; // 低音量
                }
                else if (volume < 66)
                {
                    return PackIconKind.VolumeMedium; // 中等音量
                }
                else
                {
                    return PackIconKind.VolumeHigh; // 高音量
                }
            }

            // 默认返回单值转换结果
            if (values.Length > 0 && values[0] is double vol)
            {
                if (vol <= 0)
                    return PackIconKind.VolumeMute;
                if (vol < 33)
                    return PackIconKind.VolumeLow;
                if (vol < 66)
                    return PackIconKind.VolumeMedium;
                return PackIconKind.VolumeHigh;
            }

            return PackIconKind.VolumeHigh;
        }

        public object[] ConvertBack(
            object value,
            Type[] targetTypes,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }
}
