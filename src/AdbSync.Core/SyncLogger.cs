using System.Text;

namespace AdbSync.Core;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {LevelLabel(Level)}{Message}";

    internal static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Error => L.T("ERRORE").PadRight(7),
        LogLevel.Warning => L.T("AVVISO").PadRight(7),
        LogLevel.Debug => "debug  ",
        _ => "       ",
    };
}

/// <summary>
/// Log su file (scritto subito, così sopravvive a crash) e notifica all'interfaccia.
/// <para>Modalità applicazione (<see cref="ForApplication"/>): una cartella per sessione
/// (<c>logs\&lt;data_ora&gt;_&lt;telefono&gt;\sessione.log</c>, con report e diagnostica) e un log di sistema
/// <c>logs\SystemLOG\system_&lt;anno-mese&gt;.log</c> per quello che non riguarda una sessione di copia
/// (avvio, adb, telefoni collegati, errori imprevisti). Finché non c'è una sessione tutto va nel log di sistema.</para>
/// </summary>
public sealed class SyncLogger : IDisposable
{
    public const string SystemFolderName = "SystemLOG";
    public const string SessionLogFileName = "sessione.log";

    private readonly object _lock = new();
    private readonly string? _root;
    private readonly string _tag = "";
    private readonly string? _systemDir;
    private StreamWriter? _writer;

    public event Action<LogEntry>? EntryWritten;
    /// <summary>File di log della sessione (o il file unico in modalità cartella fissa).</summary>
    public string? LogFilePath { get; private set; }
    /// <summary>Cartella dove vanno report, elenco dei nomi convertiti e diagnostica (null prima della sessione).</summary>
    public string? LogDirectory { get; private set; }

    /// <summary>Modalità cartella fissa: un file <c>adbsync_&lt;data&gt;.log</c> nella cartella indicata (test).</summary>
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

    private SyncLogger(string logRoot, string tag)
    {
        _root = logRoot;
        _tag = tag;
        try
        {
            string dir = Path.Combine(logRoot, SystemFolderName);
            Directory.CreateDirectory(dir);
            _systemDir = dir;
        }
        catch { _systemDir = null; }
    }

    /// <summary>Logger del programma: log di sistema subito, cartella di sessione con <see cref="StartSession"/>.</summary>
    /// <param name="tag">chi scrive nel log di sistema (GUI, CLI): i due programmi possono scriverci insieme.</param>
    public static SyncLogger ForApplication(string logRoot, string tag) => new(logRoot, tag);

    public static string DefaultLogDirectory => AppPaths.LogDirectory;

    /// <summary>Radice dei log (modalità applicazione).</summary>
    public string? LogRoot => _root;

    /// <summary>Log di sistema del mese corrente (null in modalità cartella fissa).</summary>
    public string? SystemLogPath => _systemDir is null ? null : Path.Combine(_systemDir, $"system_{DateTime.Now:yyyy-MM}.log");

    public bool HasSession { get { lock (_lock) return _root is not null && _writer is not null; } }

    /// <summary>
    /// Apre una nuova cartella di sessione (chiudendo quella precedente) e restituisce il percorso,
    /// oppure null se non si può creare: in quel caso si continua a scrivere solo nel log di sistema.
    /// </summary>
    public string? StartSession(string folderName)
    {
        if (_root is null) return null;
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            LogDirectory = null;
            LogFilePath = null;
        }
        try
        {
            string dir = Path.Combine(_root, folderName);
            for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(_root, $"{folderName}_{n}");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SessionLogFileName);
            var w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            lock (_lock) { _writer = w; LogDirectory = dir; LogFilePath = path; }
            System(LogLevel.Info, L.F("Sessione di log aperta: {0}", dir));
            return dir;
        }
        catch (Exception ex)
        {
            System(LogLevel.Warning, L.F("Cartella di sessione non creata ({0}): i messaggi restano nel log di sistema.", ex.Message));
            return null;
        }
    }

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warning, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    /// <summary>Messaggio della sessione (nel log di sistema solo finché una sessione non è aperta).</summary>
    public void Write(LogLevel level, string message)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            if (_writer is not null) WriteSession(e);
            else if (_root is not null) WriteSystem(e);
        }
        Notify(e);
    }

    /// <summary>Messaggio del programma (avvio, adb, telefoni, errori imprevisti): sempre nel log di sistema, e anche nella sessione se aperta.</summary>
    /// <param name="notify">false = solo nei file, senza mostrarlo (per chi lo stampa già per conto suo).</param>
    public void System(LogLevel level, string message, bool notify = true)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            if (_writer is not null) WriteSession(e);
            if (_root is not null) WriteSystem(e);
        }
        if (notify) Notify(e);
    }

    private void Notify(LogEntry e)
    {
        try { EntryWritten?.Invoke(e); } catch { }
    }

    private void WriteSession(LogEntry e)
    {
        try { _writer?.WriteLine($"{e.Time:yyyy-MM-dd} {e}"); } catch { /* il log non deve mai bloccare la copia */ }
    }

    /// <summary>Un solo scrittore alla volta nel log di sistema, anche tra più logger dello stesso processo.</summary>
    private static readonly object SystemFileLock = new();

    /// <summary>
    /// Apertura e chiusura a ogni riga, in esclusiva: interfaccia grafica e riga di comando possono scrivere
    /// nello stesso file senza sovrascriversi (chi lo trova occupato riprova per qualche istante).
    /// </summary>
    private void WriteSystem(LogEntry e)
    {
        if (_systemDir is null) return;
        string line = $"{e.Time:yyyy-MM-dd HH:mm:ss} [{_tag}] {LogEntry.LevelLabel(e.Level)}{e.Message}{Environment.NewLine}";
        byte[] bytes = new UTF8Encoding(false).GetBytes(line);
        string path = Path.Combine(_systemDir, $"system_{e.Time:yyyy-MM}.log");
        lock (SystemFileLock)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                    fs.Seek(0, SeekOrigin.End);
                    fs.Write(bytes);
                    return;
                }
                catch (IOException) { Thread.Sleep(10); }   // aperto in scrittura dall'altro programma
                catch { return; }                           // il log non deve mai bloccare il programma
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
