using DaemonKit.ViewModels;
using DaemonKit.Models;
using System;
using System.Collections.Generic;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class NodePackageDialog : Window
    {
        public NodePackageDialog()
            : this(null, null, null) { }

        /// <summary>
        /// 创建节点包对话框
        /// </summary>
        /// <param name="packagePath">包文件路径</param>
        /// <param name="allProcessNodes">所有进程节点的扁平列表（用于匹配）</param>
        /// <param name="rootNode">进程树根节点（用于新增节点）</param>
        public NodePackageDialog(
            string packagePath,
            List<ProcessItem> allProcessNodes,
            ProcessItem rootNode = null
        )
        {
            InitializeComponent();

            var viewModel = new NodePackageDialogViewModel(
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
                },
                allProcessNodes ?? new List<ProcessItem>(),
                rootNode
            );

            DataContext = viewModel;

            if (!string.IsNullOrEmpty(packagePath))
            {
                Loaded += async (s, e) =>
                {
                    await viewModel.LoadPackageAsync(packagePath);
                };
            }
        }
    }
}
