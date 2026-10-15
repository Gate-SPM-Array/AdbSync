using AdbSync.Core;

namespace AdbSync.Gui;

/// <summary>
/// Configurazione del pacchetto in <c>adbsync.ini</c> accanto all'eseguibile (link del progetto, guida, segnalazioni).
/// Nessun indirizzo è scritto nel codice: chi pubblica una versione derivata cambia solo questo file.
/// Formato INI semplice: sezioni [nome], righe chiave = valore, commenti con ; o #.
/// </summary>
internal sealed class AppConfig
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(AppPaths.BaseDirectory, "adbsync.ini");
    public bool Loaded { get; private set; }

    public static AppConfig Load()
    {
        var c = new AppConfig();
        try
        {
            if (!File.Exists(FilePath)) return c;
            string section = "";
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[' && line[^1] == ']') { section = line[1..^1].Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                c._values[section + "." + line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            c.Loaded = true;
        }
        catch { /* file illeggibile: nessun link disponibile */ }
        return c;
    }

    public string? Get(string section, string key) =>
        _values.TryGetValue(section + "." + key, out var v) && v.Length > 0 ? v : null;

    /// <summary>Valore specifico della lingua attiva (es. help.en), altrimenti quello generico (help).</summary>
    public string? GetLocalized(string section, string key) => Get(section, key + "." + L.Code) ?? Get(section, key);

    /// <summary>Solo indirizzi web (http/https): un valore diverso non viene mai aperto.</summary>
    public string? Link(string key)
    {
        string? v = GetLocalized("links", key);
        return v is not null && Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? v : null;
    }
}
