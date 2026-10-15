namespace AdbSync.Core;

/// <summary>
/// Percorsi dei dati del programma. Modalità portable: tutto accanto all'eseguibile
/// (data\, logs\, platform-tools\). Se la cartella non è scrivibile (es. Program Files)
/// si ripiega su %LOCALAPPDATA%\AdbSync.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory { get; }
    public static bool IsPortable { get; }
    public static string DataDirectory { get; }
    public static string LogDirectory { get; }
    public static string PlatformToolsDirectory { get; }
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Percorsi usati dalla v0.1.0 (non portable), usati solo per la migrazione.</summary>
    public static string LegacyLocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbSync");
    public static string LegacySettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdbSync", "settings.json");

    static AppPaths()
    {
        BaseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        IsPortable = CanWrite(BaseDirectory);
        string root = IsPortable ? BaseDirectory : LegacyLocalDirectory;
        DataDirectory = Path.Combine(root, "data");
        LogDirectory = Path.Combine(root, "logs");
        PlatformToolsDirectory = Path.Combine(root, "platform-tools");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string data = Path.Combine(dir, "data");
            Directory.CreateDirectory(data);
            string probe = Path.Combine(data, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prima esecuzione in modalità portable: porta nella cartella del programma impostazioni e
    /// platform-tools della v0.1.0, se presenti. Restituisce i messaggi da mettere nel log.
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacy()
    {
        var msgs = new List<string>();
        if (!IsPortable) return msgs;
        try
        {
            if (!File.Exists(SettingsFile) && File.Exists(LegacySettingsFile))
            {
                Directory.CreateDirectory(DataDirectory);
                File.Copy(LegacySettingsFile, SettingsFile);
                msgs.Add("Impostazioni importate da " + LegacySettingsFile);
            }
        }
        catch (Exception ex) { msgs.Add("Importazione impostazioni non riuscita: " + ex.Message); }

        try
        {
            string legacyTools = Path.Combine(LegacyLocalDirectory, "platform-tools");
            string adbName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            if (!File.Exists(Path.Combine(PlatformToolsDirectory, adbName)) && File.Exists(Path.Combine(legacyTools, adbName)))
            {
                CopyDirectory(legacyTools, PlatformToolsDirectory);
                msgs.Add($"platform-tools copiate da {legacyTools} nella cartella del programma.");
            }
        }
        catch (Exception ex) { msgs.Add("Copia platform-tools non riuscita (verranno usate quelle esistenti): " + ex.Message); }
        return msgs;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
