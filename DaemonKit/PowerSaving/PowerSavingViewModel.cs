using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;

namespace DaemonKit.PowerSaving
{
    public sealed class PowerSavingViewModel : ReactiveObject
    {
        private readonly PowerSavingManager _manager;
        private readonly BrightnessCoordinator _coordinator;
        private readonly CompositeDisposable _disposables = new();

        public ObservableCollection<DisplayControlItem> Displays { get; } = new();

        private byte _defaultNormalBrightness = 100;
        public byte DefaultNormalBrightness
        {
            get => _defaultNormalBrightness;
            set
            {
                if (_defaultNormalBrightness != value)
                {
                    _defaultNormalBrightness = value;
                    this.RaisePropertyChanged(nameof(DefaultNormalBrightness));

                    // 如果正常亮度降低，确保省电亮度同步收敛到新的上限
                    if (_defaultPowerSavingBrightness > value)
                    {
                        DefaultPowerSavingBrightness = value;
                    }
                }
            }
        }

        private byte _defaultPowerSavingBrightness = 56;
        public byte DefaultPowerSavingBrightness
        {
            get => _defaultPowerSavingBrightness;
            set
            {
                // 约束：省电亮度不能大于正常亮度
                var constrainedValue = (byte)Math.Min(value, DefaultNormalBrightness);
                if (_defaultPowerSavingBrightness != constrainedValue)
                {
                    _defaultPowerSavingBrightness = constrainedValue;
                    this.RaisePropertyChanged(nameof(DefaultPowerSavingBrightness));
                }
            }
        }

