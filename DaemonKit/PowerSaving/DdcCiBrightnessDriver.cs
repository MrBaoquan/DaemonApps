using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DNHper;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 通过 DDC/CI 控制显示器亮度的驱动。
    /// </summary>
    public sealed class DdcCiBrightnessDriver : IBrightnessDriver
    {
        private readonly MonitorEnumerator _monitorEnumerator;

        public DdcCiBrightnessDriver(MonitorEnumerator monitorEnumerator)
        {
            _monitorEnumerator =
                monitorEnumerator ?? throw new ArgumentNullException(nameof(monitorEnumerator));
        }

        public bool CanHandle(DisplayIdentity display)
        {
            var result =
                TryOpenPhysicalMonitors(display, out var monitors) && DisposeMonitors(monitors);
            return result;
        }

        public Task<BrightnessInfo?> GetBrightnessAsync(
            DisplayIdentity display,
            CancellationToken cancellationToken = default
        )
        {
            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryOpenPhysicalMonitors(display, out var monitors) || monitors.Length == 0)
                    {
                        return (BrightnessInfo?)null;
                    }

                    try
                    {
                        if (
                            GetMonitorBrightness(
                                monitors[0].hPhysicalMonitor,
                                out var minimum,
                                out var current,
                                out var maximum
                            )
                        )
                        {
                            var info = new BrightnessInfo(
                                (byte)minimum,
                                (byte)current,
                                (byte)maximum
                            );
                            return info;
                        }
                        NLogger.Error(
                            "[DDC/CI] GetBrightness: GetMonitorBrightness失败，错误: {ErrorCode}",
                            Marshal.GetLastWin32Error()
                        );
                        return null;
                    }
                    finally
                    {
                        DisposeMonitors(monitors);
                    }
                },
                cancellationToken
            );
        }

        public Task<bool> SetBrightnessAsync(
            DisplayIdentity display,
            byte brightness,
            CancellationToken cancellationToken = default
        )
        {
            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryOpenPhysicalMonitors(display, out var monitors) || monitors.Length == 0)
                    {
                        NLogger.Error("[DDC/CI] 无法打开物理监视器: {DeviceName}", display.DeviceName);
                        return false;
                    }

                    try
                    {
                        var success = true;
                        foreach (var monitor in monitors)
                        {
                            if (
                                !GetMonitorBrightness(
                                    monitor.hPhysicalMonitor,
                                    out var minimum,
                                    out var current,
                                    out var maximum
                                )
                            )
                            {
                                var errorCode = Marshal.GetLastWin32Error();
                                NLogger.Error("[DDC/CI] 获取亮度信息失败，错误代码: {ErrorCode}", errorCode);
                                success = false;
                                continue;
                            }

                            var targetBrightness = ClampBrightness(brightness, minimum, maximum);

                            if (!SetMonitorBrightness(monitor.hPhysicalMonitor, targetBrightness))
                            {
                                var errorCode = Marshal.GetLastWin32Error();
                                NLogger.Error("[DDC/CI] 亮度设置失败，错误代码: {ErrorCode}", errorCode);
                                success = false;
                            }
                        }
                        return success;
                    }
                    finally
                    {
                        DisposeMonitors(monitors);
                    }
                },
                cancellationToken
            );
        }

        private static uint ClampBrightness(byte requested, uint minimum, uint maximum)
        {
            if (maximum <= minimum)
            {
                // If invalid range, treat requested as percentage 0-100
                return Math.Min(Math.Max(requested, 0u), 100u);
            }

            // Scale requested (0-100) to actual range [minimum, maximum]
            var normalized = (requested / 100.0);
            var scaled = minimum + (maximum - minimum) * normalized;
            return (uint)Math.Round(scaled);
        }

        private bool TryOpenPhysicalMonitors(
            DisplayIdentity display,
            out PHYSICAL_MONITOR[] monitors
        )
        {
            monitors = System.Array.Empty<PHYSICAL_MONITOR>();
            var hMonitor = _monitorEnumerator.ResolveMonitorHandle(display);
            if (hMonitor == IntPtr.Zero)
            {
                return false;
            }

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                NLogger.Error("[DDC/CI] 无法获取物理监视器");
                return false;
            }

            var buffer = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
            {
                NLogger.Error("[DDC/CI] 打开物理监视器失败");
                return false;
            }

            monitors = buffer;
            return true;
        }

        private static bool DisposeMonitors(IReadOnlyList<PHYSICAL_MONITOR> monitors)
        {
            if (monitors == null || monitors.Count == 0)
            {
                return true;
            }

            _ = DestroyPhysicalMonitors((uint)monitors.Count, monitors.ToArray());
            return true;
        }

        #region Win32 Interop

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            out uint numberOfPhysicalMonitors
        );

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            uint physicalMonitorArraySize,
            [Out] PHYSICAL_MONITOR[] physicalMonitorArray
        );

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(
            uint physicalMonitorArraySize,
            [In] PHYSICAL_MONITOR[] physicalMonitorArray
        );

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(
            IntPtr hMonitor,
            out uint minimumBrightness,
            out uint currentBrightness,
            out uint maximumBrightness
        );

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

        #endregion
    }
}
