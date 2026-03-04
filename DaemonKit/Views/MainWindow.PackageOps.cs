using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;

namespace DaemonKit
{
    public partial class MainWindow
    {
        #region 软件包操作进度处理

        private Window _currentPackageDialog;

        /// <summary>
        /// 处理软件包操作进度更新
        /// </summary>
        private void OnPackageProgressUpdate(PackageProgressInfo progressInfo)
        {
            if (progressInfo.IsActive)
            {
                // 更新进度文本和百分比
                PackageProgressText.Text =
                    progressInfo.OperationType == PackageOperationType.Export
                        ? "正在打包..."
                        : "正在安装...";
                PackageProgressPercentage.Text = $"{progressInfo.ProgressPercentage:F0}%";

                // 更新对话框引用（如果提供）
                if (progressInfo.DialogInstance != null)
                {
                    _currentPackageDialog = progressInfo.DialogInstance as Window;
                }

                // 如果有对话框引用且窗口是隐藏的，显示状态栏进度
                if (_currentPackageDialog != null && !_currentPackageDialog.IsVisible)
                {
                    PackageProgressItem.Visibility = Visibility.Visible;
                }
            }
            else
            {
                // 操作完成，隐藏进度并清空引用
                PackageProgressItem.Visibility = Visibility.Collapsed;
                _currentPackageDialog = null;
            }
        }

