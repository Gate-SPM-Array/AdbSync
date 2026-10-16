using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace AdbSync.Core;

/// <summary>Errore temporaneo: il file verrà ritentato.</summary>
public class TransientCopyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Errore definitivo per quel file: inutile ritentare.</summary>
public class PermanentCopyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Errore sul lato PC (scrittura, permessi, spazio).</summary>
public class LocalIoException(Exception inner) : Exception(inner.Message, inner);

public class StallException(string message) : TransientCopyException(message);
/// <summary>Scrittura sul disco del PC ferma oltre il limite: non è colpa del telefono.</summary>
public sealed class DiskStallException(string message) : StallException(message);
/// <summary>Copia ricevuta ma non valida (MD5 diverso): il parziale va buttato, non ripreso.</summary>
public sealed class CorruptCopyException(string message) : TransientCopyException(message);

/// <summary>Esegue confronto e copia con verifica, ritentativi, rilevamento stalli e attesa riconnessione.</summary>
public sealed class SyncEngine
{
    private readonly IRemoteDevice _device;
    private readonly SyncOptions _o;
    private readonly SyncLogger _log;
    private readonly object _waitLock = new();
    private Task? _deviceWait;
    private Task? _healthCheck;
    private int _consecutiveStalls, _failedHealthRounds, _protocolLogged;
    private Task? _destWait;
    private CancellationTokenSource? _abortCts;
    private volatile string? _abortReason;
    private string _localRoot = "";

    public SyncProgress Progress { get; } = new();

    // Parametri temporali sovrascrivibili nei test.
    internal Func<int, TimeSpan> BackoffDelay { get; set; } =
        attempt => TimeSpan.FromSeconds(new[] { 2, 5, 10, 20, 30 }[Math.Clamp(attempt - 1, 0, 4)]);
    internal TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Soglia del diario delle pause: interruzioni del flusso di dati più lunghe vengono registrate nel log.</summary>
    internal long PauseLogThresholdMs { get; set; } = 500;
    internal TimeSpan? StallTimeoutOverride { get; set; }
    internal TimeSpan ForceCloseDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan? WaitTimeoutOverride { get; set; }

