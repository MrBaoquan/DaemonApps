using System.Security.Cryptography;
using System.Text;

namespace AndX.Edge;

/// <summary>
/// Windows DPAPI 密钥保护（CurrentUser 作用域）。
/// 存储格式：<c>enc:v1:&lt;base64&gt;</c>；读取兼容历史明文。
/// 红线：密钥严禁明文落盘/入日志；仅 Windows 可用。
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:v1:";

    /// <summary>判断字符串是否为 DPAPI 密文。</summary>
    public static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密明文；非 Windows 平台抛 <see cref="PlatformNotSupportedException"/>。</summary>
    public static string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 密钥保护仅在 Windows 可用");
        }
        var bytes = Encoding.UTF8.GetBytes(plain);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>解密；非密文原样返回（兼容明文配置）。</summary>
    public static string Unprotect(string? value)
    {
        if (value is null) return string.Empty;
        if (!IsEncrypted(value)) return value;
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 密钥保护仅在 Windows 可用");
        }
        var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>尝试解密；失败返回 null（不抛异常，避免配置损坏阻断启动）。</summary>
    public static string? TryUnprotect(string? value)
    {
        try
        {
            return Unprotect(value);
        }
        catch
        {
            return null;
        }
    }
}
