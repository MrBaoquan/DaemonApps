using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_and_unprotect_roundtrip()
    {
        const string plain = "edge-key-123456";
        var encrypted = SecretProtector.Protect(plain);

        Assert.StartsWith("enc:v1:", encrypted);
        Assert.NotEqual(plain, encrypted);
        Assert.True(SecretProtector.IsEncrypted(encrypted));
        Assert.Equal(plain, SecretProtector.Unprotect(encrypted));
    }

    [Fact]
    public void Unprotect_returns_plaintext_for_legacy_value()
    {
        Assert.False(SecretProtector.IsEncrypted("plain-legacy"));
        Assert.Equal("plain-legacy", SecretProtector.Unprotect("plain-legacy"));
    }

    [Fact]
    public void TryUnprotect_returns_null_on_corrupt_ciphertext()
    {
        Assert.Null(SecretProtector.TryUnprotect("enc:v1:not-base64!!"));
    }
}
