using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DaemonKit.Views;
using DNHper;
using ReactiveUI;

namespace DaemonKit
{
    public partial class MainWindow
    {
        #region 计划任务执行逻辑

        /// <summary>
        /// 检查是否是每天首次启动
        /// </summary>
        private static void CheckFirstStartToday()
        {
            var markerFile = Path.Combine(Path.GetTempPath(), "DaemonKit_LastStartDate.txt");
            var today = DateTime.Now.Date.ToString("yyyy-MM-dd");

            if (File.Exists(markerFile))
            {
                var lastStartDate = File.ReadAllText(markerFile).Trim();
                isFirstStartToday = (lastStartDate != today);
            }
            else
            {
                isFirstStartToday = true;
            }

            // 更新标记文件
            File.WriteAllText(markerFile, today);
            NLogger.Info(
                "程序启动时间: {AppStartTime}, 是否当日首次启动: {IsFirstStartToday}",
                appStartTime,
                isFirstStartToday
            );
        }

        /// <summary>
        /// 启动计划任务监控
        /// </summary>
        private void StartScheduleTaskMonitor()
        {
            // 使用新的任务调度引擎进行任务检查和执行
            Observable
                .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async _ =>
                {
                    // 新引擎检查新格式的任务
                    if (_scheduleTaskEngine != null)
                    {
                        await _scheduleTaskEngine.CheckAndExecutePendingTasks();
                    }
                });
        }

