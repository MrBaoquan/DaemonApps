using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
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
using IWshRuntimeLibrary;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Newtonsoft.Json;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;

namespace DaemonKit {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<MainViewModel> {

        public static AppSettings AppSettings { get; set; }
        ProcessItem rootProcessNode = null;

        public MainWindow () {
            InitializeComponent ();

            ViewModel = new MainViewModel ();

            NLogger.LogFileDir = "Logs";
            NLogger.Initialize ();

            // 节点编辑窗口
            ProcessNodeForm processNodeForm = new ProcessNodeForm ();
            Settings settingsWindow = new Settings ();

            var _table = new DaemonTable ();

            this.WhenActivated (disposables => {
                DataContext = this.ViewModel;

                Observable.Timer (TimeSpan.Zero, TimeSpan.FromMilliseconds (200))
                    .ObserveOn (RxApp.MainThreadScheduler)
                    .Subscribe (_ => {
                        // mainWindow.Log (
                        //     NLogger.FetchMessage ().Aggregate (string.Empty, (_current, _next) => _current + _next + "\r\n")
                        // );
                        var _logContent = NLogger.FetchMessage ().Aggregate (string.Empty, (_current, _next) => _current + _next + "\r\n");
                        var _lastContent = this.logBox.Text;

                        this.logBox.Text = _logContent;
                        if (_lastContent != _logContent) {
                            this.logBox.ScrollToEnd ();
                        }
                    });

                NLogger.Info ("DaemonKit 已启动");
                this.executeProgramsBeforeStart ();

                FetchHardwareInfo ();
                this.hardwareInfoBox.Events ().MouseDoubleClick.Subscribe (_contentLoaded => {
                    FetchHardwareInfo ();
                });

                this.ProcessTree.DataContext = this.DataContext;
                NLogger.Info ("加载进程树..");
                loadExtensions ();
                loadConfig ();
                this.ProcessTree.Items.Add (rootProcessNode);

                ProcessItem _selectedTreeNode = rootProcessNode;

                // 选中进程树某个节点
                this.ProcessTree.Events ().SelectedItemChanged.Subscribe (_ => {
                    _selectedTreeNode = _.NewValue as ProcessItem;
                });

                // 右键点击进程结点进行选择
                this.ProcessTree.Events ().PreviewMouseRightButtonDown.Subscribe (_ => {
                    var _source = _.OriginalSource as DependencyObject;
                    while (_source != null && !(_source is TreeViewItem))
                        _source = VisualTreeHelper.GetParent (_source);
                    var _treeItem = _source as TreeViewItem;
                    if (_treeItem != null) {
                        _treeItem.Focus ();
                        _.Handled = true;
                    }
                });

                ViewModel.OpenSettings.Subscribe (_ => {
                    settingsWindow.Show ();
                    settingsWindow.ViewModel.SyncSettings (AppSettings);
                    settingsWindow.ViewModel.Confirm.Subscribe (_appSettings => {
                        AppSettings = _appSettings;
                        rootProcessNode.SyncSettings (AppSettings);
                        settingsWindow.Hide ();
                        saveConfig ();
                        syncSettings ();

                    });
                });

                ViewModel.ToggleEnable.Subscribe (_item => {
                    _item.SyncEnable ();
                    saveConfig ();
                });

                ViewModel.EnableNameInput.Subscribe (_item => {
                    _item.EnableNameInput ();
                });

                ViewModel.ShowInExplorer.Subscribe (_ => {
                    WinAPI.OpenProcess ("explorer.exe", " /select," + _selectedTreeNode.NodePath);
                });

                // 添加进程结点
                ViewModel.AddTreeNode.Subscribe (_ => {
                    processNodeForm.VM.SyncCreateFormProperties ();
                    processNodeForm.Show ();
                });

                // 编辑进程结点
                ViewModel.EditTreeNode.Subscribe (_ => {
                    processNodeForm.VM.SyncEditFormProperties (_selectedTreeNode.MetaData);
                    processNodeForm.Show ();
                });

                // 删除进程结点
                ViewModel.DeleteTreeNode.Subscribe (_ => {
                    _selectedTreeNode.Parent.RemoveChild (_selectedTreeNode);
                    saveConfig ();
                });

                ViewModel.ConfirmNameInput.Subscribe (_ => {
                    _.ConfirmNameInput ();
                    saveConfig ();
                });

                // 进程表单提交
                processNodeForm.VM.Confirm.Subscribe (_ => {
                    if (processNodeForm.VM.IsCreateMode) {
                    var _item = new ProcessItem {
                    MetaData = _
                        };
                        _selectedTreeNode.AddChild (_item);
                        _item.SyncSettings (AppSettings);
                    } else {
                        _.Enable = _selectedTreeNode.Enable;
                        _selectedTreeNode.MetaData = _;
                    }
                    processNodeForm.Hide ();

                    saveConfig ();
                });

                ViewModel.ShowAppDirectory.Subscribe (_ => {
                    WinAPI.OpenProcess ("explorer.exe", AppPathes.AppRoot);
                });

                ViewModel.SMBShare.Subscribe (_ => {
                    WinAPI.OpenProcess (Path.Combine (AppPathes.ExtensionPath, "SMBShare.bat"), "", true);
                });

                ViewModel.SMBUnshare.Subscribe (_ => {
                    WinAPI.OpenProcess (Path.Combine (AppPathes.ExtensionPath, "SMBUnshare.bat"), "", true);
                });

                ViewModel.OpenRemotePanel.Subscribe (_ => {
                    _table.Show ();
                });

                ViewModel.RunNodeTree.Subscribe (_ => {
                    rootProcessNode.RunNode ();
                });

                ViewModel.KillNodeTree.Subscribe (_ => {
                    NLogger.Info ("终止进程树..");
                    rootProcessNode.KillNode ();
                });

                ViewModel.RunProcess.Subscribe (_ => {
                    WinAPI.OpenProcess (_.Path, _.Arguments, _.RunAs);
                });
                // 进程根节点启动守护
                rootProcessNode.RunNode ();

                this.Events ().Closed.Subscribe (_ => { });

                // 广播设备信息
                UdpClient _metaDataClient = new UdpClient ();
                MachineInfo _machineInfo = new MachineInfo ();
                _metaDataClient.EnableBroadcast = true;

                Observable.Timer (TimeSpan.FromMilliseconds (AppSettings.DaemonInterval), TimeSpan.FromSeconds (3))
                    .Subscribe (_ => {
                        _machineInfo.Name = rootProcessNode.Name;
                        var _ipList = HardwareInfo.GetLocalIPv4Addresses ().Aggregate (string.Empty, (_cur, _next) => _cur + _next);
                        _machineInfo.IPs = new System.Collections.ObjectModel.ObservableCollection<string> (HardwareInfo.GetLocalIPv4Addresses ().Select (_ipAddress => _ipAddress.ToString ()));
                        _machineInfo.CPUs = new System.Collections.ObjectModel.ObservableCollection<string> (hardwareInfo.CpuList.Select (_cpu => _cpu.Name));
                        _machineInfo.GPUs = new System.Collections.ObjectModel.ObservableCollection<string> (hardwareInfo.VideoControllerList.Select (_ => _.Name));
                        _machineInfo.Memories = new System.Collections.ObjectModel.ObservableCollection<string> (hardwareInfo.MemoryList.Select (_ => _.Manufacturer + _.PartNumber + _.Capacity.FormatBytes ()));
                        var _data = Encoding.UTF8.GetBytes (JsonConvert.SerializeObject (_machineInfo));
                        _metaDataClient.Send (_data, _data.Count (), new System.Net.IPEndPoint (System.Net.IPAddress.Broadcast, 7007));

                        // crash进程检测
                        if (AppSettings.CrashWindows != null) {

                            var _crashWindows = AppSettings.CrashWindows.Split ("|")
                                .Select (_crashWindow => WinAPI.FindProcess (_crashWindow))
                                .Where (_process => _process != default (Process)).ToList ();

                            if (rootProcessNode.IsRuning && _crashWindows.Count > 0) {
                                _crashWindows.ForEach (_crashWindow => {
                                    _crashWindow.Kill ();
                                });
                                NLogger.Info ("检测到崩溃进程，尝试重启..");
                                rootProcessNode.KillNode ();
                                rootProcessNode.RunNode ();
                            }
                        }

                    });

                // 命令控制
                var _recvCommandDisposable = onRecvCommand ()
                    .Subscribe (_command => {
                        if (_command.ID == Command.RESTART) {
                            WinAPI.OpenProcess ("shutdown.exe", "/r /t 0");
                        } else if (_command.ID == Command.SHUTDOWN) {
                            WinAPI.OpenProcess ("shutdown.exe", "/s /t 0");
                        } else if (_command.ID == Command.RESTART_NODE_TREE) {
                            rootProcessNode.RunNode ();
                        }
                    });

                this.Events ().Closing.Subscribe (_ => {
                    _metaDataClient.Close ();
                    _metaDataClient.Dispose ();

                    _recvCommandDisposable.Dispose ();
                });

                //PowerShell ps = PowerShell.Create ();
                //var _script = "" +
                //    "";
                //ps.AddCommand ("Get-Process").AddParameter ("Name", "PowerShell");
                //ps.AddStatement ().AddCommand ("Get-Service");
                //ps.Invoke ();

            });

            this.Title = $"软件运维中心 v{Assembly.GetExecutingAssembly().GetName().Version}";

            InputBindings.Add (new KeyBinding { Command = ViewModel.ShowAppDirectory, Key = Key.D1, Modifiers = ModifierKeys.Control });
            InputBindings.Add (new KeyBinding { Command = ViewModel.RunProcess, Key = Key.D2, Modifiers = ModifierKeys.Control, CommandParameter = ViewModel.OpenFileExplorer_args });

            InputBindings.Add (new KeyBinding { Command = ViewModel.RunProcess, Key = Key.T, Modifiers = ModifierKeys.Control, CommandParameter = ViewModel.OpenCMD_args });
            InputBindings.Add (new KeyBinding { Command = ViewModel.RunProcess, Key = Key.P, Modifiers = ModifierKeys.Control, CommandParameter = ViewModel.OpenPowerShell_args });
        }

