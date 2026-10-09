using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AndX.Edge.Tests;

internal static class TestPorts
{
    /// <summary>获取一个空闲的环回 TCP 端口（存在极小竞态，测试可接受）。</summary>
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>测试用假上游：记录收到的边缘密钥、路径与请求体。</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = string.Empty;

    public string? LastEdgeKey { get; private set; }

    public string? LastPath { get; private set; }

    public string? LastExhibitId { get; private set; }

    public byte[] LastBody { get; private set; } = Array.Empty<byte>();

    public static async Task<FakeUpstream> StartAsync()
    {
        var self = new FakeUpstream();
        var port = TestPorts.FreeTcpPort();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.Run(async context =>
        {
            self.LastEdgeKey = context.Request.Headers["x-edge-key"];
            self.LastPath = context.Request.Path.ToString() + context.Request.QueryString.ToString();
            self.LastExhibitId = context.Request.Query["exhibitId"];

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            self.LastBody = buffer.ToArray();

            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("ok");
        });

        await app.StartAsync();
        self._app = app;
        self.BaseUrl = $"http://127.0.0.1:{port}";
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
