using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
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
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;

namespace DaemonKit {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<MainViewModel> {

        public string ExecutorPath { get => Process.GetCurrentProcess ().MainModule.FileName; }
        // 进程根目录
        public string AppPath { get => System.IO.Path.GetDirectoryName (ExecutorPath); }
        // 资源目录
        public string ResDir { get => Path.Combine (AppPath, "Resources"); }
        // 配置文件目录
        public string ConfigDir { get => Path.Combine (ResDir, "Configs"); }
        // 目录树持久化路径
        public string TreeViewDataPath { get => Path.Combine (ConfigDir, "treeview.xml"); }
        public string TreeViewDataPath_Backup { get => Path.Combine (AppPath, ".cache/treeview.xml"); }
        // 拓展配置文件路径
        public string ExtensionConfigPath { get => Path.Combine (ConfigDir, "extension.xml"); }
        public string ExtensionConfigPath_Backup { get => Path.Combine (AppPath, ".cache/extension.xml"); }

        public string AppSettingPath { get => Path.Combine (ConfigDir, "settings.xml"); }
        public string AppSettingPath_Backup { get => Path.Combine (AppPath, ".cache/settings.xml"); }
        // 拓展路径
        public string ExtensionPath { get => Path.Combine (ResDir, "Extensions"); }
        public AppSettings AppSettings { get; set; }

        ProcessItem rootProcessNode = null;

