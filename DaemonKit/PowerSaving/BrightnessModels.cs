using System;
using System.Collections.Generic;
using System.Linq;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 设备类型枚举（已废弃，使用 ProtocolType 代替）
    /// </summary>
    [Obsolete("Use ProtocolType instead")]
    public enum DeviceType
    {
        /// <summary>标准显示器（DDC/CI协议）</summary>
        Monitor,

        /// <summary>KSV LED 控制器</summary>
        KsvLed,

        /// <summary>其他未知设备</summary>
        Unknown
    }

    /// <summary>
    /// 显示设备协议类型（按厂商和型号划分）
    /// </summary>
    public enum ProtocolType
    {
        /// <summary>自动检测（根据设备特征智能识别）</summary>
        Auto,

        /// <summary>DDC/CI 标准显示器协议（VESA 标准）</summary>
        DdcCi,

        /// <summary>KSV KM2/KM4 控制器 - 串口协议（RS232 115200bps）</summary>
        KSV_Serial,

        /// <summary>KSV KM2/KM4 控制器 - 网口协议（TCP 18100端口）</summary>
        KSV_Tcp,

        /// <summary>未知设备（无法识别或不支持）</summary>
        Unknown
    }

    /// <summary>
    /// 协议元数据信息
    /// </summary>
    public sealed class ProtocolInfo
    {
        public ProtocolType Type { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Manufacturer { get; }
        public bool IsVerified { get; }

        public ProtocolInfo(
            ProtocolType type,
            string displayName,
            string description,
            string manufacturer = "",
            bool isVerified = false
        )
        {
            Type = type;
            DisplayName = displayName;
            Description = description;
            Manufacturer = manufacturer;
            IsVerified = isVerified;
        }

        /// <summary>
        /// 获取所有支持的协议列表
        /// </summary>
        public static IReadOnlyList<ProtocolInfo> GetSupportedProtocols()
        {
            return new List<ProtocolInfo>
            {
                new ProtocolInfo(ProtocolType.Auto, "自动检测", "根据设备名称和特征自动识别协议类型"),
                new ProtocolInfo(
                    ProtocolType.DdcCi,
                    "DDC/CI (标准显示器)",
                    "VESA标准显示器数据通道,通过I2C总线通信",
                    "VESA"
                ),
                new ProtocolInfo(
                    ProtocolType.KSV_Serial,
                    "KSV LED - 串口",
                    "KSV KM2/KM4控制器,RS232串口协议 (115200bps 8N1)",
                    "KSV"
                ),
                new ProtocolInfo(
                    ProtocolType.KSV_Tcp,
                    "KSV LED - 网口",
                    "KSV KM2/KM4控制器,TCP网络协议 (默认端口18100)",
                    "KSV"
                ),
                new ProtocolInfo(ProtocolType.Unknown, "未知/不支持", "设备类型未知或不支持亮度控制")
            };
        }

        /// <summary>
        /// 根据协议类型获取协议信息
        /// </summary>
        public static ProtocolInfo GetInfo(ProtocolType type)
        {
            return GetSupportedProtocols().FirstOrDefault(p => p.Type == type)
                ?? new ProtocolInfo(ProtocolType.Unknown, "未知", "未知协议");
        }
    }

    /// <summary>
    /// 描述一个物理显示设备的关键标识。
    /// </summary>
    public sealed class DisplayIdentity
    {
        public DisplayIdentity(
            string deviceName,
            string devicePath,
            string friendlyName,
            int displayIndex = -1,
            DeviceType deviceType = DeviceType.Monitor
        )
        {
            DeviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
            DevicePath = devicePath ?? string.Empty;
            FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? deviceName : friendlyName;
            DisplayIndex = displayIndex;
            DeviceType = deviceType;

            // 默认协议类型为自动检测
            Protocol = ProtocolType.Auto;
            SerialPort = "COM1";
            SerialBaudRate = 115200;
            TcpAddress = "192.168.1.100";
            TcpPort = 18100;
        }

        public string DeviceName { get; }

        public string DevicePath { get; }

        public string FriendlyName { get; }

        /// <summary>
        /// 显示器在所有显示器列表中的索引（从0开始）。用于多个相同型号显示器的区分。
        /// </summary>
        public int DisplayIndex { get; }

        /// <summary>
        /// 设备类型（已废弃）
        /// </summary>
        [Obsolete("Use Protocol instead")]
        public DeviceType DeviceType { get; }

        /// <summary>
        /// 协议类型（可由用户手动选择）
        /// </summary>
        public ProtocolType Protocol { get; set; }

        /// <summary>
        /// 串口模式：COM 端口号（如 COM3）
        /// </summary>
        public string SerialPort { get; set; }

        /// <summary>
        /// 串口模式：波特率（默认 115200）
        /// </summary>
        public int SerialBaudRate { get; set; }

        /// <summary>
        /// 网口模式：IP 地址
        /// </summary>
        public string TcpAddress { get; set; }

        /// <summary>
        /// 网口模式：端口号（默认 18100）
        /// </summary>
        public int TcpPort { get; set; }

        public string DisplayName => FriendlyName;

        /// <summary>
        /// 协议是否已验证（通过实际通信测试）
        /// </summary>
        public bool IsProtocolVerified { get; set; }

        /// <summary>
        /// 解析实际使用的协议类型（如果是 Auto 则自动检测）
        /// </summary>
        public ProtocolType ResolveProtocol()
        {
            if (Protocol != ProtocolType.Auto)
                return Protocol;

            // 自动检测逻辑
            var name = DeviceName.ToLowerInvariant();
            var path = DevicePath.ToLowerInvariant();
            var friendly = FriendlyName.ToLowerInvariant();

            // KSV LED 控制器特征识别（优先级最高）
            if (
                name.Contains("ksv")
                || friendly.Contains("ksv")
                || name.Contains("km2")
                || name.Contains("km4")
                || friendly.Contains("km2")
                || friendly.Contains("km4")
            )
            {
                // 检查是否是虚拟串口设备
                if (path.Contains("com") || path.StartsWith("\\\\.\\com"))
                    return ProtocolType.KSV_Serial;

                // 默认网口（KSV设备通常配置为网口模式）
                return ProtocolType.KSV_Tcp;
            }

            // 通用 LED 关键词（保守识别为 KSV 串口）
            if (name.Contains("led") || friendly.Contains("led controller"))
            {
                return ProtocolType.KSV_Serial;
            }

            // 串口设备路径特征（可能是其他串口LED设备）
            if (path.Contains("com") || path.StartsWith("\\\\.\\com"))
            {
                return ProtocolType.KSV_Serial;
            }

            // 默认为 DDC/CI 显示器（但需要后续验证）
            return ProtocolType.DdcCi;
        }

        /// <summary>
        /// 检查设备是否明确支持 DDC/CI（通过设备特征判断）
        /// </summary>
        public bool IsDdcCiCapable()
        {
            // 串口/虚拟设备明确不支持 DDC/CI
            var path = DevicePath.ToLowerInvariant();
            if (path.Contains("com") || path.Contains("virtual"))
                return false;

            // 包含 LED/KSV 关键词的设备通常不是标准显示器
            var name = DeviceName.ToLowerInvariant();
            var friendly = FriendlyName.ToLowerInvariant();
            if (
                name.Contains("ksv")
                || name.Contains("led")
                || friendly.Contains("ksv")
                || friendly.Contains("led controller")
            )
                return false;

            // 标准显示器路径特征: DISPLAY\... 格式
            if (path.StartsWith("\\\\?\\display") || DevicePath.StartsWith("DISPLAY\\"))
                return true;

            // 其他情况需要实际通信验证
            return false;
        }
    }

    /// <summary>
    /// 当前亮度信息（0-100）。
    /// </summary>
    public sealed class BrightnessInfo
    {
        public BrightnessInfo(byte minimum, byte current, byte maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
            Current = current;
        }

        public byte Minimum { get; }

        public byte Current { get; }

        public byte Maximum { get; }
    }

    /// <summary>
    /// 省电模式配置，目前仅包含亮度。
    /// </summary>
    public sealed class PowerSavingProfile
    {
        public PowerSavingProfile(byte targetBrightness)
        {
            TargetBrightness = targetBrightness;
            _overrides = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 期望的目标亮度（0-100）。
        /// </summary>
        public byte TargetBrightness { get; }

        private readonly Dictionary<string, byte> _overrides;

        public IReadOnlyDictionary<string, byte> Overrides => _overrides;

        public PowerSavingProfile WithOverride(string devicePath, byte brightness)
        {
            if (!string.IsNullOrWhiteSpace(devicePath))
            {
                _overrides[devicePath] = brightness;
            }
            return this;
        }

        public byte ResolveTarget(DisplayIdentity display)
        {
            return _overrides.TryGetValue(display.DevicePath, out var value)
                ? value
                : TargetBrightness;
        }
    }

    /// <summary>
    /// 单个显示设备的操作结果。
    /// </summary>
    public sealed class DisplayBrightnessResult
    {
        public DisplayBrightnessResult(
            DisplayIdentity display,
            bool isSuccess,
            string? message = null
        )
        {
            Display = display;
            IsSuccess = isSuccess;
            Message = message;
        }

        public DisplayIdentity Display { get; }

        public bool IsSuccess { get; }

        public string? Message { get; }
    }

    /// <summary>
    /// 省电操作的总体结果。
    /// </summary>
    public sealed class PowerSavingResult
    {
        public PowerSavingResult(IReadOnlyList<DisplayBrightnessResult> results)
        {
            Results = results;
        }

        public IReadOnlyList<DisplayBrightnessResult> Results { get; }

        public IReadOnlyList<DisplayBrightnessResult> DisplayResults => Results;

        public bool IsSuccessful => Results.Count > 0 && Results.All(_ => _.IsSuccess);
    }
}
