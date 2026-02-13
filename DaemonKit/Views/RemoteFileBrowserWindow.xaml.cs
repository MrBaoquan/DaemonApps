using System.Windows;
using DaemonKit.ViewModels;

namespace DaemonKit.Views
{
    /// <summary>
    /// 远程文件浏览窗口
    /// </summary>
    public partial class RemoteFileBrowserWindow : Window
    {
        public RemoteFileBrowserWindow()
        {
            InitializeComponent();
        }

        public RemoteFileBrowserWindow(RemoteFileBrowserViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
            viewModel.CloseAction = () => this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
