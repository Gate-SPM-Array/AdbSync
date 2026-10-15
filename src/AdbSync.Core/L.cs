using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdbSync.Core;

/// <summary>Una lingua disponibile: l'italiano è incorporato, le altre sono file <c>lang\&lt;codice&gt;.json</c>.</summary>
public sealed record LanguageInfo(string Code, string Name, string? FilePath)
{
    public override string ToString() => Name;
}

/// <summary>
/// Traduzione dell'interfaccia con file esterni modificabili da chiunque (stile gettext).
/// Il testo italiano scritto nel codice è la chiave: <c>L.T("Confronta")</c>, <c>L.F("Copiati {0} file", n)</c>.
/// Un file di lingua associa a ogni testo italiano la sua traduzione; ciò che manca resta in italiano.
/// Formato di <c>lang\en.json</c>:
/// <code>
/// { "code": "en", "language": "English", "culture": "en-GB", "translators": "...", "app_version": "0.1.3",
///   "strings": { "Confronta": "Compare", "Copiati {0} file": "{0} files copied" } }
/// </code>
/// I segnaposto {0}, {1:0.0}… devono restare gli stessi: una traduzione che li altera viene ignorata (resta l'italiano).
/// </summary>
public static class L
{
    public const string DefaultCode = "it";
    public const string DefaultName = "Italiano";
    public const string ReferenceCode = "en"; // file completo usato come riferimento per chi traduce

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    /// <summary>Codice della lingua attiva ("it" se nessun file è caricato).</summary>
    public static string Code { get; private set; } = DefaultCode;
    public static string Name { get; private set; } = DefaultName;

    /// <summary>Cartella dei file di lingua: <c>lang</c> accanto all'eseguibile.</summary>
    public static string LangDirectory => Path.Combine(AppPaths.BaseDirectory, "lang");

    /// <summary>Traduce un testo fisso.</summary>
    public static string T(string italian) =>
        _map.Count != 0 && _map.TryGetValue(italian, out var t) ? t : italian;

    /// <summary>Traduce un testo con segnaposto {0}, {1}… e lo compone.</summary>
    public static string F(string italianFormat, params object?[] args)
    {
        string fmt = T(italianFormat);
        try { return string.Format(Format.Culture, fmt, args); }
        catch (FormatException) { return string.Format(Format.Culture, italianFormat, args); }
    }

    /// <summary>Lingue disponibili: italiano (incorporato) più ogni file valido in <see cref="LangDirectory"/>.</summary>
    public static IReadOnlyList<LanguageInfo> Available(string? directory = null)
    {
        var list = new List<LanguageInfo> { new(DefaultCode, DefaultName, null) };
        directory ??= LangDirectory;
        if (!Directory.Exists(directory)) return list;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var f = Read(file);
                string code = f.Code ?? Path.GetFileNameWithoutExtension(file);
                if (code.Equals(DefaultCode, StringComparison.OrdinalIgnoreCase) || list.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new LanguageInfo(code, string.IsNullOrWhiteSpace(f.Language) ? code : f.Language!, file));
            }
            catch { /* file non valido: non proposto */ }
        }
        return list;
    }

    /// <summary>
    /// Attiva una lingua. Restituisce i messaggi da mettere nel log (file mancante o non valido, traduzioni scartate,
    /// testi non tradotti). In caso di problemi resta l'italiano per ciò che non è utilizzabile.
    /// </summary>
    public static IReadOnlyList<string> Load(string? code, string? directory = null)
    {
        var (lang, file, map, msgs) = Prepare(code, directory);
        _map = map;
        Code = lang?.Code ?? DefaultCode;
        Name = lang?.Name ?? DefaultName;
        Format.SetCulture(lang is null ? null : file?.Culture ?? lang.Code, file?.DateFormat);
        return msgs;
    }

    /// <summary>Legge e valida una lingua senza attivarla (usato da <see cref="Load"/> e dai test).</summary>
    internal static (LanguageInfo? Lang, LangFile? File, Dictionary<string, string> Map, List<string> Messages) Prepare(string? code, string? directory = null)
    {
        var msgs = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(code) || code.Equals(DefaultCode, StringComparison.OrdinalIgnoreCase)) return (null, null, map, msgs);

        directory ??= LangDirectory;
        var lang = Available(directory).FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (lang?.FilePath is null)
        {
            msgs.Add($"Lingua \"{code}\" non trovata in {directory}: uso l'italiano.");
            return (null, null, map, msgs);
        }
        LangFile file;
        try { file = Read(lang.FilePath); }
        catch (Exception ex)
        {
            msgs.Add($"File di lingua non valido ({lang.FilePath}): {ex.Message}. Uso l'italiano.");
            return (null, null, map, msgs);
        }

        int rejected = 0;
        foreach (var (key, value) in file.Strings ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(value)) continue;            // non tradotto: resta l'italiano
            if (!SamePlaceholders(key, value))
            {
                rejected++;
                if (rejected <= 10) msgs.Add($"Traduzione ignorata (segnaposto {{0}}… diversi dall'originale): \"{Shorten(key)}\"");
                continue;
            }
            map[key] = value;
        }
        if (rejected > 10) msgs.Add($"… altre {rejected - 10} traduzioni ignorate per segnaposto diversi.");

        // copertura rispetto al file di riferimento (en.json), utile a chi traduce
        string reference = Path.Combine(directory, ReferenceCode + ".json");
        if (!lang.Code.Equals(ReferenceCode, StringComparison.OrdinalIgnoreCase) && File.Exists(reference))
        {
            try
            {
                var keys = Read(reference).Strings?.Keys ?? Enumerable.Empty<string>();
                int missing = keys.Count(k => !map.ContainsKey(k));
                if (missing > 0) msgs.Add($"{lang.Name}: {missing} testi non ancora tradotti (restano in italiano).");
            }
            catch { /* riferimento illeggibile: nessun conteggio */ }
        }
        return (lang, file, map, msgs);
    }

    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.CultureInvariant);

    /// <summary>Insieme dei segnaposto ({0}, {1:0.0}, {2,-9}…) di un testo, ordinato.</summary>
    public static IReadOnlyList<string> Placeholders(string s) =>
        Placeholder.Matches(s).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToList();

    public static bool SamePlaceholders(string original, string translation) =>
        Placeholders(original).SequenceEqual(Placeholders(translation));

    private static string Shorten(string s) => s.Length <= 60 ? s.ReplaceLineEndings(" ") : s[..57].ReplaceLineEndings(" ") + "...";

    internal sealed class LangFile
    {
        public string? Code { get; set; }
        public string? Language { get; set; }
        public string? Culture { get; set; }
        public string? DateFormat { get; set; }
        public string? Translators { get; set; }
        public string? AppVersion { get; set; }
        public Dictionary<string, string>? Strings { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static LangFile Read(string path) =>
        JsonSerializer.Deserialize<LangFile>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("file vuoto");
}
