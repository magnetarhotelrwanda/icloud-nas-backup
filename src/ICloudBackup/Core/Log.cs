using System.Text;

namespace ICloudBackup.Core;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ICloudBackup");

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string ReportDir => Path.Combine(DataDir, "reports");
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string DbPath => Path.Combine(DataDir, "state.db");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(ReportDir);
    }
}

public enum LogLevel { Debug, Info, Ok, Warn, Error }

public sealed record LogEntry(long Seq, DateTime Time, LogLevel Level, string Message);

/// <summary>Thread-safe logger: daily rolling file plus an in-memory ring buffer the UI reads.</summary>
public static class Log
{
    private const int RingSize = 2000;
    private const int KeepDays = 30;

    private static readonly object Lock = new();
    private static readonly Queue<LogEntry> Ring = new();
    private static long _seq;
    private static StreamWriter? _writer;
    private static DateOnly _writerDate;

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Ok(string msg) => Write(LogLevel.Ok, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void Error(string msg, Exception ex) => Write(LogLevel.Error, $"{msg}: {ex}");

    public static void Write(LogLevel level, string msg)
    {
        var now = DateTime.Now;
        lock (Lock)
        {
            var entry = new LogEntry(++_seq, now, level, msg);
            if (level != LogLevel.Debug)
            {
                Ring.Enqueue(entry);
                while (Ring.Count > RingSize) Ring.Dequeue();
            }

            try
            {
                var today = DateOnly.FromDateTime(now);
                if (_writer == null || _writerDate != today)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(AppPaths.LogDir);
                    var path = Path.Combine(AppPaths.LogDir, $"icloudbackup-{today:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    _writerDate = today;
                    PruneOldLogs();
                }
                _writer.WriteLine($"[{now:yyyy-MM-dd HH:mm:ss}][{level.ToString().ToUpperInvariant()}] {msg}");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    /// <summary>Returns ring-buffer entries newer than <paramref name="afterSeq"/>.</summary>
    public static List<LogEntry> GetSince(long afterSeq)
    {
        lock (Lock)
        {
            return Ring.Where(e => e.Seq > afterSeq).ToList();
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            foreach (var f in new DirectoryInfo(AppPaths.LogDir).GetFiles("icloudbackup-*.log"))
            {
                if (f.LastWriteTime < DateTime.Now.AddDays(-KeepDays)) f.Delete();
            }
        }
        catch { /* best effort */ }
    }
}