    private TimeSpan StallTimeout => StallTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(1, _o.StallTimeoutSeconds));
    internal TimeSpan? DiskStallTimeoutOverride { get; set; }
    /// <summary>Limite per una singola scrittura sul disco del PC (minimo 30 s): separato dallo stallo del telefono.</summary>
    private TimeSpan DiskStallTimeout => DiskStallTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(SyncOptions.MinDiskStallSeconds, _o.DiskStallTimeoutSeconds));
    /// <summary>Oltre questo tempo di scrittura ferma lo stato mostra "disco di destinazione lento".</summary>
    internal TimeSpan SlowDiskNotice { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Margine tolto dal file parziale prima di riprendere (l'ultima parte potrebbe non essere completa).</summary>
    internal const long ResumeSafetyMargin = 1 << 20;
    /// <summary>Solo per i test: avvolge il file di destinazione (es. per simulare un disco lento).</summary>
    internal Func<Stream, Stream>? DestinationWrapper { get; set; }
    private TimeSpan WaitTimeout => WaitTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(10, _o.DeviceWaitTimeoutSeconds));

    public SyncEngine(IRemoteDevice device, SyncOptions options, SyncLogger log)
    {
        _device = device;
        _o = options;
        _log = log;
    }

    // ------------------------------------------------------------------ CONFRONTO

    /// <summary>Scansione + piano (compatibilità): equivale a <see cref="ScanAsync"/> seguito da <see cref="PlanFromSession"/>.</summary>
    public static async Task<SyncPlan> CompareAsync(
        IRemoteDevice device, string remoteRoot, string localRoot, SyncOptions options,
        SyncLogger log, Action<string>? status, CancellationToken ct)
    {
        var session = await ScanAsync(device, remoteRoot, localRoot, options, log, status, ct);
        return PlanFromSession(session, options, log, writeDiagnostics: true);
    }

    /// <summary>Ricalcola il piano dall'elenco già acquisito (nessuna scansione).</summary>
    public static SyncPlan PlanFromSession(ComparisonSession session, SyncOptions options, SyncLogger log, bool writeDiagnostics)
    {
        var plan = session.BuildPlan(options);
        if (writeDiagnostics && options.Diagnostic)
            Diagnostics.WriteCompare(log.LogDirectory, session.RemoteRoot, session.LocalRoot, session.Remote, session.Local, plan, log);
        log.Info(DescribePlan(plan));
        foreach (var g in plan.Items.Where(i => i.Category == ItemCategory.Excluded).GroupBy(i => i.ExcludedBy).OrderByDescending(g => g.Count()))
            log.Info(L.F("Esclusi dalla regola \"{0}\": {1} file (visibili con il filtro Esclusi; si modificano con Esclusioni…).", g.Key, Format.Number(g.Count())));
        int sanitized = plan.Items.Count(i => i.NameWasSanitized && i.Category != ItemCategory.Excluded);
        if (sanitized > 0) log.Warn(L.F("{0} nomi contengono caratteri non ammessi da Windows: verranno salvati con caratteri sostitutivi equivalenti.", sanitized));
        foreach (var c in plan.Items.Where(i => i.Category is ItemCategory.Collision or ItemCategory.Blocked).Take(20))
            log.Warn(L.F("File NON copiabile: {0} — {1}", c.RelativePath, c.Error));
        if (plan.NotCopiable > 0)
            log.Warn(L.F("Attenzione: {0} elementi del telefono non potranno essere copiati (permesso negato, collisioni, bloccati).", plan.NotCopiable));
        return plan;
    }

    /// <summary>Acquisisce l'elenco di telefono e PC (verificato) e lo restituisce come sessione riutilizzabile.</summary>
    public static async Task<ComparisonSession> ScanAsync(
        IRemoteDevice device, string remoteRoot, string localRoot, SyncOptions options,
        SyncLogger log, Action<string>? status, CancellationToken ct)
    {
        remoteRoot = NormalizeRemoteRoot(remoteRoot);
        EnsureLocalDriveAvailable(localRoot);
        var sw = Stopwatch.StartNew();
        log.Info(L.F("Confronto: telefono {0}:{1}  →  PC {2}", device.DisplayName, remoteRoot, localRoot));

        int remoteCount = 0, localCount = 0;
        void Report() => status?.Invoke(L.F("Scansione in corso — telefono: {0} elementi · PC: {1} file", Format.Number(remoteCount), Format.Number(localCount)));

        var remoteTask = device.ScanAsync(remoteRoot, n => { remoteCount = n; Report(); }, ct);
        var localTask = Task.Run(() => LocalScanner.Scan(localRoot, n => { localCount = n; Report(); }, ct), ct);
        await Task.WhenAll(remoteTask, localTask);

        var remote = remoteTask.Result;
        var local = localTask.Result;
        if (remote.RootNotFound)
            throw new DirectoryNotFoundException(L.F("La cartella \"{0}\" non esiste sul telefono o non è accessibile.", remoteRoot));

        log.Info(L.F("Scansione completata in {0}: telefono {1} file e " +
                     "{2} cartelle (verifica: trovati {3}, " +
                     "dichiarati {4}, ricevuti {5}); " +
                     "PC {6} file e {7} cartelle.",
                     Format.Duration(sw.Elapsed), Format.Number(remote.Files.Count),
                     Format.Number(Math.Max(0, remote.Directories.Count - 1)), Format.Number(remote.FoundEntries),
                     Format.Number(remote.DeclaredEntries), Format.Number(remote.ReceivedEntries),
                     Format.Number(local.Files.Count), Format.Number(Math.Max(0, local.Directories.Count - 1))));
        if (local.ReparseDirectories.Count > 0)
            log.Warn(L.F("Sul PC {0} cartelle sono collegamenti (junction/symlink): non vengono attraversate né modificate.", local.ReparseDirectories.Count));
        if (remote.UsedMergedOutput)
            log.Warn(L.T("La cartella temporanea del telefono (/data/local/tmp) non è scrivibile: usata la lettura in tre passate (più lenta)."));
        if (remote.SymbolicLinks.Count > 0)
        {
            log.Warn(L.F("{0} link simbolici sul telefono non vengono seguiti né copiati.", remote.SymbolicLinks.Count));
            foreach (var l in remote.SymbolicLinks.Take(10)) log.Warn(L.F("  link: {0}", l));
        }
        if (remote.PermissionDenied.Count > 0)
        {
            log.Warn(L.F("Android non permette di leggere {0} elementi (normale per Android/data e Android/obb): non verranno copiati.", remote.PermissionDenied.Count));
            foreach (var p in remote.PermissionDenied.Take(20)) log.Warn(L.F("  permesso negato: {0}", p));
            if (remote.PermissionDenied.Count > 20) log.Warn(L.F("  ... e altri {0} (elenco completo con la modalità diagnostica).", remote.PermissionDenied.Count - 20));
        }
        foreach (var v in remote.Vanished.Take(20)) log.Warn(L.F("File sparito dal telefono durante la scansione: {0}", v));

        if (remote.Problems.Count > 0)
        {
            if (options.Diagnostic) Diagnostics.WriteCompare(log.LogDirectory, remoteRoot, localRoot, remote, local, null, log);
            foreach (var p in remote.Problems) log.Error(L.F("Scansione del telefono INCOMPLETA: {0}", p));
            throw new IncompleteScanException(L.F(
                "La scansione del telefono è INCOMPLETA, quindi il confronto è stato fermato per non darti un risultato sbagliato.\n\n" +
                "{0}\n\nRiprova; se si ripete, attiva la modalità diagnostica e invia la cartella dei log.",
                string.Join("\n", remote.Problems.Select(p => "• " + p))));
        }

        return new ComparisonSession
        {
            DeviceSerial = device.Serial,
            RemoteRoot = remoteRoot,
            LocalRoot = localRoot,
            Remote = remote,
            Local = local,
        };
    }

    private static void EnsureLocalDriveAvailable(string localRoot)
    {
        string? drive = Path.GetPathRoot(Path.GetFullPath(localRoot));
        if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
            throw new DestinationUnavailableException(L.F("L'unità {0} non è disponibile: collega il disco di destinazione e riprova.", drive));
    }

    public static string DescribePlan(SyncPlan p) =>
        L.F("Risultato confronto: {0} nuovi, {1} modificati, " +
            "{2} conflitti, {3} solo data diversa, " +
            "{4} identici, {5} solo sul PC, " +
            "{6} collisioni, esclusi dalle regole {7} del telefono e {8} solo sul PC. " +
            "Elementi: telefono {9} file + {10} cartelle, " +
            "PC {11} file + {12} cartelle. Da fare: {13}.",
            p.Count(ItemCategory.New), p.Count(ItemCategory.Changed),
            p.Count(ItemCategory.Conflict), p.Count(ItemCategory.DateOnly),
            p.Count(ItemCategory.Identical), p.Count(ItemCategory.OnlyOnPc),
            p.Count(ItemCategory.Collision), p.ExcludedCount, p.ExcludedLocalCount,
            Format.Number(p.RemoteFileCount), Format.Number(p.RemoteDirCount),
            Format.Number(p.LocalFileCount), Format.Number(p.LocalDirCount), p.WorkSummary);

    public static string NormalizeRemoteRoot(string root)
    {
        root = root.Trim().Replace('\\', '/');
        if (!root.StartsWith('/')) root = "/" + root;
        while (root.Length > 1 && root.EndsWith('/')) root = root[..^1];
        return root;
    }

    // ------------------------------------------------------------------ SINCRONIZZAZIONE

    public async Task<SyncResult> RunAsync(SyncPlan plan, CancellationToken userCt)
    {
        var sw = Stopwatch.StartNew();
        _localRoot = plan.LocalRoot;
        var items = plan.ToCopy.ToList();
        foreach (var i in items) { i.Status = ItemStatus.Pending; i.Error = null; }
        Progress.TotalFiles = items.Count;
        Progress.TotalBytes = items.Sum(i => i.RemoteSize);
        Progress.StartedAt = DateTime.UtcNow;
        Progress.Phase = L.T("Preparazione");
        _abortCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = _abortCts.Token;
        var metaErrors = new List<string>();
        int dirsCreated = 0, timesFixed = 0;

        if (!plan.HasWork)
        {
            _log.Info(L.T("Niente da fare: la cartella sul PC è già allineata."));
            return Finish(plan, items, sw, userCt, null, 0, 0, metaErrors);
        }

        foreach (var t in plan.StaleTempFiles)
        {
            try { File.Delete(t); _log.Info(L.F("Rimosso file temporaneo rimasto da un'esecuzione interrotta: {0}", t)); }
            catch (Exception ex) { _log.Warn(L.F("Impossibile rimuovere il temporaneo {0}: {1}", t, ex.Message)); }
        }

        try
        {
            EnsureLocalDriveAvailable(plan.LocalRoot);
            Directory.CreateDirectory(plan.LocalRoot);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(plan.LocalRoot))!);
            long needed = Progress.TotalBytes;
            if (needed > 0 && drive.IsReady && drive.AvailableFreeSpace < needed)
                return Finish(plan, items, sw, userCt, L.F("spazio insufficiente su {0}: servono {1}, disponibili {2}.", drive.Name, Format.Bytes(needed), Format.Bytes(drive.AvailableFreeSpace)), 0, 0, metaErrors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DestinationUnavailableException)
        {
            return Finish(plan, items, sw, userCt, L.F("cartella di destinazione non utilizzabile: {0}", ex.Message), 0, 0, metaErrors);
        }

        // 1) cartelle mancanti (anche quelle vuote sul telefono)
        var toCreate = plan.Directories.Where(d => d.NeedsCreate && !d.IsRoot).OrderBy(d => d.Depth).ToList();
        if (toCreate.Count > 0)
        {
            Progress.Phase = L.T("Creazione cartelle");
            foreach (var d in toCreate)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    Directory.CreateDirectory(PathMapper.ToLocalFullPath(plan.LocalRoot, d.LocalRelativePath));
                    d.Created = true;
                    dirsCreated++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    d.Error = ex.Message;
                    metaErrors.Add(L.F("cartella non creata: {0} — {1}", d.LocalRelativePath, ex.Message));
                    _log.Error(L.F("Cartella non creata: {0} — {1}", d.LocalRelativePath, ex.Message));
                }
            }
            if (dirsCreated > 0) _log.Info(L.F("Create {0} cartelle.", dirsCreated));
        }

        // 2) copia dei file
        if (items.Count > 0 && !ct.IsCancellationRequested)
        {
            int parallel = Math.Min(_o.ClampedParallel, items.Count);
            _log.Info(L.F("Inizio copia: {0} file, {1}, {2} trasferimenti paralleli, " +
                          "timeout stallo {3:0} s, tentativi {4}{5}.",
                          Format.Number(items.Count), Format.Bytes(Progress.TotalBytes), parallel,
                          StallTimeout.TotalSeconds, 1 + _o.MaxRetries, _o.VerifyHash ? L.T(", verifica MD5") : ""));
            Progress.Phase = L.T("Copia");
            var queue = new ConcurrentQueue<SyncItem>(items);
            var workers = Enumerable.Range(1, parallel)
                .Select(slot => Task.Run(() => WorkerAsync(slot, queue, plan, ct)))
                .ToArray();
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Abort(L.F("errore imprevisto: {0}", ex.Message));
                _log.Error(ex.ToString());
            }
        }

        // 3) date: file identici, file "stessa dimensione" verificati, poi tutte le cartelle (dalle più profonde)
        int verifiedAligned = 0;
        if (!userCt.IsCancellationRequested && DestinationAvailable())
        {
            Progress.Phase = L.T("Allineamento date");
            timesFixed = AlignFileTimes(plan, metaErrors);
            if (plan.CountToVerify > 0 && _abortReason is null)
            {
                try
                {
                    verifiedAligned = await VerifyAndAlignAsync(plan, metaErrors, ct);
                    timesFixed += verifiedAligned;
                }
                catch (OperationCanceledException) { }
                catch (DeviceUnavailableException dex) { Abort(dex.Message); }
                catch (Exception ex)
                {
                    Abort(L.F("errore imprevisto durante la verifica: {0}", ex.Message));
                    _log.Error(ex.ToString());
                }
            }
            if (!userCt.IsCancellationRequested && DestinationAvailable())
            {
                Progress.Phase = L.T("Allineamento date cartelle");
                timesFixed += AlignDirTimes(plan, metaErrors);
            }
        }
        else if (!userCt.IsCancellationRequested)
        {
            _log.Warn(L.T("Date non allineate: la destinazione non è disponibile."));
        }

        return Finish(plan, items, sw, userCt, _abortReason, dirsCreated, timesFixed, metaErrors, verifiedAligned);
    }

    /// <summary>
    /// File con stessa dimensione e data diversa: verifica del contenuto a blocchi (hash calcolati sul telefono con
    /// un comando per blocco) e, se coincide, allineamento delle date. Se il contenuto è diverso il file non viene toccato.
    /// </summary>
    private async Task<int> VerifyAndAlignAsync(SyncPlan plan, List<string> errors, CancellationToken ct)
    {
        bool quick = _o.AlignSameSizeDates == DateAlignMode.Quick;
        var todo = plan.Items.Where(i => i.Action == ItemAction.VerifyAndFixTimes && i.Remote is not null && i.Status != ItemStatus.Done).ToList();
        string kind = quick ? L.T("verifica rapida (testa e coda)") : L.T("verifica MD5 completa");
        _log.Info(L.F("Verifica e allineamento date: {0} file con stessa dimensione, {1}.", Format.Number(todo.Count), kind));
        int aligned = 0, differs = 0, done = 0;
        foreach (var batch in HashBatches(todo, quick))
        {
            ct.ThrowIfCancellationRequested();
            Progress.Phase = L.F("Verifica e allineamento date: {0} di {1}", Format.Number(done), Format.Number(todo.Count));
            IReadOnlyDictionary<string, string?> remoteHashes;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    remoteHashes = await _device.HashBatchAsync(plan.RemoteRoot, batch.Select(i => i.RelativePath).ToList(), quick, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (DeviceUnavailableException) { throw; }
                catch (Exception ex) when (attempt < 3)
                {
                    _log.Warn(L.F("Calcolo hash sul telefono non riuscito ({0}): riprovo.", ex.Message));
                    await Task.Delay(BackoffDelay(attempt), ct);
                    await EnsureDeviceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Blocco non verificabile: i suoi file restano com'erano (date non toccate) e sono segnalati; si prosegue.
                    _log.Error(L.F("Calcolo hash sul telefono non riuscito dopo 3 tentativi ({0}): {1} file non verificati.", ex.Message, batch.Count));
                    remoteHashes = new Dictionary<string, string?>();
                    break;
                }
            }

            // hash locali in parallelo (il disco del PC è il limite, non la CPU)
            var localHashes = new ConcurrentDictionary<SyncItem, string?>();
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
            {
                string path = PathMapper.ToLocalFullPath(plan.LocalRoot, i.LocalRelativePath);
                localHashes[i] = await LocalHash(path, quick, token);
            });

            foreach (var i in batch)
            {
                done++;
                string path = PathMapper.ToLocalFullPath(plan.LocalRoot, i.LocalRelativePath);
                remoteHashes.TryGetValue(i.RelativePath, out var rh);
                localHashes.TryGetValue(i, out var lh);
                // MD5 di "niente" per un file non vuoto = lettura fallita (shell senza pipefail): non è un confronto valido.
                if (rh == EmptyMd5 && (quick ? Math.Min(i.RemoteSize, 2L * QuickHashWindow) : i.RemoteSize) > 0) rh = null;
                if (string.IsNullOrEmpty(rh) || string.IsNullOrEmpty(lh))
                {
                    i.Status = ItemStatus.Failed;
                    i.Error = L.F("verifica non eseguita: {0}", string.IsNullOrEmpty(rh) ? L.T("file non leggibile sul telefono") : L.T("file non leggibile sul PC"));
                    errors.Add($"{i.LocalRelativePath} — {i.Error}");
                    continue;
                }
                if (!string.Equals(rh, lh, StringComparison.OrdinalIgnoreCase))
                {
                    i.Status = ItemStatus.ContentDiffers;
                    i.Error = L.F("contenuto DIVERSO dal telefono ({0}): date non modificate", kind);
                    differs++;
                    _log.Warn(L.F("Contenuto diverso, date non toccate: {0}", i.LocalRelativePath));
                    continue;
                }
                try
                {
                    if (LocalChangedSincePlan(i, path) is { } changed)
                    {
                        i.Status = ItemStatus.Failed;
                        i.Error = L.F("date non allineate: {0}", changed);
                        errors.Add($"{i.LocalRelativePath} — {i.Error}");
                        continue;
                    }
                    SetFileTimes(path, i.Remote!.MtimeUtc, i.Remote.MtimeUnix);
                    i.Status = ItemStatus.Done;
                    aligned++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    i.Status = ItemStatus.Failed;
                    i.Error = L.F("date non impostate: {0}", ex.Message);
                    errors.Add($"{i.LocalRelativePath} — {i.Error}");
                }
            }
        }
        _log.Info(L.F("Verifica completata: {0} file identici con date allineate, {1} con contenuto diverso (non toccati).", Format.Number(aligned), Format.Number(differs)));
        return aligned;
    }

    /// <summary>Blocchi da al massimo 200 file e ~16 KB di percorsi: un solo comando sul telefono per blocco.</summary>
    private static IEnumerable<List<SyncItem>> HashBatches(List<SyncItem> items, bool quick)
    {
        // MD5 completo: anche al massimo ~2 GB di dati per blocco, così il blocco sta comodamente nel timeout del comando.
        const long MaxDataPerBatch = 2L << 30;
        var batch = new List<SyncItem>();
        int bytes = 0;
        long data = 0;
        foreach (var i in items)
        {
            int len = i.RelativePath.Length + 8;
            if (batch.Count > 0 && (batch.Count >= 200 || bytes + len > 16_000 || (!quick && data + i.RemoteSize > MaxDataPerBatch)))
            {
                yield return batch;
                batch = new List<SyncItem>();
                bytes = 0;
                data = 0;
            }
            batch.Add(i);
            bytes += len;
            data += i.RemoteSize;
        }
        if (batch.Count > 0) yield return batch;
    }

    public const int QuickHashWindow = 65536;
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    /// <summary>
    /// Hash locale coerente con quello calcolato sul telefono: MD5 dell'intero file, oppure (rapido) MD5 dei primi
    /// min(64 KB, n) byte seguiti dagli ultimi min(64 KB, n) byte. Null se il file non è leggibile.
    /// </summary>
    internal static async Task<string?> LocalHash(string path, bool quick, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous);
            if (!quick) return Convert.ToHexStringLower(await MD5.HashDataAsync(fs, ct));
            long n = fs.Length;
            int w = (int)Math.Min(QuickHashWindow, n);
            var buf = new byte[2 * w];
            await fs.ReadExactlyAsync(buf.AsMemory(0, w), ct);
            fs.Seek(n - w, SeekOrigin.Begin);
            await fs.ReadExactlyAsync(buf.AsMemory(w, w), ct);
            return Convert.ToHexStringLower(MD5.HashData(buf));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Imposta data di modifica e di creazione (= data di modifica del telefono) sui file da allineare.</summary>
    private int AlignFileTimes(SyncPlan plan, List<string> errors)
    {
        int fixedCount = 0;
        foreach (var i in plan.Items.Where(i => i.Action == ItemAction.FixTimes && i.Remote is not null))
        {
            string path = PathMapper.ToLocalFullPath(plan.LocalRoot, i.LocalRelativePath);
            try
            {
                if (LocalChangedSincePlan(i, path) is { } changed)
                {
                    i.Status = ItemStatus.Failed;
                    i.Error = L.F("date non allineate: {0}", changed);
                    errors.Add(L.F("date non allineate: {0} — {1}", i.LocalRelativePath, changed));
                    continue;
                }
                SetFileTimes(path, i.Remote!.MtimeUtc, i.Remote.MtimeUnix);
                i.Status = ItemStatus.Done;
                fixedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                i.Status = ItemStatus.Failed;
                i.Error = L.F("date non impostate: {0}", ex.Message);
                errors.Add(L.F("date non impostate: {0} — {1}", i.LocalRelativePath, ex.Message));
            }
        }

        foreach (var e in errors.Take(50)) _log.Error(e);
        return fixedCount;
    }

    /// <summary>Date delle cartelle: si sistemano alla fine, perché scriverci dentro ne cambia la data.</summary>
    private int AlignDirTimes(SyncPlan plan, List<string> errors)
    {
        int fixedCount = 0, firstError = errors.Count;
        foreach (var d in plan.Directories.OrderByDescending(d => d.Depth))
        {
            string path = d.IsRoot ? plan.LocalRoot : PathMapper.ToLocalFullPath(plan.LocalRoot, d.LocalRelativePath);
            if (!TimeUtil.IsValidUnix(d.Remote.MtimeUnix) || !Directory.Exists(path)) continue;
            try
            {
                var target = d.Remote.MtimeUtc;
                long curM = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                long curC = new DateTimeOffset(Directory.GetCreationTimeUtc(path)).ToUnixTimeSeconds();
                if (!PlanBuilder.TimesEqual(curM, d.Remote.MtimeUnix, _o) || !PlanBuilder.TimesEqual(curC, d.Remote.MtimeUnix, _o))
                {
                    Directory.SetCreationTimeUtc(path, target);
                    Directory.SetLastWriteTimeUtc(path, target);
                    fixedCount++;
                }
                d.TimesAligned = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                d.Error = ex.Message;
                errors.Add(L.F("date cartella non impostate: {0} — {1}", d.IsRoot ? plan.LocalRoot : d.LocalRelativePath, ex.Message));
            }
        }
        foreach (var e in errors.Skip(firstError).Take(50)) _log.Error(e);
        return fixedCount;
    }

    private static void SetFileTimes(string path, DateTime mtimeUtc, long mtimeUnix)
    {
        if (!TimeUtil.IsValidUnix(mtimeUnix)) return;
        var attrs = File.GetAttributes(path);
        bool ro = attrs.HasFlag(FileAttributes.ReadOnly);
        if (ro) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        try
        {
            File.SetCreationTimeUtc(path, mtimeUtc);
            File.SetLastWriteTimeUtc(path, mtimeUtc); // per ultima: su Linux SetCreationTime tocca la data di modifica
        }
        finally
        {
            if (ro) File.SetAttributes(path, attrs);
        }
    }

    private SyncResult Finish(SyncPlan plan, List<SyncItem> items, Stopwatch sw, CancellationToken userCt, string? abortReason,
        int dirsCreated, int timesFixed, List<string> metaErrors, int verifiedAligned = 0)
    {
        bool cancelled = userCt.IsCancellationRequested;
        foreach (var i in items.Where(i => i.Status is ItemStatus.Pending or ItemStatus.Copying or ItemStatus.Retrying or ItemStatus.Requeued))
        {
            string last = i.Error is { Length: > 0 } e ? e : "";
            i.Status = ItemStatus.NotProcessed;
            string why = cancelled ? L.T("Annullato") : L.T("Non elaborato (lavoro interrotto)");
            i.Error = i.Attempts > 0 && last.Length > 0
                ? L.F("{0} durante il tentativo {1} — ultimo errore: {2}", why, i.Attempts, last)
                : why;
        }
        Progress.Active.Clear();
        // parziali conservati per la ripresa ma non completati: non si lasciano file a metà sul PC
        if (DestinationAvailable())
            foreach (var i in items.Where(i => i.Status != ItemStatus.Done))
                TryDelete(PathMapper.ToLocalFullPath(plan.LocalRoot, i.LocalRelativePath) + LocalScanner.TempSuffix);

        // File "stessa dimensione" da verificare rimasti in sospeso (annullamento o interruzione): mai contati come riusciti.
        int verifyPending = 0;
        foreach (var i in plan.Items.Where(i => i.Action == ItemAction.VerifyAndFixTimes && i.Status is ItemStatus.Pending))
        {
            i.Status = ItemStatus.NotProcessed;
            i.Error = cancelled ? L.T("Annullato: contenuto non verificato, date non toccate") : L.T("Non elaborato (lavoro interrotto): contenuto non verificato, date non toccate");
            verifyPending++;
        }
        if (verifyPending > 0)
            metaErrors.Add(L.F("{0} file con stessa dimensione NON verificati ({1}): date non toccate.", Format.Number(verifyPending), cancelled ? L.T("annullato") : L.T("lavoro interrotto")));

        var result = new SyncResult
        {
            Copied = items.Count(i => i.Status == ItemStatus.Done),
            Failed = items.Count(i => i.Status == ItemStatus.Failed),
            NotProcessed = items.Count(i => i.Status == ItemStatus.NotProcessed),
            BytesCopied = items.Where(i => i.Status == ItemStatus.Done).Sum(i => i.RemoteSize),
            DirsCreated = dirsCreated,
            TimesFixed = timesFixed,
            NotCopiable = plan.NotCopiable,
            DatesVerifiedAligned = verifiedAligned,
            ContentDiffers = plan.Items.Count(i => i.Status == ItemStatus.ContentDiffers),
            Stalls = Progress.Stalls,
            StallTimeLost = Progress.StallTimeLost,
            RecoveredAfterRetry = items.Count(i => i.Status == ItemStatus.Done && i.Attempts > 1),
            AlwaysStalled = items.Where(i => i.Status == ItemStatus.Failed && i.Stalls > 0 && i.Stalls >= i.Attempts).Select(i => i.RelativePath).ToList(),
            LeftUntouched = plan.Items.Count(i => i.Action == ItemAction.Skip && i.Category is ItemCategory.Conflict or ItemCategory.DateOnly),
            MetadataErrors = metaErrors,
            Duration = sw.Elapsed,
            Cancelled = cancelled,
            AbortReason = cancelled ? null : abortReason,
            FailedItems = items.Where(i => i.Status is ItemStatus.Failed).ToList(),
        };
        Progress.Phase = result.FullySucceeded ? L.T("Completato") : cancelled ? L.T("Annullato") : result.AbortReason is not null ? L.T("Interrotto") : L.T("Completato con errori");
        Progress.StatusMessage = null;
        Progress.FinishedAt = DateTime.UtcNow;

        result.ReportPath = WriteReport(plan, result);
        (result.RenamedLogPath, result.RenamedCount) = WriteRenamedLog(plan);
        _log.Write(result.FullySucceeded ? LogLevel.Info : LogLevel.Error, result.Summary);
        foreach (var f in result.FailedItems.Take(200)) _log.Error(L.F("  non copiato: {0} — {1}", f.RelativePath, f.Error));
        if (result.Stalls > 0)
        {
            _log.Info(L.F("Statistiche stalli: {0} stalli, circa {1} di attesa; " +
                          "{2} file riusciti dopo più tentativi; {3} file bloccati a ogni tentativo.",
                          result.Stalls, Format.Duration(result.StallTimeLost), result.RecoveredAfterRetry, result.AlwaysStalled.Count));
            foreach (var p in result.AlwaysStalled.Take(100)) _log.Warn(L.F("  sempre bloccato (candidato all'esclusione): {0}", p));
        }
        if (result.ReportPath is not null) _log.Info(L.F("Report dettagliato: {0}", result.ReportPath));
        if (result.RenamedLogPath is not null)
            _log.Info(L.F("Nomi convertiti per Windows: {0} — elenco nome originale → nome sul PC: {1}", Format.Number(result.RenamedCount), result.RenamedLogPath));
        return result;
    }

    /// <summary>
    /// Elenco testuale dei nomi convertiti (caratteri non ammessi da Windows/exFAT/NTFS): percorso originale sul telefono,
    /// percorso salvato sul PC ed esito. Scritto a ogni sincronizzazione che ne contiene, anche senza modalità diagnostica.
    /// </summary>
    private (string? Path, int Count) WriteRenamedLog(SyncPlan plan)
    {
        var dirs = plan.Directories.Where(d => !d.IsRoot && d.RelativePath != d.LocalRelativePath).ToList();
        var files = plan.Items.Where(i => i.NameWasSanitized && i.Remote is not null).ToList();
        if (dirs.Count + files.Count == 0 || _log.LogDirectory is null) return (null, 0);
        try
        {
            string path = Path.Combine(_log.LogDirectory, $"nomi_convertiti_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine(L.T("AdbSync — nomi convertiti perché non ammessi su Windows (exFAT/NTFS)"));
            w.WriteLine(L.F("Telefono: {0}", plan.RemoteRoot));
            w.WriteLine(L.F("PC:       {0}", plan.LocalRoot));
            w.WriteLine(L.T("I caratteri non ammessi da Windows sono sostituiti con caratteri Unicode quasi identici a vista (es. ? → ？ a larghezza piena, punto finale → ．): il nome resta leggibile e la conversione è sempre la stessa, così la copia incrementale li riconosce."));
            w.WriteLine(L.T("Colonne (separate da TAB): tipo, percorso sul telefono, percorso sul PC, esito, sostituzioni"));
            w.WriteLine();
            foreach (var d in dirs)
                w.WriteLine(string.Join('\t', L.T("cartella"), d.RelativePath, d.LocalRelativePath.Replace('/', '\\'),
                    d.Error is not null ? L.F("errore: {0}", d.Error) : d.Created ? L.T("creata") : L.T("presente"),
                    PathMapper.DescribeChanges(d.RelativePath, d.LocalRelativePath)));
            foreach (var i in files)
                w.WriteLine(string.Join('\t', L.T("file"), i.RelativePath, i.LocalRelativePath.Replace('/', '\\'), RenamedOutcome(i),
                    PathMapper.DescribeChanges(i.RelativePath, i.LocalRelativePath)));
            return (path, dirs.Count + files.Count);
        }
        catch (Exception ex)
        {
            _log.Warn(L.F("Impossibile scrivere l'elenco dei nomi convertiti: {0}", ex.Message));
            return (null, 0);
        }
    }

    private static string RenamedOutcome(SyncItem i) => i.Status switch
    {
        ItemStatus.Done when i.Action == ItemAction.Copy => L.T("copiato"),
        ItemStatus.Done => L.T("già presente, date allineate"),
        ItemStatus.Failed => L.F("NON copiato: {0}", i.Error),
        ItemStatus.NotProcessed => L.F("NON copiato: {0}", i.Error),
        ItemStatus.ContentDiffers => L.F("contenuto diverso: {0}", i.Error),
        _ => i.Category switch
        {
            ItemCategory.Identical => L.T("già presente"),
            ItemCategory.Excluded => L.F("escluso dalla regola {0}", i.ExcludedBy),
            ItemCategory.Collision or ItemCategory.Blocked => L.F("NON copiabile: {0}", i.Error),
            ItemCategory.Conflict or ItemCategory.DateOnly => L.T("presente sul PC, non toccato"),
            _ => i.Action == ItemAction.Skip ? L.T("non copiato (scelta manuale)") : L.T("non elaborato"),
        },
    };

    private string? WriteReport(SyncPlan plan, SyncResult result)
    {
        if (_log.LogDirectory is null) return null;
        try
        {
            string path = Path.Combine(_log.LogDirectory, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("Oggetto;Percorso telefono;Percorso PC;Categoria;Azione;Esito;Tentativi;Dimensione;Errore");
            foreach (var d in plan.Directories.Where(d => d.NeedsCreate || d.Error is not null))
                w.WriteLine(string.Join(';', "cartella", Csv(d.RelativePath), Csv(d.LocalRelativePath), "", d.NeedsCreate ? "crea" : "allinea date",
                    d.Error is null ? "Done" : "Failed", "", "", Csv(d.Error ?? "")));
            foreach (var i in plan.Items.Where(i => i.Action != ItemAction.Skip || i.Error is not null))
            {
                w.WriteLine(string.Join(';', "file", Csv(i.RelativePath), Csv(i.LocalRelativePath), i.Category, i.Action,
                    i.Status, i.Attempts, i.RemoteSize, Csv(i.Error ?? "")));
            }
            w.WriteLine();
            w.WriteLine(Csv(result.Summary));
            return path;
        }
        catch (Exception ex)
        {
            _log.Warn(L.F("Impossibile scrivere il report: {0}", ex.Message));
            return null;
        }

        static string Csv(string s) => s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private void Abort(string reason)
    {
        if (_abortReason is not null) return;
        _abortReason = reason;
        _log.Error(L.F("Interruzione del lavoro: {0}", reason));
        try { _abortCts?.Cancel(); } catch { }
    }

    private async Task WorkerAsync(int slot, ConcurrentQueue<SyncItem> queue, SyncPlan plan, CancellationToken ct)
    {
        IRemoteTransfer? transfer = null;
        try
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                if (item.Status == ItemStatus.Requeued) Progress.RequeueEnd();
                transfer = await CopyItemAsync(slot, item, plan, transfer, queue, ct);
            }
        }
        finally
        {
            transfer?.Dispose();
            Progress.Active.TryRemove(slot, out _);
        }
    }

    private enum FailureKind { Transient, Permanent, Fatal }

    private async Task<IRemoteTransfer?> CopyItemAsync(int slot, SyncItem item, SyncPlan plan, IRemoteTransfer? transfer,
        ConcurrentQueue<SyncItem> queue, CancellationToken ct)
    {
        string remotePath = PathMapper.ToRemoteFullPath(plan.RemoteRoot, item.RelativePath);
        string finalPath = PathMapper.ToLocalFullPath(plan.LocalRoot, item.LocalRelativePath);
        string tempPath = finalPath + LocalScanner.TempSuffix;
        int maxAttempts = 1 + Math.Max(0, _o.MaxRetries);
        item.Status = ItemStatus.Copying;

        for (;;)
        {
            // il numero di tentativi vive nell'elemento: sopravvive al passaggio in fondo alla coda
            int attempt = item.IncrementAttempts();
            var active = new ActiveTransfer { Slot = slot, Path = item.RelativePath, Size = item.RemoteSize, Attempt = attempt };
            Progress.Active[slot] = active;
            FailureKind kind;
            string message;
            bool stalled = false, diskStall = false, keepPartial = false;
            try
            {
                if (DestinationAvailable() && LocalChangedSincePlan(item, finalPath) is { } changedBefore)
                    throw new PermanentCopyException(changedBefore);
                if (transfer is null)
                {
                    transfer = _device.OpenTransfer(_o.UseSyncV2);
                    if (Interlocked.Exchange(ref _protocolLogged, 1) == 0)
                        _log.Info(L.F("Protocollo di trasferimento ADB: {0}", _o.UseSyncV2 ? "v2" : "v1"));
                }
                long resumeFrom = await ResumePointAsync(item, remotePath, tempPath, ct);
                long received = await PullOnceAsync(transfer, remotePath, tempPath, active, resumeFrom, ct);
                await VerifyAndCommitAsync(item, remotePath, tempPath, finalPath, received, ct);
                Progress.Active.TryRemove(slot, out _);
                Progress.FileDone(item.RemoteSize);
                item.Status = ItemStatus.Done;
                item.Error = null;
                Volatile.Write(ref _consecutiveStalls, 0);
                Volatile.Write(ref _failedHealthRounds, 0);
                _log.Debug($"OK {item.RelativePath} ({Format.Bytes(item.RemoteSize)}){(attempt > 1 ? $" al tentativo {attempt}" : "")}");
                return transfer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Progress.Active.TryRemove(slot, out _);
                TryDelete(tempPath);
                // lo stato resta "in copia": il riepilogo finale lo marca come annullato/interrotto con il motivo
                throw;
            }
            catch (Exception ex)
            {
                (kind, message) = Classify(ex);
                stalled = ex is StallException;
                diskStall = ex is DiskStallException;
                // Il parziale si conserva solo se i dati scritti sono buoni (interruzione, non copia sbagliata):
                // al tentativo successivo non si riscrive ciò che c'è già, e il disco non subisce cancella-e-riscrivi.
                keepPartial = kind == FailureKind.Transient && ex is not CorruptCopyException && ex is not LocalIoException;
                if (kind == FailureKind.Transient && ex is not TransientCopyException)
                    _log.Debug($"Dettaglio errore su {item.RelativePath}: {ex}");
            }

            Progress.Active.TryRemove(slot, out _);
            if (!keepPartial) TryDelete(tempPath);
            transfer?.Dispose();   // dopo un errore il canale si ricrea sempre da zero
            transfer = null;

            // Disco di destinazione sparito: non è un problema del file. Pausa e ripresa senza consumare tentativi.
            if (kind != FailureKind.Fatal && !DestinationAvailable())
            {
                item.Status = ItemStatus.Retrying;
                item.Error = message;
                Progress.RetryBegin();
                try
                {
                    await EnsureDestinationAsync(ct);
                }
                catch (DestinationUnavailableException dex)
                {
                    // resta "in ritentativo": il riepilogo finale lo marca come non elaborato con l'ultimo errore
                    Abort(dex.Message);
                    return null;
                }
                finally
                {
                    Progress.RetryEnd();
                }
                item.DecrementAttempts(); // il tentativo non conta
                item.Status = ItemStatus.Copying;
                continue;
            }

            if (kind == FailureKind.Fatal)
            {
                Fail(item, message);
                Abort(message);
                return null;
            }
            if (stalled)
            {
                item.AddStall();
                Progress.AddStall(diskStall ? DiskStallTimeout : StallTimeout);
            }
            if (kind == FailureKind.Permanent || attempt >= maxAttempts)
            {
                TryDelete(tempPath);
                Fail(item, attempt > 1 ? L.F("{0} (dopo {1} tentativi)", message, attempt) : message);
                return null;
            }

            // Stallo: il file è "bloccato" sul telefono, non c'è un problema di collegamento. Niente attese:
            // va in fondo alla coda e si prosegue con gli altri; lo si ritenta quando tocca di nuovo a lui.
            if (stalled)
            {
                item.Status = ItemStatus.Requeued;
                item.Error = message;
                Progress.RequeueBegin();
                queue.Enqueue(item);
                _log.Warn(diskStall
                    ? L.F("Disco di destinazione bloccato durante {0} (tentativo {1}/{2}): rimesso in fondo alla coda.", item.RelativePath, attempt, maxAttempts)
                    : L.F("Stallo su {0} (tentativo {1}/{2}): rimesso in fondo alla coda.", item.RelativePath, attempt, maxAttempts));
                // un disco lento non dice nulla sul telefono: non conta per il controllo del collegamento
                if (!diskStall && Interlocked.Increment(ref _consecutiveStalls) >= StallHealthThreshold)
                {
                    try { await CheckDeviceAfterStallsAsync(ct); }
                    catch (DeviceUnavailableException dex) { Abort(dex.Message); }
                }
                return null;
            }

            item.Status = ItemStatus.Retrying;
            item.Error = message;
            var delay = BackoffDelay(attempt);
            _log.Warn(L.F("Tentativo {0}/{1} fallito per {2}: {3} — riprovo tra {4:0} s", attempt, maxAttempts, item.RelativePath, message, delay.TotalSeconds));
            Progress.RetryBegin();
            try
            {
                await Task.Delay(delay, ct);
                await EnsureDeviceAsync(ct);
            }
            catch (DeviceUnavailableException dex)
            {
                Fail(item, dex.Message);
                Abort(dex.Message);
                return null;
            }
            finally
            {
                Progress.RetryEnd();
            }
            item.Status = ItemStatus.Copying;
        }
    }

    /// <summary>
    /// Punto di ripresa: se è rimasto un parziale del tentativo precedente e il file sul telefono non è cambiato,
    /// si riparte da lì (meno un margine di sicurezza); altrimenti da zero.
    /// </summary>
    private async Task<long> ResumePointAsync(SyncItem item, string remotePath, string tempPath, CancellationToken ct)
    {
        long existing;
        try { existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { existing = 0; }
        long point = Math.Max(0, existing - ResumeSafetyMargin);
        if (point <= 0 || item.Remote is null || existing > item.Remote.Size) { if (existing > 0) TryDelete(tempPath); return 0; }
        var st = await _device.StatAsync(remotePath, ct);
        if (st is null || st.Value.Size != item.Remote.Size || st.Value.Mtime != item.Remote.MtimeUnix)
        {
            _log.Info(L.F("{0}: il file sul telefono è cambiato, il parziale non è riutilizzabile e la copia riparte da zero.", item.RelativePath));
            TryDelete(tempPath);
            return 0;
        }
        _log.Info(L.F("Ripresa di {0}: {1} già scritti sul PC non vengono riscritti (il telefono li rimanda comunque, il protocollo ADB non permette di partire a metà).",
            item.RelativePath, Format.Bytes(point)));
        return point;
    }

    private void Fail(SyncItem item, string message)
    {
        item.Status = ItemStatus.Failed;
        item.Error = message;
        Progress.FileFailed(item.RemoteSize);
        _log.Error(L.F("NON COPIATO {0}: {1}", item.RelativePath, message));
    }

    private async Task<long> PullOnceAsync(IRemoteTransfer transfer, string remotePath, string tempPath, ActiveTransfer active, long resumeFrom, CancellationToken ct)
    {
        FileStream fs;
        try
        {
            // Se manca la cartella di destinazione (disco staccato) non va ricreata: si segnala e si attende.
            if (!DestinationAvailable())
                throw new DirectoryNotFoundException(L.F("cartella di destinazione non trovata: {0}", _localRoot));
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            fs = new FileStream(tempPath, resumeFrom > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (resumeFrom > 0)
            {
                fs.SetLength(resumeFrom);
                fs.Position = resumeFrom;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalIoException(ex);
        }

        long lastTick = Environment.TickCount64;
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Diario delle pause (diagnosi): ogni interruzione del flusso di dati oltre la soglia viene registrata con
        // il tempo passato a scrivere su disco, quello passato ad aspettare adb e lo stato del processo.
        Stream disk = DestinationWrapper?.Invoke(fs) ?? fs;
        var timed = new TimingStream(resumeFrom > 0 ? new SkipStream(disk, resumeFrom) : disk);
        long receivedTotal = 0;
        var snapshot = ProcessSnapshot.Take();
        bool firstChunk = true;
        // Task.Run: anche l'apertura della connessione (sincrona nella libreria) ricade sotto il controllo del watchdog.
        var pull = Task.Run(() => transfer.PullAsync(remotePath, timed, bytes =>
        {
            long now = Environment.TickCount64;
            long gap = now - Volatile.Read(ref lastTick);
            active.Bytes = bytes;
            Volatile.Write(ref receivedTotal, bytes);
            Volatile.Write(ref lastTick, now);
            if (!firstChunk && gap >= PauseLogThresholdMs)
            {
                var snap = ProcessSnapshot.Take();
                double writeMs = timed.LastWriteMs;
                _log.Info(L.F("[diagnosi] pausa di {0} ms su {1} a {2} di {3}: scrittura su disco {4} ms, attesa dati da adb {5} ms; {6}",
                    gap, active.Path, Format.Bytes(bytes), Format.Bytes(active.Size), (int)writeMs, Math.Max(0, gap - (long)writeMs), snap.DescribeSince(snapshot)));
                snapshot = snap;
            }
            firstChunk = false;
        }, attemptCts.Token));

        bool stalled = false, abandoned = false, diskStalled = false;
        string? slowDiskMessage = null;
        long stallMs = (long)StallTimeout.TotalMilliseconds;
        long diskStallMs = (long)DiskStallTimeout.TotalMilliseconds;
        while (!pull.IsCompleted)
        {
            await Task.WhenAny(pull, Task.Delay(WatchdogInterval));
            if (pull.IsCompleted) break;
            bool userStop = ct.IsCancellationRequested;
            // Due timeout distinti: mentre il PC sta scrivendo sul disco il cronometro del telefono è fermo
            // (la scrittura ha un suo limite, molto più lungo); il telefono è "fermo" solo se non si sta scrivendo.
            double inWrite = timed.WriteInProgressMs;
            long phoneIdle = inWrite > 0 ? 0 : Environment.TickCount64 - Math.Max(Volatile.Read(ref lastTick), timed.LastWriteEndTick);
            if (!userStop && inWrite > 0 && inWrite <= diskStallMs)
            {
                if (inWrite >= SlowDiskNotice.TotalMilliseconds)
                {
                    slowDiskMessage = L.F("Disco di destinazione lento: scrittura ferma da {0:0} s (limite {1:0} s) — attendo senza interrompere la copia.",
                        inWrite / 1000, DiskStallTimeout.TotalSeconds);
                    Progress.StatusMessage = slowDiskMessage;
                }
                continue;
            }
            if (slowDiskMessage is not null && inWrite == 0)
            {
                if (Progress.StatusMessage == slowDiskMessage) Progress.StatusMessage = null;
                slowDiskMessage = null;
            }
            if (userStop || inWrite > diskStallMs || phoneIdle > stallMs)
            {
                stalled = !userStop;
                diskStalled = !userStop && inWrite > diskStallMs;
                if (stalled)
                {
                    _log.Info(L.F("[diagnosi] stallo su {0} a {1} di {2} dopo {3} ms senza dati: {4}; {5}",
                        active.Path, Format.Bytes(active.Bytes), Format.Bytes(active.Size), diskStalled ? (long)inWrite : phoneIdle,
                        inWrite > 0 ? L.F("il programma sta scrivendo su disco da {0} ms", (int)inWrite) : L.T("nessuna scrittura su disco in corso: in attesa di dati da adb"),
                        ProcessSnapshot.Take().DescribeSince(snapshot)));
                }
                attemptCts.Cancel();
                if (await Task.WhenAny(pull, Task.Delay(ForceCloseDelay)) != pull)
                {
                    transfer.Dispose(); // chiude il socket: sblocca letture rimaste appese
                    if (await Task.WhenAny(pull, Task.Delay(ForceCloseDelay)) != pull)
                    {
                        abandoned = true;
                        _ = pull.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                break;
            }
        }

        Exception? pullError = null;
        if (!abandoned)
        {
            try { await pull; }
            catch (Exception ex) { pullError = ex; }
        }

        long length = -1;
        try
        {
            if (!abandoned)
            {
                await fs.FlushAsync(CancellationToken.None);
                length = fs.Length;
            }
        }
        catch (Exception ex) when (pullError is null && !stalled)
        {
            pullError = new LocalIoException(ex);
        }
        catch { }
        finally
        {
            try { await fs.DisposeAsync(); } catch { }
        }

        if (slowDiskMessage is not null && Progress.StatusMessage == slowDiskMessage) Progress.StatusMessage = null;
        ct.ThrowIfCancellationRequested();
        if (diskStalled)
            throw new DiskStallException(L.F("scrittura sul disco del PC ferma da oltre {0:0} s (disco di destinazione bloccato)", DiskStallTimeout.TotalSeconds));
        if (stalled || abandoned)
            throw new StallException(L.F("nessun dato ricevuto per {0:0} s (trasferimento bloccato)", StallTimeout.TotalSeconds));
        if (pullError is not null) ExceptionDispatchInfo.Throw(pullError);
        // ripresa: se il telefono ha mandato meno dei byte che avevamo già, il parziale non è coerente
        if (resumeFrom > 0 && Volatile.Read(ref receivedTotal) < resumeFrom)
            throw new CorruptCopyException(L.F("ricevuti {0} byte, meno dei {1} già scritti: la copia riparte da zero", Volatile.Read(ref receivedTotal), resumeFrom));
        return length;
    }

    private async Task VerifyAndCommitAsync(SyncItem item, string remotePath, string tempPath, string finalPath, long received, CancellationToken ct)
    {
        var remote = item.Remote!;
        if (received != remote.Size)
        {
            var st = await _device.StatAsync(remotePath, ct);
            if (st is null) throw new PermanentCopyException(L.T("il file non è più presente sul telefono"));
            if (st.Value.Size == received)
            {
                _log.Warn(L.F("{0} è stato modificato sul telefono dopo la scansione: copiata la versione attuale ({1}).", item.RelativePath, Format.Bytes(received)));
                item.Remote = remote with { Size = received, MtimeUnix = st.Value.Mtime, MtimeFraction = st.Value.Fraction };
                item.RemoteChangedDuringCopy = true;
                remote = item.Remote;
            }
            else
            {
                throw new TransientCopyException(L.F("ricevuti {0} byte invece di {1} (copia incompleta)", Format.Number(received), Format.Number(remote.Size)));
            }
        }

        if (_o.VerifyHash)
        {
            string? remoteMd5 = await _device.Md5Async(remotePath, ct);
            if (remoteMd5 is null) throw new TransientCopyException(L.T("impossibile calcolare l'MD5 sul telefono"));
            string localMd5;
            try
            {
                await using var s = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                localMd5 = Convert.ToHexStringLower(await MD5.HashDataAsync(s, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LocalIoException(ex); }
            if (!string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase))
                throw new CorruptCopyException(L.F("MD5 diverso (telefono {0}, PC {1}): copia corrotta", remoteMd5, localMd5));
        }

        if (LocalChangedSincePlan(item, finalPath) is { } changed)
            throw new PermanentCopyException(changed);
        try
        {
            if (File.Exists(finalPath))
            {
                var attrs = File.GetAttributes(finalPath);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(finalPath, attrs & ~FileAttributes.ReadOnly);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LocalIoException(ex); }

        try
        {
            if (TimeUtil.IsValidUnix(remote.MtimeUnix))
                SetFileTimes(finalPath, remote.MtimeUtc, remote.MtimeUnix);
            else
                _log.Warn(L.F("{0}: data di modifica non valida sul telefono ({1}), non impostata.", item.RelativePath, remote.MtimeUnix));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn(L.F("{0}: copiato, ma impossibile impostare le date ({1}).", item.RelativePath, ex.Message));
        }
    }

    /// <summary>
    /// Protezione da piani non aggiornati: il file sul PC deve essere ancora come al momento del confronto
    /// (assente per i nuovi; stessa dimensione e data per gli altri). Altrimenti non si tocca.
    /// </summary>
    private static string? LocalChangedSincePlan(SyncItem item, string finalPath)
    {
        try
        {
            if (Directory.Exists(finalPath))
                return L.T("sul PC esiste una cartella con lo stesso nome: non sovrascritta");
            var fi = new FileInfo(finalPath);
            if (item.Local is null)
                return fi.Exists ? L.T("il file è comparso sul PC dopo il confronto: non sovrascritto (rifai il confronto)") : null;
            if (!fi.Exists)
                return null; // eliminato sul PC dopo il confronto: ricrearlo dal telefono è sicuro
            long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (fi.Length != item.Local.Size || mtime != item.Local.MtimeUnix)
                return L.T("il file sul PC è stato modificato dopo il confronto: non sovrascritto (rifai il confronto)");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // errore di accesso: lo gestisce la copia vera e propria
        }
    }

    // ------------------------------------------------------------------ ATTESE (telefono / disco)

    private bool DestinationAvailable()
    {
        try { return Directory.Exists(_localRoot); }
        catch { return false; }
    }

    private Task EnsureDestinationAsync(CancellationToken ct)
    {
        lock (_waitLock)
        {
            if (_destWait is null || _destWait.IsCompleted)
                _destWait = WaitForDestinationCoreAsync(ct);
            return _destWait;
        }
    }

    private async Task WaitForDestinationCoreAsync(CancellationToken ct)
    {
        if (DestinationAvailable()) return;
        string msg = L.F("Disco di destinazione non disponibile ({0}): ricollegalo (stessa lettera di unità) — in attesa...", _localRoot);
        _log.Warn(msg);
        Progress.StatusMessage = msg;
        var deadline = DateTime.UtcNow + WaitTimeout;
        try
        {
            while (!DestinationAvailable())
            {
                if (DateTime.UtcNow > deadline)
                    throw new DestinationUnavailableException(L.F("il disco di destinazione ({0}) non è più disponibile da {1}.", _localRoot, Format.Duration(WaitTimeout)));
                await Task.Delay(PollInterval, ct);
            }
            _log.Info(L.T("Disco di destinazione di nuovo disponibile: riprendo la copia."));
        }
        finally
        {
            Progress.StatusMessage = null;
        }
    }

    /// <summary>Dopo questo numero di stalli consecutivi (nessun file completato nel frattempo) si verifica che il telefono risponda.</summary>
    private int StallHealthThreshold => Math.Max(10, 4 * _o.ClampedParallel);
    internal const int MaxFailedHealthRounds = 5;

    /// <summary>
    /// Molti stalli di fila senza alcun file completato: il problema potrebbe essere il collegamento, non i singoli file.
    /// Si verifica che il telefono risponda (con attesa di riconnessione se serve); se continua a non completarsi nulla
    /// per <see cref="MaxFailedHealthRounds"/> controlli consecutivi, il lavoro viene interrotto invece di girare all'infinito.
    /// </summary>
    private Task CheckDeviceAfterStallsAsync(CancellationToken ct)
    {
        lock (_waitLock)
        {
            if (_healthCheck is null || _healthCheck.IsCompleted)
                _healthCheck = CheckDeviceAfterStallsCoreAsync(ct);
            return _healthCheck;
        }
    }

    private async Task CheckDeviceAfterStallsCoreAsync(CancellationToken ct)
    {
        int stalls = Interlocked.Exchange(ref _consecutiveStalls, 0);
        int rounds = Interlocked.Increment(ref _failedHealthRounds);
        if (rounds > MaxFailedHealthRounds)
            throw new DeviceUnavailableException(L.F(
                "i trasferimenti si bloccano di continuo (oltre {0} stalli di fila senza alcun file completato, " +
                "anche se il telefono risponde). Scollega e ricollega il cavo (o riavvia il telefono) e rilancia: i file già copiati non verranno ricopiati. " +
                "Se gli stalli riguardano sempre gli stessi file (vedi Log), valuta di escluderli.",
                Format.Number(MaxFailedHealthRounds * StallHealthThreshold)));
        _log.Warn(L.F("{0} stalli di fila senza file completati: verifico che il telefono risponda (controllo {1}/{2})...", stalls, rounds, MaxFailedHealthRounds));
        Progress.StatusMessage = L.T("Molti stalli di fila: verifico il collegamento con il telefono...");
        try
        {
            await _device.WaitOnlineAsync(WaitTimeout, m => Progress.StatusMessage = m, ct);
            _log.Info(L.T("Il telefono risponde: proseguo."));
        }
        finally
        {
            Progress.StatusMessage = null;
        }
    }

    private Task EnsureDeviceAsync(CancellationToken ct)
    {
        lock (_waitLock)
        {
            if (_deviceWait is null || _deviceWait.IsCompleted)
                _deviceWait = WaitForDeviceCoreAsync(ct);
            return _deviceWait;
        }
    }

    private async Task WaitForDeviceCoreAsync(CancellationToken ct)
    {
        if (await _device.IsOnlineAsync(ct)) return;
        _log.Warn(L.T("Telefono non raggiungibile: attendo che torni disponibile..."));
        Progress.StatusMessage = L.T("Telefono non raggiungibile: in attesa della riconnessione...");
        try
        {
            await _device.WaitOnlineAsync(WaitTimeout, m => Progress.StatusMessage = m, ct);
            _log.Info(L.T("Telefono di nuovo disponibile: riprendo la copia."));
        }
        finally
        {
            Progress.StatusMessage = null;
        }
    }

    private static (FailureKind, string) Classify(Exception ex)
    {
        if (IsDiskFull(ex)) return (FailureKind.Fatal, L.T("spazio su disco esaurito sul PC"));
        switch (ex)
        {
            case StallException s: return (FailureKind.Transient, s.Message);
            case TransientCopyException t: return (FailureKind.Transient, t.Message);
            case PermanentCopyException p: return (FailureKind.Permanent, p.Message);
            case DeviceUnavailableException d: return (FailureKind.Fatal, d.Message);
            case DestinationUnavailableException d: return (FailureKind.Fatal, d.Message);
            case LocalIoException { InnerException: UnauthorizedAccessException u }:
                return (FailureKind.Permanent, L.F("accesso negato sul PC: {0}", u.Message));
            case LocalIoException l:
                return (FailureKind.Transient, L.F("errore di scrittura sul PC: {0}", l.Message));
        }
        string msg = ex.Message;
        if (msg.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return (FailureKind.Permanent, L.T("il file non è più presente sul telefono"));
        if (msg.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return (FailureKind.Permanent, L.T("permesso negato sul telefono"));
        return (FailureKind.Transient, $"{ex.GetType().Name}: {msg}");
    }

    private static bool IsDiskFull(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is IOException io)
            {
                int code = io.HResult & 0xFFFF;
                if (OperatingSystem.IsWindows() && (code == 0x70 || code == 0x27)) return true; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
                if (io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
