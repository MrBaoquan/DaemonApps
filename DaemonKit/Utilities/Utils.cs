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

namespace DaemonKit.Utilities
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

        // 延迟到 MTA 线程（Observable.Start / Task.Run）中创建，
        // 避免 WMI COM 对象绑定 STA 导致跨公寓编排回 UI 线程死锁。
        [ThreadStatic]
        private static HardwareInfo? _hardwareInfo;

        public static IObservable<string> FetchHardwareInfo()
        {
            return Observable
                .Start<string>(() =>
                {
                    // 在 MTA 线程首次访问时创建，确保 WMI COM 对象绑定 MTA
                    _hardwareInfo ??= new HardwareInfo();
                    var hardwareInfo = _hardwareInfo;
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
                .Catch<string, Exception>(ex => Observable.Return("硬件信息获取失败"))
                .ObserveOn(RxApp.MainThreadScheduler);
        }

        public static void RegisterHotKey(System.Windows.Window window, AppSettings settings)
        {
            var helper = new WindowInteropHelper(window);
            UnRegisterHotKey(window);
            if (!settings.EnableGlobalHotKey)
            {
                return;
            }

            if (settings.EnableToggleWindow)
            {
                WinAPI.RegisterHotKey(helper.Handle, 100, (uint)KeyModifiers.Ctrl, 0x44);
            }

            if (settings.EnableStartTree)
            {
                WinAPI.RegisterHotKey(helper.Handle, 101, (uint)KeyModifiers.Ctrl, 0x52);
            }

            if (settings.EnableStopTree)
            {
                WinAPI.RegisterHotKey(helper.Handle, 102, (uint)KeyModifiers.Ctrl, 0x57);
            }

            if (settings.EnableDesktopOn)
            {
                WinAPI.RegisterHotKey(
                    helper.Handle,
                    103,
                    (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                    0x45
                );
            }

            if (settings.EnableDesktopOff)
            {
                WinAPI.RegisterHotKey(
                    helper.Handle,
                    104,
                    (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                    0x57
                );
            }

            if (settings.EnableScreenshot)
            {
                WinAPI.RegisterHotKey(helper.Handle, 9000, (uint)KeyModifiers.Alt, 0x58);
                // Alt+C for color picker
                WinAPI.RegisterHotKey(helper.Handle, 9001, (uint)KeyModifiers.Alt, 0x43);
            }

            if (settings.EnableScheduleToggleHotKey)
            {
                // Alt + S
                WinAPI.RegisterHotKey(helper.Handle, 105, (uint)KeyModifiers.Alt, 0x53);
            }

            // Ctrl+Shift+T 紧急恢复（始终注册，安全机制）
            WinAPI.RegisterHotKey(
                helper.Handle,
                106,
                (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                0x54
            );

            // Ctrl+Shift+D 编排调试模式（始终注册）
            WinAPI.RegisterHotKey(
                helper.Handle,
                107,
                (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                0x44
            );

            // Ctrl+Shift+R 守护运行模式（始终注册）
            WinAPI.RegisterHotKey(
                helper.Handle,
                108,
                (uint)(KeyModifiers.Ctrl | KeyModifiers.Shift),
                0x52
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
            WinAPI.UnregisterHotKey(helper.Handle, 9000); // Alt+X
            WinAPI.UnregisterHotKey(helper.Handle, 9001); // Alt+C
            WinAPI.UnregisterHotKey(helper.Handle, 105); // Alt+S
            WinAPI.UnregisterHotKey(helper.Handle, 106); // Ctrl+Shift+T 紧急恢复
            WinAPI.UnregisterHotKey(helper.Handle, 107); // Ctrl+Shift+D 编排调试
            WinAPI.UnregisterHotKey(helper.Handle, 108); // Ctrl+Shift+R 守护运行
        }

        //static RegistryKey runKey = Registry.CurrentUser.OpenSubKey (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        const string appKey = "DaemonKit";

        public static void SyncSettings()
        {
            var AppSettings = MainWindow.AppSettings;

            // Task Scheduler COM API (ITaskService) 需要 STA 线程环境。
            // 使用专用 STA 后台线程代替 Task.Run (MTA)，避免跨公寓 COM 调用导致死锁。
            var syncThread = new System.Threading.Thread(() =>
            {
                try
                {
                    SyncStartupTask(AppSettings);
                }
                catch (Exception ex)
                {
                    NLogger.Warn("同步开机启动任务异常: {Message}", ex.Message);
                }

                try
                {
                    if (AppSettings.StartUp)
                    {
                        Services.GuardServiceHelper.SyncGuardService(true, AppPathes.ExecutorPath);
                        // 启动双向守护监测：持续检测 DaemonGuard 服务是否存活
                        Services.GuardServiceHelper.StartMonitoring();
                    }
                    else
                    {
                        Services.GuardServiceHelper.SyncGuardService(false, null);
                        // 自启动已禁用，停止监测（如果之前启用过）
                        Services.GuardServiceHelper.StopMonitoring();
                    }
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Warn("同步守护服务状态异常: {Message}", ex.Message);
                }
            });
            syncThread.SetApartmentState(System.Threading.ApartmentState.STA);
            syncThread.IsBackground = true;
            syncThread.Start();

            if (AppSettings.ShortCut)
            {
                Utils.CreateShortcutIfNotExists();
            }
            else
            {
                Utils.DeleteShortcutIfExists();
            }
        }

        /// <summary>
        /// 同步开机启动计划任务（Task Scheduler COM 操作）。
        /// 此方法必须在后台线程调用，因为 TaskService COM API 可能耗时较长。
        /// 当检测到启动路径冲突时，自动更新为当前路径（不再弹出 MessageBox 阻塞 UI）。
        /// </summary>
        private static void SyncStartupTask(AppSettings AppSettings)
        {
            if (AppSettings.StartUp)
            {
                var _startUpTask = TaskService.Instance.AllTasks
                    .Where(_task => _task.Name == appKey)
                    .FirstOrDefault();
                if (_startUpTask == null)
                {
                    TaskDefinition td = TaskService.Instance.NewTask();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Actions.Add(AppPathes.ExecutorPath);

                    LogonTrigger lt = new LogonTrigger();
                    if (AppSettings.StartUpDelay > 0)
                    {
                        lt.Delay = TimeSpan.FromSeconds(AppSettings.StartUpDelay);
                    }
                    td.Triggers.Add(lt);
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    TaskService.Instance.RootFolder.RegisterTaskDefinition(appKey, td);
                    NLogger.Info(
                        $"已设置开机启动{(AppSettings.StartUpDelay > 0 ? $"（延迟 {AppSettings.StartUpDelay} 秒）" : "")}."
                    );
                }
                else if (
                    (_startUpTask.Definition.Actions.First() as ExecAction).Path
                    != AppPathes.ExecutorPath
                )
                {
                    // 启动路径冲突 — 自动更新为当前路径（不再弹 MessageBox 阻塞 UI 线程）
                    NLogger.Warn(
                        "检测到启动路径冲突，原路径: {OldPath}，自动更新为: {NewPath}",
                        (_startUpTask.Definition.Actions.First() as ExecAction).Path,
                        AppPathes.ExecutorPath
                    );

                    _startUpTask.Definition.Actions.Clear();
                    _startUpTask.Definition.Actions.Add(AppPathes.ExecutorPath);

                    // 更新延迟启动配置
                    var logonTrigger = _startUpTask.Definition.Triggers
                        .OfType<LogonTrigger>()
                        .FirstOrDefault();
                    if (logonTrigger != null)
                    {
                        if (AppSettings.StartUpDelay > 0)
                        {
                            logonTrigger.Delay = TimeSpan.FromSeconds(AppSettings.StartUpDelay);
                        }
                        else
                        {
                            logonTrigger.Delay = TimeSpan.Zero;
                        }
                    }

                    _startUpTask.RegisterChanges();
                    NLogger.Info("已自动更新启动路径为: " + AppPathes.ExecutorPath);
                    Utils.DeleteShortcutIfExists();
                    Utils.CreateShortcutIfNotExists();
                }
                else
                {
                    // 路径正确，但可能需要更新延迟时间
                    var logonTrigger = _startUpTask.Definition.Triggers
                        .OfType<LogonTrigger>()
                        .FirstOrDefault();
                    if (logonTrigger != null)
                    {
                        var currentDelay = logonTrigger.Delay.TotalSeconds;
                        if (currentDelay != AppSettings.StartUpDelay)
                        {
                            if (AppSettings.StartUpDelay > 0)
                            {
                                logonTrigger.Delay = TimeSpan.FromSeconds(AppSettings.StartUpDelay);
                            }
                            else
                            {
                                logonTrigger.Delay = TimeSpan.Zero;
                            }
                            _startUpTask.RegisterChanges();
                            NLogger.Info("已更新开机启动延迟为 {StartUpDelay} 秒.", AppSettings.StartUpDelay);
                        }
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
        }
    }
}
