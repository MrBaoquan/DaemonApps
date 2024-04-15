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
using ReactiveUI;

namespace DaemonKit
{
    /// <summary>
    /// Schedule.xaml 的交互逻辑
    /// </summary>
    public partial class Schedule : ReactiveWindow<ScheduleViewModel>
    {
        public Schedule()
        {
            ViewModel = new ScheduleViewModel();
            DataContext = ViewModel;
            InitializeComponent();

            this.WhenActivated(disposables => { });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            base.OnClosing(e);
        }
    }
}
