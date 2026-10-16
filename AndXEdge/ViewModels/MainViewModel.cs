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

/// <summary>节点自省返回的最近调用行（后端 /api/devices/self/audit）。</summary>
public sealed class EdgeAuditRow
{
    public string Time { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string ExhibitId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public string Elapsed { get; init; } = string.Empty;
}

/// <summary>角色下拉项（值 + 中文说明）。</summary>
public sealed record EdgeRoleOption(string Value, string Label);

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
    private string _role = EdgeRoles.Local;
    private string _nodeId = string.Empty;
    private string _nodeSecretInput = string.Empty;
    private string _hubExhibitMapText = string.Empty;
    private string _enrollmentTokenInput = string.Empty;
    private string _selfSummaryText = string.Empty;
    private string _selfStatusText = string.Empty;
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
        _role = _config.Role;
        _nodeId = _config.NodeId ?? string.Empty;
        _hubExhibitMapText = EdgeConfigFormat.FormatExhibitMap(_config.HubExhibitMap);

        ToggleServerCommand = new AsyncRelayCommand(ToggleServerAsync);
        SelfTestCommand = new AsyncRelayCommand(SelfTestAsync);
        SaveConfigCommand = new RelayCommand(SaveConfig);
        ClearLogCommand = new RelayCommand(ClearLog);
        RefreshSelfCommand = new AsyncRelayCommand(RefreshSelfAsync);
        RegisterNodeCommand = new AsyncRelayCommand(RegisterNodeAsync);

