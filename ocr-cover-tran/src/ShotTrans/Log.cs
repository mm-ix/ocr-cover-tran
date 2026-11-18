using System.Text;

namespace ShotTrans;

public static class Log
{
    private static readonly object Gate = new();
    private static volatile string? _path;

    public static void Init(string logDirectory) => Configure(logDirectory, true);

    /// <summary>enabled=false 时不创建目录、不写入任何日志（已有日志文件保留）。</summary>
    public static void Configure(string logDirectory, bool enabled)
    {
        lock (Gate)
        {
            if (!enabled)
            {
                _path = null;
                return;
            }

            try
            {
                Directory.CreateDirectory(logDirectory);
                var path = Path.Combine(logDirectory, "shotrans.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var old = Path.Combine(logDirectory, "shotrans.old.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                _path = path;
            }
            catch
            {
                _path = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var path = _path;
        if (path == null) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").AppendLine(message);
            if (ex != null)
            {
                sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
            }
            lock (Gate)
            {
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
