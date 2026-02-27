using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Core
{
    class ProcManager
    {
        /// <summary>
        /// 检测文件扩展名是否为脚本类型
        /// </summary>
        private static bool IsScriptFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".vbs";
        }

        /// <summary>
        /// 规范化批处理脚本文件的编码，确保 cmd.exe / powershell.exe 5.1 能正确解析：
        /// - .bat/.cmd：将 LF 换行符转换为 CRLF（中文 Windows GBK 代码页下 LF 会导致多字节字符跨行错位）
        /// - .ps1：确保文件包含 UTF-8 BOM（PowerShell 5.1 无 BOM 时按系统 ANSI 解码，导致中文解析失败）
        /// </summary>
        private static void NormalizeScriptEncoding(string path)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var bytes = System.IO.File.ReadAllBytes(path);

                if (ext == ".bat" || ext == ".cmd")
                {
                    // 检测是否存在 LF-only 换行符
                    bool hasLfOnly = false;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        if (bytes[i] == 0x0A && (i == 0 || bytes[i - 1] != 0x0D))
                        {
                            hasLfOnly = true;
                            break;
                        }
                    }

                    if (hasLfOnly)
                    {
                        NLogger.Warn("[进程] 批处理文件使用 LF 换行符，自动转换为 CRLF: {0}", path);
                        var content = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                        content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
                        System.IO.File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
                    }
                }
                else if (ext == ".ps1")
                {
                    // 检测是否缺少 UTF-8 BOM（0xEF 0xBB 0xBF）
                    bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    if (!hasBom)
                    {
                        NLogger.Warn("[进程] PS1 文件缺少 UTF-8 BOM，自动补充（PowerShell 5.1 兼容）: {0}", path);
                        var content = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                        System.IO.File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("[进程] 脚本文件编码规范化失败: {0}, {1}", path, ex.Message);
            }
        }

        public static bool KeepTopWindow(
            string ProcessFileName,
            int posX = 0,
            int posY = 0,
            int width = 0,
            int height = 0,
            int topMost = (int)HWndInsertAfter.HWND_TOPMOST
        )
        {
            var _process = WinAPI.FindProcess(ProcessFileName);
            return KeepTopWindow(_process, posX, posY, width, height, topMost);
        }

        public static bool KeepTopWindow(
            Process process,
            int posX = 0,
            int posY = 0,
            int width = 0,
            int height = 0,
            int topMost = (int)HWndInsertAfter.HWND_TOPMOST
        )
        {
            if (process == default(Process))
                return false;
            return KeepTopWindow(process.MainWindowHandle, posX, posY, width, height, topMost);
        }

        public static bool KeepTopWindow(
            IntPtr handle,
            int posX = 0,
            int posY = 0,
            int width = 0,
            int height = 0,
            int topMost = (int)HWndInsertAfter.HWND_TOPMOST
        )
        {
            if (handle == IntPtr.Zero)
                return false;
            //WinAPI.SetWindowLong (_process.MainWindowHandle, (int) SetWindowLongIndex.GWL_STYLE, (UInt32) GWL_STYLE.WS_POPUP);
            var _noMove = posX == posY && posX == 0 ? SetWindowPosFlags.SWP_NOMOVE : 0x00;
            var _noSize = width == height && width == 0 ? SetWindowPosFlags.SWP_NOSIZE : 0x00;

            WinAPI.SetWindowPos(
                handle,
                topMost,
                posX,
                posY,
                width,
                height,
                SetWindowPosFlags.SWP_SHOWWINDOW
                    | _noMove
                    | _noSize
                    | SetWindowPosFlags.SWP_FRAMECHANGED
            );
            //WinAPI.SetFocus (handle);
            return true;
        }

        public static bool IsWindowTopMost(string ProcessFileName)
        {
            var _process = WinAPI.FindProcess(ProcessFileName);
            if (_process == default(Process))
                return false;
            return WinAPI.IsWindowTopMost(_process.MainWindowHandle);
        }

        // 守护进程
        public static void DaemonProcess(
            string Path,
            ProcessMetaData metaData,
            Action<Process> onStarted = null
        )
        {
            try
            {
                // 参数验证
                if (string.IsNullOrWhiteSpace(Path))
                {
                    NLogger.Error("进程路径为空，无法启动进程");
                    return;
                }

                if (!System.IO.Path.IsPathRooted(Path))
                {
                    NLogger.Error("进程路径必须为绝对路径: {Path}", Path);
                    return;
                }

                if (!System.IO.File.Exists(Path))
                {
                    NLogger.Error("进程文件不存在: {Path}", Path);
                    return;
                }

                if (metaData == null)
                {
                    NLogger.Error("进程元数据为空: {Path}", Path);
                    return;
                }

                NLogger.Info(
                    "[进程] 启动: {0}, 参数: {1}, 管理员: {2}",
                    Path,
                    metaData.Arguments,
                    metaData.RunAs
                );

                Process _process = null;

                try
                {
                    _process = new Process();

                    // 检测是否为脚本文件
                    bool isScript = IsScriptFile(Path);

                    // 解析脚本宿主：.ps1 → powershell.exe, .vbs → cscript.exe, .bat/.cmd → cmd.exe
                    string fileName = Path;
                    string arguments = metaData.Arguments ?? string.Empty;
                    if (isScript)
                    {
                        var ext = System.IO.Path.GetExtension(Path).ToLowerInvariant();
                        switch (ext)
                        {
                            case ".ps1":
                                // 确保 UTF-8 BOM 存在（PowerShell 5.1 在中文 Windows 无 BOM 时按 GBK 解码导致中文解析失败）
                                NormalizeScriptEncoding(Path);
                                fileName = "powershell.exe";
                                // -ExecutionPolicy Bypass 确保脚本可执行；-File 指定脚本路径
                                // 用户自定义参数追加在脚本路径之后
                                arguments = string.IsNullOrEmpty(arguments)
                                    ? $"-ExecutionPolicy Bypass -NoProfile -File \"{Path}\""
                                    : $"-ExecutionPolicy Bypass -NoProfile -File \"{Path}\" {arguments}";
                                NLogger.Info("[进程] PowerShell 脚本，使用 powershell.exe 启动: {0}", Path);
                                break;
                            case ".vbs":
                                fileName = "cscript.exe";
                                arguments = string.IsNullOrEmpty(arguments)
                                    ? $"//Nologo \"{Path}\""
                                    : $"//Nologo \"{Path}\" {arguments}";
                                NLogger.Info("[进程] VBS 脚本，使用 cscript.exe 启动: {0}", Path);
                                break;
                            case ".bat":
                            case ".cmd":
                                // 规范化换行符：LF → CRLF（中文 Windows 下 cmd.exe 解析 LF 批处理会出错）
                                NormalizeScriptEncoding(Path);
                                fileName = "cmd.exe";
                                arguments = string.IsNullOrEmpty(arguments)
                                    ? $"/c \"{Path}\""
                                    : $"/c \"{Path}\" {arguments}";
                                NLogger.Info("[进程] 批处理脚本，使用 cmd.exe 启动: {0}", Path);
                                break;
                        }
                    }

                    _process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        // 脚本文件显示命令窗口，其他程序隐藏
                        CreateNoWindow = !isScript,
                        UseShellExecute = false,
                        Verb = metaData.RunAs ? "runas" : "",
                        WorkingDirectory =
                            System.IO.Path.GetDirectoryName(Path) ?? Environment.CurrentDirectory,
                        WindowStyle = metaData.MinimizedStartUp
                            ? ProcessWindowStyle.Minimized
                            : ProcessWindowStyle.Normal
                    };

                    bool started = _process.Start();

                    if (!started)
                    {
                        NLogger.Error("[进程] 启动失败 (Start 返回 false): {0}", Path);
                        return;
                    }

                    NLogger.Info("[进程] 已启动: {0} (PID: {1})", Path, _process.Id);

                    if (isScript)
                    {
                        // 脚本进程：跳过 WaitForInputIdle（控制台无消息循环，会抛 InvalidOperationException）
                        // 直接回调 onStarted，确保 nodeProcess/nodeProcessId 在守护循环首次检查前就已赋值
                        // 避免短生命周期脚本在调度回 MainThread 前就退出导致 onStarted 永不执行
                        NLogger.Info("[进程] 脚本进程跳过 WaitForInputIdle，直接就绪: {0} (PID: {1})", Path, _process.Id);
                        try
                        {
                            onStarted?.Invoke(_process);
                        }
                        catch (Exception ex)
                        {
                            NLogger.Error("执行 onStarted 回调异常: {0}, 错误: {1}\n{2}", Path, ex.Message, ex.StackTrace);
                        }
                    }
                    else
                    {
                        // 普通程序：异步等待输入就绪
                        Observable
                            .Start(() =>
                            {
                                try
                                {
                                    _process.WaitForInputIdle(10000); // 10秒超时
                                }
                                catch (InvalidOperationException ex)
                                {
                                    NLogger.Warn("进程无用户界面或已退出: {0}, 错误: {1}", Path, ex.Message);
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Error("等待进程就绪异常: {0}, 错误: {1}", Path, ex.Message);
                                }
                                return _process;
                            })
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(
                                process =>
                                {
                                    try
                                    {
                                        if (onStarted != null && process != null && !process.HasExited)
                                        {
                                            onStarted(process);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        NLogger.Error(
                                            "执行 onStarted 回调异常: {0}, 错误: {1}\n{2}",
                                            Path,
                                            ex.Message,
                                            ex.StackTrace
                                        );
                                    }
                                },
                                error =>
                                {
                                    NLogger.Error(
                                        "异步处理进程异常: {0}, 错误: {1}\n{2}",
                                        Path,
                                        error.Message,
                                        error.StackTrace
                                    );
                                }
                            );
                    } // end else (non-script)
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    NLogger.Error(
                        "启动进程失败 (Win32错误): {0}, 错误码: {1}, 错误: {2}",
                        Path,
                        ex.NativeErrorCode,
                        ex.Message
                    );
                    if (_process != null)
                    {
                        try
                        {
                            _process.Dispose();
                        }
                        catch { }
                    }
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    NLogger.Error("启动进程失败 (权限不足): {0}, 错误: {1}", Path, ex.Message);
                    if (_process != null)
                    {
                        try
                        {
                            _process.Dispose();
                        }
                        catch { }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    NLogger.Error(
                        "启动进程失败 (未知异常): {0}, 错误: {1}\n{2}",
                        Path,
                        ex.Message,
                        ex.StackTrace
                    );
                    if (_process != null)
                    {
                        try
                        {
                            _process.Dispose();
                        }
                        catch { }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("守护进程函数异常: {0}, 错误: {1}\n{2}", Path, ex.Message, ex.StackTrace);
            }
        }

        public static bool IsProcessExists(string Path)
        {
            var exists = WinAPI.FindProcess(Path) != default(Process);
            // 守护检查频繁，使用Trace级别减少日志噪音
            // NLogger.Debug("检查进程是否存在: {0} -> {1}", Path, exists);
            return exists;
        }

        public static void KillProcess(string Path)
        {
            var processes = WinAPI.FindProcesses(Path);
            NLogger.Debug("准备终止进程: {0}, 找到 {1} 个进程", Path, processes.Count);
            processes.ForEach(p =>
            {
                try
                {
                    NLogger.Info("正在终止进程: {0} (PID: {1})", Path, p.Id);
                    p.Kill(true); // Kill entire process tree (子进程一并终止)
                    NLogger.Info("已终止进程: {0} (PID: {1})", Path, p.Id);
                }
                catch (Exception ex)
                {
                    NLogger.Error("终止进程失败: {0} (PID: {1}), 错误: {2}", Path, p.Id, ex.Message);
                }
            });
            if (processes.Count == 0)
            {
                NLogger.Warn("未找到需要终止的进程: {0}", Path);
            }
        }

        public static void KillProcess(string Path, int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (process == null)
                {
                    NLogger.Warn("未找到需要终止的进程: {0} (PID: {1})", Path, pid);
                    return;
                }

                // 检测是否为脚本文件
                bool isScript = IsScriptFile(Path);

                // 对于非脚本文件，验证路径是否匹配
                if (!isScript)
                {
                    var exePath = string.Empty;
                    try
                    {
                        exePath = process.MainModule?.FileName ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        NLogger.Debug("无法获取进程模块路径 (PID: {Pid}): {Message}", pid, ex.Message);
                    }

                    if (
                        !string.IsNullOrEmpty(exePath)
                        && !exePath.Equals(Path, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        NLogger.Warn("进程路径不匹配，跳过终止: 期望 {0}, 实际 {1} (PID: {2})", Path, exePath, pid);
                        return;
                    }
                }
                else
                {
                    NLogger.Info("检测到脚本进程，直接使用 PID 终止: {0} (PID: {1})", Path, pid);
                }

                NLogger.Info("正在终止进程: {0} (PID: {1})", Path, pid);
                process.Kill(true); // Kill entire process tree (子进程一并终止)
                NLogger.Info("已终止进程: {0} (PID: {1})", Path, pid);
            }
            catch (Exception ex)
            {
                NLogger.Error("终止进程失败: {0} (PID: {1}), 错误: {2}", Path, pid, ex.Message);
            }
        }

        /// <summary>
        /// 安全关闭进程 - 发送WM_CLOSE消息请求关闭，超时后强制终止
        /// </summary>
        /// <param name="Path">进程路径</param>
        /// <param name="timeoutMs">超时时间(毫秒)，默认5000ms</param>
        /// <returns>是否成功关闭</returns>
        public static async Task<bool> SafeKillProcess(string Path, int timeoutMs = 5000)
        {
            const int WM_CLOSE = 0x0010;
            var process = WinAPI.FindProcess(Path);

            if (process == default(Process))
            {
                NLogger.Warn("进程未找到: {0}", Path);
                return false;
            }

            try
            {
                // 发送WM_CLOSE消息
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    WinAPI.PostMessage(
                        process.MainWindowHandle,
                        WM_CLOSE,
                        IntPtr.Zero,
                        IntPtr.Zero
                    );
                    NLogger.Info("已向进程发送关闭消息: {0}", Path);

                    // 等待进程退出
                    var waitTask = Task.Run(() => process.WaitForExit(timeoutMs));
                    bool exited = await waitTask;

                    if (exited)
                    {
                        NLogger.Info("进程已正常退出: {0}", Path);
                        return true;
                    }
                    else
                    {
                        NLogger.Warn("进程未在 {0}ms 内响应关闭消息，执行强制终止: {1}", timeoutMs, Path);
                    }
                }
                else
                {
                    NLogger.Warn("进程无主窗口句柄，无法发送WM_CLOSE，执行强制终止: {0}", Path);
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("安全关闭进程失败: {0}, 错误: {1}", Path, ex.Message);
            }

            // 超时或失败则强制终止
            KillProcess(Path);
            return true;
        }

        public static async Task<bool> SafeKillProcess(string Path, int pid, int timeoutMs = 5000)
        {
            const int WM_CLOSE = 0x0010;
            Process process = null;

            try
            {
                process = Process.GetProcessById(pid);
            }
            catch
            {
                NLogger.Warn("进程未找到: {0} (PID: {1})", Path, pid);
                return false;
            }

            // 脚本文件：宿主进程为 cmd.exe/powershell.exe，路径匹配必然失败
            // 直接使用 PID 终止，跳过路径校验和 WM_CLOSE（控制台窗口不可靠响应 WM_CLOSE）
            bool isScript = IsScriptFile(Path);
            if (isScript)
            {
                NLogger.Info("[进程] 脚本进程安全关闭，直接使用 PID 终止进程树: {0} (PID: {1})", Path, pid);
                try
                {
                    process.Kill(true);
                    NLogger.Info("已终止脚本进程树: {0} (PID: {1})", Path, pid);
                }
                catch (Exception ex)
                {
                    NLogger.Error("终止脚本进程失败: {0} (PID: {1}), 错误: {2}", Path, pid, ex.Message);
                }
                return true;
            }

            var exePath = string.Empty;
            try
            {
                exePath = process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception ex)
            {
                NLogger.Debug("无法获取进程模块路径 (PID: {Pid}): {Message}", pid, ex.Message);
            }

            if (
                !string.IsNullOrEmpty(exePath)
                && !exePath.Equals(Path, StringComparison.OrdinalIgnoreCase)
            )
            {
                NLogger.Warn("进程路径不匹配，跳过安全关闭: 期望 {0}, 实际 {1} (PID: {2})", Path, exePath, pid);
                return false;
            }

            try
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    WinAPI.PostMessage(
                        process.MainWindowHandle,
                        WM_CLOSE,
                        IntPtr.Zero,
                        IntPtr.Zero
                    );
                    NLogger.Info("已向进程发送关闭消息: {0} (PID: {1})", Path, pid);

                    var waitTask = Task.Run(() => process.WaitForExit(timeoutMs));
                    bool exited = await waitTask;

                    if (exited)
                    {
                        NLogger.Info("进程已正常退出: {0} (PID: {1})", Path, pid);
                        return true;
                    }
                    else
                    {
                        NLogger.Warn(
                            "进程未在 {0}ms 内响应关闭消息，执行强制终止: {1} (PID: {2})",
                            timeoutMs,
                            Path,
                            pid
                        );
                    }
                }
                else
                {
                    NLogger.Warn("进程无主窗口句柄，无法发送WM_CLOSE，执行强制终止: {0} (PID: {1})", Path, pid);
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("安全关闭进程失败: {0} (PID: {1}), 错误: {2}", Path, pid, ex.Message);
            }

            KillProcess(Path, pid);
            return true;
        }

        /// <summary>
        /// 同步温和关闭所有匹配路径的残留进程
        /// 对每个匹配进程发送 WM_CLOSE，等待超时后强制终止
        /// 注意：此方法会阻塞调用线程（最多 timeoutMs），应在线程池调用，禁止在 UI 线程调用
        /// </summary>
        /// <param name="Path">进程可执行文件路径</param>
        /// <param name="timeoutMs">每个进程的超时时间(毫秒)</param>
        /// <returns>清理的进程数量</returns>
        public static int KillAllResidualProcesses(string Path, int timeoutMs = 3000)
        {
            const int WM_CLOSE = 0x0010;
            var processes = WinAPI.FindProcesses(Path);
            if (processes.Count == 0)
                return 0;

            NLogger.Info("[残留清理] 发现 {Count} 个残留进程: {Path}", processes.Count, Path);

            // 先向所有进程发送 WM_CLOSE
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        WinAPI.PostMessage(proc.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        NLogger.Info("[残留清理] 已发送 WM_CLOSE: PID={Pid}", proc.Id);
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Debug("[残留清理] 发送 WM_CLOSE 失败: PID={Pid}, {Msg}", proc.Id, ex.Message);
                }
            }

            // 等待所有进程退出（阻塞当前线程）
            foreach (var proc in processes)
            {
                try
                {
                    proc.WaitForExit(timeoutMs);
                }
                catch { }
            }

            // 强杀仍存活的进程
            int killed = 0;
            foreach (var proc in processes)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        NLogger.Warn("[残留清理] 进程未在 {Timeout}ms 内退出，强制终止: PID={Pid}", timeoutMs, proc.Id);
                        proc.Kill(true); // Kill entire process tree
                    }
                    killed++;
                }
                catch (Exception ex)
                {
                    NLogger.Debug("[残留清理] 终止进程异常: PID={Pid}, {Msg}", proc.Id, ex.Message);
                }
            }

            NLogger.Info("[残留清理] 清理完成: {Path}, 共清理 {Count} 个进程", Path, killed);
            return killed;
        }

        /// <summary>
        /// 通过 WMI 命令行匹配，清理脚本宿主进程（cmd.exe / powershell.exe / cscript.exe）
        /// 用于 DaemonKit 异常退出后重启时清理上一次遗留的脚本进程
        /// 注意：此方法会阻塞调用线程，应在线程池调用
        /// </summary>
        /// <param name="scriptPath">脚本文件的绝对路径（.bat/.cmd/.ps1/.vbs）</param>
        /// <returns>清理的进程数量</returns>
        public static int KillScriptResidualProcesses(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath) || !IsScriptFile(scriptPath))
                return 0;

            int killed = 0;
            try
            {
                // 将路径中的反斜杠转义为 WMI 查询所需的双反斜杠
                var escapedPath = scriptPath.Replace("\\", "\\\\");
                var query = $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%{escapedPath}%'";

                using var searcher = new System.Management.ManagementObjectSearcher(query);
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    var cmdLine = obj["CommandLine"]?.ToString() ?? string.Empty;

                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        NLogger.Info("[残留清理] 发现脚本残留进程: PID={Pid}, CommandLine={CmdLine}", pid, cmdLine);
                        proc.Kill(true); // Kill entire process tree
                        NLogger.Info("[残留清理] 已终止脚本残留进程: PID={Pid}", pid);
                        killed++;
                    }
                    catch (ArgumentException)
                    {
                        // 进程已退出
                    }
                    catch (Exception ex)
                    {
                        NLogger.Debug("[残留清理] 终止脚本残留进程异常: PID={Pid}, {Msg}", pid, ex.Message);
                    }
                }

                if (killed > 0)
                {
                    NLogger.Info("[残留清理] 脚本残留清理完成: {Path}, 共清理 {Count} 个进程", scriptPath, killed);
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("[残留清理] WMI 查询脚本残留进程异常: {Path}, {Msg}", scriptPath, ex.Message);
            }

            return killed;
        }

        /// <summary>
        /// 根据配置决定使用安全关闭还是强制终止
        /// </summary>
        public static async Task KillProcess(string Path, bool useSafeKill, int timeoutMs = 5000)
        {
            if (useSafeKill)
            {
                await SafeKillProcess(Path, timeoutMs);
            }
            else
            {
                KillProcess(Path);
            }
        }

        public static async Task KillProcess(
            string Path,
            int pid,
            bool useSafeKill,
            int timeoutMs = 5000
        )
        {
            if (useSafeKill)
            {
                await SafeKillProcess(Path, pid, timeoutMs);
            }
            else
            {
                KillProcess(Path, pid);
            }
        }
    }
}
