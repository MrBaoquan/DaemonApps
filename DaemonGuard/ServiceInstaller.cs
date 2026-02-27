using System.Diagnostics;
using System.ServiceProcess;

namespace DaemonGuard;

/// <summary>
/// 服务安装/卸载辅助工具，使用 sc.exe 注册 Windows 服务。
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "DaemonGuard";
    public const string DisplayName = "DaemonKit 守护服务";
    public const string Description = "监控 DaemonKit.exe 进程，自动在用户桌面会话中重启。";

    /// <summary>
    /// 安装为 Windows 服务。
    /// </summary>
    /// <param name="args">命令行参数，args[1] 可选指定 DaemonKit.exe 路径</param>
    public static void Install(string[] args)
    {
        string exePath =
            Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "DaemonGuard.exe");

        Console.WriteLine($"正在安装服务 {ServiceName}...");
        Console.WriteLine($"服务路径: {exePath}");

        // 检查是否已安装
        if (IsServiceInstalled())
        {
            Console.WriteLine($"服务 {ServiceName} 已存在，先卸载旧服务...");
            RunSc($"delete {ServiceName}");
            Thread.Sleep(1000);
        }

        // sc create
        int exitCode = RunSc(
            $"create {ServiceName} binPath= \"\\\"{exePath}\\\"\" start= auto "
                + $"DisplayName= \"{DisplayName}\""
        );

        if (exitCode != 0)
        {
            Console.Error.WriteLine("服务安装失败！请以管理员权限运行。");
            return;
        }

        // sc description
        RunSc($"description {ServiceName} \"{Description}\"");

        // sc failure - 配置故障恢复策略：三次均为重启，间隔 10 秒
        RunSc(
            $"failure {ServiceName} reset= 86400 actions= restart/10000/restart/10000/restart/10000"
        );

        // 如果指定了 DaemonKit.exe 路径，写入 appsettings
        if (args.Length > 1 && !args[1].StartsWith('-'))
        {
            string targetExePath = args[1];
            WriteAppSettings(targetExePath);
            Console.WriteLine($"已配置目标路径: {targetExePath}");
        }

        Console.WriteLine($"服务 {ServiceName} 安装成功！");

        // 安装后自动启动服务
        Console.WriteLine("正在自动启动服务...");
        int startResult = RunSc($"start {ServiceName}");
        if (startResult == 0)
        {
            Console.WriteLine($"服务 {ServiceName} 已启动。");
        }
        else
        {
            Console.WriteLine("自动启动失败，请手动执行: net start DaemonGuard");
        }
    }

    /// <summary>
    /// 卸载 Windows 服务。
    /// </summary>
    public static void Uninstall()
    {
        Console.WriteLine($"正在卸载服务 {ServiceName}...");

        if (!IsServiceInstalled())
        {
            Console.WriteLine($"服务 {ServiceName} 未安装。");
            return;
        }

        // 先尝试停止
        RunSc($"stop {ServiceName}");
        Thread.Sleep(2000);

        // 删除服务
        int exitCode = RunSc($"delete {ServiceName}");

        if (exitCode == 0)
        {
            Console.WriteLine($"服务 {ServiceName} 已卸载。");
        }
        else
        {
            Console.Error.WriteLine("服务卸载失败！请以管理员权限运行。");
        }
    }

    /// <summary>
    /// 显示服务状态。
    /// </summary>
    public static void ShowStatus()
    {
        if (!IsServiceInstalled())
        {
            Console.WriteLine($"服务 {ServiceName} 未安装。");
            return;
        }

        RunSc($"query {ServiceName}");
    }

    /// <summary>
    /// 写入 appsettings.json 配置文件。
    /// </summary>
    private static void WriteAppSettings(string targetExePath)
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        string json = $$"""
        {
          "Guard": {
            "TargetExePath": "{{targetExePath.Replace("\\", "\\\\")}}",
            "CheckIntervalSeconds": 5,
            "RestartDelaySeconds": 3,
            "MaxConsecutiveRestarts": 5,
            "CooldownSeconds": 60,
            "TargetProcessName": "DaemonKit"
          },
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.Hosting.Lifetime": "Information"
            }
          }
        }
        """;

        File.WriteAllText(settingsPath, json);
    }

    /// <summary>
    /// 检查服务是否已安装。
    /// </summary>
    private static bool IsServiceInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 执行 sc.exe 命令。
    /// </summary>
    private static int RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return -1;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
                Console.WriteLine(output.TrimEnd());
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error.TrimEnd());

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"执行 sc.exe 失败: {ex.Message}");
            return -1;
        }
    }
}
