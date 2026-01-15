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

        // 导入导出全局互斥锁 - 确保同时只有一个在进行
        private static bool _isExporting = false;
        private static bool _isImporting = false;

        #endregion

        #region Initialization Methods

        /// <summary>
        /// 后台服务初始化（在窗口显示后异步执行）
        /// </summary>
        private async void InitializeBackgroundServices(DateTime startTime)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var bgStartTime = DateTime.Now;

                // 异步获取硬件信息
                Dispatcher.InvokeAsync(() => FetchHardwareInfo());

                NLogger.Info($"后台初始化开始，窗口显示耗时: {(bgStartTime - startTime).TotalMilliseconds:F0}ms");
            });

            // 初始化新的任务调度引擎（使用全局配置）
            var engineStartTime = DateTime.Now;
            _scheduleTaskEngine = new ScheduleTaskEngine(rootProcessNode, GlobalSchedule)
            {
                ConfirmHandler = ConfirmSchedulePowerActionAsync,
                PowerSavingViewModelProvider = () => _powerSavingService.ViewModel
            };
            _scheduleTaskEngine.TaskExecuting += (sender, context) =>
            {
                NLogger.Info($"执行任务: [{context.TaskConfig.Name}] - {context.TaskConfig.Action}");
            };
            _scheduleTaskEngine.TaskExecuted += (sender, context) =>
            {
                if (context.IsSuccess)
                {
                    NLogger.Info($"任务完成: {context.Result}");
                }
                else
                {
                    NLogger.Error($"任务失败: {context.ErrorMessage}");
                }
            };
            NLogger.Info($"任务引擎初始化耗时: {(DateTime.Now - engineStartTime).TotalMilliseconds:F0}ms");

            // 订阅全局计划任务启用状态变化，自动保存配置
            GlobalSchedule
                .WhenAnyValue(x => x.ScheduleTasksEnabled)
                .Skip(1) // 跳过初始值
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(enabled =>
                {
                    saveConfig();
                    NLogger.Info($"计划任务已{(enabled ? "启用" : "禁用")}");
                });

            // 检查是否首次启动并启动计划任务监控
            CheckFirstStartToday();
            StartScheduleTaskMonitor();

            NLogger.Info($"总启动耗时: {(DateTime.Now - startTime).TotalMilliseconds:F0}ms");
        }

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

            // 初始化服务层（在 AppSettings 加载后会调用 Initialize）
            _powerSavingService = new PowerSavingService();
            // _idleMonitorService 将在 loadConfig 后初始化

            // 订阅软件包操作进度消息
            ReactiveUI.MessageBus.Current
                .Listen<PackageProgressInfo>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnPackageProgressUpdate);

            this.WhenActivated(disposables =>
            {
                var startTime = DateTime.Now;
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

                // 硬件信息获取改为后台异步加载，不阻塞主窗口
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
                var configLoadTime = DateTime.Now;
                NLogger.Info($"配置加载耗时: {(configLoadTime - startTime).TotalMilliseconds:F0}ms");

                this.ProcessTree.Items.Add(rootProcessNode);

                // 延迟非关键初始化到窗口显示后执行
                this.Loaded += (s, e) => InitializeBackgroundServices(startTime);

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

                            // 同步设置到 PowerSavingService
                            _powerSavingService.EnableIdleAutoPowerSaving =
                                AppSettings.EnableIdleAutoPowerSaving;

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
                    });

                // 空闲监控已迁移到 IdleMonitorService，在 loadConfig 后启动

                // 网络广播和命令接收已迁移到 NetworkBroadcastService
                _networkBroadcastService = new NetworkBroadcastService();
                _networkBroadcastService.Start(rootProcessNode, AppSettings.DaemonInterval);

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
                        _networkBroadcastService?.Dispose();
                        _crashDetectionService?.Dispose();

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

                // 统计 System 和 Tool 类别的项数，用于添加分隔线
                int systemItemCount = _extConfig.Extensions.Count(e => e.Group == "System");
                int systemBasicCount = 2; // 前两项：控制面板、任务管理器
                bool systemSeparatorAdded = false;

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
                            // 在基础系统工具和高级设置项之间添加分隔线
                            if (
                                _sysMgrMenu.Items.Count == systemBasicCount
                                && !systemSeparatorAdded
                                && systemItemCount > systemBasicCount
                            )
                            {
                                _sysMgrMenu.Items.Add(new Separator());
                                systemSeparatorAdded = true;
                            }
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

            // 将 rootProcessNode 传递给 ViewModel 以便 XAML 绑定
            ViewModel.RootProcessNode = rootProcessNode;

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

            // 加载全局计划任务配置
            if (!System.IO.File.Exists(AppPathes.GlobalSchedulePath))
            {
                // 首次运行或升级，从 rootProcessNode 迁移数据
                NLogger.Info("未找到全局计划任务配置，尝试迁移旧数据...");
                GlobalSchedule = MigrateScheduleTasksToGlobal(rootProcessNode);
                USerialization.SerializeXML(GlobalSchedule, AppPathes.GlobalSchedulePath);
                NLogger.Info($"已迁移 {GlobalSchedule.ScheduleTasks.Count} 个计划任务到全局配置");
            }
            else
            {
                if (
                    System.IO.File.ReadAllText(AppPathes.GlobalSchedulePath).Length == 0
                    && System.IO.File.Exists(AppPathes.GlobalSchedulePath_Backup)
                )
                {
                    System.IO.File.Copy(
                        AppPathes.GlobalSchedulePath_Backup,
                        AppPathes.GlobalSchedulePath,
                        true
                    );
                }
                GlobalSchedule = USerialization.DeserializeXML<GlobalScheduleConfig>(
                    AppPathes.GlobalSchedulePath
                );
            }

            // 验证全局配置
            if (!GlobalSchedule.Validate(out string validationError))
            {
                NLogger.Warn($"全局计划任务配置验证失败: {validationError}");
            }

            // 将全局配置传递给 ViewModel
            ViewModel.GlobalSchedule = GlobalSchedule;

            Utils.SyncSettings();
            rootProcessNode.SyncSettings(AppSettings);

            // 根据配置决定是否注册全局快捷键
            if (AppSettings.EnableGlobalHotKey)
            {
                Utils.RegisterHotKey(this, AppSettings);
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

            // 初始化服务层（确保 AppSettings 已加载）
            _powerSavingService.Initialize(AppSettings);
            _idleMonitorService = new IdleMonitorService(_powerSavingService, AppSettings);
            _idleMonitorService.StartMonitoring();

            if (ViewModel != null)
            {
                ViewModel.PowerSaving = _powerSavingService.ViewModel;
            }

            NLogger.Info("服务层已初始化");
        }

        // 数据持久化
        private void saveConfig()
        {
            try
            {
                // 尝试保存配置，如果文件被锁定则重试
                SaveConfigWithRetry(() =>
                {
                    USerialization.SerializeXML(rootProcessNode, AppPathes.TreeViewDataPath);
                    USerialization.SerializeXML(AppSettings, AppPathes.AppSettingPath);
                    USerialization.SerializeXML(GlobalSchedule, AppPathes.GlobalSchedulePath);
                });

                if (!Directory.Exists(AppPathes.ConfigDir_BackUp))
                {
                    Directory.CreateDirectory(AppPathes.ConfigDir_BackUp);
                    WinAPI.OpenProcess("attrib.exe", $"+h {AppPathes.ConfigDir_BackUp}");
                }

                // 备份配置文件（只备份成功保存的文件）
                try
                {
                    System.IO.File.Copy(
                        AppPathes.TreeViewDataPath,
                        AppPathes.TreeViewDataPath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn($"备份 TreeView 配置失败: {ex.Message}");
                }

                try
                {
                    System.IO.File.Copy(
                        AppPathes.ExtensionConfigPath,
                        AppPathes.ExtensionConfigPath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn($"备份扩展配置失败: {ex.Message}");
                }

                try
                {
                    System.IO.File.Copy(
                        AppPathes.AppSettingPath,
                        AppPathes.AppSettingPath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn($"备份应用设置失败: {ex.Message}");
                }

                try
                {
                    System.IO.File.Copy(
                        AppPathes.GlobalSchedulePath,
                        AppPathes.GlobalSchedulePath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn($"备份全局计划失败: {ex.Message}");
                }

                NLogger.Info("配置文件保存成功.");
            }
            catch (Exception ex)
            {
                NLogger.Error($"保存配置文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 只保存 AppSettings，不保存其他配置（用于节能模式配置频繁更新的场景）
        /// </summary>
        private void SaveAppSettingsOnly()
        {
            try
            {
                SaveConfigWithRetry(() =>
                {
                    USerialization.SerializeXML(AppSettings, AppPathes.AppSettingPath);
                });

                // 备份应用设置
                try
                {
                    if (!Directory.Exists(AppPathes.ConfigDir_BackUp))
                    {
                        Directory.CreateDirectory(AppPathes.ConfigDir_BackUp);
                    }
                    System.IO.File.Copy(
                        AppPathes.AppSettingPath,
                        AppPathes.AppSettingPath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn($"备份应用设置失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"保存应用设置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 带重试机制的配置保存
        /// </summary>
        private void SaveConfigWithRetry(
            System.Action saveAction,
            int maxRetries = 3,
            int delayMs = 50
        )
        {
            Exception lastException = null;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    saveAction();
                    return; // 保存成功，直接返回
                }
                catch (System.IO.IOException ex) when (ex.HResult == -2147024864) // 0x80070020: File is in use
                {
                    lastException = ex;
                    if (i < maxRetries - 1)
                    {
                        // 等待后重试
                        System.Threading.Thread.Sleep(delayMs);
                    }
                }
            }

            // 所有重试都失败了，抛出最后一个异常
            if (lastException != null)
            {
                throw lastException;
            }
        }

        /// <summary>
        /// 迁移旧的计划任务数据到全局配置
        /// 从进程树的所有节点收集任务，合并到全局配置中
        /// </summary>
        private GlobalScheduleConfig MigrateScheduleTasksToGlobal(ProcessItem rootNode)
        {
            var globalConfig = GlobalScheduleConfig.CreateDefault();

            // 保留根节点的启用状态
            globalConfig.ScheduleTasksEnabled = rootNode.ScheduleTasksEnabled;

            // 递归收集所有节点的任务
            CollectTasksFromNode(rootNode, globalConfig.ScheduleTasks, rootNode);

            return globalConfig;
        }

        /// <summary>
        /// 递归收集节点的计划任务
        /// </summary>
        private void CollectTasksFromNode(
            ProcessItem node,
            List<ScheduleTaskConfig> globalTasks,
            ProcessItem rootNode
        )
        {
            if (node.ScheduleTaskConfigs != null && node.ScheduleTaskConfigs.Count > 0)
            {
                foreach (var task in node.ScheduleTaskConfigs)
                {
                    var migratedTask = task.Clone();

                    // 设置目标节点信息（对于节点级操作）
                    if (migratedTask.IsNodeLevelAction())
                    {
                        migratedTask.TargetNodeId = node.Name; // 使用Name作为标识
                        migratedTask.TargetNodeName = node.Name;
                    }

                    globalTasks.Add(migratedTask);
                }
            }

            // 递归处理子节点
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CollectTasksFromNode(child, globalTasks, rootNode);
                }
            }
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
                    var hotkeyId = wParam.ToInt32();

                    if (hotkeyId == 88)
                    {
                        handled = true;
                        ViewModel.Quit.Execute().Subscribe();
                    }
                    if (hotkeyId == 99)
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
                    else if (hotkeyId == HOTKEY_ID)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableScreenshot)
                        {
                            break;
                        }
                        // Alt+X 快捷键被按下，触发截图
                        handled = true;
                        Observable
                            .Return(Unit.Default)
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ => TriggerScreenshot());
                    }
                    else if (hotkeyId == 9001)
                    { //Alt+C
                        if (!AppSettings.EnableGlobalHotKey)
                        {
                            break;
                        }
                        // Alt+C 快捷键被按下，触发拾色
                        handled = true;
                        Observable
                            .Return(Unit.Default)
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ => ViewModel.PickColor.Execute().Subscribe());
                    }
                    else if (hotkeyId == 100)
                    { //Ctrl+D
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableToggleWindow)
                        {
                            break;
                        }
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
                    else if (hotkeyId == 101)
                    { //Ctrl+R
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableStartTree)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunNodeTree.Execute().Subscribe();
                    }
                    else if (hotkeyId == 102)
                    { //Ctrl+W
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableStopTree)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.KillNodeTree.Execute().Subscribe();
                    }
                    else if (hotkeyId == 103)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableDesktopOn)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.OpenFileExplorer_args).Subscribe();
                    }
                    else if (hotkeyId == 104)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableDesktopOff)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.KillFileExplorer_args).Subscribe();
                    }
                    else if (hotkeyId == 105)
                    {
                        if (
                            !AppSettings.EnableGlobalHotKey
                            || !AppSettings.EnableScheduleToggleHotKey
                        )
                        {
                            break;
                        }

                        handled = true;
                        GlobalSchedule.ScheduleTasksEnabled = !GlobalSchedule.ScheduleTasksEnabled;
                        saveConfig();
                        NLogger.Info(
                            $"全局计划任务已{(GlobalSchedule.ScheduleTasksEnabled ? "启用" : "禁用")}（快捷键切换）"
                        );
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
            NLogger.Info($"程序启动时间: {appStartTime}, 是否当日首次启动: {isFirstStartToday}");
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
        /// 执行关���任务
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
            // 调试模式 - 暂时注释真正的关机命令
            //Process.Start("shutdown", "/s /t 0");
            NLogger.Info("系统关机命令已确认（调试模式，未真正执行）");
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
            // 调试模式 - 暂时注释真正的重启命令
            //Process.Start("shutdown", "/r /t 0");
            NLogger.Info("系统重启命令已确认（调试模式，未真正执行）");
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

        private async System.Threading.Tasks.Task<bool> ConfirmSchedulePowerActionAsync(
            Models.ScheduleTaskAction action
        )
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
                NLogger.Error($"程序重启失败: {ex.Message}");
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
                    NLogger.Info($"截图保存: {overlay.Result}");
                }
                this.WindowState = System.Windows.WindowState.Minimized;
            }
            catch (Exception ex)
            {
                NLogger.Error($"截图失败: {ex.Message}");
            }
        }

        private void OpenScreenshotFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var screenshotPath = Path.Combine(AppPathes.AppRoot, "Screenshots");
                if (!Directory.Exists(screenshotPath))
                {
                    Directory.CreateDirectory(screenshotPath);
                }
                WinAPI.OpenProcess("explorer.exe", screenshotPath);
                NLogger.Info($"打开截图文件夹: {screenshotPath}");
            }
            catch (Exception ex)
            {
                NLogger.Error($"打开截图文件夹失败: {ex.Message}");
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
                NLogger.Error($"打开导出对话框失败: {ex.Message}");
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

                // 设置导入标志
                _isImporting = true;

                var importDialog = new ImportDialog { Owner = this };
                importDialog.ShowDialog();

                // 导入完成，重置标志
                _isImporting = false;

                if (importDialog.DialogResult == true)
                {
                    // 重新加载进程树
                    try
                    {
                        loadConfig();
                        NLogger.Info("导入后已重新加载进程树配置");
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error($"导入后重新加载配置失败: {ex.Message}");
                        MessageBox.Show(
                            $"重新加载配置失败：{ex.Message}",
                            "错误",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _isImporting = false;
                NLogger.Error($"打开导入对话框失败: {ex.Message}");
                MessageBox.Show(
                    $"打开导入对话框失败：{ex.Message}",
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
                if (System.Windows.PresentationSource.FromVisual(_currentPackageDialog) == null)
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

        #endregion
    }
}