        private bool _isPowerSavingMode;
        public bool IsPowerSavingMode
        {
            get => _isPowerSavingMode;
            set
            {
                if (this.RaiseAndSetIfChanged(ref _isPowerSavingMode, value))
                {
                    // 自动切换模式
                    _ = (value ? ApplyPowerSavingAsync() : RestoreNormalAsync());
                }
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => this.RaiseAndSetIfChanged(ref _isBusy, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<Unit, Unit> ApplyPowerSavingCommand { get; }
        public ReactiveCommand<Unit, Unit> RestoreNormalCommand { get; }
        public ReactiveCommand<Unit, Unit> ApplyCustomBrightnessCommand { get; }

        public PowerSavingViewModel()
            : this(new PowerSavingManager()) { }

        public PowerSavingViewModel(PowerSavingManager manager)
        {
            _manager = manager;
            _coordinator = manager.Coordinator;

            RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
            ApplyPowerSavingCommand = ReactiveCommand.CreateFromTask(
                ApplyPowerSavingAsync,
                this.WhenAnyValue(_ => _.IsBusy, busy => !busy)
            );
            RestoreNormalCommand = ReactiveCommand.CreateFromTask(
                RestoreNormalAsync,
                this.WhenAnyValue(_ => _.IsBusy, busy => !busy)
            );
            ApplyCustomBrightnessCommand = ReactiveCommand.CreateFromTask(
                ApplyCustomBrightnessAsync,
                this.WhenAnyValue(_ => _.IsBusy, busy => !busy)
            );

            // 设置节流：亮度滑块调整后 300ms 才应用，避免频繁操作
            var normalBrightnessSubscription = this.WhenAnyValue(x => x.DefaultNormalBrightness)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Where(_ => !IsPowerSavingMode)
                .Subscribe(_ => Task.Run(SyncCurrentModeAsync));
            _disposables.Add(normalBrightnessSubscription);

            var powerSavingBrightnessSubscription = this.WhenAnyValue(
                    x => x.DefaultPowerSavingBrightness
                )
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Where(_ => IsPowerSavingMode)
                .Subscribe(_ => Task.Run(SyncCurrentModeAsync));
            _disposables.Add(powerSavingBrightnessSubscription);

            _ = RefreshAsync();
        }

        /// <summary>
        /// 从 AppSettings 加载设置
        /// </summary>
        public void LoadSettings(AppSettings settings)
        {
            if (settings == null)
                return;

            DefaultNormalBrightness = settings.PowerSavingNormalBrightness;
            DefaultPowerSavingBrightness = settings.PowerSavingLowBrightness;
            IsPowerSavingMode = settings.PowerSavingModeEnabled;

            // 等待显示器扫描完成后恢复独立配置
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // 等待 RefreshAsync 完成
                foreach (var config in settings.PowerSavingDisplayConfigs ?? new())
                {
                    var display = Displays.FirstOrDefault(
                        d => d.Identity.DeviceName == config.DeviceName
                    );
                    if (display != null)
                    {
                        display.OverrideEnabled = config.OverrideEnabled;
                        display.TargetBrightness = config.TargetBrightness;
                    }
                }
            });
        }

        /// <summary>
        /// 保存设置到 AppSettings
        /// </summary>
        public void SaveSettings(AppSettings settings)
        {
            if (settings == null)
                return;

            settings.PowerSavingModeEnabled = IsPowerSavingMode;
            settings.PowerSavingNormalBrightness = DefaultNormalBrightness;
            settings.PowerSavingLowBrightness = DefaultPowerSavingBrightness;

            // 保存每个显示器的独立配置
            settings.PowerSavingDisplayConfigs = Displays
                .Where(d => d.OverrideEnabled)
                .Select(
                    d =>
                        new DisplayConfig
                        {
                            DeviceName = d.Identity.DeviceName,
                            OverrideEnabled = d.OverrideEnabled,
                            TargetBrightness = d.TargetBrightness
                        }
                )
                .ToList();
        }

        private async Task RefreshAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                StatusMessage = "正在扫描显示器...";
                Displays.Clear();
                var displays = await _coordinator.DiscoverDisplaysAsync();
                foreach (var display in displays)
                {
                    var item = new DisplayControlItem(display, _coordinator)
                    {
                        TargetBrightness = DefaultPowerSavingBrightness
                    };
                    var info = await _coordinator.GetBrightnessAsync(display);
                    if (info != null)
                    {
                        item.CurrentBrightness = info.Current;
                        item.Minimum = info.Minimum;
                        item.Maximum = info.Maximum;
                    }
                    Displays.Add(item);
                }

                StatusMessage = Displays.Count == 0 ? "未发现可用显示器" : $"已发现 {Displays.Count} 台显示器";
            }
            catch (Exception ex)
            {
                StatusMessage = $"扫描失败: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyPowerSavingAsync()
        {
            await RunBusy(async () =>
            {
                DNHper.NLogger.Info("[PowerSaving] 开始应用省电模式...");
                var successCount = 0;
                var totalCount = Displays.Count;

                foreach (var display in Displays)
                {
                    var targetBrightness = display.OverrideEnabled
                        ? display.TargetBrightness
                        : DefaultPowerSavingBrightness;

                    DNHper.NLogger.Info(
                        $"[PowerSaving] 设置显示器: {display.Identity.DisplayName} = {targetBrightness}%"
                    );
                    var success = await _coordinator.SetBrightnessAsync(
                        display.Identity,
                        targetBrightness
                    );

                    if (success)
                    {
                        successCount++;
                        DNHper.NLogger.Info($"[PowerSaving] 应用成功: {display.Identity.DisplayName}");
                    }
                    else
                    {
                        DNHper.NLogger.Error($"[PowerSaving] 应用失败: {display.Identity.DisplayName}");
                    }
                }

                IsPowerSavingMode = true;
                StatusMessage = $"已切换到省电模式，成功 {successCount}/{totalCount}";

                // 更新显示器亮度显示
                await SyncDisplayBrightnessAsync();
            });
        }

        private async Task RestoreNormalAsync()
        {
            await RunBusy(async () =>
            {
                DNHper.NLogger.Info("[PowerSaving] 开始恢复正常模式...");
                var successCount = 0;
                var totalCount = Displays.Count;

                foreach (var display in Displays)
                {
                    var targetBrightness = display.OverrideEnabled
                        ? display.TargetBrightness
                        : DefaultNormalBrightness;

                    DNHper.NLogger.Info(
                        $"[PowerSaving] 设置显示器: {display.Identity.DisplayName} = {targetBrightness}%"
                    );
                    var success = await _coordinator.SetBrightnessAsync(
                        display.Identity,
                        targetBrightness
                    );

                    if (success)
                    {
                        successCount++;
                        DNHper.NLogger.Info($"[PowerSaving] 恢复成功: {display.Identity.DisplayName}");
                    }
                    else
                    {
                        DNHper.NLogger.Error($"[PowerSaving] 恢复失败: {display.Identity.DisplayName}");
                    }
                }

                IsPowerSavingMode = false;
                StatusMessage = $"已恢复正常模式，成功 {successCount}/{totalCount}";

                // 更新显示器亮度显示
                await SyncDisplayBrightnessAsync();
            });
        }

        private async Task ApplyCustomBrightnessAsync()
        {
            await RunBusy(async () =>
            {
                foreach (var display in Displays)
                {
                    await _coordinator.SetBrightnessAsync(
                        display.Identity,
                        display.TargetBrightness
                    );
                }

                StatusMessage = "自定义亮度已应用";
            });
        }

        private async Task RunBusy(Func<Task> work)
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                await work();
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string DescribeResult(string prefix, PowerSavingResult result)
        {
            var total = result.Results.Count;
            var success = result.Results.Count(_ => _.IsSuccess);
            return $"{prefix}，成功 {success}/{total}";
        }

        /// <summary>
        /// 同步当前模式的亮度到显示器
        /// </summary>
        private async Task SyncCurrentModeAsync()
        {
            try
            {
                var targetBrightness = IsPowerSavingMode
                    ? DefaultPowerSavingBrightness
                    : DefaultNormalBrightness;
                DNHper.NLogger.Info($"[PowerSaving] 同步当前模式亮度: {targetBrightness}%");

                foreach (var display in Displays.Where(d => !d.OverrideEnabled))
                {
                    display.TargetBrightness = targetBrightness;
                    await _coordinator.SetBrightnessAsync(display.Identity, targetBrightness);
                }

                // 更新显示器当前亮度显示
                await SyncDisplayBrightnessAsync();
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[PowerSaving] 同步当前模式亮度失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换模式时，更新显示器的当前亮度显示
        /// </summary>
        private async Task SyncDisplayBrightnessAsync()
        {
            try
            {
                // 稍微延迟确保硬件已更新
                await Task.Delay(100);
                foreach (var display in Displays)
                {
                    var info = await _coordinator.GetBrightnessAsync(display.Identity);
                    if (info != null)
                    {
                        display.CurrentBrightness = info.Current;
                    }
                }
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[PowerSaving] 同步显示器亮度失败: {ex.Message}");
            }
        }
    }

    public sealed class DisplayControlItem : ReactiveObject
    {
        public DisplayIdentity Identity { get; }
        private BrightnessCoordinator _coordinator;
        private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

        public string DisplayLabel =>
            Identity.DisplayIndex >= 0
                ? $"显示器 {Identity.DisplayIndex + 1}: {Identity.FriendlyName}"
                : Identity.FriendlyName;

        public string DisplaySubLabel =>
            string.IsNullOrWhiteSpace(Identity.DeviceName)
                ? Identity.FriendlyName
                : Identity.DeviceName;

        private byte? _currentBrightness;
        public byte? CurrentBrightness
        {
            get => _currentBrightness;
            set => this.RaiseAndSetIfChanged(ref _currentBrightness, value);
        }

        private byte _targetBrightness;
        public byte TargetBrightness
        {
            get => _targetBrightness;
            set
            {
                if (_targetBrightness != value)
                {
                    _targetBrightness = value;
                    this.RaisePropertyChanged(nameof(TargetBrightness));
                }
            }
        }

        private byte _minimum = 0;
        public byte Minimum
        {
            get => _minimum;
            set => this.RaiseAndSetIfChanged(ref _minimum, value);
        }

        // 协议配置属性
        private ProtocolType _selectedProtocol;
        public ProtocolType SelectedProtocol
        {
            get => _selectedProtocol;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedProtocol, value);
                Identity.Protocol = value; // 同步到 Identity
                this.RaisePropertyChanged(nameof(IsSerialLed));
                this.RaisePropertyChanged(nameof(IsTcpLed));
                this.RaisePropertyChanged(nameof(IsDdcCi));
                this.RaisePropertyChanged(nameof(IsKsvDevice));
                MarkConnectionDirty();
            }
        }

        // 串口参数
        private string _serialPort;
        public string SerialPort
        {
            get => _serialPort;
            set
            {
                this.RaiseAndSetIfChanged(ref _serialPort, value);
                Identity.SerialPort = value;
                MarkConnectionDirty();
            }
        }

        /// <summary>
        /// 系统中所有可用的串口列表（已排序）
        /// 支持热插拔：定期检测串口列表变化
        /// </summary>
        public ObservableCollection<string> AvailableSerialPorts
        {
            get
            {
                // 首次访问时初始化，后续通过热插拔检测更新
                if (_availableSerialPorts == null)
                {
                    _availableSerialPorts = SerialPortHelper.GetSortedSerialPorts();

                    // 启动热插拔监听（每 1 秒检查一次串口列表变化）
                    InitializeHotPlugDetection();
                }
                return _availableSerialPorts;
            }
        }

        private bool _hotPlugDetectionStarted;
        private ObservableCollection<string>? _availableSerialPorts;

        /// <summary>
        /// 初始化热插拔检测机制
        /// </summary>
        private void InitializeHotPlugDetection()
        {
            if (_hotPlugDetectionStarted)
                return;

            _hotPlugDetectionStarted = true;

            // 使用 RxJS 创建周期性检测任务
            var hotPlugSubscription = Observable
                .Interval(TimeSpan.FromSeconds(1))
                .Subscribe(_ =>
                {
                    RefreshSerialPortsList();
                });

            _disposables.Add(hotPlugSubscription);
        }

        /// <summary>
        /// 刷新串口列表，检测热插拔变化
        /// </summary>
        private void RefreshSerialPortsList()
        {
            try
            {
                var currentPorts = SerialPortHelper.GetSortedSerialPorts();

                // 检查列表是否发生了变化
                if (HasSerialPortsChanged(_availableSerialPorts, currentPorts))
                {
                    // 更新列表
                    _availableSerialPorts.Clear();
                    foreach (var port in currentPorts)
                    {
                        _availableSerialPorts.Add(port);
                    }

                    DNHper.NLogger.Info(
                        $"[PowerSaving] 串口列表已更新: {string.Join(", ", currentPorts)}"
                    );
                }
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[PowerSaving] 检测串口列表变化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查串口列表是否发生了变化
        /// </summary>
        private bool HasSerialPortsChanged(
            ObservableCollection<string>? oldPorts,
            ObservableCollection<string>? newPorts
        )
        {
            if (oldPorts == null || newPorts == null)
                return true;

            if (oldPorts.Count != newPorts.Count)
                return true;

            return !oldPorts.SequenceEqual(newPorts);
        }

        /// <summary>
        /// 手动刷新串口列表（供外部调用）
        /// </summary>
        public void RefreshSerialPorts()
        {
            if (_availableSerialPorts == null)
            {
                _availableSerialPorts = SerialPortHelper.GetSortedSerialPorts();
                InitializeHotPlugDetection();
            }
            else
            {
                RefreshSerialPortsList();
            }
        }

        private int _serialBaudRate;
        public int SerialBaudRate
        {
            get => _serialBaudRate;
            set
            {
                this.RaiseAndSetIfChanged(ref _serialBaudRate, value);
                Identity.SerialBaudRate = value;
                MarkConnectionDirty();
            }
        }

        // 网口参数
        private string _tcpAddress;
        public string TcpAddress
        {
            get => _tcpAddress;
            set
            {
                this.RaiseAndSetIfChanged(ref _tcpAddress, value);
                Identity.TcpAddress = value;
                MarkConnectionDirty();
            }
        }

        private int _tcpPort;
        public int TcpPort
        {
            get => _tcpPort;
            set
            {
                this.RaiseAndSetIfChanged(ref _tcpPort, value);
                Identity.TcpPort = value;
                MarkConnectionDirty();
            }
        }

        // UI 可见性辅助属性
        public bool IsSerialLed => SelectedProtocol == ProtocolType.KSV_Serial;
        public bool IsTcpLed => SelectedProtocol == ProtocolType.KSV_Tcp;
        public bool IsDdcCi => SelectedProtocol == ProtocolType.DdcCi;
        public bool IsKsvDevice => IsSerialLed || IsTcpLed;

        private byte _maximum = 100;
        public byte Maximum
        {
            get => _maximum;
            set => this.RaiseAndSetIfChanged(ref _maximum, value);
        }

        private bool _overrideEnabled;
        public bool OverrideEnabled
        {
            get => _overrideEnabled;
            set => this.RaiseAndSetIfChanged(ref _overrideEnabled, value);
        }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set => this.RaiseAndSetIfChanged(ref _isConnected, value);
        }

        private string _connectionStatus = "未连接";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
        }

        public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }

        public DisplayControlItem(
            DisplayIdentity identity,
            BrightnessCoordinator coordinator = null
        )
        {
            Identity = identity;
            _coordinator = coordinator;

            // 初始化协议配置属性
            _selectedProtocol = identity.Protocol;
            _serialPort = identity.SerialPort;
            _serialBaudRate = identity.SerialBaudRate;
            _tcpAddress = identity.TcpAddress;
            _tcpPort = identity.TcpPort;

            // 节流控制：独立配置亮度滑块调整后 300ms 才应用
            var targetBrightnessSubscription = this.WhenAnyValue(x => x.TargetBrightness)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Where(_ => OverrideEnabled && _coordinator != null)
                .Subscribe(async brightness =>
                {
                    await _coordinator.SetBrightnessAsync(Identity, brightness);
                    // 更新当前亮度显示
                    var info = await _coordinator.GetBrightnessAsync(Identity);
                    if (info != null)
                    {
                        CurrentBrightness = info.Current;
                    }
                });
            _disposables.Add(targetBrightnessSubscription);

            TestConnectionCommand = ReactiveCommand.CreateFromTask(
                TestConnectionAsync,
                this.WhenAnyValue(x => x.IsConnecting, busy => !busy)
            );
            _disposables.Add(TestConnectionCommand);
        }

