namespace AdbSync.Core;

/// <summary>Istante di modifica: secondi Unix + frazione in unità da 100 ns (precisione NTFS).</summary>
public static class TimeUtil
{
    public static DateTime ToUtc(long unixSeconds, int fraction100ns) =>
        DateTime.UnixEpoch.AddTicks(unixSeconds * TimeSpan.TicksPerSecond + fraction100ns);

    public static bool IsValidUnix(long unixSeconds) => unixSeconds > 0 && unixSeconds < 253402300799;
}

/// <summary>File presente sul telefono (percorso relativo alla cartella sorgente, separatore '/').</summary>
public sealed record RemoteFile(string RelativePath, long Size, long MtimeUnix, int MtimeFraction = 0)
{
    public DateTime MtimeUtc => TimeUtil.ToUtc(MtimeUnix, MtimeFraction);
}

/// <summary>Cartella presente sul telefono ("" = la cartella sorgente stessa).</summary>
public sealed record RemoteDir(string RelativePath, long MtimeUnix, int MtimeFraction = 0)
{
    public DateTime MtimeUtc => TimeUtil.ToUtc(MtimeUnix, MtimeFraction);
}

/// <summary>File presente sul PC (percorso relativo con separatore '/'; il percorso completo si ricava dalla radice).</summary>
public sealed record LocalFile(string RelativePath, long Size, long MtimeUnix, long CreationUnix = 0);

/// <summary>Cartella presente sul PC.</summary>
public sealed record LocalDir(string RelativePath, long MtimeUnix, long CreationUnix);

/// <summary>Allineamento delle date per i file con stesso nome e stessa dimensione ma data diversa.</summary>
public enum DateAlignMode
{
    /// <summary>Non toccare (predefinito).</summary>
    None,
    /// <summary>Stesso nome e dimensione al byte: si allineano le date senza altra verifica.</summary>
    SizeOnly,
    /// <summary>Verifica rapida: MD5 dei primi e degli ultimi 64 KB (esatta per file fino a 128 KB).</summary>
    Quick,
    /// <summary>Verifica completa: MD5 dell'intero file su telefono e PC.</summary>
    Full,
}

public enum ItemCategory
{
    /// <summary>Presente solo sul telefono: da copiare.</summary>
    New,
    /// <summary>Presente su entrambi, il telefono ha una versione più recente: da copiare.</summary>
    Changed,
    /// <summary>Presente su entrambi, la copia sul PC è più recente e diversa: non si tocca (salvo opzione).</summary>
    Conflict,
    /// <summary>Stessa dimensione, data diversa con PC più recente: quasi certamente identico (es. copiato in passato via MTP).</summary>
    DateOnly,
    /// <summary>Identico: nessuna copia.</summary>
    Identical,
    /// <summary>Presente solo sul PC: in modalità Aggiorna non si tocca.</summary>
    OnlyOnPc,
    /// <summary>Due file del telefono finirebbero nello stesso file su Windows (maiuscole/minuscole o caratteri sostituiti).</summary>
    Collision,
    /// <summary>Non copiabile in sicurezza (es. destinazione dentro un collegamento/junction, nome riservato ai temporanei).</summary>
    Blocked,
    /// <summary>Escluso da una regola (vedi <see cref="SyncItem.ExcludedBy"/>): non confrontato e mai toccato.</summary>
    Excluded,
}

public enum ItemAction
{
    Copy,
    Skip,
    /// <summary>Contenuto identico: si allineano solo le date (modifica e creazione = data del telefono).</summary>
    FixTimes,
    /// <summary>Stessa dimensione, data diversa: si verifica il contenuto (rapida o MD5) e, se coincide, si allineano le date.</summary>
    VerifyAndFixTimes,
}

public enum ItemStatus
{
    Pending, Copying, Retrying,
    /// <summary>Andato in stallo: rimesso in fondo alla coda, verrà ritentato dopo gli altri.</summary>
    Requeued,
    Done, Failed, NotProcessed,
    /// <summary>Verifica eseguita: il contenuto sul PC è DIVERSO da quello del telefono (date non toccate).</summary>
    ContentDiffers,
}

public sealed class SyncItem
{
    /// <summary>Percorso relativo sul telefono (o sul PC per gli elementi OnlyOnPc).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Percorso relativo sul PC dopo la conversione dei nomi non validi per Windows.</summary>
    public required string LocalRelativePath { get; init; }

    public RemoteFile? Remote { get; set; }
    /// <summary>Il file è cambiato sul telefono dopo la scansione ed è stata copiata la versione attuale (<see cref="Remote"/> aggiornato).</summary>
    public bool RemoteChangedDuringCopy { get; set; }
    public LocalFile? Local { get; init; }
    public ItemCategory Category { get; set; }
    public ItemAction Action { get; set; }
    public bool NameWasSanitized { get; init; }
    /// <summary>Regola di esclusione che ha scartato l'elemento (stessa istanza di stringa per tutti: nessun costo per file).</summary>
    public string? ExcludedBy { get; init; }

