using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AndX.Edge;

/// <summary>
/// 节点请求签名（solution §16.3 / implementation §13.3；服务端 <c>device-crypto.service.ts</c> 的镜像）。
///
/// <code>
/// canonical = [ METHOD, PATH?sortedQuery, TIMESTAMP(ms), NONCE, SHA256-HEX(body) ].join('\n')
/// </code>
///
/// - 查询串规范化：按 key 升序（key 相同按 value 升序）、**不做百分号解码**、空值渲染为 `key`、无参数省略 `?`；
/// - 签名 = HMAC-SHA256(canonical, 节点密钥) 的小写 hex，随 `x-device-id` / `x-device-timestamp` /
///   `x-device-nonce` / `x-device-signature` 下发；
/// - 流式端点（octet-stream）另下发 `x-device-body-sha256`（服务端不缓冲 body，用它参与签名并校验实际字节）。
///
/// ⚠️ **中间层铁律**：`query` 与 `body` 都在签名内 —— Hub 注入展项后必须**重新签名**，
/// Relay 则不得改动任何一项（见 implementation §13.7）。
/// </summary>
public static class EdgeNodeSigner
{
    /// <summary>组装签名原文。</summary>
    public static string BuildCanonical(string method, string pathWithQuery, string timestamp, string nonce, string bodyHashHex)
    {
        return string.Join(
            "\n",
            string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant(),
            Canonicalize(pathWithQuery),
            timestamp ?? string.Empty,
            nonce ?? string.Empty,
            bodyHashHex ?? string.Empty);
    }

    /// <summary>请求体 SHA-256（小写 hex；空体为固定空串哈希）。</summary>
    public static string Sha256Hex(byte[]? body)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(body ?? Array.Empty<byte>()));
    }

    /// <summary>HMAC-SHA256 签名（小写 hex）。</summary>
    public static string Sign(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty));
        return ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical ?? string.Empty)));
    }

    /// <summary>一次性 nonce（32 位 hex；服务端按节点维度去重）。</summary>
    public static string NewNonce() => Guid.NewGuid().ToString("N");

    /// <summary>时间戳（Unix 毫秒，十进制字符串）。</summary>
    public static string NowMilliseconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>路径 + 规范化查询串（与服务端 <c>canonicalizePath</c> 逐字节一致）。</summary>
    public static string Canonicalize(string? pathWithQuery)
    {
        var raw = pathWithQuery ?? string.Empty;
        var separator = raw.IndexOf('?');
        if (separator < 0)
        {
            return raw;
        }

        var path = raw[..separator];
        var pairs = new List<string[]>();
        foreach (var segment in raw[(separator + 1)..].Split('&'))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            var eq = segment.IndexOf('=');
            pairs.Add(eq < 0
                ? new[] { segment, string.Empty }
                : new[] { segment[..eq], segment[(eq + 1)..] });
        }

        pairs.Sort((a, b) =>
        {
            var keyCompare = string.CompareOrdinal(a[0], b[0]);
            return keyCompare != 0 ? keyCompare : string.CompareOrdinal(a[1], b[1]);
        });

        var query = string.Join("&", pairs.Select(pair => pair[1].Length == 0 ? pair[0] : $"{pair[0]}={pair[1]}"));
        return query.Length > 0 ? $"{path}?{query}" : path;
    }

    /// <summary>按最终（已注入展项后的）路径与字节计算签名头；调用方需先移除客户端伪造的 <c>x-device-*</c>。</summary>
    public static IReadOnlyDictionary<string, string> Sign(
        string method,
        string pathWithQuery,
        byte[]? body,
        string nodeId,
        string nodeSecret)
    {
        var bodyHash = Sha256Hex(body);
        var timestamp = NowMilliseconds();
        var nonce = NewNonce();
        var signature = Sign(nodeSecret, BuildCanonical(method, pathWithQuery, timestamp, nonce, bodyHash));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EdgeConstants.DeviceIdHeader] = nodeId,
            [EdgeConstants.DeviceTimestampHeader] = timestamp,
            [EdgeConstants.DeviceNonceHeader] = nonce,
            [EdgeConstants.DeviceBodySha256Header] = bodyHash,
            [EdgeConstants.DeviceSignatureHeader] = signature,
        };
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}