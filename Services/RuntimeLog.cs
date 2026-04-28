using System.Text;
using System.IO;

namespace BoothDesktop.Services;

public static class RuntimeLog
{
    private static readonly object Gate = new();

    public static void Info(string source, string message) => Write("INFO", source, message);
    public static void Warn(string source, string message) => Write("WARN", source, message);
    public static void Error(string source, string message) => Write("ERROR", source, message);

    private static void Write(string level, string source, string message)
    {
        try
        {
            var utc = DateTime.UtcNow;
            var local = DateTime.Now;
            var line = $"{utc:O} [{level}] [{source}] {message} (local={local:yyyy-MM-dd HH:mm:ss.fff})";
            lock (Gate)
            {
                Directory.CreateDirectory(LessRealBoothPaths.LogsRoot);
                var path = LessRealBoothPaths.RuntimeLogPathUtc(utc);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            /* never break UX because logger failed */
        }
    }
}
