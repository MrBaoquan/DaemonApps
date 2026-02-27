using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DaemonGuard;

/// <summary>
/// 守护工作线程 - 定期检测 DaemonKit.exe 是否存活，如不存在则在用户会话中重启。
/// </summary>
public class GuardWorker : BackgroundService
{
    private readonly ILogger<GuardWorker> _logger;
    private readonly GuardOptions _options;

    private int _consecutiveRestarts;
    private DateTime _lastRestartTime = DateTime.MinValue;

    public GuardWorker(ILogger<GuardWorker> logger, IOptions<GuardOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _options.ResolveDefaults();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DaemonGuard 守护服务已启动，目标: {TargetExe}, 检测间隔: {Interval}s",
            _options.TargetExePath,
            _options.CheckIntervalSeconds
        );

        // 启动后等待一小段时间，给系统启动过程留出余量
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndGuardAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "守护检测循环发生异常");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("DaemonGuard 守护服务已停止");
    }

    private async Task CheckAndGuardAsync(CancellationToken stoppingToken)
    {
        // 1. 检测目标进程是否存活
        bool isRunning = IsTargetProcessRunning();

        if (isRunning)
        {
            // 进程正常运行，重置连续重启计数
            if (_consecutiveRestarts > 0)
            {
                _logger.LogWarning("目标进程已恢复运行，重置连续重启计数 ({Count} → 0)", _consecutiveRestarts);
                _consecutiveRestarts = 0;
            }
            return;
        }

        // 2. 进程不存在 - 检查是否处于冷却期
        if (_consecutiveRestarts >= _options.MaxConsecutiveRestarts)
        {
            TimeSpan elapsed = DateTime.Now - _lastRestartTime;
            if (elapsed.TotalSeconds < _options.CooldownSeconds)
            {
                _logger.LogWarning(
                    "连续重启已达上限 ({Max} 次)，冷却中... 剩余 {Remaining:F0}s",
                    _options.MaxConsecutiveRestarts,
                    _options.CooldownSeconds - elapsed.TotalSeconds
                );
                return;
            }

            // 冷却期结束，重置计数
            _logger.LogInformation("冷却期已过，重置连续重启计数");
            _consecutiveRestarts = 0;
        }

        // 3. 检查是否有用户登录
        if (!ProcessLauncher.HasActiveUserSession())
        {
            _logger.LogWarning("目标进程未运行，但当前没有活跃的用户会话，等待用户登录...");
            return;
        }

        // 4. 检查目标 exe 文件是否存在
        if (!File.Exists(_options.TargetExePath))
        {
            _logger.LogError("目标可执行文件不存在: {Path}", _options.TargetExePath);
            return;
        }

        // 5. 延迟重启（避免瞬间重启）
        _logger.LogWarning(
            "检测到 {ProcessName} 未运行，将在 {Delay}s 后重启 (连续第 {Count} 次)",
            _options.TargetProcessName,
            _options.RestartDelaySeconds,
            _consecutiveRestarts + 1
        );

        await Task.Delay(TimeSpan.FromSeconds(_options.RestartDelaySeconds), stoppingToken);

        // 再次检查，可能在延迟期间进程已被其他方式启动
        if (IsTargetProcessRunning())
        {
            _logger.LogWarning("延迟期间目标进程已启动，取消本次重启");
            return;
        }

        // 6. 在用户会话中启动进程
        try
        {
            _logger.LogWarning(
                "正在尝试启动 {ProcessName}: {ExePath}",
                _options.TargetProcessName,
                _options.TargetExePath
            );

            int pid = ProcessLauncher.LaunchInUserSession(
                _options.TargetExePath,
                _options.Arguments,
                _options.WorkingDirectory
            );

            _consecutiveRestarts++;
            _lastRestartTime = DateTime.Now;

            _logger.LogWarning(
                "已在用户会话中重启 {ProcessName}，PID: {Pid} (连续第 {Count} 次)",
                _options.TargetProcessName,
                pid,
                _consecutiveRestarts
            );

            // 等待 3 秒后验证进程是否存活
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            bool alive = false;
            int exitCode = 0;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    exitCode = proc.ExitCode;
                }
                else
                {
                    alive = true;
                }
            }
            catch
            { /* 进程可能已不存在 */
            }

            if (!alive)
            {
                _logger.LogWarning(
                    "进程 PID={Pid} 在启动后 3 秒内已退出，退出码: 0x{ExitCode:X} ({ExitCodeDec})，可能存在启动失败",
                    pid,
                    exitCode,
                    exitCode
                );
            }
            else
            {
                _logger.LogWarning("进程 PID={Pid} 启动后 3 秒仍在运行，启动成功", pid);
            }
        }
        catch (Exception ex)
        {
            _consecutiveRestarts++;
            _lastRestartTime = DateTime.Now;

            _logger.LogError(
                ex,
                "在用户会话中启动 {ProcessName} 失败 (连续第 {Count} 次)",
                _options.TargetProcessName,
                _consecutiveRestarts
            );
        }
    }

    /// <summary>
    /// 检测目标进程是否正在运行。
    /// </summary>
    private bool IsTargetProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(_options.TargetProcessName);
            bool running = processes.Length > 0;

            // 释放进程句柄
            foreach (var p in processes)
            {
                p.Dispose();
            }

            return running;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检测进程 {ProcessName} 时发生异常", _options.TargetProcessName);
            return false;
        }
    }
}
