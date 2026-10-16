using System.Net.Http;
using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

/// <summary>首启向导相关的纯逻辑：注册换取节点身份 + Hub 展项映射文本解析。</summary>
public sealed class EdgeNodeEnrollmentTests
{
    [Fact]
    public void ParseExhibitMap_reads_ip_pairs_and_skips_comments()
    {
        var map = EdgeConfigFormat.ParseExhibitMap(
            "192.168.1.10=1001\n# 注释行\n\n 192.168.1.11 = 1002 \n");

        Assert.Equal(2, map.Count);
        Assert.Equal("1001", map["192.168.1.10"]);
        Assert.Equal("1002", map["192.168.1.11"]); // 去空白
    }

    [Fact]
    public void ParseExhibitMap_rejects_malformed_line()
    {
        Assert.Throws<FormatException>(() => EdgeConfigFormat.ParseExhibitMap("192.168.1.10"));
        Assert.Throws<FormatException>(() => EdgeConfigFormat.ParseExhibitMap("=1001"));
    }

    [Fact]
    public void FormatExhibitMap_sorts_and_round_trips()
    {
        var text = EdgeConfigFormat.FormatExhibitMap(new Dictionary<string, string>
        {
            ["192.168.1.20"] = "1002",
            ["192.168.1.10"] = "1001",
        });

        Assert.StartsWith("192.168.1.10=1001", text);
        Assert.Equal(2, EdgeConfigFormat.ParseExhibitMap(text).Count);
        Assert.Equal(string.Empty, EdgeConfigFormat.FormatExhibitMap(null));
    }

    [Fact]
    public async Task EnrollAsync_returns_node_identity_and_rejects_bad_input()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.EnrollResponse =
            "{\"code\":0,\"message\":\"ok\",\"data\":{\"nodeId\":\"HUB-9\",\"role\":\"hub\",\"exhibitId\":null,\"secret\":\"s3cret\"}}";

        var result = await EdgeNodeEnrollment.EnrollAsync(upstream.BaseUrl, "token-1", "HUB-9", "中控");

        Assert.Equal("HUB-9", result.NodeId);
        Assert.Equal("hub", result.Role);
        Assert.Equal("s3cret", result.Secret);
        Assert.Equal("/api/edge/enroll", upstream.LastPath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            EdgeNodeEnrollment.EnrollAsync(upstream.BaseUrl, "", "HUB-9"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            EdgeNodeEnrollment.EnrollAsync("", "token-1", "HUB-9"));
    }

    [Fact]
    public async Task EnrollAsync_surfaces_server_error_envelope()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.EnrollStatus = 403;
        upstream.EnrollResponse = "{\"code\":\"ENROLLMENT_TOKEN_EXHAUSTED\",\"message\":\"已用尽\"}";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdgeNodeEnrollment.EnrollAsync(upstream.BaseUrl, "token-1", "HUB-9"));

        Assert.Contains("403", ex.Message);
        Assert.Contains("ENROLLMENT_TOKEN_EXHAUSTED", ex.Message);
    }
}