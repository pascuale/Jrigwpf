using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Jrig.Audio;

/// <summary>
/// Log file for audio troubleshooting: %AppData%\jrig\logs\jrig-YYYYMMDD-HHMMSS.log (last 10 kept).
/// Never call this from the audio thread (it allocates); the audio thread only bumps counters in
/// <see cref="SignalProbe"/>, and the diagnostics window / a timer logs those once a second.
/// </summary>
public static class DiagLog
{
    private static readonly BlockingCollection<string> Queue = new(2000);
    private static readonly List<string> Recent = new();
    private static readonly object RecentLock = new();
    private static readonly Thread Writer;

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jrig", "logs");

    public static string LogPath { get; }

    /// <summary>Raised on a background thread for every line written.</summary>
    public static event Action<string>? LineLogged;

    static DiagLog()
    {
        LogPath = Path.Combine(LogDirectory, $"jrig-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            foreach (var old in new DirectoryInfo(LogDirectory).GetFiles("jrig-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
            {
                try { old.Delete(); } catch { /* ignore */ }
            }
        }
        catch { /* logging must never stop the app */ }

        Writer = new Thread(WriterLoop) { IsBackground = true, Name = "jrig log writer" };
        Writer.Start();

        Info($"jrig log started. App base: {AppContext.BaseDirectory}");
        Info($"OS: {Environment.OSVersion}  64-bit process: {Environment.Is64BitProcess}  .NET: {Environment.Version}  " +
             $"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (RecentLock)
        {
            Recent.Add(line);
            if (Recent.Count > 600) Recent.RemoveRange(0, 100);
        }
        Queue.TryAdd(line);
        try { LineLogged?.Invoke(line); } catch { /* ignore */ }
    }

    /// <summary>The most recent lines kept in memory (for the diagnostics window).</summary>
    public static string[] Tail(int count)
    {
        lock (RecentLock)
            return Recent.Skip(Math.Max(0, Recent.Count - count)).ToArray();
    }

    /// <summary>Whole log so far (reads the file; falls back to the in-memory tail).</summary>
    public static string ReadAll()
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch
        {
            return string.Join(Environment.NewLine, Tail(600));
        }
    }

    private static void WriterLoop()
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var line in Queue.GetConsumingEnumerable())
                sw.WriteLine(line);
        }
        catch { /* disk problems must not crash audio */ }
    }
}
