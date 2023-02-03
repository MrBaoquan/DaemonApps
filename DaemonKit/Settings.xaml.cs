using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DaemonKit.Core;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;
namespace DaemonKit {
    /// <summary>
    /// Settings.xaml 的交互逻辑
    /// </summary>
    public partial class Settings : ReactiveWindow<SettingsViewModel> {
        public Settings () {
            InitializeComponent ();
            this.WhenActivated (d => {
                ViewModel = new SettingsViewModel ();
                DataContext = ViewModel;

                ViewModel.Cancel.Subscribe (_ => {
                    this.Hide ();
                });
            });
        }

        protected override void OnClosing (CancelEventArgs e) {
            base.OnClosing (e);
            this.Hide ();
            e.Cancel = true;
        }
    }
}