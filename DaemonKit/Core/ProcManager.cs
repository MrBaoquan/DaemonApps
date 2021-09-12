using System;
using System.Diagnostics;
using DNHper;

namespace DaemonKit.Core {
    class ProcManager {
        public static bool KeepTopWindow (string ProcessFileName) {
            var _process = WinAPI.FindProcess (ProcessFileName);
            if (_process == default (Process)) return false;
            //WinAPI.SetWindowLong (_process.MainWindowHandle, (int) SetWindowLongIndex.GWL_STYLE, (UInt32) GWL_STYLE.WS_POPUP);
            WinAPI.SetWindowPos (_process.MainWindowHandle, (int) HWndInsertAfter.HWND_TOPMOST,
                0, 0, 0, 0,
                SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_FRAMECHANGED);
            WinAPI.ShowWindow (_process.MainWindowHandle, (int) CMDShow.SW_SHOWNORMAL);
            return true;
        }

        // 守护进程
        public static void DaemonProcess (string Path, string Args = "", bool runas = false) {
            string _processName = System.IO.Path.GetFileNameWithoutExtension (Path);
            if (System.IO.Path.IsPathRooted (Path)) {
                // 如果进程未打开则打开该程序
                if (WinAPI.OpenProcessIfNotOpend (Path, Args, runas)) {
                    NLogger.Info ("[DK]: 已打开进程{0}", Path);
                }
            }
        }
    }
}