    /// <summary>Motivo della classificazione (per la modalità diagnostica).</summary>
    public string Reason { get; init; } = "";

    private volatile ItemStatus _status = ItemStatus.Pending;
    public ItemStatus Status { get => _status; set => _status = value; }

    private volatile string? _error;
    public string? Error { get => _error; set => _error = value; }

    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    internal int IncrementAttempts() => Interlocked.Increment(ref _attempts);
    internal void DecrementAttempts() => Interlocked.Decrement(ref _attempts);

    /// <summary>Quante volte il trasferimento si è bloccato (stallo).</summary>
    private int _stalls;
    public int Stalls => Volatile.Read(ref _stalls);
    internal void AddStall() => Interlocked.Increment(ref _stalls);

    public long RemoteSize => Remote?.Size ?? 0;
}

/// <summary>Cartella del telefono da ricreare o di cui allineare la data sul PC.</summary>
public sealed class DirItem
{
    public required string RelativePath { get; init; }
    public required string LocalRelativePath { get; init; }
    public required RemoteDir Remote { get; init; }
    public LocalDir? Local { get; init; }
    public bool NeedsCreate { get; init; }
    public bool NeedsTimeFix { get; init; }
    public bool IsRoot => RelativePath.Length == 0;
    /// <summary>Creata in questa esecuzione.</summary>
    public bool Created { get; set; }
    /// <summary>Date impostate (o già corrette) in questa esecuzione.</summary>
    public bool TimesAligned { get; set; }
    public int Depth => RelativePath.Length == 0 ? 0 : RelativePath.Count(c => c == '/') + 1;
    public string? Error { get; set; }
}

public sealed class SyncPlan
{
    public required string RemoteRoot { get; init; }
    public required string LocalRoot { get; init; }
    public required IReadOnlyList<SyncItem> Items { get; init; }
    public IReadOnlyList<DirItem> Directories { get; init; } = Array.Empty<DirItem>();
    /// <summary>File del telefono esclusi dalle regole.</summary>
    public int ExcludedCount { get; init; }
    /// <summary>File presenti solo sul PC che ricadono in una regola di esclusione.</summary>
    public int ExcludedLocalCount { get; init; }
    public IReadOnlyList<string> StaleTempFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PermissionDenied { get; init; } = Array.Empty<string>();
    public DateTime CreatedAt { get; } = DateTime.Now;

    public IEnumerable<SyncItem> ToCopy => Items.Where(i => i.Action == ItemAction.Copy);
    public int CountToCopy => Items.Count(i => i.Action == ItemAction.Copy);
    public long BytesToCopy => Items.Where(i => i.Action == ItemAction.Copy).Sum(i => i.RemoteSize);
    public int CountFileTimeFixes => Items.Count(i => i.Action is ItemAction.FixTimes or ItemAction.VerifyAndFixTimes);
    public int CountToVerify => Items.Count(i => i.Action == ItemAction.VerifyAndFixTimes);
    public int RemoteFileCount { get; init; }
    public int RemoteDirCount { get; init; }
    public int LocalFileCount { get; init; }
    public int LocalDirCount { get; init; }
    /// <summary>Quando è stato acquisito l'elenco su cui si basa il piano.</summary>
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public int CountDirsToCreate => Directories.Count(d => d.NeedsCreate);
    public int CountDirTimeFixes => Directories.Count(d => !d.NeedsCreate && d.NeedsTimeFix);
    public int Count(ItemCategory c) => Items.Count(i => i.Category == c);

    /// <summary>Elementi del telefono che non finiranno sul PC per motivi tecnici (non per scelta).</summary>
    public int NotCopiable => Count(ItemCategory.Collision) + Count(ItemCategory.Blocked) + PermissionDenied.Count;

    /// <summary>Vero se la sincronizzazione ha qualcosa da fare (copie, cartelle o date).</summary>
    public bool HasWork => CountToCopy > 0 || CountFileTimeFixes > 0 || CountDirsToCreate > 0 || CountDirTimeFixes > 0;

    public string WorkSummary
    {
        get
        {
            var parts = new List<string>();
            if (CountToCopy > 0) parts.Add($"{Format.Number(CountToCopy)} file da copiare ({Format.Bytes(BytesToCopy)})");
            if (CountDirsToCreate > 0) parts.Add($"{Format.Number(CountDirsToCreate)} cartelle da creare");
            int verify = CountToVerify;
            int dates = CountFileTimeFixes - verify + CountDirTimeFixes;
            if (dates > 0) parts.Add($"{Format.Number(dates)} date da allineare");
            if (verify > 0) parts.Add($"{Format.Number(verify)} file da verificare e allineare");
            return parts.Count == 0 ? "nulla da fare: la cartella sul PC è già allineata" : string.Join(", ", parts);
        }
    }
}

