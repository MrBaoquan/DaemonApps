using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using DNHper;
//using IWshRuntimeLibrary;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace DaemonKit.Core {
    class AppMain {
        static RegistryKey runKey = Registry.CurrentUser.OpenSubKey (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        public static string ConfigPath {
            get {
                return Path.Combine (Path.GetDirectoryName (Process.GetCurrentProcess ().MainModule.FileName), "config.xml");
            }
        }

        public static string BakConfigPath {
            get {
                return Path.Combine (Path.GetDirectoryName (Process.GetCurrentProcess ().MainModule.FileName), "temp", "config.xml");
            }
        }

        public static AppConfig appConfig;
        private static string mainProcess = string.Empty;
        public static string MainProcess {
            get { return mainProcess; }
        }
        private static float delayTime = 5.0f, intervalTime = 10.0f;
        private static bool bKeepTop = true;
        private static bool bAutoStart = true;
        private static bool bRunAs = true;
        public static void syncConfig () {
            var _executorPath = Process.GetCurrentProcess ().MainModule.FileName;
            var _uniKey = "DaemonKit_" + _executorPath.ToMD5 ();

            if (mainProcess != appConfig.MainProcess) {
                var _desktopDir = Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory);
                var _execLink = Path.Combine (_desktopDir, string.Format ("DK_{0}.lnk", Path.GetFileNameWithoutExtension (mainProcess)));
                if (System.IO.File.Exists (_execLink)) {
                    System.IO.File.Delete (_execLink);
                    NLogger.Info ("[DK]: 已删除桌面快捷方式:{0}", _execLink);
                }
            }
            mainProcess = appConfig.MainProcess;
            delayTime = Math.Max (appConfig.DelayTime, 0.1f);
            intervalTime = Math.Max (appConfig.IntervalTime, 0.1f);
            bKeepTop = appConfig.KeepTop;
            bAutoStart = appConfig.AutoStart;
            bRunAs = appConfig.RunAs;

            if (bAutoStart) {
                runKey.SetValue (_uniKey, _executorPath);
                if (!TaskService.Instance.AllTasks.ToList ().Exists (_task => _task.Name == _uniKey)) {
                    TaskDefinition td = TaskService.Instance.NewTask ();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Actions.Add (_executorPath, appConfig.Arguments);

                    LogonTrigger lt = new LogonTrigger ();
                    td.Triggers.Add (lt);
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    TaskService.Instance.RootFolder.RegisterTaskDefinition (_uniKey, td);
                    NLogger.Info ("[DK]: 已设置开机启动.");
                }
            } else {
                runKey.DeleteValue (_uniKey, false);
                if (TaskService.Instance.AllTasks.ToList ().Exists (_task => _task.Name == _uniKey)) {
                    TaskService.Instance.RootFolder.DeleteTask (_uniKey, false);
                    NLogger.Info ("[DK]: 已取消开机启动.");
                }
            }

            createShortcutIfNotExists ();
        }

        private static void createShortcutIfNotExists () {
            var _executorPath = Process.GetCurrentProcess ().MainModule.FileName;
            var _desktopDir = Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory);
            var _execLink = Path.Combine (_desktopDir, string.Format ("DK_{0}.lnk", Path.GetFileNameWithoutExtension (appConfig.MainProcess)));

            // if (System.IO.File.Exists (_execLink)) { return; }
            // NLogger.Info ("[DK]: 已创建桌面快捷方式:{0}.", _execLink);

            // WshShellClass wsh = new WshShellClass ();
            // IWshShortcut _shortcut = (IWshShortcut) wsh.CreateShortcut (_execLink);
            // _shortcut.IconLocation = Path.Combine (Path.GetDirectoryName (_executorPath), "icon.ico");
            // _shortcut.TargetPath = _executorPath;
            // _shortcut.Save ();
        }

        public static void SaveConfig () {
            USerialization.SerializeXML (appConfig, ConfigPath);
            syncBakConfig ();
        }

        private static void syncBakConfig () {
            if (!Directory.Exists (Path.GetDirectoryName (BakConfigPath))) {
                Directory.CreateDirectory (Path.GetDirectoryName (BakConfigPath));
            }
            System.IO.File.Copy (ConfigPath, BakConfigPath, true);
        }

        private static MainWindow mainWindow = null;

        private static void loadConfig () {
            if (!System.IO.File.Exists (ConfigPath) || new FileInfo (ConfigPath).Length == 0) {
                if (!System.IO.File.Exists (BakConfigPath)) {
                    USerialization.SerializeXML (new AppConfig (), ConfigPath);
                } else {
                    System.IO.File.Copy (BakConfigPath, ConfigPath, true);
                }
            }

            // 反序列化配置文件
            try {
                appConfig = USerialization.DeserializeXML<AppConfig> (ConfigPath);
                syncBakConfig ();
            } catch (Exception e) {
                NLogger.Error (e.Message);
                //MessageBox.Show(e.Message, "错误", MessageBoxButtons.OK);
            }
        }
        public static void ProgramEntry (MainWindow InWindow) {
            mainWindow = InWindow;
            NLogger.LogFileDir = "Logs";
            NLogger.Initialize ();

            loadConfig ();

            syncConfig ();
            RunDaemonTask ();

            //Observable.Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(200))
            //    .ObserveOn(mainWindow)
            //    .Subscribe(_ => {
            //        //mainWindow.Log(
            //        //    NLogger.FetchMessage().Aggregate(string.Empty, (_current, _next) => _current + _next + "\r\n")
            //        //);
            //    });
        }

        private static IDisposable _checkHandler = null;
        public static void RunDaemonTask () {
            if (!System.IO.File.Exists (mainProcess)) return;
            ClearDeamon ();
            NLogger.Info ("[DK]: 已启动守护任务.");
            _checkHandler = Observable
                .Timer (TimeSpan.FromSeconds (delayTime), TimeSpan.FromSeconds (intervalTime))
                .Subscribe (_ => {
                    NLogger.Info ("[DK]: 执行守护任务");
                    OpenProcess ();
                });
        }

        public static void OpenProcess () {
            ProcManager.DaemonProcess (mainProcess, appConfig.Arguments, bRunAs);

            if (bKeepTop) {
                ProcManager.KeepTopWindow (mainProcess);
            }

            if (appConfig.KillIfHung) {
                var _process = WinAPI.FindProcess (mainProcess);
                if (_process == default (Process)) return;

                // 如果程序挂起 则关闭进程
                if (WinAPI.IsHungAppWindow (_process.MainWindowHandle)) {
                    _process.Kill ();
                    NLogger.Info ("[DK]: 检测到进程挂起, 已将其杀死: {0}", mainProcess);
                }
            }

        }

        public static void KillProcess () {
            ClearDeamon ();
            var _process = WinAPI.FindProcess (mainProcess);
            if (_process == default (Process)) return;
            _process.Kill ();
            NLogger.Info ("[DK]: 已杀死进程: {0}", mainProcess);
        }

        public static void ClearDeamon () {
            if (_checkHandler != null) {
                _checkHandler.Dispose ();
                _checkHandler = null;
            }
        }
    }
}