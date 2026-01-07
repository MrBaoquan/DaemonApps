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

        // TCP 模式：追踪连接指令是否已发送（避免重复发送）
        private bool _tcpConnectCommandSent = false;

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

                DNHper.NLogger.Debug(
                    $"[KSV-LED] SetBrightnessAsync: 输入百分比={brightness}%, 计算后的0-255值={hexBrightness}"
                );

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
                        _tcpClient.ReceiveTimeout = 3000; // 设置3秒超时
                        _tcpClient.SendTimeout = 3000;
                        await _tcpClient.ConnectAsync(_portOrIp, _baudRateOrPort);
                        _networkStream = _tcpClient.GetStream();

                        // 发送设备连接指令（网口协议要求，仅在建立新连接时发送一次）
                        await SendDeviceConnectCommandAsync(ct);
                        _tcpConnectCommandSent = true;
                        DNHper.NLogger.Info($"[KSV-LED] TCP 连接已建立 {_portOrIp}:{_baudRateOrPort}");
                        DNHper.NLogger.Info($"[KSV-LED] 提示: 请确认该地址为KSV系列LED控制器（KSV24c/KSV12c等）");
                    }

                    // 构造网口指令（长帧格式，第21字节为亮度）
                    byte[] command = BuildTcpCommand(brightness, 0x40); // 对比度默认 64

                    DNHper.NLogger.Info($"[KSV-LED] TCP 目标地址: {_portOrIp}:{_baudRateOrPort}");
                    DNHper.NLogger.Debug(
                        $"[KSV-LED] TCP 发送亮度指令数据: {BitConverter.ToString(command)}"
                    );

                    await _networkStream!.WriteAsync(command, 0, command.Length, ct);
                    DNHper.NLogger.Info($"[KSV-LED] TCP 发送亮度指令，亮度值: {brightness}");

                    // 等待并检查是否有响应数据（协议未明确要求响应，但某些设备可能返回）
                    await Task.Delay(50, ct);
                    if (_networkStream.DataAvailable)
                    {
                        byte[] responseBuffer = new byte[256];
                        int responseBytesRead = await _networkStream.ReadAsync(
                            responseBuffer,
                            0,
                            responseBuffer.Length,
                            ct
                        );
                        DNHper.NLogger.Debug(
                            $"[KSV-LED] TCP 接收设置亮度响应 (长度: {responseBytesRead}): {BitConverter.ToString(responseBuffer, 0, responseBytesRead)}"
                        );

                        // 检查是否为有效KSV响应
                        if (responseBytesRead > 0 && responseBytesRead < 18)
                        {
                            DNHper.NLogger.Warn(
                                $"[KSV-LED] 收到异常响应 (长度: {responseBytesRead})，可能连接了非KSV设备"
                            );
                        }
                    }
                    else
                    {
                        DNHper.NLogger.Debug($"[KSV-LED] TCP 设置亮度无响应数据（符合预期）");
                    }

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
                    _tcpConnectCommandSent = false; // 重置连接指令标志

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
            // D2 02 96 49 1A 00 00 00 00 00 00 00 21 20 06 00 00 AC 01 3A [对比度] [亮度] 2E FD 69 B6
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
            frame[20] = contrast; // 对比度值（在前）
            frame[21] = brightness; // 亮度值（在后）

            DNHper.NLogger.Debug(
                $"[KSV-LED] BuildTcpCommand: 亮度字节=0x{brightness:X2}({brightness}), 对比度字节=0x{contrast:X2}({contrast}), 帧数据[20-21]=0x{frame[20]:X2}{frame[21]:X2}"
            );

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
                _tcpConnectCommandSent = false; // 重置连接指令标志
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

                    // 添加 2 秒连接超时，防止网络延迟卡顿
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            await _tcpClient.ConnectAsync(
                                _portOrIp,
                                _baudRateOrPort,
                                connectCts.Token
                            );
                        }
                        catch (OperationCanceledException)
                        {
                            DNHper.NLogger.Error(
                                $"[KSV-LED] TCP 连接超时 {_portOrIp}:{_baudRateOrPort}（2秒）"
                            );
                            _tcpClient.Dispose();
                            _tcpClient = null;
                            return 50; // 返回默认值
                        }
                    }

                    _networkStream = _tcpClient.GetStream();

                    // 发送设备连接指令（仅在建立新连接时发送一次）
                    if (!_tcpConnectCommandSent)
                    {
                        await SendDeviceConnectCommandAsync(ct);
                        _tcpConnectCommandSent = true;
                    }
                    DNHper.NLogger.Info($"[KSV-LED] TCP 连接已建立 {_portOrIp}:{_baudRateOrPort}");
                }

                // 清空接收缓冲区（避免旧数据干扰）
                if (_networkStream!.DataAvailable)
                {
                    byte[] discardBuffer = new byte[256];
                    while (_networkStream.DataAvailable)
                    {
                        await _networkStream.ReadAsync(discardBuffer, 0, discardBuffer.Length, ct);
                        await Task.Delay(10, ct); // 等待可能的后续数据
                    }
                    DNHper.NLogger.Debug($"[KSV-LED] TCP 已清空接收缓冲区");
                }

                // 构造网口读取指令（0x22 命令）
                byte[] readCommand = BuildTcpReadCommand();

                DNHper.NLogger.Info($"[KSV-LED] TCP 目标地址: {_portOrIp}:{_baudRateOrPort}");
                DNHper.NLogger.Debug(
                    $"[KSV-LED] TCP 发送读取指令数据: {BitConverter.ToString(readCommand)}"
                );

                await _networkStream.WriteAsync(readCommand, 0, readCommand.Length, ct);
                DNHper.NLogger.Info($"[KSV-LED] TCP 发送读取亮度指令");

                // 等待设备响应
                await Task.Delay(50, ct);

                // 读取响应（预期 26 字节）
                byte[] response = new byte[26];
                int bytesRead = await _networkStream.ReadAsync(response, 0, response.Length, ct);

                DNHper.NLogger.Debug(
                    $"[KSV-LED] TCP 原始响应 (长度: {bytesRead}): {BitConverter.ToString(response, 0, bytesRead)}"
                );

                if (bytesRead < 18)
                {
                    DNHper.NLogger.Error($"[KSV-LED] TCP 响应数据不足 (实际: {bytesRead} 字节，预期: 26 字节)");
                    DNHper.NLogger.Error($"[KSV-LED] 可能原因: 连接到了错误的设备或设备不支持KSV协议");
                    DNHper.NLogger.Error(
                        $"[KSV-LED] 请检查目标地址 {_portOrIp}:{_baudRateOrPort} 是否为正确的KSV LED设备"
                    );
                    return 50; // 默认值
                }

                // 查找有效数据包起始位置（搜索完整帧头 D2-02-96-49）
                int validPacketIndex = -1;
                for (int i = 0; i <= bytesRead - 18; i++)
                {
                    if (
                        response[i] == 0xD2
                        && response[i + 1] == 0x02
                        && response[i + 2] == 0x96
                        && response[i + 3] == 0x49
                    )
                    {
                        // 检查后续是否有完整的命令字段
                        if (
                            i + 13 < bytesRead
                            && response[i + 12] == 0x22
                            && response[i + 13] == 0x20
                        )
                        {
                            validPacketIndex = i;
                            break;
                        }
                    }
                }

                if (validPacketIndex >= 0 && validPacketIndex + 18 <= bytesRead)
                {
                    byte contrast = response[validPacketIndex + 16]; // 对比度
                    byte brightness = response[validPacketIndex + 17]; // 亮度
                    DNHper.NLogger.Info(
                        $"[KSV-LED] TCP 读取成功（偏移{validPacketIndex}），亮度: {brightness}, 对比度: {contrast}"
                    );
                    return brightness;
                }
                else
                {
                    DNHper.NLogger.Error($"[KSV-LED] TCP 响应格式错误: 未找到有效的KSV协议帧头 (D2-02-96-49)");
                    DNHper.NLogger.Error(
                        $"[KSV-LED] 收到数据: {BitConverter.ToString(response, 0, bytesRead)}"
                    );
                    DNHper.NLogger.Error($"[KSV-LED] 这不是有效的KSV LED控制器响应，请确认设备类型和连接地址");
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
            // D2 02 96 49 1A 00 00 00 00 00 00 00 22 20 06 00 00 AB 11 3A [保留] [保留] 2E FD 69 B6
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
            frame[20] = 0x00; // 读取命令的位置20保留为0x00
            frame[21] = 0x00; // 读取命令的位置21保留为0x00

            // 固定包尾（根据协议文档补充）
            frame[22] = 0x2E;
            frame[23] = 0xFD;
            frame[24] = 0x69;
            frame[25] = 0xB6;

            return frame;
        }
    }
}
