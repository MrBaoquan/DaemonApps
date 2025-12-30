using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reflection;
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
        #region Fields

        public static AppSettings AppSettings { get; set; }
        ProcessItem rootProcessNode = null!;

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
            ProcessNodeForm processNodeForm = new ProcessNodeForm();
            Settings settingsWindow = new Settings();
            Schedule scheduleWindow = new Schedule();

            var _table = new DaemonTable();

            this.WhenActivated(disposables =>
            {
                DataContext = this.ViewModel;

                Observable
                    .Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(200))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        // mainWindow.Log (
                        //     NLogger.FetchMessage ().Aggregate (string.Empty, (_current, _next) => _current + _next + "\r\n")
                        // );
                        var _logContent = NLogger
                            .FetchMessage()
                            .Aggregate(
                                string.Empty,
                                (_current, _next) => _current + _next + "\r\n"
                            );
                        var _lastContent = this.logBox.Text;

                        this.logBox.Text = _logContent;
                        if (_lastContent != _logContent)
                        {
                            this.logBox.ScrollToEnd();
                        }
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

                ViewModel.OpenSettings.Subscribe(_ =>
                {
                    settingsWindow.Show();
                    var helper = new WindowInteropHelper(settingsWindow);
                    ProcManager.KeepTopWindow(helper.Handle, 0, 0, 0, 0);
                    settingsWindow.ViewModel.SyncSettings(AppSettings);
                    settingsWindow.ViewModel.Confirm.Subscribe(_appSettings =>
                    {
                        AppSettings = _appSettings;
                        rootProcessNode.SyncSettings(AppSettings);
                        settingsWindow.Hide();
                        saveConfig();
                        Utils.SyncSettings();
                    });
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
                ViewModel.AddTreeNode.Subscribe(_ =>
                {
                    processNodeForm.VM.SyncCreateFormProperties();
                    processNodeForm.Show();
                });

                // 编辑进程结点
                ViewModel.EditTreeNode.Subscribe(_ =>
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
                ViewModel.EditSchedule.Subscribe(_ =>
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
                processNodeForm.VM.Confirm.Subscribe(_ =>
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

                ViewModel.OpenRemotePanel.Subscribe(_ =>
                {
                    _table.Show();
                });

                ViewModel.OpenScheduleWindow.Subscribe(_ =>
                {
                    scheduleWindow.Title = scheduleWindow.ViewModel!.SetEditingProcessItem(
                        rootProcessNode
                    );

                    scheduleWindow.Show();
                    // 窗口置于最前
                    var scheduleHelper = new WindowInteropHelper(scheduleWindow);
                    ProcManager.KeepTopWindow(scheduleHelper.Handle, 0, 0, 0, 0);
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

            var _appVersion = Assembly.GetExecutingAssembly().GetName().Version;
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

        #region Hardware Info

        static readonly HardwareInfo hardwareInfo = new HardwareInfo();

        /// <summary>
        /// 拉取硬件信息
        /// </summary>
        private void FetchHardwareInfo()
        {
            this.hardwareInfoBox.Text = "硬件信息玩命读取中...";

            Utils
                .FetchHardwareInfo()
                .Subscribe(_text =>
                {
                    hardwareInfoBox.Text = _text;
                });
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
            Utils.RegisterHotKey(this);
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
    }
}
