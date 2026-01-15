using DaemonKit.ViewModels;
using System;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class ProgressWindow : Window
    {
        public ProgressWindow()
        {
            InitializeComponent();
        }

        public ProgressWindow(ProgressWindowViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
            viewModel.RequestClose += (s, e) => Close();
            viewModel.RequestMinimize += (s, e) =>
            {
                // 隐藏窗口而不是最小化，并重新启用主窗口
                Hide();
                if (Owner != null)
                {
                    Owner.IsEnabled = true;
                    Owner.Activate();

                    // 显示状态栏进度指示器
                    if (Owner is MainWindow mainWindow)
                    {
                        mainWindow.ShowPackageProgressInStatusBar();
                    }
                }
            };

            // 显示时禁用Owner窗口，实现模态效果
            Loaded += (s, e) =>
            {
                if (Owner != null)
                {
                    Owner.IsEnabled = false;
                }
            };

            // 关闭时重新启用Owner窗口，并立即隐藏状态栏进度
            Closed += (s, e) =>
            {
                if (Owner != null)
                {
                    Owner.IsEnabled = true;
                    Owner.Activate();

                    // 立即隐藏状态栏进度指示器，防止窗口关闭后还能点击
                    if (Owner is MainWindow mainWindow)
                    {
                        mainWindow.HidePackageProgressInStatusBar();
                    }
                }
            };
        }
    }
}
