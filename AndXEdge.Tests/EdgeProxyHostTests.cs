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
    // ===== S7：角色化行为（Relay 纯透传 / Hub 注入并重签）=====

    [Fact]
    public async Task Relay_passes_through_without_key_or_query_rewrite()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Relay,
            ExhibitId = "1001", // 配置了也不得注入：Relay 纯透传
            EdgeKey = null,     // Relay 不持凭据
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.ListenUrl + "/api/edge/tickets?b=2&a=1")
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
        };
        request.Headers.TryAddWithoutValidation(EdgeConstants.DeviceIdHeader, "DEV-9");
        request.Headers.TryAddWithoutValidation(EdgeConstants.DeviceSignatureHeader, "sig-from-plugin");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("/api/edge/tickets?b=2&a=1", upstream.LastPath); // 未补 exhibitId、未重排
        Assert.Null(upstream.LastExhibitId);
        Assert.Null(upstream.LastEdgeKey);                            // 未注入共享密钥
        Assert.Equal("DEV-9", upstream.LastNodeId);                  // 插件签名头原样透传
        Assert.Equal("sig-from-plugin", upstream.LastSignature);
        Assert.Equal(new byte[] { 1, 2, 3 }, upstream.LastBody);
    }

    [Fact]
    public async Task Hub_injects_exhibit_into_json_body_and_re_signs_with_own_identity()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        const string nodeId = "HUB-1";
        const string nodeSecret = "hub-secret-value";
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Hub,
            NodeId = nodeId,
            NodeSecret = nodeSecret,
            ExhibitId = "1001",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.ListenUrl + "/api/ai/jobs")
        {
            Content = new StringContent(
                "{\"capability\":\"ai.image.generate\",\"prompt\":\"hi\"}",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(EdgeConstants.DeviceIdHeader, "SPOOFED");
        request.Headers.TryAddWithoutValidation(EdgeConstants.DeviceSignatureHeader, "spoofed-sig");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal(nodeId, upstream.LastNodeId);           // 伪造头被剥离并重签
        Assert.NotEqual("spoofed-sig", upstream.LastSignature);
        Assert.Null(upstream.LastEdgeKey);                   // Hub 不注入共享密钥
        Assert.Contains("\"exhibitId\":\"1001\"", System.Text.Encoding.UTF8.GetString(upstream.LastBody));

        // 签名必须覆盖「注入后的最终路径 + 真实 body」
        Assert.Equal(EdgeNodeSigner.Sha256Hex(upstream.LastBody), upstream.LastBodySha256);
        var expected = EdgeNodeSigner.Sign(
            nodeSecret,
            EdgeNodeSigner.BuildCanonical(
                "POST",
                upstream.LastPath!,
                upstream.LastTimestamp!,
                upstream.LastNonce!,
                upstream.LastBodySha256!));
        Assert.Equal(expected, upstream.LastSignature);
    }

    [Fact]
    public async Task Hub_injects_exhibit_into_query_for_binary_upload_and_re_signs()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        const string nodeSecret = "hub-secret-value";
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Hub,
            NodeId = "HUB-2",
            NodeSecret = nodeSecret,
            ExhibitId = "1001",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        var payload = new byte[] { 9, 8, 7, 6, 5, 4 };
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            host.ListenUrl + "/api/edge/resources?mediaType=image&fileName=a.png")
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("1001", upstream.LastExhibitId);        // 二进制端点：展项身份走 query
        Assert.Equal(payload, upstream.LastBody);            // body 字节不变（摘要必须对得上）
        Assert.Equal(EdgeNodeSigner.Sha256Hex(payload), upstream.LastBodySha256);
        var expected = EdgeNodeSigner.Sign(
            nodeSecret,
            EdgeNodeSigner.BuildCanonical(
                "POST",
                upstream.LastPath!,
                upstream.LastTimestamp!,
                upstream.LastNonce!,
                upstream.LastBodySha256!));
        Assert.Equal(expected, upstream.LastSignature);
    }

    [Fact]
    public async Task Hub_does_not_sign_public_paths_but_still_strips_client_device_headers()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var config = new EdgeConfig
        {
            UpstreamBaseUrl = upstream.BaseUrl,
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Hub,
            NodeId = "HUB-3",
            NodeSecret = "s3",
            ExhibitId = "1001",
        };

        await using var host = new EdgeProxyHost();
        await host.StartAsync(config);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, host.ListenUrl + "/api/resources/abc/qrcode.png");
        request.Headers.TryAddWithoutValidation(EdgeConstants.DeviceIdHeader, "SPOOFED");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        Assert.Equal("/api/resources/abc/qrcode.png", upstream.LastPath);
        Assert.Null(upstream.LastNodeId);      // 公开路径不签名
        Assert.Null(upstream.LastSignature);
    }
}