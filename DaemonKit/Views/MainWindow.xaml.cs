using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
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
using DaemonKit.Models;
using DaemonKit.Utilities;
using DaemonKit.Views;
using DaemonKit.PowerSaving;
using DaemonKit.Services;
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
using Splat;
using System.Collections.Generic;

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

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

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

        // 服务层
        private PowerSavingService _powerSavingService = null!;
        private IdleMonitorService _idleMonitorService = null!;
        private NetworkBroadcastService _networkBroadcastService = null!;
        private CrashDetectionService? _crashDetectionService;
        private P2PFileTransferService _p2pService = null!;
        private TransferTaskManager _transferTaskManager = null!;
        private Views.TransferListWindow? _transferListWindow;
        private Views.ResourceLibraryWindow? _resourceLibraryWindow;

        // 全局计划任务配置
        public static GlobalScheduleConfig GlobalSchedule { get; set; } = null!;

        // 程序启动时间
        private static DateTime appStartTime = DateTime.Now;
        private static bool isFirstStartToday = false;

        // 单例管理 - 确保截屏窗口只有一个实例
        private static PickerOverlay? _activePickerOverlay = null;

        // 新的任务调度引擎
        private ScheduleTaskEngine _scheduleTaskEngine = null!;

        // 倒计时对话框单例与等待队列
        private CountdownConfirmDialog? _activeCountdownDialog;
        private readonly List<TaskCompletionSource<bool>> _countdownAwaiters = new();

        // UI 线程诊断检查点：看门狗读取此字段定位阻塞回调
        internal static volatile string _uiCheckpoint = "init";

        // 导入导出全局互斥锁 - 确保同时只有一个在进行
        private static bool _isExporting = false;
        private static bool _isImporting = false;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            ViewModel = new MainViewModel();

            NLogger.LogFileDir = AppPathes.LogsDir;
            NLogger.LogFileName = "DaemonKit.log";
            NLogger.Initialize();

            // 确保所有目录存在
            AppPathes.EnsureDirectories();

            // 节点编辑窗口
            processNodeForm = new ProcessNodeForm();
            settingsWindow = new Settings();
            scheduleWindow = new Schedule();

            // 初始化P2P文件传输服务和任务管理器（应用级生命周期，不依赖联调面板）
            // 注意：此时 AppSettings 尚未加载，使用默认值；loadConfig 后会通过 UpdateMaxConcurrentTransfers 同步
            _p2pService = Locator.Current.GetService<P2PFileTransferService>()!;
            _transferTaskManager = Locator.Current.GetService<TransferTaskManager>()!;
            _transferTaskManager.LoadHistory();
            _table = new DaemonTable(_p2pService, _transferTaskManager);

            // 初始化服务层（在 AppSettings 加载后会调用 Initialize）
            _powerSavingService = Locator.Current.GetService<PowerSavingService>()!;
            // _idleMonitorService 将在 loadConfig 后初始化

            // 订阅软件包操作进度消息
            ReactiveUI.MessageBus.Current
                .Listen<PackageProgressInfo>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnPackageProgressUpdate);

            // 订阅"在资源库中查看此设备"跳转消息
            ReactiveUI.MessageBus.Current
                .Listen<string>("OpenResourceLibrary")
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(deviceFilter => ShowResourceLibraryWindow(deviceFilter));

            // 订阅配置包导入完成消息 — 导入实际完成后才执行热重载
            ReactiveUI.MessageBus.Current
                .Listen<bool>("TreeBundleImportCompleted")
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(autoStart => ReloadAfterImport(autoStart));

            this.WhenActivated(disposables =>
            {
                var startTime = DateTime.Now;
                DataContext = this.ViewModel;

                Observable
                    .Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(200))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _uiCheckpoint = "200ms_logUpdate";
                        var messages = NLogger.FetchMessage();
                        UpdateLogBox(messages);
                        _uiCheckpoint = "idle";
                    });

                NLogger.Info("DaemonKit 启动中...");
                Utils.ExecuteProgramsBeforeStart();

                // 硬件信息获取改为后台异步加载，不阻塞主窗口
                this.hardwareInfoBox
                    .Events()
                    .MouseDoubleClick.Subscribe(_contentLoaded =>
                    {
                        FetchHardwareInfo();
                    });

                this.ProcessTree.DataContext = this.DataContext;
                loadExtensions();
                loadConfig();

                this.ProcessTree.Items.Add(rootProcessNode);

                // 直接调用 async void 方法：InitializeBackgroundServices 内部首行就是 await Task.Run(...)，
                // 会立即 yield 回调方, 不阻塞后续步骤 5-9 的同步执行。
                // 注意：不能用 Dispatcher.InvokeAsync(Background)，因为 200ms/500ms/1s 的定时器
                // 以 Normal 优先级持续派发, 会永久饿死 Background 优先级的回调。
                InitializeBackgroundServices(startTime);

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
                            var oldEnableIdleAutoPowerSaving =
                                AppSettings.EnableIdleAutoPowerSaving;

                            AppSettings = newSettingsWindow.ViewModel.Confirm.Execute().Wait();
                            rootProcessNode.SyncSettings(AppSettings);
                            saveConfig();
                            Utils.SyncSettings();

                            // 应用端口覆盖和认证设置
                            CommonVars.ApplyPortOverrides(
                                AppSettings.CustomMetaPort,
                                AppSettings.CustomControlPort,
                                AppSettings.CustomFileTransferPort,
                                AppSettings.AuthToken
                            );

                            // 同步设置到 PowerSavingService
                            _powerSavingService.EnableIdleAutoPowerSaving =
                                AppSettings.EnableIdleAutoPowerSaving;

                            // 同步最大并发传输数到 P2P 服务
                            _p2pService.UpdateMaxConcurrentTransfers(
                                AppSettings.MaxConcurrentTransfers
                            );

                            // 如果 EnableIdleAutoPowerSaving 状态改变，记录日志
                            if (
                                oldEnableIdleAutoPowerSaving
                                != AppSettings.EnableIdleAutoPowerSaving
                            )
                            {
                                NLogger.Info(
                                    $"空闲自动省电已{(AppSettings.EnableIdleAutoPowerSaving ? "启用" : "禁用")}"
                                );
                            }

                            // 动态注册/注销快捷键
                            if (AppSettings.EnableGlobalHotKey && !oldHotKeyEnabled)
                            {
                                Utils.RegisterHotKey(this, AppSettings);
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
                                    NLogger.Error("切换触摸屏状态时发生异常: {Message}", ex.Message);
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

                ViewModel.ExportNodePackage.Subscribe(_item =>
                {
                    if (_item == null || _item.IsSuperRoot)
                        return;
                    if (
                        string.IsNullOrEmpty(_item.NodePath)
                        || !System.IO.File.Exists(_item.NodePath)
                    )
                    {
                        MessageBox.Show(
                            $"节点程序文件不存在：{_item.NodePath}",
                            "提示",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        return;
                    }
                    var dialog = new NodePackageExportDialog(_item) { Owner = this };
                    dialog.ShowDialog();
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

                // 清空进程树
                ViewModel.ClearProcessTree.Subscribe(_ =>
                {
                    if (rootProcessNode == null || rootProcessNode.Children == null || rootProcessNode.Children.Count == 0)
                        return;

                    var result = MessageBox.Show(
                        $"确定要清空进程树中的所有 {rootProcessNode.Children.Count} 个子节点吗？\n此操作不可撤销。",
                        "清空进程树",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );
                    if (result != MessageBoxResult.Yes)
                        return;

                    // 先停止所有进程
                    rootProcessNode.KillNode();
                    rootProcessNode.Children.Clear();
                    NLogger.Info("[进程树] 已清空所有子节点");
                    saveConfig();
                });

                // 编辑结点计划任务
                ViewModel.EditSchedule
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        scheduleWindow.ViewModel!.SetGlobalConfig(GlobalSchedule, rootProcessNode);
                        scheduleWindow.Title = "全局计划任务";
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

                ViewModel.OpenRemotePanel
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _table.Show();
                    });

                ViewModel.OpenTransferList
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => ShowTransferListWindow());

                ViewModel.OpenResourceLibrary
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => ShowResourceLibraryWindow());

                ViewModel.OpenScheduleWindow
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        scheduleWindow.ViewModel!.SetGlobalConfig(GlobalSchedule, rootProcessNode);
                        scheduleWindow.Title = "全局计划任务";

                        scheduleWindow.Show();
                        // 窗口置于最前
                        var scheduleHelper = new WindowInteropHelper(scheduleWindow);
                        ProcManager.KeepTopWindow(scheduleHelper.Handle, 0, 0, 0, 0);
                    });

                ViewModel.OpenPowerSavingPanel
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        var powerSavingWindow = _powerSavingService.GetOrCreateWindow(AppSettings);

                        // Update MainViewModel reference
                        if (ViewModel != null)
                        {
                            ViewModel.PowerSaving = _powerSavingService.ViewModel;
                        }

                        // 设置配置改变时的保存回调
                        _powerSavingService.ViewModel.OnConfigChanged = () =>
                        {
                            _powerSavingService.SaveSettings(AppSettings);
                            SaveAppSettingsOnly();
                        };

                        powerSavingWindow.Show();
                        powerSavingWindow.Activate();
                        var helper = new WindowInteropHelper(powerSavingWindow);
                        ProcManager.KeepTopWindow(helper.Handle, 0, 0, 0, 0);
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
                            NLogger.Info("拾取颜色: {Result}", overlay.Result);
                            MessageBox.Show(
                                $"颜色 {overlay.Result} 已复制到剪贴板",
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
                            NLogger.Info("截图保存: {Result}", overlay.Result);
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
                            NLogger.Error("切换触摸屏状态异常: {Message}", ex.Message);
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
                    scheduleWindow.ViewModel.SaveTaskConfigs();
                    saveConfig();
                    NLogger.Info("全局任务计划已保存");
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
                });

                ViewModel.HideWindow.Subscribe(_ =>
                {
                    if (this.Visibility == Visibility.Hidden)
                        return;
                    this.Visibility = Visibility.Hidden;
                });

                ViewModel.Quit.Subscribe(_ =>
                {
                    NLogger.Info("准备退出程序，请稍后...");
                    rootProcessNode.KillNode();
                    Utils.UnRegisterHotKey(this);
                    Application.Current.Shutdown();
                });

                ViewModel.ShutdownSystem.Subscribe(async _ =>
                {
                    // 调试模式 - 仅测试确认对话框
                    await ExecuteShutdownTask();
                });

                ViewModel.RestartSystem.Subscribe(async _ =>
                {
                    // 调试模式 - 仅测试确认对话框
                    await ExecuteRestartTask();
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
                    .Subscribe(async _ =>
                    {
                        _uiCheckpoint = "1s_clock+schedule";
                        this.clockText.Text = DateTime.Now.ToString("yyyy-MM-dd H:mm:ss");

                        // TODO 执行结点计划任务
                        var _scheduleItems = rootProcessNode.RefreshSchedule();
                        foreach (var item in _scheduleItems)
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
                                await ExecuteShutdownTask();
                            }
                            else if (scheduleItem.TaskType == ScheduleTaskType.Restart)
                            {
                                await ExecuteRestartTask();
                            }

                            scheduleItem.MarkAsExecuted();
                        }
                        _uiCheckpoint = "idle";
                    });

                // 空闲监控已迁移到 IdleMonitorService，在 loadConfig 后启动

                // 网络广播和命令接收已迁移到 NetworkBroadcastService
                _networkBroadcastService = Locator.Current.GetService<NetworkBroadcastService>()!;
                _networkBroadcastService.Start(rootProcessNode, AppSettings.DaemonInterval);

                // 启动P2P文件传输服务器（应用级，不依赖联调面板打开）
                // 在后台线程启动，因为端口被占用时可能需要重试等待
                _p2pService.UpdateMaxConcurrentTransfers(AppSettings.MaxConcurrentTransfers);
                _p2pService.MachineInfoProvider = () =>
                    _networkBroadcastService.CurrentMachineInfo;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _p2pService.StartServer();
                        NLogger.Info("[P2P] 文件传输服务已随应用启动");
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error("[P2P] 启动文件传输服务失败: {Message}", ex.Message);
                    }
                });

                // 订阅传输服务事件 → 委托给TransferTaskManager统一管理
                // 使用Buffer替代GroupBy+Sample，避免每个TaskId创建永久定时器导致泄漏
                _p2pService.TransferProgress
                    .Buffer(TimeSpan.FromMilliseconds(200))
                    .Where(batch => batch.Count > 0)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(batch =>
                    {
                        // 每个TaskId只取最新一条
                        foreach (var task in batch.GroupBy(t => t.TaskId).Select(g => g.Last()))
                        {
                            _transferTaskManager.TrackTask(task);
                        }
                    })
                    .DisposeWith(disposables);

                _p2pService.TransferCompleted
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(task => _transferTaskManager.CompleteTask(task.TaskId, true))
                    .DisposeWith(disposables);

                _p2pService.TransferFailed
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(
                        task =>
                            _transferTaskManager.CompleteTask(task.TaskId, false, task.ErrorMessage)
                    )
                    .DisposeWith(disposables);

                // 状态栏传输状态刷新（每500ms检查）
                Observable
                    .Interval(TimeSpan.FromMilliseconds(500))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        _uiCheckpoint = "500ms_statusBar";
                        UpdateTransferStatusBar();
                        _uiCheckpoint = "idle";
                    })
                    .DisposeWith(disposables);

                // 设置硬件信息就绪回调，自动更新硬件信息显示
                _networkBroadcastService.SetHardwareInfoReadyCallback(() =>
                {
                    Dispatcher.InvokeAsync(() => FetchHardwareInfo());
                });

                // 崩溃检测已迁移到 CrashDetectionService
                if (!string.IsNullOrWhiteSpace(AppSettings.CrashWindows))
                {
                    _crashDetectionService = new CrashDetectionService();
                    _crashDetectionService.Start(rootProcessNode, AppSettings.CrashWindows);
                }

                var _allChildNodes = rootProcessNode.AllChildren();

                // 订阅网络命令流
                var _recvCommandDisposable = _networkBroadcastService.CommandStream
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_command =>
                    {
                        NLogger.Info("收到远程命令: EventID={EventID}", _command.EventID);

                        // 提取发送方IP（用于ACK回复）
                        var senderIP = _command.Data?.Value<string>("requesterIP");

                        if (_command.EventID == Command.RESTART)
                        {
                            NLogger.Info("执行系统重启命令");
                            SendAck(senderIP, Command.RESTART);
                            ViewModel.RestartSystem.Execute().Subscribe();
                        }
                        else if (_command.EventID == Command.SHUTDOWN)
                        {
                            NLogger.Info("执行系统关机命令");
                            SendAck(senderIP, Command.SHUTDOWN);
                            ViewModel.ShutdownSystem.Execute().Subscribe();
                        }
                        else if (_command.EventID == Command.RESTART_NODE_TREE)
                        {
                            NLogger.Info("执行重启进程树命令");
                            SendAck(senderIP, Command.RESTART_NODE_TREE);
                            rootProcessNode.RunNode();
                        }
                        else if (_command.EventID == Command.STOP)
                        {
                            NLogger.Info("执行停止进程树命令");
                            SendAck(senderIP, Command.STOP);
                            rootProcessNode.KillNode();
                        }
                        else if (_command.EventID == Command.BOOT)
                        {
                            NLogger.Info("执行启动进程树命令");
                            SendAck(senderIP, Command.BOOT);
                            rootProcessNode.RunNode();
                        }
                        else if (_command.EventID == Command.EXPORT_PACKAGE)
                        {
                            NLogger.Info("执行远程导出进程包命令");
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                var incomingTaskId = _command.Data?.Value<string>("taskId") ?? "";
                                try
                                {
                                    var requesterIP_forProgress = _command.Data?.Value<string>(
                                        "requesterIP"
                                    );

                                    // 创建进度回调：通过UDP向请求方发送导出进度
                                    IProgress<string> statusProgress = null;
                                    if (!string.IsNullOrEmpty(requesterIP_forProgress))
                                    {
                                        var myIP_forProgress = GetLocalIPForRemote(
                                            requesterIP_forProgress
                                        );
                                        statusProgress = new Progress<string>(message =>
                                        {
                                            try
                                            {
                                                var progressCommand = new Command
                                                {
                                                    EventID = Command.EXPORT_PACKAGE_PROGRESS,
                                                    Data = new Newtonsoft.Json.Linq.JObject
                                                    {
                                                        ["message"] = message,
                                                        ["remoteIP"] = myIP_forProgress
                                                    }
                                                };
                                                var pJson = JsonConvert.SerializeObject(
                                                    progressCommand
                                                );
                                                var pData = System.Text.Encoding.UTF8.GetBytes(
                                                    pJson
                                                );
                                                using (var udp = new System.Net.Sockets.UdpClient())
                                                {
                                                    udp.Send(
                                                        pData,
                                                        pData.Length,
                                                        requesterIP_forProgress,
                                                        CommonVars.ControlPort
                                                    );
                                                }
                                            }
                                            catch
                                            { /* 进度通知失败不影响导出 */
                                            }
                                        });
                                    }

                                    var exportedFileName = await ExportPackageToSharedFolderAsync(
                                        rootProcessNode,
                                        statusProgress
                                    );

                                    // 导出完成后发送通知回请求方
                                    var requesterIP = _command.Data?.Value<string>("requesterIP");
                                    if (!string.IsNullOrEmpty(requesterIP))
                                    {
                                        NLogger.Info(
                                            $"[Remote Export] 向 {requesterIP} 发送导出完成通知, 文件名: {exportedFileName}"
                                        );

                                        // 获取本机IP（用于让请求方识别是谁发来的通知）
                                        var myIP = GetLocalIPForRemote(requesterIP);

                                        var completedCommand = new Command
                                        {
                                            EventID = Command.EXPORT_PACKAGE_COMPLETED,
                                            Data = new Newtonsoft.Json.Linq.JObject
                                            {
                                                ["success"] = !string.IsNullOrEmpty(
                                                    exportedFileName
                                                ),
                                                ["machineName"] = Environment.MachineName,
                                                ["remoteIP"] = myIP, // 附带本机IP
                                                ["packageFileName"] = exportedFileName ?? "", // 附带导出文件名
                                                ["taskId"] = incomingTaskId // 回传任务ID用于精确匹配TCS
                                            }
                                        };

                                        // 使用UDP发送通知
                                        var json = JsonConvert.SerializeObject(completedCommand);
                                        var data = System.Text.Encoding.UTF8.GetBytes(json);
                                        using (var udpClient = new System.Net.Sockets.UdpClient())
                                        {
                                            udpClient.Send(
                                                data,
                                                data.Length,
                                                requesterIP,
                                                CommonVars.ControlPort
                                            );
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error("远程导出进程包失败: {Message}", ex.Message);

                                    // 发送失败通知
                                    var requesterIP = _command.Data?.Value<string>("requesterIP");
                                    if (!string.IsNullOrEmpty(requesterIP))
                                    {
                                        var myIP = GetLocalIPForRemote(requesterIP);

                                        var completedCommand = new Command
                                        {
                                            EventID = Command.EXPORT_PACKAGE_COMPLETED,
                                            Data = new Newtonsoft.Json.Linq.JObject
                                            {
                                                ["success"] = false,
                                                ["error"] = ex.Message,
                                                ["machineName"] = Environment.MachineName,
                                                ["remoteIP"] = myIP,
                                                ["taskId"] = incomingTaskId // 回传任务ID
                                            }
                                        };

                                        // 使用UDP发送通知
                                        try
                                        {
                                            var json = JsonConvert.SerializeObject(
                                                completedCommand
                                            );
                                            var data = System.Text.Encoding.UTF8.GetBytes(json);
                                            using (
                                                var udpClient = new System.Net.Sockets.UdpClient()
                                            )
                                            {
                                                udpClient.Send(
                                                    data,
                                                    data.Length,
                                                    requesterIP,
                                                    CommonVars.ControlPort
                                                );
                                            }
                                        }
                                        catch (Exception sendEx)
                                        {
                                            NLogger.Warn(
                                                $"[Remote Export] 发送失败通知异常: {sendEx.Message}"
                                            );
                                        }
                                    }
                                }
                            });
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
                                    NLogger.Warn("未找到进程节点: {ProcessPath}", _processPath);
                                    return;
                                }

                                _processNode.NotifyHeartbeat();
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("处理心跳命令异常: {Message}", ex.Message);
                            }
                        }
                        else if (_command.EventID == Command.EXPORT_PACKAGE_PROGRESS)
                        {
                            // 导出进程包进度通知
                            try
                            {
                                var progressMsg = _command.Data?.Value<string>("message") ?? "";
                                var remoteIP = _command.Data?.Value<string>("remoteIP");

                                if (!string.IsNullOrEmpty(remoteIP) && _table?.ViewModel != null)
                                {
                                    _table.ViewModel.OnExportPackageProgress(remoteIP, progressMsg);
                                }
                            }
                            catch (Exception ex)
                            {
                                NLogger.Warn("处理导出进度通知异常: {Message}", ex.Message);
                            }
                        }
                        else if (_command.EventID == Command.EXPORT_PACKAGE_COMPLETED)
                        {
                            // 导出进程包完成通知
                            try
                            {
                                if (_command.Data == null)
                                {
                                    NLogger.Warn("导出完成通知缺少数据");
                                    return;
                                }

                                var success = _command.Data.Value<bool>("success");
                                var machineName =
                                    _command.Data.Value<string>("machineName") ?? "未知设备";
                                var error = _command.Data.Value<string>("error");
                                var remoteIP = _command.Data.Value<string>("remoteIP"); // 从命令数据中获取远程IP
                                var packageFileName = _command.Data.Value<string>(
                                    "packageFileName"
                                ); // 导出的文件名
                                var taskId = _command.Data.Value<string>("taskId"); // 任务ID（用于精确匹配TCS）

                                if (string.IsNullOrEmpty(remoteIP))
                                {
                                    NLogger.Warn("导出完成通知缺少remoteIP");
                                    return;
                                }

                                NLogger.Info(
                                    $"[Remote Export] 收到 {machineName}({remoteIP}) 的导出完成通知，成功: {success}, 文件: {packageFileName}"
                                );

                                // 通知 DaemonPanelViewModel
                                if (_table?.ViewModel != null)
                                {
                                    _table.ViewModel.OnExportPackageCompleted(
                                        remoteIP,
                                        success,
                                        error,
                                        packageFileName,
                                        taskId
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("处理导出完成通知异常: {Message}", ex.Message);
                            }
                        }
                        else if (_command.EventID == Command.PUSH_PACKAGE_TO_REQUESTER)
                        {
                            // 请求方通过UDP要求本机主动推送文件（避免TCP入站被防火墙阻断）
                            var transferService = _p2pService;
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var requesterIP = _command.Data?.Value<string>("requesterIP");
                                    var fileName = _command.Data?.Value<string>("fileName");
                                    var requesterPort =
                                        _command.Data?.Value<int>("requesterPort") ?? 7009;

                                    if (
                                        string.IsNullOrEmpty(requesterIP)
                                        || string.IsNullOrEmpty(fileName)
                                    )
                                    {
                                        NLogger.Warn("[PUSH] 推送请求缺少必要参数");
                                        return;
                                    }

                                    NLogger.Info(
                                        $"[PUSH] 收到推送请求: 文件={fileName}, 目标={requesterIP}:{requesterPort}"
                                    );

                                    var sharedDir = Utilities.AppPathes.SharedFilesDir;
                                    var filePath = System.IO.Path.Combine(sharedDir, fileName);

                                    if (!System.IO.File.Exists(filePath))
                                    {
                                        NLogger.Error("[PUSH] 要推送的文件不存在: {FilePath}", filePath);
                                        return;
                                    }

                                    // 创建目标机器信息
                                    var targetMachine = new MachineInfo
                                    {
                                        ID = requesterIP,
                                        IPs =
                                            new System.Collections.ObjectModel.ObservableCollection<string>
                                            {
                                                requesterIP
                                            }
                                    };

                                    // 主动推送文件到请求方
                                    if (transferService != null)
                                    {
                                        try
                                        {
                                            await transferService.SendFilesAsync(
                                                targetMachine,
                                                new List<string> { filePath },
                                                sourceHint: TransferTaskSource.PackageDownload
                                            );
                                            NLogger.Info(
                                                $"[PUSH] 文件推送完成: {fileName} -> {requesterIP}"
                                            );
                                        }
                                        catch (Exception ex)
                                        {
                                            NLogger.Error("[PUSH] 文件推送失败: {Message}", ex.Message);
                                        }
                                    }
                                    else
                                    {
                                        NLogger.Error("[PUSH] 无法获取传输服务实例");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error("[PUSH] 推送文件异常: {Message}", ex.Message);
                                }
                            });
                        }
                        // 文件列表请求/响应已迁移到 TCP 通道（P2PFileTransferService.HandleListFilesRequest）
                        else if (_command.EventID == Command.PUSH_DOWNLOAD_FILES)
                        {
                            // 远程设备要求本机主动推送指定文件（从文件浏览对话框发起的下载）
                            var transferService = _p2pService;
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var requesterIP = _command.Data?.Value<string>("requesterIP");
                                    var fileNamesToken = _command.Data?["fileNames"];
                                    var fileNames =
                                        fileNamesToken?.ToObject<string[]>()
                                        ?? Array.Empty<string>();

                                    if (string.IsNullOrEmpty(requesterIP) || fileNames.Length == 0)
                                    {
                                        NLogger.Warn("[PUSH_DOWNLOAD_FILES] 缺少必要参数");
                                        return;
                                    }

                                    NLogger.Info(
                                        $"[PUSH_DOWNLOAD_FILES] 收到文件推送请求: 目标={requesterIP}, 文件数={fileNames.Length}"
                                    );

                                    var sharedDir = Utilities.AppPathes.SharedFilesDir;
                                    var filePaths = new List<string>();

                                    foreach (var fileName in fileNames)
                                    {
                                        var filePath = System.IO.Path.Combine(sharedDir, fileName);
                                        if (System.IO.File.Exists(filePath))
                                        {
                                            filePaths.Add(filePath);
                                        }
                                        else
                                        {
                                            NLogger.Warn(
                                                $"[PUSH_DOWNLOAD_FILES] 文件不存在: {filePath}"
                                            );
                                        }
                                    }

                                    if (filePaths.Count == 0)
                                    {
                                        NLogger.Warn("[PUSH_DOWNLOAD_FILES] 没有有效的文件可推送");
                                        return;
                                    }

                                    // 创建目标机器信息
                                    var targetMachine = new MachineInfo
                                    {
                                        ID = requesterIP,
                                        IPs =
                                            new System.Collections.ObjectModel.ObservableCollection<string>
                                            {
                                                requesterIP
                                            }
                                    };

                                    if (transferService != null)
                                    {
                                        try
                                        {
                                            await transferService.SendFilesAsync(
                                                targetMachine,
                                                filePaths,
                                                sourceHint: TransferTaskSource.RemoteBrowseDownload
                                            );
                                            NLogger.Info(
                                                $"[PUSH_DOWNLOAD_FILES] 文件推送完成: {filePaths.Count} 个文件 -> {requesterIP}"
                                            );
                                        }
                                        catch (Exception ex)
                                        {
                                            NLogger.Error(
                                                $"[PUSH_DOWNLOAD_FILES] 文件推送失败: {ex.Message}"
                                            );
                                        }
                                    }
                                    else
                                    {
                                        NLogger.Error("[PUSH_DOWNLOAD_FILES] 无法获取传输服务实例");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error(
                                        "[PUSH_DOWNLOAD_FILES] 推送文件异常: {Message}",
                                        ex.Message
                                    );
                                }
                            });
                        }
                        // ── 音频控制 ────────────────────────────────────────────
                        else if (_command.EventID == Command.SET_VOLUME)
                        {
                            var volume = _command.Data?.Value<int>("volume") ?? -1;
                            if (volume >= 0 && volume <= 100)
                            {
                                NLogger.Info("执行远程设置音量命令: {Volume}%", volume);
                                SendAck(senderIP, Command.SET_VOLUME);
                                ViewModel.SystemVolume = volume;
                            }
                            else
                            {
                                NLogger.Warn("SET_VOLUME 参数无效: {Volume}", volume);
                            }
                        }
                        else if (_command.EventID == Command.MUTE)
                        {
                            NLogger.Info("执行远程静音命令");
                            SendAck(senderIP, Command.MUTE);
                            WinAPI.SetMute(true);
                            ViewModel.IsMuted = true;
                        }
                        else if (_command.EventID == Command.UNMUTE)
                        {
                            NLogger.Info("执行远程取消静音命令");
                            SendAck(senderIP, Command.UNMUTE);
                            WinAPI.SetMute(false);
                            ViewModel.IsMuted = false;
                        }
                        else if (_command.EventID == Command.TOGGLE_MUTE)
                        {
                            NLogger.Info("执行远程切换静音命令");
                            SendAck(senderIP, Command.TOGGLE_MUTE);
                            ViewModel.ToggleMute();
                        }
                        else if (_command.EventID == Command.VOLUME_UP)
                        {
                            NLogger.Info("执行远程音量步进增加命令");
                            SendAck(senderIP, Command.VOLUME_UP);
                            ViewModel.VolumeStepUp();
                        }
                        else if (_command.EventID == Command.VOLUME_DOWN)
                        {
                            NLogger.Info("执行远程音量步进减少命令");
                            SendAck(senderIP, Command.VOLUME_DOWN);
                            ViewModel.VolumeStepDown();
                        }
                        // ── 节能模式 ────────────────────────────────────────────
                        else if (_command.EventID == Command.ENTER_POWER_SAVING)
                        {
                            NLogger.Info("执行远程开启节能模式命令");
                            SendAck(senderIP, Command.ENTER_POWER_SAVING);
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var vm = _powerSavingService?.ViewModel;
                                    if (vm == null)
                                    {
                                        // 确保 PowerSavingService 已初始化
                                        Dispatcher.Invoke(() =>
                                        {
                                            _powerSavingService.GetOrCreateWindow(AppSettings);
                                            ViewModel.PowerSaving = _powerSavingService.ViewModel;
                                        });
                                        vm = _powerSavingService?.ViewModel;
                                    }
                                    if (vm != null)
                                    {
                                        var cmd = vm.GetType().GetProperty("ApplyPowerSavingCommand")
                                            ?.GetValue(vm) as ReactiveUI.ReactiveCommand<Unit, Unit>;
                                        if (cmd != null && await cmd.CanExecute.FirstAsync())
                                            await cmd.Execute();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error("远程开启节能模式失败: {Message}", ex.Message);
                                }
                            });
                        }
                        else if (_command.EventID == Command.EXIT_POWER_SAVING)
                        {
                            NLogger.Info("执行远程退出节能模式命令");
                            SendAck(senderIP, Command.EXIT_POWER_SAVING);
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var vm = _powerSavingService?.ViewModel;
                                    if (vm != null)
                                    {
                                        var cmd = vm.GetType().GetProperty("RestoreNormalCommand")
                                            ?.GetValue(vm) as ReactiveUI.ReactiveCommand<Unit, Unit>;
                                        if (cmd != null && await cmd.CanExecute.FirstAsync())
                                            await cmd.Execute();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error("远程退出节能模式失败: {Message}", ex.Message);
                                }
                            });
                        }
                        // ── 显示器控制 ──────────────────────────────────────────
                        else if (_command.EventID == Command.MONITOR_OFF)
                        {
                            NLogger.Info("执行远程关闭显示器命令");
                            SendAck(senderIP, Command.MONITOR_OFF);
                            WinAPI.TurnOffMonitor();
                        }
                        else if (_command.EventID == Command.MONITOR_ON)
                        {
                            NLogger.Info("执行远程唤醒显示器命令");
                            SendAck(senderIP, Command.MONITOR_ON);
                            WinAPI.WakeUpMonitor();
                        }
                        // ── 系统功能 ────────────────────────────────────────────
                        else if (_command.EventID == Command.TAKE_SCREENSHOT)
                        {
                            NLogger.Info("执行远程触发截图命令");
                            SendAck(senderIP, Command.TAKE_SCREENSHOT);
                            ViewModel.TakeScreenshot.Execute().Subscribe();
                        }
                        else if (_command.EventID == Command.DISABLE_DESKTOP)
                        {
                            NLogger.Info("执行远程关闭桌面进程命令");
                            SendAck(senderIP, Command.DISABLE_DESKTOP);
                            try
                            {
                                WinAPI.OpenProcess("taskkill.exe", "/f /im explorer.exe");
                                AppSettings.DisableExplorer = true;
                                saveConfig();
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("远程关闭桌面进程失败: {Message}", ex.Message);
                            }
                        }
                        else if (_command.EventID == Command.ENABLE_DESKTOP)
                        {
                            NLogger.Info("执行远程启用桌面进程命令");
                            SendAck(senderIP, Command.ENABLE_DESKTOP);
                            try
                            {
                                WinAPI.OpenProcess("explorer.exe", "");
                                AppSettings.DisableExplorer = false;
                                saveConfig();
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("远程启用桌面进程失败: {Message}", ex.Message);
                            }
                        }
                        else if (_command.EventID == Command.TOGGLE_TOUCH)
                        {
                            NLogger.Info("执行远程切换触摸屏命令");
                            SendAck(senderIP, Command.TOGGLE_TOUCH);
                            try
                            {
                                AppSettings.DisableTouchScreen = !AppSettings.DisableTouchScreen;
                                if (DeviceManager.SetTouchScreenEnabled(!AppSettings.DisableTouchScreen))
                                {
                                    NLogger.Info($"远程触摸屏已{(AppSettings.DisableTouchScreen ? "禁用" : "启用")}");
                                    saveConfig();
                                }
                                else
                                {
                                    AppSettings.DisableTouchScreen = !AppSettings.DisableTouchScreen;
                                    NLogger.Warn("远程切换触摸屏失败");
                                }
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("远程切换触摸屏异常: {Message}", ex.Message);
                            }
                        }
                        else
                        {
                            NLogger.Warn("收到未知命令: EventID={EventID}", _command.EventID);
                        }
                    });

                // UI 线程响应性看门狗：后台线程定期检查 Dispatcher 是否卡住
                var uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                NLogger.Debug("[看门狗] UI线程ID={ThreadId}", uiThreadId);
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // 先等3秒让启动完成
                    await System.Threading.Tasks.Task.Delay(3000);
                    bool dumpWritten = false;
                    for (int watchdogTick = 0; watchdogTick < 120; watchdogTick++) // 监控10分钟
                    {
                        var checkpoint = _uiCheckpoint;
                        var responded = false;
                        Dispatcher.InvokeAsync(() => { responded = true; },
                            System.Windows.Threading.DispatcherPriority.Send);
                        await System.Threading.Tasks.Task.Delay(2000); // 给 Dispatcher 2秒响应
                        if (!responded)
                        {
                            // 连续快速采样3次以精确定位
                            var cp1 = _uiCheckpoint;
                            await System.Threading.Tasks.Task.Delay(500);
                            var cp2 = _uiCheckpoint;
                            await System.Threading.Tasks.Task.Delay(500);
                            var cp3 = _uiCheckpoint;
                            NLogger.Warn(
                                "[看门狗] UI线程无响应！tick={Tick}, 检查点=[{CP0}]->[{CP1}]->[{CP2}]->[{CP3}]",
                                watchdogTick, checkpoint, cp1, cp2, cp3);

                            // 首次检测到卡死时，写一个 MiniDump 以便分析堆栈
                            if (!dumpWritten)
                            {
                                dumpWritten = true;
                                try
                                {
                                    var dumpPath = System.IO.Path.Combine(
                                        AppPathes.LogsDir,
                                        $"DaemonKit_hang_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");
                                    using var fs = new System.IO.FileStream(dumpPath,
                                        System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite);
                                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                                    MiniDumpWriteDump(
                                        proc.Handle, (uint)proc.Id,
                                        fs.SafeFileHandle.DangerousGetHandle(),
                                        2 /* MiniDumpWithFullMemory */,
                                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                                    NLogger.Warn("[看门狗] 已生成 dump 文件: {DumpPath}", dumpPath);
                                }
                                catch (Exception dumpEx)
                                {
                                    NLogger.Warn("[看门狗] 生成 dump 失败: {Msg}", dumpEx.Message);
                                }
                            }
                        }
                        await System.Threading.Tasks.Task.Delay(3000); // 两次检查间隔
                    }
                });

                this.Events()
                    .Closed.Subscribe(_ =>
                    {
                        _recvCommandDisposable.Dispose();
                        _networkBroadcastService?.Dispose();
                        _crashDetectionService?.Dispose();
                        _p2pService?.Dispose();
                        _transferTaskManager?.SaveHistory();
                        _transferTaskManager?.Dispose();

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

        #endregion

        #region 命令ACK

        /// <summary>
        /// 向发送方回复命令确认应答
        /// </summary>
        private void SendAck(string targetIP, int originalEventId)
        {
            if (string.IsNullOrEmpty(targetIP))
                return;

            try
            {
                var ackCommand = new Command
                {
                    EventID = Command.ACK,
                    Data = new Newtonsoft.Json.Linq.JObject
                    {
                        ["ackEvt"] = originalEventId,
                        ["machineName"] = Environment.MachineName,
                        ["timestamp"] = DateTime.Now.ToString("o")
                    }
                };
                var json = JsonConvert.SerializeObject(ackCommand);
                var data = System.Text.Encoding.UTF8.GetBytes(json);
                using var udpClient = new System.Net.Sockets.UdpClient();
                udpClient.Send(data, data.Length, targetIP, CommonVars.ControlPort);
            }
            catch (Exception ex)
            {
                NLogger.Warn("发送ACK失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region P/Invoke

        [System.Runtime.InteropServices.DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump",
            CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall, SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType,
            IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        #endregion
    }
}
