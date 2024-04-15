using Avalonia.Controls;
using Avalonia.ReactiveUI;
using ReactiveUI;
using UNICopy.ViewModels;

namespace UNICopy.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {
                this.listCopy.ItemsSource = ViewModel?.FileCopyList;
            });
        }
    }
}
