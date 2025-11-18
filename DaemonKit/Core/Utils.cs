using DNHper;
using Hardware.Info;
using Microsoft.Win32.TaskScheduler;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace DaemonKit
{
    internal class Utils
    {

        public static void DeleteShortcutIfExists()
        {
            var _desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var _execLink = Path.Combine(_desktopDir, "运维管家.lnk");
            if (System.IO.File.Exists(_execLink))
            {
                System.IO.File.Delete(_execLink);
                NLogger.Info("已删除桌面快捷方式:{0}", _execLink);
            }
        }

        /// <summary>
        /// 创建桌面快捷方式
        /// </summary>
        public static void CreateShortcutIfNotExists()
        {
            var _desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var _execLink = Path.Combine(_desktopDir, "运维管家.lnk");

            if (System.IO.File.Exists(_execLink))
            {
                return;
            }

            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                var _shortcut = shell.CreateShortcut(_execLink);

                // WshShellClass wsh = new WshShellClass ();
                // IWshShortcut _shortcut = (IWshShortcut) wsh.CreateShortcut (_execLink);
                _shortcut.IconLocation = Path.Combine(AppPathes.ResDir, "Icons/logo.ico");
                _shortcut.TargetPath = AppPathes.ExecutorPath;
                _shortcut.Save();

                NLogger.Info("已创建桌面快捷方式:{0}.", _execLink);
            }
            catch
            {
                NLogger.Error("创建桌面快捷方式失败");
            }

        }

        /// <summary>
        /// 在进程树启动前执行的脚本文件
        /// </summary>
        public static void ExecuteProgramsBeforeStart()
        {
            if (!Directory.Exists(AppPathes.StartUpHooksDir))
                return;
            var _files = Directory.GetFiles(
                AppPathes.StartUpHooksDir,
                "*.*",
                SearchOption.TopDirectoryOnly
            );

            _files
                .Where(_path => _path.EndsWith(".bat") || _path.EndsWith(".cmd"))
                .ToList()
                .ForEach(_script =>
                {
                    // WinAPI.OpenProcess("cmd.exe", $"/k {_script}", true, false);
                    WinAPI.OpenProcess(
                        "C:\\Windows\\System32\\cmd.exe",
                        $"/c {_script}",
                        true,
                        false
                    );
                    NLogger.Info("StartUp Hook 执行脚本:{0}", Path.GetFileName(_script));
                });

            _files
                .Where(_path => _path.EndsWith(".exe"))
                .ToList()
                .ForEach(_program =>
                {
                    WinAPI.OpenProcess(_program, "", true);
                    NLogger.Info("StartUp Hook 执行程序:{0}", Path.GetFileName(_program));
                });
        }

        static readonly HardwareInfo hardwareInfo = new HardwareInfo();
        public static IObservable<string> FetchHardwareInfo()
        {
            return Observable
                .Start<string>(() =>
                {
                    hardwareInfo.RefreshCPUList();
                    hardwareInfo.RefreshVideoControllerList();
                    hardwareInfo.RefreshMemoryList();
                    hardwareInfo.RefreshNetworkAdapterList();
                    hardwareInfo.RefreshMonitorList();
                    hardwareInfo.RefreshBIOSList();
                    hardwareInfo.RefreshMotherboardList();
                    var _description = HardwareInfo
                        .GetLocalIPv4Addresses()
                        .Aggregate(
                            "IPv4地址:" + "\r\n",
                            (_current, _next) =>
                            {
                                return _current + _next + "\r\n";
                            }
                        );
                    _description = hardwareInfo.CpuList.Aggregate(
                        _description + "\r\nCPU:\r\n",
                        (_current, _next) =>
                        {
                            return _current + _next.Name;
                        }
                    );
                    _description = hardwareInfo.VideoControllerList.Aggregate(
                        _description + "\r\n\r\nGPU:\r\n",
                        (_current, _next) =>
                        {
                            return _current + _next.Name;
                        }
                    );
                    _description = hardwareInfo.MemoryList.Aggregate(
                        _description + "\r\n\r\n内存:\r\n",
                        (_current, _next) =>
                        {
                            return _current
                                + string.Format(
                                    "{0}-{1}({2})",
                                    _next.Manufacturer,
                                    _next.PartNumber,
                                    _next.Capacity.FormatBytes()
                                )
                                + "\r\n";
                        }
                    );
                    _description = hardwareInfo.MonitorList.Aggregate(
                        _description + "\r\n显示器:\r\n",
                        (_current, _next) =>
                        {
                            return _current + _next.Name + "\r\n";
                        }
                    );
                    _description = hardwareInfo.BiosList.Aggregate(
                        _description + "\r\nBIOS:\r\n",
                        (_current, _next) =>
                        {
                            return _current + _next.Manufacturer + " " + _next.Version + "\r\n";
                        }
                    );
                    _description = hardwareInfo.MotherboardList.Aggregate(
                        _description + "\r\n主板:\r\n",
                        (_current, _next) =>
                        {
                            return _current + _next.Manufacturer + " " + _next.Product + "\r\n";
                        }
                    );
                    return _description;
                })
                .Catch<string,Exception>(ex=> Observable.Return("硬件信息获取失败"))
                .ObserveOn(RxApp.MainThreadScheduler);
        }

        public static void RegisterHotKey(System.Windows.Window window)
        {
            var helper = new WindowInteropHelper(window);
            WinAPI.RegisterHotKey(helper.Handle, 100, (uint)KeyModifiers.Ctrl, 0x44);
            WinAPI.RegisterHotKey(helper.Handle, 101, (uint)KeyModifiers.Ctrl, 0x52);
            WinAPI.RegisterHotKey(helper.Handle, 102, (uint)KeyModifiers.Ctrl, 0x57);
            WinAPI.RegisterHotKey(
                helper.Handle,
                103,
                (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                0x45
            );
            WinAPI.RegisterHotKey(
                helper.Handle,
                104,
                (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                0x57
            );
        }

        public static void UnRegisterHotKey(System.Windows.Window window)
        {
            var helper = new WindowInteropHelper(window);
            WinAPI.UnregisterHotKey(helper.Handle, 100);
            WinAPI.UnregisterHotKey(helper.Handle, 101);
            WinAPI.UnregisterHotKey(helper.Handle, 102);
            WinAPI.UnregisterHotKey(helper.Handle, 103);
            WinAPI.UnregisterHotKey(helper.Handle, 104);
        }
        //static RegistryKey runKey = Registry.CurrentUser.OpenSubKey (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        const string appKey = "DaemonKit";

        public static void SyncSettings()
        {
            var AppSettings = MainWindow.AppSettings;
            if (AppSettings.StartUp)
            {
                //runKey.SetValue (appKey, AppPathes.ExecutorPath);
                var _startUpTask = TaskService.Instance.AllTasks
                    .Where(_task => _task.Name == appKey)
                    .FirstOrDefault();
                if (_startUpTask == null)
                {
                    TaskDefinition td = TaskService.Instance.NewTask();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Actions.Add(AppPathes.ExecutorPath);

                    LogonTrigger lt = new LogonTrigger();
                    td.Triggers.Add(lt);
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    TaskService.Instance.RootFolder.RegisterTaskDefinition(appKey, td);
                    NLogger.Info("已设置开机启动.");
                }
                else if (
                    (_startUpTask.Definition.Actions.First() as ExecAction).Path
                    != AppPathes.ExecutorPath
                )
                {
                    if (
                        MessageBox.Show(
                            $"已设置{_startUpTask.Definition.Actions.First()}为默认启动路径，是否更改当前进程为默认启动项",
                            "启动路径冲突",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Warning,
                            MessageBoxResult.Cancel
                        ) == MessageBoxResult.Yes
                    )
                    {
                        _startUpTask.Definition.Actions.Clear();
                        _startUpTask.Definition.Actions.Add(AppPathes.ExecutorPath);
                        _startUpTask.RegisterChanges();
                        NLogger.Info("已更改启动路径为: " + AppPathes.ExecutorPath);
                        Utils.DeleteShortcutIfExists();
                        Utils.CreateShortcutIfNotExists();
                    }
                }
            }
            else
            {
                if (TaskService.Instance.AllTasks.ToList().Exists(_task => _task.Name == appKey))
                {
                    TaskService.Instance.RootFolder.DeleteTask(appKey, false);
                    NLogger.Info("已取消开机启动.");
                }
            }

            if (AppSettings.ShortCut)
            {
                Utils.CreateShortcutIfNotExists();
            }
            else
            {
                Utils.DeleteShortcutIfExists();
            }
        }
    }
}
