using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace AndX.Edge;

/// <summary>
/// AndXEdge 本机反向代理宿主（Kestrel + YARP），由 WPF 层控制生命周期。
///
/// 角色（solution §16.3 / implementation §13.7）决定"谁持凭据、以及是否改动转发内容"：
/// <list type="bullet">
/// <item><c>local</c>：环回监听；代持共享密钥 <c>x-edge-key</c>（并在调用方未带时补 <c>exhibitId</c>），插件零密钥。</item>
/// <item><c>relay</c>：局域网出口；**纯透传** —— 不改 query/body、不注入凭据、不碰签名头，插件签名原样到达后端。</item>
/// <item><c>hub</c>：多展项共用出口；剥离客户端伪造的 <c>x-device-*</c>，解析展项身份（IP 映射 → 默认）后
///       注入并**用 Hub 自己的节点凭据重新签名**（query 与 body 都在签名内，顺序不可颠倒）。</item>
/// </list>
/// 请求/响应体默认不缓冲（支撑大文件分片透传）；仅 Hub 角色在需要签名时缓冲单个请求体（上限
/// <see cref="MaxBufferedBodyBytes"/>），因为签名必须覆盖真实字节。
/// </summary>
public sealed class EdgeProxyHost : IAsyncDisposable
{
    private const string ClusterId = "andx-upstream";

    /// <summary>Hub 角色为签名而缓冲请求体的上限（一个分片量级；超过则拒绝，避免内存放大）。</summary>
    private const long MaxBufferedBodyBytes = 32L * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    public EdgeProxyHost(EdgeRequestLog? log = null)
    {
        Log = log ?? new EdgeRequestLog();
    }

    /// <summary>请求日志（诊断页展示）。</summary>
    public EdgeRequestLog Log { get; }

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>运行中的监听基址；未运行时为 null。</summary>
    public string? ListenUrl { get; private set; }

    /// <summary>启停状态变化回调。</summary>
    public event Action? StateChanged;

    /// <summary>按配置启动代理。配置不完整或已在运行时抛异常。</summary>
    public async Task StartAsync(EdgeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                throw new InvalidOperationException("AndXEdge 已在运行");
            }
            if (!config.IsReady)
            {
                throw new InvalidOperationException(config.ReadinessError ?? "配置不完整，无法启动");
            }

            var upstream = config.UpstreamBaseUrl.TrimEnd('/');
            var edgeKey = config.EdgeKey ?? string.Empty;

