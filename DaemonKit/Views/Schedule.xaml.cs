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
using DaemonKit.Models;
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

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            // 获取点击按钮对应的任务配置
            var button = sender as Button;
            if (button?.DataContext is Models.ScheduleTaskConfig config)
            {
                // 创建副本以避免直接修改原对象
                var configCopy = new Models.ScheduleTaskConfig
                {
                    Name = config.Name,
                    Trigger = config.Trigger,
                    Action = config.Action,
                    DailyTime = config.DailyTime,
                    DelaySeconds = config.DelaySeconds,
                    Description = config.Description,
                    Enabled = config.Enabled,
                    TargetNodeId = config.TargetNodeId,
                    TargetNodeName = config.TargetNodeName,
                    ClickX = config.ClickX,
                    ClickY = config.ClickY,
                    MaxExecuteCount = config.MaxExecuteCount
                };

                // 打开编辑对话框
                var dialog = new ScheduleTaskEditDialog(configCopy, ViewModel.RootProcessNode)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    // 用户点击确定，应用更改
                    config.Name = configCopy.Name;
                    config.Trigger = configCopy.Trigger;
                    config.Action = configCopy.Action;
                    config.DailyTime = configCopy.DailyTime;
                    config.DelaySeconds = configCopy.DelaySeconds;
                    config.Description = configCopy.Description;
                    config.Enabled = configCopy.Enabled;
                    config.TargetNodeId = configCopy.TargetNodeId;
                    config.TargetNodeName = configCopy.TargetNodeName;
                    config.ClickX = configCopy.ClickX;
                    config.ClickY = configCopy.ClickY;
                    config.MaxExecuteCount = configCopy.MaxExecuteCount;

                    // 编辑后立即保存
                    ViewModel?.SaveTaskConfigs();
                }
            }
        }
    }
}