        //public static RoutedCommand OpenProcess = new RoutedCommand ();
        public MainWindow () {
            InitializeComponent ();

            ViewModel = new MainViewModel ();

            NLogger.LogFileDir = "Logs";
            NLogger.Initialize ();

            // 节点编辑窗口
            ProcessNodeForm processNodeForm = new ProcessNodeForm ();
            Settings settingsWindow = new Settings ();
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

                FetchHardwareInfo ();
                this.hardwareInfoBox.Events ().MouseDoubleClick.Subscribe (_contentLoaded => {
                    FetchHardwareInfo ();
                });

                //this.BindCommand (this.ViewModel, vm => vm.DisplayCommand, v => v.menu_cut).DisposeWith (disposables);
                //this.Bind (this.ViewModel, vm => vm.Text, v => v.textbox.Text).DisposeWith (disposables);
                this.ProcessTree.DataContext = this.DataContext;
                NLogger.Info ("DaemonKit 加载进程树..");
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
                        settingsWindow.Hide ();
                        saveConfig ();
                        syncSettings ();
                    });
                });

                ViewModel.ToggleEnable.Subscribe (_item => {
                    _item.SyncEnable ();
                    saveConfig ();
                });

                ViewModel.ShowInExplorer.Subscribe (_ => {
                    WinAPI.OpenProcess ("explorer.exe", " /select," + _selectedTreeNode.MetaData.Path);
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

                // 进程表单提交
                processNodeForm.VM.Confirm.Subscribe (_ => {
                    if (processNodeForm.VM.IsCreateMode) {
                        _selectedTreeNode.AddChild (new ProcessItem {
                            MetaData = _
                        });
                    } else {
                        _.Enable = _selectedTreeNode.Enable;
                        _selectedTreeNode.MetaData = _;
                    }
                    processNodeForm.Hide ();

                    saveConfig ();
                });

                ViewModel.ShowAppDirectory.Subscribe (_ => {
                    WinAPI.OpenProcess ("explorer.exe", AppPath);
                });

                ViewModel.RunNodeTree.Subscribe (_ => {
                    rootProcessNode.RunNode ();
                });

                ViewModel.KillNodeTree.Subscribe (_ => {
                    rootProcessNode.KillNode ();
                });

                ViewModel.RunProcess.Subscribe (_ => {
                    WinAPI.OpenProcess (_.Path, _.Arguments, _.RunAs);
                });

                rootProcessNode.RunNode ();

            });

            InputBindings.Add (new KeyBinding { Command = ViewModel.ShowAppDirectory, Key = Key.D1, Modifiers = ModifierKeys.Control });
            InputBindings.Add (new KeyBinding { Command = ViewModel.RunProcess, Key = Key.T, Modifiers = ModifierKeys.Control, CommandParameter = ViewModel.OpenCMD_args });
            InputBindings.Add (new KeyBinding { Command = ViewModel.RunProcess, Key = Key.P, Modifiers = ModifierKeys.Control, CommandParameter = ViewModel.OpenPowerShell_args });
        }

        static readonly HardwareInfo hardwareInfo = new HardwareInfo ();
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
            });

        }

        private void loadExtensions () {

            if (!System.IO.File.Exists (ExtensionConfigPath)) {
                USerialization.SerializeXML (new ExtensionConfig (), ExtensionConfigPath);
            };

            try {
                var _extConfig = USerialization.DeserializeXML<ExtensionConfig> (ExtensionConfigPath);
                var _cusMenu = new MenuItem { Header = _extConfig.Name };

                _extConfig.Extensions.WithIndex ().ToList ().ForEach (_extention => {
                    var _menuItem = new MenuItem { Header = _extention.item.Name };

                    Action < (Extension item, int index) > _handleMenuClick = (_ext) => {
                        var _extensionPath = Path.Combine (ExtensionPath, _ext.item.Path);
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

        // 加载配置文件
        private void loadConfig () {
            if (!System.IO.File.Exists (TreeViewDataPath)) {
                if (!Directory.Exists (Path.GetDirectoryName (TreeViewDataPath)))
                    Directory.CreateDirectory (Path.GetDirectoryName (TreeViewDataPath));
                rootProcessNode = new ProcessItem { MetaData = new ProcessMetaData { Name = "[ 进程树 ]", Delay = 0, Path = string.Empty } };
                saveConfig ();
            }
            if (System.IO.File.ReadAllText (TreeViewDataPath).Length == 0 && System.IO.File.Exists (TreeViewDataPath_Backup)) {
                System.IO.File.Copy (TreeViewDataPath_Backup, TreeViewDataPath, true);
            }
            rootProcessNode = USerialization.DeserializeXML<ProcessItem> (TreeViewDataPath);
            rootProcessNode.SyncRelationships ();

            if (!System.IO.File.Exists (AppSettingPath)) {
                USerialization.SerializeXML (new AppSettings (), AppSettingPath);
            }
            if (System.IO.File.ReadAllText (AppSettingPath).Length == 0 && System.IO.File.Exists (AppSettingPath_Backup)) {
                System.IO.File.Copy (AppSettingPath_Backup, AppSettingPath, true);
            }
            AppSettings = USerialization.DeserializeXML<AppSettings> (AppSettingPath);

            syncSettings ();
        }

        // 数据持久化
        private void saveConfig () {
            USerialization.SerializeXML (rootProcessNode, TreeViewDataPath);

            USerialization.SerializeXML (AppSettings, AppSettingPath);
            if (!Directory.Exists (Path.GetDirectoryName (TreeViewDataPath_Backup))) {
                Directory.CreateDirectory (Path.GetDirectoryName (TreeViewDataPath_Backup));
            }
            // 备份配置文件
            System.IO.File.Copy (TreeViewDataPath, TreeViewDataPath_Backup, true);
            System.IO.File.Copy (ExtensionConfigPath, ExtensionConfigPath_Backup, true);
            System.IO.File.Copy (AppSettingPath, AppSettingPath_Backup, true);
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
            }
            return IntPtr.Zero;
        }

        static RegistryKey runKey = Registry.CurrentUser.OpenSubKey (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        const string appKey = "DaemonKit";
        private void syncSettings () {
            if (AppSettings.StartUp) {
                runKey.SetValue (appKey, ExecutorPath);
                if (!TaskService.Instance.AllTasks.ToList ().Exists (_task => _task.Name == appKey)) {
                    TaskDefinition td = TaskService.Instance.NewTask ();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Actions.Add (ExecutorPath);

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

        private void createShortcutIfNotExists () {
            var _desktopDir = Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory);
            var _execLink = Path.Combine (_desktopDir, "软件运维中心.lnk");

            if (System.IO.File.Exists (_execLink)) { return; }
            NLogger.Info ("已创建桌面快捷方式:{0}.", _execLink);

            WshShellClass wsh = new WshShellClass ();
            IWshShortcut _shortcut = (IWshShortcut) wsh.CreateShortcut (_execLink);
            _shortcut.IconLocation = Path.Combine (AppPath, "logo.ico");
            _shortcut.TargetPath = ExecutorPath;
            _shortcut.Save ();
        }

    }
}