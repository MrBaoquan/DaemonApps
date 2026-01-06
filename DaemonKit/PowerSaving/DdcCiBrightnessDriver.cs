using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
        public bool CanHandle(DisplayIdentity display)
        {
            NLogger.Debug($"[DDC/CI] 检查是否可以处理显示器: {display.DeviceName}");
            var result =
                TryOpenPhysicalMonitors(display, out var monitors) && DisposeMonitors(monitors);
            NLogger.Debug($"[DDC/CI] CanHandle结果: {result}");
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
                        NLogger.Warn($"[DDC/CI] GetBrightness: 无法打开显示器 {display.DeviceName}");
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
                            NLogger.Info(
                                $"[DDC/CI] GetBrightness: 成功获取 {display.DeviceName} 亮度: {current}"
                            );
                            return info;
                        }
                        NLogger.Error(
                            $"[DDC/CI] GetBrightness: GetMonitorBrightness失败，错误: {Marshal.GetLastWin32Error()}"
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

                    NLogger.Info($"[DDC/CI] 正在设置亮度: {display.DeviceName} = {brightness}%");

                    if (!TryOpenPhysicalMonitors(display, out var monitors) || monitors.Length == 0)
                    {
                        NLogger.Error($"[DDC/CI] 无法打开物理监视器: {display.DeviceName}");
                        return false;
                    }

                    try
                    {
                        var success = true;
                        foreach (var monitor in monitors)
                        {
                            NLogger.Debug(
                                $"[DDC/CI] 处理监视器: {monitor.szPhysicalMonitorDescription}"
                            );

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
                                NLogger.Error($"[DDC/CI] 获取亮度信息失败，错误代码: {errorCode}（可能不支持DDC/CI）");
                                success = false;
                                continue;
                            }

                            NLogger.Debug($"[DDC/CI] 亮度范围: {minimum}-{maximum}, 当前: {current}%");
                            var targetBrightness = ClampBrightness(brightness, minimum, maximum);
                            NLogger.Debug(
                                $"[DDC/CI] 计算目标亮度: {brightness}% -> {targetBrightness} (范围{minimum}-{maximum})"
                            );

                            if (!SetMonitorBrightness(monitor.hPhysicalMonitor, targetBrightness))
                            {
                                var errorCode = Marshal.GetLastWin32Error();
                                NLogger.Error(
                                    $"[DDC/CI] SetMonitorBrightness 失败，目标值: {targetBrightness}，错误代码: {errorCode}"
                                );
                                success = false;
                            }
                            else
                            {
                                NLogger.Info($"[DDC/CI] 亮度设置成功，目标值: {targetBrightness}");
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
            monitors = Array.Empty<PHYSICAL_MONITOR>();
            var hMonitor = ResolveMonitorHandle(display);
            if (hMonitor == IntPtr.Zero)
            {
                NLogger.Error($"[DDC/CI] 未找到监视器句柄: {display.DeviceName}");
                return false;
            }

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                NLogger.Error(
                    $"[DDC/CI] GetNumberOfPhysicalMonitorsFromHMONITOR 失败或计数为0，错误: {Marshal.GetLastWin32Error()}"
                );
                return false;
            }

            var buffer = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
            {
                NLogger.Error(
                    $"[DDC/CI] GetPhysicalMonitorsFromHMONITOR 失败，错误: {Marshal.GetLastWin32Error()}"
                );
                return false;
            }

            monitors = buffer;
            NLogger.Info($"[DDC/CI] 成功打开 {count} 个物理监视器");
            return true;
        }

        private IntPtr ResolveMonitorHandle(DisplayIdentity display)
        {
            var candidates = EnumerateMonitorHandles();
            NLogger.Info($"[DDC/CI] 枚举得到 {candidates.Count} 个监视器");
            foreach (var h in candidates)
            {
                NLogger.Info($"[DDC/CI]   - {h.DeviceName}");
            }

            if (candidates.Count == 0)
            {
                NLogger.Warn($"[DDC/CI] 没有找到任何监视器");
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
                NLogger.Info($"[DDC/CI] [策略1] 名称直接匹配成功: {matched.DeviceName}");
                return matched.Handle;
            }

            // 策略2：使用DisplayIndex来区分相同型号的显示器
            // 当有多个相同型号的显示器时，根据在WindowsDisplayAPI中的索引位置来匹配
            if (display.DisplayIndex >= 0 && display.DisplayIndex < candidates.Count)
            {
                // 假设候选列表按顺序对应各个显示器
                var byIndex = candidates[display.DisplayIndex];
                NLogger.Info(
                    $"[DDC/CI] [策略2] 使用DisplayIndex匹配: 索引{display.DisplayIndex} -> {byIndex.DeviceName}"
                );
                return byIndex.Handle;
            }

            // 策略3：如果只有一个监视器，使用它
            if (candidates.Count == 1)
            {
                NLogger.Warn($"[DDC/CI] [策略3] 只有一个监视器，使用它: {candidates[0].DeviceName}");
                return candidates[0].Handle;
            }

            // 策略4：使用第一个监视器作为最后fallback
            NLogger.Warn($"[DDC/CI] [策略4] 无法确定匹配，使用第一个监视器作为fallback: {candidates[0].DeviceName}");
            return candidates[0].Handle;
        }

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private readonly struct MonitorHandle
        {
            public MonitorHandle(IntPtr handle, string deviceName)
            {
                Handle = handle;
                DeviceName = deviceName;
            }

            public IntPtr Handle { get; }

            public string DeviceName { get; }
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
