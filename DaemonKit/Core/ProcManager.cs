using System;
using System.Diagnostics;
using DNHper;

namespace DaemonKit.Core {
    class ProcManager {
        public static bool KeepTopWindow (string ProcessFileName, int posX = 0, int posY = 0, int width = 0, int height = 0, int topMost = (int) HWndInsertAfter.HWND_TOPMOST) {
            var _process = WinAPI.FindProcess (ProcessFileName);
            return KeepTopWindow (_process, posX, posY, width, height, topMost);
        }

        public static bool KeepTopWindow (Process process, int posX = 0, int posY = 0, int width = 0, int height = 0, int topMost = (int) HWndInsertAfter.HWND_TOPMOST) {
            if (process == default (Process)) return false;
            //WinAPI.SetWindowLong (_process.MainWindowHandle, (int) SetWindowLongIndex.GWL_STYLE, (UInt32) GWL_STYLE.WS_POPUP);
            var _noMove = posX == posY && posX == 0 ? SetWindowPosFlags.SWP_NOMOVE : 0x00;
            var _noSize = width == height && width == 0 ? SetWindowPosFlags.SWP_NOSIZE : 0x00;

            WinAPI.SetWindowPos (process.MainWindowHandle, topMost,
                posX, posY, width, height,
                SetWindowPosFlags.SWP_SHOWWINDOW | _noMove | _noSize | SetWindowPosFlags.SWP_FRAMECHANGED);
            WinAPI.ShowWindow (process.MainWindowHandle, (int) CMDShow.SW_SHOW);
            return true;
        }

        public static bool IsWindowTopMost (string ProcessFileName) {
            var _process = WinAPI.FindProcess (ProcessFileName);
            if (_process == default (Process)) return false;
            return WinAPI.IsWindowTopMost (_process.MainWindowHandle);
        }

        // 守护进程
        public static void DaemonProcess (string Path, string Args = "", bool runas = false) {
            string _processName = System.IO.Path.GetFileNameWithoutExtension (Path);
            if (System.IO.Path.IsPathRooted (Path)) {
                // 如果进程未打开则打开该程序
                if (WinAPI.OpenProcessIfNotOpend (Path, Args, runas)) {
                    NLogger.Info ("已打开进程{0}", Path);
                }
            }
        }

        public static void KillProcess (string Path) {
            var _process = WinAPI.FindProcess (Path);
            if (_process == default (Process)) return;
            _process.Kill ();
            NLogger.Info ("已终止进程: {0}", Path);
        }
    }
}