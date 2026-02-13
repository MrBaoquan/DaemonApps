using System;
using System.Windows;

namespace DaemonKit.Views
{
    /// <summary>
    /// BackupManagerWindow.xaml 的交互逻辑
    /// </summary>
    public partial class BackupManagerWindow : Window
    {
        public ViewModels.BackupManagerViewModel ViewModel { get; }

        public BackupManagerWindow()
        {
            InitializeComponent();
            ViewModel = new ViewModels.BackupManagerViewModel();
            DataContext = ViewModel;
        }
    }
}
