using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using DNHper;
using ReactiveUI;
namespace DaemonKit.Core {
    class ProcManager {
        public static bool KeepTopWindow (string ProcessFileName, int posX = 0, int posY = 0, int width = 0, int height = 0, int topMost = (int) HWndInsertAfter.HWND_TOPMOST) {
            var _process = WinAPI.FindProcess (ProcessFileName);
            return KeepTopWindow (_process, posX, posY, width, height, topMost);
        }

        public static bool KeepTopWindow (Process process, int posX = 0, int posY = 0, int width = 0, int height = 0, int topMost = (int) HWndInsertAfter.HWND_TOPMOST) {
            if (process == default (Process)) return false;
            return KeepTopWindow (process.MainWindowHandle, posX, posY, width, height, topMost);
        }

        public static bool KeepTopWindow (IntPtr handle, int posX = 0, int posY = 0, int width = 0, int height = 0, int topMost = (int) HWndInsertAfter.HWND_TOPMOST) {
            if (handle == IntPtr.Zero) return false;
            //WinAPI.SetWindowLong (_process.MainWindowHandle, (int) SetWindowLongIndex.GWL_STYLE, (UInt32) GWL_STYLE.WS_POPUP);
            var _noMove = posX == posY && posX == 0 ? SetWindowPosFlags.SWP_NOMOVE : 0x00;
            var _noSize = width == height && width == 0 ? SetWindowPosFlags.SWP_NOSIZE : 0x00;

            WinAPI.SetWindowPos (handle, topMost,
                posX, posY, width, height,
                SetWindowPosFlags.SWP_SHOWWINDOW | _noMove | _noSize | SetWindowPosFlags.SWP_FRAMECHANGED);
            //WinAPI.SetFocus (handle);
            return true;
        }

        public static bool IsWindowTopMost (string ProcessFileName) {
            var _process = WinAPI.FindProcess (ProcessFileName);
            if (_process == default (Process)) return false;
            return WinAPI.IsWindowTopMost (_process.MainWindowHandle);
        }

        // 守护进程
        public static void DaemonProcess (string Path, ProcessMetaData metaData, Action<Process> onStarted = null) {
            if (System.IO.Path.IsPathRooted (Path)) {
                Process _process = new Process ();
                _process.StartInfo = new ProcessStartInfo {
                    FileName = Path,
                    Arguments = metaData.Arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    Verb = metaData.RunAs? "runas": "",
                    WorkingDirectory = System.IO.Path.GetDirectoryName (Path),
                    WindowStyle = metaData.MinimizedStartUp?ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
                };
                _process.Start ();
                Observable
                    .Start (() => {
                        try {
                            _process.WaitForInputIdle ();
                        } catch (System.Exception e) {
                            NLogger.Error (e.Message);
                        }
                        return _process;
                    })
                    .ObserveOn (RxApp.MainThreadScheduler)
                    .Subscribe (_process => {
                        if (onStarted != null) onStarted (_process);
                    });
                // 如果进程未打开则打开该程序
                if (WinAPI.OpenProcessIfNotOpend (Path, new ProcessStartInfo {
                        FileName = Path,
                            Arguments = metaData.Arguments,
                            Verb = metaData.RunAs? "runas": "",
                            WorkingDirectory = System.IO.Path.GetDirectoryName (Path),
                            WindowStyle = metaData.MinimizedStartUp?ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
                    })) {
                    NLogger.Info ("已打开进程{0}", Path);
                }
            } else {
                NLogger.Warn ($"进程路径必须为绝对路径:{Path}");
            }
        }

        public static bool IsProcessExists (string Path) {
            return WinAPI.FindProcess (Path) != default (Process);
        }

        public static void KillProcess (string Path) {
            WinAPI.KillProcesses (Path);
            NLogger.Info ("已终止进程: {0}", Path);
        }

    }
}