using System.Net.Http;
using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

public sealed class EdgeProxyHostTests
{
    [Fact]
    public async Task StartAsync_throws_when_not_ready()
    {
        await using var host = new EdgeProxyHost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(new EdgeConfig()));
    }

    [Fact]
    public async Task Injects_edge_key_and_preserves_path_body_and_exhibit()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenAddress = "127.0.0.1",
            ListenPort = TestPorts.FreeTcpPort(),
            EdgeKey = "server-side-key",
            ExhibitId = "1001",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            host.ListenUrl + "/api/edge/uploads?offset=0")
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 }),
        };
        // 客户端伪造的密钥必须被覆盖
        request.Headers.TryAddWithoutValidation("x-edge-key", "spoofed-by-client");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("server-side-key", upstream.LastEdgeKey);
        Assert.Equal("/api/edge/uploads?offset=0&exhibitId=1001", upstream.LastPath);
        Assert.Equal("1001", upstream.LastExhibitId);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, upstream.LastBody);

        // 请求日志应记录本次透传
        Assert.Contains(
            host.Log.Snapshot(),
            e => e.Method == "POST" && e.Path.StartsWith("/api/edge/uploads") && e.Status == 200);
    }

    [Fact]
    public async Task Does_not_inject_exhibit_when_already_provided()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            EdgeKey = "k",
            ExhibitId = "1001",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        var body = await client.GetStringAsync(host.ListenUrl + "/api/edge/uploads/x?exhibitId=2002&offset=8");
        Assert.Equal("ok", body);
        Assert.Equal("2002", upstream.LastExhibitId);
        Assert.Equal("/api/edge/uploads/x?exhibitId=2002&offset=8", upstream.LastPath);
    }

    [Fact]
    public async Task Healthz_reports_ok()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            EdgeKey = "k",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        var json = (await client.GetStringAsync(host.ListenUrl + "/healthz")).Replace(" ", string.Empty);

        Assert.Contains("\"status\":\"ok\"", json);
        Assert.Contains("\"service\":\"AndXEdge\"", json);
    }

    [Fact]
    public async Task Resources_path_is_proxied_without_edge_prefix()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            EdgeKey = "k",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        var body = await client.GetStringAsync(host.ListenUrl + "/api/resources/abc/qrcode.png");

        Assert.Equal("ok", body);
        Assert.Equal("/api/resources/abc/qrcode.png", upstream.LastPath);
        Assert.Equal("k", upstream.LastEdgeKey);
    }
}
