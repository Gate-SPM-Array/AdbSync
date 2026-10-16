using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using AdbSync.Core;
using AdbSync.Core.Adb;
using Xunit;

namespace AdbSync.Tests;

public sealed class V018Tests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_v018_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;

    public V018Tests() => _log = new SyncLogger(_local + "_logs");

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_local, true); } catch { }
        try { Directory.Delete(_local + "_logs", true); } catch { }
    }

    private SyncEngine Engine(IRemoteDevice d, SyncOptions o) => new(d, o, _log)
    {
        BackoffDelay = _ => TimeSpan.FromMilliseconds(10),
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        StallTimeoutOverride = TimeSpan.FromSeconds(5),
    };

    private string LocalPath(string rel) => Path.Combine(_local, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    // ------------------------------------------------------------ MD5 visibile

    [Fact]
    public async Task VerifiedCopiesShowTheirMd5InSummaryAndReport()
    {
        var d = new FakeRemoteDevice();
        for (int k = 0; k < 3; k++) d.Add($"foto{k}.jpg", 50_000 + k, 1_700_000_000, seed: k);
        var opts = new SyncOptions { VerifyHash = true };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(3, r.Md5Verified);
        Assert.Contains("MD5 verificato su 3 file copiati", r.Summary);
        var lines = File.ReadAllLines(r.ReportPath!);
        Assert.EndsWith(";MD5", lines[0]);
        for (int k = 0; k < 3; k++)
            Assert.Contains(lines, l => l.StartsWith($"file;foto{k}.jpg;", StringComparison.Ordinal) && l.EndsWith(";" + Md5(d.Files[$"foto{k}.jpg"].Data)));
    }

    [Fact]
    public async Task CorruptCopiesAreCountedAndWithoutMd5TheSummarySaysOnlySizeWasChecked()
    {
        var d = new FakeRemoteDevice();
        d.Add("video.mp4", 80_000, 1_700_000_000);
        d.AddFault("video.mp4", FakeRemoteDevice.Fault.Corrupt, FakeRemoteDevice.Fault.Corrupt);
        var opts = new SyncOptions { VerifyHash = true };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(1, r.CorruptRedone);                  // file, non tentativi (2 copie corrotte dello stesso file)
        Assert.Equal(3, plan.Items.Single().Attempts);
        Assert.Equal(1, r.Md5Verified);
        Assert.Contains("copia scartata e ritentata", r.Summary);

        var d2 = new FakeRemoteDevice();
        d2.Add("altro.jpg", 1000, 1_700_000_000);
        var plan2 = await SyncEngine.CompareAsync(d2, d2.Root, _local + "_2", new SyncOptions(), _log, null, default);
        var r2 = await Engine(d2, new SyncOptions()).RunAsync(plan2, default);
        try
        {
            Assert.True(r2.FullySucceeded);
            Assert.Contains("Verifica MD5 non attiva", r2.Summary);
            Assert.Equal(0, r2.Md5Verified);
        }
        finally { try { Directory.Delete(_local + "_2", true); } catch { } }
    }

    // ------------------------------------------------------------ md5sum da stdin

    [Fact]
    public void Md5CommandWorksForNamesWithBackslashInRealMksh()
    {
        if (!File.Exists("/usr/bin/mksh") || !File.Exists("/usr/bin/toybox")) return;
        string bin = Path.Combine(_local, "bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "md5sum"), "/usr/bin/toybox");
        string file = Path.Combine(_local, "foto\\strana 'x'.jpg");
        var data = new byte[3000];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(file, data);

        var psi = new ProcessStartInfo("/usr/bin/mksh") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(RemoteShell.BuildMd5Command(file));
        psi.Environment["PATH"] = bin + ":/usr/bin:/bin";
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(Md5(data), RemoteShell.ParseMd5(output));
        Assert.Null(RemoteShell.ParseMd5(RunMksh(RemoteShell.BuildMd5Command(file + ".manca"), bin)));
    }

    private static string RunMksh(string command, string bin)
    {
        var psi = new ProcessStartInfo("/usr/bin/mksh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["PATH"] = bin + ":/usr/bin:/bin";
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    // ------------------------------------------------------------ permesso negato in lettura

    [Fact]
    public async Task PermissionDeniedReadsAreAWarningAndAreNotProposedAgain()
    {
        var d = new FakeRemoteDevice();
        d.Add("Android/data/app/cache.bin", 2000, 1_700_000_000);
        d.Add("DCIM/foto.jpg", 3000, 1_700_000_000);
        d.AddFault("Android/data/app/cache.bin", FakeRemoteDevice.Fault.PermissionDenied);
        var opts = new SyncOptions();
        var session = await SyncEngine.ScanAsync(d, d.Root, _local, opts, _log, null, default);
        var plan = SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false);
        var r = await Engine(d, opts).RunAsync(plan, default);

        Assert.False(r.FullySucceeded);
        Assert.Equal(0, r.Failed);
        Assert.Equal(1, r.Unreadable);
        Assert.Equal(1, r.Copied);
        Assert.StartsWith("COMPLETATO CON AVVISI", r.Summary);
        var item = plan.Items.Single(i => i.RelativePath == "Android/data/app/cache.bin");
        Assert.Equal(ItemStatus.Unreadable, item.Status);
        Assert.Equal(1, item.Attempts);                     // inutile ritentare

        session.ApplyResults(plan);
        var next = SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false);
        Assert.Equal(0, next.CountToCopy);                  // "Riprova" non lo ripropone
        var again = next.Items.Single(i => i.RelativePath == "Android/data/app/cache.bin");
        Assert.Equal(ItemCategory.Blocked, again.Category);
        Assert.Contains("permesso negato", again.Error);
    }

    // ------------------------------------------------------------ verifica del backup

    private async Task<(FakeRemoteDevice D, ComparisonSession S, SyncPlan P)> CopiedBackupAsync(int files = 5)
    {
        var d = new FakeRemoteDevice();
        for (int k = 0; k < files; k++) d.Add($"DCIM/img{k}.jpg", 200_000 + k * 10, 1_700_000_000 + k, seed: k);
        var opts = new SyncOptions();
        var session = await SyncEngine.ScanAsync(d, d.Root, _local, opts, _log, null, default);
        var plan = SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        session.ApplyResults(plan);
        return (d, session, SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false));
    }

    /// <summary>Cambia un byte del file sul PC lasciando invariate dimensione e data: per il confronto è "identico".</summary>
    private void Tamper(string rel, int offset)
    {
        string p = LocalPath(rel);
        var t = File.GetLastWriteTimeUtc(p);
        var bytes = File.ReadAllBytes(p);
        bytes[offset] ^= 0x55;
        File.WriteAllBytes(p, bytes);
        File.SetLastWriteTimeUtc(p, t);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupVerificationFindsFilesThatDifferOnlyInContent(bool quick)
    {
        var (d, session, plan) = await CopiedBackupAsync();
        Assert.Equal(5, plan.Count(ItemCategory.Identical));
        Tamper("DCIM/img3.jpg", 10);   // nei primi 64 KB: lo vede anche la verifica rapida

        var engine = Engine(d, new SyncOptions());
        var v = await engine.VerifyBackupAsync(plan, quick, default);
        Assert.False(v.AllIdentical);
        Assert.Equal(5, v.Total);
        Assert.Equal(4, v.Identical);
        Assert.Equal(1, v.Different);
        Assert.Equal("DCIM/img3.jpg", v.DifferentItems.Single().RelativePath);
        Assert.StartsWith("VERIFICA CON DIFFERENZE", v.Summary);
        Assert.Equal(ItemStatus.Verified, plan.Items.Single(i => i.RelativePath == "DCIM/img0.jpg").Status);

        var report = File.ReadAllLines(v.ReportPath!);
        Assert.Contains(report, l => l.StartsWith("DCIM/img3.jpg;", StringComparison.Ordinal) && l.EndsWith(";DIVERSO"));
        Assert.Contains(report, l => l.StartsWith("DCIM/img0.jpg;", StringComparison.Ordinal) && l.EndsWith(";identico"));
        if (!quick) Assert.Contains(Md5(d.Files["DCIM/img0.jpg"].Data), File.ReadAllText(v.ReportPath!));

        // nulla viene toccato dalla verifica; il piano successivo lo segnala come conflitto e non lo ricopia da solo
        session.ApplyResults(plan);
        var next = SyncEngine.PlanFromSession(session, new SyncOptions(), _log, writeDiagnostics: false);
        var diff = next.Items.Single(i => i.RelativePath == "DCIM/img3.jpg");
        Assert.Equal(ItemCategory.Conflict, diff.Category);
        Assert.Equal(ItemAction.Skip, diff.Action);
        Assert.False(next.HasWork);

        // ricopia per scelta esplicita: poi torna identico
        diff.Action = ItemAction.Copy;
        var r = await Engine(d, new SyncOptions()).RunAsync(next, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(d.Files["DCIM/img3.jpg"].Data, File.ReadAllBytes(LocalPath("DCIM/img3.jpg")));
        session.ApplyResults(next);
        var after = SyncEngine.PlanFromSession(session, new SyncOptions(), _log, writeDiagnostics: false);
        Assert.Equal(ItemCategory.Identical, after.Items.Single(i => i.RelativePath == "DCIM/img3.jpg").Category);
    }

    [Fact]
    public async Task QuickVerificationCannotSeeAChangeInTheMiddleButFullMd5Does()
    {
        var (d, _, plan) = await CopiedBackupAsync(1);
        Tamper("DCIM/img0.jpg", 100_000);   // fuori dai primi e dagli ultimi 64 KB
        Assert.True((await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: true, default)).AllIdentical);
        var full = await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: false, default);
        Assert.Equal(1, full.Different);
    }

    [Fact]
    public async Task VerificationNeverCallsAnIncompleteBackupComplete()
    {
        var (d, session, _) = await CopiedBackupAsync(3);
        d.Add("DCIM/nuova.jpg", 5000, 1_700_000_100);          // sul telefono ma non sul PC
        var fresh = await SyncEngine.ScanAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        var plan = SyncEngine.PlanFromSession(fresh, new SyncOptions(), _log, writeDiagnostics: false);
        var v = await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: false, default);
        Assert.True(v.AllIdentical);
        Assert.False(v.Complete);
        Assert.Equal(1, v.NotInBackup);
        Assert.Contains("NON è completo", v.Summary);

        // cartella del PC sbagliata o vuota: niente da verificare, ma il backup non c'è
        var empty = await SyncEngine.ScanAsync(d, d.Root, _local + "_vuota", new SyncOptions(), _log, null, default);
        var v2 = await Engine(d, new SyncOptions()).VerifyBackupAsync(SyncEngine.PlanFromSession(empty, new SyncOptions(), _log, false), quick: true, default);
        Assert.Equal(0, v2.Total);
        Assert.False(v2.Complete);
        Assert.Contains("NON è completo", v2.Summary);
    }

    [Fact]
    public async Task QuickVerificationDoesNotClaimFilesAreIdentical()
    {
        var (d, _, plan) = await CopiedBackupAsync(2);
        var v = await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: true, default);
        Assert.True(v.Complete);
        Assert.StartsWith("VERIFICA RAPIDA SUPERATA", v.Summary);
        Assert.DoesNotContain("identici", v.Summary);
    }

    [Fact]
    public async Task UnverifiableFilesAreNeverReportedAsIdentical()
    {
        var (d, _, plan) = await CopiedBackupAsync(3);
        d.HashBatchError = new IOException("telefono non risponde");
        var v = await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: true, default);
        Assert.False(v.AllIdentical);
        Assert.Equal(0, v.Identical);
        Assert.Equal(3, v.NotVerifiable);
        Assert.StartsWith("VERIFICA CON DIFFERENZE", v.Summary);
    }

    [Fact]
    public async Task AllIdenticalBackupPasses()
    {
        var (d, _, plan) = await CopiedBackupAsync(4);
        var v = await Engine(d, new SyncOptions()).VerifyBackupAsync(plan, quick: false, default);
        Assert.True(v.AllIdentical, v.Summary);
        Assert.StartsWith("VERIFICA SUPERATA", v.Summary);
        Assert.Equal(4, v.Identical);
    }

    // ------------------------------------------------------------ log per le segnalazioni

    [Fact]
    public void SupportZipHasSessionAndSystemLogsButNoFileLists()
    {
        string root = Path.Combine(_local, "logs");
        string sessionDir;
        using (var log = SyncLogger.ForApplication(root, "GUI"))
        {
            sessionDir = log.StartSession("2026-10-16_14-30-05_Xiaomi")!;
            log.Info("riga della sessione");
        }
        File.WriteAllText(Path.Combine(sessionDir, "report_20261016_143100.csv"), "elenco dei file");
        Directory.CreateDirectory(Path.Combine(sessionDir, "diagnostica_20261016_143000"));
        string zip = LogSessions.CreateSupportZip(root, sessionDir, DateTime.Now);
        using var z = ZipFile.OpenRead(zip);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("sessione/sessione.log", names);          // senza il nome del telefono
        Assert.DoesNotContain(names, n => n.Contains("Xiaomi"));
        Assert.Contains($"SystemLOG/system_{DateTime.Now:yyyy-MM}.log", names);
        Assert.DoesNotContain(names, n => n.Contains("report_") || n.Contains("diagnostica_"));
        Assert.Equal(0, LogSessions.Inspect(root).OlderFiles);   // lo zip non conta come "file delle versioni precedenti"
    }

    [Fact]
    public void LogNameNeverContainsTheFullSerial()
    {
        var dev = new DeviceInfo("9b4f1c2d3e7a", "2201117TG", "Online", true);
        Assert.Equal("2201117TG (…3e7a)", dev.LogName);
        Assert.DoesNotContain("9b4f1c2d3e7a", dev.LogName);
    }
}
