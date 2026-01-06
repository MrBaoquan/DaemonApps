using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
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

namespace DaemonKit
{
    /// <summary>
    /// Settings.xaml 的交互逻辑
    /// </summary>
    public partial class Settings : ReactiveWindow<SettingsViewModel>
    {
        public Settings()
        {
            InitializeComponent();
            ViewModel = new SettingsViewModel();

            this.WhenActivated(d =>
            {
                DataContext = ViewModel;

                ViewModel.Confirm
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        try
                        {
                            this.DialogResult = true;
                        }
                        catch
                        {
                            // 如果设置失败，直接关闭窗口
                        }
                        this.Close();
                    });

                ViewModel.Cancel
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        try
                        {
                            this.DialogResult = false;
                        }
                        catch
                        {
                            // 如果设置失败，直接关闭窗口
                        }
                        this.Close();
                    });
            });
        }
    }
}
