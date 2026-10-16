using System.Text.Json;
using System.Text.Json.Serialization;

namespace AndX.Edge;

/// <summary>
/// AndXEdge 运行配置。持久化到 <c>~/.andx-edge.json</c>（可用 <c>ANDX_EDGE_CONFIG</c> 覆盖路径）。
/// 密钥经 Windows DPAPI 加密保存（<see cref="EdgeKeyEncrypted"/>）；环境变量优先级最高。
/// </summary>
public sealed class EdgeConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>上游后端 API 基址，例如 <c>http://10.0.0.8:3000</c>（本机 Edge 需要能访问它）。</summary>
    public string UpstreamBaseUrl { get; set; } = string.Empty;

    /// <summary>监听地址，默认 <c>127.0.0.1</c>（仅本机）。</summary>
    public string ListenAddress { get; set; } = EdgeConstants.DefaultListenAddress;

    /// <summary>监听端口，默认 6699。</summary>
    public int ListenPort { get; set; } = EdgeConstants.DefaultPort;

    /// <summary>可选展项 ID：配置后由 Edge 在 <c>/api/edge/*</c> 查询串上补充（服务端亦可经 ANDX_EDGE_EXHIBIT_ID 默认）。</summary>
    public string? ExhibitId { get; set; }

    /// <summary>
    /// 角色：<c>local</c>（默认，本机零密钥，代持共享密钥）/ <c>relay</c>（无外网中继，**纯透传**）/
    /// <c>hub</c>（多展项共用出口，Edge 持**自己的节点身份**，注入展项后重新签名）。见 implementation §13.7。
    /// </summary>
    public string Role { get; set; } = EdgeRoles.Local;

    /// <summary>节点编号（Hub 角色必填；与 NodeSecret 配对，来自 <c>POST /api/edge/enroll</c>）。</summary>
    public string? NodeId { get; set; }

    /// <summary>DPAPI 加密后的节点密钥（持久化字段）。</summary>
    public string? NodeSecretEncrypted { get; set; }

    /// <summary>明文节点密钥（仅内存，不序列化）。</summary>
    [JsonIgnore]
    public string? NodeSecret { get; set; }

    /// <summary>
    /// Hub 角色的「展项机 IP → 展项 ID」映射（本地表；命中即用，未命中回退 <see cref="ExhibitId"/>）。
    /// 一机一展项的现场用它把 LAN 内多台展项机区分开。
    /// </summary>
    public Dictionary<string, string> HubExhibitMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DPAPI 加密后的 Edge 密钥（持久化字段）。</summary>
    public string? EdgeKeyEncrypted { get; set; }

    /// <summary>明文 Edge 密钥（仅内存，不序列化）。</summary>
    [JsonIgnore]
    public string? EdgeKey { get; set; }

    /// <summary>解析后的角色枚举。</summary>
    [JsonIgnore]
    public EdgeRoleKind RoleKind => EdgeRoles.Parse(Role);

    /// <summary>本次加载实际使用的配置文件路径。</summary>
    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>监听基址，例如 <c>http://127.0.0.1:6699</c>。</summary>
    [JsonIgnore]
    public string ListenUrl => $"http://{ListenAddress}:{ListenPort}";

    /// <summary>是否具备启动条件（上游地址 + Edge 密钥）。</summary>
    [JsonIgnore]
    public bool IsReady => string.IsNullOrWhiteSpace(ReadinessError);

    /// <summary>不满足启动条件时的原因；就绪时为 null。</summary>
    [JsonIgnore]
    public string? ReadinessError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UpstreamBaseUrl))
            {
                return "未配置上游后端地址（upstreamBaseUrl）";
            }
            if (!Uri.TryCreate(UpstreamBaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "上游后端地址无效（需为 http/https 绝对地址）";
            }
            // 共享密钥只在 Local 角色需要；Relay 纯透传（不持凭据）、Hub 用节点凭据
            if (RoleKind == EdgeRoleKind.Local && string.IsNullOrWhiteSpace(EdgeKey))
            {
                return "未配置 Edge 密钥（x-edge-key）";
            }
            if (EdgeRoles.RequiresNodeCredential(RoleKind))
            {
                if (string.IsNullOrWhiteSpace(NodeId))
                {
                    return "Hub 角色未配置节点编号（nodeId，来自注册令牌换取的节点身份）";
                }
                if (string.IsNullOrWhiteSpace(NodeSecret))
                {
                    return "Hub 角色未配置节点密钥（nodeSecret）";
                }
            }
            if (ListenPort is <= 0 or > 65535)
            {
                return "监听端口无效（1-65535）";
            }
            if (string.IsNullOrWhiteSpace(ListenAddress))
            {
                return "监听地址为空";
            }
            return null;
        }
    }

    /// <summary>用于界面展示的脱敏节点密钥。</summary>
    [JsonIgnore]
    public string MaskedNodeSecret
    {
        get
        {
            if (string.IsNullOrEmpty(NodeSecret)) return "（未配置）";
            if (NodeSecret.Length <= 4) return "****";
            return "****" + NodeSecret[^4..];
        }
    }

    /// <summary>用于界面展示的脱敏密钥。</summary>
    [JsonIgnore]
    public string MaskedEdgeKey
    {
        get
        {
            if (string.IsNullOrEmpty(EdgeKey)) return "（未配置）";
            if (EdgeKey.Length <= 4) return "****";
            return "****" + EdgeKey[^4..];
        }
    }

    /// <summary>解析配置文件路径：环境变量 <c>ANDX_EDGE_CONFIG</c> 优先，其次用户目录。</summary>
    public static string ResolveConfigPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ANDX_EDGE_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andx-edge.json");
    }

    /// <summary>加载配置：读取文件 → 应用环境变量 → 解密密钥。</summary>
    public static EdgeConfig Load(string? path = null)
    {
        path ??= ResolveConfigPath();
        EdgeConfig config = new();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<EdgeConfig>(json, JsonOptions) ?? new EdgeConfig();
            }
            catch
            {
                // 配置损坏时回退默认，避免阻断启动
                config = new EdgeConfig();
            }
        }

        config.ConfigPath = path;
        config.ApplyEnvironmentOverrides();

        // 密钥优先级：环境变量 > 加密配置
        if (string.IsNullOrEmpty(config.EdgeKey) && !string.IsNullOrEmpty(config.EdgeKeyEncrypted))
        {
            config.EdgeKey = SecretProtector.TryUnprotect(config.EdgeKeyEncrypted);
        }
        if (string.IsNullOrEmpty(config.NodeSecret) && !string.IsNullOrEmpty(config.NodeSecretEncrypted))
        {
            config.NodeSecret = SecretProtector.TryUnprotect(config.NodeSecretEncrypted);
        }
        return config;
    }

    /// <summary>保存配置：加密密钥后写 JSON。</summary>
    public void Save(string? path = null)
    {
        path ??= string.IsNullOrWhiteSpace(ConfigPath) ? ResolveConfigPath() : ConfigPath;
        ConfigPath = path;

        EdgeKeyEncrypted = string.IsNullOrEmpty(EdgeKey)
            ? null
            : SecretProtector.Protect(EdgeKey);
        NodeSecretEncrypted = string.IsNullOrEmpty(NodeSecret)
            ? null
            : SecretProtector.Protect(NodeSecret);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>应用环境变量覆盖（<c>ANDX_EDGE_*</c>）。</summary>
    public void ApplyEnvironmentOverrides()
    {
        var upstream = Environment.GetEnvironmentVariable("ANDX_EDGE_UPSTREAM");
        if (!string.IsNullOrWhiteSpace(upstream)) UpstreamBaseUrl = upstream!;

        var key = Environment.GetEnvironmentVariable("ANDX_EDGE_KEY");
        if (!string.IsNullOrWhiteSpace(key)) EdgeKey = key!;

        var listenAddress = Environment.GetEnvironmentVariable("ANDX_EDGE_LISTEN_ADDRESS");
        if (!string.IsNullOrWhiteSpace(listenAddress)) ListenAddress = listenAddress!;

        var listenPort = Environment.GetEnvironmentVariable("ANDX_EDGE_LISTEN_PORT");
        if (!string.IsNullOrWhiteSpace(listenPort) && int.TryParse(listenPort, out var port)) ListenPort = port;

        var exhibitId = Environment.GetEnvironmentVariable("ANDX_EDGE_EXHIBIT_ID");
        if (!string.IsNullOrWhiteSpace(exhibitId)) ExhibitId = exhibitId!;

        var role = Environment.GetEnvironmentVariable("ANDX_EDGE_ROLE");
        if (!string.IsNullOrWhiteSpace(role)) Role = role!;

        var nodeId = Environment.GetEnvironmentVariable("ANDX_EDGE_NODE_ID");
        if (!string.IsNullOrWhiteSpace(nodeId)) NodeId = nodeId!;

        var nodeSecret = Environment.GetEnvironmentVariable("ANDX_EDGE_NODE_SECRET");
        if (!string.IsNullOrWhiteSpace(nodeSecret)) NodeSecret = nodeSecret!;
    }
}
