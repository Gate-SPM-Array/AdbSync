using System.IO.Compression;
using System.Runtime.InteropServices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace AdbSync.Core.Adb;

public sealed record DeviceInfo(string Serial, string Model, string State, bool IsOnline)
{
    public override string ToString() =>
        IsOnline ? $"{Model} ({Serial})" : $"{Model} ({Serial}) — {StateLabel}";

    public string StateLabel => State switch
    {
        "Online" => L.T("connesso"),
        "Unauthorized" => L.T("NON AUTORIZZATO: conferma il debug USB sul telefono"),
        "Offline" => L.T("offline"),
        "Authorizing" => L.T("in autorizzazione"),
        "NoPermissions" => L.T("permessi insufficienti"),
        _ => State,
    };
}

/// <summary>Individua adb, avvia il server adb e fornisce l'elenco dei dispositivi.</summary>
public sealed class AdbEnvironment
{
    public string AdbPath { get; }
    public AdbClient Client { get; } = new();

    private AdbEnvironment(string adbPath) => AdbPath = adbPath;

    private static string AdbExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "adb.exe" : "adb";

    /// <summary>platform-tools gestite dal programma: nella cartella del programma (portable).</summary>
    public static string PrivatePlatformToolsDir => AppPaths.PlatformToolsDirectory;

    /// <summary>Cerca adb: percorso configurato, cartella del programma, copia scaricata, SDK Android, PATH.</summary>
    public static string? Locate(string? configuredPath)
    {
        // Prima quelle nella cartella del programma (portable), poi il percorso scelto dall'utente, poi il sistema.
        var candidates = new List<string?>
        {
            Path.Combine(PrivatePlatformToolsDir, AdbExeName),
            Path.Combine(AppContext.BaseDirectory, AdbExeName),
        };
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
            candidates.Add(Path.Combine(configuredPath, AdbExeName));
        }
        candidates.Add(Path.Combine(AppPaths.LegacyLocalDirectory, "platform-tools", AdbExeName));
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(v)) candidates.Add(Path.Combine(v, "platform-tools", AdbExeName));
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", AdbExeName));
        candidates.Add(@"C:\platform-tools\" + AdbExeName);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), AdbExeName));
        }
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>Scarica le platform-tools ufficiali Google nella cartella dati dell'utente.</summary>
    public static async Task<string> DownloadPlatformToolsAsync(IProgress<string>? status, CancellationToken ct)
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                  : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "linux";
        string url = $"https://dl.google.com/android/repository/platform-tools-latest-{os}.zip";
        string baseDir = Path.GetDirectoryName(PrivatePlatformToolsDir)!;
        Directory.CreateDirectory(baseDir);
        string zip = Path.Combine(baseDir, "platform-tools.zip");

        status?.Report(L.F("Download di {0}", url));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        await using (var src = await http.GetStreamAsync(url, ct))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst, ct);

        status?.Report(L.T("Estrazione..."));
        string existing = Path.Combine(PrivatePlatformToolsDir, AdbExeName);
        if (File.Exists(existing)) KillServer(existing); // adb in esecuzione blocca i file da sostituire
        if (Directory.Exists(PrivatePlatformToolsDir)) Directory.Delete(PrivatePlatformToolsDir, recursive: true);
        ZipFile.ExtractToDirectory(zip, baseDir, overwriteFiles: true); // lo zip contiene la cartella platform-tools/
        File.Delete(zip);

        string adb = Path.Combine(PrivatePlatformToolsDir, AdbExeName);
        if (!File.Exists(adb)) throw new FileNotFoundException(L.T("adb non trovato nello zip scaricato"), adb);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(adb, File.GetUnixFileMode(adb) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        status?.Report(L.F("platform-tools installate in {0}", PrivatePlatformToolsDir));
        return adb;
    }

    /// <summary>Avvia (se necessario) il server adb e restituisce l'ambiente pronto.</summary>
    public static async Task<AdbEnvironment> StartAsync(string adbPath, CancellationToken ct)
    {
        var env = new AdbEnvironment(adbPath);
        await env.EnsureServerAsync(ct);
        return env;
    }

    public async Task EnsureServerAsync(CancellationToken ct)
    {
        try
        {
            await AdbServer.Instance.StartServerAsync(AdbPath, restartServerIfNewer: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(L.F("Impossibile avviare il server adb ({0}): {1}", AdbPath, ex.Message), ex);
        }
    }

    /// <summary>Arresta il server adb (libera i file della cartella platform-tools).</summary>
    public static void KillServer(string adbPath)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(adbPath, "kill-server")
            {
                CreateNoWindow = true, UseShellExecute = false,
            });
            p?.WaitForExit(10_000);
        }
        catch { }
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        var list = await Client.GetDevicesAsync(ct);
        return list.Select(ToInfo).ToList();
    }

    internal async Task<DeviceData?> FindDeviceDataAsync(string serial, CancellationToken ct)
    {
        var list = await Client.GetDevicesAsync(ct);
        foreach (var d in list)
            if (d.Serial == serial) return d;
        return null;
    }

    internal static DeviceInfo ToInfo(DeviceData d)
    {
        string model = !string.IsNullOrWhiteSpace(d.Model) ? d.Model.Replace('_', ' ')
                     : !string.IsNullOrWhiteSpace(d.Product) ? d.Product : L.T("Dispositivo");
        return new DeviceInfo(d.Serial, model, d.State.ToString(), d.State == DeviceState.Online);
    }

    public AdbRemoteDevice OpenDevice(string serial, string displayName) => new(this, serial, displayName);
}
