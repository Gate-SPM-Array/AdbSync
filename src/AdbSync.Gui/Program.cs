using AdbSync.Core;

namespace AdbSync.Gui;

internal static class Program
{
    /// <summary>Messaggi della migrazione dalla v0.1.0 (da mettere nel log).</summary>
    internal static IReadOnlyList<string> MigrationMessages { get; private set; } = Array.Empty<string>();
    /// <summary>Messaggi del caricamento della lingua (file mancante, traduzioni scartate...).</summary>
    internal static IReadOnlyList<string> LanguageMessages { get; private set; } = Array.Empty<string>();
    /// <summary>Logger del programma: creato prima della finestra, così anche un errore all'avvio finisce nel SystemLOG.</summary>
    internal static SyncLogger Log { get; private set; } = null!;

    [STAThread]
    private static void Main()
    {
        // Prima di creare qualunque finestra: migrazione delle impostazioni e lingua (i testi dei controlli
        // vengono tradotti quando i controlli sono creati, anche negli inizializzatori dei campi).
        MigrationMessages = AppPaths.MigrateLegacy();
        LanguageMessages = L.Load(AppSettings.Load().Language);
        Log = SyncLogger.ForApplication(AppPaths.LogDirectory, "GUI");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.System(LogLevel.Error, L.F("Errore imprevisto, il programma si chiude: {0}", e.ExceptionObject), notify: false);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.System(LogLevel.Error, L.F("Errore imprevisto in un'attività in background: {0}", e.Exception), notify: false);
            e.SetObserved();
        };
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.System(LogLevel.Error, L.F("Errore imprevisto: {0}", e.Exception), notify: false);
            MessageBox.Show(L.F("Errore imprevisto:\n\n{0}", e.Exception.Message), "AdbSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Log.System(LogLevel.Error, L.F("Errore imprevisto, il programma si chiude: {0}", ex), notify: false);
            throw;
        }
        finally
        {
            Log.System(LogLevel.Info, L.T("AdbSync chiuso."), notify: false);
            Log.Dispose();
        }
    }
}