        private IObservable<Command> onRecvCommand () {
            return Observable.Create<Command> (_observer => {
                CancellationTokenSource _cts = new CancellationTokenSource ();
                UdpClient _commandClient = new UdpClient (7008);
                Observable.Start (async () => {
                    while (!_cts.IsCancellationRequested) {
                        var _command = await _commandClient.ReceiveAsync ();
                        var _commandStr = Encoding.UTF8.GetString (_command.Buffer);
                        _observer.OnNext (JsonConvert.DeserializeObject<Command> (_commandStr));
                    }
                    _observer.OnCompleted ();
                    _commandClient.Close ();
                    _commandClient.Dispose ();
                });
                return _cts;
            });
        }

        static readonly HardwareInfo hardwareInfo = new HardwareInfo ();

        /// <summary>
        /// 拉取硬件信息
        /// </summary>
        private void FetchHardwareInfo () {
            this.hardwareInfoBox.Text = "硬件信息玩命读取中...";

            Observable.Start<string> (() => {
                hardwareInfo.RefreshCPUList ();
                hardwareInfo.RefreshVideoControllerList ();
                hardwareInfo.RefreshMemoryList ();
                hardwareInfo.RefreshNetworkAdapterList ();
                hardwareInfo.RefreshMonitorList ();
                hardwareInfo.RefreshBIOSList ();
                hardwareInfo.RefreshMotherboardList ();
                var _description = HardwareInfo.GetLocalIPv4Addresses ().Aggregate ("IPv4地址:" + "\r\n", (_current, _next) => { return _current + _next + "\r\n"; });
                _description = hardwareInfo.CpuList.Aggregate (_description + "\r\nCPU:\r\n", (_current, _next) => { return _current + _next.Name; });
                _description = hardwareInfo.VideoControllerList.Aggregate (_description + "\r\n\r\nGPU:\r\n", (_current, _next) => { return _current + _next.Name; });
                _description = hardwareInfo.MemoryList.Aggregate (_description + "\r\n\r\n内存:\r\n", (_current, _next) => {
                    return _current +
                        string.Format ("{0}-{1}({2})", _next.Manufacturer, _next.PartNumber, _next.Capacity.FormatBytes ()) + "\r\n";
                });
                _description = hardwareInfo.MonitorList.Aggregate (_description + "\r\n显示器:\r\n", (_current, _next) => { return _current + _next.Name + "\r\n"; });
                _description = hardwareInfo.BiosList.Aggregate (_description + "\r\nBIOS:\r\n", (_current, _next) => { return _current + _next.Manufacturer + " " + _next.Version + "\r\n"; });
                _description = hardwareInfo.MotherboardList.Aggregate (_description + "\r\n主板:\r\n", (_current, _next) => { return _current + _next.Manufacturer + " " + _next.Product + "\r\n"; });
                return _description;
            }).ObserveOn (RxApp.MainThreadScheduler).Subscribe (_description => {
                hardwareInfoBox.Text = _description;
            }, _ => {
                hardwareInfoBox.Text = "硬件信息获取失败！";
            });

        }

