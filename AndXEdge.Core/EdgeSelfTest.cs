using System.Diagnostics;

namespace AndX.Edge;

/// <summary>链路自检结果。</summary>
public sealed record EdgeSelfTestResult(bool Ok, string Message, int? Status, long ElapsedMs);

/// <summary>
/// 链路自检：经本机 Edge 调用一个受边缘密钥保护的真实路由，用于区分
/// 「Edge 未运行 / 上游不可达 / 密钥无效 / 链路正常」。
/// </summary>
public static class EdgeSelfTest
{
    /// <summary>执行自检。<paramref name="listenUrl"/> 为本机 Edge 基址。</summary>
    public static async Task<EdgeSelfTestResult> RunAsync(string listenUrl, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

            // 攻击面最小：GET 一个必然不存在的大文件分片上传会话；
            // 401=密钥被拒；503=后端未启用；其余(<500)说明鉴权已通过、上游可达。
            var url = $"{listenUrl.TrimEnd('/')}{EdgeConstants.EdgePathPrefix}/uploads/__andx_selftest__";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            return status switch
            {
                401 => new EdgeSelfTestResult(false, "Edge 密钥无效或被拒绝（401 EDGE_UNAUTHORIZED）", status, stopwatch.ElapsedMilliseconds),
                503 => new EdgeSelfTestResult(false, "后端未启用边缘接口（503，缺少 ANDX_EDGE_KEY）", status, stopwatch.ElapsedMilliseconds),
                _ when status < 500 => new EdgeSelfTestResult(true, $"链路正常（上游返回 {status}，鉴权已通过）", status, stopwatch.ElapsedMilliseconds),
                _ => new EdgeSelfTestResult(false, $"上游返回异常状态 {status}", status, stopwatch.ElapsedMilliseconds),
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new EdgeSelfTestResult(false, $"无法连通本机 Edge：{ex.Message}", null, stopwatch.ElapsedMilliseconds);
        }
    }
}
