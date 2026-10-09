using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace AndX.Edge;

/// <summary>
/// AndXEdge 应用入口。支持无人值守部署子命令：
/// <list type="bullet">
/// <item><c>--install-edge-key</c>：从 stdin 读取 Edge 密钥，DPAPI 加密写入配置后退出（密钥不经命令行，避免同机进程读取 cmdline）。</item>
/// <item><c>--print-config</c>：打印脱敏配置与就绪状态后退出（排障用）。</item>
/// </list>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args ?? Array.Empty<string>();

        if (Array.Exists(args, a => string.Equals(a, "--install-edge-key", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(InstallEdgeKeyFromStdin());
            return;
        }

        if (Array.Exists(args, a => string.Equals(a, "--print-config", StringComparison.OrdinalIgnoreCase)))
        {
            PrintConfig();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private static int InstallEdgeKeyFromStdin()
    {
        try
        {
            // 部署：echo "edge-key" | AndXEdge.exe --install-edge-key
            var key = Console.ReadLine()?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                Console.Error.WriteLine("安装失败：未从 stdin 读到 Edge 密钥");
                return 1;
            }

            var config = EdgeConfig.Load();
            config.EdgeKey = key;
            config.Save();
            Console.WriteLine($"Edge 密钥已加密写入 {config.ConfigPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"安装失败: {ex.Message}");
            return 1;
        }
    }

    private static void PrintConfig()
    {
        try
        {
            var config = EdgeConfig.Load();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                service = EdgeConstants.ServiceName,
                version = EdgeConstants.Version,
                upstream = config.UpstreamBaseUrl,
                listen = config.ListenUrl,
                exhibitId = config.ExhibitId,
                edgeKey = config.MaskedEdgeKey,
                ready = config.IsReady,
                error = config.ReadinessError,
                configPath = config.ConfigPath,
            }, options));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取配置失败: {ex.Message}");
        }
    }
}
