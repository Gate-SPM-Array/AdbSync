using System.Text.Json;
using AdbSync.Core;

namespace AdbSync.Gui;

internal sealed class AppSettings
{
    public string? AdbPath { get; set; }
    public string? LastSerial { get; set; }
    public string RemotePath { get; set; } = "/sdcard/DCIM";
    public string LocalPath { get; set; } = "";
    public bool SizeOnly { get; set; }
    public bool IgnoreDst { get; set; } = true;
    public bool VerifyHash { get; set; }
    public bool OverwriteConflicts { get; set; }
    public int Parallel { get; set; } = 2;
    public int StallSeconds { get; set; } = 30;
    /// <summary>Limite di una singola scrittura sul disco del PC (minimo 30 s), impostato dal menu Strumenti.</summary>
    public int DiskStallSeconds { get; set; } = 120;
    public int Retries { get; set; } = 5;
    public int WaitDeviceSeconds { get; set; } = 300;
    public List<string> Excludes { get; set; } = new(SyncOptions.DefaultExcludes);
    public bool ShowDetailedLog { get; set; }

    public bool Diagnostic { get; set; }
    /// <summary>Protocollo di trasferimento ADB v2 (come "adb pull").</summary>
    public bool SyncV2 { get; set; }
    /// <summary>Lingua dell'interfaccia: "it" (incorporata) o il codice di un file lang\&lt;codice&gt;.json.</summary>
    public string Language { get; set; } = L.DefaultCode;
    public DateAlignMode AlignSameSizeDates { get; set; } = DateAlignMode.None;
    /// <summary>Posizione del divisore tra le schede e il riquadro Stato (0 = automatica).</summary>
    public int SplitterDistance { get; set; }

    /// <summary>Portable: data\settings.json accanto all'eseguibile (vedi AppPaths).</summary>
    private static string FilePath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* impostazioni corrotte: si riparte dai valori predefiniti */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public SyncOptions ToOptions()
    {
        var o = new SyncOptions
        {
            CompareMode = SizeOnly ? CompareMode.SizeOnly : CompareMode.SizeAndDate,
            IgnoreDstShift = IgnoreDst,
            VerifyHash = VerifyHash,
            OverwriteConflicts = OverwriteConflicts,
            ParallelTransfers = Parallel,
            UseSyncV2 = SyncV2,
            StallTimeoutSeconds = StallSeconds,
            DiskStallTimeoutSeconds = Math.Max(SyncOptions.MinDiskStallSeconds, DiskStallSeconds),
            MaxRetries = Retries,
            DeviceWaitTimeoutSeconds = WaitDeviceSeconds,
            Diagnostic = Diagnostic,
            AlignSameSizeDates = AlignSameSizeDates,
            ExcludePatterns = new List<string>(Excludes),
        };
        if (!o.ExcludePatterns.Contains("*" + LocalScanner.TempSuffix)) o.ExcludePatterns.Add("*" + LocalScanner.TempSuffix);
        return o;
    }
}
