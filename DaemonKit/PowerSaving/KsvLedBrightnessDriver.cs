using System;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// KSV LED 控制器亮度驱动 - 支持串口（RS232）和网口（TCP）协议
    /// 适用型号：KSV24c, KSV12c (网口), KSV6c, KSV8c, KSV2C, KSV4c, KM2, KM4 (串口)
    /// </summary>
    public sealed class KsvLedBrightnessDriver : IBrightnessDriver
    {
        private readonly string _connectionType; // "serial" or "tcp"
        private readonly string _portOrIp;
        private readonly int _baudRateOrPort;
        private SerialPort? _serialPort;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;

        // 串口模式：缓存最后设置的亮度值（用于读取）
        private byte _lastSetBrightness = 50;

        /// <summary>
        /// 私有构造函数
        /// </summary>
        private KsvLedBrightnessDriver(string type, string portOrIp, int baudRateOrPort)
        {
            _connectionType = type;
            _portOrIp = portOrIp;
            _baudRateOrPort = baudRateOrPort;
        }

        /// <summary>
        /// 创建串口模式驱动
        /// </summary>
        public static KsvLedBrightnessDriver CreateSerial(string comPort, int baudRate = 115200)
        {
            return new KsvLedBrightnessDriver("serial", comPort, baudRate);
        }

        /// <summary>
        /// 创建网口模式驱动
        /// </summary>
        public static KsvLedBrightnessDriver CreateTcp(string ipAddress, int port = 18100)
        {
            return new KsvLedBrightnessDriver("tcp", ipAddress, port);
        }

        public bool CanHandle(DisplayIdentity display)
        {
            // 通过 DeviceName 或 FriendlyName 判断是否为 KSV LED 设备
            var name = display.DeviceName.ToLowerInvariant();
            return name.Contains("ksv")
                || name.Contains("led")
                || name.Contains("km2")
                || name.Contains("km4");
        }

        public async Task<BrightnessInfo?> GetBrightnessAsync(
            DisplayIdentity display,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                byte currentBrightness;

                if (_connectionType == "tcp")
                {
                    // 网口模式：使用 0x22 命令读取亮度
                    currentBrightness = await GetBrightnessViaTcpAsync(cancellationToken);
                }
                else
                {
                    // 串口模式：返回缓存的上次设置值（协议无专门读取命令）
                    currentBrightness = _lastSetBrightness;
                    DNHper.NLogger.Info($"[KSV-LED] 串口模式返回缓存亮度: {currentBrightness}");
                }

                // 将 0-255 映射回 0-100
                byte brightness = (byte)(currentBrightness * 100 / 255);
                return new BrightnessInfo(0, brightness, 100);
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[KSV-LED] 读取亮度失败: {ex.Message}");
                return new BrightnessInfo(0, 50, 100); // 返回默认值
            }
        }

        public async Task<bool> SetBrightnessAsync(
            DisplayIdentity display,
            byte brightness,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                // 0-100 映射到 0-255
                byte hexBrightness = (byte)(brightness * 255 / 100);

                if (_connectionType == "serial")
                {
                    return await SetBrightnessViaSerialAsync(hexBrightness, cancellationToken);
                }
                else
                {
                    return await SetBrightnessViaTcpAsync(hexBrightness, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[KSV-LED] 设置亮度失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 串口方式设置亮度
        /// </summary>
        private async Task<bool> SetBrightnessViaSerialAsync(byte brightness, CancellationToken ct)
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // 确保串口已打开
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        _serialPort = new SerialPort(
                            _portOrIp,
                            _baudRateOrPort,
                            Parity.None,
                            8,
                            StopBits.One
                        );
                        _serialPort.Open();
                        DNHper.NLogger.Info($"[KSV-LED] 串口 {_portOrIp} 已打开，波特率 {_baudRateOrPort}");
                    }

                    // 构造串口指令：E9 00 93 [亮度] 00 [校验和] 0D 0A
                    byte[] command = BuildSerialCommand(0x93, brightness);

                    _serialPort.Write(command, 0, command.Length);
                    DNHper.NLogger.Info($"[KSV-LED] 串口发送亮度指令: {BitConverter.ToString(command)}");

                    // 等待硬件处理
                    await Task.Delay(50, ct);

                    // 更新缓存的亮度值
                    _lastSetBrightness = brightness;

                    // 发送固化指令保存设置
                    byte[] saveCommand = BuildSerialCommand(0x95, 0x00);
                    _serialPort.Write(saveCommand, 0, saveCommand.Length);
                    DNHper.NLogger.Info(
                        $"[KSV-LED] 串口发送固化指令: {BitConverter.ToString(saveCommand)}"
                    );

                    return true;
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Warn(
                        $"[KSV-LED] 串口设置亮度失败 (尝试 {attempt}/{maxRetries}): {ex.Message}"
                    );

                    // 关闭失效的串口连接
                    try
                    {
                        _serialPort?.Close();
                    }
                    catch { }
                    _serialPort = null;

                    // 最后一次重试失败
                    if (attempt >= maxRetries)
                    {
                        DNHper.NLogger.Error($"[KSV-LED] 串口设置亮度失败，已重试 {maxRetries} 次");
                        return false;
                    }

                    // 短暂延迟后重试
                    await Task.Delay(200, ct);
                }
            }

            return false;
        }

        /// <summary>
        /// 网口方式设置亮度
        /// </summary>
        private async Task<bool> SetBrightnessViaTcpAsync(byte brightness, CancellationToken ct)
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // 确保 TCP 连接已建立
                    if (_tcpClient == null || !_tcpClient.Connected)
                    {
                        _tcpClient = new TcpClient();
                        await _tcpClient.ConnectAsync(_portOrIp, _baudRateOrPort);
                        _networkStream = _tcpClient.GetStream();

                        // 发送设备连接指令（网口协议要求）
                        await SendDeviceConnectCommandAsync(ct);
                        DNHper.NLogger.Info($"[KSV-LED] TCP 连接已建立 {_portOrIp}:{_baudRateOrPort}");
                    }

                    // 构造网口指令（长帧格式，第21字节为亮度）
                    byte[] command = BuildTcpCommand(brightness, 0x40); // 对比度默认 64

                    await _networkStream!.WriteAsync(command, 0, command.Length, ct);
                    DNHper.NLogger.Info($"[KSV-LED] TCP 发送亮度指令，亮度值: {brightness}");

                    return true;
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Warn(
                        $"[KSV-LED] TCP 设置亮度失败 (尝试 {attempt}/{maxRetries}): {ex.Message}"
                    );

                    // 关闭失效的 TCP 连接
                    try
                    {
                        _networkStream?.Close();
                    }
                    catch { }
                    try
                    {
                        _tcpClient?.Close();
                    }
                    catch { }
                    _tcpClient = null;
                    _networkStream = null;

                    // 最后一次重试失败
                    if (attempt >= maxRetries)
                    {
                        DNHper.NLogger.Error($"[KSV-LED] TCP 设置亮度失败，已重试 {maxRetries} 次");
                        return false;
                    }

                    // 短暂延迟后重试
                    await Task.Delay(500, ct);
                }
            }

            return false;
        }

        /// <summary>
        /// 构造串口指令帧
        /// </summary>
        private byte[] BuildSerialCommand(byte commandCode, byte data)
        {
            // E9 00 [CMD] [DATA] 00 [CHECKSUM] 0D 0A
            byte[] frame = new byte[8];
            frame[0] = 0xE9; // 帧起始
            frame[1] = 0x00; // 设备ID
            frame[2] = commandCode; // 命令字 (0x93=亮度, 0x94=对比度, 0x95=固化)
            frame[3] = data; // 数据
            frame[4] = 0x00; // Data2

            // 计算校验和 (前5字节累加取低8位)
            int checksum = frame[0] + frame[1] + frame[2] + frame[3] + frame[4];
            frame[5] = (byte)(checksum & 0xFF);

            frame[6] = 0x0D; // 帧结束
            frame[7] = 0x0A;

            return frame;
        }

        /// <summary>
        /// 构造网口指令帧
        /// </summary>
        private byte[] BuildTcpCommand(byte brightness, byte contrast)
        {
            // 网口长帧格式（固定26字节）
            // D2 02 96 49 1A 00 00 00 00 00 00 00 21 20 06 00 00 AC 01 3A [亮度] [对比度] 2E FD 69 B6
            byte[] frame = new byte[26];

            // 固定包头
            frame[0] = 0xD2;
            frame[1] = 0x02;
            frame[2] = 0x96;
            frame[3] = 0x49;
            frame[4] = 0x1A;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = 0x00;
            frame[9] = 0x00;
            frame[10] = 0x00;
            frame[11] = 0x00;
            frame[12] = 0x21;
            frame[13] = 0x20;
            frame[14] = 0x06;
            frame[15] = 0x00;
            frame[16] = 0x00;
            frame[17] = 0xAC;
            frame[18] = 0x01;
            frame[19] = 0x3A;

            // 亮度和对比度
            frame[20] = brightness; // 亮度值
            frame[21] = contrast; // 对比度值

            // 固定包尾
            frame[22] = 0x2E;
            frame[23] = 0xFD;
            frame[24] = 0x69;
            frame[25] = 0xB6;

            return frame;
        }

        /// <summary>
        /// 发送设备连接指令（网口协议必须）
        /// </summary>
        private async Task SendDeviceConnectCommandAsync(CancellationToken ct)
        {
            // 根据协议文档，需要先发送连接指令（具体格式需要根据实际文档补充）
            // 这里使用占位实现
            byte[] connectCommand = new byte[]
            {
                0xD2,
                0x02,
                0x96,
                0x49, /* ... 其他字节 */
            };
            if (_networkStream != null)
            {
                await _networkStream.WriteAsync(connectCommand, 0, connectCommand.Length, ct);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
                _networkStream?.Dispose();
                _tcpClient?.Dispose();
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[KSV-LED] 释放资源时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 网口方式读取亮度（使用 0x22 命令）
        /// </summary>
        private async Task<byte> GetBrightnessViaTcpAsync(CancellationToken ct)
        {
            try
            {
                // 确保 TCP 连接已建立
                if (_tcpClient == null || !_tcpClient.Connected)
                {
                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync(_portOrIp, _baudRateOrPort);
                    _networkStream = _tcpClient.GetStream();

                    // 发送设备连接指令
                    await SendDeviceConnectCommandAsync(ct);
                    DNHper.NLogger.Info($"[KSV-LED] TCP 连接已建立 {_portOrIp}:{_baudRateOrPort}");
                }

                // 构造网口读取指令（0x22 命令）
                byte[] readCommand = BuildTcpReadCommand();
                await _networkStream!.WriteAsync(readCommand, 0, readCommand.Length, ct);
                DNHper.NLogger.Info($"[KSV-LED] TCP 发送读取亮度指令");

                // 读取响应（预期 26 字节）
                byte[] response = new byte[26];
                int bytesRead = await _networkStream.ReadAsync(response, 0, response.Length, ct);

                if (bytesRead < 18)
                {
                    DNHper.NLogger.Warn($"[KSV-LED] TCP 响应数据不足，实际: {bytesRead} 字节");
                    return 50; // 默认值
                }

                // 验证响应包头（前12字节）
                if (
                    response[0] == 0xD2
                    && response[1] == 0x02
                    && response[2] == 0x96
                    && response[3] == 0x49
                    && response[12] == 0x22
                    && response[13] == 0x20
                )
                {
                    byte contrast = response[16]; // 对比度
                    byte brightness = response[17]; // 亮度
                    DNHper.NLogger.Info($"[KSV-LED] TCP 读取成功，亮度: {brightness}, 对比度: {contrast}");
                    return brightness;
                }
                else
                {
                    DNHper.NLogger.Warn($"[KSV-LED] TCP 响应格式错误: {BitConverter.ToString(response)}");
                    return 50;
                }
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[KSV-LED] TCP 读取亮度失败: {ex.Message}");
                return 50;
            }
        }

        /// <summary>
        /// 构造网口读取指令（0x22 命令）
        /// </summary>
        private byte[] BuildTcpReadCommand()
        {
            // 网口读取指令格式（26字节）
            // D2 02 96 49 1A 00 00 00 00 00 00 00 22 20 06 00 00 AB 11 3A 00 02 ...
            byte[] frame = new byte[26];

            // 固定包头
            frame[0] = 0xD2;
            frame[1] = 0x02;
            frame[2] = 0x96;
            frame[3] = 0x49;
            frame[4] = 0x1A;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            frame[8] = 0x00;
            frame[9] = 0x00;
            frame[10] = 0x00;
            frame[11] = 0x00;
            frame[12] = 0x22; // 命令字：读取
            frame[13] = 0x20;
            frame[14] = 0x06;
            frame[15] = 0x00;
            frame[16] = 0x00;
            frame[17] = 0xAB;
            frame[18] = 0x11;
            frame[19] = 0x3A;
            frame[20] = 0x00;
            frame[21] = 0x02;

            // 固定包尾（根据协议文档补充）
            frame[22] = 0x2E;
            frame[23] = 0xFD;
            frame[24] = 0x69;
            frame[25] = 0xB6;

            return frame;
        }
    }
}
