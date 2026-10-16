using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AndX.Edge;
using Xunit;

namespace AndX.Edge.Tests;

/// <summary>
/// 手动端到端（默认跳过）：把**真实 Edge 代码**接到**真实后端**，验证 S7 的 Relay / Hub 行为。
///
/// 运行：
/// <code>
/// $env:ANDX_E2E="1"; $env:ANDX_E2E_UPSTREAM="http://127.0.0.1"; $env:ANDX_E2E_ADMIN_PASS="&lt;admin 口令&gt;"
/// dotnet test AndXEdge.Tests/AndXEdge.Tests.csproj --filter ManualEdgeE2ETests
/// </code>
///
/// 前置：本机 dev 栈在跑（nginx:80 → api 容器），且已应用主仓迁移 0015–0017。
/// 副作用：创建并停用 2 个节点、签发并消费 1 张注册令牌。
/// </summary>
public sealed class ManualEdgeE2ETests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ANDX_E2E") == "1";

    private static string Upstream => (Environment.GetEnvironmentVariable("ANDX_E2E_UPSTREAM") ?? "http://127.0.0.1").TrimEnd('/');

    private static string AdminUser => Environment.GetEnvironmentVariable("ANDX_E2E_ADMIN_USER") ?? "admin";

    private static string? AdminPass => Environment.GetEnvironmentVariable("ANDX_E2E_ADMIN_PASS");

    [Fact]
    public async Task Relay_and_hub_round_trip_against_live_backend()
    {
        if (!Enabled)
        {
            return; // 手动用例：未显式开启时静默跳过
        }
        Assert.False(string.IsNullOrWhiteSpace(AdminPass), "缺少 ANDX_E2E_ADMIN_PASS");

        using var admin = new HttpClient { BaseAddress = new Uri(Upstream) };
        var token = await AdminLoginAsync(admin);
        var (projectId, exhibitId) = await PickProjectExhibitAsync(admin, token);
        var run = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        // ===== 1) 展项机节点（注册令牌 → 自助注册） =====
        var tokenResp = await SendJsonAsync(
            admin,
            HttpMethod.Post,
            "/api/admin/enrollment-tokens",
            new { exhibitId, maxUses = 1, ttlSeconds = 600 },
            token);
        tokenResp.EnsureSuccessStatusCode();
        var plainToken = (await ReadDataAsync(tokenResp)).GetProperty("token").GetString();

        using var anonymous = new HttpClient { BaseAddress = new Uri(Upstream) };
        var machineNodeId = $"S7E2E-EXHIBIT-{run}";
        var enrollResp = await SendJsonAsync(
            anonymous,
            HttpMethod.Post,
            "/api/edge/enroll",
            new { token = plainToken, nodeId = machineNodeId, name = "S7E2E 展项机" },
            null);
        enrollResp.EnsureSuccessStatusCode();
        var enrolled = await ReadDataAsync(enrollResp);
        var machineNode = enrolled.GetProperty("nodeId").GetString()!;
        var machineSecret = enrolled.GetProperty("secret").GetString()!;

        // ===== 2) Relay：插件签名原样经 Edge 透传 =====
        await using var relay = new EdgeProxyHost();
        await relay.StartAsync(new EdgeConfig
        {
            UpstreamBaseUrl = Upstream,
            ListenAddress = "127.0.0.1",
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Relay,
        });

        using var plugin = new HttpClient();
        const string signedPath = "/api/edge/tickets";
        var body = Encoding.UTF8.GetBytes("{\"purpose\":\"EXHIBIT_PAY\"}");
        using (var relayRequest = new HttpRequestMessage(HttpMethod.Post, relay.ListenUrl + signedPath)
               {
                   Content = new ByteArrayContent(body),
               })
        {
            relayRequest.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            foreach (var (name, value) in EdgeNodeSigner.Sign("POST", signedPath, body, machineNode, machineSecret))
            {
                relayRequest.Headers.TryAddWithoutValidation(name, value);
            }

            using var relayResponse = await plugin.SendAsync(relayRequest);
            var relayPayload = await relayResponse.Content.ReadAsStringAsync();
            Assert.True(
                relayResponse.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created,
                $"Relay 透传应放行，实际 {(int)relayResponse.StatusCode}: {relayPayload}");
        }

        // ===== 3) Hub：客户端不签名，Edge 注入展项后用自己的节点身份重签 =====
        var hubNodeId = $"S7E2E-HUB-{run}";
        var hubRegResp = await SendJsonAsync(
            admin,
            HttpMethod.Post,
            "/api/admin/devices",
            new { deviceId = hubNodeId, role = "hub", projectId, name = "S7E2E 中控" },
            token);
        hubRegResp.EnsureSuccessStatusCode();
        var hubReg = await ReadDataAsync(hubRegResp);
        var hubSecret = hubReg.GetProperty("secret").GetString()!;
        var hubPk = hubReg.GetProperty("device").GetProperty("id").GetString()!;

        var grantResp = await SendJsonAsync(
            admin,
            HttpMethod.Put,
            $"/api/admin/devices/{hubNodeId}/exhibits",
            new { exhibitIds = new[] { exhibitId } },
            token);
        grantResp.EnsureSuccessStatusCode();

        await using var hub = new EdgeProxyHost();
        await hub.StartAsync(new EdgeConfig
        {
            UpstreamBaseUrl = Upstream,
            ListenAddress = "127.0.0.1",
            ListenPort = TestPorts.FreeTcpPort(),
            Role = EdgeRoles.Hub,
            NodeId = hubNodeId,
            NodeSecret = hubSecret,
            ExhibitId = exhibitId,
        });

        using var hubResponse = await SendJsonAsync(
            plugin,
            HttpMethod.Post,
            hub.ListenUrl + "/api/edge/tickets",
            new { purpose = "EXHIBIT_PAY" },
            null);
        var hubPayload = await hubResponse.Content.ReadAsStringAsync();
        Assert.True(
            hubResponse.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created,
            $"Hub 注入应放行，实际 {(int)hubResponse.StatusCode}: {hubPayload}");

        // ===== 4) 审计：Relay 归展项机、Hub 归中控且展项来自授权集合 =====
        var devicesResp = await SendAsync(admin, HttpMethod.Get, $"/api/admin/devices?tenantId={projectId}", token);
        devicesResp.EnsureSuccessStatusCode();
        var devices = await ReadDataAsync(devicesResp);
        string? machinePk = null;
        foreach (var device in devices.EnumerateArray())
        {
            if (device.GetProperty("deviceId").GetString() == machineNode)
            {
                machinePk = device.GetProperty("id").GetString();
            }
        }
        Assert.NotNull(machinePk);

        var auditResp = await SendAsync(
            admin,
            HttpMethod.Get,
            $"/api/admin/audit/requests?projectId={projectId}&limit=100",
            token);
        auditResp.EnsureSuccessStatusCode();
        var audit = await ReadDataAsync(auditResp);

        var relayRow = FindRow(audit, r => r.GetProperty("deviceId").GetString() == machinePk && r.GetProperty("statusCode").GetInt32() < 400);
        Assert.True(relayRow.HasValue, "审计应记录 Relay 透传（归到展项机节点）");
        Assert.Equal("node_hmac", relayRow!.Value.GetProperty("identitySource").GetString());

        var hubRow = FindRow(audit, r => r.GetProperty("deviceId").GetString() == hubPk && r.GetProperty("statusCode").GetInt32() < 400);
        Assert.True(hubRow.HasValue, "审计应记录 Hub 调用（归到中控节点）");
        Assert.Equal("hub_hmac", hubRow!.Value.GetProperty("identitySource").GetString());
        Assert.Equal(exhibitId, hubRow.Value.GetProperty("exhibitId").GetString());

        // ===== 5) 诊断自省：Edge 用自己的节点身份回查 =====
        using var selfClient = new HttpClient();
        var selfJson = await selfClient.GetStringAsync(hub.ListenUrl + "/diagnostics/self");
        Assert.Contains("\"role\":\"hub\"", selfJson.Replace(" ", string.Empty));
        Assert.Contains(exhibitId, selfJson);

        // ===== 6) 清理 =====
        foreach (var node in new[] { machineNode, hubNodeId })
        {
            await SendJsonAsync(admin, HttpMethod.Post, $"/api/admin/devices/{node}/enabled", new { enabled = false }, token);
        }
    }

    private static JsonElement? FindRow(JsonElement page, Func<JsonElement, bool> predicate)
    {
        foreach (var row in page.GetProperty("items").EnumerateArray())
        {
            if (predicate(row))
            {
                return row;
            }
        }
        return null;
    }

    private static async Task<string> AdminLoginAsync(HttpClient client)
    {
        var resp = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/admin/auth/login",
            new { username = AdminUser, password = AdminPass },
            null);
        resp.EnsureSuccessStatusCode();
        var data = await ReadDataAsync(resp);
        return data.GetProperty("accessToken").GetString()!;
    }

    private static async Task<(string projectId, string exhibitId)> PickProjectExhibitAsync(HttpClient client, string token)
    {
        var projects = await SendAsync(client, HttpMethod.Get, "/api/admin/projects", token);
        projects.EnsureSuccessStatusCode();
        var projectList = await ReadDataAsync(projects);
        var projectId = projectList.EnumerateArray().First().GetProperty("id").GetString()!;

        var exhibits = await SendAsync(client, HttpMethod.Get, $"/api/admin/projects/{projectId}/exhibits", token);
        exhibits.EnsureSuccessStatusCode();
        var exhibitList = await ReadDataAsync(exhibits);
        var exhibitId = exhibitList.EnumerateArray().First().GetProperty("id").GetString()!;
        return (projectId, exhibitId);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token) =>
        SendJsonAsync(client, method, path, null, token);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
        }
        return await client.SendAsync(request);
    }

    /// <summary>解析统一信封 {code,message,data} 并返回 data（克隆以避免 JsonDocument 释放）。</summary>
    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("data", out var data)
            ? data.Clone()
            : document.RootElement.Clone();
    }
}