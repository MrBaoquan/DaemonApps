using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DaemonKit.Core;
using DNHper;
using Hardware.Info;
// using IWshRuntimeLibrary;
using H.NotifyIcon;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Newtonsoft.Json;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;
using System.Collections.Generic;
using System.Reactive.Disposables;

namespace DaemonKit
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<MainViewModel>
    {
        #region Win32 API for Global Hotkey

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000; // Alt+X for screenshot
        private const int WM_HOTKEY = 0x0312;

        #endregion

        #region Fields

        public static AppSettings AppSettings { get; set; }
        ProcessItem rootProcessNode = null!;
        ProcessNodeForm processNodeForm = null!;
        Settings settingsWindow = null!;
        Schedule scheduleWindow = null!;
        DaemonTable _table = null!;

        // 程序启动时间
        private static DateTime appStartTime = DateTime.Now;
        private static bool isFirstStartToday = false;

        // 单例管理 - 确保截屏窗口只有一个实例
        private static PickerOverlay? _activePickerOverlay = null;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            ViewModel = new MainViewModel();

            NLogger.LogFileDir = "Logs";
            NLogger.LogFileName = "DaemonKit.log";
            NLogger.Initialize();

            // 节点编辑窗口
            processNodeForm = new ProcessNodeForm();
            settingsWindow = new Settings();
            scheduleWindow = new Schedule();
            _table = new DaemonTable();

            this.WhenActivated(disposables =>
            {
                DataContext = this.ViewModel;

                Observable
                    .Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(200))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        var messages = NLogger.FetchMessage();
                        UpdateLogBox(messages);
                    });

                NLogger.Info("DaemonKit 已启动");
                Utils.ExecuteProgramsBeforeStart();

                FetchHardwareInfo();
                this.hardwareInfoBox
                    .Events()
                    .MouseDoubleClick.Subscribe(_contentLoaded =>
                    {
                        FetchHardwareInfo();
                    });

                this.ProcessTree.DataContext = this.DataContext;
                NLogger.Info("加载进程树..");
                loadExtensions();
                loadConfig();

                // 检查是否首次启动并启动计划任务监控
                CheckFirstStartToday();
                StartScheduleTaskMonitor();

                this.ProcessTree.Items.Add(rootProcessNode);

                ProcessItem _selectedTreeNode = rootProcessNode;

                // 选中进程树某个节点
                this.ProcessTree
                    .Events()
                    .SelectedItemChanged.Subscribe(_ =>
                    {
                        _selectedTreeNode = _.NewValue as ProcessItem;
                    });

                // 右键点击进程结点进行选择
                this.ProcessTree
                    .Events()
                    .PreviewMouseRightButtonDown.Subscribe(_ =>
                    {
                        var _source = _.OriginalSource as DependencyObject;
                        while (_source != null && !(_source is TreeViewItem))
                            _source = VisualTreeHelper.GetParent(_source);
                        var _treeItem = _source as TreeViewItem;
                        if (_treeItem != null)
                        {
                            _treeItem.Focus();
                            _.Handled = true;
                        }
                    });

                ViewModel.OpenSettings
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        // 每次创建新的设置窗口实例，避免重复使用已关闭的窗口
                        var newSettingsWindow = new Settings();
                        newSettingsWindow.ViewModel.SyncSettings(AppSettings);
                        var result = newSettingsWindow.ShowDialog();
                        if (result == true)
                        {
                            var oldHotKeyEnabled = AppSettings.EnableGlobalHotKey;
                            var oldTouchScreenDisabled = AppSettings.DisableTouchScreen;

                            AppSettings = newSettingsWindow.ViewModel.Confirm.Execute().Wait();
                            rootProcessNode.SyncSettings(AppSettings);
                            saveConfig();
                            Utils.SyncSettings();

                            // 动态注册/注销快捷键
                            if (AppSettings.EnableGlobalHotKey && !oldHotKeyEnabled)
                            {
                                Utils.RegisterHotKey(this);
                                NLogger.Info("已启用全局快捷键");
                            }
                            else if (!AppSettings.EnableGlobalHotKey && oldHotKeyEnabled)
                            {
                                Utils.UnRegisterHotKey(this);
                                NLogger.Info("已禁用全局快捷键");
                            }

                            // 动态启用/禁用触摸屏
                            if (AppSettings.DisableTouchScreen != oldTouchScreenDisabled)
                            {
                                try
                                {
                                    if (
                                        DeviceManager.SetTouchScreenEnabled(
                                            !AppSettings.DisableTouchScreen
                                        )
                                    )
                                    {
                                        NLogger.Info(
                                            $"触摸屏已{(AppSettings.DisableTouchScreen ? "禁用" : "启用")}"
                                        );
                                    }
                                    else
                                    {
                                        NLogger.Warn(
                                            $"触摸屏{(AppSettings.DisableTouchScreen ? "禁用" : "启用")}失败"
                                        );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error($"切换触摸屏状态时发生异常: {ex.Message}");
                                    MessageBox.Show(
                                        $"切换触摸屏失败: {ex.Message}",
                                        "错误",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Error
                                    );
                                }
                            }
                        }
                    });

                ViewModel.ToggleEnable.Subscribe(_item =>
                {
                    if (!_item.IsSuperRoot && _item.Enable && !rootProcessNode.Enable)
                    {
                        rootProcessNode.Enable = true;
                        rootProcessNode.MetaData.Enable = true;
                    }
                    _item.SyncEnable();
                    saveConfig();
                });

                ViewModel.EnableNameInput.Subscribe(_item =>
                {
                    _item.EnableNameInput();
                });

                ViewModel.ShowInExplorer.Subscribe(_ =>
                {
                    WinAPI.OpenProcess("explorer.exe", " /select," + _selectedTreeNode.NodePath);
                });

                // 添加进程结点
                ViewModel.AddTreeNode
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        processNodeForm.VM.SyncCreateFormProperties();
                        processNodeForm.Show();
                    });

                // 编辑进程结点
                ViewModel.EditTreeNode
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        processNodeForm.VM.SyncEditFormProperties(_selectedTreeNode.MetaData);
                        processNodeForm.Show();
                    });

                // 删除进程结点
                ViewModel.DeleteTreeNode.Subscribe(_ =>
                {
                    _selectedTreeNode.Parent!.RemoveChild(_selectedTreeNode);
                    saveConfig();
                });

                // 编辑结点计划任务
                ViewModel.EditSchedule
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        scheduleWindow.Title = scheduleWindow.ViewModel!.SetEditingProcessItem(
                            _selectedTreeNode
                        );
                        scheduleWindow.Show();
                        var scheduleHelper = new WindowInteropHelper(scheduleWindow);
                        ProcManager.KeepTopWindow(scheduleHelper.Handle, 0, 0, 0, 0);
                    });

                ViewModel.ConfirmNameInput.Subscribe(_ =>
                {
                    _.ConfirmNameInput();
                    saveConfig();
                });

                // 进程表单提交
                processNodeForm.VM.Confirm
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (processNodeForm.VM.IsCreateMode)
                        {
                            var _item = new ProcessItem { MetaData = _ };
                            _selectedTreeNode.AddChild(_item);
                            _item.SyncSettings(AppSettings);
                        }
                        else
                        {
                            _.Enable = _selectedTreeNode.Enable;
                            _selectedTreeNode.MetaData = _;
                        }
                        processNodeForm.Hide();

                        saveConfig();
                    });

                ViewModel.ShowAppDirectory.Subscribe(_ =>
                {
                    WinAPI.OpenProcess("explorer.exe", AppPathes.AppRoot);
                });

                ViewModel.SMBShare.Subscribe(_ =>
                {
                    WinAPI.OpenProcess(
                        Path.Combine(AppPathes.ExtensionPath, "SMBShare.bat"),
                        "",
                        true
                    );
                });

                ViewModel.SMBUnshare.Subscribe(_ =>
                {
                    WinAPI.OpenProcess(
                        Path.Combine(AppPathes.ExtensionPath, "SMBUnshare.bat"),
                        "",
                        true
                    );
                });

                ViewModel.OpenRemotePanel
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _table.Show();
                    });

                ViewModel.OpenScheduleWindow
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        scheduleWindow.Title = scheduleWindow.ViewModel!.SetEditingProcessItem(
                            rootProcessNode
                        );

                        scheduleWindow.Show();
                        // 窗口置于最前
                        var scheduleHelper = new WindowInteropHelper(scheduleWindow);
                        ProcManager.KeepTopWindow(scheduleHelper.Handle, 0, 0, 0, 0);
                    });

                ViewModel.PickColor
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (_activePickerOverlay != null)
                        {
                            _activePickerOverlay.Close();
                            _activePickerOverlay = null;
                        }
                        var overlay = new PickerOverlay { Mode = PickerOverlay.PickerMode.Color };
                        _activePickerOverlay = overlay;
                        overlay.Closed += (s, args) => _activePickerOverlay = null;
                        this.WindowState = System.Windows.WindowState.Minimized;
                        if (overlay.ShowDialog() == true)
                        {
                            NLogger.Info($"拾取颜色: {overlay.Result}");
                            MessageBox.Show(
                                $"颜色 {overlay.Result} 已复制到剪贴板",
                                "拾取成功",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                        }
                        this.WindowState = System.Windows.WindowState.Normal;
                    });

                ViewModel.PickPosition
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (_activePickerOverlay != null)
                        {
                            _activePickerOverlay.Close();
                            _activePickerOverlay = null;
                        }
                        var overlay = new PickerOverlay
                        {
                            Mode = PickerOverlay.PickerMode.Position
                        };
                        _activePickerOverlay = overlay;
                        overlay.Closed += (s, args) => _activePickerOverlay = null;
                        this.WindowState = System.Windows.WindowState.Minimized;
                        if (overlay.ShowDialog() == true)
                        {
                            NLogger.Info($"拾取位置: {overlay.Result}");
                            MessageBox.Show(
                                $"位置 {overlay.Result} 已复制到剪贴板",
                                "拾取成功",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                        }
                        this.WindowState = System.Windows.WindowState.Normal;
                    });

                ViewModel.TakeScreenshot
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (_activePickerOverlay != null)
                        {
                            _activePickerOverlay.Close();
                            _activePickerOverlay = null;
                        }
                        var overlay = new PickerOverlay
                        {
                            Mode = PickerOverlay.PickerMode.Screenshot
                        };
                        _activePickerOverlay = overlay;
                        overlay.Closed += (s, args) => _activePickerOverlay = null;
                        this.WindowState = System.Windows.WindowState.Minimized;
                        if (overlay.ShowDialog() == true)
                        {
                            NLogger.Info($"截图保存: {overlay.Result}");
                            MessageBox.Show(
                                $"截图已保存并复制到剪贴板\n{overlay.Result}",
                                "截图成功",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                        }
                        this.WindowState = System.Windows.WindowState.Normal;
                    });

                ViewModel.ToggleTouchScreen
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        try
                        {
                            AppSettings.DisableTouchScreen = !AppSettings.DisableTouchScreen;
                            if (
                                DeviceManager.SetTouchScreenEnabled(!AppSettings.DisableTouchScreen)
                            )
                            {
                                NLogger.Info(
                                    $"触摸屏已{(AppSettings.DisableTouchScreen ? "禁用" : "启用")}"
                                );
                                MessageBox.Show(
                                    $"触摸屏已{(AppSettings.DisableTouchScreen ? "禁用" : "启用")}",
                                    "成功",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information
                                );
                                saveConfig();
                            }
                            else
                            {
                                AppSettings.DisableTouchScreen = !AppSettings.DisableTouchScreen;
                                NLogger.Warn(
                                    $"触摸屏{(AppSettings.DisableTouchScreen ? "启用" : "禁用")}失败"
                                );
                                MessageBox.Show(
                                    $"触摸屏{(AppSettings.DisableTouchScreen ? "启用" : "禁用")}失败",
                                    "失败",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            NLogger.Error($"切换触摸屏状态异常: {ex.Message}");
                            MessageBox.Show(
                                $"切换触摸屏失败: {ex.Message}",
                                "错误",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                            );
                        }
                    });

                ViewModel.RunNodeTree
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        rootProcessNode.RunNode();
                    });

                ViewModel.KillNodeTree.Subscribe(_ =>
                {
                    rootProcessNode.KillNode();
                });

                ViewModel.RunProcess.Subscribe(_ =>
                {
                    WinAPI.OpenProcess(_.Path, _.Arguments, _.RunAs);
                });
                // 进程根节点启动守护
                rootProcessNode.RunNode();

                rootProcessNode
                    .AllChildren()
                    .Select(_ => _.ScheduleItems)
                    .SelectMany(_ => _)
                    .ToList()
                    .ForEach(_ => _.CalculateStatus());

                scheduleWindow.ViewModel.SaveCommand.Subscribe(_ =>
                {
                    var itemNode = scheduleWindow.ViewModel.EditingProcessItem;
                    itemNode.ScheduleItems = scheduleWindow.ViewModel.ScheduleItems.ToList();
                    itemNode
                        .AllChildren()
                        .Select(_ => _.ScheduleItems)
                        .SelectMany(_ => _)
                        .ToList()
                        .ForEach(_ => _.CalculateStatus());
                    saveConfig();
                });
                // TODO 测试结束

                ViewModel.ShowWindow.Subscribe(_ =>
                {
                    if (
                        this.Visibility == Visibility.Visible
                        && (this.WindowState != System.Windows.WindowState.Minimized)
                    )
                        return;
                    this.Visibility = Visibility.Visible;
                    var helper = new WindowInteropHelper(this);
                    WinAPI.SetWindowPos(
                        helper.Handle,
                        (int)HWndInsertAfter.HWND_TOPMOST,
                        0,
                        0,
                        0,
                        0,
                        SetWindowPosFlags.SWP_SHOWWINDOW
                            | SetWindowPosFlags.SWP_NOMOVE
                            | SetWindowPosFlags.SWP_NOSIZE
                            | SetWindowPosFlags.SWP_FRAMECHANGED
                    );
                    WinAPI.ShowWindow(helper.Handle, (int)CMDShow.SW_SHOWNORMAL);
                    NLogger.Info("显示主面板");
                });

                ViewModel.HideWindow.Subscribe(_ =>
                {
                    if (this.Visibility == Visibility.Hidden)
                        return;
                    this.Visibility = Visibility.Hidden;
                    NLogger.Info("隐藏主面板");
                });

                ViewModel.Quit.Subscribe(_ =>
                {
                    NLogger.Info("准备退出程序，请稍后...");
                    rootProcessNode.KillNode();
                    Utils.UnRegisterHotKey(this);
                    Application.Current.Shutdown();
                });

                ViewModel.ShutdownSystem.Subscribe(_ =>
                {
                    WinAPI.OpenProcess("shutdown.exe", "/s /t 0");
                });

                ViewModel.RestartSystem.Subscribe(_ =>
                {
                    WinAPI.OpenProcess("shutdown.exe", "/r /t 0");
                });

                if (AppSettings.DisableExplorer)
                {
                    WinAPI.OpenProcess("taskkill.exe", "/f /im explorer.exe");
                }

                if (AppSettings.MinimizeStartUp)
                {
                    ViewModel.HideWindow.Execute().Subscribe();
                }

                this.clockText.Text = DateTime.Now.ToString("yyyy-MM-dd H:mm:ss");
                Observable
                    .Interval(TimeSpan.FromSeconds(1))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        this.clockText.Text = DateTime.Now.ToString("yyyy-MM-dd H:mm:ss");

                        // TODO 执行结点计划任务
                        var _scheduleItems = rootProcessNode.RefreshSchedule();
                        _scheduleItems.ForEach(
                            ((ProcessItem processItem, ScheduleItem scheduleItem) item) =>
                            {
                                var (processItem, scheduleItem) = item;
                                if (scheduleItem.TaskType == ScheduleTaskType.Start)
                                {
                                    processItem.RunNode();
                                }
                                else if (scheduleItem.TaskType == ScheduleTaskType.Stop)
                                {
                                    processItem.KillNode();
                                }
                                else if (scheduleItem.TaskType == ScheduleTaskType.Shutdown)
                                {
                                    ViewModel.ShutdownSystem.Execute().Subscribe();
                                }
                                else if (scheduleItem.TaskType == ScheduleTaskType.Restart)
                                {
                                    ViewModel.RestartSystem.Execute().Subscribe();
                                }

                                scheduleItem.MarkAsExecuted();
                            }
                        );
                    });

                // 广播设备信息
                UdpClient _metaDataClient = new UdpClient();
                MachineInfo _machineInfo = new MachineInfo();
                _metaDataClient.EnableBroadcast = true;

                // 守护检测
                var _sendMsgDisposeable = Observable
                    .Timer(
                        TimeSpan.FromMilliseconds(AppSettings.DaemonInterval),
                        TimeSpan.FromSeconds(3)
                    )
                    .Subscribe(_ =>
                    {
                        _machineInfo.Name = rootProcessNode.Name;
                        var _ipList = HardwareInfo
                            .GetLocalIPv4Addresses()
                            .Aggregate(string.Empty, (_cur, _next) => _cur + _next);
                        _machineInfo.IPs =
                            new System.Collections.ObjectModel.ObservableCollection<string>(
                                HardwareInfo
                                    .GetLocalIPv4Addresses()
                                    .Select(_ipAddress => _ipAddress.ToString())
                            );
                        _machineInfo.CPUs =
                            new System.Collections.ObjectModel.ObservableCollection<string>(
                                hardwareInfo.CpuList.Select(_cpu => _cpu.Name)
                            );
                        _machineInfo.GPUs =
                            new System.Collections.ObjectModel.ObservableCollection<string>(
                                hardwareInfo.VideoControllerList.Select(_ => _.Name)
                            );
                        _machineInfo.Memories =
                            new System.Collections.ObjectModel.ObservableCollection<string>(
                                hardwareInfo.MemoryList.Select(
                                    _ => _.Manufacturer + _.PartNumber + _.Capacity.FormatBytes()
                                )
                            );
                        var _data = Encoding.UTF8.GetBytes(
                            JsonConvert.SerializeObject(_machineInfo)
                        );

                        _metaDataClient.Send(
                            _data,
                            _data.Count(),
                            new System.Net.IPEndPoint(
                                System.Net.IPAddress.Broadcast,
                                CommonVars.MetaPort
                            )
                        );

                        // crash进程检测
                        if (AppSettings.CrashWindows != null)
                        {
                            var _crashWindows = AppSettings.CrashWindows
                                .Split("|")
                                .Select(_crashWindow => WinAPI.FindProcess(_crashWindow))
                                .Where(_process => _process != default(Process))
                                .ToList();

                            if (rootProcessNode.IsRuning && _crashWindows.Count > 0)
                            {
                                _crashWindows.ForEach(_crashWindow =>
                                {
                                    _crashWindow.Kill();
                                });
                                NLogger.Info("检测到崩溃进程，尝试重启..");
                                rootProcessNode.KillNode();
                                rootProcessNode.RunNode();
                            }
                        }
                    });

                var _allChildNodes = rootProcessNode.AllChildren();

                // 命令控制
                var _recvCommandDisposable = onRecvCommand()
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_command =>
                    {
                        NLogger.Info($"收到远程命令: EventID={_command.EventID}");

                        if (_command.EventID == Command.RESTART)
                        {
                            NLogger.Info("执行系统重启命令");
                            ViewModel.RestartSystem.Execute().Subscribe();
                        }
                        else if (_command.EventID == Command.SHUTDOWN)
                        {
                            NLogger.Info("执行系统关机命令");
                            ViewModel.ShutdownSystem.Execute().Subscribe();
                        }
                        else if (_command.EventID == Command.RESTART_NODE_TREE)
                        {
                            NLogger.Info("执行重启进程树命令");
                            rootProcessNode.RunNode();
                        }
                        else if (_command.EventID == Command.STOP)
                        {
                            NLogger.Info("执行停止进程树命令");
                            rootProcessNode.KillNode();
                        }
                        else if (_command.EventID == Command.HEARTBEAT)
                        {
                            // 心跳处理
                            try
                            {
                                if (_command.Data == null)
                                {
                                    NLogger.Warn("心跳命令缺少数据");
                                    return;
                                }

                                var _processPath = _command.Data.Value<string>("process");

                                if (string.IsNullOrEmpty(_processPath))
                                {
                                    NLogger.Warn("心跳命令缺少进程路径");
                                    return;
                                }

                                _processPath = _processPath.ToForwardSlash();
                                var _processNode = _allChildNodes
                                    .Where(_node => _node.NodePath.ToForwardSlash() == _processPath)
                                    .FirstOrDefault();

                                if (_processNode == null)
                                {
                                    NLogger.Warn($"未找到进程节点: {_processPath}");
                                    return;
                                }

                                _processNode.NotifyHeartbeat();
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error($"处理心跳命令异常: {ex.Message}");
                            }
                        }
                        else
                        {
                            NLogger.Warn($"收到未知命令: EventID={_command.EventID}");
                        }
                    });

                this.Events()
                    .Closed.Subscribe(_ =>
                    {
                        _recvCommandDisposable.Dispose();
                        _sendMsgDisposeable.Dispose();

                        _metaDataClient.Close();
                        _metaDataClient.Dispose();

                        NLogger.Info("程序已退出,再见...");
                    });
            });

            var _appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.Title = $"运维管家 v{_appVersion.Major}.{_appVersion.Minor}.{_appVersion.Build}";

            InputBindings.Add(
                new KeyBinding
                {
                    Command = ViewModel.ShowAppDirectory,
                    Key = Key.D1,
                    Modifiers = ModifierKeys.Control
                }
            );
            InputBindings.Add(
                new KeyBinding
                {
                    Command = ViewModel.RunProcess,
                    Key = Key.D2,
                    Modifiers = ModifierKeys.Control,
                    CommandParameter = ViewModel.OpenFileExplorer_args
                }
            );

            InputBindings.Add(
                new KeyBinding
                {
                    Command = ViewModel.RunProcess,
                    Key = Key.T,
                    Modifiers = ModifierKeys.Control,
                    CommandParameter = ViewModel.OpenCMD_args
                }
            );
            InputBindings.Add(
                new KeyBinding
                {
                    Command = ViewModel.RunProcess,
                    Key = Key.P,
                    Modifiers = ModifierKeys.Control,
                    CommandParameter = ViewModel.OpenPowerShell_args
                }
            );
            InputBindings.Add(
                new KeyBinding
                {
                    Command = ViewModel.ToggleTouchScreen,
                    Key = Key.T,
                    Modifiers = ModifierKeys.Control | ModifierKeys.Shift
                }
            );
        }

        private IObservable<Command> onRecvCommand()
        {
            return Observable.Create<Command>(observer =>
            {
                var cts = new CancellationTokenSource();

                UdpClient commandClient = null;
                UdpClient heartbeatClient = null;

                // 订阅取消时执行的清理操作
                var disposable = Disposable.Create(() =>
                {
                    cts.Cancel();

                    try
                    {
                        commandClient?.Close();
                        heartbeatClient?.Close();
                    }
                    catch { }

                    commandClient?.Dispose();
                    heartbeatClient?.Dispose();

                    cts.Dispose();
                });

                try
                {
                    // 初始化 UDP 客户端
                    commandClient = new UdpClient(CommonVars.ControlPort);
                    heartbeatClient = new UdpClient(CommonVars.HeartbeatPort);

                    // 启动两个异步任务，接收数据并推送
                    System.Threading.Tasks.Task.Run(
                        async () =>
                        {
                            try
                            {
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var result = await commandClient
                                        .ReceiveAsync()
                                        .ConfigureAwait(false);
                                    var cmdStr = Encoding.UTF8.GetString(result.Buffer);
                                    var cmd = JsonConvert.DeserializeObject<Command>(cmdStr);

                                    // 空值检查
                                    if (cmd != null)
                                    {
                                        observer.OnNext(cmd);
                                    }
                                    else
                                    {
                                        NLogger.Warn("接收到无效的命令数据（反序列化为null）");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // 不终止流，记录错误并继续
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    NLogger.Error($"接收控制命令异常: {ex.Message}");
                                }
                            }
                        },
                        cts.Token
                    );

                    System.Threading.Tasks.Task.Run(
                        async () =>
                        {
                            try
                            {
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var result = await heartbeatClient
                                        .ReceiveAsync()
                                        .ConfigureAwait(false);
                                    var cmdStr = Encoding.UTF8.GetString(result.Buffer);
                                    var cmd = JsonConvert.DeserializeObject<Command>(cmdStr);

                                    // 空值检查
                                    if (cmd == null)
                                    {
                                        NLogger.Warn("接收到无效的心跳数据（反序列化为null）");
                                        continue;
                                    }

                                    if (cmd.EventID != Command.HEARTBEAT)
                                        continue;
                                    observer.OnNext(cmd);
                                }
                            }
                            catch (Exception ex)
                            {
                                // 不终止流，记录错误并继续
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    NLogger.Error($"接收心跳命令异常: {ex.Message}");
                                }
                            }
                        },
                        cts.Token
                    );
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                }

                return disposable;
            });
        }

        #endregion

        #region Log Display with Color Coding

        private string _lastLogContent = string.Empty;

        /// <summary>
        /// 更新日志显示，根据警告级别添加颜色
        /// </summary>
        private void UpdateLogBox(List<string> messages)
        {
            if (messages == null || messages.Count == 0)
                return;

            var newContent = string.Join("\r\n", messages);
            if (newContent == _lastLogContent)
                return;

            _lastLogContent = newContent;

            var document = new System.Windows.Documents.FlowDocument();
            var paragraph = new System.Windows.Documents.Paragraph();

            foreach (var message in messages)
            {
                var run = new System.Windows.Documents.Run(message + "\r\n");

                // 根据日志级别设置颜色 (NLog格式: [Info], [Warn], [Error], [Debug])
                if (message.Contains("[Error]") || message.Contains("[Fatal]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // #F44336 红色
                    run.FontWeight = FontWeights.Medium;
                }
                else if (message.Contains("[Warn]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // #FF9800 橙色
                }
                else if (message.Contains("[Info]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); // #616161 深灰
                }
                else if (message.Contains("[Debug]") || message.Contains("[Trace]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // #9E9E9E 浅灰
                }
                else
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); // 默认颜色
                }

                paragraph.Inlines.Add(run);
            }
            document.Blocks.Add(paragraph);
            logBox.Document = document;
            logBox.ScrollToEnd();
        }

        #endregion

        #region Hardware Info

        static readonly HardwareInfo hardwareInfo = new HardwareInfo();

        // 硬件信息富文本样式
        private static readonly SolidColorBrush HardwareLabelBrush = new SolidColorBrush(
            Color.FromRgb(0x42, 0x42, 0x42)
        );
        private static readonly SolidColorBrush HardwareValueBrush = new SolidColorBrush(
            Color.FromRgb(0x37, 0x47, 0x4F)
        );
        private static readonly SolidColorBrush HardwareSecondaryBrush = new SolidColorBrush(
            Color.FromRgb(0x75, 0x75, 0x75)
        );

        static MainWindow()
        {
            // 冻结画刷以提高性能
            HardwareLabelBrush.Freeze();
            HardwareValueBrush.Freeze();
            HardwareSecondaryBrush.Freeze();
        }

        /// <summary>
        /// 拉取硬件信息
        /// </summary>
        private void FetchHardwareInfo()
        {
            UpdateHardwareInfoBox("⏳ 硬件信息读取中...");

            Utils
                .FetchHardwareInfo()
                .Subscribe(_text =>
                {
                    UpdateHardwareInfoBox(_text);
                });
        }

        /// <summary>
        /// 更新硬件信息显示（富文本格式）
        /// </summary>
        private void UpdateHardwareInfoBox(string text)
        {
            var document = new System.Windows.Documents.FlowDocument();
            var paragraph = new System.Windows.Documents.Paragraph();
            paragraph.LineHeight = 1.8;
            paragraph.Margin = new Thickness(0);

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                    continue;
                }

                // 检查是否是标签行（以冒号结尾，且冒号后没有内容或只有空格）
                if (
                    line.EndsWith(":")
                    || (
                        line.Contains(":")
                        && line.Substring(line.IndexOf(':') + 1).Trim().Length == 0
                    )
                )
                {
                    // 标签行 - 深灰色，粗体，14号字
                    var labelRun = new System.Windows.Documents.Run(line + "\r\n")
                    {
                        Foreground = HardwareLabelBrush,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14
                    };
                    paragraph.Inlines.Add(labelRun);
                }
                else
                {
                    // 值行 - 蓝色，13号字
                    var valueRun = new System.Windows.Documents.Run(line + "\r\n")
                    {
                        Foreground = HardwareValueBrush,
                        FontSize = 13
                    };
                    paragraph.Inlines.Add(valueRun);
                }
            }

            document.Blocks.Add(paragraph);
            hardwareInfoBox.Document = document;
        }

        #endregion

        #region Configuration Management

        /// <summary>
        /// 加载拓展菜单
        /// </summary>
        private void loadExtensions()
        {
            if (!System.IO.File.Exists(AppPathes.ExtensionConfigPath))
            {
                USerialization.SerializeXML(new ExtensionConfig(), AppPathes.ExtensionConfigPath);
            }

            try
            {
                var _extConfig = USerialization.DeserializeXML<ExtensionConfig>(
                    AppPathes.ExtensionConfigPath
                );
                var _sysMgrMenu = new MenuItem { Header = "系统" };
                var _toolMenu = new MenuItem { Header = "工具" };

                _extConfig.Extensions
                    .WithIndex()
                    .ToList()
                    .ForEach(_extention =>
                    {
                        var _menuItem = new MenuItem { Header = _extention.item.Name };

                        Action<(Extension item, int index)> _handleMenuClick = (_ext) =>
                        {
                            var _extensionPath = Path.Combine(
                                AppPathes.ExtensionPath,
                                _ext.item.Path
                            );
                            if (
                                !Path.IsPathRooted(_ext.item.Path)
                                && System.IO.File.Exists(_extensionPath)
                            )
                            {
                                WinAPI.OpenProcess(_extensionPath, _ext.item.Args, _ext.item.RunAs);
                            }
                            else
                            {
                                WinAPI.OpenProcess(_ext.item.Path, _ext.item.Args, _ext.item.RunAs);
                            }
                        };

                        _menuItem
                            .Events()
                            .Click.Subscribe(_ =>
                            {
                                _handleMenuClick(_extention);
                            });

                        var _menuCommand = ReactiveCommand.Create<
                            (Extension item, int index),
                            (Extension item, int index)
                        >(_param => _param);
                        _menuCommand.Subscribe(_ext =>
                        {
                            _handleMenuClick(_ext);
                        });

                        //_menuItem.InputGestureText = string.Format ("Ctrl+F{0}", _extention.index + 1);
                        InputBindings.Add(
                            new KeyBinding
                            {
                                Command = _menuCommand,
                                Key = Key.F1 + _extention.index,
                                Modifiers = ModifierKeys.Control,
                                CommandParameter = _extention
                            }
                        );
                        if (_extention.item.Group == "System")
                        {
                            _sysMgrMenu.Items.Add(_menuItem);
                        }
                        else
                        {
                            _toolMenu.Items.Add(_menuItem);
                        }
                    });

                this.MainMenu.Items.Insert(2, _sysMgrMenu);
                this.MainMenu.Items.Insert(3, _toolMenu);
            }
            catch (System.Exception) { }
        }

        /// <summary>
        /// 加载配置文件
        /// </summary>
        private void loadConfig()
        {
            if (!System.IO.File.Exists(AppPathes.TreeViewDataPath))
            {
                if (!Directory.Exists(Path.GetDirectoryName(AppPathes.TreeViewDataPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPathes.TreeViewDataPath));
                if (System.IO.File.Exists(AppPathes.TreeViewDataPath_Backup))
                {
                    System.IO.File.Copy(
                        AppPathes.TreeViewDataPath_Backup,
                        AppPathes.TreeViewDataPath,
                        true
                    );
                }
                else
                {
                    rootProcessNode = new ProcessItem
                    {
                        MetaData = new ProcessMetaData
                        {
                            Name = "[ 进程树 ]",
                            Delay = 0,
                            Path = string.Empty
                        }
                    };
                    USerialization.SerializeXML(rootProcessNode, AppPathes.TreeViewDataPath);
                }
            }
            if (
                System.IO.File.ReadAllText(AppPathes.TreeViewDataPath).Length == 0
                && System.IO.File.Exists(AppPathes.TreeViewDataPath_Backup)
            )
            {
                System.IO.File.Copy(
                    AppPathes.TreeViewDataPath_Backup,
                    AppPathes.TreeViewDataPath,
                    true
                );
            }
            rootProcessNode = USerialization.DeserializeXML<ProcessItem>(
                AppPathes.TreeViewDataPath
            );
            rootProcessNode.SyncRelationships();

            if (!System.IO.File.Exists(AppPathes.AppSettingPath))
            {
                USerialization.SerializeXML(new AppSettings(), AppPathes.AppSettingPath);
            }
            if (
                System.IO.File.ReadAllText(AppPathes.AppSettingPath).Length == 0
                && System.IO.File.Exists(AppPathes.AppSettingPath_Backup)
            )
            {
                System.IO.File.Copy(
                    AppPathes.AppSettingPath_Backup,
                    AppPathes.AppSettingPath,
                    true
                );
            }
            AppSettings = USerialization.DeserializeXML<AppSettings>(AppPathes.AppSettingPath);

            Utils.SyncSettings();
            rootProcessNode.SyncSettings(AppSettings);

            // 根据配置决定是否注册全局快捷键
            if (AppSettings.EnableGlobalHotKey)
            {
                Utils.RegisterHotKey(this);
                NLogger.Info("已注册全局快捷键");
            }

            // 根据配置决定是否禁用触摸屏
            if (AppSettings.DisableTouchScreen)
            {
                try
                {
                    if (DeviceManager.SetTouchScreenEnabled(false))
                    {
                        NLogger.Info("触摸屏已禁用");
                    }
                    else
                    {
                        NLogger.Warn("触摸屏禁用失败");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Error($"初始化触摸屏状态时发生异常: {ex.Message}");
                }
            }
        }

        // 数据持久化
        private void saveConfig()
        {
            USerialization.SerializeXML(rootProcessNode, AppPathes.TreeViewDataPath);
            USerialization.SerializeXML(AppSettings, AppPathes.AppSettingPath);
            if (!Directory.Exists(AppPathes.ConfigDir_BackUp))
            {
                Directory.CreateDirectory(AppPathes.ConfigDir_BackUp);
                WinAPI.OpenProcess("attrib.exe", $"+h {AppPathes.ConfigDir_BackUp}");
            }
            // 备份配置文件
            System.IO.File.Copy(
                AppPathes.TreeViewDataPath,
                AppPathes.TreeViewDataPath_Backup,
                true
            );
            System.IO.File.Copy(
                AppPathes.ExtensionConfigPath,
                AppPathes.ExtensionConfigPath_Backup,
                true
            );
            System.IO.File.Copy(AppPathes.AppSettingPath, AppPathes.AppSettingPath_Backup, true);

            NLogger.Info("配置文件保存成功.");
        }

        #endregion

        #region Window Lifecycle & Hotkey Handling

        private HwndSource _source;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source.AddHook(HwndHook);
            // 快捷键注册移至loadConfig之后，确保AppSettings已加载
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            ViewModel.HideWindow.Execute().Subscribe();
            e.Cancel = true;
            base.OnClosing(e);
        }

        private IntPtr HwndHook(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled
        )
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            switch (msg)
            {
                case WM_HOTKEY:
                    if (wParam.ToInt32() == 88)
                    {
                        handled = true;
                        ViewModel.Quit.Execute().Subscribe();
                    }
                    if (wParam.ToInt32() == 99)
                    {
                        handled = true;
                        if (
                            this.Visibility == Visibility.Hidden
                            || this.WindowState == System.Windows.WindowState.Minimized
                        )
                        {
                            ViewModel.ShowWindow.Execute().Subscribe();
                        }
                    }
                    else if (wParam.ToInt32() == HOTKEY_ID)
                    {
                        // Alt+X 快捷键被按下，触发截图
                        handled = true;
                        Dispatcher.BeginInvoke(
                            new System.Action(() =>
                            {
                                TriggerScreenshot();
                            })
                        );
                    }
                    else if (wParam.ToInt32() == 100)
                    { //Ctrl+D
                        handled = true;
                        if (
                            this.Visibility == Visibility.Hidden
                            || this.WindowState == System.Windows.WindowState.Minimized
                        )
                        {
                            ViewModel.ShowWindow.Execute().Subscribe();
                        }
                        else
                        {
                            ViewModel.HideWindow.Execute().Subscribe();
                        }
                    }
                    else if (wParam.ToInt32() == 101)
                    { //Ctrl+R
                        handled = true;
                        ViewModel.RunNodeTree.Execute().Subscribe();
                    }
                    else if (wParam.ToInt32() == 102)
                    { //Ctrl+W
                        handled = true;
                        ViewModel.KillNodeTree.Execute().Subscribe();
                    }
                    else if (wParam.ToInt32() == 103)
                    {
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.OpenFileExplorer_args).Subscribe();
                    }
                    else if (wParam.ToInt32() == 104)
                    {
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.KillFileExplorer_args).Subscribe();
                    }
                    break;
                case WM_QUERYENDSESSION:
                    break;
                case WM_ENDSESSION:
                    break;
            }
            return IntPtr.Zero;
        }

        #endregion

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
            NLogger.Info($"程序启动时间: {appStartTime}, 是否每天首次启动: {isFirstStartToday}");
        }

        /// <summary>
        /// 启动计划任务监控
        /// </summary>
        private void StartScheduleTaskMonitor()
        {
            // 每秒检查一次任务
            Observable
                .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    CheckAndExecuteScheduleTasks();
                });
        }

        /// <summary>
        /// 检查并执行计划任务
        /// </summary>
        private void CheckAndExecuteScheduleTasks()
        {
            var allItems = rootProcessNode
                .AllChildren()
                .Select(_ => _.ScheduleItems)
                .SelectMany(_ => _)
                .ToList();

            foreach (var item in allItems)
            {
                if (!item.CanExecute())
                    continue;

                // 检查程序启动后的任务
                if (item.Trigger == Core.TriggerType.OnAppStart)
                {
                    var elapsed = (DateTime.Now - appStartTime).TotalSeconds;
                    if (elapsed >= item.DelaySeconds)
                    {
                        ExecuteScheduleTask(item);
                    }
                }
                else if (item.Trigger == Core.TriggerType.OnAppStartOnce && isFirstStartToday)
                {
                    var elapsed = (DateTime.Now - appStartTime).TotalSeconds;
                    if (elapsed >= item.DelaySeconds)
                    {
                        ExecuteScheduleTask(item);
                    }
                }
                else if (item.Trigger == Core.TriggerType.Daily)
                {
                    if (item.CanExecute())
                    {
                        ExecuteScheduleTask(item);
                    }
                }
            }
        }

        /// <summary>
        /// 执行计划任务
        /// </summary>
        private async void ExecuteScheduleTask(ScheduleItem item)
        {
            item.MarkAsExecuted();
            NLogger.Info($"开始执行任务: {item.TaskType}");

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
                NLogger.Error($"任务执行失败: {ex.Message}");
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
        private async System.Threading.Tasks.Task ExecuteShutdownTask()
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
        private async System.Threading.Tasks.Task ExecuteRestartTask()
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
        private async System.Threading.Tasks.Task ExecuteRestartAppTask()
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
        private async System.Threading.Tasks.Task<bool> ShowCountdownConfirm(
            string title,
            string message
        )
        {
            var taskCompletionSource = new TaskCompletionSource<bool>();

            await Dispatcher.InvokeAsync(() =>
            {
                var dialog = new CountdownConfirmDialog
                {
                    ViewModel = new CountdownConfirmViewModel(title, message, 10)
                };

                var result = dialog.ShowDialog();
                taskCompletionSource.SetResult(result == true);
            });

            return await taskCompletionSource.Task;
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
                NLogger.Error($"程序重启失败: {ex.Message}");
                MessageBox.Show(
                    $"程序重启失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void TriggerScreenshot()
        {
            try
            {
                var overlay = new PickerOverlay { Mode = PickerOverlay.PickerMode.Screenshot };
                this.WindowState = System.Windows.WindowState.Minimized;
                if (overlay.ShowDialog() == true)
                {
                    NLogger.Info($"截图保存: {overlay.Result}");
                    MessageBox.Show(
                        $"截图已保存并复制到剪贴板\n{overlay.Result}",
                        "截图成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                this.WindowState = System.Windows.WindowState.Minimized;
            }
            catch (Exception ex)
            {
                NLogger.Error($"截图失败: {ex.Message}");
            }
        }

        #endregion
    }
}
