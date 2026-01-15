using DaemonKit.ViewModels;
using System;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class ImportDialog : Window
    {
        public ImportDialog()
        {
            InitializeComponent();
            var viewModel = new ImportDialogViewModel(result =>
            {
                // 如果窗口不是以 ShowDialog 打开，设置 DialogResult 会抛异常，改为安全关闭
                try
                {
                    DialogResult = result;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            });
            DataContext = viewModel;
            viewModel.SetDialogWindow(this);
        }
    }
}