        UpdateStatus();
    }

    public ObservableCollection<EdgeLogRow> Requests { get; } = new();

    public ICommand ToggleServerCommand { get; }

    public ICommand SelfTestCommand { get; }

    public ICommand SaveConfigCommand { get; }

    public ICommand ClearLogCommand { get; }

    /// <summary>刷新节点自省（诊断页：我是谁 / 能代表哪些展项 / 最近调用）。</summary>
    public ICommand RefreshSelfCommand { get; }

    /// <summary>用注册令牌换取节点身份（首启向导）。</summary>
    public ICommand RegisterNodeCommand { get; }

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

    /// <summary>角色：local / relay / hub。</summary>
    public string Role
    {
        get => _role;
        set
        {
            if (SetProperty(ref _role, value))
            {
                OnPropertyChanged(nameof(NodeStatusText));
                OnPropertyChanged(nameof(IsLocalRole));
            }
        }
    }

    /// <summary>角色下拉项。</summary>
    public IReadOnlyList<EdgeRoleOption> RoleOptions { get; } = new[]
    {
        new EdgeRoleOption(EdgeRoles.Local, "local — 本机零密钥（Edge 代持共享密钥）"),
        new EdgeRoleOption(EdgeRoles.Relay, "relay — 无外网中继（纯透传，不持凭据）"),
        new EdgeRoleOption(EdgeRoles.Hub, "hub — 多展项共用出口（Edge 持节点身份，注入展项并重签）"),
    };

    /// <summary>是否 Local 角色（设置页据此提示共享密钥字段）。</summary>
    public bool IsLocalRole => EdgeRoles.Parse(Role) == EdgeRoleKind.Local;

    public string NodeId
    {
        get => _nodeId;
        set
        {
            if (SetProperty(ref _nodeId, value)) OnPropertyChanged(nameof(NodeStatusText));
        }
    }

    /// <summary>节点密钥输入（留空表示保持原值；保存时 DPAPI 加密）。</summary>
    public string NodeSecretInput
    {
        get => _nodeSecretInput;
        set => SetProperty(ref _nodeSecretInput, value);
    }

    /// <summary>Hub 展项映射（每行 <c>IP=展项ID</c>；命中优先，未命中回退展项 ID）。</summary>
    public string HubExhibitMapText
    {
        get => _hubExhibitMapText;
        set => SetProperty(ref _hubExhibitMapText, value);
    }

    /// <summary>注册令牌输入（首启向导；一次性）。</summary>
    public string EnrollmentTokenInput
    {
        get => _enrollmentTokenInput;
        set => SetProperty(ref _enrollmentTokenInput, value);
    }

    /// <summary>节点身份摘要（角色/编号/密钥掩码）。</summary>
    public string NodeStatusText =>
        $"角色：{Role} · 节点：{(string.IsNullOrWhiteSpace(NodeId) ? "（未配置）" : NodeId)} · 密钥：{_config.MaskedNodeSecret}";

    /// <summary>自省摘要（节点身份 + 授权展项）。</summary>
    public string SelfSummaryText
    {
        get => _selfSummaryText;
        private set => SetProperty(ref _selfSummaryText, value);
    }

    /// <summary>自省状态/错误文本。</summary>
    public string SelfStatusText
    {
        get => _selfStatusText;
        private set => SetProperty(ref _selfStatusText, value);
    }

    /// <summary>本节点最近调用（自省返回）。</summary>
    public ObservableCollection<EdgeAuditRow> SelfAuditRows { get; } = new();

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
        await RefreshSelfAsync();
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

    /// <summary>读取本机 /diagnostics/self（Edge 用节点身份回查后端）：展示角色/节点/授权展项/最近调用。</summary>
    private async Task RefreshSelfAsync()
    {
        if (!_host.IsRunning || _host.ListenUrl is null)
        {
            SelfAuditRows.Clear();
            SelfSummaryText = string.Empty;
            SelfStatusText = "服务未运行，无法读取节点自省";
            return;
        }

        SelfStatusText = "读取中…";
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = await client.GetStringAsync(_host.ListenUrl + "/diagnostics/self").ConfigureAwait(true);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;

            var role = root.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : Role;
            var nodeId = root.TryGetProperty("self", out var self) && self.TryGetProperty("nodeId", out var nodeValue)
                ? nodeValue.GetString()
                : NodeId;
            var exhibits = new List<string>();
            if (root.TryGetProperty("self", out self) &&
                self.TryGetProperty("authorizedExhibitIds", out var authorized) &&
                authorized.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in authorized.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        exhibits.Add(value);
                    }
                }
            }

            SelfSummaryText =
                $"角色：{role} · 节点：{(string.IsNullOrWhiteSpace(nodeId) ? "（未配置）" : nodeId)} · 可代表展项：{(exhibits.Count == 0 ? "—" : string.Join(", ", exhibits))}";

            SelfAuditRows.Clear();
            if (root.TryGetProperty("audit", out var audit) &&
                audit.TryGetProperty("items", out var items) &&
                items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    SelfAuditRows.Add(new EdgeAuditRow
                    {
                        Time = ReadString(item, "createdAt"),
                        Endpoint = ReadString(item, "endpoint"),
                        Method = ReadString(item, "method"),
                        ExhibitId = ReadString(item, "exhibitId"),
                        Status = ReadString(item, "statusCode"),
                        ErrorCode = ReadString(item, "errorCode"),
                        Elapsed = ReadString(item, "durationMs") + " ms",
                    });
                }
            }

            SelfStatusText = SelfAuditRows.Count > 0
                ? $"最近调用 {SelfAuditRows.Count} 条"
                : (root.TryGetProperty("note", out var note) ? note.GetString() ?? string.Empty : "暂无调用记录");
        }
        catch (Exception ex)
        {
            SelfSummaryText = string.Empty;
            SelfStatusText = $"读取失败：{ex.Message}";
        }
    }

    private static string ReadString(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return string.Empty;
        }
        return value.ToString();
    }

    /// <summary>首启向导：用注册令牌换取节点身份（nodeId/secret），成功即保存（DPAPI 加密）并切到 Hub 角色。</summary>
    private async Task RegisterNodeAsync()
    {
        if (string.IsNullOrWhiteSpace(EnrollmentTokenInput))
        {
            SaveStatusText = "请先填写注册令牌（由后台「节点管理」签发）";
            return;
        }
        if (string.IsNullOrWhiteSpace(NodeId))
        {
            SaveStatusText = "请先填写节点编号（展馆内唯一）";
            return;
        }

        SaveStatusText = "注册中…";
        try
        {
            var result = await EdgeNodeEnrollment
                .EnrollAsync(UpstreamBaseUrl, EnrollmentTokenInput, NodeId)
                .ConfigureAwait(true);
            _config.NodeId = result.NodeId;
            _config.NodeSecret = result.Secret;
            NodeId = result.NodeId;
            Role = EdgeRoles.Hub;
            _config.Role = EdgeRoles.Hub;
            if (!string.IsNullOrWhiteSpace(result.ExhibitId) && string.IsNullOrWhiteSpace(ExhibitId))
            {
                ExhibitId = result.ExhibitId!;
            }
            _config.Save();
            EnrollmentTokenInput = string.Empty;
            OnPropertyChanged(nameof(EnrollmentTokenInput));
            SaveStatusText = $"注册成功：节点 {result.NodeId}（角色已切到 hub，密钥已 DPAPI 加密保存）";
            AppLog.Info($"节点注册成功：{result.NodeId} exhibit={result.ExhibitId}");
            UpdateStatus();
        }
        catch (Exception ex)
        {
            SaveStatusText = $"注册失败：{ex.Message}";
            AppLog.Error($"节点注册失败: {ex}");
        }
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
        _config.Role = string.IsNullOrWhiteSpace(_role) ? EdgeRoles.Local : _role.Trim();
        _config.NodeId = string.IsNullOrWhiteSpace(_nodeId) ? null : _nodeId.Trim();
        _config.HubExhibitMap = EdgeConfigFormat.ParseExhibitMap(_hubExhibitMapText);
        if (!string.IsNullOrWhiteSpace(_edgeKeyInput))
        {
            _config.EdgeKey = _edgeKeyInput.Trim();
        }
        if (!string.IsNullOrWhiteSpace(_nodeSecretInput))
        {
            _config.NodeSecret = _nodeSecretInput.Trim();
            _nodeSecretInput = string.Empty;
            OnPropertyChanged(nameof(NodeSecretInput));
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
