namespace AdbSync.Core;

public sealed class RemoteScanResult
{
    public required IReadOnlyList<RemoteFile> Files { get; init; }
    public IReadOnlyList<RemoteDir> Directories { get; init; } = Array.Empty<RemoteDir>();

    /// <summary>Cartelle/file che Android non permette di leggere (tollerati, ma elencati e riportati nel riepilogo).</summary>
    public IReadOnlyList<string> PermissionDenied { get; init; } = Array.Empty<string>();

    /// <summary>File spariti tra l'elenco e la lettura dei dati (es. cancellati durante la scansione).</summary>
    public IReadOnlyList<string> Vanished { get; init; } = Array.Empty<string>();

    /// <summary>Link simbolici: non vengono seguiti né copiati (elencati nel log).</summary>
    public IReadOnlyList<string> SymbolicLinks { get; init; } = Array.Empty<string>();

    /// <summary>Problemi che rendono la scansione NON affidabile: se non è vuoto il confronto deve fermarsi.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>Tutte le righe non-dati ricevute, così come sono (per la modalità diagnostica).</summary>
    public IReadOnlyList<string> RawMessages { get; init; } = Array.Empty<string>();

    public long FoundEntries { get; init; }
    public long DeclaredEntries { get; init; }
    public long ReceivedEntries { get; init; }
    public bool UsedMergedOutput { get; init; }
    public bool RootNotFound { get; init; }
}

/// <summary>
/// Interpreta l'output del comando di scansione e ne verifica la completezza.
/// Principio: in caso di dubbio la scansione è INCOMPLETA. Un elenco parziale non deve mai
/// diventare un piano di copia (bug della v0.1.0: "Argument list too long" ignorato).
/// Verifiche: trovati (conteggio indipendente) = dichiarati dai blocchi = ricevuti + spariti;
/// ogni blocco aperto è stato chiuso; codici di uscita di find/xargs spiegati; elenco errori completo.
/// </summary>
public sealed class RemoteScanParser
{
    private readonly List<RemoteFile> _files = new();
    private readonly List<RemoteDir> _dirs = new();
    private readonly List<string> _links = new();
    private readonly List<string> _permission = new();
    private readonly List<string> _vanished = new();
    private readonly List<string> _vanishedDirs = new();
    private readonly List<string> _fatal = new();
    private readonly List<string> _unparsed = new();
    private readonly List<string> _raw = new();
    private long _declared, _received, _batches, _batchEnds;
    private long? _found, _foundLines;
    private int? _findExit, _xargsExit;
    private bool _exitSeen, _cdError, _noTmp, _errEnd, _rootDenied;

    public long EntriesReceived => _received;

    public void Feed(string line)
    {
        // adb può consegnare "\r\n": il \r finale non fa mai parte di un nome valido per Windows
        if (line.EndsWith('\r')) line = line[..^1];
        if (line.Length == 0) return;

        if (TryMarker(line, RemoteShell.BatchMarker, out long n)) { _declared += n; _batches++; return; }
        if (line.StartsWith(RemoteShell.BatchEndMarker, StringComparison.Ordinal)) { _batchEnds++; return; }
        if (line.StartsWith(RemoteShell.ErrMarker, StringComparison.Ordinal))
        {
            string msg = line[RemoteShell.ErrMarker.Length..];
            // il marcatore di fine viene scritto in coda al file degli errori e riletto con il loro prefisso
            if (msg == RemoteShell.ErrEndMarker) _errEnd = true;
            else ClassifyError(msg);
            return;
        }
        if (TryMarker(line, RemoteShell.FoundMarker, out n)) { _found = n; return; }
        if (TryMarker(line, RemoteShell.FoundLinesMarker, out n)) { _foundLines = n; return; }
        if (TryMarker(line, RemoteShell.FindExitMarker, out n)) { _findExit = (int)n; return; }
        if (TryMarker(line, RemoteShell.XargsExitMarker, out n)) { _xargsExit = (int)n; return; }
        if (line.StartsWith(RemoteShell.ExitMarker, StringComparison.Ordinal)) { _exitSeen = true; return; }
        if (line == RemoteShell.ErrEndMarker) { _errEnd = true; return; }
        if (line == RemoteShell.CdErrorMarker) { _cdError = true; return; }
        if (line == RemoteShell.NoTmpMarker) { _noTmp = true; Raw(line); return; }

        if (TryParseEntry(line)) return;
        AddUnparsed(line);
    }

    private static bool TryMarker(string line, string marker, out long value)
    {
        value = 0;
        return line.StartsWith(marker, StringComparison.Ordinal) && long.TryParse(line.AsSpan(marker.Length).Trim(), out value);
    }

    /// <summary>Formato: "tipo|dimensione|secondi|data leggibile|./percorso".</summary>
    private bool TryParseEntry(string line)
    {
        int p1 = line.IndexOf('|');
        if (p1 <= 0) return false;
        int p2 = line.IndexOf('|', p1 + 1);
        if (p2 < 0) return false;
        int p3 = line.IndexOf('|', p2 + 1);
        if (p3 < 0) return false;
        int p4 = line.IndexOf('|', p3 + 1);
        if (p4 < 0) return false;

        var type = line.AsSpan(0, p1);
        if (!long.TryParse(line.AsSpan(p1 + 1, p2 - p1 - 1), out long size)) return false;
        if (!long.TryParse(line.AsSpan(p2 + 1, p3 - p2 - 1), out long mtime)) return false;
        string human = line.Substring(p3 + 1, p4 - p3 - 1);
        string path = line[(p4 + 1)..];
        if (path != "." && !path.StartsWith("./", StringComparison.Ordinal)) return false;

        string rel = path == "." ? "" : path[2..];
        int frac = RemoteShell.ParseFraction(human);
        _received++;
        if (type.StartsWith("regular", StringComparison.Ordinal)) _files.Add(new RemoteFile(rel, size, mtime, frac));
        else if (type.SequenceEqual("directory")) _dirs.Add(new RemoteDir(rel, mtime, frac));
        else if (type.SequenceEqual("symbolic link")) _links.Add(rel);
        else Raw(L.F("(tipo ignorato: {0}) {1}", type.ToString(), rel));
        return true;
    }

