namespace AdbSync.Core;

public enum CompareMode
{
    /// <summary>Dimensione e data di modifica (come FreeFileSync, predefinito).</summary>
    SizeAndDate,
    /// <summary>Solo dimensione: utile se i file sul PC sono stati copiati in passato senza preservare la data.</summary>
    SizeOnly,
}

public sealed class SyncOptions
{
    public static readonly string[] DefaultExcludes =
    {
        ".thumbnails/",   // miniature generate da Android
        ".trashed-*",     // file nel cestino di Android 11+
        ".pending-*",     // file in fase di scrittura (MediaStore)
        "*.adbsync-part", // file temporanei di questo programma
    };

    public CompareMode CompareMode { get; set; } = CompareMode.SizeAndDate;

    /// <summary>Tolleranza sulla data (secondi). 2 s copre la granularità di FAT/exFAT.</summary>
    public int TimeToleranceSeconds { get; set; } = 2;

    /// <summary>Considera uguali date che differiscono esattamente di un'ora (ora legale su dischi FAT/exFAT).</summary>
    public bool IgnoreDstShift { get; set; } = true;

    /// <summary>Se vero, i conflitti (PC più recente e diverso) vengono sovrascritti con la versione del telefono.</summary>
    public bool OverwriteConflicts { get; set; }

    public List<string> ExcludePatterns { get; set; } = new(DefaultExcludes);

    /// <summary>Numero di trasferimenti contemporanei (1-4).</summary>
    public int ParallelTransfers { get; set; } = 2;

    /// <summary>
    /// Se per questo numero di secondi (minimo 1) non arriva alcun byte, il trasferimento è considerato bloccato:
    /// il file viene rimesso in fondo alla coda e ritentato dopo gli altri.
    /// </summary>
    public int StallTimeoutSeconds { get; set; } = 30;

    /// <summary>File con stessa dimensione e data diversa ("solo data diversa"): se e come allinearne le date.</summary>
    public DateAlignMode AlignSameSizeDates { get; set; } = DateAlignMode.None;

    /// <summary>Tentativi aggiuntivi per file dopo il primo.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>Quanto attendere la riconnessione del telefono prima di interrompere tutto.</summary>
    public int DeviceWaitTimeoutSeconds { get; set; } = 300;

    /// <summary>Verifica ogni file copiato confrontando l'MD5 calcolato sul telefono con quello sul PC (più lento).</summary>
    public bool VerifyHash { get; set; }

    /// <summary>Usa la versione 2 del protocollo di trasferimento ADB (come "adb pull"); se il telefono non la supporta si torna alla v1.</summary>
    public bool UseSyncV2 { get; set; }

    /// <summary>Salva gli elenchi completi file per file di ogni confronto nella cartella dei log.</summary>
    public bool Diagnostic { get; set; }

    internal int ClampedParallel => Math.Clamp(ParallelTransfers, 1, 4);
}
