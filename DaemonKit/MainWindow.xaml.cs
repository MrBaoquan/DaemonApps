using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using DaemonKit.Core;
using DNHper;
using ReactiveUI;

namespace DaemonKit {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<MainViewModel> {
        //public static RoutedCommand OpenProcess = new RoutedCommand ();
        public MainWindow () {
            InitializeComponent ();
            ViewModel = new MainViewModel ();

            this.WhenActivated (disposables => {
                DataContext = this.ViewModel;

                this.BindCommand (this.ViewModel, vm => vm.DisplayCommand, v => v.menu_cut).DisposeWith (disposables);
                //this.Bind (this.ViewModel, vm => vm.Text, v => v.textbox.Text).DisposeWith (disposables);

                ProcessItem rootProcess = new ProcessItem { Name = "进程树" };
                ProcessItem _mainProcess = new ProcessItem { Name = "创建数字都市" };
                rootProcess.AddChild (_mainProcess);
                this.ProcssTree.Items.Add (rootProcess);

                this.ProcssTree.ContextMenuOpening += ((_1, _2) => {
                    _2.Handled = false;
                });
            });
        }

    }
}