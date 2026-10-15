using System.Text;

namespace AdbSync.Core;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss} {Level switch { LogLevel.Error => "ERRORE ", LogLevel.Warning => "AVVISO ", LogLevel.Debug => "debug  ", _ => "       " }}{Message}";
}

/// <summary>Log su file (scritto subito, così sopravvive a crash) e notifica all'interfaccia.</summary>
public sealed class SyncLogger : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public event Action<LogEntry>? EntryWritten;
    public string? LogFilePath { get; }
    public string? LogDirectory { get; }

    public SyncLogger(string? logDirectory)
    {
        if (logDirectory is null) return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            LogDirectory = logDirectory;
            LogFilePath = Path.Combine(logDirectory, $"adbsync_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _writer = new StreamWriter(LogFilePath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    public static string DefaultLogDirectory => AppPaths.LogDirectory;

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warning, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    public void Write(LogLevel level, string message)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            try { _writer?.WriteLine($"{e.Time:yyyy-MM-dd} {e}"); } catch { /* il log non deve mai bloccare la copia */ }
        }
        try { EntryWritten?.Invoke(e); } catch { }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