        /// <summary>
        /// 加载拓展菜单
        /// </summary>
        private void loadExtensions () {

            if (!System.IO.File.Exists (AppPathes.ExtensionConfigPath)) {
                USerialization.SerializeXML (new ExtensionConfig (), AppPathes.ExtensionConfigPath);
            };

            try {
                var _extConfig = USerialization.DeserializeXML<ExtensionConfig> (AppPathes.ExtensionConfigPath);
                var _cusMenu = new MenuItem { Header = _extConfig.Name };

                _extConfig.Extensions.WithIndex ().ToList ().ForEach (_extention => {
                    var _menuItem = new MenuItem { Header = _extention.item.Name };

                    Action < (Extension item, int index) > _handleMenuClick = (_ext) => {
                        var _extensionPath = Path.Combine (AppPathes.ExtensionPath, _ext.item.Path);
                        if (!Path.IsPathRooted (_ext.item.Path) && System.IO.File.Exists (_extensionPath)) {
                            WinAPI.OpenProcess (_extensionPath, _ext.item.Args, _ext.item.RunAs);
                        } else {
                            WinAPI.OpenProcess (_ext.item.Path, _ext.item.Args, _ext.item.RunAs);
                        }
                    };

                    _menuItem.Events ().Click.Subscribe (_ => {
                        _handleMenuClick (_extention);
                    });

                    var _menuCommand = ReactiveCommand.Create < (Extension item, int index),
                        (Extension item, int index) > (_param => _param);
                    _menuCommand.Subscribe (_ext => {
                        _handleMenuClick (_ext);
                    });

                    _menuItem.InputGestureText = string.Format ("Ctrl+F{0}", _extention.index + 1);
                    InputBindings.Add (new KeyBinding { Command = _menuCommand, Key = Key.F1 + _extention.index, Modifiers = ModifierKeys.Control, CommandParameter = _extention });

                    _cusMenu.Items.Add (_menuItem);
                });

                this.MainMenu.Items.Insert (1, _cusMenu);
            } catch (System.Exception) { }
        }

