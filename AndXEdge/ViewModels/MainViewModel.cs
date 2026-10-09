using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AndX.Edge;

namespace AndX.Edge.ViewModels;

/// <summary>请求日志行的界面模型。</summary>
public sealed class EdgeLogRow
{
    public string Time { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Elapsed { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>AndXEdge 主界面视图模型：服务启停、配置编辑、自检与请求日志。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 500;

    private readonly EdgeConfig _config;
    private readonly EdgeProxyHost _host;
    private bool _initialized;

    private string _upstreamBaseUrl;
    private string _listenAddress;
    private string _listenPortText;
    private string _exhibitId;
    private string _edgeKeyInput = string.Empty;
    private string _selfTestText = string.Empty;
    private string _saveStatusText = string.Empty;
    private string _statusText = string.Empty;
    private Brush _statusBrush = Brushes.Gray;
    private bool _isRunning;

    public MainViewModel()
    {
        _config = EdgeConfig.Load();
        _host = new EdgeProxyHost();
        _host.Log.EntryAdded += OnLogEntry;
        _host.StateChanged += OnHostStateChanged;

        _upstreamBaseUrl = _config.UpstreamBaseUrl;
        _listenAddress = _config.ListenAddress;
        _listenPortText = _config.ListenPort.ToString(CultureInfo.InvariantCulture);
        _exhibitId = _config.ExhibitId ?? string.Empty;

        ToggleServerCommand = new AsyncRelayCommand(ToggleServerAsync);
        SelfTestCommand = new AsyncRelayCommand(SelfTestAsync);
        SaveConfigCommand = new RelayCommand(SaveConfig);
        ClearLogCommand = new RelayCommand(ClearLog);

        UpdateStatus();
    }

    public ObservableCollection<EdgeLogRow> Requests { get; } = new();

    public ICommand ToggleServerCommand { get; }

    public ICommand SelfTestCommand { get; }

    public ICommand SaveConfigCommand { get; }

    public ICommand ClearLogCommand { get; }

    public string UpstreamBaseUrl
    {
        get => _upstreamBaseUrl;
        set => SetProperty(ref _upstreamBaseUrl, value);
    }

    public string ListenAddress
    {
        get => _listenAddress;
        set => SetProperty(ref _listenAddress, value);
    }

    public string ListenPortText
    {
        get => _listenPortText;
        set => SetProperty(ref _listenPortText, value);
    }

    public string ExhibitId
    {
        get => _exhibitId;
        set => SetProperty(ref _exhibitId, value);
    }

    public string EdgeKeyInput
    {
        get => _edgeKeyInput;
        set => SetProperty(ref _edgeKeyInput, value);
    }

    public string SelfTestText
    {
        get => _selfTestText;
        private set => SetProperty(ref _selfTestText, value);
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set => SetProperty(ref _saveStatusText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public string ToggleButtonText => _isRunning ? "停止服务" : "启动服务";

    public string ListenUrl => _config.ListenUrl;

    public string ConfigPathText => _config.ConfigPath;

    public string EdgeKeyStatusText => $"Edge 密钥：{_config.MaskedEdgeKey}";

    /// <summary>窗口加载后调用：配置就绪则自动启动服务。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        AppLog.Info($"AndXEdge {EdgeConstants.Version} 启动，配置：{_config.ConfigPath}");

        if (_config.IsReady)
        {
            try
            {
                await StartHostAsync();
            }
            catch (Exception ex)
            {
                SelfTestText = $"自动启动失败：{ex.Message}";
                AppLog.Error($"自动启动失败: {ex}");
            }
        }
        else
        {
            SelfTestText = _config.ReadinessError ?? "配置不完整";
        }

        UpdateStatus();
    }

    /// <summary>关闭窗口时同步停止服务并释放资源（Core 内部全部 ConfigureAwait(false)，无 UI 死锁）。</summary>
    public void Dispose()
    {
        _host.Log.EntryAdded -= OnLogEntry;
        _host.StateChanged -= OnHostStateChanged;
        try
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止服务异常: {ex.Message}");
        }
    }

    private async Task StartHostAsync()
    {
        ApplyFormToConfig();
        if (!IsLoopbackAddress(_config.ListenAddress))
        {
            AppLog.Warn($"监听地址 {_config.ListenAddress} 非环回：局域网内主机亦可访问本机边缘网关，请确认网络可信");
        }
        await _host.StartAsync(_config);
        AppLog.Info($"服务已启动：{_host.ListenUrl} -> {_config.UpstreamBaseUrl}");
    }

    private static bool IsLoopbackAddress(string address) =>
        string.Equals(address, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(address, "::1", StringComparison.Ordinal);

    private async Task ToggleServerAsync()
    {
        if (_host.IsRunning)
        {
            try
            {
                await _host.StopAsync();
                AppLog.Info("服务已停止");
            }
            catch (Exception ex)
            {
                SelfTestText = $"停止失败：{ex.Message}";
                AppLog.Error($"停止失败: {ex}");
            }
            UpdateStatus();
            return;
        }

        try
        {
            await StartHostAsync();
            SelfTestText = string.Empty;
        }
        catch (Exception ex)
        {
            SelfTestText = $"启动失败：{ex.Message}";
            AppLog.Error($"启动失败: {ex}");
        }
        UpdateStatus();
    }

    private async Task SelfTestAsync()
    {
        if (!_host.IsRunning || _host.ListenUrl is null)
        {
            SelfTestText = "服务未运行，无法自检";
            return;
        }

        SelfTestText = "自检中…";
        var result = await EdgeSelfTest.RunAsync(_host.ListenUrl);
        SelfTestText = (result.Ok ? "✔ " : "✘ ") + result.Message;
        AppLog.Info($"自检：ok={result.Ok} status={result.Status} elapsed={result.ElapsedMs}ms {result.Message}");
    }

    private void SaveConfig()
    {
        ApplyFormToConfig();
        try
        {
            _config.Save();
            EdgeKeyInput = string.Empty;
            SaveStatusText = $"已保存：{_config.ConfigPath}（监听/上游变更需重启服务后生效）";
            AppLog.Info($"配置已保存：{_config.ListenUrl} -> {_config.UpstreamBaseUrl}");
        }
        catch (Exception ex)
        {
            SaveStatusText = $"保存失败：{ex.Message}";
            AppLog.Error($"保存配置失败: {ex}");
        }
        UpdateStatus();
    }

    private void ClearLog()
    {
        Requests.Clear();
        _host.Log.Clear();
    }

    private void ApplyFormToConfig()
    {
        _config.UpstreamBaseUrl = _upstreamBaseUrl?.Trim() ?? string.Empty;
        _config.ListenAddress = string.IsNullOrWhiteSpace(_listenAddress)
            ? EdgeConstants.DefaultListenAddress
            : _listenAddress.Trim();
        _config.ListenPort = int.TryParse(_listenPortText, out var port) && port is > 0 and <= 65535
            ? port
            : EdgeConstants.DefaultPort;
        _config.ExhibitId = string.IsNullOrWhiteSpace(_exhibitId) ? null : _exhibitId.Trim();
        if (!string.IsNullOrWhiteSpace(_edgeKeyInput))
        {
            _config.EdgeKey = _edgeKeyInput.Trim();
        }
    }

    private void UpdateStatus()
    {
        IsRunning = _host.IsRunning;

        if (_host.IsRunning)
        {
            StatusText = $"运行中 · {_host.ListenUrl}";
            StatusBrush = Brushes.SeaGreen;
        }
        else if (_config.IsReady)
        {
            StatusText = "已停止（配置就绪）";
            StatusBrush = Brushes.Goldenrod;
        }
        else
        {
            StatusText = $"未就绪 · {_config.ReadinessError}";
            StatusBrush = Brushes.IndianRed;
        }

        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(ListenUrl));
        OnPropertyChanged(nameof(ConfigPathText));
        OnPropertyChanged(nameof(EdgeKeyStatusText));
    }

    private void OnLogEntry(EdgeLogEntry entry)
    {
        void Add()
        {
            Requests.Insert(0, new EdgeLogRow
            {
                Time = entry.Timestamp.ToString("HH:mm:ss.fff"),
                Method = entry.Method,
                Path = entry.Path,
                Status = entry.Status.ToString(CultureInfo.InvariantCulture),
                Elapsed = $"{entry.ElapsedMs} ms",
                RequestId = entry.RequestId ?? string.Empty,
            });

            while (Requests.Count > MaxRows)
            {
                Requests.RemoveAt(Requests.Count - 1);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Add();
        }
        else
        {
            dispatcher.Invoke(Add);
        }

        AppLog.Info($"HTTP {entry.Method} {entry.Path} -> {entry.Status} ({entry.ElapsedMs}ms) rid={entry.RequestId}");
    }

    private void OnHostStateChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UpdateStatus();
        }
        else
        {
            dispatcher.Invoke(UpdateStatus);
        }
    }
}
