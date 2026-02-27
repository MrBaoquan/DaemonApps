using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DaemonKit.Models;
using DNHper;
using Newtonsoft.Json;
using Splat;

namespace DaemonKit.Services
{
    /// <summary>
    /// 设备发现模式
    /// </summary>
    public enum DiscoveryMode
    {
        /// <summary>仅UDP广播（同子网）</summary>
        BroadcastOnly,

        /// <summary>仅手动配置设备</summary>
        ManualOnly,

        /// <summary>混合模式（广播+手动配置）</summary>
        Hybrid
    }

    /// <summary>
    /// 设备发现服务
    /// 支持：
    /// 1. UDP广播发现（同子网）
    /// 2. 手动配置设备列表（跨路由器）
    /// 3. TCP直连探测（跨路由器）
    /// 使用响应式编程
    /// </summary>
    public class DeviceDiscoveryService : IDisposable
    {
        #region 常量

        // UDP广播端口使用 CommonVars 可配置端口
        private static int UDP_BROADCAST_PORT => CommonVars.MetaPort;

        // TCP探测端口（复用文件传输端口，可配置）
        private static int TCP_PROBE_PORT => CommonVars.FileTransferPort;
        private const int PROBE_TIMEOUT_MS = 3000; // 探测超时
        private const int OFFLINE_THRESHOLD_SECONDS = 15; // 离线判定阈值
        #endregion

        #region 字段

        private readonly CompositeDisposable _disposables = new();
        private readonly Subject<MachineInfoExtended> _deviceDiscovered = new();
        private readonly Subject<string> _deviceOffline = new();
        private readonly BehaviorSubject<DiscoveryMode> _discoveryMode;

        private UdpClient? _broadcastListener;
        private CancellationTokenSource? _listenerCts;

        // 设备缓存 (ID -> 设备信息，优先使用 MachineInfo.ID 去重)
        private readonly Dictionary<string, MachineInfoExtended> _deviceCache = new();
        private readonly object _cacheLock = new();

        // 手动配置的设备IP列表
        private readonly HashSet<string> _manualDevices = new();
        private readonly object _manualLock = new();

        // P2P 服务引用（用于 TCP 设备信息交换）
        private P2PFileTransferService? _p2pService;

        // 手动设备周期性重探测间隔（秒）
        private const int MANUAL_REPROBE_INTERVAL_SECONDS = 15;

        // 配置文件路径
        private readonly string _configFilePath;

        #endregion

        #region 属性

        /// <summary>
        /// 发现的设备流（响应式）
        /// </summary>
        public IObservable<MachineInfoExtended> DeviceDiscovered =>
            _deviceDiscovered.AsObservable();

        /// <summary>
        /// 设备离线流（响应式）
        /// </summary>
        public IObservable<string> DeviceOffline => _deviceOffline.AsObservable();

        /// <summary>
        /// 当前发现模式
        /// </summary>
        public IObservable<DiscoveryMode> CurrentMode => _discoveryMode.AsObservable();

        /// <summary>
        /// 所有在线设备（只读快照）
        /// </summary>
        public IReadOnlyList<MachineInfoExtended> OnlineDevices
        {
            get
            {
                lock (_cacheLock)
                {
                    return _deviceCache.Values
                        .Where(d => d.Status == MachineStatus.Online)
                        .ToList();
                }
            }
        }

        /// <summary>
        /// 手动配置的设备列表
        /// </summary>
        public IReadOnlyList<string> ManualDevices
        {
            get
            {
                lock (_manualLock)
                {
                    return _manualDevices.ToList();
                }
            }
        }

        #endregion

        #region 构造函数

