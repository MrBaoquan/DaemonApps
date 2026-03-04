using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
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
                    hasEverHadWindow = false;
                    Interlocked.Exchange(ref lastHeartbeatTicks, 0);

                    ProcManager.DaemonProcess(
                        NodePath,
                        metaData,
                        _process =>
                        {
                            NLogger.Info(
                                "[进程] {ProcessName} 就绪 (PID: {Pid})",
                                ProcessName,
                                _process.Id
                            );
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
                                        if (
                                            _child.Enable
                                            && !_child.IsSuperRoot
                                            && File.Exists(_child.NodePath)
                                        )
                                        {
                                            try
                                            {
                                                bool isScript =
                                                    _child.MetaData.IsScript
                                                    || ProcManager.IsScriptFile(_child.NodePath);
                                                if (isScript)
                                                {
                                                    // 脚本类型：通过 WMI 命令行匹配清理残留的宿主进程
                                                    ProcManager.KillScriptResidualProcesses(
                                                        _child.NodePath
                                                    );
                                                }
                                                else
                                                {
                                                    // 普通程序：按可执行文件路径匹配清理残留进程
                                                    ProcManager.KillAllResidualProcesses(
                                                        _child.NodePath,
                                                        MainWindow.AppSettings?.SafeKillTimeout
                                                            ?? 3000
                                                    );
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                NLogger.Warn(
                                                    "清理残留进程异常: {Path}, {Msg}",
                                                    _child.NodePath,
                                                    ex.Message
                                                );
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
                            MainWindow._uiCheckpoint = $"runChild:{_childNode.ProcessName}";
                            _childNode.RunNode();
                            MainWindow._uiCheckpoint = "idle";
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
        private int maxError = 5;
        private bool enableCpuStallDetection = false;
        private int inputIdleTimeoutMs = 1000;

        // 守护计数
        private int noResponse = 0;
        private int noHeartbeat = 0;
        private int noWindowHandle = 0;
        private int noInputIdle = 0;
        private int noCpuProgress = 0;

        /// <summary>
        /// 运行时自动检测：进程是否曾经拥有过主窗口句柄。
        /// 用于区分"窗口程序窗口消失"和"控制台/后台程序天然无窗口"两种场景，
        /// 避免对控制台程序误触发"主窗口句柄消失"重启。
        /// </summary>
        private bool hasEverHadWindow = false;

        /// <summary>
        /// 窗口探测期计数：进程启动后的前 N 个守护周期用于自动探测进程类型。
        /// 探测期内 MainWindowHandle==Zero 不计入 noWindowHandle 失败计数。
        /// </summary>
        private const int WindowProbeTickCount = 6;

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

        // ── P/Invoke: EnumWindows 用于多窗口进程二次确认 ──
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool
        )]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// 检查指定 PID 的进程是否仍拥有任意可见窗口（用于多窗口进程的二次确认）。
        /// 当 MainWindowHandle 短暂为 Zero（如闪屏切换主窗口）时，
        /// 通过 EnumWindows 枚举所有顶级窗口来判断进程是否真的失去了窗口。
        /// </summary>
        private static bool HasAnyVisibleWindow(int processId)
        {
            bool found = false;
            EnumWindows(
                (hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == (uint)processId && IsWindowVisible(hWnd))
                    {
                        found = true;
                        return false; // 找到即停止枚举
                    }
                    return true;
                },
                IntPtr.Zero
            );
            return found;
        }

        /// <summary>
        /// 统一的进程查找方法，根据 ProcessMatchName 配置决定匹配策略。
        /// - ProcessMatchName 非空 → 按进程名匹配（WinAPI.FindProcess 的 bare-name 分支）
        /// - ProcessMatchName 为空 → 按 NodePath 精确路径匹配（默认行为）
        /// </summary>
        private Process? FindMonitoredProcess()
        {
            var matchName = metaData.ProcessMatchName;
            if (!string.IsNullOrWhiteSpace(matchName))
            {
                // bare-name 模式：进程名匹配（不含 .exe 后缀）
                var proc = WinAPI.FindProcess(matchName);
                return proc != default(Process) ? proc : null;
            }
            else
            {
                // 精确路径模式（默认）
                var proc = WinAPI.FindProcess(NodePath);
                return proc != default(Process) ? proc : null;
            }
        }

        /// <summary>
        /// 查找所有匹配的被守护进程（用于 KillNode 场景）
        /// </summary>
        private System.Collections.Generic.List<Process> FindAllMonitoredProcesses()
        {
            var matchName = metaData.ProcessMatchName;
            if (!string.IsNullOrWhiteSpace(matchName))
                return WinAPI.FindProcesses(matchName);
            else
                return WinAPI.FindProcesses(NodePath);
        }

        /// <summary>
        /// 上次收到心跳的 UTC Ticks（通过 Interlocked 保证跨线程读写安全）。
        /// 0 表示从未收到过心跳。
        /// </summary>
        private long lastHeartbeatTicks = 0;

        public void NotifyHeartbeat()
        {
            var previousTicks = Interlocked.Exchange(ref lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            if (previousTicks == 0)
            {
                NLogger.Info("[心跳] {ProcessName} 首次收到心跳信号", ProcessName);
            }
            noHeartbeat = 0;
        }

        public bool IsHeartbeatAlive()
        {
            var ticks = Interlocked.Read(ref lastHeartbeatTicks);
            if (ticks == 0)
                return true; // 从未收到心跳，视为存活（心跳检测为 opt-in）
            var elapsed = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            var alive = elapsed.TotalMilliseconds < daemonInterval;
            if (!alive)
            {
                NLogger.Debug(
                    "[心跳] {ProcessName} 心跳超时检测: 距上次心跳 {Elapsed:F0}ms, 阈值 {Threshold}ms",
                    ProcessName,
                    elapsed.TotalMilliseconds,
                    daemonInterval
                );
            }
            return alive;
        }

        private void daemonNode()
        {
            if (metaData.NoDaemon)
                return;

            // 检测是否为脚本类型（手动标记或自动识别）
            bool isScript = metaData.IsScript || ProcManager.IsScriptFile(NodePath);
            if (isScript)
            {
                NLogger.Info(
                    "[守护] 开始监控脚本进程: {Path} (间隔={Interval}ms, 容错={MaxErr}次)",
                    NodePath,
                    daemonInterval,
                    maxError
                );
            }
            else
            {
                NLogger.Info(
                    "[守护] 开始监控进程: {Path} (间隔={Interval}ms, 容错={MaxErr}次, CPU停滞检测={CpuStall})",
                    NodePath,
                    daemonInterval,
                    maxError,
                    enableCpuStallDetection
                );
            }

            noResponse = 0;
            noHeartbeat = 0;
            noWindowHandle = 0;
            noInputIdle = 0;
            noCpuProgress = 0;
            lastCpuTime = TimeSpan.Zero;
            hasEverHadWindow = false;

            // 进程启动后, 根据守护间隔进行守护
            // 移除 .Skip(1)：首次守护检测在 daemonInterval 后即生效（而非 2*daemonInterval）
            daemonHandler = Observable
                .Interval(TimeSpan.FromMilliseconds(daemonInterval))
                .Subscribe(
                    _daemonCount =>
                    {
                        // 在后台线程执行守护监控，避免 Win32 跨进程调用阻塞 UI 线程
                        var proc = nodeProcess;
                        if (proc == null)
                            return;

                        // ═══════════════════════════════════════
                        // 脚本模式：仅检测进程退出，跳过窗口相关检测
                        // ═══════════════════════════════════════
                        if (isScript)
                        {
                            try
                            {
                                if (proc.HasExited)
                                {
                                    NLogger.Info("[守护] 脚本进程已退出: {Path}", NodePath);
                                    RxApp.MainThreadScheduler.Schedule(
                                        () => RestartProcessChain("脚本进程退出")
                                    );
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                NLogger.Warn(
                                    "[守护] 检测脚本进程状态异常: {Path}, {Msg}",
                                    NodePath,
                                    ex.Message
                                );
                                RxApp.MainThreadScheduler.Schedule(
                                    () => RestartProcessChain("脚本进程已不存在")
                                );
                                return;
                            }

                            // 脚本模式下跳过其他检测（窗口、响应、输入空闲等）
                            return;
                        }

                        // ═══════════════════════════════════════
                        // 普通程序模式：独立检查项（非互斥）
                        // ═══════════════════════════════════════

                        // ── 前置检查：进程是否存活 + 刷新过期引用 ──
                        bool procExited = false;
                        try
                        {
                            procExited = proc.HasExited;
                        }
                        catch
                        {
                            procExited = true;
                        }

                        if (procExited)
                        {
                            // 原 Process 对象已退出，尝试通过 FindMonitoredProcess 重新绑定
                            // 支持 ProcessMatchName（启动器场景）和默认精确路径匹配
                            var liveProc = FindMonitoredProcess();
                            if (liveProc != null)
                            {
                                NLogger.Info(
                                    "[守护] {Path} 进程引用已过期，重新绑定到 PID={Pid}",
                                    NodePath,
                                    liveProc.Id
                                );
                                nodeProcess = liveProc;
                                nodeProcessId = liveProc.Id;
                                proc = liveProc;
                                // 重置所有计数器，给新进程一个干净的起点
                                noResponse = 0;
                                noHeartbeat = 0;
                                noWindowHandle = 0;
                                noInputIdle = 0;
                                noCpuProgress = 0;
                                lastCpuTime = TimeSpan.Zero;
                                hasEverHadWindow = false;
                            }
                            else
                            {
                                RxApp.MainThreadScheduler.Schedule(
                                    () => RestartProcessChain("进程退出")
                                );
                                return;
                            }
                        }

                        // ── 检查1：Process.Responding（UI线程消息泵检测）──
                        // 启动保护期内跳过（WPF/UE 等重度初始化应用启动时 UI 线程阻塞）
                        // 对无消息循环的控制台进程，Responding 始终返回 true（无害）
                        if (_daemonCount > WindowProbeTickCount && !proc.Responding)
                        {
                            ++noResponse;
                            NLogger.Warn(
                                "[守护] {Path} 未响应 ({Count}/{Max})",
                                NodePath,
                                noResponse,
                                maxError
                            );
                            if (noResponse >= maxError)
                            {
                                RxApp.MainThreadScheduler.Schedule(
                                    () => RestartProcessChain("未响应 (Responding=false)")
                                );
                                return;
                            }
                        }
                        else
                        {
                            noResponse = 0;
                        }

                        // ── 检查2：心跳（opt-in，仅在进程主动发送过心跳后生效）──
                        if (!IsHeartbeatAlive())
                        {
                            ++noHeartbeat;
                            var hbTicks = Interlocked.Read(ref lastHeartbeatTicks);
                            var hbElapsed =
                                hbTicks > 0
                                    ? (
                                        DateTime.UtcNow - new DateTime(hbTicks, DateTimeKind.Utc)
                                    ).TotalMilliseconds
                                    : -1;
                            NLogger.Warn(
                                "[守护] {NodePath} 无心跳 ({NoHeartbeat}/{MaxError}), 距上次心跳 {Elapsed:F0}ms",
                                NodePath,
                                noHeartbeat,
                                maxError,
                                hbElapsed
                            );
                            if (noHeartbeat >= maxError)
                            {
                                NLogger.Error(
                                    "[守护] {NodePath} 心跳超时达上限，触发重启 (连续 {Count} 次无心跳)",
                                    NodePath,
                                    noHeartbeat
                                );
                                RxApp.MainThreadScheduler.Schedule(
                                    () => RestartProcessChain("心跳超时")
                                );
                                return;
                            }
                        }
                        else
                        {
                            noHeartbeat = 0;
                        }

                        // ── 检查3：主窗口句柄（自动区分窗口程序 vs 控制台程序）──
                        bool hasWindow = proc.MainWindowHandle != IntPtr.Zero;

                        if (hasWindow)
                        {
                            // 首次检测到窗口，标记为窗口程序
                            if (!hasEverHadWindow)
                            {
                                hasEverHadWindow = true;
                                NLogger.Info("[守护] {Path} 检测到主窗口，标记为窗口程序", NodePath);
                            }
                            noWindowHandle = 0;

                            // ── 检查4：WaitForInputIdle（仅窗口程序，检测卡死窗口）──
                            // 启动保护期内跳过（避免慢启动 WPF/游戏被误判为卡死）
                            if (_daemonCount > WindowProbeTickCount)
                                try
                                {
                                    if (!proc.WaitForInputIdle(inputIdleTimeoutMs))
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
                                            RxApp.MainThreadScheduler.Schedule(
                                                () =>
                                                    RestartProcessChain(
                                                        "窗口卡死 (WaitForInputIdle 超时)"
                                                    )
                                            );
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
                        else
                        {
                            // MainWindowHandle == Zero
                            if (hasEverHadWindow)
                            {
                                // 曾有窗口，现在消失了 → 二次确认：枚举所有可见窗口
                                // 多窗口进程（闪屏→主窗口切换）期间 MainWindowHandle 可能短暂为 Zero
                                if (HasAnyVisibleWindow(proc.Id))
                                {
                                    // 进程仍有可见窗口，只是 MainWindowHandle 切换中，不计入失败
                                    NLogger.Debug(
                                        "[守护] {Path} MainWindowHandle 为空但仍有可见窗口，跳过计数",
                                        NodePath
                                    );
                                }
                                else
                                {
                                    ++noWindowHandle;
                                    NLogger.Warn(
                                        "[守护] {Path} 主窗口句柄消失 ({Count}/{Max})",
                                        NodePath,
                                        noWindowHandle,
                                        maxError
                                    );
                                    if (noWindowHandle >= maxError)
                                    {
                                        RxApp.MainThreadScheduler.Schedule(
                                            () => RestartProcessChain("主窗口句柄消失")
                                        );
                                        return;
                                    }
                                }
                            }
                            else if (_daemonCount <= WindowProbeTickCount)
                            {
                                // 探测期内未出现窗口，暂不计数（可能是启动较慢的窗口程序）
                            }
                            else
                            {
                                // 超过探测期仍无窗口 → 自动识别为控制台/后台程序
                                // 跳过窗口句柄和 InputIdle 检查（避免误判）
                                // 仅在首次标识时输出一条日志
                                if (_daemonCount == WindowProbeTickCount + 1)
                                {
                                    NLogger.Info(
                                        "[守护] {Path} 探测期结束未发现窗口，自动识别为控制台/后台程序，跳过窗口检测",
                                        NodePath
                                    );
                                }
                            }
                        }

                        // ── 检查5：CPU 停滞检测（可选，通过 AppSettings 配置启用）──
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
                                        RxApp.MainThreadScheduler.Schedule(
                                            () => RestartProcessChain("资源停滞 (CPU 时间无增长)")
                                        );
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
                    ex => NLogger.Error("[守护] 监控异常终止: {Path}, {Msg}", NodePath, ex.Message)
                );
        }

        /// <summary>
        /// 优先尝试温和退出，再执行进程链重启
        /// </summary>
        /// <param name="reason">触发原因</param>
        private void RestartProcessChain(string reason)
        {
            NLogger.Warn("[守护] {Path} 重启，原因: {Reason}", NodePath, reason);

            TryGracefulStop();

            // 仅重启当前节点及其子进程，避免牵连整棵进程树中无关的兄弟节点
            this.KillNode();
            this.RunNode();
        }

        /// <summary>
        /// 温和退出：发送 WM_CLOSE（CloseMainWindow），等待短暂时间，再交给 KillNode
        /// 脚本进程（cmd.exe 宿主）跳过温和退出，避免 2 秒无效等待
        /// </summary>
        private void TryGracefulStop()
        {
            try
            {
                var proc = nodeProcess; // 线程安全快照
                if (proc == null || proc.HasExited)
                    return;

                // 脚本进程：控制台窗口对 WM_CLOSE 响应不可靠，跳过温和退出直接强杀
                if (metaData.IsScript || ProcManager.IsScriptFile(NodePath))
                {
                    NLogger.Info("[守护] 脚本进程跳过温和退出: {Path}", NodePath);
                    return;
                }

                if (proc.CloseMainWindow())
                {
                    if (proc.WaitForExit(2000))
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

        // ── 线程安全：volatile 确保守护线程和 UI 线程间的跨线程可见性 ──
        private volatile Process? _nodeProcess = null;
        private int _nodeProcessId = -1; // -1 = 无进程; 通过 Volatile.Read/Write 确保原子访问

        protected Process? nodeProcess
        {
            get => _nodeProcess;
            set => _nodeProcess = value;
        }

        protected int? nodeProcessId
        {
            get
            {
                var id = Volatile.Read(ref _nodeProcessId);
                return id < 0 ? null : (int?)id;
            }
            set => Volatile.Write(ref _nodeProcessId, value ?? -1);
        }

        public void KeepTop()
        {
            var _process = nodeProcess; // 线程安全快照
            if (_process == null)
                return;
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

            // 终止子节点延迟启动订阅，避免 Kill 后仍有子节点被调度启动
            if (m_runChildDisposables != null)
            {
                m_runChildDisposables.Dispose();
                m_runChildDisposables = null;
            }

            if (this.IsSuperRoot && Status != -1)
            {
                NLogger.Info("[进程树] 停止进程树");
            }

            Status = -1;

            // 快照当前进程引用（线程安全）
            var currentProcess = nodeProcess;
            var currentPid = nodeProcessId;

            // 当配置了 ProcessMatchName 时，需要按进程名查找并终止实际运行的进程
            // （启动器场景下 NodePath 对应的启动器可能早已退出，实际进程路径不同）
            bool hasMatchName = !string.IsNullOrWhiteSpace(metaData.ProcessMatchName);

            if (hasMatchName)
            {
                // 按 ProcessMatchName 查找并终止所有匹配进程
                var matchedProcesses = FindAllMonitoredProcesses();
                if (matchedProcesses.Count > 0)
                {
                    NLogger.Info(
                        "[进程] 按守护进程名 '{MatchName}' 终止 {Count} 个进程",
                        metaData.ProcessMatchName,
                        matchedProcesses.Count
                    );
                    foreach (var p in matchedProcesses)
                    {
                        try
                        {
                            p.Kill(true);
                            NLogger.Info("[进程] 已终止: PID={Pid}", p.Id);
                        }
                        catch (Exception ex)
                        {
                            NLogger.Error("[进程] 终止失败: PID={Pid}, {Msg}", p.Id, ex.Message);
                        }
                    }
                }
                // 同时终止启动器本身（如果仍在运行）
                if (currentPid.HasValue)
                {
                    try
                    {
                        var launcherProc = Process.GetProcessById(currentPid.Value);
                        if (!launcherProc.HasExited)
                        {
                            launcherProc.Kill(true);
                            NLogger.Info("[进程] 已终止启动器: PID={Pid}", currentPid.Value);
                        }
                    }
                    catch
                    { /* 启动器已退出，忽略 */
                    }
                }
            }
            else if (currentPid.HasValue)
            {
                // 同步终止：确保旧进程退出后再清理引用，防止 RunNode 启动新进程时旧进程仍存活
                ProcManager.KillProcess(NodePath, currentPid.Value);
            }
            else if (currentProcess != null)
            {
                ProcManager.KillProcess(NodePath);
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
