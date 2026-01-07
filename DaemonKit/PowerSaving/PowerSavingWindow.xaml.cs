using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;

namespace DaemonKit.PowerSaving
{
    public partial class PowerSavingWindow : Window, INotifyPropertyChanged
    {
        private PowerSavingViewModel _viewModel;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public PowerSavingWindow()
        {
            InitializeComponent();
            _viewModel = new PowerSavingViewModel();
            DataContext = _viewModel;
        }

        public PowerSavingWindow(AppSettings settings)
            : this()
        {
            _viewModel.LoadSettings(settings);
        }

        /// <summary>
        /// 切换到正常模式
        /// </summary>
        private void NormalModeButton_Click(object sender, RoutedEventArgs e)
        {
            // 防止事件冒泡导致重复触发
            e.Handled = true;

            if (DataContext is PowerSavingViewModel vm)
            {
                DNHper.NLogger.Info(
                    $"[PowerSaving] 点击正常模式按钮，当前 IsBusy={vm.IsBusy}, IsPowerSavingMode={vm.IsPowerSavingMode}"
                );

                // 如果已经是正常模式，不需要再次执行
                if (!vm.IsPowerSavingMode)
                {
                    DNHper.NLogger.Info("[PowerSaving] 已经是正常模式，跳过");
                    return;
                }

                // ReactiveCommand.Execute() 返回 IObservable，必须订阅才会执行
                vm.RestoreNormalCommand
                    .Execute()
                    .Subscribe(
                        _ => { },
                        ex => DNHper.NLogger.Error($"[PowerSaving] 切换正常模式失败: {ex.Message}")
                    );
            }
        }

        /// <summary>
        /// 切换到省电模式
        /// </summary>
        private void PowerSavingModeButton_Click(object sender, RoutedEventArgs e)
        {
            // 防止事件冒泡导致重复触发
            e.Handled = true;

            if (DataContext is PowerSavingViewModel vm)
            {
                DNHper.NLogger.Info(
                    $"[PowerSaving] 点击省电模式按钮，当前 IsBusy={vm.IsBusy}, IsPowerSavingMode={vm.IsPowerSavingMode}"
                );

                // 如果已经是省电模式，不需要再次执行
                if (vm.IsPowerSavingMode)
                {
                    DNHper.NLogger.Info("[PowerSaving] 已经是省电模式，跳过");
                    return;
                }

                // ReactiveCommand.Execute() 返回 IObservable，必须订阅才会执行
                vm.ApplyPowerSavingCommand
                    .Execute()
                    .Subscribe(
                        _ => { },
                        ex => DNHper.NLogger.Error($"[PowerSaving] 切换省电模式失败: {ex.Message}")
                    );
            }
        }
    }
}
