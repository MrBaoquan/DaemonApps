using System.Diagnostics;
using System.IO;

namespace DaemonKit.Utilities
{
    class AppPathes
    {
        public static string ExecutorPath
        {
            get => Process.GetCurrentProcess().MainModule.FileName;
        }

        private static string _appPath = string.Empty;

        // 进程根目录
        public static string AppRoot
        {
            get
            {
                if (string.IsNullOrEmpty(_appPath))
                {
                    _appPath = Path.GetDirectoryName(ExecutorPath);
                }
                return _appPath;
            }
        }

        // ==================== 资源目录（配置 + 静态资源） ====================

        /// <summary>
        /// 资源目录 - 存放配置文件和静态资源
        /// </summary>
        public static string ResDir
        {
            get => Path.Combine(AppRoot, "Resources");
        }

        /// <summary>
        /// 配置文件目录
        /// </summary>
        public static string ConfigDir
        {
            get => Path.Combine(ResDir, "Configs");
        }

        /// <summary>
        /// 拓展路径
        /// </summary>
        public static string ExtensionPath
        {
            get => Path.Combine(ResDir, "Extensions");
        }

        // ==================== 用户数据目录（可增长、可清理） ====================

        /// <summary>
        /// 用户数据目录 - 存放可增长的用户数据（共享文件、接收文件、备份、截图）
        /// </summary>
        public static string DataDir
        {
            get => Path.Combine(AppRoot, "Data");
        }

        /// <summary>
        /// P2P共享文件目录 - 存放共享给其他设备的文件
        /// </summary>
        public static string SharedFilesDir
        {
            get => Path.Combine(DataDir, "SharedFiles");
        }

        /// <summary>
        /// P2P接收文件目录 - 存放从其他设备接收的文件
        /// </summary>
        public static string ReceivedFilesDir
        {
            get => Path.Combine(DataDir, "ReceivedFiles");
        }

        /// <summary>
        /// 进程包备份目录 - 存放备份的进程配置包
        /// </summary>
        public static string BackupsDir
        {
            get => Path.Combine(DataDir, "Backups");
        }

        /// <summary>
        /// 截图目录 - 存放截图文件
        /// </summary>
        public static string ScreenshotsDir
        {
            get => Path.Combine(DataDir, "Screenshots");
        }

        /// <summary>
        /// 配置备份目录 - 存放配置文件的备份副本，防止断电/崩溃导致配置损坏
        /// </summary>
        public static string ConfigBackupDir
        {
            get => Path.Combine(ConfigDir, "Backups");
        }

        // ==================== 配置文件路径 ====================

        // 目录树持久化路径
        public static string TreeViewDataPath
        {
            get => Path.Combine(ConfigDir, "treeview.xml");
        }
        public static string TreeViewDataPath_Backup
        {
            get => Path.Combine(ConfigBackupDir, "treeview.xml");
        }

        // 拓展配置文件路径
        public static string ExtensionConfigPath
        {
            get => Path.Combine(ConfigDir, "extension.xml");
        }
        public static string ExtensionConfigPath_Backup
        {
            get => Path.Combine(ConfigBackupDir, "extension.xml");
        }

        // 应用设置
        public static string AppSettingPath
        {
            get => Path.Combine(ConfigDir, "settings.xml");
        }
        public static string AppSettingPath_Backup
        {
            get => Path.Combine(ConfigBackupDir, "settings.xml");
        }

        // 全局计划任务配置路径
        public static string GlobalSchedulePath
        {
            get => Path.Combine(ConfigDir, "schedule.xml");
        }
        public static string GlobalSchedulePath_Backup
        {
            get => Path.Combine(ConfigBackupDir, "schedule.xml");
        }

        // 任务计划配置路径
        public static string ScheduleConfigPath
        {
            get => Path.Combine(ConfigDir, "ScheduleConfig.xml");
        }

        // 快捷键配置路径
        public static string HotkeyConfigPath
        {
            get => Path.Combine(ConfigDir, "HotkeyConfig.xml");
        }

        // ==================== 设备与传输配置 ====================

        /// <summary>
        /// 设备发现配置路径
        /// </summary>
        public static string DeviceDiscoveryConfigPath
        {
            get => Path.Combine(ConfigDir, "device_discovery.json");
        }

        /// <summary>
        /// 设备缓存路径
        /// </summary>
        public static string DeviceCachePath
        {
            get => Path.Combine(ConfigDir, "device_cache.json");
        }

        /// <summary>
        /// 传输历史记录路径
        /// </summary>
        public static string TransferHistoryPath
        {
            get => Path.Combine(ConfigDir, "transfer_history.json");
        }

        // ==================== 应用与Hook目录 ====================

        public static string AppDir
        {
            get => Path.Combine(AppRoot, "Applications");
        }

        /// <summary>
        /// hook 文件目录
        /// </summary>
        public static string HooksDir
        {
            get => Path.Combine(AppRoot, "Hooks");
        }
        public static string StartUpHooksDir
        {
            get => Path.Combine(HooksDir, "StartUp");
        }
        public static string DestroyHooksDir
        {
            get => Path.Combine(HooksDir, "Destroy");
        }

        // ==================== 日志目录 ====================

        /// <summary>
        /// 日志文件目录
        /// </summary>
        public static string LogsDir
        {
            get => Path.Combine(AppRoot, "Logs");
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 确保所有必要的目录存在
        /// </summary>
        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(ConfigBackupDir);
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(SharedFilesDir);
            Directory.CreateDirectory(ReceivedFilesDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(ScreenshotsDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(ExtensionPath);
        }
    }
}
