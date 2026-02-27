using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DNHper;

namespace DaemonKit.Services;

/// <summary>
/// DaemonGuard 守护服务集成辅助工具。
/// 守护服务跟随开机自启动设置：启用自启动时自动安装并启动，禁用时停止服务。
/// 整个过程不阻塞 UI 线程，失败时静默降级。
/// </summary>
public static class GuardServiceHelper
{
    private const string ServiceName = "DaemonGuard";
    private const string GuardSubDir = "Guard";
    private const string GuardExeName = "DaemonGuard.exe";

    /// <summary>
    /// 根据开机自启动设置同步守护服务状态。
    /// 此方法是守护服务管理的唯一入口，应在 SyncSettings 中调用。
    /// </summary>
    /// <param name="autoStartEnabled">是否启用了开机自启动</param>
    /// <param name="daemonKitExePath">DaemonKit.exe 的完整路径（自启动时使用的路径）</param>
    public static void SyncGuardService(bool autoStartEnabled, string? daemonKitExePath)
    {
        try
        {
            if (autoStartEnabled && !string.IsNullOrWhiteSpace(daemonKitExePath))
            {
                EnsureGuardServiceRunning(daemonKitExePath);
            }
            else
            {
                EnsureGuardServiceStopped();
            }
        }
        catch (Exception ex)
        {
            // 任何异常都不影响 DaemonKit 正常运行
            NLogger.Warn("同步守护服务状态异常: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// 确保守护服务已安装且正在运行（使用指定的自启动路径）。
    /// 当服务已在运行但守护目标路径与当前 DaemonKit 不一致时，
    /// 会自动更新配置并重启服务。
    /// </summary>
    private static void EnsureGuardServiceRunning(string daemonKitExePath)
    {
        if (IsServiceRunning())
        {
            // 服务已在运行 — 检查守护目标路径是否需要更新
            if (TryUpdateGuardTargetIfNeeded(daemonKitExePath))
            {
                NLogger.Info("守护服务目标路径已更新，正在重启服务以加载新配置...");
                RunSc($"stop {ServiceName}");
                Thread.Sleep(2000);
                TryStartService();
            }
            else
            {
                NLogger.Info("DaemonGuard 守护服务已在运行，目标路径一致");
            }
            return;
        }

        if (IsServiceInstalled())
        {
            // 已安装但未运行 — 确保配置正确后启动
            TryUpdateGuardTargetIfNeeded(daemonKitExePath);
            TryStartService();
            return;
        }

        // 服务未安装，尝试自动安装
        string? guardExePath = FindGuardExe();
        if (guardExePath == null)
        {
            NLogger.Info("未找到 DaemonGuard.exe，跳过守护服务自动安装");
            return;
        }

        NLogger.Info("正在自动安装守护服务: {GuardExe}, 守护目标: {Target}", guardExePath, daemonKitExePath);
        if (TryInstallService(guardExePath, daemonKitExePath))
        {
            NLogger.Info("DaemonGuard 守护服务自动安装并启动成功");
        }
        else
        {
            NLogger.Warn("DaemonGuard 守护服务自动安装失败，程序将继续正常运行");
        }
    }

    /// <summary>
    /// 停止守护服务（不卸载，仅停止）。
    /// 当开机自启动被禁用时调用。
    /// </summary>
    private static void EnsureGuardServiceStopped()
    {
        if (!IsServiceInstalled())
            return;

        if (!IsServiceRunning())
        {
            NLogger.Info("DaemonGuard 守护服务未运行，无需停止");
            return;
        }

        NLogger.Info("开机自启动已禁用，正在停止守护服务...");
        int exitCode = RunSc($"stop {ServiceName}");
        if (exitCode == 0)
        {
            NLogger.Info("DaemonGuard 守护服务已停止");
        }
        else
        {
            NLogger.Warn("停止守护服务失败，sc.exe 退出码: {ExitCode}", exitCode);
        }
    }

    /// <summary>
    /// 公开方法：停止守护服务。
    /// 供 App.OnExit 在正常退出时调用，避免 Guard 误重启。
    /// </summary>
    public static void StopService()
    {
        int exitCode = RunSc($"stop {ServiceName}");
        if (exitCode != 0)
        {
            NLogger.Warn("停止守护服务失败，sc.exe 退出码: {ExitCode}", exitCode);
        }
    }

    /// <summary>
    /// 检查 DaemonGuard 服务是否已安装。
    /// </summary>
    public static bool IsServiceInstalled()
    {
        try
        {
            var (exitCode, _) = RunScQuery();
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            NLogger.Warn("检查守护服务是否安装时异常: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 检查 DaemonGuard 服务是否正在运行。
    /// </summary>
    public static bool IsServiceRunning()
    {
        try
        {
            var (exitCode, output) = RunScQuery();
            if (exitCode != 0)
                return false;

            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试启动 DaemonGuard 服务。
    /// </summary>
    private static bool TryStartService()
    {
        try
        {
            NLogger.Info("正在启动 DaemonGuard 守护服务...");
            int exitCode = RunSc($"start {ServiceName}");

            if (exitCode == 0)
            {
                NLogger.Info("DaemonGuard 守护服务启动成功");
                return true;
            }

            NLogger.Warn("启动守护服务失败，sc.exe 退出码: {ExitCode}", exitCode);
            return false;
        }
        catch (Exception ex)
        {
            NLogger.Warn("启动守护服务失败: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 获取守护服务状态的友好描述。
    /// </summary>
    public static string GetServiceStatusText()
    {
        try
        {
            var (exitCode, output) = RunScQuery();
            if (exitCode != 0)
                return "未安装";

            if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return "运行中";
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                return "已停止";
            if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
                return "正在启动";
            if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
                return "正在停止";
            if (output.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
                return "已暂停";

            return "状态未知";
        }
        catch
        {
            return "状态未知";
        }
    }

    /// <summary>
    /// 查找 DaemonGuard.exe 的路径。
    /// 搜索顺序：DaemonKit.exe 同级 Guard\DaemonGuard.exe → 同级 DaemonGuard.exe
    /// </summary>
    private static string? FindGuardExe()
    {
        string baseDir = AppContext.BaseDirectory;

        // 优先查找 Guard 子目录
        string guardInSubDir = Path.Combine(baseDir, GuardSubDir, GuardExeName);
        if (File.Exists(guardInSubDir))
            return guardInSubDir;

        // 兜底：同级目录
        string guardInSameDir = Path.Combine(baseDir, GuardExeName);
        if (File.Exists(guardInSameDir))
            return guardInSameDir;

        return null;
    }

    /// <summary>
    /// 调用 DaemonGuard.exe --install 静默安装服务。
    /// 安装过程中会自动写入 appsettings.json 并启动服务。
    /// </summary>
    private static bool TryInstallService(string guardExePath, string daemonKitExePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = guardExePath,
                Arguments = $"--install \"{daemonKitExePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                NLogger.Warn("启动 DaemonGuard.exe 安装进程失败");
                return false;
            }

            // 等待安装完成，最多 30 秒
            bool exited = process.WaitForExit(30000);
            if (!exited)
            {
                NLogger.Warn("DaemonGuard.exe 安装超时");
                return false;
            }

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                NLogger.Warn(
                    "DaemonGuard.exe 安装退出码: {ExitCode}, 错误: {Error}",
                    process.ExitCode,
                    error
                );
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            NLogger.Warn("调用 DaemonGuard.exe 安装失败: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 执行 sc.exe query 并返回退出码和输出内容。
    /// </summary>
    private static (int exitCode, string output) RunScQuery()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {ServiceName}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, string.Empty);

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        return (process.ExitCode, output);
    }

    /// <summary>
    /// 执行 sc.exe 命令并返回退出码。
    /// </summary>
    private static int RunSc(string arguments)
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

        process.WaitForExit(10000);
        return process.ExitCode;
    }

    /// <summary>
    /// 检查并更新守护服务的目标路径配置。
    /// 如果当前配置中的 TargetExePath 与指定路径不同，则更新 appsettings.json。
    /// </summary>
    /// <returns>true 表示配置已更新，需要重启服务</returns>
    private static bool TryUpdateGuardTargetIfNeeded(string daemonKitExePath)
    {
        try
        {
            string? guardDir = GetServiceBinaryDirectory();
            if (guardDir == null)
            {
                NLogger.Warn("无法获取守护服务安装目录，跳过配置检查");
                return false;
            }

            string settingsPath = Path.Combine(guardDir, "appsettings.json");

            // 检查现有配置中的路径是否一致
            if (File.Exists(settingsPath))
            {
                string content = File.ReadAllText(settingsPath);
                // JSON 中反斜杠是转义的，比较时使用双反斜杠形式
                string escapedPath = daemonKitExePath.Replace("\\", "\\\\");
                if (content.Contains(escapedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // 路径一致，无需更新
                }
            }

            // 写入更新后的配置
            WriteGuardAppSettings(settingsPath, daemonKitExePath);
            NLogger.Info("已更新守护服务配置，新目标: {Target}", daemonKitExePath);
            return true;
        }
        catch (Exception ex)
        {
            NLogger.Warn("更新守护服务配置失败: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 通过 sc qc 查询获取已安装服务的可执行文件所在目录。
    /// </summary>
    private static string? GetServiceBinaryDirectory()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc {ServiceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // 解析 BINARY_PATH_NAME 行
            // 格式: "        BINARY_PATH_NAME   : \"C:\path\to\DaemonGuard.exe\""
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx >= 0)
                    {
                        string rawPath = line[(colonIdx + 1)..].Trim().Replace("\"", "");
                        return Path.GetDirectoryName(rawPath);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            NLogger.Warn("查询服务配置失败: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 写入守护服务的 appsettings.json 配置文件。
    /// </summary>
    private static void WriteGuardAppSettings(string settingsPath, string targetExePath)
    {
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
}