            var routes = new[]
            {
                new RouteConfig
                {
                    RouteId = "andx-edge",
                    ClusterId = ClusterId,
                    Match = new RouteMatch { Path = EdgeConstants.EdgePathPrefix + "/{**catch-all}" },
                },
                new RouteConfig
                {
                    RouteId = "andx-ai",
                    ClusterId = ClusterId,
                    Match = new RouteMatch { Path = "/api/ai/{**catch-all}" },
                },
                new RouteConfig
                {
                    RouteId = "andx-resources",
                    ClusterId = ClusterId,
                    Match = new RouteMatch { Path = EdgeConstants.ResourcesPathPrefix + "/{**catch-all}" },
                },
            };
            var clusters = new[]
            {
                new ClusterConfig
                {
                    ClusterId = ClusterId,
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["upstream"] = new DestinationConfig { Address = upstream },
                    },
                },
            };

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(config.ListenUrl);
            builder.WebHost.ConfigureKestrel(options =>
            {
                // 大文件一次性/分片透传：不限制请求体大小（本机 / 内网信任边界）
                options.Limits.MaxRequestBodySize = null;
            });
            builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var method = context.Request.Method;
                var path = context.Request.Path.ToString() + context.Request.QueryString.ToString();

                // 关联 ID：缺失则生成并转发，便于日志串联（不在签名覆盖范围内）
                if (!context.Request.Headers.ContainsKey(EdgeConstants.RequestIdHeader))
                {
                    context.Request.Headers[EdgeConstants.RequestIdHeader] = Guid.NewGuid().ToString("N");
                }

                try
                {
                    switch (config.RoleKind)
                    {
                        case EdgeRoleKind.Relay:
                            // 纯透传：不改 query/body、不注入凭据、不碰签名头
                            break;
                        case EdgeRoleKind.Hub:
                            await ApplyHubIdentityAsync(context, config).ConfigureAwait(false);
                            break;
                        default:
                            ApplyLocalKeyInjection(context, config, edgeKey);
                            break;
                    }

                    await next().ConfigureAwait(false);
                }
                finally
                {
                    stopwatch.Stop();
                    Log.Add(new EdgeLogEntry(
                        DateTimeOffset.Now,
                        method,
                        path,
                        context.Response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        ResolveRequestId(context)));
                }
            });

            app.MapGet("/healthz", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    status = "ok",
                    service = EdgeConstants.ServiceName,
                    version = EdgeConstants.Version,
                    role = config.RoleKind.ToString().ToLowerInvariant(),
                    upstream,
                    exhibitId = config.ExhibitId,
                    nodeId = config.NodeId,
                });
            });

            // 诊断自省：用本机节点身份回查后端（Edge 诊断页「我是谁 + 能代表哪些展项 + 最近调用」）
            app.MapGet("/diagnostics/self", async (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (string.IsNullOrWhiteSpace(config.NodeId) || string.IsNullOrWhiteSpace(config.NodeSecret))
                {
                    return Results.Json(new
                    {
                        service = EdgeConstants.ServiceName,
                        role = config.RoleKind.ToString().ToLowerInvariant(),
                        upstream,
                        note = "当前角色未配置节点身份（Relay 纯透传不持凭据；Hub 需配置 nodeId/nodeSecret）",
                    });
                }

                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var self = await CallSignedAsync(http, config, upstream, "/api/devices/self").ConfigureAwait(false);
                    var audit = await CallSignedAsync(http, config, upstream, "/api/devices/self/audit?limit=20").ConfigureAwait(false);
                    return Results.Json(new
                    {
                        service = EdgeConstants.ServiceName,
                        role = config.RoleKind.ToString().ToLowerInvariant(),
                        upstream,
                        self,
                        audit,
                    });
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new { service = EdgeConstants.ServiceName, upstream, error = ex.Message },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            });

            app.MapReverseProxy();

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _app = app;
            ListenUrl = config.ListenUrl;
            IsRunning = true;
        }
        finally
        {
            _gate.Release();
        }
        StateChanged?.Invoke();
    }

    /// <summary>停止代理（幂等）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is null)
            {
                IsRunning = false;
                return;
            }
            var app = _app;
            _app = null;
            IsRunning = false;
            ListenUrl = null;
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Local：代持共享密钥，并在调用方未带时补 <c>exhibitId</c>（S5 后由节点凭据取代）。</summary>
    private static void ApplyLocalKeyInjection(HttpContext context, EdgeConfig config, string edgeKey)
    {
        context.Request.Headers.Remove(EdgeConstants.KeyHeader);
        if (!string.IsNullOrEmpty(edgeKey))
        {
            context.Request.Headers[EdgeConstants.KeyHeader] = edgeKey;
        }

        if (!string.IsNullOrWhiteSpace(config.ExhibitId)
            && context.Request.Path.StartsWithSegments(EdgeConstants.EdgePathPrefix)
            && !context.Request.Query.ContainsKey("exhibitId"))
        {
            context.Request.QueryString = new QueryString(QueryHelpers.AddQueryString(
                context.Request.QueryString.Value ?? string.Empty,
                "exhibitId",
                config.ExhibitId!));
        }
    }

    /// <summary>
    /// Hub：剥离客户端伪造的签名头 → 解析展项身份（IP 映射 → 默认）→ 注入（JSON 进 body，其余进 query）
    /// → 用最终 query + body **重新签名** → 回填缓冲的 body。
    /// </summary>
    private static async Task ApplyHubIdentityAsync(HttpContext context, EdgeConfig config)
    {
        // LAN 客户端不可信：先剥掉它可能伪造的节点签名头（由 Edge 用自己身份重签）
        foreach (var header in EdgeConstants.DeviceHeaderNames)
        {
            context.Request.Headers.Remove(header);
        }

        if (!RequiresNodeSignature(context.Request.Path))
        {
            // 公开路径（/api/resources/* 出图等）无需签名，也不缓冲 body
            return;
        }

        var exhibitId = ResolveExhibitId(context, config);
        var isJson = context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;

        // 需要签名时「能带 body」的方法一律缓冲：ContentLength 可能缺失（chunked），
        // 只按 ContentLength 判断会漏读 → 摘要按空体算 → 服务端判「请求体与摘要不一致」（E2E 实测）
        byte[]? body = null;
        if (CanCarryBody(context.Request.Method))
        {
            body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(exhibitId))
        {
            if (isJson && body is { Length: > 0 })
            {
                body = WithExhibitId(body, exhibitId!);
            }
            else if (!context.Request.Query.ContainsKey("exhibitId"))
            {
                context.Request.QueryString = new QueryString(QueryHelpers.AddQueryString(
                    context.Request.QueryString.Value ?? string.Empty,
                    "exhibitId",
                    exhibitId!));
            }
        }

        // 顺序不可颠倒：签名必须覆盖"注入后的最终 query 与 body"
        var signedPath = context.Request.Path + context.Request.QueryString;
        var headers = EdgeNodeSigner.Sign(
            context.Request.Method,
            signedPath,
            body,
            config.NodeId!,
            config.NodeSecret!);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        if (body is not null)
        {
            context.Request.Body = new MemoryStream(body, writable: false);
            context.Request.ContentLength = body.Length;
        }
    }

    /// <summary>需要节点签名的控制面路径（Hub 据此决定是否注入展项并重签）。</summary>
    private static bool RequiresNodeSignature(PathString path)
    {
        // 用纯前缀比较（PathString.StartsWithSegments 对带尾斜杠的前缀语义较绕，这里显式且可读）
        var value = path.Value ?? string.Empty;
        foreach (var prefix in EdgeConstants.SignedPathPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }{
        }}
        return false;
    }

    /// <summary>展项身份：客户端 IP 命中本地映射优先，其次默认 <see cref="EdgeConfig.ExhibitId"/>。</summary>
    private static string? ResolveExhibitId(HttpContext context, EdgeConfig config)
    {
        var clientIp = NormalizeIp(context.Connection.RemoteIpAddress);
        if (!string.IsNullOrWhiteSpace(clientIp) &&
            config.HubExhibitMap.TryGetValue(clientIp!, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }
        return string.IsNullOrWhiteSpace(config.ExhibitId) ? null : config.ExhibitId;
    }

    private static string? NormalizeIp(IPAddress? address)
    {
        if (address is null) return null;
        // LAN 上常见 IPv4-mapped IPv6（::ffff:192.168.1.10），归一成 IPv4 便于配置映射
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        return address.ToString();
    }

    /// <summary>该方法是否可能携带请求体（用于决定是否缓冲以参与签名）。</summary>
    private static bool CanCarryBody(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBufferedBodyBytes)
        {
            throw new BadHttpRequestException($"请求体超过 Edge 签名缓冲上限（{MaxBufferedBodyBytes} 字节）", StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream(request.ContentLength is > 0 ? (int)request.ContentLength.Value : 0);
        await request.Body.CopyToAsync(buffer).ConfigureAwait(false);
        if (buffer.Length > MaxBufferedBodyBytes)
        {
            throw new BadHttpRequestException($"请求体超过 Edge 签名缓冲上限（{MaxBufferedBodyBytes} 字节）", StatusCodes.Status413PayloadTooLarge);
        }
        return buffer.ToArray();
    }

    /// <summary>JSON 体注入 <c>exhibitId</c>（Hub 对 /api/ai/* 等 body 承载展项的端点）。</summary>
    private static byte[] WithExhibitId(byte[] json, string exhibitId)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
            {
                return json;
            }
            node["exhibitId"] = exhibitId;
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>用节点身份签名调用后端（诊断自省用；GET，无 body）。</summary>
    private static async Task<JsonElement?> CallSignedAsync(HttpClient http, EdgeConfig config, string upstream, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, upstream + path);
        var headers = EdgeNodeSigner.Sign(HttpMethod.Get.Method, path, null, config.NodeId!, config.NodeSecret!);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"后端 {path} 返回 {(int)response.StatusCode}: {Truncate(payload, 200)}");
        }

        using var document = JsonDocument.Parse(payload);
        // 统一信封 {code,message,data} → 取 data
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("data", out var data))
        {
            return data.Clone();
        }
        return document.RootElement.Clone();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string? ResolveRequestId(HttpContext context)
    {
        foreach (var name in new[] { EdgeConstants.RequestIdHeader, "x-trace-id", "x-correlation-id" })
        {
            if (context.Response.Headers.TryGetValue(name, out var value) && value.Count > 0)
            {
                return value[0];
            }
        }
        if (context.Request.Headers.TryGetValue(EdgeConstants.RequestIdHeader, out var requestValue) && requestValue.Count > 0)
        {
            return requestValue[0];
        }
        return null;
    }
}