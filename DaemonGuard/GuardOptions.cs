namespace DaemonGuard;

/// <summary>
/// 守护服务配置选项。
/// </summary>
public class GuardOptions
{
    /// <summary>
    /// DaemonKit.exe 的完整路径。
    /// 默认使用服务所在目录同级的 DaemonKit\DaemonKit.exe。
    /// </summary>
    public string TargetExePath { get; set; } = string.Empty;

    /// <summary>
    /// DaemonKit.exe 的命令行参数。
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// DaemonKit.exe 的工作目录（默认使用 exe 所在目录）。
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 进程存活检测间隔（秒），默认 5 秒。
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 发现进程不存在后，延迟多少秒再重启（避免频繁重启），默认 3 秒。
    /// </summary>
    public int RestartDelaySeconds { get; set; } = 3;

    /// <summary>
    /// 最大连续重启次数，超过后暂停守护并等待冷却，默认 5 次。
    /// </summary>
    public int MaxConsecutiveRestarts { get; set; } = 5;

    /// <summary>
    /// 连续重启达上限后的冷却时间（秒），默认 60 秒。
    /// </summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>
    /// 目标进程名称（不含扩展名），用于进程检测。
    /// </summary>
    public string TargetProcessName { get; set; } = "DaemonKit";

    /// <summary>
    /// 解析并验证配置，自动补全路径。
    /// </summary>
    public void ResolveDefaults()
    {
        if (string.IsNullOrWhiteSpace(TargetExePath))
        {
            // 默认路径：服务 exe 位于 Guard/ 子目录，DaemonKit.exe 在上级目录
            string serviceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string? parentDir = Path.GetDirectoryName(serviceDir);
            if (parentDir != null)
            {
                TargetExePath = Path.Combine(parentDir, "DaemonKit.exe");
            }
            else
            {
                TargetExePath = Path.Combine(serviceDir, "DaemonKit.exe");
            }
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            WorkingDirectory = Path.GetDirectoryName(TargetExePath);
        }
    }
}