        /// <summary>
        /// 加载配置文件
        /// </summary>
        private void loadConfig () {
            if (!System.IO.File.Exists (AppPathes.TreeViewDataPath)) {
                if (!Directory.Exists (Path.GetDirectoryName (AppPathes.TreeViewDataPath)))
                    Directory.CreateDirectory (Path.GetDirectoryName (AppPathes.TreeViewDataPath));
                rootProcessNode = new ProcessItem { MetaData = new ProcessMetaData { Name = "[ 进程树 ]", Delay = 0, Path = string.Empty } };
                USerialization.SerializeXML (rootProcessNode, AppPathes.TreeViewDataPath);
            }
            if (System.IO.File.ReadAllText (AppPathes.TreeViewDataPath).Length == 0 && System.IO.File.Exists (AppPathes.TreeViewDataPath_Backup)) {
                System.IO.File.Copy (AppPathes.TreeViewDataPath_Backup, AppPathes.TreeViewDataPath, true);
            }
            rootProcessNode = USerialization.DeserializeXML<ProcessItem> (AppPathes.TreeViewDataPath);
            rootProcessNode.SyncRelationships ();

            if (!System.IO.File.Exists (AppPathes.AppSettingPath)) {
                USerialization.SerializeXML (new AppSettings { StartUp = true, ShortCut = true, DelayDaemon = 500, DaemonInterval = 5000, ErrorCount = 1, CrashWindows = string.Empty }, AppPathes.AppSettingPath);
            }
            if (System.IO.File.ReadAllText (AppPathes.AppSettingPath).Length == 0 && System.IO.File.Exists (AppPathes.AppSettingPath_Backup)) {
                System.IO.File.Copy (AppPathes.AppSettingPath_Backup, AppPathes.AppSettingPath, true);
            }
            AppSettings = USerialization.DeserializeXML<AppSettings> (AppPathes.AppSettingPath);

            syncSettings ();
            rootProcessNode.SyncSettings (AppSettings);
        }

