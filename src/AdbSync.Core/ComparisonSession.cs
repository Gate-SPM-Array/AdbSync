namespace AdbSync.Core;

/// <summary>
/// Elenco acquisito (scansione di telefono e PC) riutilizzabile: il piano si ricalcola da qui in pochi secondi
/// quando cambiano le opzioni, senza riscansionare. Dopo ogni esecuzione l'elenco del PC viene aggiornato con
/// ciò che il programma ha effettivamente scritto, così resta allineato allo stato reale.
/// </summary>
public sealed class ComparisonSession
{
    public required string DeviceSerial { get; init; }
    public required string RemoteRoot { get; init; }
    public required string LocalRoot { get; init; }
    public required RemoteScanResult Remote { get; init; }
    public required LocalScanResult Local { get; init; }
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public DateTime UpdatedAt { get; private set; } = DateTime.Now;

    /// <summary>File con stessa dimensione verificati come DIVERSI in questa sessione (percorso PC).</summary>
    private readonly HashSet<string> _knownDifferent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    /// <summary>File cambiati sul telefono durante la copia: la versione copiata sostituisce quella della scansione.</summary>
    private readonly Dictionary<string, RemoteFile> _remoteUpdates = new(StringComparer.Ordinal);

    public bool Matches(string serial, string remoteRoot, string localRoot) =>
        serial == DeviceSerial &&
        SyncEngine.NormalizeRemoteRoot(remoteRoot) == RemoteRoot &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalRoot)), StringComparison.OrdinalIgnoreCase);

    public SyncPlan BuildPlan(SyncOptions options)
    {
        lock (_lock)
        {
            IEnumerable<RemoteFile> remoteFiles = _remoteUpdates.Count == 0
                ? Remote.Files
                : Remote.Files.Select(f => _remoteUpdates.TryGetValue(f.RelativePath, out var u) ? u : f);
            var plan = PlanBuilder.Build(RemoteRoot, LocalRoot, remoteFiles, Local, options, Remote.Directories,
                Remote.PermissionDenied, _knownDifferent);
            return new SyncPlan
            {
                RemoteRoot = plan.RemoteRoot,
                LocalRoot = plan.LocalRoot,
                Items = plan.Items,
                Directories = plan.Directories,
                ExcludedCount = plan.ExcludedCount,
                StaleTempFiles = Local.StaleTempFiles.ToList(),
                PermissionDenied = plan.PermissionDenied,
                RemoteFileCount = plan.RemoteFileCount,
                RemoteDirCount = plan.RemoteDirCount,
                LocalFileCount = plan.LocalFileCount,
                LocalDirCount = plan.LocalDirCount,
                ScannedAt = ScannedAt,
            };
        }
    }

    /// <summary>Riporta nell'elenco del PC il risultato di un'esecuzione (file copiati, date allineate, cartelle create).</summary>
    public void ApplyResults(SyncPlan plan)
    {
        lock (_lock)
        {
            // cartelle in cui il programma ha scritto: la loro data sul PC è cambiata (salvo riallineamento successivo)
            var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            static string Parent(string rel) { int k = rel.LastIndexOf('/'); return k < 0 ? "" : rel[..k]; }
            foreach (var i in plan.Items)
            {
                if (i.Remote is null) continue;
                if (i.Status == ItemStatus.Done)
                {
                    long size = i.Action == ItemAction.Copy ? i.Remote.Size : i.Local?.Size ?? i.Remote.Size;
                    Local.Files[i.LocalRelativePath] = new LocalFile(i.LocalRelativePath, size, i.Remote.MtimeUnix, i.Remote.MtimeUnix);
                    if (i.Action == ItemAction.Copy) touchedDirs.Add(Parent(i.LocalRelativePath));
                    if (i.RemoteChangedDuringCopy) _remoteUpdates[i.RelativePath] = i.Remote;
                }
                else if (i.Status == ItemStatus.ContentDiffers)
                {
                    _knownDifferent.Add(i.LocalRelativePath);
                }
            }
            foreach (var d in plan.Directories.Where(d => d.Created)) touchedDirs.Add(Parent(d.LocalRelativePath));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var d in plan.Directories)
            {
                if (d.TimesAligned)
                    Local.Directories[d.LocalRelativePath] = new LocalDir(d.LocalRelativePath, d.Remote.MtimeUnix, d.Remote.MtimeUnix);
                else if (d.Created || touchedDirs.Contains(d.LocalRelativePath))
                {
                    // creata o modificata ma date non riallineate (esecuzione interrotta): il prossimo piano la sistemerà
                    Local.Directories.TryGetValue(d.LocalRelativePath, out var old);
                    Local.Directories[d.LocalRelativePath] = new LocalDir(d.LocalRelativePath, now, old?.CreationUnix ?? now);
                }
            }
            // i temporanei rimasti sono stati eliminati all'inizio dell'esecuzione
            Local.StaleTempFiles.Clear();
            UpdatedAt = DateTime.Now;
        }
    }
}
