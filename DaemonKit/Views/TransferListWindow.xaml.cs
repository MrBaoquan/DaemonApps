using System.Windows;
using DaemonKit.ViewModels;

namespace DaemonKit.Views
{
    /// <summary>
    /// 文件传输管理窗口 - 支持正在下载/已完成分类展示
    /// </summary>
    public partial class TransferListWindow : Window
    {
        private readonly TransferListViewModel _viewModel;

        public TransferListWindow()
        {
            InitializeComponent();
        }

        public TransferListWindow(TransferListViewModel viewModel)
            : this()
        {
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            _viewModel?.Dispose();
        }
    }
}
