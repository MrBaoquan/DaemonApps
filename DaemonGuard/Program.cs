using DaemonGuard;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

// ============================================================
//  命令行参数处理：安装 / 卸载 / 直接运行
// ============================================================
if (args.Length > 0)
{
    string command = args[0].ToLower().TrimStart('-', '/');

    switch (command)
    {
        case "install":
            ServiceInstaller.Install(args);
            return;
        case "uninstall":
        case "remove":
            ServiceInstaller.Uninstall();
            return;
        case "status":
            ServiceInstaller.ShowStatus();
            return;
        case "help":
        case "?":
            PrintHelp();
            return;
    }
}

// ============================================================
//  构建并运行 Windows 服务宿主
// ============================================================
var builder = Host.CreateApplicationBuilder(args);

// 配置为 Windows 服务
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceInstaller.ServiceName;
});

// 配置日志
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddEventLog(
    new EventLogSettings { SourceName = ServiceInstaller.ServiceName, LogName = "Application" }
);

// 确保 EventLog 也输出 Information 级别（默认可能为 Warning）
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Information);

// 绑定配置节 "Guard"
builder.Services.Configure<GuardOptions>(builder.Configuration.GetSection("Guard"));

// 注册守护工作线程
builder.Services.AddHostedService<GuardWorker>();

var host = builder.Build();
host.Run();

// ============================================================
//  帮助信息
// ============================================================
static void PrintHelp()
{
    Console.WriteLine(
        """
    DaemonGuard - DaemonKit 专用进程守护服务
    
    用法:
      DaemonGuard.exe                    以控制台模式运行（调试用）
      DaemonGuard.exe --install          安装为 Windows 服务
      DaemonGuard.exe --install <path>   安装并指定 DaemonKit.exe 路径
      DaemonGuard.exe --uninstall        卸载 Windows 服务
      DaemonGuard.exe --status           查看服务状态
      DaemonGuard.exe --help             显示此帮助
    
    配置:
      可通过 appsettings.json 的 "Guard" 节配置选项:
        TargetExePath          - DaemonKit.exe 路径
        Arguments              - 启动参数
        WorkingDirectory       - 工作目录
        CheckIntervalSeconds   - 检测间隔（默认 5s）
        RestartDelaySeconds    - 重启延迟（默认 3s）
        MaxConsecutiveRestarts - 最大连续重启次数（默认 5）
        CooldownSeconds        - 冷却时间（默认 60s）
        TargetProcessName      - 进程名（默认 DaemonKit）
    
    SCM 故障恢复:
      安装时自动配置 SCM 故障策略：前三次失败均自动重启服务。
    """
    );
}
