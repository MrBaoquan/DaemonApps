using System;
using System.Collections.Generic;
using System.Management;

namespace DaemonKit.Services;

/// <summary>
/// 系统资源监控服务（CPU/内存/GPU）。
/// 注意：任何采集异常都必须吞掉并降级，不能影响主流程。
/// </summary>
public sealed class SystemStatusMonitorService
{
    public SystemStatusSnapshot CollectSnapshot(bool enableGpuMonitoring)
    {
        var snapshot = new SystemStatusSnapshot { Timestamp = DateTime.Now };

        // CPU
        snapshot.CpuUsagePercent = SafeCollectCpuUsage();

        // 内存
        (snapshot.MemoryUsagePercent, snapshot.MemoryUsedGb, snapshot.MemoryTotalGb) =
            SafeCollectMemoryUsage();

        // GPU（可选）
        snapshot.GpuUsagePercent = enableGpuMonitoring ? SafeCollectGpuUsage() : null;

        return snapshot;
    }

    /// <summary>
    /// CPU/内存 WMI 查询超时。通常很快，但异常恢复场景下也可能卡住。
    /// </summary>
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);

    private static double SafeCollectCpuUsage()
    {
        try
        {
            // 使用 WMI 获取整体 CPU 负载百分比，避免额外依赖。
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT LoadPercentage FROM Win32_Processor"
            );
            searcher.Options.Timeout = DefaultQueryTimeout;

            var values = new List<double>();
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["LoadPercentage"] != null)
                {
                    if (double.TryParse(obj["LoadPercentage"].ToString(), out var value))
                    {
                        values.Add(value);
                    }
                }
            }

            if (values.Count == 0)
                return 0;

            var avg = 0d;
            foreach (var value in values)
                avg += value;
            avg /= values.Count;

            return Math.Clamp(avg, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private static (double usagePercent, double usedGb, double totalGb) SafeCollectMemoryUsage()
    {
        try
        {
            // TotalVisibleMemorySize / FreePhysicalMemory 单位是 KB。
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"
            );
            searcher.Options.Timeout = DefaultQueryTimeout;

            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["TotalVisibleMemorySize"] == null || obj["FreePhysicalMemory"] == null)
                    continue;

                if (!double.TryParse(obj["TotalVisibleMemorySize"].ToString(), out var totalKb))
                    continue;
                if (!double.TryParse(obj["FreePhysicalMemory"].ToString(), out var freeKb))
                    continue;

                if (totalKb <= 0)
                    continue;

                var usedKb = Math.Max(0, totalKb - freeKb);
                var usagePercent = Math.Clamp((usedKb / totalKb) * 100d, 0, 100);
                var usedGb = usedKb / 1024d / 1024d;
                var totalGb = totalKb / 1024d / 1024d;

                return (usagePercent, usedGb, totalGb);
            }

            return (0, 0, 0);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// GPU 查询超时（秒）。Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine
    /// 在部分驱动/异常恢复场景下可能挂起 30s+，必须限时。
    /// </summary>
    private static readonly TimeSpan GpuQueryTimeout = TimeSpan.FromSeconds(8);

    private static double? SafeCollectGpuUsage()
    {
        try
        {
            // GPU Engine 计数器（WMI）
            // 说明：不同驱动下可用性不同，失败时返回 null。
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"
            );
            // 设置 WMI 查询超时，防止驱动异常时无限阻塞线程池线程
            searcher.Options.Timeout = GpuQueryTimeout;

            var found = false;
            var maxUtilization = 0d;

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // 过滤无关实例，主要关注常见计算/图形引擎。
                if (
                    !name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("engtype_Cuda", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                if (obj["UtilizationPercentage"] == null)
                    continue;

                if (!double.TryParse(obj["UtilizationPercentage"].ToString(), out var util))
                    continue;

                found = true;
                if (util > maxUtilization)
                    maxUtilization = util;
            }

            if (!found)
                return null;

            return Math.Clamp(maxUtilization, 0, 100);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SystemStatusSnapshot
{
    public DateTime Timestamp { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }
    public double? GpuUsagePercent { get; set; }
}
