using DaemonKit.ViewModels;
using System.Windows;

namespace DaemonKit.Views
{
    public partial class ImportDialog : Window
    {
        public ImportDialog()
        {
            InitializeComponent();
            DataContext = new ImportDialogViewModel(result => DialogResult = result);
        }
    }
}
