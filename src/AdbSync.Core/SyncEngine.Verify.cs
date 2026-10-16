using System.Diagnostics;
using System.Text;

namespace AdbSync.Core;

/// <summary>Esito della verifica del backup (confronto degli hash telefono ↔ PC sui file già presenti su entrambi).</summary>
public sealed class VerifyResult
{
    public bool Quick { get; init; }
    public int Total { get; init; }
    public int Identical { get; init; }
    public int Different { get; init; }
    /// <summary>File non leggibili sul telefono o sul PC: non verificati (mai contati come identici).</summary>
    public int NotVerifiable { get; init; }
    public long Bytes { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
    public string? AbortReason { get; init; }
    public string? ReportPath { get; set; }
    public IReadOnlyList<SyncItem> DifferentItems { get; init; } = Array.Empty<SyncItem>();
    /// <summary>File del telefono che sul PC mancano o hanno dimensione/data diverse: non verificati, il backup non è completo.</summary>
    public int NotInBackup { get; init; }
    /// <summary>Elementi del telefono non copiabili (permesso negato, collisioni, bloccati).</summary>
    public int NotCopiable { get; init; }

    public int Checked => Identical + Different + NotVerifiable;
    /// <summary>Tutti i file verificati coincidono (rapida: inizio e fine).</summary>
    public bool AllIdentical => Different == 0 && NotVerifiable == 0 && Checked == Total && !Cancelled && AbortReason is null;
    /// <summary>Verifica superata e nessun file del telefono fuori dal backup.</summary>
    public bool Complete => AllIdentical && NotInBackup == 0 && NotCopiable == 0;

    public string Summary
    {
        get
        {
            string kind = Quick ? L.T("verifica rapida: primi e ultimi 64 KB di ogni file") : L.T("MD5 completo");
            string counts = Quick
                ? L.F("{0} file con inizio e fine uguali, {1} DIVERSI, {2} non verificabili su {3} ({4}, {5}, {6}).",
                    Format.Number(Identical), Format.Number(Different), Format.Number(NotVerifiable), Format.Number(Total),
                    Format.Bytes(Bytes), kind, Format.Duration(Duration))
                : L.F("{0} file identici, {1} DIVERSI, {2} non verificabili su {3} ({4}, {5}, {6}).",
                    Format.Number(Identical), Format.Number(Different), Format.Number(NotVerifiable), Format.Number(Total),
                    Format.Bytes(Bytes), kind, Format.Duration(Duration));
            string outside = (NotInBackup > 0
                    ? " " + L.F("Il backup però NON è completo: {0} file del telefono mancano sul PC o hanno dimensione o data diverse (non verificati: usa Confronta e Sincronizza).", Format.Number(NotInBackup))
                    : "")
                + (NotCopiable > 0 ? " " + L.F("{0} elementi del telefono non sono copiabili (vedi Errori).", Format.Number(NotCopiable)) : "");
            if (Total == 0) return L.T("Nessun file da verificare: non ci sono file uguali per nome, dimensione e data su telefono e PC.") + outside;
            if (Cancelled) return L.F("VERIFICA ANNULLATA dall'utente. {0}", counts) + outside;
            if (AbortReason is not null) return L.F("VERIFICA INTERROTTA: {0} {1}", AbortReason, counts) + outside;
            if (AllIdentical)
                return (Quick
                    ? L.F("VERIFICA RAPIDA SUPERATA: in tutti i {0} file presenti su telefono e PC i primi e gli ultimi 64 KB coincidono ({1}, {2}). Per controllare l'intero contenuto usa la verifica completa.",
                        Format.Number(Total), Format.Bytes(Bytes), Format.Duration(Duration))
                    : L.F("VERIFICA SUPERATA: tutti i {0} file presenti su telefono e PC sono identici ({1}, {2}, {3}).",
                        Format.Number(Total), Format.Bytes(Bytes), kind, Format.Duration(Duration))) + outside;
            return L.F("VERIFICA CON DIFFERENZE: {0}", counts) + outside;
        }
    }
}

public sealed partial class SyncEngine
{
    /// <summary>
    /// Verifica del backup sull'elenco già acquisito, senza nuova scansione: per ogni file uguale per nome, dimensione e data
    /// su telefono e PC confronta l'hash calcolato sul telefono (a blocchi, un comando per blocco) con quello del PC.
    /// Non modifica nulla: i file diversi vengono segnalati (ricopiarli è una scelta esplicita dell'utente).
    /// </summary>
    public async Task<VerifyResult> VerifyBackupAsync(SyncPlan plan, bool quick, CancellationToken userCt)
    {
        var sw = Stopwatch.StartNew();
        var todo = plan.Items.Where(i => i.Category == ItemCategory.Identical && i.Remote is not null && i.Local is not null).ToList();
        long HashedBytes(SyncItem i) => quick ? Math.Min(i.RemoteSize, 2L * QuickHashWindow) : i.RemoteSize;
        foreach (var i in todo) { i.Status = ItemStatus.Pending; i.Error = null; }
        Progress.TotalFiles = todo.Count;
        Progress.TotalBytes = todo.Sum(HashedBytes);
        Progress.StartedAt = DateTime.UtcNow;
        Progress.Phase = L.T("Verifica del backup");
        _abortCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = _abortCts.Token;
        string kind = quick ? L.T("verifica rapida (testa e coda)") : L.T("verifica MD5 completa");
        _log.Info(L.F("Verifica del backup: {0} file presenti su telefono e PC ({1}), {2}.", Format.Number(todo.Count), Format.Bytes(todo.Sum(i => i.RemoteSize)), kind));

        int identical = 0, different = 0, notVerifiable = 0;
        var differentItems = new List<SyncItem>();
        string? abort = null;
        StreamWriter? report = null;
        string? reportPath = null;
        if (_log.LogDirectory is not null && todo.Count > 0)
        {
            try
            {
                reportPath = Path.Combine(_log.LogDirectory, $"verifica_backup_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                report = new StreamWriter(reportPath, false, new UTF8Encoding(true));
                report.WriteLine(quick
                    ? "Percorso telefono;Percorso PC;Dimensione;Hash rapido telefono;Hash rapido PC;Esito"
                    : "Percorso telefono;Percorso PC;Dimensione;MD5 telefono;MD5 PC;Esito");
            }
            catch (Exception ex)
            {
                _log.Warn(L.F("Impossibile scrivere il report: {0}", ex.Message));
                report = null;
                reportPath = null;
            }
        }

        try
        {
            EnsureLocalDriveAvailable(plan.LocalRoot);
            int done = 0;
            foreach (var batch in HashBatches(todo, quick))
            {
                ct.ThrowIfCancellationRequested();
                Progress.Phase = L.F("Verifica del backup: {0} di {1}", Format.Number(done), Format.Number(todo.Count));
                var remoteHashes = await RemoteHashesAsync(plan.RemoteRoot, batch, quick, ct);
                var localHashes = await LocalHashesAsync(plan.LocalRoot, batch, quick, ct);
                foreach (var i in batch)
                {
                    done++;
                    string? rh = ValidRemoteHash(remoteHashes, i, quick);
                    localHashes.TryGetValue(i, out var lh);
                    string outcome;
                    if (rh is null || string.IsNullOrEmpty(lh))
                    {
                        i.Status = ItemStatus.Failed;
                        i.Error = L.F("verifica non eseguita: {0}", rh is null ? L.T("file non leggibile sul telefono") : L.T("file non leggibile sul PC"));
                        notVerifiable++;
                        outcome = "non verificabile";
                    }
                    else if (!string.Equals(rh, lh, StringComparison.OrdinalIgnoreCase))
                    {
                        i.Status = ItemStatus.ContentDiffers;
                        i.Error = L.F("contenuto DIVERSO dal telefono ({0}): la copia sul PC non corrisponde", kind);
                        different++;
                        differentItems.Add(i);
                        outcome = "DIVERSO";
                        if (different <= 200) _log.Warn(L.F("Contenuto diverso dal telefono: {0}", i.LocalRelativePath));
                    }
                    else
                    {
                        i.Status = ItemStatus.Verified;
                        identical++;
                        outcome = "identico";
                    }
                    Progress.FileDone(HashedBytes(i));
                    report?.WriteLine(string.Join(';', Csv(i.RelativePath), Csv(i.LocalRelativePath), i.RemoteSize, rh ?? "", lh ?? "", outcome));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is DeviceUnavailableException or DestinationUnavailableException)
        {
            abort = ex.Message;
            _log.Error(L.F("Interruzione del lavoro: {0}", ex.Message));
        }
        catch (Exception ex)
        {
            abort = L.F("errore imprevisto durante la verifica: {0}", ex.Message);
            _log.Error(ex.ToString());
        }

        bool cancelled = userCt.IsCancellationRequested;
        foreach (var i in todo.Where(i => i.Status == ItemStatus.Pending))
        {
            i.Status = ItemStatus.NotProcessed;
            i.Error = cancelled ? L.T("Annullato: contenuto non verificato") : L.T("Non elaborato (lavoro interrotto): contenuto non verificato");
        }
        var result = new VerifyResult
        {
            Quick = quick,
            Total = todo.Count,
            Identical = identical,
            Different = different,
            NotVerifiable = notVerifiable,
            Bytes = todo.Sum(i => i.RemoteSize),
            Duration = sw.Elapsed,
            Cancelled = cancelled,
            AbortReason = cancelled ? null : abort,
            DifferentItems = differentItems,
            ReportPath = reportPath,
            NotInBackup = plan.Items.Count(i => i.Remote is not null && i.Category is ItemCategory.New or ItemCategory.Changed or ItemCategory.Conflict or ItemCategory.DateOnly),
            NotCopiable = plan.NotCopiable,
        };
        Progress.Phase = result.AllIdentical ? L.T("Completato") : cancelled ? L.T("Annullato") : result.AbortReason is not null ? L.T("Interrotto") : L.T("Completato con errori");
        Progress.StatusMessage = null;
        Progress.FinishedAt = DateTime.UtcNow;
        if (report is not null)
        {
            try
            {
                report.WriteLine();
                report.WriteLine(Csv(result.Summary));
                report.Dispose();
            }
            catch { }
        }
        _log.Write(result.Complete ? LogLevel.Info : result.AllIdentical ? LogLevel.Warning : LogLevel.Error, result.Summary);
        if (different > 200) _log.Warn(L.F("  ... e altri {0} file diversi (elenco completo nel report).", Format.Number(different - 200)));
        if (reportPath is not null) _log.Info(L.F("Report dettagliato: {0}", reportPath));
        return result;
    }
}
