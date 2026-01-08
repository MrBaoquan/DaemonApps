using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Xml.Serialization;
using DaemonKit.Core;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Models
{
    public enum TriggerType
    {
        Daily,
        Interval,
        OnAppStart, // 程序启动后
        OnAppStartOnce // 每天首次启动后
    }

    public class TaskTrigger
    {
        [XmlAttribute]
        public TriggerType Mode { get; set; } = TriggerType.Daily;

        [XmlAttribute]
        public string Time { get; set; } = "00:00";

        [XmlAttribute]
        public int Interval { get; set; } = 60;

        [XmlAttribute]
        public int Unit { get; set; } = 2; // 0: 秒 1: 分 2: 时
    }

    public class ProcessMetaData
    {
        // 进程展示名
        [XmlAttribute]
        public string Name = string.Empty;

        [XmlAttribute]
        // 进程路径
        public string Path = string.Empty;

        [XmlAttribute]
        public string Arguments = string.Empty;

        [XmlAttribute]
        public bool RunAs = true;

        [XmlAttribute]
        public bool KeepTop = false;

        [XmlAttribute]
        public bool NoDaemon = false;

        [XmlAttribute]
        public bool IsScript = false;

        [XmlAttribute]
        public bool MoveWindow = false;

        [XmlAttribute]
        public bool ResizeWindow = false;

        [XmlAttribute]
        public bool MinimizedStartUp = false;

        [XmlAttribute]
        public int Delay = 500;

        [XmlAttribute]
        public bool Enable = true;

        [XmlAttribute]
        public int PosX = 0;

        [XmlAttribute]
        public int PosY = 0;

        [XmlAttribute]
        public int Width = 0;

        [XmlAttribute]
        public int Height = 0;

        [XmlElement("Schedule")]
        public List<TaskTrigger> Triggers = new List<TaskTrigger>();
    }

    public class ProcessItem : ReactiveObject
    {
        [XmlIgnore]
        public ProcessItem? Parent { get; set; }

        [XmlIgnore]
        public ProcessItem RootNode
        {
            get
            {
                var _node = Parent;
                if (_node == null || _node.Parent == null)
                    return this;
                while (_node != null && _node.Parent != null && _node.Parent.IsSuperRoot == false)
                {
                    _node = _node.Parent;
                }
                return _node ?? this;
            }
        }

        [XmlIgnore]
        public bool IsSuperRoot
        {
            get => Parent == null;
        }

        [XmlIgnore]
        public bool IsLeaf
        {
            get => Children.Count <= 0;
        }

        private string _nodeId = string.Empty;

        /// <summary>
        /// 节点唯一标识（用于区分同名节点），若未设置则自动生成
        /// </summary>
        [XmlAttribute]
        public string NodeId
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_nodeId))
                {
                    _nodeId = Guid.NewGuid().ToString("N");
                }
                return _nodeId;
            }
            set => _nodeId = value;
        }

        [XmlIgnore]
        public string ShortNodeId
        {
            get
            {
                var id = NodeId;
                return id.Length > 8 ? id.Substring(0, 8) : id;
            }
        }

        [XmlIgnore]
        public string NodePath
        {
            get
            {
                if (!System.IO.Path.IsPathRooted(MetaData.Path))
                {
                    return System.IO.Path.Combine(AppPathes.AppDir, MetaData.Path);
                }
                return MetaData.Path;
            }
        }

        private List<ProcessItem> TraceToRoot(ProcessItem InItem)
        {
            List<ProcessItem> _list = new List<ProcessItem>() { InItem };
            while (InItem.Parent != null)
            {
                _list.Add(InItem.Parent);
                InItem = InItem.Parent;
            }
            return _list;
        }

        public ProcessItem()
        {
            this.Children = new ObservableCollection<ProcessItem>();
            this.RunNodeCommand = ReactiveCommand.Create(() => { });
            this.KillNodeCommand = ReactiveCommand.Create(() => { });
            this.DisableNameInput = ReactiveCommand.Create(() => { });
            this.ToggleEnableCommand = ReactiveCommand.Create<bool, bool>(_isEnable => _isEnable);
            this.ScheduleCommand = ReactiveCommand.Create(() => { });

            Status = -1;

            this.RunNodeCommand.Subscribe(_ =>
            {
                RunNode();
            });

            this.KillNodeCommand.Subscribe(_ =>
            {
                KillNode();
            });

            this.ToggleEnableCommand.Subscribe(_isEnable =>
            {
                Children
                    .ToList()
                    .ForEach(_child =>
                    {
                        _child.MetaData.Enable = _isEnable;
                        _child.Enable = _isEnable;
                    });
            });

            this.WhenAnyValue(x => x.Enable)
                .Subscribe(_isEnable =>
                {
                    if (!_isEnable)
                    {
                        KillNode();
                    }
                    BtnRunVisibility = _isEnable ? Visibility.Visible : Visibility.Hidden;
                });

            this.DisableNameInput.Subscribe(_ =>
            {
                this.NameInputVisibility = Visibility.Hidden;
            });
        }

        private ProcessMetaData metaData = new ProcessMetaData();
        public ProcessMetaData MetaData
        {
            get => metaData;
            set
            {
                metaData = value;
                Name = metaData.Name;
                Path = System.IO.Path.GetFileName(metaData.Path);
                Enable = metaData.Enable;
                Delay = metaData.Delay;
                NameField = Name;
            }
        }

        [XmlElement("ScheduleItem")]
        public List<ScheduleItem> ScheduleItems { get; set; } = new List<ScheduleItem>();

        /// <summary>新的计划任务配置列表</summary>
        [XmlElement("ScheduleTaskConfig")]
        public List<ScheduleTaskConfig> ScheduleTaskConfigs { get; set; } =
            new List<ScheduleTaskConfig>();

        /// <summary>全局计划任务启用标志</summary>
        private bool _scheduleTasksEnabled = true;

        [XmlAttribute]
        public bool ScheduleTasksEnabled
        {
            get => _scheduleTasksEnabled;
            set => this.RaiseAndSetIfChanged(ref _scheduleTasksEnabled, value);
        }

        [XmlIgnore]
        public string ProcessName => System.IO.Path.GetFileName(metaData.Path);

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> RunNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> KillNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<bool, bool> ToggleEnableCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DisableNameInput { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> ScheduleCommand { get; protected set; }

        private IDisposable? _runNodeHandler = null;

        static void ClearHandler(ref IDisposable? InHandler)
        {
            if (InHandler != null)
            {
                InHandler.Dispose();
                InHandler = null;
            }
        }

        private IDisposable? m_runChildDisposables;

        // 刷新结点计划任务
        public List<(ProcessItem processItem, ScheduleItem scheduleItem)> RefreshSchedule()
        {
            return AllChildren()
                .SelectMany(_child => _child.ScheduleItems.Select(_item => (_child, _item)))
                .Where(_ => _._item.CanExecute())
                .ToList();

            // .ToList()
            // .ForEach(_child =>
            // {
            //     _child.ScheduleItems
            //         .ToList()
            //         .ForEach(_item =>
            //         {
            //             if (_item.CanExecute())
            //             {
            //                 _item.MarkAsExecuted();
            //                 if (_item.TaskType == ScheduleTaskType.Start)
            //                 {
            //                     NLogger.Info($"执行计划任务: {_item.TaskType} {_child.Name}");
            //                     _child.RunNode();
            //                 }
            //                 else if (_item.TaskType == ScheduleTaskType.Stop)
            //                 {
            //                     _child.KillNode();
            //                 }
            //                 else if (_item.TaskType == ScheduleTaskType.Shutdown)
            //                 {
            //                     NLogger.Info($"执行计划任务: {_item.TaskType} {_child.Name}");
            //                 }
            //             }
            //         });
            // });
        }

        public List<ProcessItem> AllChildren()
        {
            List<ProcessItem> _list = new List<ProcessItem>();
            _list.Add(this);
            Children
                .ToList()
                .ForEach(_child =>
                {
                    _list.AddRange(_child.AllChildren());
                });
            return _list;
        }

        // 执行节点任务
        public void RunNode()
        {
            if (!IsSuperRoot && !File.Exists(NodePath))
            {
                NLogger.Error($"{NodePath} 不存在，请检查");
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
                            NLogger.Warn($"进程{ProcessName}就绪, PID: {_process.Id}");
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
                catch (System.Exception err)
                {
                    NLogger.Error(err.Message);
                }
            }

            if (_runNodeHandler != null)
            {
                _runNodeHandler.Dispose();
                _runNodeHandler = null;
            }

            Action _runChildNode = () =>
            {
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
                NLogger.Info("启动进程树, Delay:{0}", delayDaemon);
                m_runChildDisposables = Observable
                    .Timer(TimeSpan.FromMilliseconds(delayDaemon))
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
            NLogger.Info($"{ProcessName}窗口预调整开始");
            KeepTop();
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
                        NLogger.Info($"{ProcessName}窗口预调整结束");
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
                NLogger.Info("开始守护脚本进程:{0} (脚本模式)", NodePath);
            }
            else
            {
                NLogger.Info("开始守护进程:{0}", NodePath);
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
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_daemonCount =>
                {
                    if (nodeProcess == null)
                        return;

                    // 脚本模式：仅检测进程退出，跳过窗口相关检测
                    if (isScript)
                    {
                        try
                        {
                            if (nodeProcess.HasExited)
                            {
                                NLogger.Info("脚本进程已退出: {0}", NodePath);
                                RestartProcessChain("脚本进程退出");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            NLogger.Warn("检测脚本进程状态异常: {0}, 错误: {1}", NodePath, ex.Message);
                            RestartProcessChain("脚本进程已不存在");
                            return;
                        }

                        // 脚本模式下跳过其他检测（窗口、响应、输入空闲等）
                        return;
                    }

                    // 普通程序模式：完整守护逻辑
                    if (!ProcManager.IsProcessExists(NodePath))
                    {
                        // if (nodeProcess.HasExited) { //TODO: 这种方式感觉不稳定     有待后续测试
                        RestartProcessChain("进程退出");
                        return;
                    }
                    else if (!nodeProcess.Responding)
                    {
                        ++noResponse;
                        NLogger.Warn("进程:{0} 未响应，容忍度: {1}/{2}", NodePath, noResponse, maxError);
                        if (noResponse >= maxError)
                        {
                            RestartProcessChain("未响应 (Responding=false)");
                            return;
                        }
                    }
                    else if (IsHeartbeatAlive() == false)
                    {
                        ++noHeartbeat;
                        NLogger.Warn($"进程 {NodePath} 无心跳, 容忍度: {noHeartbeat} / {maxError}");
                        if (noHeartbeat >= maxError)
                        {
                            RestartProcessChain("心跳超时");
                            return;
                        }
                    }

                    // 主窗口句柄缺失
                    if (nodeProcess.MainWindowHandle == IntPtr.Zero)
                    {
                        ++noWindowHandle;
                        NLogger.Warn(
                            "进程:{0} 主窗口句柄缺失，容忍度: {1}/{2}",
                            NodePath,
                            noWindowHandle,
                            maxError
                        );
                        if (noWindowHandle >= maxError)
                        {
                            RestartProcessChain("主窗口句柄消失");
                            return;
                        }
                    }
                    else
                    {
                        noWindowHandle = 0;

                        // 检测输入空闲超时（卡死窗口）
                        try
                        {
                            if (!nodeProcess.WaitForInputIdle(100))
                            {
                                ++noInputIdle;
                                NLogger.Warn(
                                    "进程:{0} WaitForInputIdle 超时，容忍度: {1}/{2}",
                                    NodePath,
                                    noInputIdle,
                                    maxError
                                );
                                if (noInputIdle >= maxError)
                                {
                                    RestartProcessChain("窗口卡死 (WaitForInputIdle 超时)");
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
                            var cpuTime = nodeProcess.TotalProcessorTime;
                            if (lastCpuTime != TimeSpan.Zero && cpuTime == lastCpuTime)
                            {
                                ++noCpuProgress;
                                NLogger.Warn(
                                    "进程:{0} CPU 未前进，容忍度: {1}/{2}",
                                    NodePath,
                                    noCpuProgress,
                                    maxError
                                );
                                if (noCpuProgress >= maxError)
                                {
                                    RestartProcessChain("资源停滞 (CPU 时间无增长)");
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
                            NLogger.Debug($"获取 CPU 时间失败: {ex.Message}");
                        }
                    }

                    // 如果需要窗口置顶, 则在守护间隔前3次尝试置顶
                    if (_daemonCount <= 3)
                    {
                        NLogger.Info($"尝试调整窗口:{ProcessName} 第{_daemonCount}次");
                        KeepTop();
                        if (_daemonCount == 3)
                        {
                            NLogger.Info($"尝试调整窗口:{ProcessName} 任务结束");
                        }
                    }
                });
        }

        /// <summary>
        /// 优先尝试温和退出，再执行进程链重启
        /// </summary>
        /// <param name="reason">触发原因</param>
        private void RestartProcessChain(string reason)
        {
            NLogger.Warn("进程:{0} 守护重启，原因: {1}", NodePath, reason);

            TryGracefulStop();

            RootNode.KillNode();
            RootNode.RunNode();
        }

        /// <summary>
        /// 温和退出：发送 WM_CLOSE（CloseMainWindow），等待短暂时间，再交给 KillNode
        /// </summary>
        private void TryGracefulStop()
        {
            try
            {
                if (nodeProcess == null || nodeProcess.HasExited)
                    return;

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
            catch (System.Exception e)
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
                NLogger.Info("终止进程树..");
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

            // if (nodeProcess != null)
            //     nodeProcess.Kill ();
            nodeProcess = null;
            nodeProcessId = null;
            Children
                .ToList()
                .ForEach(_child =>
                {
                    _child.KillNode();
                });
        }

        public void SyncEnable()
        {
            new List<ProcessItem> { this }
                .Flatten<ProcessItem>(_item => _item.Children)
                .ToList()
                .ForEach(_child =>
                {
                    _child.MetaData.Enable = Enable;
                    _child.Enable = Enable;
                });
        }

        public void EnableNameInput()
        {
            this.NameInputVisibility = Visibility.Visible;
        }

        private string _name = string.Empty;

        [XmlIgnore]
        public string Name
        {
            set => this.RaiseAndSetIfChanged(ref _name, value);
            get => _name;
        }

        private string _nameField = string.Empty;

        [XmlIgnore]
        public string NameField
        {
            set => this.RaiseAndSetIfChanged(ref _nameField, value);
            get => _nameField;
        }
        private bool _enable = true;

        [XmlIgnore]
        public bool Enable
        {
            set => this.RaiseAndSetIfChanged(ref _enable, value);
            get => _enable;
        }

        private int _delay = 500;

        [XmlAttribute]
        public int Delay
        {
            set => this.RaiseAndSetIfChanged(ref _delay, value);
            get => _delay;
        }

        private string _path = string.Empty;

        [XmlAttribute]
        public string Path
        {
            set => this.RaiseAndSetIfChanged(ref _path, value);
            get => _path;
        }

        public bool IsRuning
        {
            get => Status == 1;
        }

        [XmlIgnore]
        private int _status = -1; // -1 未启动 0 启动中  1 已启动
        public int Status
        {
            set
            {
                BtnRunVisibility = Visibility.Collapsed;
                BtnLoadingVisibility = Visibility.Collapsed;
                BtnStopVisibility = Visibility.Collapsed;

                if (value == -1)
                {
                    BtnRunVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                else if (value == 0)
                {
                    BtnLoadingVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                else if (value == 1)
                {
                    BtnStopVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                _status = value;
            }
            get => _status;
        }

        [XmlIgnore]
        private Visibility btnRunVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnRunVisibility
        {
            get => btnRunVisibility;
            set => this.RaiseAndSetIfChanged(ref btnRunVisibility, value);
        }

        [XmlIgnore]
        private Visibility nameInputVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility NameInputVisibility
        {
            get => nameInputVisibility;
            set => this.RaiseAndSetIfChanged(ref nameInputVisibility, value);
        }

        [XmlIgnore]
        private Visibility btnLoadingVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnLoadingVisibility
        {
            get => btnLoadingVisibility;
            set => this.RaiseAndSetIfChanged(ref btnLoadingVisibility, value);
        }

        [XmlIgnore]
        private Visibility btnStopVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnStopVisibility
        {
            get => btnStopVisibility;
            set => this.RaiseAndSetIfChanged(ref btnStopVisibility, value);
        }

        public ObservableCollection<ProcessItem> Children { set; get; }

        /// <summary>
        /// 添加子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void AddChild(ProcessItem InChild)
        {
            InChild.Parent = this;
            Children.Add(InChild);
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void RemoveChild(ProcessItem InChild)
        {
            Children.Remove(InChild);
        }

        /// <summary>
        /// 同步子节点的父级关系
        /// </summary>
        public void SyncRelationships()
        {
            Action<ProcessItem> _sync = _ => { };
            _sync = (ProcessItem InItem) =>
            {
                InItem.Children
                    .ToList()
                    .ForEach(_child =>
                    {
                        _child.Parent = InItem;
                        if (_child.Children.Count > 0)
                        {
                            _sync(_child);
                        }
                    });
            };
            _sync(this);
        }

        public void SyncSettings(AppSettings appSettings)
        {
            this.delayDaemon = appSettings.DelayDaemon;
            this.daemonInterval = appSettings.DaemonInterval;
            this.maxError = appSettings.ErrorCount;
            this.Children
                .ToList()
                .ForEach(_childNode =>
                {
                    _childNode.SyncSettings(appSettings);
                });
        }

        public void ConfirmNameInput()
        {
            if (NameField.Trim() == string.Empty)
            {
                NLogger.Warn("备注名不能为空");
                return;
            }
            Name = NameField;
            metaData.Name = Name;
            NameInputVisibility = Visibility.Collapsed;
        }
    }
}
