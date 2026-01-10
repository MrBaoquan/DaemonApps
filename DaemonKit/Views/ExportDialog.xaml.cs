using DaemonKit.Models;
using DaemonKit.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class ExportDialog : Window
    {
        public ExportDialog(IEnumerable<ProcessItem> processTree)
        {
            InitializeComponent();
            DataContext = new ExportDialogViewModel(processTree, result => DialogResult = result);
        }
    }
}
