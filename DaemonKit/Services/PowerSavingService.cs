using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DaemonKit.Models;
using DaemonKit.PowerSaving;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Services
{
    /// <summary>
    /// 省电模式服务 - 封装省电模式管理逻辑
    /// </summary>
    public class PowerSavingService
    {
        private readonly PowerSavingViewModel _viewModel;
        private PowerSavingWindow? _window;

        public PowerSavingService()
        {
            _viewModel = new PowerSavingViewModel();
        }

        /// <summary>
        /// 获取 PowerSavingViewModel 实例
        /// </summary>
        public PowerSavingViewModel ViewModel => _viewModel;

        /// <summary>
        /// 初始化服务，加载设置
        /// </summary>
        public void Initialize(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _viewModel.LoadSettings(settings);
            NLogger.Info("[PowerSavingService] 服务已初始化");
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        public void SaveSettings(AppSettings settings)
        {
            _viewModel.SaveSettings(settings);
        }

        /// <summary>
        /// 应用省电模式
        /// </summary>
        public Task ApplyPowerSavingAsync()
        {
            _viewModel.ApplyPowerSavingCommand.Execute().Subscribe();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复正常模式
        /// </summary>
        public Task RestoreNormalAsync()
        {
            _viewModel.RestoreNormalCommand.Execute().Subscribe();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 获取或创建省电窗口
        /// </summary>
        public PowerSavingWindow GetOrCreateWindow(AppSettings settings)
        {
            if (_window == null || !_window.IsLoaded || _window.Parent != null)
            {
                try
                {
                    _window?.Close();
                }
                catch { }

                _window = new PowerSavingWindow(settings);

                // 设置 DataContext 为内部 ViewModel
                _window.DataContext = _viewModel;

                // 订阅关闭事件
                _window.Closed += (s, e) =>
                {
                    SaveSettings(settings);
                    NLogger.Info("[PowerSavingService] 窗口已关闭，设置已保存");
                };
            }

            return _window;
        }

        /// <summary>
        /// 当前是否处于省电模式
        /// </summary>
        public bool IsPowerSavingMode => _viewModel.IsPowerSavingMode;

        /// <summary>
        /// 是否启用空闲自动省电
        /// </summary>
        public bool EnableIdleAutoPowerSaving
        {
            get => _viewModel.EnableIdleAutoPowerSaving;
            set => _viewModel.EnableIdleAutoPowerSaving = value;
        }
    }
}
