using System.Windows;
using System.Reactive.Linq;
using DaemonKit.ViewModels;
using ReactiveUI;

namespace DaemonKit.Views
{
    /// <summary>
    /// 资源库窗口 — 聚合所有在线设备的共享文件
    /// </summary>
    public partial class ResourceLibraryWindow : Window
    {
        public ResourceLibraryWindow()
        {
            InitializeComponent();
        }

        public ResourceLibraryWindow(ResourceLibraryViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
            viewModel.CloseAction = () => this.Close();

            // 窗口加载后自动扫描
            Loaded += async (s, e) =>
            {
                await viewModel.RefreshCommand.Execute();
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
