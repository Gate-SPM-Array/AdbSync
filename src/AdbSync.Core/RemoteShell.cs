namespace AdbSync.Core;

/// <summary>Costruzione dei comandi shell inviati al telefono.</summary>
public static class RemoteShell
{
    public const string ExitMarker = "__ADBSYNC_EXIT__:";
    public const string CdErrorMarker = "__ADBSYNC_CDERR__";
    public const string BatchMarker = "__ADBSYNC_BATCH__:";
    public const string BatchEndMarker = "__ADBSYNC_BEND__:";
    public const string FoundMarker = "__ADBSYNC_FOUND__:";
    public const string FoundLinesMarker = "__ADBSYNC_FOUNDLINES__:";
    public const string FindExitMarker = "__ADBSYNC_FINDEXIT__:";
    public const string XargsExitMarker = "__ADBSYNC_XARGSEXIT__:";
    public const string ErrMarker = "__ADBSYNC_ERR__:";
    public const string ErrEndMarker = "__ADBSYNC_ERREND__";
    public const string NoTmpMarker = "__ADBSYNC_NOTMP__";

    /// <summary>Massimo numero di percorsi per singola chiamata a stat.</summary>
    public const int BatchFiles = 500;
    /// <summary>Massimo numero di byte della riga di comando di ogni chiamata (molto sotto qualunque limite del kernel).</summary>
    public const int BatchBytes = 65536;

    public const string DefaultTmpDir = "/data/local/tmp";

    /// <summary>Quoting sicuro per sh: tutto tra apici singoli, gli apici interni diventano '\''.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private const string Types = "\\( -type f -o -type d -o -type l \\)";
    private static readonly string StatBatch =
        $"sh -c 'printf \"%s\\n\" \"{BatchMarker}$#\"; stat -c \"%F|%s|%Y|%y|%n\" \"$@\"; printf \"%s\\n\" \"{BatchEndMarker}$?\"' sh";

    /// <summary>
    /// Scansione dell'albero (file, cartelle e link) con dimensione e data al nanosecondo.
    /// Garanzie di completezza (ogni anomalia diventa un problema bloccante nel parser):
    ///  1. find scrive l'elenco completo (separato da NUL) in un file temporaneo; il suo codice di uscita viene riportato.
    ///  2. il numero di elementi trovati viene contato in modo INDIPENDENTE (tr, oppure un secondo conteggio).
    ///  3. xargs passa a stat blocchi limitati (<see cref="BatchFiles"/> percorsi, <see cref="BatchBytes"/> byte):
    ///     impossibile superare il limite degli argomenti ("Argument list too long", bug della v0.1.0).
    ///  4. ogni blocco ha un'intestazione (quanti percorsi) e una chiusura (esito di stat); il codice di xargs viene riportato.
    ///  5. gli errori vanno in un file separato (mai mescolati ai dati) chiuso da un marcatore di fine.
    ///  6. tutti i messaggi sono stampati con printf (echo di mksh interpreta le sequenze con '\').
    /// Senza una cartella temporanea scrivibile si usano tre passate separate (errori, conteggio, dati).
    /// </summary>
    public static string BuildScanCommand(string remoteRoot, string tmpDir = DefaultTmpDir)
    {
        string withTmp =
            "E=\"$T.err\"; L=\"$T.lst\"; " +
            $"find . {Types} -print0 >\"$L\" 2>>\"$E\"; F=$?; " +
            $"printf '%s\\n' \"{FindExitMarker}$F\"; " +
            "N=$(tr -cd '\\000' <\"$L\" 2>/dev/null | wc -c); N=$((${N:-0}+0)); " +
            $"if [ \"$N\" -eq 0 ]; then N=$(xargs -0 -n {BatchFiles} -s {BatchBytes} sh -c 'echo $#' sh <\"$L\" | {{ s=0; while read n; do s=$((s+n)); done; echo $s; }}); fi; " +
            $"printf '%s\\n' \"{FoundMarker}$N\"; " +
            $"xargs -0 -n {BatchFiles} -s {BatchBytes} {StatBatch} <\"$L\" 2>>\"$E\"; X=$?; " +
            $"printf '%s\\n' \"{XargsExitMarker}$X\"; " +
            $"printf '%s\\n' \"{ErrEndMarker}\" >>\"$E\"; " +
            $"while IFS= read -r l || [ -n \"$l\" ]; do printf '%s\\n' \"{ErrMarker}$l\"; done <\"$E\"; " +
            "rm -f \"$E\" \"$L\"";

        string withoutTmp =
            $"printf '%s\\n' {NoTmpMarker}; " +
            // passata 1: solo gli errori di find (lo stdout va scartato, lo stderr diventa la pipe)
            $"find . {Types} 2>&1 >/dev/null | while IFS= read -r l || [ -n \"$l\" ]; do printf '%s\\n' \"{ErrMarker}$l\"; done; " +
            $"printf '%s\\n' \"{ErrEndMarker}\"; " +
            // passata 2: conteggio (per righe: un nome con "a capo" darebbe un conteggio diverso → errore, mai silenzio)
            $"printf '%s\\n' \"{FoundLinesMarker}$(find . {Types} 2>/dev/null | wc -l)\"; " +
            // passata 3: dati
            $"find . {Types} -print0 2>/dev/null | xargs -0 -n {BatchFiles} -s {BatchBytes} {StatBatch} 2>/dev/null; X=$?; " +
            $"printf '%s\\n' \"{XargsExitMarker}$X\"";

        return
            $"cd {Quote(remoteRoot)} 2>/dev/null || {{ printf '%s\\n' {CdErrorMarker}; exit 1; }}; " +
            "T=; " +
            $"for d in {Quote(tmpDir)} \"$TMPDIR\"; do " +
            "[ -n \"$d\" ] && ( : > \"$d/.adbsync_$$.err\" ) 2>/dev/null && { T=\"$d/.adbsync_$$\"; break; }; done; " +
            "if [ -n \"$T\" ]; then " + withTmp + "; else " + withoutTmp + "; fi; " +
            $"printf '%s\\n' \"{ExitMarker}0\"";
    }

