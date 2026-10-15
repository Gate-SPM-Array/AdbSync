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

public sealed class StallException(string message) : TransientCopyException(message);

/// <summary>Esegue confronto e copia con verifica, ritentativi, rilevamento stalli e attesa riconnessione.</summary>
public sealed class SyncEngine
{
    private readonly IRemoteDevice _device;
    private readonly SyncOptions _o;
    private readonly SyncLogger _log;
    private readonly object _waitLock = new();
    private Task? _deviceWait;
    private Task? _healthCheck;
    private int _consecutiveStalls, _failedHealthRounds;
    private Task? _destWait;
    private CancellationTokenSource? _abortCts;
    private volatile string? _abortReason;
    private string _localRoot = "";

    public SyncProgress Progress { get; } = new();

    // Parametri temporali sovrascrivibili nei test.
    internal Func<int, TimeSpan> BackoffDelay { get; set; } =
        attempt => TimeSpan.FromSeconds(new[] { 2, 5, 10, 20, 30 }[Math.Clamp(attempt - 1, 0, 4)]);
    internal TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan? StallTimeoutOverride { get; set; }
    internal TimeSpan ForceCloseDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan? WaitTimeoutOverride { get; set; }

    private TimeSpan StallTimeout => StallTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(1, _o.StallTimeoutSeconds));
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
        int sanitized = plan.Items.Count(i => i.NameWasSanitized);
        if (sanitized > 0) log.Warn($"{sanitized} nomi contengono caratteri non ammessi da Windows: verranno salvati con caratteri sostitutivi equivalenti.");
        foreach (var c in plan.Items.Where(i => i.Category is ItemCategory.Collision or ItemCategory.Blocked).Take(20))
            log.Warn($"File NON copiabile: {c.RelativePath} — {c.Error}");
        if (plan.NotCopiable > 0)
            log.Warn($"Attenzione: {plan.NotCopiable} elementi del telefono non potranno essere copiati (permesso negato, collisioni, bloccati).");
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
        log.Info($"Confronto: telefono {device.DisplayName}:{remoteRoot}  →  PC {localRoot}");

        int remoteCount = 0, localCount = 0;
        void Report() => status?.Invoke($"Scansione in corso — telefono: {Format.Number(remoteCount)} elementi · PC: {Format.Number(localCount)} file");

        var remoteTask = device.ScanAsync(remoteRoot, n => { remoteCount = n; Report(); }, ct);
        var localTask = Task.Run(() => LocalScanner.Scan(localRoot, n => { localCount = n; Report(); }, ct), ct);
        await Task.WhenAll(remoteTask, localTask);

        var remote = remoteTask.Result;
        var local = localTask.Result;
        if (remote.RootNotFound)
            throw new DirectoryNotFoundException($"La cartella \"{remoteRoot}\" non esiste sul telefono o non è accessibile.");

        log.Info($"Scansione completata in {Format.Duration(sw.Elapsed)}: telefono {Format.Number(remote.Files.Count)} file e " +
                 $"{Format.Number(Math.Max(0, remote.Directories.Count - 1))} cartelle (verifica: trovati {Format.Number(remote.FoundEntries)}, " +
                 $"dichiarati {Format.Number(remote.DeclaredEntries)}, ricevuti {Format.Number(remote.ReceivedEntries)}); " +
                 $"PC {Format.Number(local.Files.Count)} file e {Format.Number(Math.Max(0, local.Directories.Count - 1))} cartelle.");
        if (local.ReparseDirectories.Count > 0)
            log.Warn($"Sul PC {local.ReparseDirectories.Count} cartelle sono collegamenti (junction/symlink): non vengono attraversate né modificate.");
        if (remote.UsedMergedOutput)
            log.Warn("La cartella temporanea del telefono (/data/local/tmp) non è scrivibile: usata la lettura in tre passate (più lenta).");
        if (remote.SymbolicLinks.Count > 0)
        {
            log.Warn($"{remote.SymbolicLinks.Count} link simbolici sul telefono non vengono seguiti né copiati.");
            foreach (var l in remote.SymbolicLinks.Take(10)) log.Warn("  link: " + l);
        }
        if (remote.PermissionDenied.Count > 0)
        {
            log.Warn($"Android non permette di leggere {remote.PermissionDenied.Count} elementi (normale per Android/data e Android/obb): non verranno copiati.");
            foreach (var p in remote.PermissionDenied.Take(20)) log.Warn("  permesso negato: " + p);
            if (remote.PermissionDenied.Count > 20) log.Warn($"  ... e altri {remote.PermissionDenied.Count - 20} (elenco completo con la modalità diagnostica).");
        }
        foreach (var v in remote.Vanished.Take(20)) log.Warn("File sparito dal telefono durante la scansione: " + v);

        if (remote.Problems.Count > 0)
        {
            if (options.Diagnostic) Diagnostics.WriteCompare(log.LogDirectory, remoteRoot, localRoot, remote, local, null, log);
            foreach (var p in remote.Problems) log.Error("Scansione del telefono INCOMPLETA: " + p);
            throw new IncompleteScanException(
                "La scansione del telefono è INCOMPLETA, quindi il confronto è stato fermato per non darti un risultato sbagliato.\n\n" +
                string.Join("\n", remote.Problems.Select(p => "• " + p)) +
                "\n\nRiprova; se si ripete, attiva la modalità diagnostica e invia la cartella dei log.");
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
            throw new DestinationUnavailableException($"L'unità {drive} non è disponibile: collega il disco di destinazione e riprova.");
    }

    public static string DescribePlan(SyncPlan p) =>
        $"Risultato confronto: {p.Count(ItemCategory.New)} nuovi, {p.Count(ItemCategory.Changed)} modificati, " +
        $"{p.Count(ItemCategory.Conflict)} conflitti, {p.Count(ItemCategory.DateOnly)} solo data diversa, " +
        $"{p.Count(ItemCategory.Identical)} identici, {p.Count(ItemCategory.OnlyOnPc)} solo sul PC, " +
        $"{p.Count(ItemCategory.Collision)} collisioni, {p.ExcludedCount} esclusi. " +
        $"Elementi: telefono {Format.Number(p.RemoteFileCount)} file + {Format.Number(p.RemoteDirCount)} cartelle, " +
        $"PC {Format.Number(p.LocalFileCount)} file + {Format.Number(p.LocalDirCount)} cartelle. Da fare: {p.WorkSummary}.";

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
        Progress.Phase = "Preparazione";
        _abortCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = _abortCts.Token;
        var metaErrors = new List<string>();
        int dirsCreated = 0, timesFixed = 0;

        if (!plan.HasWork)
        {
            _log.Info("Niente da fare: la cartella sul PC è già allineata.");
            return Finish(plan, items, sw, userCt, null, 0, 0, metaErrors);
        }

        foreach (var t in plan.StaleTempFiles)
        {
            try { File.Delete(t); _log.Info("Rimosso file temporaneo rimasto da un'esecuzione interrotta: " + t); }
            catch (Exception ex) { _log.Warn($"Impossibile rimuovere il temporaneo {t}: {ex.Message}"); }
        }

        try
        {
            EnsureLocalDriveAvailable(plan.LocalRoot);
            Directory.CreateDirectory(plan.LocalRoot);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(plan.LocalRoot))!);
            long needed = Progress.TotalBytes;
            if (needed > 0 && drive.IsReady && drive.AvailableFreeSpace < needed)
                return Finish(plan, items, sw, userCt, $"spazio insufficiente su {drive.Name}: servono {Format.Bytes(needed)}, disponibili {Format.Bytes(drive.AvailableFreeSpace)}.", 0, 0, metaErrors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DestinationUnavailableException)
        {
            return Finish(plan, items, sw, userCt, "cartella di destinazione non utilizzabile: " + ex.Message, 0, 0, metaErrors);
        }

        // 1) cartelle mancanti (anche quelle vuote sul telefono)
        var toCreate = plan.Directories.Where(d => d.NeedsCreate && !d.IsRoot).OrderBy(d => d.Depth).ToList();
        if (toCreate.Count > 0)
        {
            Progress.Phase = "Creazione cartelle";
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
                    metaErrors.Add($"cartella non creata: {d.LocalRelativePath} — {ex.Message}");
                    _log.Error($"Cartella non creata: {d.LocalRelativePath} — {ex.Message}");
                }
            }
            if (dirsCreated > 0) _log.Info($"Create {dirsCreated} cartelle.");
        }

        // 2) copia dei file
        if (items.Count > 0 && !ct.IsCancellationRequested)
        {
            int parallel = Math.Min(_o.ClampedParallel, items.Count);
            _log.Info($"Inizio copia: {Format.Number(items.Count)} file, {Format.Bytes(Progress.TotalBytes)}, {parallel} trasferimenti paralleli, " +
                      $"timeout stallo {StallTimeout.TotalSeconds:0} s, tentativi {1 + _o.MaxRetries}{(_o.VerifyHash ? ", verifica MD5" : "")}.");
            Progress.Phase = "Copia";
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
                Abort("errore imprevisto: " + ex.Message);
                _log.Error(ex.ToString());
            }
        }

        // 3) date: file identici, file "stessa dimensione" verificati, poi tutte le cartelle (dalle più profonde)
        int verifiedAligned = 0;
        if (!userCt.IsCancellationRequested && DestinationAvailable())
        {
            Progress.Phase = "Allineamento date";
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
                    Abort("errore imprevisto durante la verifica: " + ex.Message);
                    _log.Error(ex.ToString());
                }
            }
            if (!userCt.IsCancellationRequested && DestinationAvailable())
            {
                Progress.Phase = "Allineamento date cartelle";
                timesFixed += AlignDirTimes(plan, metaErrors);
            }
        }
        else if (!userCt.IsCancellationRequested)
        {
            _log.Warn("Date non allineate: la destinazione non è disponibile.");
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
        string kind = quick ? "verifica rapida (testa e coda)" : "verifica MD5 completa";
        _log.Info($"Verifica e allineamento date: {Format.Number(todo.Count)} file con stessa dimensione, {kind}.");
        int aligned = 0, differs = 0, done = 0;
        foreach (var batch in HashBatches(todo, quick))
        {
            ct.ThrowIfCancellationRequested();
            Progress.Phase = $"Verifica e allineamento date: {Format.Number(done)} di {Format.Number(todo.Count)}";
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
                    _log.Warn($"Calcolo hash sul telefono non riuscito ({ex.Message}): riprovo.");
                    await Task.Delay(BackoffDelay(attempt), ct);
                    await EnsureDeviceAsync(ct);
                }
                catch (Exception ex)
                {
                    // Blocco non verificabile: i suoi file restano com'erano (date non toccate) e sono segnalati; si prosegue.
                    _log.Error($"Calcolo hash sul telefono non riuscito dopo 3 tentativi ({ex.Message}): {batch.Count} file non verificati.");
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
                    i.Error = "verifica non eseguita: " + (string.IsNullOrEmpty(rh) ? "file non leggibile sul telefono" : "file non leggibile sul PC");
                    errors.Add($"{i.LocalRelativePath} — {i.Error}");
                    continue;
                }
                if (!string.Equals(rh, lh, StringComparison.OrdinalIgnoreCase))
                {
                    i.Status = ItemStatus.ContentDiffers;
                    i.Error = $"contenuto DIVERSO dal telefono ({kind}): date non modificate";
                    differs++;
                    _log.Warn($"Contenuto diverso, date non toccate: {i.LocalRelativePath}");
                    continue;
                }
                try
                {
                    if (LocalChangedSincePlan(i, path) is { } changed)
                    {
                        i.Status = ItemStatus.Failed;
                        i.Error = "date non allineate: " + changed;
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
                    i.Error = "date non impostate: " + ex.Message;
                    errors.Add($"{i.LocalRelativePath} — {i.Error}");
                }
            }
        }
        _log.Info($"Verifica completata: {Format.Number(aligned)} file identici con date allineate, {Format.Number(differs)} con contenuto diverso (non toccati).");
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
                    i.Error = "date non allineate: " + changed;
                    errors.Add($"date non allineate: {i.LocalRelativePath} — {changed}");
                    continue;
                }
                SetFileTimes(path, i.Remote!.MtimeUtc, i.Remote.MtimeUnix);
                i.Status = ItemStatus.Done;
                fixedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                i.Status = ItemStatus.Failed;
                i.Error = "date non impostate: " + ex.Message;
                errors.Add($"date non impostate: {i.LocalRelativePath} — {ex.Message}");
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
                errors.Add($"date cartella non impostate: {(d.IsRoot ? plan.LocalRoot : d.LocalRelativePath)} — {ex.Message}");
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
            string why = cancelled ? "Annullato" : "Non elaborato (lavoro interrotto)";
            i.Error = i.Attempts > 0 && last.Length > 0
                ? $"{why} durante il tentativo {i.Attempts} — ultimo errore: {last}"
                : why;
        }
        Progress.Active.Clear();

        // File "stessa dimensione" da verificare rimasti in sospeso (annullamento o interruzione): mai contati come riusciti.
        int verifyPending = 0;
        foreach (var i in plan.Items.Where(i => i.Action == ItemAction.VerifyAndFixTimes && i.Status is ItemStatus.Pending))
        {
            i.Status = ItemStatus.NotProcessed;
            i.Error = cancelled ? "Annullato: contenuto non verificato, date non toccate" : "Non elaborato (lavoro interrotto): contenuto non verificato, date non toccate";
            verifyPending++;
        }
        if (verifyPending > 0)
            metaErrors.Add($"{Format.Number(verifyPending)} file con stessa dimensione NON verificati ({(cancelled ? "annullato" : "lavoro interrotto")}): date non toccate.");

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
        Progress.Phase = result.FullySucceeded ? "Completato" : cancelled ? "Annullato" : result.AbortReason is not null ? "Interrotto" : "Completato con errori";
        Progress.StatusMessage = null;
        Progress.FinishedAt = DateTime.UtcNow;

        result.ReportPath = WriteReport(plan, result);
        _log.Write(result.FullySucceeded ? LogLevel.Info : LogLevel.Error, result.Summary);
        foreach (var f in result.FailedItems.Take(200)) _log.Error($"  non copiato: {f.RelativePath} — {f.Error}");
        if (result.Stalls > 0)
        {
            _log.Info($"Statistiche stalli: {result.Stalls} stalli, circa {Format.Duration(result.StallTimeLost)} di attesa; " +
                      $"{result.RecoveredAfterRetry} file riusciti dopo più tentativi; {result.AlwaysStalled.Count} file bloccati a ogni tentativo.");
            foreach (var p in result.AlwaysStalled.Take(100)) _log.Warn("  sempre bloccato (candidato all'esclusione): " + p);
        }
        if (result.ReportPath is not null) _log.Info("Report dettagliato: " + result.ReportPath);
        return result;
    }

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
            _log.Warn("Impossibile scrivere il report: " + ex.Message);
            return null;
        }

        static string Csv(string s) => s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private void Abort(string reason)
    {
        if (_abortReason is not null) return;
        _abortReason = reason;
        _log.Error("Interruzione del lavoro: " + reason);
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
            bool stalled = false;
            try
            {
                if (DestinationAvailable() && LocalChangedSincePlan(item, finalPath) is { } changedBefore)
                    throw new PermanentCopyException(changedBefore);
                transfer ??= _device.OpenTransfer();
                long received = await PullOnceAsync(transfer, remotePath, tempPath, active, ct);
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
                if (kind == FailureKind.Transient && ex is not TransientCopyException)
                    _log.Debug($"Dettaglio errore su {item.RelativePath}: {ex}");
            }

            Progress.Active.TryRemove(slot, out _);
            TryDelete(tempPath);
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
                Progress.AddStall(StallTimeout);
            }
            if (kind == FailureKind.Permanent || attempt >= maxAttempts)
            {
                Fail(item, attempt > 1 ? $"{message} (dopo {attempt} tentativi)" : message);
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
                _log.Warn($"Stallo su {item.RelativePath} (tentativo {attempt}/{maxAttempts}): rimesso in fondo alla coda.");
                if (Interlocked.Increment(ref _consecutiveStalls) >= StallHealthThreshold)
                {
                    try { await CheckDeviceAfterStallsAsync(ct); }
                    catch (DeviceUnavailableException dex) { Abort(dex.Message); }
                }
                return null;
            }

            item.Status = ItemStatus.Retrying;
            item.Error = message;
            var delay = BackoffDelay(attempt);
            _log.Warn($"Tentativo {attempt}/{maxAttempts} fallito per {item.RelativePath}: {message} — riprovo tra {delay.TotalSeconds:0} s");
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

    private void Fail(SyncItem item, string message)
    {
        item.Status = ItemStatus.Failed;
        item.Error = message;
        Progress.FileFailed(item.RemoteSize);
        _log.Error($"NON COPIATO {item.RelativePath}: {message}");
    }

    private async Task<long> PullOnceAsync(IRemoteTransfer transfer, string remotePath, string tempPath, ActiveTransfer active, CancellationToken ct)
    {
        FileStream fs;
        try
        {
            // Se manca la cartella di destinazione (disco staccato) non va ricreata: si segnala e si attende.
            if (!DestinationAvailable())
                throw new DirectoryNotFoundException($"cartella di destinazione non trovata: {_localRoot}");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalIoException(ex);
        }

        long lastTick = Environment.TickCount64;
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Task.Run: anche l'apertura della connessione (sincrona nella libreria) ricade sotto il controllo del watchdog.
        var pull = Task.Run(() => transfer.PullAsync(remotePath, fs, bytes =>
        {
            active.Bytes = bytes;
            Volatile.Write(ref lastTick, Environment.TickCount64);
        }, attemptCts.Token));

        bool stalled = false, abandoned = false;
        long stallMs = (long)StallTimeout.TotalMilliseconds;
        while (!pull.IsCompleted)
        {
            await Task.WhenAny(pull, Task.Delay(WatchdogInterval));
            if (pull.IsCompleted) break;
            bool userStop = ct.IsCancellationRequested;
            if (userStop || Environment.TickCount64 - Volatile.Read(ref lastTick) > stallMs)
            {
                stalled = !userStop;
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

        ct.ThrowIfCancellationRequested();
        if (stalled || abandoned)
            throw new StallException($"nessun dato ricevuto per {StallTimeout.TotalSeconds:0} s (trasferimento bloccato)");
        if (pullError is not null) ExceptionDispatchInfo.Throw(pullError);
        return length;
    }

    private async Task VerifyAndCommitAsync(SyncItem item, string remotePath, string tempPath, string finalPath, long received, CancellationToken ct)
    {
        var remote = item.Remote!;
        if (received != remote.Size)
        {
            var st = await _device.StatAsync(remotePath, ct);
            if (st is null) throw new PermanentCopyException("il file non è più presente sul telefono");
            if (st.Value.Size == received)
            {
                _log.Warn($"{item.RelativePath} è stato modificato sul telefono dopo la scansione: copiata la versione attuale ({Format.Bytes(received)}).");
                item.Remote = remote with { Size = received, MtimeUnix = st.Value.Mtime, MtimeFraction = st.Value.Fraction };
                item.RemoteChangedDuringCopy = true;
                remote = item.Remote;
            }
            else
            {
                throw new TransientCopyException($"ricevuti {Format.Number(received)} byte invece di {Format.Number(remote.Size)} (copia incompleta)");
            }
        }

        if (_o.VerifyHash)
        {
            string? remoteMd5 = await _device.Md5Async(remotePath, ct);
            if (remoteMd5 is null) throw new TransientCopyException("impossibile calcolare l'MD5 sul telefono");
            string localMd5;
            try
            {
                await using var s = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                localMd5 = Convert.ToHexStringLower(await MD5.HashDataAsync(s, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LocalIoException(ex); }
            if (!string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase))
                throw new TransientCopyException($"MD5 diverso (telefono {remoteMd5}, PC {localMd5}): copia corrotta");
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
                _log.Warn($"{item.RelativePath}: data di modifica non valida sul telefono ({remote.MtimeUnix}), non impostata.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"{item.RelativePath}: copiato, ma impossibile impostare le date ({ex.Message}).");
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
                return "sul PC esiste una cartella con lo stesso nome: non sovrascritta";
            var fi = new FileInfo(finalPath);
            if (item.Local is null)
                return fi.Exists ? "il file è comparso sul PC dopo il confronto: non sovrascritto (rifai il confronto)" : null;
            if (!fi.Exists)
                return null; // eliminato sul PC dopo il confronto: ricrearlo dal telefono è sicuro
            long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (fi.Length != item.Local.Size || mtime != item.Local.MtimeUnix)
                return "il file sul PC è stato modificato dopo il confronto: non sovrascritto (rifai il confronto)";
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
        string msg = $"Disco di destinazione non disponibile ({_localRoot}): ricollegalo (stessa lettera di unità) — in attesa...";
        _log.Warn(msg);
        Progress.StatusMessage = msg;
        var deadline = DateTime.UtcNow + WaitTimeout;
        try
        {
            while (!DestinationAvailable())
            {
                if (DateTime.UtcNow > deadline)
                    throw new DestinationUnavailableException($"il disco di destinazione ({_localRoot}) non è più disponibile da {Format.Duration(WaitTimeout)}.");
                await Task.Delay(PollInterval, ct);
            }
            _log.Info("Disco di destinazione di nuovo disponibile: riprendo la copia.");
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
            throw new DeviceUnavailableException(
                $"i trasferimenti si bloccano di continuo (oltre {Format.Number(MaxFailedHealthRounds * StallHealthThreshold)} stalli di fila senza alcun file completato, " +
                "anche se il telefono risponde). Scollega e ricollega il cavo (o riavvia il telefono) e rilancia: i file già copiati non verranno ricopiati. " +
                "Se gli stalli riguardano sempre gli stessi file (vedi Log), valuta di escluderli.");
        _log.Warn($"{stalls} stalli di fila senza file completati: verifico che il telefono risponda (controllo {rounds}/{MaxFailedHealthRounds})...");
        Progress.StatusMessage = "Molti stalli di fila: verifico il collegamento con il telefono...";
        try
        {
            await _device.WaitOnlineAsync(WaitTimeout, m => Progress.StatusMessage = m, ct);
            _log.Info("Il telefono risponde: proseguo.");
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
        _log.Warn("Telefono non raggiungibile: attendo che torni disponibile...");
        Progress.StatusMessage = "Telefono non raggiungibile: in attesa della riconnessione...";
        try
        {
            await _device.WaitOnlineAsync(WaitTimeout, m => Progress.StatusMessage = m, ct);
            _log.Info("Telefono di nuovo disponibile: riprendo la copia.");
        }
        finally
        {
            Progress.StatusMessage = null;
        }
    }

    private static (FailureKind, string) Classify(Exception ex)
    {
        if (IsDiskFull(ex)) return (FailureKind.Fatal, "spazio su disco esaurito sul PC");
        switch (ex)
        {
            case StallException s: return (FailureKind.Transient, s.Message);
            case TransientCopyException t: return (FailureKind.Transient, t.Message);
            case PermanentCopyException p: return (FailureKind.Permanent, p.Message);
            case DeviceUnavailableException d: return (FailureKind.Fatal, d.Message);
            case DestinationUnavailableException d: return (FailureKind.Fatal, d.Message);
            case LocalIoException { InnerException: UnauthorizedAccessException u }:
                return (FailureKind.Permanent, "accesso negato sul PC: " + u.Message);
            case LocalIoException l:
                return (FailureKind.Transient, "errore di scrittura sul PC: " + l.Message);
        }
        string msg = ex.Message;
        if (msg.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return (FailureKind.Permanent, "il file non è più presente sul telefono");
        if (msg.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return (FailureKind.Permanent, "permesso negato sul telefono");
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
