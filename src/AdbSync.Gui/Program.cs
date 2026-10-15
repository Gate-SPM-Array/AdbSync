using AdbSync.Core;

namespace AdbSync.Gui;

internal static class Program
{
    /// <summary>Messaggi della migrazione dalla v0.1.0 (da mettere nel log).</summary>
    internal static IReadOnlyList<string> MigrationMessages { get; private set; } = Array.Empty<string>();
    /// <summary>Messaggi del caricamento della lingua (file mancante, traduzioni scartate...).</summary>
    internal static IReadOnlyList<string> LanguageMessages { get; private set; } = Array.Empty<string>();

    [STAThread]
    private static void Main()
    {
        // Prima di creare qualunque finestra: migrazione delle impostazioni e lingua (i testi dei controlli
        // vengono tradotti quando i controlli sono creati, anche negli inizializzatori dei campi).
        MigrationMessages = AppPaths.MigrateLegacy();
        LanguageMessages = L.Load(AppSettings.Load().Language);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(L.F("Errore imprevisto:\n\n{0}", e.Exception.Message), "AdbSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}
