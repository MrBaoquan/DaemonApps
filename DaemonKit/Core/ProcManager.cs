using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Core
{
    class ProcManager
    {
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
                    NLogger.Error($"进程路径必须为绝对路径: {Path}");
                    return;
                }

                if (!System.IO.File.Exists(Path))
                {
                    NLogger.Error($"进程文件不存在: {Path}");
                    return;
                }

                if (metaData == null)
                {
                    NLogger.Error($"进程元数据为空: {Path}");
                    return;
                }

                NLogger.Info(
                    "准备启动进程: {0}, 参数: {1}, 管理员: {2}",
                    Path,
                    metaData.Arguments,
                    metaData.RunAs
                );

                Process _process = null;

                try
                {
                    _process = new Process();
                    _process.StartInfo = new ProcessStartInfo
                    {
                        FileName = Path,
                        Arguments = metaData.Arguments ?? string.Empty,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        Verb = metaData.RunAs ? "runas" : "",
                        WorkingDirectory =
                            System.IO.Path.GetDirectoryName(Path) ?? Environment.CurrentDirectory,
                        WindowStyle = metaData.MinimizedStartUp
                            ? ProcessWindowStyle.Minimized
                            : ProcessWindowStyle.Normal
                    };

                    NLogger.Debug("启动进程: {0}", Path);
                    bool started = _process.Start();

                    if (!started)
                    {
                        NLogger.Error("进程启动失败 (Start 返回 false): {0}", Path);
                        return;
                    }

                    NLogger.Info("进程已启动: {0}, PID: {1}", Path, _process.Id);

                    // 异步等待输入就绪
                    Observable
                        .Start(() =>
                        {
                            try
                            {
                                NLogger.Debug("等待进程输入就绪: {0}", Path);
                                _process.WaitForInputIdle(10000); // 10秒超时
                                NLogger.Debug("进程输入已就绪: {0}", Path);
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
                                        NLogger.Debug("调用 onStarted 回调: {0}", Path);
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
                    p.Kill();
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
    }
}