        /// <summary>
        /// 执行计划任务
        /// </summary>
        private async void ExecuteScheduleTask(ScheduleItem item)
        {
            item.MarkAsExecuted();
            NLogger.Info("开始执行任务: {TaskType}", item.TaskType);

            try
            {
                switch (item.TaskType)
                {
                    case ScheduleTaskType.Shutdown:
                        await ExecuteShutdownTask();
                        break;
                    case ScheduleTaskType.Restart:
                        await ExecuteRestartTask();
                        break;
                    case ScheduleTaskType.RestartApp:
                        await ExecuteRestartAppTask();
                        break;
                    case ScheduleTaskType.Start:
                        rootProcessNode.RunNode();
                        break;
                    case ScheduleTaskType.Stop:
                        rootProcessNode.KillNode();
                        break;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("任务执行失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"任务执行失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        /// <summary>
        /// 执行关机任务
        /// </summary>
        private async Task ExecuteShutdownTask()
        {
            if (AppSettings.EnableCountdownConfirm)
            {
                var confirmed = await ShowCountdownConfirm("系统关机", "系统将在倒计时结束后关机");
                if (!confirmed)
                    return;
            }

            NLogger.Info("执行关机命令");
            Process.Start("shutdown", "/s /t 0");
        }

        /// <summary>
        /// 执行电脑重启任务
        /// </summary>
        private async Task ExecuteRestartTask()
        {
            if (AppSettings.EnableCountdownConfirm)
            {
                var confirmed = await ShowCountdownConfirm("系统重启", "系统将在倒计时结束后重启");
                if (!confirmed)
                    return;
            }

            NLogger.Info("执行重启命令");
            Process.Start("shutdown", "/r /t 0");
        }

        /// <summary>
        /// 执行程序重启任务
        /// </summary>
        private async Task ExecuteRestartAppTask()
        {
            if (AppSettings.EnableCountdownConfirm)
            {
                var confirmed = await ShowCountdownConfirm("程序重启", "程序将在倒计时结束后重启");
                if (!confirmed)
                    return;
            }

            NLogger.Info("执行程序重启命令");
            RestartApplication();
        }

        /// <summary>
        /// 显示倒计时确认对话框
        /// </summary>
        private async Task<bool> ShowCountdownConfirm(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>();

            await Dispatcher.InvokeAsync(() =>
            {
                // 若已有倒计时弹窗，则重置倒计时并复用，避免重复弹窗
                if (_activeCountdownDialog != null && _activeCountdownDialog.IsVisible)
                {
                    _countdownAwaiters.Add(tcs);
                    _activeCountdownDialog.ResetCountdown(10);
                    _activeCountdownDialog.Activate();
                    return;
                }

                _countdownAwaiters.Clear();
                _countdownAwaiters.Add(tcs);

                _activeCountdownDialog = new CountdownConfirmDialog
                {
                    ViewModel = new CountdownConfirmViewModel(title, message, 10)
                };

                _activeCountdownDialog.Closed += CountdownDialog_Closed;
                _activeCountdownDialog.ShowDialog();
            });

            return await tcs.Task;
        }

        private void CountdownDialog_Closed(object? sender, EventArgs e)
        {
            bool result = (_activeCountdownDialog?.DialogResult ?? false) == true;

            foreach (var waiter in _countdownAwaiters)
            {
                waiter.TrySetResult(result);
            }

            _countdownAwaiters.Clear();

            if (_activeCountdownDialog != null)
            {
                _activeCountdownDialog.Closed -= CountdownDialog_Closed;
                _activeCountdownDialog = null;
            }
        }

        private async Task<bool> ConfirmSchedulePowerActionAsync(Models.ScheduleTaskAction action)
        {
            if (!AppSettings.EnableCountdownConfirm)
            {
                return true;
            }

            return action switch
            {
                Models.ScheduleTaskAction.ShutdownSystem
                    => await ShowCountdownConfirm("系统关机", "系统将在倒计时结束后关机"),
                Models.ScheduleTaskAction.RestartSystem
                    => await ShowCountdownConfirm("系统重启", "系统将在倒计时结束后重启"),
                _ => true
            };
        }

        /// <summary>
        /// 重启应用程序
        /// </summary>
        private void RestartApplication()
        {
            try
            {
                // 获取当前程序路径
                var exePath = Process.GetCurrentProcess().MainModule.FileName;

                // 启动新实例
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        Verb = "runas" // 以管理员权限运行
                    }
                );

                // 退出当前实例
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                NLogger.Error("程序重启失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"程序重启失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        // GetIdleDuration 和 HandleIdleTimeout 已迁移到 IdleMonitorService

        private void TriggerScreenshot()
        {
            try
            {
                var overlay = PickerOverlay.GetInstance();
                // 如果已经在显示中，直接激活并返回，防止多实例
                if (overlay.IsVisible)
                {
                    overlay.Activate();
                    return;
                }

                overlay.Mode = PickerOverlay.PickerMode.Screenshot;
                this.WindowState = System.Windows.WindowState.Minimized;
                if (overlay.ShowDialog() == true)
                {
                    NLogger.Info("截图保存: {Result}", overlay.Result);
                }
                this.WindowState = System.Windows.WindowState.Minimized;
            }
            catch (Exception ex)
            {
                NLogger.Error("截图失败: {ErrorMessage}", ex.Message);
            }
        }

        private void OpenScreenshotFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var screenshotPath = AppPathes.ScreenshotsDir;
                if (!Directory.Exists(screenshotPath))
                {
                    Directory.CreateDirectory(screenshotPath);
                }
                WinAPI.OpenProcess("explorer.exe", screenshotPath);
                NLogger.Info("打开截图文件夹: {ScreenshotPath}", screenshotPath);
            }
            catch (Exception ex)
            {
                NLogger.Error("打开截图文件夹失败: {ErrorMessage}", ex.Message);
            }
        }

        private void ExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 检查是否已有导入/导出在进行
                if (_isImporting)
                {
                    MessageBox.Show(
                        "导入正在进行中，请等待导入完成后再进行导出",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                if (_isExporting)
                {
                    MessageBox.Show(
                        "导出已在进行中，请等待完成",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                // 传入包含根节点的列表，确保导出时能看到并选择根节点
                var exportDialog = new ExportDialog(new[] { rootProcessNode }) { Owner = this };

                // 设置导出标志
                _isExporting = true;

                exportDialog.ShowDialog();

                // 导出完成，重置标志
                _isExporting = false;
            }
            catch (Exception ex)
            {
                _isExporting = false;
                NLogger.Error("打开导出对话框失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"打开导出对话框失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void ImportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 检查是否已有导入/导出在进行
                if (_isExporting)
                {
                    MessageBox.Show(
                        "导出正在进行中，请等待导出完成后再进行导入",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                if (_isImporting)
                {
                    MessageBox.Show(
                        "导入已在进行中，请等待完成",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                // 导入前确认：终止进程树是破坏性操作
                var confirmResult = MessageBox.Show(
                    "导入配置包将终止当前运行的进程树，导入完成后需要手动重新启动进程树。\n\n是否继续？",
                    "导入确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (confirmResult != MessageBoxResult.Yes)
                    return;

                // 设置导入标志
                _isImporting = true;

                var importDialog = new ImportDialog { Owner = this };
                importDialog.ShowDialog();

                // 导入完成，重置标志
                _isImporting = false;

                // 注意：导入对话框关闭后，实际导入仍在 ProgressWindow 中异步执行
                // 热重载由 MessageBus "TreeBundleImportCompleted" 消息触发（导入真正完成后）
            }
            catch (Exception ex)
            {
                _isImporting = false;
                NLogger.Error("打开导入对话框失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"打开导入对话框失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        /// <summary>
        /// 打开备份管理窗口
        /// </summary>
        private void OpenBackupManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var backupWindow = new BackupManagerWindow { Owner = this };
                backupWindow.ShowDialog();

                // 如果有恢复操作，可能需要重新加载配置
                // 由恢复操作自行处理
            }
            catch (Exception ex)
            {
                NLogger.Error("打开备份管理窗口失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"打开备份管理窗口失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void OpenHotkeySettings_Click(object sender, RoutedEventArgs e)
        {
            var oldHotKeyEnabled = AppSettings.EnableGlobalHotKey;
            var hotkeyWindow = new HotkeySettingsWindow { Owner = this };
            hotkeyWindow.ViewModel.LoadFrom(AppSettings);
            var result = hotkeyWindow.ShowDialog();
            if (result == true)
            {
                hotkeyWindow.ViewModel.ApplyTo(AppSettings);
                saveConfig();

                if (AppSettings.EnableGlobalHotKey)
                {
                    Utils.RegisterHotKey(this, AppSettings);
                    NLogger.Info("已启用全局快捷键");
                }
                else
                {
                    Utils.UnRegisterHotKey(this);
                    if (oldHotKeyEnabled)
                    {
                        NLogger.Info("已禁用全局快捷键");
                    }
                }
            }
        }

        #endregion
    }
}
