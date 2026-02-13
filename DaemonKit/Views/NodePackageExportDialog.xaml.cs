using DaemonKit.ViewModels;
using DaemonKit.Models;
using System;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class NodePackageExportDialog : Window
    {
        public NodePackageExportDialog()
            : this(null) { }

        /// <summary>
        /// 创建节点包导出对话框
        /// </summary>
        /// <param name="node">要导出的进程节点</param>
        public NodePackageExportDialog(ProcessItem node)
        {
            InitializeComponent();

            var viewModel = new NodePackageExportDialogViewModel(
                node,
                result =>
                {
                    try
                    {
                        DialogResult = result;
                    }
                    catch (InvalidOperationException)
                    {
                        Close();
                    }
                }
            );

            DataContext = viewModel;
        }
    }
}
