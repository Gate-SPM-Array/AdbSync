using System.Globalization;
using System.Text;

namespace AdbSync.Core;

/// <summary>
/// Modalità diagnostica: salva, per ogni confronto, gli elenchi completi file per file
/// (telefono, PC, piano con motivazione, messaggi grezzi del telefono) nella cartella dei log.
/// </summary>
public static class Diagnostics
{
    public static string? WriteCompare(string? logDirectory, string remoteRoot, string localRoot,
        RemoteScanResult remote, LocalScanResult local, SyncPlan? plan, SyncLogger log)
    {
        if (logDirectory is null) return null;
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(logDirectory, $"diagnostica_{stamp}");
            Directory.CreateDirectory(dir);
            var utf8 = new UTF8Encoding(true); // BOM: Excel riconosce l'UTF-8

            using (var w = new StreamWriter(Path.Combine(dir, "1_telefono.tsv"), false, utf8))
            {
                w.WriteLine($"# telefono: {remoteRoot}  —  elementi dichiarati {remote.DeclaredEntries}, ricevuti {remote.ReceivedEntries}, file {remote.Files.Count}, cartelle {remote.Directories.Count}");
                w.WriteLine("tipo\tpercorso\tdimensione\tdata_modifica_utc");
                foreach (var d in remote.Directories.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
                    w.WriteLine($"cartella\t{(d.RelativePath.Length == 0 ? "." : d.RelativePath)}\t\t{Iso(d.MtimeUtc)}");
                foreach (var f in remote.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                    w.WriteLine($"file\t{f.RelativePath}\t{f.Size}\t{Iso(f.MtimeUtc)}");
            }

            using (var w = new StreamWriter(Path.Combine(dir, "2_pc.tsv"), false, utf8))
            {
                w.WriteLine($"# PC: {localRoot}  —  file {local.Files.Count}, cartelle {local.Directories.Count}, temporanei {local.StaleTempFiles.Count}");
                w.WriteLine("tipo\tpercorso\tdimensione\tdata_modifica_utc\tdata_creazione_utc");
                foreach (var d in local.Directories.Values.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
                    w.WriteLine($"cartella\t{(d.RelativePath.Length == 0 ? "." : d.RelativePath)}\t\t{IsoUnix(d.MtimeUnix)}\t{IsoUnix(d.CreationUnix)}");
                foreach (var f in local.Files.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                    w.WriteLine($"file\t{f.RelativePath}\t{f.Size}\t{IsoUnix(f.MtimeUnix)}\t{IsoUnix(f.CreationUnix)}");
                foreach (var t in local.StaleTempFiles) w.WriteLine($"temporaneo\t{t}\t\t\t");
            }

            if (plan is not null)
            {
                using var w = new StreamWriter(Path.Combine(dir, "3_piano.tsv"), false, utf8);
                w.WriteLine("# " + SyncEngine.DescribePlan(plan));
                w.WriteLine("oggetto\tpercorso_telefono\tpercorso_pc\tcategoria\tazione\tmotivo");
                foreach (var d in plan.Directories)
                    w.WriteLine($"cartella\t{(d.RelativePath.Length == 0 ? "." : d.RelativePath)}\t{(d.LocalRelativePath.Length == 0 ? "." : d.LocalRelativePath)}\t" +
                                $"{(d.Local is null ? "assente sul PC" : "presente")}\t{(d.NeedsCreate ? "crea" : d.NeedsTimeFix ? "allinea date" : "nessuna")}\t");
                foreach (var i in plan.Items)
                    w.WriteLine($"file\t{(i.Remote is null ? "" : i.RelativePath)}\t{i.LocalRelativePath}\t{i.Category}\t{i.Action}\t{i.Reason}");
            }

            using (var w = new StreamWriter(Path.Combine(dir, "4_messaggi_telefono.txt"), false, utf8))
            {
                w.WriteLine($"Elementi trovati da find (conteggio indipendente): {remote.FoundEntries}");
                w.WriteLine($"Elementi dichiarati dai blocchi: {remote.DeclaredEntries}");
                w.WriteLine($"Elementi ricevuti: {remote.ReceivedEntries}");
                w.WriteLine($"Spariti durante la scansione: {remote.Vanished.Count}");
                w.WriteLine($"Permesso negato: {remote.PermissionDenied.Count}");
                foreach (var p in remote.PermissionDenied) w.WriteLine("  permesso negato: " + p);
                w.WriteLine($"Link simbolici (non copiati): {remote.SymbolicLinks.Count}");
                foreach (var l in remote.SymbolicLinks) w.WriteLine("  link: " + l);
                w.WriteLine($"Output unito (cartella temporanea non scrivibile): {remote.UsedMergedOutput}");
                w.WriteLine(remote.Problems.Count == 0 ? "Esito verifica: scansione COMPLETA" : "Esito verifica: scansione INCOMPLETA");
                foreach (var p in remote.Problems) w.WriteLine("  PROBLEMA: " + p);
                w.WriteLine();
                w.WriteLine("--- messaggi grezzi ricevuti dal telefono (righe non-dati) ---");
                foreach (var m in remote.RawMessages) w.WriteLine(m);
            }

            log.Info("Diagnostica salvata in " + dir);
            return dir;
        }
        catch (Exception ex)
        {
            log.Warn("Impossibile scrivere i file di diagnostica: " + ex.Message);
            return null;
        }
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    private static string IsoUnix(long unix) => unix <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
