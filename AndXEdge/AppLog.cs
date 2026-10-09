using System.IO;
using System.Text;

namespace AndX.Edge;

/// <summary>轻量文件日志（写入程序目录 Logs/，按天分文件）。失败不影响主流程。</summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                var file = Path.Combine(LogDirectory, $"andx-edge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(
                    file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响业务
        }
    }
}
