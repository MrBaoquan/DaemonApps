using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DaemonKit.PowerSaving
{
    public partial class PowerSavingWindow : Window, INotifyPropertyChanged
    {
        private PowerSavingViewModel _viewModel;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public PowerSavingWindow()
        {
            InitializeComponent();
            _viewModel = new PowerSavingViewModel();
            DataContext = _viewModel;

            // 订阅按钮点击事件
            if (FindName("NormalModeButton") is Button normalBtn)
            {
                normalBtn.Click += NormalModeButton_Click;
            }
            if (FindName("PowerSavingModeButton") is Button savingBtn)
            {
                savingBtn.Click += PowerSavingModeButton_Click;
            }
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
            if (DataContext is PowerSavingViewModel vm)
            {
                // 先设置模式，然后强制应用亮度（即使已经是正常模式）
                vm.IsPowerSavingMode = false;
                // 如果已经是正常模式，手动触发恢复
                _ = vm.RestoreNormalCommand.Execute();
            }
        }

        /// <summary>
        /// 切换到省电模式
        /// </summary>
        private void PowerSavingModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PowerSavingViewModel vm)
            {
                // 先设置模式，然后强制应用亮度（即使已经是省电模式）
                vm.IsPowerSavingMode = true;
                // 如果已经是省电模式，手动触发应用
                _ = vm.ApplyPowerSavingCommand.Execute();
            }
        }
    }
}
