using System;
using System.Windows;

namespace DaemonKit.Views
{
    /// <summary>
    /// BackupPackageWindow.xaml 的交互逻辑
    /// </summary>
    public partial class BackupPackageWindow : Window
    {
        public ViewModels.BackupPackageViewModel ViewModel { get; }

        public BackupPackageWindow(string defaultPackagePath)
        {
            InitializeComponent();
            ViewModel = new ViewModels.BackupPackageViewModel(defaultPackagePath, this);
            DataContext = ViewModel;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
