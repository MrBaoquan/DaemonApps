using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
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

        /// <summary>
        /// 用于取消正在进行的亮度设置任务
        /// </summary>
        private CancellationTokenSource? _brightnessTaskCts;

        /// <summary>
        /// 缓存已保存的显示器配置，用于在刷新时恢复
        /// </summary>
        private List<DisplayConfig> _savedDisplayConfigs = new();

        /// <summary>
        /// 配置改变时的保存回调
        /// </summary>
        public Action? OnConfigChanged { get; set; }

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
            set => this.RaiseAndSetIfChanged(ref _isPowerSavingMode, value);
        }

        private bool _enableIdleAutoPowerSaving;
        public bool EnableIdleAutoPowerSaving
        {
            get => _enableIdleAutoPowerSaving;
            set => this.RaiseAndSetIfChanged(ref _enableIdleAutoPowerSaving, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    this.RaiseAndSetIfChanged(ref _isBusy, value);
                }
            }
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

            // 刷新显示器列表允许随时执行，方便用户主动刷新
            RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);

            // 模式切换不需要阻塞，立即响应UI
            ApplyPowerSavingCommand = ReactiveCommand.CreateFromTask(ApplyPowerSavingAsync);
            RestoreNormalCommand = ReactiveCommand.CreateFromTask(RestoreNormalAsync);

            // 自定义亮度应用需要等待扫描完成
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

            // 不在构造函数中自动扫描显示器
            // 等待 LoadSettings 完成后再扫描，避免重复扫描和配置丢失
        }

        /// <summary>
        /// 从 AppSettings 加载设置
        /// </summary>
        public void LoadSettings(AppSettings settings)
        {
            if (settings == null)
                return;

            // 缓存保存的配置供 RefreshAsync 使用
            _savedDisplayConfigs = settings.PowerSavingDisplayConfigs ?? new();

            DefaultNormalBrightness = settings.PowerSavingNormalBrightness;
            DefaultPowerSavingBrightness = settings.PowerSavingLowBrightness;

            // 先扫描显示器，然后立即初始化亮度
            _ = Task.Run(async () =>
            {
                // 执行唯一的一次显示器扫描（RefreshAsync 内部会自动恢复配置）
                await RefreshAsync();

                // 同步 EnableIdleAutoPowerSaving 状态到 ViewModel
                EnableIdleAutoPowerSaving = settings.EnableIdleAutoPowerSaving;

                // 根据EnableIdleAutoPowerSaving设置启动模式
                if (settings.EnableIdleAutoPowerSaving)
                {
                    // 设置模式并通知UI更新
                    RxApp.MainThreadScheduler.Schedule(
                        Unit.Default,
                        (scheduler, _) =>
                        {
                            _isPowerSavingMode = false;
                            this.RaisePropertyChanged(nameof(IsPowerSavingMode));
                            return System.Reactive.Disposables.Disposable.Empty;
                        }
                    );

                    // 同步正常模式的亮度设置（并行设置，不阻塞）
                    var totalCount = Displays.Count;

                    var setBrightnessTasks = Displays
                        .Select(async display =>
                        {
                            var targetBrightness = display.OverrideEnabled
                                ? display.TargetBrightness
                                : DefaultNormalBrightness;

                            try
                            {
                                var success = await _coordinator.SetBrightnessAsync(
                                    display.Identity,
                                    targetBrightness
                                );

                                if (success)
                                {
                                    return true;
                                }
                                else
                                {
                                    DNHper.NLogger.Warn(
                                        $"[PowerSaving] 启动时设置失败: {display.Identity.DisplayName}"
                                    );
                                    return false;
                                }
                            }
                            catch (Exception ex)
                            {
                                DNHper.NLogger.Error(
                                    $"[PowerSaving] 启动时设置异常: {display.Identity.DisplayName}, {ex.Message}"
                                );
                                return false;
                            }
                        })
                        .ToList();

                    // 等待所有任务完成
                    var results = await Task.WhenAll(setBrightnessTasks);
                    var successCount = results.Count(r => r);

                    // 更新显示器亮度显示
                    await SyncDisplayBrightnessAsync();

                    DNHper.NLogger.Info($"[PowerSaving] 启动时亮度设置完成: 成功 {successCount}/{totalCount}");
                }
                else
                {
                    RxApp.MainThreadScheduler.Schedule(
                        Unit.Default,
                        (scheduler, _) =>
                        {
                            _isPowerSavingMode = settings.PowerSavingModeEnabled;
                            this.RaisePropertyChanged(nameof(IsPowerSavingMode));
                            return System.Reactive.Disposables.Disposable.Empty;
                        }
                    );

                    // 同步对应模式的亮度
                    await SyncDisplayBrightnessAsync();
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

            // 保存所有显示器的配置（包括协议、IP、端口等），不仅仅是启用覆盖的配置
            // 这样即使未启用覆盖，协议配置也不会丢失
            _savedDisplayConfigs = Displays
                .Select(
                    d =>
                        new DisplayConfig
                        {
                            DeviceName = d.Identity.DeviceName,
                            DisplayIndex = d.Identity.DisplayIndex,
                            OverrideEnabled = d.OverrideEnabled,
                            TargetBrightness = d.TargetBrightness,
                            Protocol = d.Identity.Protocol.ToString(),
                            SerialPort = d.Identity.SerialPort,
                            SerialBaudRate = d.Identity.SerialBaudRate,
                            TcpAddress = d.Identity.TcpAddress,
                            TcpPort = d.Identity.TcpPort
                        }
                )
                .ToList();

            // 同时更新 AppSettings
            settings.PowerSavingDisplayConfigs = _savedDisplayConfigs;
        }

        private async Task RefreshAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                StatusMessage = "正在扫描显示器...";

                // 在 UI 线程上清空集合
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Displays.Clear();
                });

                var displays = await _coordinator.DiscoverDisplaysAsync();
                foreach (var display in displays)
                {
                    var item = new DisplayControlItem(display, _coordinator, OnConfigChanged)
                    {
                        TargetBrightness = DefaultPowerSavingBrightness
                    };

                    // 立即恢复保存的配置，在创建 DisplayControlItem 后立刻应用
                    // 使用 DeviceName + DisplayIndex 的组合来唯一标识显示器
                    var savedConfig = _savedDisplayConfigs.FirstOrDefault(
                        c =>
                            c.DeviceName == display.DeviceName
                            && c.DisplayIndex == display.DisplayIndex
                    );
                    if (savedConfig != null)
                    {
                        if (Enum.TryParse<ProtocolType>(savedConfig.Protocol, out var protocol))
                        {
                            item.Identity.Protocol = protocol;
                            item.Identity.SerialPort = savedConfig.SerialPort;
                            item.Identity.SerialBaudRate = savedConfig.SerialBaudRate;
                            item.Identity.TcpAddress = savedConfig.TcpAddress;
                            item.Identity.TcpPort = savedConfig.TcpPort;

                            // 同时更新 ViewModel 属性
                            item.SelectedProtocol = protocol;
                            item.SerialPort = savedConfig.SerialPort;
                            item.SerialBaudRate = savedConfig.SerialBaudRate;
                            item.TcpAddress = savedConfig.TcpAddress;
                            item.TcpPort = savedConfig.TcpPort;
                        }
                        item.OverrideEnabled = savedConfig.OverrideEnabled;
                        item.TargetBrightness = savedConfig.TargetBrightness;
                    }

                    // 获取亮度信息，使用 3 秒超时防止网络设备超时导致扫描卡顿
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        {
                            var info = await _coordinator.GetBrightnessAsync(display, cts.Token);
                            if (info != null)
                            {
                                item.CurrentBrightness = info.Current;
                                item.Minimum = info.Minimum;
                                item.Maximum = info.Maximum;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 超时：设置默认亮度范围，不中断扫描流程
                        DNHper.NLogger.Warn($"[PowerSaving] 获取显示器 {display.FriendlyName} 亮度超时（3秒）");
                        item.CurrentBrightness = null;
                        item.Minimum = 0;
                        item.Maximum = 100;
                    }
                    catch (Exception ex)
                    {
                        // 其他错误：设置默认值
                        DNHper.NLogger.Warn(
                            $"[PowerSaving] 获取显示器 {display.FriendlyName} 亮度失败: {ex.Message}"
                        );
                        item.CurrentBrightness = null;
                        item.Minimum = 0;
                        item.Maximum = 100;
                    }

                    // 在 UI 线程上添加到集合
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Displays.Add(item);
                    });
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
            // 立即更新UI状态，不等待亮度设置完成
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsPowerSavingMode = true;
                StatusMessage = "正在切换到省电模式...";
            });

            // 后台异步设置亮度，不阻塞UI
            _ = SetBrightnessInBackgroundAsync(DefaultPowerSavingBrightness, "省电模式");
        }

        private async Task RestoreNormalAsync()
        {
            // 立即更新UI状态，不等待亮度设置完成
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsPowerSavingMode = false;
                StatusMessage = "正在恢复正常模式...";
            });

            // 后台异步设置亮度，不阻塞UI
            _ = SetBrightnessInBackgroundAsync(DefaultNormalBrightness, "正常模式");
        }

        /// <summary>
        /// 后台设置所有显示器亮度，支持取消旧任务
        /// </summary>
        private async Task SetBrightnessInBackgroundAsync(byte defaultBrightness, string modeName)
        {
            // 取消之前的亮度设置任务
            _brightnessTaskCts?.Cancel();
            _brightnessTaskCts?.Dispose();
            _brightnessTaskCts = new CancellationTokenSource();

            var cts = _brightnessTaskCts;
            var token = cts.Token;

            try
            {
                var totalCount = Displays.Count;

                // 并行设置所有显示器亮度
                var setBrightnessTasks = Displays
                    .Select(async display =>
                    {
                        // 检查是否被取消
                        if (token.IsCancellationRequested)
                        {
                            return false;
                        }

                        var targetBrightness = display.OverrideEnabled
                            ? display.TargetBrightness
                            : defaultBrightness;

                        try
                        {
                            var success = await _coordinator.SetBrightnessAsync(
                                display.Identity,
                                targetBrightness,
                                token
                            );

                            if (success)
                            {
                                // 直接更新显示的当前亮度
                                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    display.CurrentBrightness = targetBrightness;
                                });
                                return true;
                            }
                            else
                            {
                                DNHper.NLogger.Warn(
                                    $"[PowerSaving] 设置失败: {display.Identity.DisplayName}"
                                );
                                return false;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return false;
                        }
                        catch (Exception ex)
                        {
                            DNHper.NLogger.Error(
                                $"[PowerSaving] 设置异常: {display.Identity.DisplayName}, {ex.Message}"
                            );
                            return false;
                        }
                    })
                    .ToList();

                // 等待所有任务完成
                var results = await Task.WhenAll(setBrightnessTasks);
                var successCount = results.Count(r => r);

                // 检查是否被取消
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // 更新最终状态消息
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"已切换到{modeName}，成功 {successCount}/{totalCount}";
                });

                DNHper.NLogger.Info(
                    $"[PowerSaving] 后台亮度设置完成: {modeName}, 成功 {successCount}/{totalCount}"
                );
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[PowerSaving] 后台亮度设置异常: {ex.Message}");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"设置{modeName}时出错: {ex.Message}";
                });
            }
        }

        private async Task ApplyCustomBrightnessAsync()
        {
            await RunBusy(async () =>
            {
                // 并行设置所有显示器的自定义亮度
                var setBrightnessTasks = Displays
                    .Select(async display =>
                    {
                        try
                        {
                            return await _coordinator.SetBrightnessAsync(
                                display.Identity,
                                display.TargetBrightness
                            );
                        }
                        catch (Exception ex)
                        {
                            DNHper.NLogger.Error(
                                $"[PowerSaving] 应用自定义亮度异常: {display.Identity.DisplayName}, {ex.Message}"
                            );
                            return false;
                        }
                    })
                    .ToList();

                var results = await Task.WhenAll(setBrightnessTasks);
                var successCount = results.Count(r => r);

                StatusMessage = $"自定义亮度已应用，成功 {successCount}/{Displays.Count}";
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
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                DNHper.NLogger.Error($"[PowerSaving] 执行失败: {ex.Message}");
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

                // 并行设置所有未启用覆盖的显示器亮度
                var setBrightnessTasks = Displays
                    .Where(d => !d.OverrideEnabled)
                    .Select(async display =>
                    {
                        try
                        {
                            display.TargetBrightness = targetBrightness;
                            return await _coordinator.SetBrightnessAsync(
                                display.Identity,
                                targetBrightness
                            );
                        }
                        catch (Exception ex)
                        {
                            DNHper.NLogger.Error(
                                $"[PowerSaving] 同步模式亮度异常: {display.Identity.DisplayName}, {ex.Message}"
                            );
                            return false;
                        }
                    })
                    .ToList();

                if (setBrightnessTasks.Count > 0)
                {
                    await Task.WhenAll(setBrightnessTasks);
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
        /// 注意：不需要重新读取硬件亮度，直接使用设置的目标值即可
        /// </summary>
        private async Task SyncDisplayBrightnessAsync()
        {
            try
            {
                // 直接更新当前亮度显示为目标值，避免重复枚举硬件
                var targetBrightness = IsPowerSavingMode
                    ? DefaultPowerSavingBrightness
                    : DefaultNormalBrightness;

                foreach (var display in Displays)
                {
                    var brightness = display.OverrideEnabled
                        ? display.TargetBrightness
                        : targetBrightness;
                    display.CurrentBrightness = brightness;
                }

                await Task.CompletedTask;
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
        private BrightnessCoordinator? _coordinator;
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
                    if (_availableSerialPorts != null)
                    {
                        _availableSerialPorts.Clear();
                        foreach (var port in currentPorts)
                        {
                            _availableSerialPorts.Add(port);
                        }
                    }

                    DNHper.NLogger.Info(
                        $"[PowerSaving] 串口列表已更新: {string.Join(", ", currentPorts)}"
                    );
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
            BrightnessCoordinator? coordinator = null,
            Action? onConfigChanged = null
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
                    if (_coordinator != null)
                    {
                        var success = await _coordinator.SetBrightnessAsync(Identity, brightness);
                        // 直接更新当前亮度显示，避免重复查询硬件
                        if (success)
                        {
                            CurrentBrightness = brightness;
                        }
                    }
                    onConfigChanged?.Invoke();
                });
            _disposables.Add(targetBrightnessSubscription);

            // 监听协议和连接参数变化
            if (onConfigChanged != null)
            {
                var configChangedSubscription = this.WhenAnyValue(
                        x => x.SelectedProtocol,
                        x => x.SerialPort,
                        x => x.SerialBaudRate,
                        x => x.TcpAddress,
                        x => x.TcpPort,
                        x => x.OverrideEnabled
                    )
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .Subscribe(_ => onConfigChanged());
                _disposables.Add(configChangedSubscription);
            }

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
