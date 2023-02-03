using System.Diagnostics;
using System.IO;
class AppPathes {

    public static string ExecutorPath { get => Process.GetCurrentProcess ().MainModule.FileName; }

    private static string _appPath = string.Empty;
    // 进程根目录
    public static string AppRoot {
        get {
            if (string.IsNullOrEmpty (_appPath)) {
                _appPath = Path.GetDirectoryName (ExecutorPath);
            }
            return _appPath;
        }
    }
    // 资源目录
    public static string ResDir { get => Path.Combine (AppRoot, "Resources"); }
    // 配置文件目录
    public static string ConfigDir { get => Path.Combine (ResDir, "Configs"); }
    public static string ConfigDir_BackUp { get => Path.Combine (AppRoot, ".cache"); }
    // 目录树持久化路径
    public static string TreeViewDataPath { get => Path.Combine (ConfigDir, "treeview.xml"); }
    public static string TreeViewDataPath_Backup { get => Path.Combine (AppRoot, ".cache/treeview.xml"); }
    // 拓展配置文件路径
    public static string ExtensionConfigPath { get => Path.Combine (ConfigDir, "extension.xml"); }
    public static string ExtensionConfigPath_Backup { get => Path.Combine (AppRoot, ".cache/extension.xml"); }
    public static string AppSettingPath { get => Path.Combine (ConfigDir, "settings.xml"); }
    public static string AppSettingPath_Backup { get => Path.Combine (AppRoot, ".cache/settings.xml"); }
    // 拓展路径
    public static string ExtensionPath { get => Path.Combine (ResDir, "Extensions"); }
    public static string AppDir { get => System.IO.Path.Combine (System.IO.Path.GetDirectoryName (ExecutorPath), "Applications"); }

    /// <summary>
    /// hook 文件目录
    /// </summary>
    /// <returns></returns>
    public static string HooksDir { get => Path.Combine (AppRoot, "Hooks"); }
    public static string StartUpHooksDir { get => Path.Combine (HooksDir, "StartUp"); }
    public static string DestroyHooksDir { get => Path.Combine (HooksDir, "Destroy"); }
}