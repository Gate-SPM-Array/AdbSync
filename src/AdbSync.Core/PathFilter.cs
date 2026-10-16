using System.Text;
using System.Text.RegularExpressions;

namespace AdbSync.Core;

/// <summary>
/// Esclusioni in stile semplice:
///  - "nome/"          esclude qualsiasi cartella con quel nome (in qualunque punto)
///  - "*.tmp"          esclude i file il cui nome corrisponde
///  - "WhatsApp/Media/.Statuses/"  con '/' interno: percorso a partire dalla radice
/// '*' = qualsiasi testo dentro un nome, '**' = anche attraverso le cartelle, '?' = un carattere. Maiuscole ignorate.
/// </summary>
public sealed class PathFilter
{
    private readonly List<Rule> _rules = new();

    private sealed record Rule(Regex Regex, bool DirOnly, bool Anchored, string Pattern);

    public PathFilter(IEnumerable<string>? patterns)
    {
        if (patterns is null) return;
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0 || p.StartsWith('#')) continue;
            bool dirOnly = p.EndsWith('/');
            p = p.Trim('/');
            if (p.Length == 0) continue;
            bool anchored = p.Contains('/');
            // Cartella ancorata alla radice: deve essere un prefisso del percorso del file.
            string rx = anchored && dirOnly ? "^" + GlobToRegex(p) + "/" : "^" + GlobToRegex(p) + "$";
            _rules.Add(new Rule(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), dirOnly, anchored, raw.Trim()));
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    public bool IsExcluded(string relativePath) => Match(relativePath) is not null;

    /// <summary>Restituisce la regola (così come è stata scritta) che esclude il percorso, oppure null.</summary>
    public string? Match(string relativePath)
    {
        if (_rules.Count == 0) return null;
        var segments = relativePath.Split('/');
        string fileName = segments[^1];
        foreach (var r in _rules)
        {
            if (r.Anchored)
            {
                if (r.Regex.IsMatch(relativePath)) return r.Pattern;
            }
            else if (r.DirOnly)
            {
                for (int i = 0; i < segments.Length - 1; i++)
                    if (r.Regex.IsMatch(segments[i])) return r.Pattern;
            }
            else if (r.Regex.IsMatch(fileName)) return r.Pattern;
        }
        return null;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
