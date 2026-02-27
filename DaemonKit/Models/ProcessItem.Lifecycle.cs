using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DaemonKit.Core;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// ProcessItem 生命周期管理 - RunNode / KillNode / 守护逻辑
    /// </summary>
    public partial class ProcessItem
    {
        // 执行节点任务
        public void RunNode()
        {
            if (!IsSuperRoot && !File.Exists(NodePath))
            {
                NLogger.Error("{NodePath} 不存在，请检查", NodePath);
                Status = -1;
                return;
            }

            if (Enable && !IsSuperRoot)
            {
                try
                {
                    // 若已在运行则先关闭，再重启
                    if (nodeProcess != null || nodeProcessId.HasValue)
                    {
                        NLogger.Warn("进程{0} 已在运行，先关闭再重启", ProcessName);
                        KillNode();
                    }

                    // 重置守护状态
                    noResponse = 0;
                    noHeartbeat = 0;
                    noWindowHandle = 0;
                    noInputIdle = 0;
                    noCpuProgress = 0;
                    lastCpuTime = TimeSpan.Zero;

                    ProcManager.DaemonProcess(
                        NodePath,
                        metaData,
                        _process =>
                        {
                            NLogger.Info("[进程] {ProcessName} 就绪 (PID: {Pid})", ProcessName, _process.Id);
                            // 程序打开完成，窗口准备就绪
                            nodeProcess = _process;
                            nodeProcessId = _process.Id;
                            // 预先置顶窗口
                            preKeepTop();
                        }
                    );
                    // 按照时序进行置顶         保证窗口置顶先后顺序正确
                    daemonNode();
                }
                catch (Exception err)
                {
                    NLogger.Error("[进程] {ProcessName} 启动失败: {Message}", ProcessName, err.Message);
                }
            }

            if (_runNodeHandler != null)
            {
                _runNodeHandler.Dispose();
                _runNodeHandler = null;
            }

            Action _runChildNode = () =>
            {
                NLogger.Info("[进程树] 启动子节点 ({Count} 个)", Children.Count);
                Children
                    .ToList()
                    .ForEach(_child =>
                    {
                        _child.KillNode();
                        _child.Status = 0;
                    });
                _runNodeHandler = Observable
                    .Merge(
                        Children.Select(
                            _child =>
                                Observable
                                    .Timer(TimeSpan.FromMilliseconds(_child.MetaData.Delay))
                                    .Do(_ =>
                                    {
                                        // 在线程池清理残留进程（Timer 默认在线程池触发），避免阻塞 UI 线程
                                        if (_child.Enable && !_child.IsSuperRoot && File.Exists(_child.NodePath))
                                        {
                                            try
                                            {
                                                bool isScript = _child.MetaData.IsScript || IsScriptFile(_child.NodePath);
                                                if (isScript)
                                                {
                                                    // 脚本类型：通过 WMI 命令行匹配清理残留的宿主进程
                                                    ProcManager.KillScriptResidualProcesses(_child.NodePath);
                                                }
                                                else
                                                {
                                                    // 普通程序：按可执行文件路径匹配清理残留进程
                                                    ProcManager.KillAllResidualProcesses(
                                                        _child.NodePath,
                                                        MainWindow.AppSettings?.SafeKillTimeout ?? 3000);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                NLogger.Warn("清理残留进程异常: {Path}, {Msg}", _child.NodePath, ex.Message);
                                            }
                                        }
                                    })
                                    .Select(_ => _child)
                        )
                    )
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(
                        _childNode =>
                        {
                            _childNode.RunNode();
                        },
                        () =>
                        {
                            if (_runNodeHandler != null)
                            {
                                _runNodeHandler.Dispose();
                                _runNodeHandler = null;
                            }
                        }
                    );
            };

            // 进程树ROOT根节点延迟启动
            if (this.IsSuperRoot)
            {
                if (m_runChildDisposables != null)
                {
                    m_runChildDisposables.Dispose();
                }
                Status = 0;
                NLogger.Info("[进程树] 启动 (延迟 {Delay}ms)", delayDaemon);
                m_runChildDisposables = Observable
                    .Timer(TimeSpan.FromMilliseconds(delayDaemon))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _runChildNode();
                        Status = 1;
                        m_runChildDisposables = null;
                    });
                return;
            }
            Status = 1;
            _runChildNode();
        }

        private int delayDaemon = 500;
        private int daemonInterval = 5000;
        private int maxError = 1;
        private bool enableCpuStallDetection = false;

        // 守护计数
        private int noResponse = 0;
        private int noHeartbeat = 0;
        private int noWindowHandle = 0;
        private int noInputIdle = 0;
        private int noCpuProgress = 0;

        private TimeSpan lastCpuTime = TimeSpan.Zero;

        // 守护当前进程节点
        IDisposable? daemonHandler = null;

        IDisposable? preKeepTopHandler = null;

        private void preKeepTop()
        {
            NLogger.Debug("[窗口] {ProcessName} 预调整开始", ProcessName);
            // 在后台线程执行窗口置顶，避免跨进程 SendMessage 阻塞 UI 线程
            System.Threading.Tasks.Task.Run(() => KeepTop());
            preKeepTopHandler = Observable
                .Timer(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500))
                .Take(2)
                .Subscribe(
                    _ =>
                    {
                        KeepTop();
                    },
                    () =>
                    {
                        NLogger.Debug("[窗口] {ProcessName} 预调整完成", ProcessName);
                    }
                );
        }

        #region 进程守护

        /// <summary>
        /// 检测文件扩展名是否为脚本类型
        /// </summary>
        private bool IsScriptFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".vbs";
        }

        private DateTime lastHeartbeat = DateTime.MinValue;

        public void NotifyHeartbeat()
        {
            lastHeartbeat = DateTime.Now;
            noHeartbeat = 0;
        }

        public bool IsHeartbeatAlive()
        {
            if (lastHeartbeat == DateTime.MinValue)
                return true;
            var _interval = DateTime.Now - lastHeartbeat;
            return _interval.Milliseconds < daemonInterval;
        }

        private void daemonNode()
        {
            if (metaData.NoDaemon)
                return;

            // 检测是否为脚本类型（手动标记或自动识别）
            bool isScript = metaData.IsScript || IsScriptFile(NodePath);
            if (isScript)
            {
                NLogger.Info("[守护] 开始监控脚本进程: {Path}", NodePath);
            }
            else
            {
                NLogger.Info("[守护] 开始监控进程: {Path}", NodePath);
            }

            noResponse = 0;
            noHeartbeat = 0;
            noWindowHandle = 0;
            noInputIdle = 0;
            noCpuProgress = 0;
            lastCpuTime = TimeSpan.Zero;

            // 进程启动后, 根据守护间隔进行守护
            daemonHandler = Observable
                .Interval(TimeSpan.FromMilliseconds(daemonInterval))
                .Skip(1)
                .Subscribe(_daemonCount =>
                {
                    // 在后台线程执行守护监控，避免 Win32 跨进程调用阻塞 UI 线程
                    var proc = nodeProcess;
                    if (proc == null)
                        return;

                    // 脚本模式：仅检测进程退出，跳过窗口相关检测
                    if (isScript)
                    {
                        try
                        {
                            if (proc.HasExited)
                            {
                                NLogger.Info("[守护] 脚本进程已退出: {Path}", NodePath);
                                RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("脚本进程退出"));
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            NLogger.Warn("[守护] 检测脚本进程状态异常: {Path}, {Msg}", NodePath, ex.Message);
                            RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("脚本进程已不存在"));
                            return;
                        }

                        // 脚本模式下跳过其他检测（窗口、响应、输入空闲等）
                        return;
                    }

                    // 普通程序模式：完整守护逻辑
                    if (!ProcManager.IsProcessExists(NodePath))
                    {
                        // if (nodeProcess.HasExited) { //TODO: 这种方式感觉不稳定     有待后续测试
                        RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("进程退出"));
                        return;
                    }
                    else if (!proc.Responding)
                    {
                        ++noResponse;
                        NLogger.Warn("[守护] {Path} 未响应 ({Count}/{Max})", NodePath, noResponse, maxError);
                        if (noResponse >= maxError)
                        {
                            RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("未响应 (Responding=false)"));
                            return;
                        }
                    }
                    else if (IsHeartbeatAlive() == false)
                    {
                        ++noHeartbeat;
                        NLogger.Warn(
                            "[守护] {NodePath} 无心跳 ({NoHeartbeat}/{MaxError})",
                            NodePath,
                            noHeartbeat,
                            maxError
                        );
                        if (noHeartbeat >= maxError)
                        {
                            RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("心跳超时"));
                            return;
                        }
                    }

                    // 主窗口句柄缺失
                    if (proc.MainWindowHandle == IntPtr.Zero)
                    {
                        ++noWindowHandle;
                        NLogger.Warn(
                            "[守护] {Path} 主窗口句柄缺失 ({Count}/{Max})",
                            NodePath,
                            noWindowHandle,
                            maxError
                        );
                        if (noWindowHandle >= maxError)
                        {
                            RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("主窗口句柄消失"));
                            return;
                        }
                    }
                    else
                    {
                        noWindowHandle = 0;

                        // 检测输入空闲超时（卡死窗口）
                        try
                        {
                            if (!proc.WaitForInputIdle(100))
                            {
                                ++noInputIdle;
                                NLogger.Warn(
                                    "[守护] {Path} 窗口卡死 ({Count}/{Max})",
                                    NodePath,
                                    noInputIdle,
                                    maxError
                                );
                                if (noInputIdle >= maxError)
                                {
                                    RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("窗口卡死 (WaitForInputIdle 超时)"));
                                    return;
                                }
                            }
                            else
                            {
                                noInputIdle = 0;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // 无消息循环的进程会抛出异常，忽略
                        }
                    }

                    if (enableCpuStallDetection)
                    {
                        try
                        {
                            var cpuTime = proc.TotalProcessorTime;
                            if (lastCpuTime != TimeSpan.Zero && cpuTime == lastCpuTime)
                            {
                                ++noCpuProgress;
                                NLogger.Warn(
                                    "[守护] {Path} CPU无进展 ({Count}/{Max})",
                                    NodePath,
                                    noCpuProgress,
                                    maxError
                                );
                                if (noCpuProgress >= maxError)
                                {
                                    RxApp.MainThreadScheduler.Schedule(() => RestartProcessChain("资源停滞 (CPU 时间无增长)"));
                                    return;
                                }
                            }
                            else
                            {
                                noCpuProgress = 0;
                                lastCpuTime = cpuTime;
                            }
                        }
                        catch (Exception ex)
                        {
                            NLogger.Debug("获取 CPU 时间失败: {Message}", ex.Message);
                        }
                    }

                    // 如果需要窗口置顶, 则在守护间隔前3次尝试置顶
                    if (_daemonCount <= 3)
                    {
                        NLogger.Debug(
                            "[窗口] {ProcessName} 置顶调整 ({DaemonCount}/3)",
                            ProcessName,
                            _daemonCount
                        );
                        KeepTop();
                    }
                },
                ex => NLogger.Error("[守护] 监控异常终止: {Path}, {Msg}", NodePath, ex.Message));
        }

        /// <summary>
        /// 优先尝试温和退出，再执行进程链重启
        /// </summary>
        /// <param name="reason">触发原因</param>
        private void RestartProcessChain(string reason)
        {
            NLogger.Warn("[守护] {Path} 重启，原因: {Reason}", NodePath, reason);

            TryGracefulStop();

            RootNode.KillNode();
            RootNode.RunNode();
        }

        /// <summary>
        /// 温和退出：发送 WM_CLOSE（CloseMainWindow），等待短暂时间，再交给 KillNode
        /// 脚本进程（cmd.exe 宿主）跳过温和退出，避免 2 秒无效等待
        /// </summary>
        private void TryGracefulStop()
        {
            try
            {
                if (nodeProcess == null || nodeProcess.HasExited)
                    return;

                // 脚本进程：控制台窗口对 WM_CLOSE 响应不可靠，跳过温和退出直接强杀
                if (metaData.IsScript || IsScriptFile(NodePath))
                {
                    NLogger.Info("[守护] 脚本进程跳过温和退出: {Path}", NodePath);
                    return;
                }

                if (nodeProcess.CloseMainWindow())
                {
                    if (nodeProcess.WaitForExit(2000))
                    {
                        NLogger.Info("进程:{0} 已通过 WM_CLOSE 退出", NodePath);
                        return;
                    }
                    else
                    {
                        NLogger.Warn("进程:{0} WM_CLOSE 未在 2s 内退出，准备强制结束", NodePath);
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("温和退出进程:{0} 失败: {1}", NodePath, ex.Message);
            }
        }

        #endregion

        protected Process? nodeProcess { get; set; } = null;
        protected int? nodeProcessId { get; set; } = null;

        public void KeepTop()
        {
            if (nodeProcess == null)
                return;
            var _process = nodeProcess;
            try
            {
                WinAPI.SetWindowPos(
                    _process.MainWindowHandle,
                    (int)(HWndInsertAfter.HWND_TOPMOST),
                    metaData.PosX,
                    metaData.PosY,
                    metaData.Width,
                    metaData.Height,
                    (
                        metaData.MinimizedStartUp
                            ? SetWindowPosFlags.SWP_HIDEWINDOW
                            : SetWindowPosFlags.SWP_SHOWWINDOW
                    )
                        | (metaData.MoveWindow ? 0x00 : SetWindowPosFlags.SWP_NOMOVE)
                        | (metaData.ResizeWindow ? 0x00 : SetWindowPosFlags.SWP_NOSIZE)
                        | (metaData.KeepTop ? 0x00 : SetWindowPosFlags.SWP_NOZORDER)
                        | SetWindowPosFlags.SWP_FRAMECHANGED
                );
                if (metaData.KeepTop)
                {
                    WinAPI.ShowWindow(_process.MainWindowHandle, (int)CMDShow.SW_SHOW);
                    WinAPI.SetForegroundWindow(_process.MainWindowHandle);
                }
            }
            catch (Exception e)
            {
                NLogger.Error(e.Message);
            }
        }

        public void KillNode()
        {
            if (_runNodeHandler != null)
            {
                _runNodeHandler.Dispose();
                _runNodeHandler = null;
            }

            if (daemonHandler != null)
            {
                daemonHandler.Dispose();
                daemonHandler = null;
            }

            if (preKeepTopHandler != null)
            {
                preKeepTopHandler.Dispose();
                preKeepTopHandler = null;
            }

            if (this.IsSuperRoot && Status != -1)
            {
                NLogger.Info("[进程树] 停止进程树");
            }

            Status = -1;
            if (nodeProcessId.HasValue)
            {
                _ = ProcManager.KillProcess(
                    NodePath,
                    nodeProcessId.Value,
                    MainWindow.AppSettings?.SafeKillProcess ?? false,
                    MainWindow.AppSettings?.SafeKillTimeout ?? 5000
                );
            }
            else if (nodeProcess != null)
            {
                _ = ProcManager.KillProcess(
                    NodePath,
                    MainWindow.AppSettings?.SafeKillProcess ?? false,
                    MainWindow.AppSettings?.SafeKillTimeout ?? 5000
                );
            }

            nodeProcess = null;
            nodeProcessId = null;
            Children
                .ToList()
                .ForEach(_child =>
                {
                    _child.KillNode();
                });
        }
    }
}
