using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using DaemonKit.Models;
using MaterialDesignThemes.Wpf;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 资源文件分类 → MaterialDesign PackIcon Kind
    /// </summary>
    public class ResourceCategoryToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ResourceFileCategory category)
            {
                return category switch
                {
                    ResourceFileCategory.Package => PackIconKind.PackageVariant,
                    ResourceFileCategory.Executable => PackIconKind.ApplicationCog,
                    ResourceFileCategory.Archive => PackIconKind.ZipBox,
                    ResourceFileCategory.Library => PackIconKind.Puzzle,
                    ResourceFileCategory.Config => PackIconKind.CogOutline,
                    ResourceFileCategory.Document => PackIconKind.FileDocumentOutline,
                    ResourceFileCategory.Image => PackIconKind.Image,
                    _ => PackIconKind.File
                };
            }
            return PackIconKind.File;
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
    /// 资源文件分类 → 颜色画刷
    /// </summary>
    public class ResourceCategoryToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ResourceFileCategory category)
            {
                return category switch
                {
                    ResourceFileCategory.Package => new SolidColorBrush(Color.FromRgb(56, 142, 60)), // 深绿
                    ResourceFileCategory.Executable
                        => new SolidColorBrush(Color.FromRgb(233, 30, 99)), // 粉红
                    ResourceFileCategory.Archive => new SolidColorBrush(Color.FromRgb(255, 152, 0)), // 橙色
                    ResourceFileCategory.Library
                        => new SolidColorBrush(Color.FromRgb(156, 39, 176)), // 紫色
                    ResourceFileCategory.Config => new SolidColorBrush(Color.FromRgb(0, 150, 136)), // 青绿
                    ResourceFileCategory.Document
                        => new SolidColorBrush(Color.FromRgb(33, 150, 243)), // 蓝色
                    ResourceFileCategory.Image => new SolidColorBrush(Color.FromRgb(76, 175, 80)), // 绿色
                    _ => new SolidColorBrush(Color.FromRgb(158, 158, 158)) // 灰色
                };
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
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
    /// 枚举值匹配时返回Visible，否则Collapsed（支持MarkupExtension内联使用）
    /// </summary>
    public class EnumToVisibilityConverter : MarkupExtension, IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return Visibility.Collapsed;
            return value.ToString() == parameter.ToString()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotSupportedException();
        }

        public override object ProvideValue(IServiceProvider serviceProvider) => this;
    }
}
