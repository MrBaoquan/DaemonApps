namespace AndX.Edge;

/// <summary>AndXEdge 常量。契约细节与 <c>andx-sdk-spec</c> / 后端 EdgeKeyGuard 保持一致。</summary>
public static class EdgeConstants
{
    /// <summary>服务名（诊断页、/healthz 展示）。</summary>
    public const string ServiceName = "AndXEdge";

    /// <summary>版本号。</summary>
    public const string Version = "0.1.0";

    /// <summary>边缘网关共享密钥请求头（与后端 EdgeKeyGuard 一致）。</summary>
    public const string KeyHeader = "x-edge-key";

    /// <summary>请求关联 ID 头（Edge 在缺失时生成，便于日志串联）。</summary>
    public const string RequestIdHeader = "x-request-id";

    /// <summary>默认监听端口（沿用旧 FileSharer，便于存量联调）。</summary>
    public const int DefaultPort = 6699;

    /// <summary>默认绑定地址：仅环回（本机信任边界）。</summary>
    public const string DefaultListenAddress = "127.0.0.1";

    /// <summary>被代理的后端路径前缀。</summary>
    public const string EdgePathPrefix = "/api/edge";

    /// <summary>公开资源路径前缀（二维码出图，可选代理，供无外网场景）。</summary>
    public const string ResourcesPathPrefix = "/api/resources";
}