    private void ClassifyError(string msg)
    {
        Raw(msg);
        bool fromFind = msg.StartsWith("find: ", StringComparison.Ordinal);
        bool fromStat = msg.StartsWith("stat: ", StringComparison.Ordinal);
        if ((fromFind || fromStat) && msg.EndsWith(": Permission denied", StringComparison.Ordinal))
        {
            string p = ExtractPath(msg);
            if (p == ".") _rootDenied = true;
            else _permission.Add(p);
        }
        else if (fromStat && msg.EndsWith(": No such file or directory", StringComparison.Ordinal))
            _vanished.Add(ExtractPath(msg));      // file elencato da find e poi sparito: incluso nel conteggio dichiarato
        else if (fromFind && msg.EndsWith(": No such file or directory", StringComparison.Ordinal))
            _vanishedDirs.Add(ExtractPath(msg));  // cartella sparita durante la visita: il suo contenuto non è stato contato
        else
            _fatal.Add(msg);
    }

    /// <summary>"find: ./data/x: Permission denied" → "data/x"; "find: .: Permission denied" → ".".</summary>
    private static string ExtractPath(string msg)
    {
        int start = msg.IndexOf(": ", StringComparison.Ordinal) + 2;
        int end = msg.LastIndexOf(": ", StringComparison.Ordinal);
        if (start < 2 || end <= start) return msg;
        string p = msg[start..end].Trim('\'', '"', '‘', '’');
        if (p == ".") return ".";
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    private void AddUnparsed(string line)
    {
        Raw(line);
        _unparsed.Add(line);
    }

    private void Raw(string line)
    {
        if (_raw.Count < 200_000) _raw.Add(line);
    }

    public RemoteScanResult Complete()
    {
        var problems = new List<string>();
        long found = _found ?? _foundLines ?? -1;
        if (!_cdError)
        {
            if (!_exitSeen)
                problems.Add(L.T("la scansione si è interrotta prima della fine (connessione persa o telefono che non risponde)"));
            if (_rootDenied)
                problems.Add(L.T("Android non permette di leggere la cartella scelta (permesso negato): scegline una accessibile, ad es. /sdcard/..."));
            if (_fatal.Count > 0)
                problems.Add(L.F("il telefono ha segnalato {0} errori durante la scansione, ad es.: \"{1}\"", _fatal.Count, Shorten(_fatal[0])));
            if (_unparsed.Count > 0)
                problems.Add(L.F("{0} righe non interpretabili (nomi con caratteri di \"a capo\"?), ad es.: \"{1}\"", _unparsed.Count, Shorten(_unparsed[0])));
            if (!_errEnd)
                problems.Add(L.T("l'elenco degli errori del telefono è incompleto (memoria del telefono piena?)"));
            if (found < 0)
                problems.Add(L.T("manca il conteggio indipendente degli elementi trovati"));
            if (_batches == 0)
                problems.Add(L.T("il telefono non ha restituito alcun blocco di dati"));
            if (_batches != _batchEnds)
                problems.Add(L.F("{0} blocchi di dati aperti ma mai chiusi (processo interrotto sul telefono)", _batches - _batchEnds));
            if (found >= 0 && found != _declared)
                problems.Add(L.F("conteggio non corrispondente: find ha trovato {0} elementi ma ne sono stati passati {1} alla lettura dei dati", found, _declared));
            if (_declared != _received + _vanished.Count)
                problems.Add(_vanished.Count > 0
                    ? L.F("conteggio non corrispondente: dichiarati {0} elementi, ricevuti {1} (+{2} spariti durante la scansione)", _declared, _received, _vanished.Count)
                    : L.F("conteggio non corrispondente: dichiarati {0} elementi, ricevuti {1}", _declared, _received));

            bool findErrorsExplained = _permission.Count > 0 || _vanishedDirs.Count > 0 || _rootDenied;
            if (_findExit is int fe && fe != 0 && (fe > 128 || !findErrorsExplained))
                problems.Add(fe > 128
                    ? L.F("find è terminato con codice {0} (processo interrotto)", fe)
                    : L.F("find è terminato con codice {0} senza un errore che lo spieghi", fe));
            if (_xargsExit is null)
                problems.Add(L.T("manca l'esito della lettura dei dati (xargs)"));
            else if (_xargsExit is int xe && xe != 0)
            {
                bool statErrorsExplained = _vanished.Count > 0 || _permission.Count > 0;
                if (xe != 123 || !statErrorsExplained)
                    problems.Add(xe > 128
                        ? L.F("la lettura dei dati (xargs) è terminata con codice {0} (processo interrotto)", xe)
                        : L.F("la lettura dei dati (xargs) è terminata con codice {0}", xe));
            }
        }

        return new RemoteScanResult
        {
            Files = _files,
            Directories = _dirs,
            SymbolicLinks = _links,
            PermissionDenied = _permission,
            Vanished = _vanished.Concat(_vanishedDirs).ToList(),
            Problems = problems,
            RawMessages = _raw,
            FoundEntries = found,
            DeclaredEntries = _declared,
            ReceivedEntries = _received,
            UsedMergedOutput = _noTmp,
            RootNotFound = _cdError,
        };
    }

    private static string Shorten(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
