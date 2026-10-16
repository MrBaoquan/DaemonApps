namespace AndX.Edge;

/// <summary>
/// 边缘网关角色（solution §16.3 / implementation §13.7）：
/// 同一程序按配置切换，决定「谁持凭据、以及是否改动转发内容」。
/// </summary>
public enum EdgeRoleKind
{
    /// <summary>本机零密钥形态：Edge 代持凭据并在转发前注入（现状，S5 后由节点凭据取代共享密钥）。</summary>
    Local = 0,

    /// <summary>无外网展项的中继出口：**纯透传**，不改 query/body、不重签、不持凭据。</summary>
    Relay = 1,

    /// <summary>多展项共用出口：Edge 持自己的节点身份，注入展项身份后**重新签名**。</summary>
    Hub = 2,
}

/// <summary>角色解析与常量。</summary>
public static class EdgeRoles
{
    public const string Local = "local";
    public const string Relay = "relay";
    public const string Hub = "hub";

    /// <summary>解析角色字符串（大小写不敏感；未知值回退 <see cref="EdgeRoleKind.Local"/>）。</summary>
    public static EdgeRoleKind Parse(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Relay => EdgeRoleKind.Relay,
            Hub => EdgeRoleKind.Hub,
            _ => EdgeRoleKind.Local,
        };
    }

    /// <summary>角色是否要求配置节点身份（Hub 需要；Relay 不需要；Local 用共享密钥）。</summary>
    public static bool RequiresNodeCredential(EdgeRoleKind role) => role == EdgeRoleKind.Hub;

    /// <summary>是否为真值字符串（用于环境变量开关）。</summary>
    public static bool IsTrue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}