    /// <summary>
    /// Hash di un blocco di file con un solo comando. Per ogni file stampa "md5|./percorso" (md5 vuoto se illeggibile).
    /// quick = MD5 dei primi min(64 KB, n) byte seguiti dagli ultimi min(64 KB, n) byte (esatto per file fino a 128 KB).
    /// </summary>
    public static string BuildHashBatchCommand(string remoteRoot, IEnumerable<string> relativePaths, bool quick)
    {
        var sb = new System.Text.StringBuilder();
        // pipefail (mksh, bash): un errore di lettura di head/tail/md5sum annulla l'hash invece di produrre l'MD5 di dati parziali.
        sb.Append($"cd {Quote(remoteRoot)} 2>/dev/null || {{ printf '%s\n' {CdErrorMarker}; exit 1; }}; (set -o pipefail) 2>/dev/null && set -o pipefail; for f in");
        foreach (var p in relativePaths) sb.Append(' ').Append(Quote("./" + p));
        string hash = quick
            ? $"{{ head -c {SyncEngine.QuickHashWindow} \"$f\" && tail -c {SyncEngine.QuickHashWindow} \"$f\"; }} 2>/dev/null | md5sum 2>/dev/null"
            : "md5sum < \"$f\" 2>/dev/null";
        sb.Append("; do if [ -r \"$f\" ] && [ -f \"$f\" ]; then h=$(").Append(hash).Append(") || h=; else h=; fi; ");
        sb.Append("printf '%s|%s\\n' \"${h%% *}\" \"$f\"; done; ");
        sb.Append($"printf '%s\\n' \"{ExitMarker}0\"");
        return sb.ToString();
    }

    /// <summary>Interpreta l'output di <see cref="BuildHashBatchCommand"/>: percorso relativo → md5 (null se illeggibile).</summary>
    public static Dictionary<string, string?> ParseHashBatch(IEnumerable<string> lines, out bool complete)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        complete = false;
        foreach (var raw in lines)
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith(ExitMarker, StringComparison.Ordinal)) { complete = true; continue; }
            int bar = line.IndexOf('|');
            if (bar < 0) continue;
            string hash = line[..bar].Trim();
            string path = line[(bar + 1)..];
            if (!path.StartsWith("./", StringComparison.Ordinal)) continue;
            bool valid = hash.Length == 32 && hash.All(Uri.IsHexDigit);
            result[path[2..]] = valid ? hash.ToLowerInvariant() : null;
        }
        return result;
    }

    public static string BuildListDirsCommand(string remotePath) =>
        $"cd {Quote(remotePath)} 2>/dev/null && find . -mindepth 1 -maxdepth 1 -type d 2>/dev/null";

    public static string BuildStatCommand(string remotePath) =>
        $"stat -c '%s|%Y|%y' {Quote(remotePath)} 2>&1";

    /// <summary>
    /// MD5 di un file letto da stdin: l'output è "hash  -" anche per i nomi con "\" o a capo
    /// (con il nome come argomento md5sum li riscrive con escape e mette "\" davanti all'hash).
    /// </summary>
    public static string BuildMd5Command(string remotePath) =>
        $"md5sum < {Quote(remotePath)} 2>&1";

    /// <summary>Estrae la frazione di secondo (in unità da 100 ns) da "2024-03-01 10:00:00.123456789 +0100".</summary>
    public static int ParseFraction(string humanTime)
    {
        int dot = humanTime.IndexOf('.');
        if (dot < 0) return 0;
        int i = dot + 1, digits = 0, value = 0;
        while (i < humanTime.Length && char.IsAsciiDigit(humanTime[i]))
        {
            if (digits < 7) { value = value * 10 + (humanTime[i] - '0'); digits++; }
            i++;
        }
        while (digits < 7) { value *= 10; digits++; }
        return value;
    }

    public static bool TryParseStat(string output, out long size, out long mtime, out int fraction)
    {
        size = mtime = 0;
        fraction = 0;
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length >= 2 && long.TryParse(parts[0], out size) && long.TryParse(parts[1], out mtime))
            {
                if (parts.Length >= 3) fraction = ParseFraction(parts[2]);
                return true;
            }
        }
        return false;
    }

    public static string? ParseMd5(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length >= 32 && line[..32].All(Uri.IsHexDigit) && (line.Length == 32 || char.IsWhiteSpace(line[32])))
                return line[..32].ToLowerInvariant();
        }
        return null;
    }
}
