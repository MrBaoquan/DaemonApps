using System;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
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
    /// 2. 接收远程控制命令 (Command)
    /// 3. 接收心跳命令 (Heartbeat)
    /// </summary>
    public class NetworkBroadcastService : IDisposable
    {
        private UdpClient? _metaDataClient;
        private IDisposable? _broadcastDisposable;
        private IDisposable? _commandDisposable;
        private CancellationTokenSource? _receiveCts;

        private readonly HardwareInfo _hardwareInfo;
        private readonly MachineInfo _machineInfo;
        private ProcessItem? _rootProcessNode;
        private int _broadcastInterval = 3000; // 默认3秒

        public NetworkBroadcastService()
        {
            _hardwareInfo = new HardwareInfo();
            _hardwareInfo.RefreshAll();
            _machineInfo = new MachineInfo();
        }

        /// <summary>
        /// 接收到的控制命令流
        /// </summary>
        public IObservable<Command> CommandStream { get; private set; } =
            Observable.Empty<Command>();

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
                        NLogger.Error($"设备信息广播异常: {ex.Message}");
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

            _machineInfo.Name = _rootProcessNode.Name;

            _machineInfo.IPs = new System.Collections.ObjectModel.ObservableCollection<string>(
                HardwareInfo.GetLocalIPv4Addresses().Select(ip => ip.ToString())
            );

            _machineInfo.CPUs = new System.Collections.ObjectModel.ObservableCollection<string>(
                _hardwareInfo.CpuList.Select(cpu => cpu.Name)
            );

            _machineInfo.GPUs = new System.Collections.ObjectModel.ObservableCollection<string>(
                _hardwareInfo.VideoControllerList.Select(gpu => gpu.Name)
            );

            _machineInfo.Memories = new System.Collections.ObjectModel.ObservableCollection<string>(
                _hardwareInfo.MemoryList.Select(
                    mem => mem.Manufacturer + mem.PartNumber + mem.Capacity.FormatBytes()
                )
            );
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
        /// 启动命令接收器
        /// </summary>
        private void StartCommandReceiver()
        {
            _receiveCts = new CancellationTokenSource();

            CommandStream = Observable.Create<Command>(observer =>
            {
                var cts = new CancellationTokenSource();

                UdpClient? commandClient = null;
                UdpClient? heartbeatClient = null;

                var disposable = Disposable.Create(() =>
                {
                    cts.Cancel();

                    try
                    {
                        commandClient?.Close();
                        heartbeatClient?.Close();
                    }
                    catch { }

                    commandClient?.Dispose();
                    heartbeatClient?.Dispose();
                    cts.Dispose();
                });

                try
                {
                    commandClient = new UdpClient(CommonVars.ControlPort);
                    heartbeatClient = new UdpClient(CommonVars.HeartbeatPort);

                    // 控制命令接收任务
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var result = await commandClient
                                        .ReceiveAsync()
                                        .ConfigureAwait(false);
                                    var cmdStr = Encoding.UTF8.GetString(result.Buffer);
                                    var cmd = JsonConvert.DeserializeObject<Command>(cmdStr);

                                    if (cmd != null)
                                    {
                                        observer.OnNext(cmd);
                                    }
                                    else
                                    {
                                        NLogger.Warn("接收到无效的命令数据（反序列化为null）");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    NLogger.Error($"接收控制命令异常: {ex.Message}");
                                }
                            }
                        },
                        cts.Token
                    );

                    // 心跳命令接收任务
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var result = await heartbeatClient
                                        .ReceiveAsync()
                                        .ConfigureAwait(false);
                                    var cmdStr = Encoding.UTF8.GetString(result.Buffer);
                                    var cmd = JsonConvert.DeserializeObject<Command>(cmdStr);

                                    if (cmd == null)
                                    {
                                        NLogger.Warn("接收到无效的心跳数据（反序列化为null）");
                                        continue;
                                    }

                                    if (cmd.EventID != Command.HEARTBEAT)
                                        continue;

                                    observer.OnNext(cmd);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    NLogger.Error($"接收心跳命令异常: {ex.Message}");
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

            NLogger.Info("命令接收服务已启动");
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
    }
}
