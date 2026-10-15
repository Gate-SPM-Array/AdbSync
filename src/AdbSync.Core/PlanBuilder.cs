namespace AdbSync.Core;

/// <summary>Confronto telefono ↔ PC e costruzione del piano (logica alla FreeFileSync, modalità "Aggiorna").</summary>
public static class PlanBuilder
{
    public static SyncPlan Build(
        string remoteRoot,
        string localRoot,
        IEnumerable<RemoteFile> remoteFiles,
        LocalScanResult local,
        SyncOptions options,
        IEnumerable<RemoteDir>? remoteDirs = null,
        IReadOnlyList<string>? permissionDenied = null,
        IReadOnlySet<string>? knownDifferent = null)
    {
        var filter = new PathFilter(options.ExcludePatterns);
        var items = new List<SyncItem>();
        var claimed = new Dictionary<string, SyncItem>(StringComparer.OrdinalIgnoreCase);
        int excluded = 0, excludedLocal = 0;
        var excludedLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in remoteFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (!r.RelativePath.EndsWith(LocalScanner.TempSuffix, StringComparison.OrdinalIgnoreCase) && filter.Match(r.RelativePath) is { } rule)
            {
                // Escluso: resta visibile (categoria "Esclusi") con la regola responsabile, ma non viene mai toccato.
                excluded++;
                var (exRel, exSan) = PathMapper.ToLocalRelative(r.RelativePath);
                local.Files.TryGetValue(exRel, out var exLocal);
                if (exLocal is not null) excludedLocalPaths.Add(exRel);
                items.Add(new SyncItem
                {
                    RelativePath = r.RelativePath, LocalRelativePath = exRel, Remote = r, Local = exLocal,
                    Category = ItemCategory.Excluded, Action = ItemAction.Skip, NameWasSanitized = exSan, ExcludedBy = rule,
                    Reason = options.Diagnostic ? "escluso dalla regola " + rule : "",
                });
                continue;
            }

            var (localRel, sanitized) = PathMapper.ToLocalRelative(r.RelativePath);
            string? blocked = r.RelativePath.EndsWith(LocalScanner.TempSuffix, StringComparison.OrdinalIgnoreCase)
                ? $"il nome termina con \"{LocalScanner.TempSuffix}\", riservato ai file temporanei del programma"
                : UnderReparse(localRel, local.ReparseDirectories) is { } rp
                    ? $"sul PC la cartella \"{rp}\" è un collegamento (junction/symlink): non si scrive fuori dalla destinazione"
                    : null;
            if (blocked is not null)
            {
                items.Add(new SyncItem
                {
                    RelativePath = r.RelativePath, LocalRelativePath = localRel, Remote = r,
                    Category = ItemCategory.Blocked, Action = ItemAction.Skip, NameWasSanitized = sanitized,
                    Reason = "non copiabile: " + blocked, Error = "Non copiabile: " + blocked,
                });
                continue;
            }
            if (claimed.TryGetValue(localRel, out var first))
            {
                items.Add(new SyncItem
                {
                    RelativePath = r.RelativePath,
                    LocalRelativePath = localRel,
                    Remote = r,
                    Category = ItemCategory.Collision,
                    Action = ItemAction.Skip,
                    NameWasSanitized = sanitized,
                    Reason = "collisione di nomi su Windows",
                    Error = $"Su Windows coincide con \"{first.RelativePath}\" (maiuscole/minuscole o caratteri non ammessi)",
                });
                continue;
            }

            local.Files.TryGetValue(localRel, out var lf);
            // la motivazione testuale serve solo alla diagnostica: con 1.000.000 di file costerebbe centinaia di MB
            var (category, reason) = ClassifyWithReason(r, lf, options, withReason: options.Diagnostic);
            var action = category switch
            {
                ItemCategory.New or ItemCategory.Changed => ItemAction.Copy,
                ItemCategory.Conflict when options.OverwriteConflicts => ItemAction.Copy,
                // Solo se anche la data di modifica coincide: in modalità "solo dimensione" un file con data
                // diversa potrebbe essere stato modificato sul PC e non va "ritoccato".
                ItemCategory.Identical when lf is not null && TimesEqual(lf.MtimeUnix, r.MtimeUnix, options)
                                            && !TimesEqual(lf.CreationUnix, r.MtimeUnix, options) => ItemAction.FixTimes,
                ItemCategory.DateOnly when options.AlignSameSizeDates == DateAlignMode.SizeOnly => ItemAction.FixTimes,
                ItemCategory.DateOnly when options.AlignSameSizeDates is DateAlignMode.Quick or DateAlignMode.Full => ItemAction.VerifyAndFixTimes,
                _ => ItemAction.Skip,
            };
            bool knownDiff = false;
            if (category == ItemCategory.DateOnly && knownDifferent?.Contains(localRel) == true)
            {
                // già verificato in questa sessione: il contenuto è diverso → non si ritocca e non si riverifica
                category = ItemCategory.Conflict;
                action = options.OverwriteConflicts ? ItemAction.Copy : ItemAction.Skip;
                reason = "stessa dimensione ma contenuto diverso (verificato): " + reason;
                knownDiff = true;
            }
            if (action == ItemAction.FixTimes && options.Diagnostic) reason += "; date da allineare a quelle del telefono";
            var item = new SyncItem
            {
                RelativePath = r.RelativePath,
                LocalRelativePath = localRel,
                Remote = r,
                Local = lf,
                Category = category,
                NameWasSanitized = sanitized,
                Action = action,
                Reason = reason,
                Error = knownDiff ? "Contenuto diverso dal telefono (verificato): date non modificate" : null,
            };
            claimed[localRel] = item;
            items.Add(item);
        }