public sealed class SyncResult
{
    public int Copied { get; init; }
    public int Failed { get; init; }
    public int NotProcessed { get; init; }
    public long BytesCopied { get; init; }
    public int DirsCreated { get; init; }
    /// <summary>Elementi del telefono rimasti fuori dalla copia perché non copiabili (collisioni, bloccati, permesso negato).</summary>
    public int NotCopiable { get; init; }
    /// <summary>Conflitti e "solo data diversa" lasciati invariati sul PC per scelta.</summary>
    public int LeftUntouched { get; init; }
    public int TimesFixed { get; init; }
    /// <summary>File con stessa dimensione verificati e allineati.</summary>
    public int DatesVerifiedAligned { get; init; }
    /// <summary>File con stessa dimensione ma contenuto diverso (date non toccate).</summary>
    public int ContentDiffers { get; init; }
    public int Stalls { get; init; }
    public TimeSpan StallTimeLost { get; init; }
    /// <summary>File copiati al secondo tentativo o oltre.</summary>
    public int RecoveredAfterRetry { get; init; }
    /// <summary>File non copiati perché si bloccavano a ogni tentativo.</summary>
    public IReadOnlyList<string> AlwaysStalled { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MetadataErrors { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
    public string? AbortReason { get; init; }
    public IReadOnlyList<SyncItem> FailedItems { get; init; } = Array.Empty<SyncItem>();
    public string? ReportPath { get; set; }
    /// <summary>File di testo con l'associazione nome originale (telefono) → nome salvato sul PC, se ci sono nomi convertiti.</summary>
    public string? RenamedLogPath { get; set; }
    public int RenamedCount { get; set; }

    /// <summary>Vero solo se TUTTO ciò che c'è sul telefono (salvo scelte esplicite) ora è sul PC.</summary>
    public bool FullySucceeded =>
        Failed == 0 && NotProcessed == 0 && MetadataErrors.Count == 0 && NotCopiable == 0 && !Cancelled && AbortReason is null;

    public string Summary
    {
        get
        {
            string speed = Duration.TotalSeconds > 0.5 && BytesCopied > 0
                ? $" — media {Format.Bytes((long)(BytesCopied / Duration.TotalSeconds))}/s"
                : "";
            string head = $"{Format.Number(Copied)} file copiati ({Format.Bytes(BytesCopied)}) in {Format.Duration(Duration)}{speed}";
            if (DirsCreated > 0) head += $", {Format.Number(DirsCreated)} cartelle create";
            if (TimesFixed > 0) head += $", {Format.Number(TimesFixed)} date allineate";
            if (DatesVerifiedAligned > 0) head += $" (di cui {Format.Number(DatesVerifiedAligned)} dopo verifica del contenuto)";
            head += ".";
            if (ContentDiffers > 0) head += $" {Format.Number(ContentDiffers)} file con stessa dimensione ma contenuto DIVERSO: lasciati invariati (vedi Errori).";
            if (Stalls > 0) head += $" Stalli: {Format.Number(Stalls)} (circa {Format.Duration(StallTimeLost)} di attesa), file riusciti dopo più tentativi: {Format.Number(RecoveredAfterRetry)}.";
            if (LeftUntouched > 0) head += $" {Format.Number(LeftUntouched)} file più recenti sul PC lasciati invariati (conflitti / solo data).";
            if (FullySucceeded) return "COMPLETATO: tutto quanto previsto è stato eseguito e verificato. " + head;
            var parts = new List<string> { head };
            if (NotCopiable > 0) parts.Add($"{Format.Number(NotCopiable)} elementi del telefono NON copiabili (permesso negato, collisioni di nomi o bloccati: vedi Log ed Errori).");
            if (Failed > 0) parts.Add($"{Format.Number(Failed)} file NON copiati (vedi scheda Errori).");
            if (NotProcessed > 0) parts.Add($"{Format.Number(NotProcessed)} file non elaborati.");
            if (MetadataErrors.Count > 0) parts.Add($"{Format.Number(MetadataErrors.Count)} cartelle/date non impostate.");
            if (Cancelled) return "ANNULLATO dall'utente. " + string.Join(" ", parts);
            if (AbortReason is not null) return "INTERROTTO: " + AbortReason + " " + string.Join(" ", parts);
            if (Failed == 0 && NotProcessed == 0 && MetadataErrors.Count == 0)
                return "COMPLETATO CON AVVISI: copiato tutto ciò che era copiabile. " + string.Join(" ", parts);
            return "TERMINATO CON ERRORI. " + string.Join(" ", parts);
        }
    }
}
