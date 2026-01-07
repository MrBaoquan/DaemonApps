using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DNHper;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 负责监视器句柄枚举和缓存的服务。
    /// </summary>
    public sealed class MonitorEnumerator
    {
        private IReadOnlyList<MonitorHandle>? _cachedMonitorHandles;
        private readonly object _cacheLock = new object();

        /// <summary>
        /// 获取或枚举所有可用的监视器句柄（带缓存）。
        /// </summary>
        public IReadOnlyList<MonitorHandle> GetMonitorHandles()
        {
            if (_cachedMonitorHandles != null)
            {
                return _cachedMonitorHandles;
            }

            lock (_cacheLock)
            {
                // 双重检查锁定模式
                if (_cachedMonitorHandles != null)
                {
                    return _cachedMonitorHandles;
                }

                _cachedMonitorHandles = EnumerateMonitorHandles();
                return _cachedMonitorHandles;
            }
        }

        /// <summary>
        /// 根据DisplayIdentity解析对应的监视器句柄。
        /// 使用4层策略：
        /// 1. 按名称匹配（不同型号显示器）
        /// 2. 按DisplayIndex匹配（相同型号显示器）
        /// 3. 单个监视器fallback
        /// 4. 第一个监视器ultimate fallback
        /// </summary>
        public IntPtr ResolveMonitorHandle(DisplayIdentity display)
        {
            var candidates = GetMonitorHandles();

            if (candidates.Count == 0)
            {
                NLogger.Error($"[MonitorEnumerator] 未找到任何监视器");
                return IntPtr.Zero;
            }

            // 策略1：直接按名称匹配（通常用于不同型号的显示器）
            var matched = candidates.FirstOrDefault(
                m =>
                    string.Equals(
                        m.DeviceName,
                        display.DeviceName,
                        StringComparison.OrdinalIgnoreCase
                    )
            );

            if (matched.Handle != IntPtr.Zero)
            {
                return matched.Handle;
            }

            // 策略2：使用DisplayIndex来区分相同型号的显示器
            if (display.DisplayIndex >= 0 && display.DisplayIndex < candidates.Count)
            {
                return candidates[display.DisplayIndex].Handle;
            }

            // 策略3：如果只有一个监视器，使用它
            if (candidates.Count == 1)
            {
                return candidates[0].Handle;
            }

            // 策略4：使用第一个监视器作为fallback
            NLogger.Warn($"[MonitorEnumerator] 无法确定监视器匹配，使用第一个作为fallback");
            return candidates[0].Handle;
        }

        /// <summary>
        /// 清除缓存的监视器句柄（用于显示器配置变化时调用）。
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedMonitorHandles = null;
                NLogger.Info("[MonitorEnumerator] 监视器句柄缓存已清除");
            }
        }

        /// <summary>
        /// 枚举所有当前可用的监视器句柄。
        /// </summary>
        private static IReadOnlyList<MonitorHandle> EnumerateMonitorHandles()
        {
            var handles = new List<MonitorHandle>();

            bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
            {
                var info = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                    szDevice = new string('\0', 32)
                };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    handles.Add(new MonitorHandle(hMonitor, info.szDevice));
                }
                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
            return handles;
        }

        #region Win32 Interop

        private delegate bool MonitorEnumProc(
            IntPtr hMonitor,
            IntPtr hdcMonitor,
            ref RECT lprcMonitor,
            IntPtr dwData
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData
        );

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        #endregion
    }

    /// <summary>
    /// 代表一个监视器的句柄和设备名称。
    /// </summary>
    public readonly struct MonitorHandle
    {
        public MonitorHandle(IntPtr handle, string deviceName)
        {
            Handle = handle;
            DeviceName = deviceName;
        }

        public IntPtr Handle { get; }

        public string DeviceName { get; }
    }
}
