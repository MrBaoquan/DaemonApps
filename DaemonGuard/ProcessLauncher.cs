using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DaemonGuard;

/// <summary>
/// 处理 Session 0 隔离问题，在用户桌面会话中启动 GUI 进程。
/// Windows 服务运行在 Session 0，无法直接显示 UI，
/// 需要通过 WTSQueryUserToken + CreateProcessAsUser 在用户会话中创建进程。
/// 支持 requireAdministrator manifest 的目标程序（通过 LinkedToken 获取提权令牌）。
/// </summary>
public static class ProcessLauncher
{
    #region Win32 Constants

    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint MAXIMUM_ALLOWED = 0x02000000;

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    private const int SW_SHOW = 5;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenLinkedToken = 19</summary>
    private const int TokenLinkedToken = 19;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenSessionId = 12</summary>
    private const int TokenSessionId = 12;

    #endregion

    #region Win32 Structures

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    #endregion

    #region Win32 Imports

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken
    );

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        int TokenInformationLength,
        out int ReturnLength
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        ref uint TokenInformation,
        int TokenInformationLength
    );

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        IntPtr hToken,
        bool bInherit
    );

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion

    /// <summary>
    /// 在当前活跃的用户桌面会话中启动指定的可执行文件。
    /// 优先使用已有的 DaemonKit 计划任务（最可靠，自动处理 UAC 和会话隔离），
    /// 降级到 schtasks 创建临时任务，最后使用 CreateProcessAsUser。
    /// </summary>
    /// <param name="exePath">可执行文件完整路径</param>
    /// <param name="arguments">命令行参数（可选）</param>
    /// <param name="workingDirectory">工作目录（可选，默认使用 exe 所在目录）</param>
    /// <returns>启动的进程 ID，成功但无法获取 PID 时返回 0</returns>
    public static int LaunchInUserSession(
        string exePath,
        string? arguments = null,
        string? workingDirectory = null
    )
    {
        // 方案 A：通过已注册的 DaemonKit 计划任务启动
        //         这是 DaemonKit 自己注册的登录时自启动任务，运行身份和权限完全正确
        try
        {
            int pid = LaunchViaExistingScheduledTask(exePath);
            if (pid >= 0)
                return pid;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DaemonGuard] 方案A(已有计划任务)失败: {ex.Message}");
        }

        // 方案 B：降级到 CreateProcessAsUser
        return LaunchViaCreateProcessAsUser(exePath, arguments, workingDirectory);
    }

    /// <summary>
    /// 通过已注册的 "DaemonKit" 计划任务启动进程。
    /// 该任务由 DaemonKit 的 SyncSettings 注册，配置为：
    ///   - 以 Administrator 身份运行
    ///   - 仅在交互模式下运行
    ///   - 最高权限
    /// 从 Windows 服务中运行此任务可以完美解决 Session 0 隔离问题。
    /// </summary>
    private static int LaunchViaExistingScheduledTask(string exePath)
    {
        const string taskName = "DaemonKit";
        string processName = Path.GetFileNameWithoutExtension(exePath);

        // 检查任务是否存在
        int queryResult = RunProcess("schtasks.exe", $"/query /tn \"{taskName}\"", timeoutMs: 5000);
        if (queryResult != 0)
        {
            throw new InvalidOperationException($"计划任务 '{taskName}' 不存在");
        }

        // 运行任务
        int runResult = RunProcess("schtasks.exe", $"/run /tn \"{taskName}\"", timeoutMs: 10000);
        if (runResult != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /run /tn \"{taskName}\" 失败，退出码: {runResult}"
            );
        }

        // 等待进程出现
        Thread.Sleep(2000);
        var processes = Process.GetProcessesByName(processName);
        int pid = 0;
        if (processes.Length > 0)
        {
            pid = processes[0].Id;
        }
        foreach (var p in processes)
            p.Dispose();

        return pid;
    }

    /// <summary>
    /// 执行一个进程并等待其退出，返回退出码。
    /// </summary>
    private static int RunProcess(string fileName, string arguments, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return -1;

        process.WaitForExit(timeoutMs);
        return process.ExitCode;
    }

    /// <summary>
    /// 通过 CreateProcessAsUser 在用户会话中启动进程（降级方案）。
    /// </summary>
    private static int LaunchViaCreateProcessAsUser(
        string exePath,
        string? arguments = null,
        string? workingDirectory = null
    )
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr linkedToken = IntPtr.Zero;
        IntPtr duplicateToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            // 1. 获取活跃的控制台 Session ID
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                throw new InvalidOperationException("无法获取活跃的用户会话，可能没有用户登录。");
            }

            // 2. 获取该 Session 的用户令牌
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"WTSQueryUserToken 失败 (Session {sessionId})，错误码: {error}"
                );
            }

            // 3. 获取 LinkedToken（提权令牌）以支持 requireAdministrator 的目标程序
            //    WTSQueryUserToken 返回的是 UAC 过滤后的标准令牌，
            //    需要通过 TokenLinkedToken 获取对应的管理员令牌。
            IntPtr tokenForDuplication = userToken;
            IntPtr linkedTokenBuffer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (
                    GetTokenInformation(
                        userToken,
                        TokenLinkedToken,
                        linkedTokenBuffer,
                        IntPtr.Size,
                        out _
                    )
                )
                {
                    linkedToken = Marshal.ReadIntPtr(linkedTokenBuffer);
                    if (linkedToken != IntPtr.Zero)
                    {
                        tokenForDuplication = linkedToken;
                        System.Diagnostics.Trace.WriteLine(
                            $"[DaemonGuard] LinkedToken 获取成功: 0x{linkedToken:X}"
                        );
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    // ERROR_NO_SUCH_LOGON_SESSION(1312) = UAC 未启用或无链接令牌，使用原始令牌即可
                    System.Diagnostics.Trace.WriteLine(
                        $"[DaemonGuard] GetTokenInformation(LinkedToken) 失败，错误码: {err}，将使用原始用户令牌"
                    );
                }
            }
            finally
            {
                Marshal.FreeHGlobal(linkedTokenBuffer);
            }

            // 4. 复制令牌为主令牌
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = false
            };

            if (
                !DuplicateTokenEx(
                    tokenForDuplication,
                    MAXIMUM_ALLOWED,
                    ref sa,
                    SecurityImpersonation,
                    TokenPrimary,
                    out duplicateToken
                )
            )
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"DuplicateTokenEx 失败，错误码: {error}");
            }

            // 4.5 将复制令牌的 Session ID 设置为用户会话（关键！）
            //     DuplicateTokenEx 可能将新令牌分配到服务所在的 Session 0，
            //     必须显式设置为用户的控制台 Session 才能访问交互式桌面。
            if (!SetTokenInformation(duplicateToken, TokenSessionId, ref sessionId, sizeof(uint)))
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Trace.WriteLine(
                    $"[DaemonGuard] SetTokenInformation(SessionId={sessionId}) 失败，错误码: {error}"
                );
                // 不抛异常，继续尝试创建进程
            }

            // 5. 创建用户环境变量块
            if (!CreateEnvironmentBlock(out environment, duplicateToken, false))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"CreateEnvironmentBlock 失败，错误码: {error}");
            }

            // 6. 准备启动信息
            string? workDir = workingDirectory ?? Path.GetDirectoryName(exePath);
            string commandLine = string.IsNullOrEmpty(arguments)
                ? $"\"{exePath}\""
                : $"\"{exePath}\" {arguments}";

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default", // 指向用户桌面
                dwFlags = 0x00000001, // STARTF_USESHOWWINDOW
                wShowWindow = SW_SHOW
            };

            var processAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = false
            };

            var threadAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = false
            };

            // 7. 在用户会话中创建进程
            uint creationFlags =
                NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            if (
                !CreateProcessAsUser(
                    duplicateToken,
                    null,
                    commandLine,
                    ref processAttributes,
                    ref threadAttributes,
                    false,
                    creationFlags,
                    environment,
                    workDir,
                    ref si,
                    out PROCESS_INFORMATION pi
                )
            )
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"CreateProcessAsUser 失败，错误码: {error}");
            }

            // 8. 清理进程和线程句柄，返回 PID
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            return pi.dwProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            if (duplicateToken != IntPtr.Zero)
                CloseHandle(duplicateToken);
            if (linkedToken != IntPtr.Zero)
                CloseHandle(linkedToken);
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
        }
    }

    /// <summary>
    /// 检查是否有活跃的用户桌面会话。
    /// </summary>
    public static bool HasActiveUserSession()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        return sessionId != 0xFFFFFFFF;
    }
}