        // 数据持久化
        private void saveConfig () {
            USerialization.SerializeXML (rootProcessNode, AppPathes.TreeViewDataPath);
            USerialization.SerializeXML (AppSettings, AppPathes.AppSettingPath);
            if (!Directory.Exists (Path.GetDirectoryName (AppPathes.TreeViewDataPath_Backup))) {
                Directory.CreateDirectory (Path.GetDirectoryName (AppPathes.TreeViewDataPath_Backup));
            }
            // 备份配置文件
            System.IO.File.Copy (AppPathes.TreeViewDataPath, AppPathes.TreeViewDataPath_Backup, true);
            System.IO.File.Copy (AppPathes.ExtensionConfigPath, AppPathes.ExtensionConfigPath_Backup, true);
            System.IO.File.Copy (AppPathes.AppSettingPath, AppPathes.AppSettingPath_Backup, true);
        }

        private HwndSource _source;
        protected override void OnSourceInitialized (EventArgs e) {
            base.OnSourceInitialized (e);
            var helper = new WindowInteropHelper (this);
            _source = HwndSource.FromHwnd (helper.Handle);
            _source.AddHook (HwndHook);
            RegisterHotKey ();
        }

        protected override void OnClosing (CancelEventArgs e) {
            rootProcessNode.KillNode ();
            UnRegisterHotKey ();
            base.OnClosing (e);
        }

        private void RegisterHotKey () {
            var helper = new WindowInteropHelper (this);
            WinAPI.RegisterHotKey (helper.Handle, 100, (uint) KeyModifiers.Ctrl, 0x44);
            WinAPI.RegisterHotKey (helper.Handle, 101, (uint) KeyModifiers.Ctrl, 0x52);
            WinAPI.RegisterHotKey (helper.Handle, 102, (uint) KeyModifiers.Ctrl, 0x57);
        }

        private void UnRegisterHotKey () {
            var helper = new WindowInteropHelper (this);
            WinAPI.UnregisterHotKey (helper.Handle, 100);
            WinAPI.UnregisterHotKey (helper.Handle, 101);
            WinAPI.UnregisterHotKey (helper.Handle, 102);
        }

