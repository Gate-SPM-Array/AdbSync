using System.Text;
using System.Text.RegularExpressions;

namespace AdbSync.Core;

/// <summary>Chi è il telefono: produttore, modello, nome dato dall'utente, versione di Android.</summary>
public sealed record DeviceIdentity(string Manufacturer, string Model, string? Name, string? AndroidVersion)
{
    /// <summary>
    /// Un solo comando (mksh + toybox): ogni riga ha un prefisso, così una riga vuota o un comando mancante
    /// (per esempio <c>settings</c> non disponibile) non sposta le altre.
    /// </summary>
    public const string Command =
        "printf 'm=%s\\n' \"$(getprop ro.product.manufacturer)\"; " +
        "printf 'p=%s\\n' \"$(getprop ro.product.model)\"; " +
        "printf 'n=%s\\n' \"$(settings get global device_name 2>/dev/null)\"; " +
        "printf 'b=%s\\n' \"$(settings get secure bluetooth_name 2>/dev/null)\"; " +
        "printf 'a=%s\\n' \"$(getprop ro.build.version.release)\"";

    public static DeviceIdentity Parse(string output, string fallbackModel)
    {
        string? m = null, p = null, n = null, b = null, a = null;
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '=') continue;
            string v = line[2..].Trim();
            if (v.Length == 0 || v == "null") continue;
            switch (line[0])
            {
                case 'm': m = v; break;
                case 'p': p = v; break;
                case 'n': n = v; break;
                case 'b': b = v; break;
                case 'a': a = v; break;
            }
        }
        return new DeviceIdentity(m ?? "", p ?? fallbackModel, n ?? b, a);
    }

    /// <summary>Solo il modello noto ad adb (quando il telefono non risponde al comando).</summary>
    public static DeviceIdentity Fallback(string model) => new("", model, null, null);

    /// <summary>Produttore + modello, senza ripetere il produttore se il modello lo contiene già.</summary>
    public string FullModel
    {
        get
        {
            string maker = Manufacturer.Length > 0 ? char.ToUpperInvariant(Manufacturer[0]) + Manufacturer[1..] : "";
            if (maker.Length == 0 || Model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase)) return Model;
            return maker + " " + Model;
        }
    }

    /// <summary>Nome dato dall'utente, solo se dice qualcosa in più del modello.</summary>
    public string? DistinctName =>
        Name is null || Name.Equals(Model, StringComparison.OrdinalIgnoreCase) || Name.Equals(FullModel, StringComparison.OrdinalIgnoreCase)
            ? null : Name;

    public string Describe() =>
        FullModel + (DistinctName is { } n ? $" «{n}»" : "") + (AndroidVersion is { } a ? ", Android " + a : "");
}

/// <summary>Statistiche della cartella dei log (mostrate all'apertura del programma).</summary>
public readonly record struct LogFolderStats(int Sessions, long Bytes, int OlderFiles);

/// <summary>Cartelle di sessione: logs\&lt;data_ora&gt;_&lt;modello&gt;_&lt;nome&gt;\.</summary>
public static class LogSessions
{
    private static readonly Regex SessionName = new(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_", RegexOptions.CultureInvariant);
    private const int MaxPart = 40;

    /// <summary>
    /// Nome della cartella: data e ora (così l'ordine alfabetico è quello cronologico), modello e nome del telefono.
    /// Mai il numero di serie intero: al massimo le ultime 4 cifre, per distinguere due telefoni uguali.
    /// </summary>
    public static string FolderName(DateTime start, DeviceIdentity id, string? serialSuffix = null)
    {
        var parts = new List<string> { start.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) };
        if (Clean(id.FullModel) is { } model) parts.Add(model);
        if (id.DistinctName is { } name && Clean(name) is { } n && !parts.Contains(n, StringComparer.OrdinalIgnoreCase)) parts.Add(n);
        if (serialSuffix is not null && Clean(serialSuffix) is { } s) parts.Add(s);
        if (parts.Count == 1) parts.Add("telefono");
        return string.Join('_', parts);
    }

    /// <summary>Ultime 4 lettere/cifre del numero di serie.</summary>
    public static string SerialSuffix(string serial)
    {
        var alnum = serial.Where(char.IsAsciiLetterOrDigit).ToArray();
        return new string(alnum[Math.Max(0, alnum.Length - 4)..]);
    }

    /// <summary>Solo lettere e cifre; tutto il resto diventa un trattino (valido su NTFS/exFAT, niente "_" che separa le parti).</summary>
    internal static string? Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string r = sb.ToString().TrimEnd('-');
        if (r.Length > MaxPart) r = r[..MaxPart].TrimEnd('-');
        return r.Length == 0 ? null : r;
    }

    public static bool IsSessionFolder(string name) => SessionName.IsMatch(name);

    /// <summary>
    /// Zip da allegare a una segnalazione: il log della sessione indicata e il SystemLOG del mese (e del precedente).
    /// Niente report CSV né diagnostica: contengono l'elenco completo dei file. Restituisce il percorso dello zip.
    /// </summary>
    public static string CreateSupportZip(string logRoot, string? sessionDirectory, DateTime now)
    {
        string zipPath = Path.Combine(logRoot, $"segnalazione_{now:yyyyMMdd_HHmmss}.zip");
        var files = new List<(string Source, string Entry)>();
        if (sessionDirectory is not null)
        {
            string s = Path.Combine(sessionDirectory, SyncLogger.SessionLogFileName);
            // nome generico: la cartella della sessione contiene il nome dato al telefono
            if (File.Exists(s)) files.Add((s, "sessione/" + SyncLogger.SessionLogFileName));
        }
        foreach (var month in new[] { now.AddMonths(-1), now })
        {
            string name = $"system_{month:yyyy-MM}.log";
            string p = Path.Combine(logRoot, SyncLogger.SystemFolderName, name);
            if (File.Exists(p)) files.Add((p, SyncLogger.SystemFolderName + "/" + name));
        }
        Directory.CreateDirectory(logRoot);
        using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (source, entryName) in files)
        {
            // i log possono essere aperti in scrittura proprio ora
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using var output = entry.Open();
            input.CopyTo(output);
        }
        return zipPath;
    }

    /// <summary>Numero di sessioni, dimensione totale della cartella log e file delle versioni precedenti (prima della 0.1.7).</summary>
    public static LogFolderStats Inspect(string logRoot)
    {
        var root = new DirectoryInfo(logRoot);
        if (!root.Exists) return default;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long bytes = 0;
        try { foreach (var f in root.EnumerateFiles("*", opts)) bytes += f.Length; } catch { }
        int sessions = 0, older = 0;
        try
        {
            sessions = root.EnumerateDirectories().Count(d => IsSessionFolder(d.Name));
            older = root.EnumerateFiles().Count(f => !f.Name.StartsWith("segnalazione_", StringComparison.OrdinalIgnoreCase)) + root.EnumerateDirectories("diagnostica_*").Count();
        }
        catch { }
        return new LogFolderStats(sessions, bytes, older);
    }
}
