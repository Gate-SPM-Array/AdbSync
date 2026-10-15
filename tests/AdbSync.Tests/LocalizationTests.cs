using System.Text;
using System.Text.Json;
using AdbSync.Core;
using Xunit;

namespace AdbSync.Tests;

/// <summary>
/// Coerenza tra i testi del codice e i file di lingua. Ogni testo mostrato all'utente passa da L.T("…") o L.F("…", …)
/// con un letterale (anche concatenato con +): qui si estraggono tutti e si verifica che lang/en.json li traduca tutti,
/// con gli stessi segnaposto, e che non contenga traduzioni orfane.
/// </summary>
public class LocalizationTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AdbSync.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("radice del repository non trovata");
    }

    private static string LangDir => Path.Combine(RepoRoot(), "lang");

    /// <summary>Estrae le chiavi (testo italiano) di tutte le chiamate L.T / L.F nei sorgenti.</summary>
    internal static (Dictionary<string, string> Keys, List<string> Problems) ExtractKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);   // chiave → primo file:riga
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            string src = File.ReadAllText(file);
            string name = Path.GetRelativePath(RepoRoot(), file);
            int idx = 0;
            while ((idx = NextCall(src, idx)) >= 0)
            {
                int line = src.Take(idx).Count(c => c == '\n') + 1;
                int p = idx + 4; // dopo "L.T(" / "L.F("
                var sb = new StringBuilder();
                bool ok = true;
                while (true)
                {
                    p = SkipWs(src, p);
                    if (!TryReadLiteral(src, ref p, sb, out string? err))
                    {
                        problems.Add($"{name}:{line}: {err}");
                        ok = false;
                        break;
                    }
                    p = SkipWs(src, p);
                    if (p < src.Length && src[p] == '+') { p++; continue; }
                    break;
                }
                if (ok)
                {
                    string key = sb.ToString();
                    if (key.Length == 0) problems.Add($"{name}:{line}: testo vuoto");
                    else keys.TryAdd(key, $"{name}:{line}");
                }
                idx = p;
            }
        }
        return (keys, problems);
    }

    private static int NextCall(string s, int from)
    {
        while (true)
        {
            int a = s.IndexOf("L.T(", from, StringComparison.Ordinal);
            int b = s.IndexOf("L.F(", from, StringComparison.Ordinal);
            int i = a < 0 ? b : b < 0 ? a : Math.Min(a, b);
            if (i < 0) return -1;
            // deve essere l'identificatore L, non la fine di un altro nome (es. "XL.T(")
            if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) { from = i + 4; continue; }
            // ignora la definizione e i commenti di documentazione
            int lineStart = s.LastIndexOf('\n', i) + 1;
            string prefix = s[lineStart..i].TrimStart();
            if (prefix.StartsWith("//", StringComparison.Ordinal) || prefix.StartsWith("///", StringComparison.Ordinal) || prefix.StartsWith("*", StringComparison.Ordinal))
            { from = i + 4; continue; }
            return i;
        }
    }

    private static int SkipWs(string s, int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        return p;
    }

    private static bool TryReadLiteral(string s, ref int p, StringBuilder sb, out string? error)
    {
        error = null;
        if (p >= s.Length) { error = "fine file"; return false; }
        if (s[p] == '$') { error = "stringa interpolata ($\"…\") in L.T/L.F: usare L.F(\"… {0} …\", valore)"; return false; }
        if (s[p] == '@') { error = "stringa verbatim (@\"…\") in L.T/L.F: usare una stringa normale"; return false; }
        if (s[p] != '"') { error = "L.T/L.F senza letterale: il testo deve essere scritto nel codice (non una variabile)"; return false; }

        int q = 0;
        while (p + q < s.Length && s[p + q] == '"') q++;
        if (q >= 3)
        {
            // raw string literal """…"""
            int start = p + q;
            int end = s.IndexOf(new string('"', q), start, StringComparison.Ordinal);
            if (end < 0) { error = "raw string non chiusa"; return false; }
            sb.Append(Dedent(s[start..end]));
            p = end + q;
            return true;
        }
        if (q == 2) { p += 2; return true; } // ""
        p++;
        while (p < s.Length && s[p] != '"')
        {
            if (s[p] == '\\')
            {
                char c = s[p + 1];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(p + 2, 4), 16));
                        p += 4;
                        break;
                    default: error = $"sequenza di escape non gestita \\{c}"; return false;
                }
                p += 2;
                continue;
            }
            if (s[p] == '\n') { error = "stringa non chiusa"; return false; }
            sb.Append(s[p]);
            p++;
        }
        p++; // virgoletta finale
        return true;
    }

    /// <summary>Regole C# dei raw string multilinea: si toglie il rientro della riga con le virgolette di chiusura.</summary>
    private static string Dedent(string raw)
    {
        if (!raw.Contains('\n')) return raw;
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        string indent = lines[^1]; // solo spazi prima delle virgolette di chiusura
        var body = lines[1..^1].Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l.TrimStart());
        return string.Join("\n", body);
    }

    private static Dictionary<string, string> EnglishStrings()
    {
        var file = L.Read(Path.Combine(LangDir, "en.json"));
        return file.Strings ?? new Dictionary<string, string>();
    }

    [Fact]
    public void AllLocalizedCallsUseLiterals()
    {
        var (_, problems) = ExtractKeys();
        Assert.True(problems.Count == 0, "Chiamate L.T/L.F non verificabili:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EnglishFileTranslatesEveryText()
    {
        var (keys, _) = ExtractKeys();
        var en = EnglishStrings();
        var missing = keys.Where(k => !en.TryGetValue(k.Key, out var v) || string.IsNullOrWhiteSpace(v))
                          .Select(k => $"{k.Value}: \"{k.Key}\"").ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} testi senza traduzione inglese in lang/en.json:\n" + string.Join("\n", missing.Take(200)));
    }

    [Fact]
    public void EnglishPlaceholdersMatchOriginals()
    {
        var bad = EnglishStrings().Where(kv => !string.IsNullOrEmpty(kv.Value) && !L.SamePlaceholders(kv.Key, kv.Value))
                                  .Select(kv => $"\"{kv.Key}\" → \"{kv.Value}\"").ToList();
        Assert.True(bad.Count == 0, "Segnaposto diversi:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EnglishFileHasNoOrphanTexts()
    {
        var (keys, _) = ExtractKeys();
        var orphans = EnglishStrings().Keys.Where(k => !keys.ContainsKey(k)).ToList();
        Assert.True(orphans.Count == 0, $"{orphans.Count} traduzioni non più usate nel codice:\n" + string.Join("\n", orphans.Take(100)));
    }

    [Fact]
    public void EnglishFileIsValidJsonWithMetadata()
    {
        var file = L.Read(Path.Combine(LangDir, "en.json"));
        Assert.Equal("en", file.Code);
        Assert.Equal("English", file.Language);
        Assert.False(string.IsNullOrWhiteSpace(file.Culture));
    }

    [Fact]
    public void LoaderRejectsBrokenPlaceholdersAndKeepsItalianForMissingTexts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "adbsync_lang_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "en.json"), JsonSerializer.Serialize(new
            {
                code = "en", language = "English", culture = "en-GB",
                strings = new Dictionary<string, string> { ["Confronta"] = "Compare", ["Copiati {0} file"] = "{0} files copied", ["Uno"] = "One", ["Due"] = "" },
            }));
            File.WriteAllText(Path.Combine(dir, "de.json"), JsonSerializer.Serialize(new
            {
                code = "de", language = "Deutsch",
                strings = new Dictionary<string, string> { ["Confronta"] = "Vergleichen", ["Copiati {0} file"] = "Dateien kopiert" },
            }));
            File.WriteAllText(Path.Combine(dir, "rotto.json"), "{ non è json");

            var langs = L.Available(dir);
            Assert.Equal(new[] { "it", "de", "en" }, langs.Select(l => l.Code).ToArray());

            var (lang, _, map, msgs) = L.Prepare("de", dir);
            Assert.Equal("Deutsch", lang!.Name);
            Assert.Equal("Vergleichen", map["Confronta"]);
            Assert.False(map.ContainsKey("Copiati {0} file"));             // segnaposto perso: scartata
            Assert.Contains(msgs, m => m.Contains("segnaposto"));
            Assert.Contains(msgs, m => m.Contains("non ancora tradotti"));  // copertura rispetto a en.json

            var (_, _, enMap, enMsgs) = L.Prepare("en", dir);
            Assert.Equal(3, enMap.Count);                                   // "Due" vuoto: resta l'italiano
            Assert.Empty(enMsgs);

            var (none, _, emptyMap, noneMsgs) = L.Prepare("xx", dir);
            Assert.Null(none);
            Assert.Empty(emptyMap);
            Assert.Contains(noneMsgs, m => m.Contains("non trovata"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RepositoryEnglishFileLoadsWithoutWarnings()
    {
        var (lang, _, map, msgs) = L.Prepare("en", LangDir);
        Assert.NotNull(lang);
        Assert.Empty(msgs);
        Assert.Equal(EnglishStrings().Count(kv => kv.Value.Length > 0), map.Count);
    }

    [Theory]
    [InlineData("Copiati {0} di {1}", "{0} of {1} copied", true)]
    [InlineData("{0,-9} {1:0.0}%", "{1:0.0}% {0,-9}", true)]
    [InlineData("Copiati {0}", "Copied", false)]
    [InlineData("Valore {0:0.0}", "Value {0}", false)]
    [InlineData("Graffe {{letterali}} {0}", "Braces {{literal}} {0}", true)]
    public void PlaceholderComparison(string it, string en, bool same) => Assert.Equal(same, L.SamePlaceholders(it, en));
}