        public void SetCoordinator(BrightnessCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        private void MarkConnectionDirty()
        {
            IsConnected = false;
            ConnectionStatus = "未连接";
        }

        private async Task TestConnectionAsync()
        {
            if (_coordinator == null)
            {
                ConnectionStatus = "未找到协调器";
                IsConnected = false;
                return;
            }

            IsConnecting = true;
            ConnectionStatus = "连接中...";
            try
            {
                // 对于串口设备，先验证串口是否存在
                if (Identity.Protocol == ProtocolType.KSV_Serial)
                {
                    // 检查串口是否存在
                    var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                    if (
                        !availablePorts.Contains(
                            Identity.SerialPort,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    {
                        IsConnected = false;
                        ConnectionStatus = $"失败: 串口 {Identity.SerialPort} 不存在";
                        IsConnecting = false;
                        return;
                    }
                    DNHper.NLogger.Info($"[PowerSaving] 串口 {Identity.SerialPort} 验证成功");
                }
                else if (Identity.Protocol == ProtocolType.KSV_Tcp)
                {
                    // 对于网口设备，验证地址和端口格式
                    if (
                        string.IsNullOrWhiteSpace(Identity.TcpAddress)
                        || Identity.TcpPort <= 0
                        || Identity.TcpPort > 65535
                    )
                    {
                        IsConnected = false;
                        ConnectionStatus = $"失败: 无效的网络配置 {Identity.TcpAddress}:{Identity.TcpPort}";
                        IsConnecting = false;
                        return;
                    }
                    DNHper.NLogger.Info(
                        $"[PowerSaving] 网络地址 {Identity.TcpAddress}:{Identity.TcpPort} 验证成功"
                    );
                }

                // 尝试获取亮度信息来验证连接
                var info = await _coordinator.GetBrightnessAsync(Identity);
                if (info != null)
                {
                    IsConnected = true;
                    Minimum = info.Minimum;
                    Maximum = info.Maximum;
                    CurrentBrightness = info.Current;
                    ConnectionStatus = "连接成功";
                }
                else
                {
                    IsConnected = false;
                    ConnectionStatus = "连接失败：无法获取亮度信息";
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                ConnectionStatus = $"失败: {ex.Message}";
                DNHper.NLogger.Error($"[PowerSaving] 连接测试异常: {ex.Message}");
            }
            finally
            {
                IsConnecting = false;
            }
        }
    }

    /// <summary>
    /// 串口枚举和排序工具类
    /// </summary>
    internal static class SerialPortHelper
    {
        /// <summary>
        /// 获取排序后的串口列表（数字排序而非字母排序）
        /// 例如：COM1, COM2, COM3 而非 COM1, COM10, COM2
        /// </summary>
        public static ObservableCollection<string> GetSortedSerialPorts()
        {
            var ports = System.IO.Ports.SerialPort.GetPortNames();

            // 按数字排序串口列表
            var sortedPorts = ports.OrderBy(p => ExtractPortNumber(p)).ToList();

            if (sortedPorts.Count == 0)
            {
                return new ObservableCollection<string> { "未找到串口" };
            }

            return new ObservableCollection<string>(sortedPorts);
        }

        /// <summary>
        /// 从串口名称中提取数字用于排序
        /// 例如：COM10 → 10, COM1 → 1
        /// </summary>
        private static int ExtractPortNumber(string portName)
        {
            var match = Regex.Match(portName, @"(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var number)
                ? number
                : int.MaxValue; // 无法解析的端口排到最后
        }
    }
}
