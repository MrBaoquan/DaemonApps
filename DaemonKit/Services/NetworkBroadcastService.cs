using System;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DaemonKit.Models;
using DNHper;
using Hardware.Info;
using Newtonsoft.Json;

namespace DaemonKit.Services
{
    /// <summary>
    /// 网络广播服务
    /// 职责：
    /// 1. UDP 广播设备信息 (MachineInfo)
    /// 2. 接收远程控制命令和心跳 (Command) — 统一通过 ControlPort 接收
    /// </summary>
    public class NetworkBroadcastService : IDisposable
    {
        private UdpClient? _metaDataClient;
        private IDisposable? _broadcastDisposable;
        private IDisposable? _commandDisposable;
        private CancellationTokenSource? _receiveCts;

        private HardwareInfo? _hardwareInfo;
        private readonly MachineInfo _machineInfo;
        private ProcessItem? _rootProcessNode;
        private int _broadcastInterval = 3000; // 默认3秒

        private volatile bool _hardwareInfoReady = false;
        private Action? _hardwareInfoReadyCallback;

        public NetworkBroadcastService()
        {
            _machineInfo = new MachineInfo();

            // 异步预热硬件信息，避免阻塞主线程启动
            // 关键：HardwareInfo 必须在 MTA 线程（Task.Run）中创建和使用。
            // WMI 内部使用 COM 对象，若在 STA（UI 线程）上创建，后续从 MTA 线程调用
            // Refresh* 方法时 COM 会将所有调用编排回 STA 执行，独占 UI 线程导致卡死。
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _hardwareInfo = new HardwareInfo();
                    _hardwareInfo.RefreshAll();
                    _hardwareInfoReady = true;
                    NLogger.Info("[Network] 硬件信息预热完成");

                    // 通知主窗口更新显示
                    _hardwareInfoReadyCallback?.Invoke();
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[Network] 硬件信息预热失败: {Message}", ex.Message);
                }
            });
        }

        /// <summary>
        /// 设置硬件信息就绪时的回调
        /// </summary>
        public void SetHardwareInfoReadyCallback(Action callback)
        {
            _hardwareInfoReadyCallback = callback;
            // 如果已经就绪，立即调用
            if (_hardwareInfoReady)
            {
                callback?.Invoke();
            }
        }

        /// <summary>
        /// 接收到的控制命令流
        /// </summary>
        public IObservable<Command> CommandStream { get; private set; } =
            Observable.Empty<Command>();

        /// <summary>
        /// 获取当前设备信息快照（供其他服务读取，如 P2P MachineInfo 响应）
        /// </summary>
        public MachineInfo CurrentMachineInfo
        {
            get
            {
                UpdateMachineInfo();
                return _machineInfo;
            }
        }

        /// <summary>
        /// 启动网络广播和命令接收
        /// </summary>
        /// <param name="rootProcessNode">根进程节点（用于填充设备信息）</param>
        /// <param name="broadcastIntervalMs">广播间隔（毫秒），默认3000ms</param>
        public void Start(ProcessItem rootProcessNode, int broadcastIntervalMs = 3000)
        {
            _rootProcessNode = rootProcessNode;
            _broadcastInterval = broadcastIntervalMs;

            StartBroadcast();
            StartCommandReceiver();
        }

        /// <summary>
        /// 启动设备信息广播
        /// </summary>
        private void StartBroadcast()
        {
            _metaDataClient = new UdpClient();
            _metaDataClient.EnableBroadcast = true;

            _broadcastDisposable = Observable
                .Timer(TimeSpan.FromMilliseconds(_broadcastInterval), TimeSpan.FromSeconds(3))
                .Subscribe(_ =>
                {
                    try
                    {
                        UpdateMachineInfo();
                        SendBroadcast();
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error("设备信息广播异常: {Message}", ex.Message);
                    }
                });

            NLogger.Info("网络广播服务已启动");
        }

        /// <summary>
        /// 更新机器信息
        /// </summary>
        private void UpdateMachineInfo()
        {
            if (_rootProcessNode == null)
                return;

            // 获取设备名称：如果根节点名称是默认值"进程树"，则尝试使用第一个子节点的名称
            var deviceName = _rootProcessNode.Name;
            if (IsDefaultTreeName(deviceName) && _rootProcessNode.Children?.Count > 0)
            {
                var firstChild = _rootProcessNode.Children[0];
                if (
                    !string.IsNullOrWhiteSpace(firstChild.Name)
                    && !IsDefaultTreeName(firstChild.Name)
                )
                {
                    deviceName = firstChild.Name;
                }
            }
            _machineInfo.Name = deviceName;

            _machineInfo.IPs = new System.Collections.ObjectModel.ObservableCollection<string>(
                HardwareInfo.GetLocalIPv4Addresses().Select(ip => ip.ToString())
            );

            // 若硬件信息尚未准备好，跳过昂贵的采集以避免阻塞
            if (_hardwareInfoReady)
            {
                _machineInfo.CPUs = new System.Collections.ObjectModel.ObservableCollection<string>(
                    _hardwareInfo.CpuList.Select(cpu => cpu.Name)
                );

                _machineInfo.GPUs = new System.Collections.ObjectModel.ObservableCollection<string>(
                    _hardwareInfo.VideoControllerList.Select(gpu => gpu.Name)
                );

                _machineInfo.Memories =
                    new System.Collections.ObjectModel.ObservableCollection<string>(
                        _hardwareInfo.MemoryList.Select(
                            mem => mem.Manufacturer + mem.PartNumber + mem.Capacity.FormatBytes()
                        )
                    );
            }
        }

        /// <summary>
        /// 发送 UDP 广播
        /// </summary>
        private void SendBroadcast()
        {
            if (_metaDataClient == null)
                return;

            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_machineInfo));

            _metaDataClient.Send(
                data,
                data.Length,
                new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, CommonVars.MetaPort)
            );
        }

        /// <summary>
        /// 启动命令接收器（控制指令和心跳统一通过 ControlPort 接收）
        /// </summary>
        private void StartCommandReceiver()
        {
            _receiveCts = new CancellationTokenSource();

            CommandStream = Observable.Create<Command>(observer =>
            {
                var cts = new CancellationTokenSource();

                UdpClient? commandClient = null;

                var disposable = Disposable.Create(() =>
                {
                    cts.Cancel();

                    try
                    {
                        commandClient?.Close();
                    }
                    catch { }

                    commandClient?.Dispose();
                    cts.Dispose();
                });

                try
                {
                    commandClient = new UdpClient(CommonVars.ControlPort);

                    // 统一命令接收任务（控制指令 + 心跳均走 ControlPort）
                    Task.Run(
                        async () =>
                        {
                            while (!cts.Token.IsCancellationRequested)
                            {
                                try
                                {
                                    var result = await commandClient
                                        .ReceiveAsync()
                                        .ConfigureAwait(false);
                                    var cmdStr = Encoding.UTF8.GetString(result.Buffer);

                                    Command cmd;
                                    try
                                    {
                                        cmd = JsonConvert.DeserializeObject<Command>(cmdStr);
                                    }
                                    catch (JsonReaderException)
                                    {
                                        // 修复非法 JSON 转义序列（如 Windows 路径中的 \U, \D 等未转义反斜杠）
                                        var fixedStr = Regex.Replace(
                                            cmdStr,
                                            @"\\(?![""\\\/bfnrtu])",
                                            @"\\"
                                        );
                                        NLogger.Debug("自动修复非法 JSON 转义: {Original}", cmdStr);
                                        try
                                        {
                                            cmd = JsonConvert.DeserializeObject<Command>(fixedStr);
                                        }
                                        catch (Exception retryEx)
                                        {
                                            NLogger.Error(
                                                "修复后仍无法解析命令: {Message}, 原始数据: {Raw}",
                                                retryEx.Message,
                                                cmdStr
                                            );
                                            continue;
                                        }
                                    }

                                    if (cmd == null)
                                    {
                                        NLogger.Warn("接收到无效的命令数据（反序列化为null）");
                                        continue;
                                    }

                                    // 认证校验：如果本机启用了认证，则验证令牌
                                    if (
                                        CommonVars.IsAuthEnabled && cmd.EventID != Command.HEARTBEAT
                                    )
                                    {
                                        if (cmd.Token != CommonVars.AuthToken)
                                        {
                                            NLogger.Warn(
                                                "拒绝未认证的命令: EventID={EventID}, 来源={Source}",
                                                cmd.EventID,
                                                result.RemoteEndPoint
                                            );
                                            continue;
                                        }
                                    }

                                    // 将 UDP 来源 IP 注入 Command，下游无需在 data 中携带 requesterIP
                                    cmd.SenderIP = result.RemoteEndPoint.Address.ToString();

                                    observer.OnNext(cmd);
                                }
                                catch (ObjectDisposedException)
                                {
                                    // UdpClient 已关闭，退出循环
                                    break;
                                }
                                catch (SocketException) when (cts.Token.IsCancellationRequested)
                                {
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    if (!cts.Token.IsCancellationRequested)
                                    {
                                        NLogger.Error("接收控制命令异常: {Message}", ex.Message);
                                    }
                                    // 继续循环，不因单条消息异常而中断整个接收服务
                                }
                            }
                        },
                        cts.Token
                    );
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                }

                return disposable;
            });

            NLogger.Info("命令接收服务已启动（ControlPort={Port}）", CommonVars.ControlPort);
        }

        /// <summary>
        /// 停止所有网络服务
        /// </summary>
        public void Stop()
        {
            Dispose();
        }

        public void Dispose()
        {
            _broadcastDisposable?.Dispose();
            _commandDisposable?.Dispose();
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();

            try
            {
                _metaDataClient?.Close();
            }
            catch { }

            _metaDataClient?.Dispose();
            _metaDataClient = null;

            NLogger.Info("网络广播服务已停止");
        }

        /// <summary>
        /// 检查名称是否为默认的进程树名称
        /// </summary>
        private static bool IsDefaultTreeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            // 常见的默认名称格式
            var defaultNames = new[] { "进程树", "[ 进程树 ]", "[进程树]", "ProcessTree", "Root" };

            return defaultNames.Any(
                d => name.Equals(d, StringComparison.OrdinalIgnoreCase) || name.Contains("进程树")
            );
        }
    }
}
