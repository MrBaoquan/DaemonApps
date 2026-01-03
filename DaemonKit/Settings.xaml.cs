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

                ViewModel.Confirm.Subscribe(_ =>
                {
                    // 延迟设置 DialogResult，确保窗口已完全作为对话框显示
                    Dispatcher.BeginInvoke(
                        new Action(() =>
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
                        }),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle
                    );
                });

                ViewModel.Cancel.Subscribe(_ =>
                {
                    Dispatcher.BeginInvoke(
                        new Action(() =>
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
                        }),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle
                    );
                });
            });
        }
    }
}
