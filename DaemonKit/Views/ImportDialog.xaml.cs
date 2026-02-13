using DaemonKit.ViewModels;
using System;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class ImportDialog : Window
    {
        public ImportDialog()
            : this(null) { }

        /// <summary>
        /// 创建导入对话框，可选预填包路径（用于资源库部署）
        /// </summary>
        /// <param name="prefilledPackagePath">预填的包文件路径，为null时需用户手动选择</param>
        public ImportDialog(string prefilledPackagePath)
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

            // 如果有预填路径，在窗口加载后自动加载包信息
            if (!string.IsNullOrEmpty(prefilledPackagePath))
            {
                Loaded += async (s, e) =>
                {
                    await viewModel.LoadPackageFromPathAsync(prefilledPackagePath);
                };
            }
        }
    }
}
