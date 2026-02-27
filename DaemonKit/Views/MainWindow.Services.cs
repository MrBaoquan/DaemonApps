using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DaemonKit.Core;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit
{
    public partial class MainWindow
    {
        #region Initialization Methods

        /// <summary>
        /// 后台服务初始化（在窗口显示后异步执行）
        /// </summary>
        private async void InitializeBackgroundServices(DateTime startTime)
        {
            _uiCheckpoint = "initBgSvc_enter";
            try
            {
                await Task.Run(() =>
                {
                    // 异步获取硬件信息
                    Dispatcher.InvokeAsync(() => FetchHardwareInfo());
                });
                _uiCheckpoint = "initBgSvc_afterTaskRun";

                // 初始化新的任务调度引擎（使用全局配置）
                _scheduleTaskEngine = new ScheduleTaskEngine(rootProcessNode, GlobalSchedule)
                {
                    ConfirmHandler = ConfirmSchedulePowerActionAsync,
                    PowerSavingViewModelProvider = () => _powerSavingService.ViewModel
                };
                _scheduleTaskEngine.TaskExecuting += (sender, context) =>
                {
                    NLogger.Info(
                        "[任务] 执行: [{TaskName}] - {Action}",
                        context.TaskConfig.Name,
                        context.TaskConfig.Action
                    );
                };
                _scheduleTaskEngine.TaskExecuted += (sender, context) =>
                {
                    if (context.IsSuccess)
                    {
                        NLogger.Info("[任务] 完成: {Result}", context.Result);
                    }
                    else
                    {
                        NLogger.Error("[任务] 失败: {ErrorMessage}", context.ErrorMessage);
                    }
                };

                // 订阅全局计划任务启用状态变化，自动保存配置
                GlobalSchedule
                    .WhenAnyValue(x => x.ScheduleTasksEnabled)
                    .Skip(1) // 跳过初始值
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(enabled =>
                    {
                        saveConfig();
                        NLogger.Info("[任务] 计划任务已{Status}", enabled ? "启用" : "禁用");
                    });

                // 检查是否首次启动并启动计划任务监控
                CheckFirstStartToday();
                StartScheduleTaskMonitor();

                NLogger.Info(
                    "DaemonKit 就绪 (耗时 {ElapsedMs}ms)",
                    (DateTime.Now - startTime).TotalMilliseconds.ToString("F0")
                );
            }
            catch (Exception ex)
            {
                NLogger.Error("后台服务初始化异常: {ErrorMessage}", ex.Message);
                NLogger.Error("堆栈: {StackTrace}", ex.StackTrace);
            }
        }

        #endregion
    }
}
