using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace AndX.Edge;

/// <summary>
/// AndXEdge 本机反向代理宿主：环回监听，将 <c>/api/edge/*</c>（及公开的 <c>/api/resources/*</c>）
/// 流式转发到上游后端，注入 <c>x-edge-key</c>，请求/响应体不缓冲（支撑大文件分片透传）。
/// 进程内托管 Kestrel + YARP，由 WPF 层控制生命周期。
/// </summary>
public sealed class EdgeProxyHost : IAsyncDisposable
{
    private const string ClusterId = "andx-upstream";

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
            var edgeKey = config.EdgeKey!;

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
                // 大文件一次性/分片透传：不限制请求体大小（本机信任边界）
                options.Limits.MaxRequestBodySize = null;
            });
            builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var method = context.Request.Method;
                var path = context.Request.Path.ToString() + context.Request.QueryString.ToString();

                // 关联 ID：缺失则生成并转发，便于日志串联
                if (!context.Request.Headers.ContainsKey(EdgeConstants.RequestIdHeader))
                {
                    context.Request.Headers[EdgeConstants.RequestIdHeader] = Guid.NewGuid().ToString("N");
                }

                // 鉴权注入：先移除客户端可能伪造的，再写入 Edge 密钥
                context.Request.Headers.Remove(EdgeConstants.KeyHeader);
                context.Request.Headers[EdgeConstants.KeyHeader] = edgeKey;

                // 可选展项补充（仅 edge 前缀，且调用方未带时）
                if (!string.IsNullOrWhiteSpace(config.ExhibitId)
                    && context.Request.Path.StartsWithSegments(EdgeConstants.EdgePathPrefix)
                    && !context.Request.Query.ContainsKey("exhibitId"))
                {
                    context.Request.QueryString = new QueryString(QueryHelpers.AddQueryString(
                        context.Request.QueryString.Value ?? string.Empty,
                        "exhibitId",
                        config.ExhibitId!));
                }

                try
                {
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
                    upstream,
                    exhibitId = config.ExhibitId,
                });
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
