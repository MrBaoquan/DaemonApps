using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DaemonKit.Models;
using DNHper;

namespace DaemonKit.Services
{
    /// <summary>
    /// 崩溃检测服务
    /// 职责：
    /// 1. 监控进程崩溃（通过检测特定窗口标题）
    /// 2. 自动重启崩溃的进程树
    /// 3. 记录崩溃日志
    /// </summary>
    public class CrashDetectionService : IDisposable
    {
        private IDisposable? _monitorDisposable;
        private ProcessItem? _rootProcessNode;
        private string? _crashWindowTitles;
        private int _checkInterval = 200; // 默认200ms

        /// <summary>
        /// 崩溃检测事件（进程树重启时触发）
        /// </summary>
        public event EventHandler<CrashDetectedEventArgs>? CrashDetected;

        /// <summary>
        /// 启动崩溃监控
        /// </summary>
        /// <param name="rootNode">根进程节点</param>
        /// <param name="crashWindowTitles">崩溃窗口标题（用|分隔）</param>
        /// <param name="checkIntervalMs">检测间隔（毫秒），默认200ms</param>
        public void Start(ProcessItem rootNode, string crashWindowTitles, int checkIntervalMs = 200)
        {
            if (string.IsNullOrWhiteSpace(crashWindowTitles))
            {
                NLogger.Warn("未配置崩溃窗口标题，崩溃检测服务未启动");
                return;
            }

            _rootProcessNode = rootNode;
            _crashWindowTitles = crashWindowTitles;
            _checkInterval = checkIntervalMs;

            StartMonitoring();
        }

        /// <summary>
        /// 开始监控循环
        /// </summary>
        private void StartMonitoring()
        {
            _monitorDisposable = Observable
                .Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(_checkInterval))
                .Subscribe(_ =>
                {
                    CheckForCrash();
                });

            NLogger.Info($"崩溃检测服务已启动（检测间隔: {_checkInterval}ms）");
        }

        /// <summary>
        /// 检查是否有崩溃进程
        /// </summary>
        private void CheckForCrash()
        {
            if (_rootProcessNode == null || string.IsNullOrWhiteSpace(_crashWindowTitles))
                return;

            try
            {
                var crashWindows = _crashWindowTitles
                    .Split('|')
                    .Select(title => WinAPI.FindProcess(title))
                    .Where(process => process != default(Process))
                    .ToList();

                if (_rootProcessNode.IsRuning && crashWindows.Count > 0)
                {
                    NLogger.Warn($"检测到 {crashWindows.Count} 个崩溃进程窗口");

                    // 关闭所有崩溃窗口
                    crashWindows.ForEach(crashWindow =>
                    {
                        try
                        {
                            NLogger.Info(
                                $"关闭崩溃进程: {crashWindow.MainWindowTitle} (PID: {crashWindow.Id})"
                            );
                            crashWindow.Kill();
                        }
                        catch (Exception ex)
                        {
                            NLogger.Error($"关闭崩溃进程失败: {ex.Message}");
                        }
                    });

                    // 重启进程树
                    NLogger.Info("检测到崩溃进程，尝试重启进程树...");
                    _rootProcessNode.KillNode();
                    _rootProcessNode.RunNode();

                    // 触发崩溃事件
                    OnCrashDetected(
                        new CrashDetectedEventArgs
                        {
                            CrashWindowCount = crashWindows.Count,
                            CrashTime = DateTime.Now,
                            RootNodeName = _rootProcessNode.Name
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"崩溃检测异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发崩溃检测事件
        /// </summary>
        protected virtual void OnCrashDetected(CrashDetectedEventArgs e)
        {
            CrashDetected?.Invoke(this, e);
        }

        /// <summary>
        /// 停止崩溃监控
        /// </summary>
        public void Stop()
        {
            Dispose();
        }

        public void Dispose()
        {
            _monitorDisposable?.Dispose();
            _monitorDisposable = null;

            NLogger.Info("崩溃检测服务已停止");
        }
    }

    /// <summary>
    /// 崩溃检测事件参数
    /// </summary>
    public class CrashDetectedEventArgs : EventArgs
    {
        public int CrashWindowCount { get; set; }
        public DateTime CrashTime { get; set; }
        public string RootNodeName { get; set; } = string.Empty;
    }
}