        public DeviceDiscoveryService(DiscoveryMode initialMode = DiscoveryMode.Hybrid)
        {
            _discoveryMode = new BehaviorSubject<DiscoveryMode>(initialMode);
            _configFilePath = Utilities.AppPathes.DeviceDiscoveryConfigPath;

            // 加载配置
            LoadConfiguration();

            // 设置离线检测定时器
            SetupOfflineDetection();

            // 设置手动设备周期性重探测
            SetupManualDeviceReprobe();
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 启动设备发现服务
        /// </summary>
        public void Start()
        {
            var mode = _discoveryMode.Value;

            if (mode == DiscoveryMode.BroadcastOnly || mode == DiscoveryMode.Hybrid)
            {
                StartBroadcastListener();
            }

            if (mode == DiscoveryMode.ManualOnly || mode == DiscoveryMode.Hybrid)
            {
                // 对手动配置的设备进行探测
                ProbeManualDevices();
            }

            NLogger.Info("[Discovery] 设备发现服务已启动，模式: {Mode}", mode);
        }

        /// <summary>
        /// 停止设备发现服务
        /// </summary>
        public void Stop()
        {
            StopBroadcastListener();
            NLogger.Info("[Discovery] 设备发现服务已停止");
        }

        /// <summary>
        /// 切换发现模式
        /// </summary>
        public void SetMode(DiscoveryMode mode)
        {
            var oldMode = _discoveryMode.Value;
            if (oldMode == mode)
                return;

            _discoveryMode.OnNext(mode);

            // 根据新模式调整
            if (mode == DiscoveryMode.ManualOnly)
            {
                StopBroadcastListener();
            }
            else if (oldMode == DiscoveryMode.ManualOnly)
            {
                StartBroadcastListener();
            }

            if (mode == DiscoveryMode.ManualOnly || mode == DiscoveryMode.Hybrid)
            {
                ProbeManualDevices();
            }

            NLogger.Info("[Discovery] 切换模式: {OldMode} -> {NewMode}", oldMode, mode);
        }

        /// <summary>
        /// 添加手动配置的设备
        /// </summary>
        public void AddManualDevice(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return;

            lock (_manualLock)
            {
                if (_manualDevices.Add(ipAddress))
                {
                    SaveConfiguration();
                    NLogger.Info("[Discovery] 添加手动设备: {IpAddress}", ipAddress);

                    // 立即探测
                    _ = ProbeDeviceAsync(ipAddress);
                }
            }
        }

        /// <summary>
        /// 批量添加手动设备
        /// </summary>
        public void AddManualDevices(IEnumerable<string> ipAddresses)
        {
            foreach (var ip in ipAddresses)
            {
                AddManualDevice(ip);
            }
        }

        /// <summary>
        /// 移除手动配置的设备
        /// </summary>
        public void RemoveManualDevice(string ipAddress)
        {
            lock (_manualLock)
            {
                if (_manualDevices.Remove(ipAddress))
                {
                    SaveConfiguration();
                    NLogger.Info("[Discovery] 移除手动设备: {IpAddress}", ipAddress);
                }
            }
        }

        /// <summary>
        /// 手动探测所有配置的设备
        /// </summary>
        public void ProbeManualDevices()
        {
            List<string> devices;
            lock (_manualLock)
            {
                devices = _manualDevices.ToList();
            }

            if (devices.Count == 0)
            {
                NLogger.Info("[Discovery] 没有手动配置的设备需要探测");
                return;
            }

            NLogger.Info("[Discovery] 开始探测 {Count} 个手动配置的设备", devices.Count);

            // 并行探测所有设备
            Observable
                .FromAsync(async () =>
                {
                    var tasks = devices.Select(ip => ProbeDeviceAsync(ip));
                    await Task.WhenAll(tasks);
                })
                .Subscribe(
                    _ => { },
                    ex => NLogger.Error("[Discovery] 探测设备异常: {Message}", ex.Message)
                );
        }

        /// <summary>
        /// 探测单个设备（TCP 连接探测 + MachineInfo 信息交换）
        /// </summary>
        public async Task<bool> ProbeDeviceAsync(string ipAddress)
        {
            try
            {
                NLogger.Info("[Discovery] 正在探测设备: {IpAddress}", ipAddress);

                // 尝试通过 P2P TCP 通道获取完整 MachineInfo
                _p2pService ??= Locator.Current.GetService<P2PFileTransferService>();

                MachineInfo? remoteInfo = null;
                if (_p2pService != null)
                {
                    try
                    {
                        remoteInfo = await _p2pService.RequestRemoteMachineInfoAsync(
                            ipAddress,
                            PROBE_TIMEOUT_MS
                        );
                    }
                    catch (Exception ex)
                    {
                        NLogger.Warn(
                            "[Discovery] TCP 设备信息请求失败，回退到 TCP 握手探测: {IpAddress} - {Message}",
                            ipAddress,
                            ex.Message
                        );
                    }
                }

                // 如果 P2P 请求失败，回退到纯 TCP 握手探测
                if (remoteInfo == null)
                {
                    using var client = new TcpClient();
                    using var cts = new CancellationTokenSource(PROBE_TIMEOUT_MS);
                    await client.ConnectAsync(ipAddress, TCP_PROBE_PORT, cts.Token);

                    if (!client.Connected)
                    {
                        MarkDeviceOffline(ipAddress);
                        return false;
                    }
                }

                // 创建或更新设备信息
                var device = new MachineInfoExtended
                {
                    ID = remoteInfo?.ID ?? ipAddress,
                    Name = remoteInfo?.Name ?? $"Device-{ipAddress}",
                    IPs =
                        remoteInfo?.IPs
                        ?? new System.Collections.ObjectModel.ObservableCollection<string>
                        {
                            ipAddress
                        },
                    CPUs = remoteInfo?.CPUs,
                    GPUs = remoteInfo?.GPUs,
                    Memories = remoteInfo?.Memories,
                    Status = MachineStatus.Online,
                    LastSeen = DateTime.Now,
                    IsManuallyAdded = true
                };

                // 确保 ID 不为空
                if (string.IsNullOrEmpty(device.ID))
                {
                    device.ID = ipAddress;
                }

                NLogger.Info(
                    "[Discovery] 设备在线: {IpAddress}，名称: {DeviceName}，信息来源: {Source}",
                    ipAddress,
                    device.Name,
                    remoteInfo != null ? "MachineInfo交换" : "TCP握手"
                );

                UpdateDeviceCache(device);
                _deviceDiscovered.OnNext(device);
                return true;
            }
            catch (OperationCanceledException)
            {
                NLogger.Warn("[Discovery] 探测超时: {IpAddress}", ipAddress);
            }
            catch (SocketException ex)
            {
                NLogger.Warn("[Discovery] 探测失败: {IpAddress} - {Message}", ipAddress, ex.Message);
            }
            catch (Exception ex)
            {
                NLogger.Error("[Discovery] 探测异常: {IpAddress} - {Message}", ipAddress, ex.Message);
            }

            // 标记离线
            MarkDeviceOffline(ipAddress);
            return false;
        }

        /// <summary>
        /// 处理接收到的广播设备信息
        /// </summary>
        public void HandleBroadcastDevice(MachineInfo machineInfo, string sourceIP)
        {
            if (machineInfo == null)
                return;

            var extended =
                machineInfo as MachineInfoExtended
                ?? new MachineInfoExtended
                {
                    ID = machineInfo.ID ?? sourceIP,
                    Name = machineInfo.Name,
                    IPs =
                        machineInfo.IPs
                        ?? new System.Collections.ObjectModel.ObservableCollection<string>
                        {
                            sourceIP
                        },
                    CPUs = machineInfo.CPUs,
                    GPUs = machineInfo.GPUs,
                    Memories = machineInfo.Memories,
                    Status = MachineStatus.Online,
                    LastSeen = DateTime.Now,
                    IsManuallyAdded = false
                };

            // 确保ID不为空
            if (string.IsNullOrEmpty(extended.ID))
            {
                extended.ID = sourceIP;
            }

            UpdateDeviceCache(extended);
            _deviceDiscovered.OnNext(extended);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 启动UDP广播监听
        /// </summary>
        private void StartBroadcastListener()
        {
            if (_broadcastListener != null)
                return;

            try
            {
                _listenerCts = new CancellationTokenSource();
                _broadcastListener = new UdpClient(UDP_BROADCAST_PORT);

                // 使用RX处理广播接收
                var listenerObservable = Observable.Create<(MachineInfo, string)>(observer =>
                {
                    var cts = _listenerCts;

                    Task.Run(
                        async () =>
                        {
                            while (!cts.Token.IsCancellationRequested)
                            {
                                try
                                {
                                    var result = await _broadcastListener.ReceiveAsync(cts.Token);
                                    var json = Encoding.UTF8.GetString(result.Buffer);
                                    var machineInfo = JsonConvert.DeserializeObject<MachineInfo>(
                                        json
                                    );
                                    var sourceIP = result.RemoteEndPoint.Address.ToString();

                                    if (machineInfo != null)
                                    {
                                        observer.OnNext((machineInfo, sourceIP));
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    NLogger.Warn("[Discovery] 广播接收异常: {Message}", ex.Message);
                                }
                            }
                            observer.OnCompleted();
                        },
                        cts.Token
                    );

                    return Disposable.Create(() =>
                    {
                        cts.Cancel();
                    });
                });

                var subscription = listenerObservable.Subscribe(
                    tuple => HandleBroadcastDevice(tuple.Item1, tuple.Item2),
                    ex => NLogger.Error("[Discovery] 广播监听错误: {Message}", ex.Message)
                );

                _disposables.Add(subscription);
                NLogger.Info("[Discovery] UDP广播监听已启动");
            }
            catch (Exception ex)
            {
                NLogger.Error("[Discovery] 启动广播监听失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 停止UDP广播监听
        /// </summary>
        private void StopBroadcastListener()
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;

            try
            {
                _broadcastListener?.Close();
            }
            catch { }

            _broadcastListener?.Dispose();
            _broadcastListener = null;

            NLogger.Info("[Discovery] UDP广播监听已停止");
        }

        /// <summary>
        /// 设置离线检测定时器
        /// </summary>
        private void SetupOfflineDetection()
        {
            var offlineCheckSubscription = Observable
                .Interval(TimeSpan.FromSeconds(5))
                .Subscribe(_ => CheckOfflineDevices());

            _disposables.Add(offlineCheckSubscription);
        }

        /// <summary>
        /// 设置手动设备周期性重探测（每 MANUAL_REPROBE_INTERVAL_SECONDS 秒重新探测所有手动设备）
        /// </summary>
        private void SetupManualDeviceReprobe()
        {
            var reprobeSubscription = Observable
                .Interval(TimeSpan.FromSeconds(MANUAL_REPROBE_INTERVAL_SECONDS))
                .Subscribe(_ =>
                {
                    var mode = _discoveryMode.Value;
                    if (mode == DiscoveryMode.ManualOnly || mode == DiscoveryMode.Hybrid)
                    {
                        ProbeManualDevices();
                    }
                });

            _disposables.Add(reprobeSubscription);
        }

        /// <summary>
        /// 检查离线设备
        /// </summary>
        private void CheckOfflineDevices()
        {
            var now = DateTime.Now;
            var offlineThreshold = TimeSpan.FromSeconds(OFFLINE_THRESHOLD_SECONDS);

            List<string> offlineDevices = new();

            lock (_cacheLock)
            {
                foreach (var kvp in _deviceCache)
                {
                    var device = kvp.Value;
                    if (
                        device.Status == MachineStatus.Online
                        && now - device.LastSeen > offlineThreshold
                    )
                    {
                        device.Status = MachineStatus.Offline;
                        offlineDevices.Add(kvp.Key);
                    }
                }
            }

            foreach (var deviceId in offlineDevices)
            {
                _deviceOffline.OnNext(deviceId);
                NLogger.Info("[Discovery] 设备离线: {DeviceId}", deviceId);
            }
        }

        /// <summary>
        /// 更新设备缓存（使用 ID 作为主键，避免 DHCP 换 IP 导致设备重复）
        /// </summary>
        private void UpdateDeviceCache(MachineInfoExtended device)
        {
            lock (_cacheLock)
            {
                // 优先使用 ID 作为缓存键，回退到首个 IP
                var key = !string.IsNullOrEmpty(device.ID)
                    ? device.ID
                    : device.IPs?.FirstOrDefault() ?? "unknown";

                if (string.IsNullOrEmpty(key) || key == "unknown")
                    return;

                // 检查是否存在旧的 IP-based key，若设备 ID 与 IP 不同则清除旧条目
                var firstIp = device.IPs?.FirstOrDefault();
                if (firstIp != null && firstIp != key && _deviceCache.ContainsKey(firstIp))
                {
                    _deviceCache.Remove(firstIp);
                }

                if (_deviceCache.TryGetValue(key, out var existing))
                {
                    // 保留手动添加标记
                    var isManual = existing.IsManuallyAdded || device.IsManuallyAdded;

                    // 更新现有设备
                    existing.Name = device.Name ?? existing.Name;
                    existing.IPs = device.IPs ?? existing.IPs;
                    existing.CPUs = device.CPUs ?? existing.CPUs;
                    existing.GPUs = device.GPUs ?? existing.GPUs;
                    existing.Memories = device.Memories ?? existing.Memories;
                    existing.Status = MachineStatus.Online;
                    existing.LastSeen = DateTime.Now;
                    existing.IsManuallyAdded = isManual;
                }
                else
                {
                    device.LastSeen = DateTime.Now;
                    device.Status = MachineStatus.Online;
                    _deviceCache[key] = device;
                }
            }
        }

        /// <summary>
        /// 标记设备离线（支持按 ID 或 IP 查找）
        /// </summary>
        private void MarkDeviceOffline(string idOrIp)
        {
            lock (_cacheLock)
            {
                // 优先按 key 直接查找
                if (_deviceCache.TryGetValue(idOrIp, out var device))
                {
                    device.Status = MachineStatus.Offline;
                    _deviceOffline.OnNext(idOrIp);
                    return;
                }

                // 回退：按 IP 在所有设备中查找
                var match = _deviceCache.FirstOrDefault(
                    kvp => kvp.Value.IPs?.Contains(idOrIp) == true
                );
                if (match.Value != null)
                {
                    match.Value.Status = MachineStatus.Offline;
                    _deviceOffline.OnNext(match.Key);
                }
            }
        }

        /// <summary>
        /// 加载配置
        /// </summary>
        private void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonConvert.DeserializeObject<DiscoveryConfig>(json);

                    if (config != null)
                    {
                        _discoveryMode.OnNext(config.Mode);

                        lock (_manualLock)
                        {
                            _manualDevices.Clear();
                            if (config.ManualDevices != null)
                            {
                                foreach (var ip in config.ManualDevices)
                                {
                                    _manualDevices.Add(ip);
                                }
                            }
                        }

                        NLogger.Info(
                            $"[Discovery] 已加载配置，手动设备数: {config.ManualDevices?.Count ?? 0}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("[Discovery] 加载配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private void SaveConfiguration()
        {
            try
            {
                List<string> devices;
                lock (_manualLock)
                {
                    devices = _manualDevices.ToList();
                }

                var config = new DiscoveryConfig
                {
                    Mode = _discoveryMode.Value,
                    ManualDevices = devices
                };

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(_configFilePath, json);

                NLogger.Info("[Discovery] 配置已保存");
            }
            catch (Exception ex)
            {
                NLogger.Error("[Discovery] 保存配置失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Stop();
            _disposables.Dispose();
            _deviceDiscovered.Dispose();
            _deviceOffline.Dispose();
            _discoveryMode.Dispose();
        }

        #endregion

        #region 配置类

        private class DiscoveryConfig
        {
            public DiscoveryMode Mode { get; set; } = DiscoveryMode.Hybrid;
            public List<string>? ManualDevices { get; set; }
        }

        #endregion
    }
}
