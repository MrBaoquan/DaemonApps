using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

public sealed class EdgeConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"andx-edge-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var config = EdgeConfig.Load(_path);

        Assert.Equal(EdgeConstants.DefaultPort, config.ListenPort);
        Assert.Equal(EdgeConstants.DefaultListenAddress, config.ListenAddress);
        Assert.False(config.IsReady);
        Assert.NotNull(config.ReadinessError);
    }

    [Fact]
    public void Save_and_load_roundtrip_encrypts_key()
    {
        var config = EdgeConfig.Load(_path);
        config.UpstreamBaseUrl = "http://10.0.0.8:3000";
        config.ListenPort = 7000;
        config.EdgeKey = "server-side-key";
        config.Save();

        var raw = File.ReadAllText(_path);
        Assert.DoesNotContain("server-side-key", raw); // 明文绝不落盘（安全红线）
        Assert.Contains("enc:v1:", raw);

        var loaded = EdgeConfig.Load(_path);
        Assert.Equal("http://10.0.0.8:3000", loaded.UpstreamBaseUrl);
        Assert.Equal(7000, loaded.ListenPort);
        Assert.Equal("server-side-key", loaded.EdgeKey);
        Assert.True(loaded.IsReady);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_uses_env_values()
    {
        var config = new EdgeConfig();
        Environment.SetEnvironmentVariable("ANDX_EDGE_UPSTREAM", "http://env-host:1234");
        Environment.SetEnvironmentVariable("ANDX_EDGE_KEY", "env-key");
        Environment.SetEnvironmentVariable("ANDX_EDGE_LISTEN_PORT", "8123");
        Environment.SetEnvironmentVariable("ANDX_EDGE_EXHIBIT_ID", "1001");
        try
        {
            config.ApplyEnvironmentOverrides();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANDX_EDGE_UPSTREAM", null);
            Environment.SetEnvironmentVariable("ANDX_EDGE_KEY", null);
            Environment.SetEnvironmentVariable("ANDX_EDGE_LISTEN_PORT", null);
            Environment.SetEnvironmentVariable("ANDX_EDGE_EXHIBIT_ID", null);
        }

        Assert.Equal("http://env-host:1234", config.UpstreamBaseUrl);
        Assert.Equal("env-key", config.EdgeKey);
        Assert.Equal(8123, config.ListenPort);
        Assert.Equal("1001", config.ExhibitId);
        Assert.True(config.IsReady);
    }

    [Fact]
    public void MaskedEdgeKey_hides_value()
    {
        Assert.Equal("****efgh", new EdgeConfig { EdgeKey = "abcdefgh" }.MaskedEdgeKey);
        Assert.Equal("（未配置）", new EdgeConfig().MaskedEdgeKey);
    }

    [Fact]
    public void ReadinessError_reports_missing_fields()
    {
        Assert.Contains("上游", new EdgeConfig { EdgeKey = "k" }.ReadinessError);
        Assert.Contains("Edge 密钥", new EdgeConfig { UpstreamBaseUrl = "http://x:1" }.ReadinessError);
    }
}