        /// <summary>
        /// 点击进度按钮唤起对话框
        /// </summary>
        private void PackageProgressButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPackageDialog != null)
            {
                // 检查窗口是否已关闭（PresentationSource为null表示窗口已关闭）
                if (PresentationSource.FromVisual(_currentPackageDialog) == null)
                {
                    // 窗口已关闭，清理状态栏
                    HidePackageProgressInStatusBar();
                    return;
                }

                // 显示窗口
                _currentPackageDialog.Show();
                _currentPackageDialog.WindowState = System.Windows.WindowState.Normal;
                _currentPackageDialog.Activate();

                // 禁用主窗口，恢复模态效果
                this.IsEnabled = false;
            }
        }

        /// <summary>
        /// 显示状态栏进度指示器（用于进度窗口隐藏时）
        /// </summary>
        public void ShowPackageProgressInStatusBar()
        {
            // 直接设置为可见，因为隐藏时一定有活动的进度
            PackageProgressItem.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 隐藏状态栏软件包操作进度并清空引用
        /// </summary>
        public void HidePackageProgressInStatusBar()
        {
            PackageProgressItem.Visibility = Visibility.Collapsed;
            _currentPackageDialog = null;
        }

        /// <summary>
        /// 打开传输列表窗口（单例模式）
        /// </summary>
        /// <param name="tabIndex">要激活的Tab索引：0=上传, 1=下载, -1=保持不变（默认）</param>
        public void ShowTransferListWindow(int tabIndex = -1)
        {
            if (_transferListWindow != null)
            {
                // 窗口已存在，检查是否被关闭了
                if (PresentationSource.FromVisual(_transferListWindow) != null)
                {
                    // 切换到指定Tab
                    if (
                        tabIndex >= 0
                        && _transferListWindow.DataContext
                            is ViewModels.TransferListViewModel existingVm
                    )
                    {
                        existingVm.SelectedTabIndex = tabIndex;
                    }
                    _transferListWindow.Activate();
                    return;
                }
                _transferListWindow = null;
            }

            var vm = new ViewModels.TransferListViewModel(_transferTaskManager, _p2pService);
            if (tabIndex >= 0)
            {
                vm.SelectedTabIndex = tabIndex;
            }
            _transferListWindow = new Views.TransferListWindow(vm);
            _transferListWindow.Owner = this;
            _transferListWindow.Show();
        }

        /// <summary>
        /// 打开资源库窗口（单例模式），可选预设设备过滤
        /// </summary>
        private void ShowResourceLibraryWindow(string deviceFilter = null)
        {
            if (_resourceLibraryWindow != null)
            {
                if (PresentationSource.FromVisual(_resourceLibraryWindow) != null)
                {
                    // 窗口已存在，如果有过滤条件则更新搜索
                    if (
                        !string.IsNullOrEmpty(deviceFilter)
                        && _resourceLibraryWindow.DataContext
                            is ViewModels.ResourceLibraryViewModel existingVM
                    )
                    {
                        existingVM.SearchText = deviceFilter;
                    }
                    _resourceLibraryWindow.Activate();
                    return;
                }
                _resourceLibraryWindow = null;
            }

            var panelVM = _table?.ViewModel;
            if (panelVM == null)
            {
                NLogger.Warn("[资源库] 联调面板ViewModel未初始化");
                return;
            }

            var vm = new ViewModels.ResourceLibraryViewModel(panelVM);
            if (!string.IsNullOrEmpty(deviceFilter))
            {
                vm.SearchText = deviceFilter;
            }
            _resourceLibraryWindow = new Views.ResourceLibraryWindow(vm);
            _resourceLibraryWindow.Show();
        }

        /// <summary>
        /// 更新状态栏传输状态显示
        /// </summary>
        private void UpdateTransferStatusBar()
        {
            var activeCount = _transferTaskManager.ActiveCount;
            if (activeCount > 0)
            {
                TransferStatusItem.Visibility = Visibility.Visible;
                var speed = _transferTaskManager.TotalSpeedDisplay;
                var upload = _transferTaskManager.UploadCount;
                var download = _transferTaskManager.DownloadCount;
                var parts = new List<string>();
                if (upload > 0)
                    parts.Add($"↑{upload}");
                if (download > 0)
                    parts.Add($"↓{download}");
                TransferStatusText.Text = $"{string.Join(" ", parts)} {speed}";
            }
            else
            {
                TransferStatusItem.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 更新状态栏系统资源显示。
        /// </summary>
        private void UpdateSystemStatusBar(SystemStatusSnapshot snapshot)
        {
            try
            {
                var cpuText = $"CPU {snapshot.CpuUsagePercent:F0}%";
                var memText = $"内存 {snapshot.MemoryUsagePercent:F0}%";
                var gpuText = snapshot.GpuUsagePercent.HasValue
                    ? $"GPU {snapshot.GpuUsagePercent.Value:F0}%"
                    : "GPU --";

                SystemStatusText.Text = $"{cpuText} | {memText} | {gpuText}";
                SystemStatusItem.ToolTip =
                    $"CPU: {snapshot.CpuUsagePercent:F1}%\n"
                    + $"内存: {snapshot.MemoryUsedGb:F1}/{snapshot.MemoryTotalGb:F1} GB ({snapshot.MemoryUsagePercent:F1}%)\n"
                    + (
                        snapshot.GpuUsagePercent.HasValue
                            ? $"GPU: {snapshot.GpuUsagePercent.Value:F1}%"
                            : "GPU: 不可用"
                    );
            }
            catch (Exception ex)
            {
                // 监控展示异常必须静默降级，不能影响主流程
                NLogger.Warn("[系统监控] 更新状态栏异常（已忽略）: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 评估关键阈值告警（仅写日志，不中断主流程）。
        /// </summary>
        private void EvaluateSystemCriticalAlerts(SystemStatusSnapshot snapshot)
        {
            try
            {
                // 1) 内存告警：达到阈值立即告警
                var memoryCriticalThreshold = Math.Clamp(
                    AppSettings.CriticalMemoryUsagePercent,
                    50,
                    100
                );
                var memoryRecoveryThreshold = Math.Max(0, memoryCriticalThreshold - 5);
                if (snapshot.MemoryUsagePercent >= memoryCriticalThreshold)
                {
                    if (!_memoryCriticalActive)
                    {
                        _memoryCriticalActive = true;
                        NLogger.Warn(
                            "[系统监控告警] 内存使用率过高: {Usage:F1}% (阈值: {Threshold}%)，已用 {Used:F1}/{Total:F1} GB",
                            snapshot.MemoryUsagePercent,
                            memoryCriticalThreshold,
                            snapshot.MemoryUsedGb,
                            snapshot.MemoryTotalGb
                        );
                    }
                }
                else if (
                    _memoryCriticalActive && snapshot.MemoryUsagePercent <= memoryRecoveryThreshold
                )
                {
                    _memoryCriticalActive = false;
                    NLogger.Info(
                        "[系统监控恢复] 内存使用率恢复正常: {Usage:F1}% (恢复阈值: ≤{Threshold}%)",
                        snapshot.MemoryUsagePercent,
                        memoryRecoveryThreshold
                    );
                }

                // 2) CPU 告警：连续 3 次高负载才报警，避免瞬时抖动误报
                var cpuCriticalThreshold = Math.Clamp(AppSettings.CriticalCpuUsagePercent, 50, 100);
                var cpuRecoveryThreshold = Math.Max(0, cpuCriticalThreshold - 10);
                if (snapshot.CpuUsagePercent >= cpuCriticalThreshold)
                {
                    _cpuCriticalConsecutiveCount++;
                    if (!_cpuCriticalActive && _cpuCriticalConsecutiveCount >= 3)
                    {
                        _cpuCriticalActive = true;
                        NLogger.Warn(
                            "[系统监控告警] CPU 持续高负载: {Usage:F1}% (阈值: {Threshold}%，连续 {Count} 次)",
                            snapshot.CpuUsagePercent,
                            cpuCriticalThreshold,
                            _cpuCriticalConsecutiveCount
                        );
                    }
                }
                else
                {
                    _cpuCriticalConsecutiveCount = 0;
                    if (_cpuCriticalActive && snapshot.CpuUsagePercent <= cpuRecoveryThreshold)
                    {
                        _cpuCriticalActive = false;
                        NLogger.Info(
                            "[系统监控恢复] CPU 负载恢复正常: {Usage:F1}% (恢复阈值: ≤{Threshold}%)",
                            snapshot.CpuUsagePercent,
                            cpuRecoveryThreshold
                        );
                    }
                }

                // 3) GPU 告警：可用时才判断，连续 3 次高负载才报警
                if (snapshot.GpuUsagePercent.HasValue)
                {
                    var gpuCriticalThreshold = Math.Clamp(
                        AppSettings.CriticalGpuUsagePercent,
                        50,
                        100
                    );
                    var gpuRecoveryThreshold = Math.Max(0, gpuCriticalThreshold - 10);

                    if (snapshot.GpuUsagePercent.Value >= gpuCriticalThreshold)
                    {
                        _gpuCriticalConsecutiveCount++;
                        if (!_gpuCriticalActive && _gpuCriticalConsecutiveCount >= 3)
                        {
                            _gpuCriticalActive = true;
                            NLogger.Warn(
                                "[系统监控告警] GPU 持续高负载: {Usage:F1}% (阈值: {Threshold}%，连续 {Count} 次)",
                                snapshot.GpuUsagePercent.Value,
                                gpuCriticalThreshold,
                                _gpuCriticalConsecutiveCount
                            );
                        }
                    }
                    else
                    {
                        _gpuCriticalConsecutiveCount = 0;
                        if (
                            _gpuCriticalActive
                            && snapshot.GpuUsagePercent.Value <= gpuRecoveryThreshold
                        )
                        {
                            _gpuCriticalActive = false;
                            NLogger.Info(
                                "[系统监控恢复] GPU 负载恢复正常: {Usage:F1}% (恢复阈值: ≤{Threshold}%)",
                                snapshot.GpuUsagePercent.Value,
                                gpuRecoveryThreshold
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 告警评估异常不可影响主进程
                NLogger.Warn("[系统监控] 告警评估异常（已忽略）: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 点击状态栏传输指示器打开传输列表窗口
        /// </summary>
        private void TransferStatusButton_Click(object sender, RoutedEventArgs e)
        {
            ShowTransferListWindow();
        }

        #endregion

        #region 远程导出进程包

        /// <summary>
        /// 导出进程包到共享文件夹（用于远程下载）
        /// </summary>
        private async Task<string> ExportPackageToSharedFolderAsync(
            ProcessItem rootNode,
            IProgress<string> statusProgress = null
        )
        {
            try
            {
                // 确保共享目录存在
                var sharedDir = AppPathes.SharedFilesDir;
                if (!Directory.Exists(sharedDir))
                {
                    Directory.CreateDirectory(sharedDir);
                }

                // 生成导出文件名
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var projectName = rootNode?.MetaData?.Name ?? Environment.MachineName;
                // 清理非法文件名字符
                projectName = string.Join("_", projectName.Split(Path.GetInvalidFileNameChars()));
                var packageName = $"{projectName}_{timestamp}.dkp.zip";
                var packagePath = Path.Combine(sharedDir, packageName);

                NLogger.Info("[Remote Export] 开始导出进程包到共享目录: {PackagePath}", packagePath);

                // 获取所有配置文件
                var configFiles = new List<string>
                {
                    AppPathes.TreeViewDataPath,
                    AppPathes.AppSettingPath,
                    AppPathes.GlobalSchedulePath,
                    AppPathes.ScheduleConfigPath,
                    AppPathes.HotkeyConfigPath,
                    AppPathes.ExtensionConfigPath
                }
                    .Where(File.Exists)
                    .ToList();

                // 收集进程树所有节点
                var allNodes = new List<ProcessItem> { rootNode };
                CollectAllNodes(rootNode, allNodes);

                // 执行导出（包含程序文件，完整备份）
                var success = await ExportImportService.ExportPackageAsync(
                    packagePath,
                    configFiles,
                    allNodes,
                    includeAllPrograms: true, // 远程导出时包含程序文件，完整备份
                    description: $"远程导出 - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    statusProgress: statusProgress
                );

                if (success)
                {
                    NLogger.Info("[Remote Export] 进程包导出成功: {PackagePath}", packagePath);
                    return packageName; // 返回文件名供通知使用
                }
                else
                {
                    NLogger.Error("[Remote Export] 进程包导出失败");
                    return null;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[Remote Export] 导出进程包异常: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 获取与远程IP通信的本地IP地址
        /// </summary>
        private string GetLocalIPForRemote(string remoteIP)
        {
            try
            {
                using (
                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Dgram,
                        0
                    )
                )
                {
                    socket.Connect(remoteIP, 65530);
                    var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// 递归收集所有子节点
        /// </summary>
        private void CollectAllNodes(ProcessItem node, List<ProcessItem> nodes)
        {
            if (node?.Children == null)
                return;
            foreach (var child in node.Children)
            {
                nodes.Add(child);
                CollectAllNodes(child, nodes);
            }
        }

        #endregion
    }
}
