using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDisplayAPI;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 负责显示设备发现与驱动分发的协调器。
    /// </summary>
    public sealed class BrightnessCoordinator
    {
        // 缓存已创建的驱动实例，避免重复创建
        private readonly Dictionary<string, IBrightnessDriver> _driverCache = new();

        public BrightnessCoordinator() { }

        /// <summary>
        /// 发现当前可用的显示设备。
        /// </summary>
        public Task<IReadOnlyList<DisplayIdentity>> DiscoverDisplaysAsync(
            CancellationToken cancellationToken = default
        )
        {
            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var displays = Display
                        .GetDisplays()
                        .Where(_ => _.IsAvailable)
                        .Select(
                            (display, index) =>
                            {
                                // 优先使用EDID解析的友好名称
                                var edidName = MonitorNameResolver.GetFriendlyName(
                                    display.DevicePath
                                );

                                var friendlyName = !string.IsNullOrWhiteSpace(edidName)
                                    ? edidName
                                    : (
                                        !string.IsNullOrWhiteSpace(display.DisplayName)
                                            ? display.DisplayName
                                            : display.DisplayFullName
                                    );

                                var identity = new DisplayIdentity(
                                    display.DeviceName,
                                    display.DevicePath,
                                    friendlyName,
                                    index
                                );
                                // 自动检测协议类型
                                identity.Protocol = identity.ResolveProtocol();
                                return identity;
                            }
                        )
                        .ToList();

                    return (IReadOnlyList<DisplayIdentity>)displays;
                },
                cancellationToken
            );
        }

        public Task<BrightnessInfo?> GetBrightnessAsync(
            DisplayIdentity display,
            CancellationToken cancellationToken = default
        )
        {
            var driver = ResolveDriver(display);
            return driver == null
                ? Task.FromResult<BrightnessInfo?>(null)
                : driver.GetBrightnessAsync(display, cancellationToken);
        }

        public Task<bool> SetBrightnessAsync(
            DisplayIdentity display,
            byte brightness,
            CancellationToken cancellationToken = default
        )
        {
            var driver = ResolveDriver(display);
            return driver == null
                ? Task.FromResult(false)
                : driver.SetBrightnessAsync(display, brightness, cancellationToken);
        }

        /// <summary>
        /// 根据 DisplayIdentity.Protocol 动态创建或返回缓存的驱动实例
        /// </summary>
        private IBrightnessDriver? ResolveDriver(DisplayIdentity display)
        {
            var protocol =
                display.Protocol == ProtocolType.Auto
                    ? display.ResolveProtocol()
                    : display.Protocol;

            // 为不同协议生成唯一缓存键
            string cacheKey = protocol switch
            {
                ProtocolType.KSV_Serial
                    => $"KSV_Serial_{display.SerialPort}_{display.SerialBaudRate}",
                ProtocolType.KSV_Tcp => $"KSV_Tcp_{display.TcpAddress}_{display.TcpPort}",
                ProtocolType.DdcCi => "DdcCi", // DDC/CI 驱动全局共享
                _ => "Unknown"
            };

            if (_driverCache.TryGetValue(cacheKey, out var cachedDriver))
            {
                return cachedDriver;
            }

            // 按需创建驱动实例
            IBrightnessDriver? driver = protocol switch
            {
                ProtocolType.DdcCi => new DdcCiBrightnessDriver(),
                ProtocolType.KSV_Serial
                    => KsvLedBrightnessDriver.CreateSerial(
                        display.SerialPort,
                        display.SerialBaudRate
                    ),
                ProtocolType.KSV_Tcp
                    => KsvLedBrightnessDriver.CreateTcp(display.TcpAddress, display.TcpPort),
                _ => null // Unknown 协议返回 null
            };

            if (driver != null)
            {
                _driverCache[cacheKey] = driver;

                // 记录驱动创建日志
                var protocolInfo = ProtocolInfo.GetInfo(protocol);
                DNHper.NLogger.Info(
                    $"[BrightnessCoordinator] 创建驱动: {protocolInfo.DisplayName} "
                        + $"设备={display.DisplayName}, 参数={GetDriverParams(display, protocol)}"
                );
            }

            return driver;
        }

        /// <summary>
        /// 获取驱动参数描述（用于日志）
        /// </summary>
        private string GetDriverParams(DisplayIdentity display, ProtocolType protocol)
        {
            return protocol switch
            {
                ProtocolType.KSV_Serial => $"{display.SerialPort}@{display.SerialBaudRate}bps",
                ProtocolType.KSV_Tcp => $"{display.TcpAddress}:{display.TcpPort}",
                ProtocolType.DdcCi => display.DevicePath,
                _ => "N/A"
            };
        }
    }
}
