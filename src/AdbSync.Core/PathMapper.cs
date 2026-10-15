using System.Text;

namespace AdbSync.Core;

/// <summary>
/// Converte i nomi di file Android (che ammettono quasi tutti i caratteri) in nomi validi su Windows.
/// I caratteri vietati sono sostituiti con il corrispondente "a larghezza piena" Unicode, così il nome resta leggibile
/// e la conversione è stabile tra un'esecuzione e l'altra (indispensabile per la copia incrementale).
/// </summary>
public static class PathMapper
{
    private static readonly Dictionary<char, char> Replacements = new()
    {
        ['<'] = '＜', ['>'] = '＞', [':'] = '：', ['"'] = '＂',
        ['\\'] = '＼', ['|'] = '｜', ['?'] = '？', ['*'] = '＊',
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Restituisce il percorso relativo sicuro per Windows (separatore '/') e se è stato modificato.</summary>
    public static (string LocalRelative, bool Changed) ToLocalRelative(string remoteRelative)
    {
        // Caso comune (nessun carattere problematico): stessa istanza di stringa, nessuna allocazione.
        if (!NeedsMapping(remoteRelative)) return (remoteRelative, false);
        var segments = remoteRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool changed = false;
        for (int i = 0; i < segments.Length; i++)
        {
            string s = SanitizeSegment(segments[i]);
            if (!ReferenceEquals(s, segments[i]) && s != segments[i]) changed = true;
            segments[i] = s;
        }
        return (string.Join('/', segments), changed);
    }

    private static bool NeedsMapping(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path[^1] == '/' || path.Contains("//", StringComparison.Ordinal)) return true;
        int segStart = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/')
            {
                char c = path[i];
                if (c < 32 || Replacements.ContainsKey(c)) return true;
                continue;
            }
            // fine di un segmento: punti/spazi finali o nome riservato
            if (i == segStart) return true;
            char last = path[i - 1];
            if (last == '.' || last == ' ') return true;
            var seg = path.AsSpan(segStart, i - segStart);
            int dot = seg.IndexOf('.');
            var stem = dot >= 0 ? seg[..dot] : seg;
            if (stem.Length is 3 or 4 && Reserved.Contains(stem.ToString())) return true;
            segStart = i + 1;
        }
        return false;
    }

    internal static string SanitizeSegment(string name)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            char r = c;
            if (c < 32) r = (char)(0x2400 + c); // simboli di controllo visibili (␀..␟)
            else if (Replacements.TryGetValue(c, out var rep)) r = rep;
            if (r != c)
            {
                sb ??= new StringBuilder(name, 0, i, name.Length);
                sb.Append(r);
            }
            else sb?.Append(c);
        }
        string result = sb?.ToString() ?? name;

        // Windows rimuove punti e spazi finali: li sostituiamo per non perdere/confondere il file.
        if (result.Length > 0 && (result[^1] == '.' || result[^1] == ' '))
        {
            int end = result.Length;
            while (end > 0 && (result[end - 1] == '.' || result[end - 1] == ' ')) end--;
            var tail = new StringBuilder(result.Length);
            tail.Append(result, 0, end);
            for (int i = end; i < result.Length; i++) tail.Append(result[i] == '.' ? '．' : '␣'); // ． e ␣
            result = tail.ToString();
        }

        // Nomi riservati di Windows (anche con estensione: "CON.txt").
        string stem = result;
        int dot = result.IndexOf('.');
        if (dot >= 0) stem = result[..dot];
        if (Reserved.Contains(stem)) result = "_" + result;

        return result;
    }

    public static string ToLocalFullPath(string localRoot, string localRelative) =>
        Path.Combine(localRoot, localRelative.Replace('/', Path.DirectorySeparatorChar));

    public static string ToRemoteFullPath(string remoteRoot, string remoteRelative) =>
        remoteRoot.TrimEnd('/') + "/" + remoteRelative;
}
