using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

/// <summary>
/// 节点签名（solution §16.3）：canonical 必须与服务端 <c>device-crypto.service.ts</c> / 端侧 C# 逐字节一致。
/// 向量与主仓 <c>deploy/smoke/*</c> 及 unity 包内 <c>NodeIdentityTests</c> 同源。
/// </summary>
public sealed class EdgeNodeSignerTests
{
    private const string Secret = "test-node-secret-0123456789";
    private const string Timestamp = "1735689600000";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Canonical_and_hmac_match_shared_test_vector()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"purpose\":\"EXHIBIT_PAY\",\"exhibitId\":\"1024\"}");

        Assert.Equal("6a1144e259763bdb5d2f7c7e5e527205475475d23717d9d05947d882c123c74c", EdgeNodeSigner.Sha256Hex(body));

        var canonical = EdgeNodeSigner.BuildCanonical("POST", "/api/edge/tickets", Timestamp, Nonce, EdgeNodeSigner.Sha256Hex(body));
        Assert.Equal(
            "POST\n/api/edge/tickets\n" + Timestamp + "\n" + Nonce
            + "\n6a1144e259763bdb5d2f7c7e5e527205475475d23717d9d05947d882c123c74c",
            canonical);
        Assert.Equal("7e569fb70b43c99897aaf83ccde9f50fb178b2d02f83439dd91840af2b1ead01", EdgeNodeSigner.Sign(Secret, canonical));

        // 空体（GET）向量
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", EdgeNodeSigner.Sha256Hex(null));
    }

    [Fact]
    public void Canonicalize_sorts_query_without_decoding()
    {
        Assert.Equal(
            "/api/edge/resources?exhibitId=1024&fileName=a%20b.png&mediaType=image",
            EdgeNodeSigner.Canonicalize("/api/edge/resources?mediaType=image&fileName=a%20b.png&exhibitId=1024"));
        Assert.Equal("/x?j=1&k=a&k=b", EdgeNodeSigner.Canonicalize("/x?k=b&k=a&j=1"));
        Assert.Equal("/x?flag&z=1", EdgeNodeSigner.Canonicalize("/x?flag=&z=1"));
        Assert.Equal("/api/edge/tickets", EdgeNodeSigner.Canonicalize("/api/edge/tickets"));
        Assert.Equal("/x", EdgeNodeSigner.Canonicalize("/x?"));
    }

    [Fact]
    public void Sign_emits_all_headers_and_is_verifiable_from_final_inputs()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");

        var headers = EdgeNodeSigner.Sign("POST", "/api/edge/tickets?b=2&a=1", body, "HUB-1", Secret);

        Assert.Equal("HUB-1", headers[EdgeConstants.DeviceIdHeader]);
        Assert.Equal(EdgeNodeSigner.Sha256Hex(body), headers[EdgeConstants.DeviceBodySha256Header]);
        Assert.Equal(32, headers[EdgeConstants.DeviceNonceHeader].Length);

        var expected = EdgeNodeSigner.Sign(
            Secret,
            EdgeNodeSigner.BuildCanonical(
                "POST",
                "/api/edge/tickets?a=1&b=2",
                headers[EdgeConstants.DeviceTimestampHeader],
                headers[EdgeConstants.DeviceNonceHeader],
                headers[EdgeConstants.DeviceBodySha256Header]));
        Assert.Equal(expected, headers[EdgeConstants.DeviceSignatureHeader]);
    }

    [Fact]
    public void Role_parsing_defaults_to_local()
    {
        Assert.Equal(EdgeRoleKind.Local, EdgeRoles.Parse(null));
        Assert.Equal(EdgeRoleKind.Local, EdgeRoles.Parse("unknown"));
        Assert.Equal(EdgeRoleKind.Relay, EdgeRoles.Parse("relay"));
        Assert.Equal(EdgeRoleKind.Hub, EdgeRoles.Parse("HUB"));
        Assert.False(EdgeRoles.RequiresNodeCredential(EdgeRoleKind.Relay));
        Assert.True(EdgeRoles.RequiresNodeCredential(EdgeRoleKind.Hub));
    }
}