        foreach (var lf in local.Files.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (claimed.ContainsKey(lf.RelativePath) || excludedLocalPaths.Contains(lf.RelativePath)) continue;
            if (filter.Match(lf.RelativePath) is { } localRule)
            {
                excludedLocal++;
                items.Add(new SyncItem
                {
                    RelativePath = lf.RelativePath, LocalRelativePath = lf.RelativePath, Local = lf,
                    Category = ItemCategory.Excluded, Action = ItemAction.Skip, ExcludedBy = localRule,
                    Reason = options.Diagnostic ? "solo sul PC, escluso dalla regola " + localRule : "",
                });
                continue;
            }
            items.Add(new SyncItem
            {
                RelativePath = lf.RelativePath,
                LocalRelativePath = lf.RelativePath,
                Local = lf,
                Category = ItemCategory.OnlyOnPc,
                Action = ItemAction.Skip,
                Reason = "presente solo sul PC (mai cancellato)",
            });
        }

        var dirs = BuildDirectories(localRoot, remoteDirs, local, filter, options);

        return new SyncPlan
        {
            RemoteRoot = remoteRoot,
            LocalRoot = localRoot,
            Items = items,
            Directories = dirs,
            RemoteFileCount = items.Count(i => i.Remote is not null),
            RemoteDirCount = remoteDirs?.Count(d => d.RelativePath.Length > 0) ?? 0,
            LocalFileCount = local.Files.Count,
            LocalDirCount = local.Directories.Count(d => d.Key.Length > 0),
            ExcludedCount = excluded,
            ExcludedLocalCount = excludedLocal,
            StaleTempFiles = local.StaleTempFiles,
            PermissionDenied = permissionDenied ?? Array.Empty<string>(),
        };
    }

    private static List<DirItem> BuildDirectories(string localRoot, IEnumerable<RemoteDir>? remoteDirs, LocalScanResult local, PathFilter filter, SyncOptions o)
    {
        var result = new List<DirItem>();
        if (remoteDirs is null) return result;
        bool rootIsDrive = IsDriveRoot(localRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in remoteDirs.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
        {
            if (d.RelativePath.Length == 0 && rootIsDrive) continue; // non si cambia la data della radice di un'unità
            if (d.RelativePath.Length > 0 && filter.IsExcluded(d.RelativePath + "/\0")) continue;
            var (localRel, _) = d.RelativePath.Length == 0 ? ("", false) : PathMapper.ToLocalRelative(d.RelativePath);
            if (localRel.Length > 0 && (local.ReparseDirectories.Contains(localRel) || UnderReparse(localRel, local.ReparseDirectories) is not null))
                continue; // mai toccare cartelle che sono collegamenti, né il loro contenuto
            if (!seen.Add(localRel)) continue;
            local.Directories.TryGetValue(localRel, out var ld);
            bool needsCreate = ld is null;
            bool needsFix = ld is null || !TimesEqual(ld.MtimeUnix, d.MtimeUnix, o) || !TimesEqual(ld.CreationUnix, d.MtimeUnix, o);
            result.Add(new DirItem
            {
                RelativePath = d.RelativePath,
                LocalRelativePath = localRel,
                Remote = d,
                Local = ld,
                NeedsCreate = needsCreate,
                NeedsTimeFix = needsFix,
            });
        }
        return result;
    }

    /// <summary>Restituisce la cartella-collegamento che contiene il percorso, se esiste.</summary>
    private static string? UnderReparse(string localRel, HashSet<string> reparse)
    {
        if (reparse.Count == 0) return null;
        int idx = -1;
        while ((idx = localRel.IndexOf('/', idx + 1)) > 0)
        {
            string prefix = localRel[..idx];
            if (reparse.Contains(prefix)) return prefix;
        }
        return null;
    }

    internal static bool IsDriveRoot(string path)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string? root = Path.GetPathRoot(full);
            return root is not null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool TimesEqual(long a, long b, SyncOptions o)
    {
        long diff = a - b;
        int tol = Math.Max(0, o.TimeToleranceSeconds);
        return Math.Abs(diff) <= tol || (o.IgnoreDstShift && Math.Abs(Math.Abs(diff) - 3600) <= tol);
    }

    public static ItemCategory Classify(RemoteFile r, LocalFile? lf, SyncOptions o) => ClassifyWithReason(r, lf, o).Category;

    public static (ItemCategory Category, string Reason) ClassifyWithReason(RemoteFile r, LocalFile? lf, SyncOptions o, bool withReason = true)
    {
        if (lf is null) return (ItemCategory.New, "presente solo sul telefono");

        long diff = lf.MtimeUnix - r.MtimeUnix; // > 0: PC più recente
        bool timeEqual = TimesEqual(lf.MtimeUnix, r.MtimeUnix, o);
        bool sizeEqual = lf.Size == r.Size;
        if (!withReason)
        {
            if (sizeEqual && (timeEqual || o.CompareMode == CompareMode.SizeOnly)) return (ItemCategory.Identical, "");
            if (!timeEqual && diff < 0) return (ItemCategory.Changed, "");
            if (!sizeEqual && timeEqual) return (ItemCategory.Changed, "");
            return (sizeEqual ? ItemCategory.DateOnly : ItemCategory.Conflict, "");
        }
        string facts = $"dimensione telefono {r.Size} / PC {lf.Size}, data PC − telefono = {diff} s";

        if (sizeEqual && (timeEqual || o.CompareMode == CompareMode.SizeOnly))
            return (ItemCategory.Identical, $"identico ({facts}{(o.CompareMode == CompareMode.SizeOnly && !timeEqual ? ", confronto solo dimensione" : "")})");
        if (!timeEqual && diff < 0) return (ItemCategory.Changed, $"telefono più recente ({facts})");
        if (!sizeEqual && timeEqual) return (ItemCategory.Changed, $"stessa data ma dimensione diversa: vale il telefono ({facts})");
        if (sizeEqual) return (ItemCategory.DateOnly, $"stessa dimensione, PC più recente ({facts})");
        return (ItemCategory.Conflict, $"dimensione diversa e PC più recente ({facts})");
    }
}
