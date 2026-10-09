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

    /// <summary>DPAPI 加密后的 Edge 密钥（持久化字段）。</summary>
    public string? EdgeKeyEncrypted { get; set; }

    /// <summary>明文 Edge 密钥（仅内存，不序列化）。</summary>
    [JsonIgnore]
    public string? EdgeKey { get; set; }

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
            if (string.IsNullOrWhiteSpace(EdgeKey))
            {
                return "未配置 Edge 密钥（x-edge-key）";
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
    }
}
