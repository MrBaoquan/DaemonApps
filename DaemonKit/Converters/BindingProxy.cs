using System.Windows;

namespace DaemonKit.Converters
{
    /// <summary>
    /// 绑定代理 - 用于在ContextMenu/PopupBox等脱离可视化树的元素中访问ViewModel
    /// 用法：在Window.Resources中声明 &lt;converters:BindingProxy x:Key="ViewModelProxy" Data="{Binding}"/&gt;
    /// 然后在DataTemplate中通过 Source={StaticResource ViewModelProxy} 访问ViewModel命令
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            "Data",
            typeof(object),
            typeof(BindingProxy),
            new UIPropertyMetadata(null)
        );

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