        private IntPtr HwndHook (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            const int WM_HOTKEY = 0x0312;
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            switch (msg) {
                case WM_HOTKEY:
                    if (wParam.ToInt32 () == 100) {
                        var helper = new WindowInteropHelper (this);
                        WinAPI.SetWindowPos (helper.Handle, (int) HWndInsertAfter.HWND_TOPMOST,
                            0, 0, 0, 0,
                            SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_FRAMECHANGED);
                        WinAPI.ShowWindow (helper.Handle, (int) CMDShow.SW_SHOWNORMAL);
                    } else if (wParam.ToInt32 () == 101) {
                        ViewModel.RunNodeTree.Execute ().Subscribe ();
                    } else if (wParam.ToInt32 () == 102) {
                        ViewModel.KillNodeTree.Execute ().Subscribe ();
                    }
                    break;
                case WM_QUERYENDSESSION:
                    break;
                case WM_ENDSESSION:
                    break;
            }
            return IntPtr.Zero;
        }

        static RegistryKey runKey = Registry.CurrentUser.OpenSubKey (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        const string appKey = "DaemonKit";
        private void syncSettings () {
            if (AppSettings.StartUp) {
                runKey.SetValue (appKey, AppPathes.ExecutorPath);
                if (!TaskService.Instance.AllTasks.ToList ().Exists (_task => _task.Name == appKey)) {
                    TaskDefinition td = TaskService.Instance.NewTask ();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Actions.Add (AppPathes.ExecutorPath);

                    LogonTrigger lt = new LogonTrigger ();
                    td.Triggers.Add (lt);
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    TaskService.Instance.RootFolder.RegisterTaskDefinition (appKey, td);
                    NLogger.Info ("已设置开机启动.");
                }
            } else {
                runKey.DeleteValue (appKey, false);
                if (TaskService.Instance.AllTasks.ToList ().Exists (_task => _task.Name == appKey)) {
                    TaskService.Instance.RootFolder.DeleteTask (appKey, false);
                    NLogger.Info ("已取消开机启动.");
                }
            }

            if (AppSettings.ShortCut) {
                createShortcutIfNotExists ();
            } else {
                var _desktopDir = Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory);
                var _execLink = Path.Combine (_desktopDir, "软件运维中心.lnk");
                if (System.IO.File.Exists (_execLink)) {
                    System.IO.File.Delete (_execLink);
                    NLogger.Info ("已删除桌面快捷方式:{0}", _execLink);
                }
            }
        }

        /// <summary>
        /// 创建桌面快捷方式
        /// </summary>
        private void createShortcutIfNotExists () {
            var _desktopDir = Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory);
            var _execLink = Path.Combine (_desktopDir, "软件运维中心.lnk");

            if (System.IO.File.Exists (_execLink)) { return; }
            NLogger.Info ("已创建桌面快捷方式:{0}.", _execLink);

            WshShellClass wsh = new WshShellClass ();
            IWshShortcut _shortcut = (IWshShortcut) wsh.CreateShortcut (_execLink);
            _shortcut.IconLocation = Path.Combine (AppPathes.AppRoot, "logo.ico");
            _shortcut.TargetPath = AppPathes.ExecutorPath;
            _shortcut.Save ();
        }

        /// <summary>
        /// 在进程树启动前执行的脚本文件
        /// </summary>
        private void executeProgramsBeforeStart () {
            if (!Directory.Exists (AppPathes.StartUpHooksDir)) return;
            var _files = Directory.GetFiles (AppPathes.StartUpHooksDir, "*.*", SearchOption.TopDirectoryOnly);

            _files.Where (_path => _path.EndsWith (".bat") || _path.EndsWith (".cmd"))
                .ToList ()
                .ForEach (_script => {
                    WinAPI.OpenProcess ("cmd.exe", $"/c {_script}", true, false);
                    NLogger.Info ("StartUp Hook 执行脚本:{0}", Path.GetFileName (_script));
                });

            _files.Where (_path => _path.EndsWith (".exe"))
                .ToList ()
                .ForEach (_program => {
                    WinAPI.OpenProcess (_program, "", true);
                    NLogger.Info ("StartUp Hook 执行程序:{0}", Path.GetFileName (_program));
                });
        }

    }
}