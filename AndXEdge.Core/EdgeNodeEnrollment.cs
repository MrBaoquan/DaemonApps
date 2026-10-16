using System.Net.Http.Json;
using System.Text.Json;

namespace AndX.Edge;

/// <summary>节点自助注册结果（<c>POST /api/edge/enroll</c> 的一次性明文密钥）。</summary>
public sealed record EdgeEnrollmentResult(string NodeId, string Role, string? ExhibitId, string Secret);

/// <summary>
/// 节点自助注册（solution §16.2）：用运维签发的一次性令牌换取本机节点身份。
/// 密钥只在应答中出现一次，调用方必须立即加密落盘（本方法只负责取回，不落盘）。
/// </summary>
public static class EdgeNodeEnrollment
{
    /// <summary>
    /// 用注册令牌换取节点身份；不做重试（令牌一次性，重试可能落在"服务端已消费"之后）。
    /// </summary>
    public static async Task<EdgeEnrollmentResult> EnrollAsync(
        string upstreamBaseUrl,
        string enrollmentToken,
        string nodeId,
        string? name = null,
        TimeSpan? timeout = null,
        HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(upstreamBaseUrl))
        {
            throw new ArgumentException("上游后端地址不能为空", nameof(upstreamBaseUrl));
        }
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            throw new ArgumentException("注册令牌不能为空", nameof(enrollmentToken));
        }
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("节点编号不能为空", nameof(nodeId));
        }

        var ownsClient = client is null;
        var http = client ?? new HttpClient();
        http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            var url = upstreamBaseUrl.TrimEnd('/') + "/api/edge/enroll";
            using var response = await http
                .PostAsJsonAsync(url, new { token = enrollmentToken.Trim(), nodeId = nodeId.Trim(), name })
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"注册失败（HTTP {(int)response.StatusCode}）：{Truncate(payload, 200)}");
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var envelope)
                ? envelope
                : root;

            var secret = data.TryGetProperty("secret", out var secretValue) ? secretValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException("注册响应缺少节点密钥（secret）");
            }

            return new EdgeEnrollmentResult(
                data.TryGetProperty("nodeId", out var id) && id.GetString() is { Length: > 0 } returned ? returned : nodeId.Trim(),
                data.TryGetProperty("role", out var role) ? role.GetString() ?? EdgeRoles.Hub : EdgeRoles.Hub,
                data.TryGetProperty("exhibitId", out var exhibit) ? exhibit.GetString() : null,
                secret!);
        }
        finally
        {
            if (ownsClient)
            {
                http.Dispose();
            }
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>配置项的文本格式（界面编辑用）：Hub 展项映射按行 <c>IP=展项ID</c>。</summary>
public static class EdgeConfigFormat
{
    /// <summary>解析「IP=展项ID」多行文本；忽略空行与 # 注释，非法行抛出便于界面提示。</summary>
    public static Dictionary<string, string> ParseExhibitMap(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new FormatException($"展项映射行格式应为 IP=展项ID：{line}");
            }

            map[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return map;
    }

    /// <summary>序列化为多行文本（按 IP 排序，便于人工核对）。</summary>
    public static string FormatExhibitMap(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            map.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));
    }
}