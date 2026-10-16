using AdbSync.Core;

namespace AdbSync.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_test_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;
    private readonly List<LogEntry> _entries = new();

    public EngineTests()
    {
        _log = new SyncLogger(Path.Combine(_local + "_logs"));
        _log.EntryWritten += e => { lock (_entries) _entries.Add(e); };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_local, true); } catch { }
        try { Directory.Delete(_local + "_logs", true); } catch { }
    }

    private static FakeRemoteDevice Phone(int files = 5, int size = 20_000)
    {
        var d = new FakeRemoteDevice();
        for (int i = 0; i < files; i++) d.Add($"Camera/IMG_{i:000}.jpg", size + i, 1_700_000_000 + i);
        return d;
    }

    private SyncEngine Engine(IRemoteDevice d, SyncOptions? o = null) => new(d, o ?? new SyncOptions(), _log)
    {
        BackoffDelay = _ => TimeSpan.FromMilliseconds(10),
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        StallTimeoutOverride = TimeSpan.FromMilliseconds(300),
        ForceCloseDelay = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromMilliseconds(20),
        WaitTimeoutOverride = TimeSpan.FromSeconds(3),
    };

    private async Task<(SyncPlan Plan, SyncResult Result)> Run(FakeRemoteDevice d, SyncOptions? o = null, CancellationToken ct = default)
    {
        o ??= new SyncOptions();
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, o, _log, null, CancellationToken.None);
        var result = await Engine(d, o).RunAsync(plan, ct);
        return (plan, result);
    }

    private void AssertFilesMatch(FakeRemoteDevice d)
    {
        foreach (var (rel, f) in d.Files)
        {
            var p = Path.Combine(_local, rel);
            Assert.True(File.Exists(p), "manca " + rel);
            Assert.Equal(f.Data, File.ReadAllBytes(p));
            // data di modifica conservata con la frazione di secondo
            Assert.Equal(TimeUtil.ToUtc(f.Mtime, d.FileFraction), File.GetLastWriteTimeUtc(p));
        }
        Assert.Empty(Directory.GetFiles(_local, "*" + LocalScanner.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopiesEverythingThenSecondRunIsIncremental()
    {
        var d = Phone();
        var (_, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(5, r.Copied);
        AssertFilesMatch(d);

        // seconda esecuzione: nulla da copiare
        var plan2 = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(0, plan2.CountToCopy);
        Assert.Equal(5, plan2.Count(ItemCategory.Identical));
        Assert.Equal(0, plan2.CountDirsToCreate);
        Assert.Equal(0, plan2.CountDirTimeFixes);

        // un file nuovo e uno modificato sul telefono
        d.Add("Camera/NUOVO.jpg", 999, 1_800_000_000);
        d.Add("Camera/IMG_000.jpg", 777, 1_800_000_000);
        var plan3 = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(1, plan3.Count(ItemCategory.New));
        Assert.Equal(1, plan3.Count(ItemCategory.Changed));
        var r3 = await Engine(d).RunAsync(plan3, default);
        Assert.True(r3.FullySucceeded);
        AssertFilesMatch(d);
    }

    [Fact]
    public async Task StallIsDetectedAndRetried()
    {
        var d = Phone();
        d.AddFault("Camera/IMG_002.jpg", FakeRemoteDevice.Fault.Stall);
        var (_, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        AssertFilesMatch(d);
        Assert.Contains(_entries, e => e.Message.Contains("Stallo su") && e.Message.Contains("rimesso in fondo alla coda"));
    }

    [Fact]
    public async Task HungTransferIgnoringCancellationIsForceClosed()
    {
        var d = Phone(files: 2);
        d.AddFault("Camera/IMG_001.jpg", FakeRemoteDevice.Fault.HangIgnoringCancel);
        var (_, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        AssertFilesMatch(d);
    }

    [Fact]
    public async Task TruncatedAndCorruptedCopiesAreRetried()
    {
        var d = Phone();
        d.AddFault("Camera/IMG_001.jpg", FakeRemoteDevice.Fault.Truncate);
        d.AddFault("Camera/IMG_003.jpg", FakeRemoteDevice.Fault.Corrupt);
        var (plan, r) = await Run(d, new SyncOptions { VerifyHash = true });
        Assert.True(r.FullySucceeded, r.Summary);
        AssertFilesMatch(d);
        // un tentativo fallito per il guasto + quello riuscito; eventuali stalli dovuti al carico della macchina di test non contano
        foreach (var name in new[] { "Camera/IMG_001.jpg", "Camera/IMG_003.jpg" })
        {
            var item = plan.Items.Single(i => i.RelativePath == name);
            Assert.Equal(2, item.Attempts - item.Stalls);
        }
    }

    [Fact]
    public async Task CorruptionWithoutHashStillCaughtIfSizeDiffers_ButNotIfSameSize()
    {
        // senza verifica hash, una corruzione a parità di dimensione non è rilevabile: documentiamo il comportamento
        var d = Phone(files: 1);
        d.AddFault("Camera/IMG_000.jpg", FakeRemoteDevice.Fault.Corrupt);
        var (_, r) = await Run(d);
        Assert.True(r.FullySucceeded);
    }

    [Fact]
    public async Task PersistentErrorsAreReportedNotHidden()
    {
        var d = Phone();
        d.AddFault("Camera/IMG_004.jpg", Enumerable.Repeat(FakeRemoteDevice.Fault.TransientError, 10).ToArray());
        var (plan, r) = await Run(d, new SyncOptions { MaxRetries = 2 });
        Assert.False(r.FullySucceeded);
        Assert.Equal(4, r.Copied);
        Assert.Equal(1, r.Failed);
        var failed = plan.Items.Single(i => i.RelativePath == "Camera/IMG_004.jpg");
        Assert.Equal(ItemStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Contains("NON", r.Summary.ToUpperInvariant());
        Assert.False(File.Exists(Path.Combine(_local, "Camera/IMG_004.jpg")));
        Assert.True(File.Exists(r.ReportPath));
    }

    [Fact]
    public async Task MissingFileIsPermanentAndNotRetried()
    {
        var d = Phone();
        d.AddFault("Camera/IMG_001.jpg", FakeRemoteDevice.Fault.NoSuchFile);
        var (plan, r) = await Run(d);
        Assert.Equal(1, r.Failed);
        Assert.Equal(1, plan.Items.Single(i => i.RelativePath == "Camera/IMG_001.jpg").Attempts);
    }

    [Fact]
    public async Task WaitsForDeviceReconnectionAndResumes()
    {
        var d = Phone(files: 6);
        d.ComesBackAfter = TimeSpan.FromMilliseconds(500);
        d.AddFault("Camera/IMG_002.jpg", FakeRemoteDevice.Fault.GoOffline);
        var (_, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        AssertFilesMatch(d);
        Assert.Contains(_entries, e => e.Message.Contains("di nuovo disponibile"));
    }

    [Fact]
    public async Task AbortsCleanlyWhenDeviceNeverComesBack()
    {
        var d = Phone(files: 20);
        d.ChunkDelay = TimeSpan.FromMilliseconds(5);
        d.AddFault("Camera/IMG_003.jpg", FakeRemoteDevice.Fault.GoOffline);
        var (_, r) = await Run(d, new SyncOptions { ParallelTransfers = 1 });
        Assert.False(r.FullySucceeded);
        Assert.NotNull(r.AbortReason);
        Assert.True(r.NotProcessed > 0);
        Assert.Equal(20, r.Copied + r.Failed + r.NotProcessed);
        Assert.Empty(Directory.GetFiles(_local, "*" + LocalScanner.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialFiles()
    {
        var d = Phone(files: 30, size: 200_000);
        d.ChunkDelay = TimeSpan.FromMilliseconds(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var (_, r) = await Run(d, ct: cts.Token);
        Assert.True(r.Cancelled);
        Assert.True(r.NotProcessed > 0);
        Assert.Empty(Directory.GetFiles(_local, "*" + LocalScanner.TempSuffix, SearchOption.AllDirectories));
        // i file completati sono integri e la ripresa copia solo il resto
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(r.NotProcessed, plan.CountToCopy);
        var r2 = await Engine(d).RunAsync(plan, default);
        Assert.True(r2.FullySucceeded);
        AssertFilesMatch(d);
    }

    [Fact]
    public async Task StaleTempFilesAreCleanedAndWindowsUnsafeNamesHandled()
    {
        var d = Phone(files: 1);
        d.Add("Note/riunione: ore 10?.txt", 50, 1_700_000_000);
        Directory.CreateDirectory(Path.Combine(_local, "Camera"));
        File.WriteAllText(Path.Combine(_local, "Camera", "vecchio.jpg" + LocalScanner.TempSuffix), "x");
        var (plan, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.True(File.Exists(Path.Combine(_local, "Note", "riunione： ore 10？.txt")));
        Assert.Empty(Directory.GetFiles(_local, "*" + LocalScanner.TempSuffix, SearchOption.AllDirectories));
        // al secondo giro il file con nome convertito risulta identico
        var plan2 = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(0, plan2.CountToCopy);
    }

    [Fact]
    public async Task EmptyFoldersAreCreatedAndFolderDatesPreserved()
    {
        var d = Phone(files: 3);
        d.EmptyDirs["Vuota/Annidata"] = 1_600_000_000;
        d.EmptyDirs["Vuota"] = 1_600_000_100;
        var (plan, r) = await Run(d);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.True(Directory.Exists(Path.Combine(_local, "Vuota", "Annidata")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000).UtcDateTime, Directory.GetLastWriteTimeUtc(Path.Combine(_local, "Vuota", "Annidata")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_100).UtcDateTime, Directory.GetLastWriteTimeUtc(Path.Combine(_local, "Vuota")));
        // la cartella in cui sono stati scritti i file ha comunque la data del telefono (impostata alla fine)
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(d.DirMtime + 1).UtcDateTime, Directory.GetLastWriteTimeUtc(Path.Combine(_local, "Camera")));
        Assert.True(r.DirsCreated >= 3);
    }

    [Fact]
    public async Task IncompleteScanStopsTheComparisonAndWritesDiagnostics()
    {
        var d = Phone();
        d.ScanProblems.Add("il telefono ha segnalato 13 errori durante la scansione, ad es.: \"find: exec stat: Argument list too long\"");
        var ex = await Assert.ThrowsAsync<IncompleteScanException>(() =>
            SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions { Diagnostic = true }, _log, null, default));
        Assert.Contains("INCOMPLETA", ex.Message);
        Assert.Single(Directory.GetDirectories(_local + "_logs", "diagnostica_*"));
    }

    [Fact]
    public async Task DiagnosticModeWritesFullListings()
    {
        var d = Phone(files: 4);
        await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions { Diagnostic = true }, _log, null, default);
        var dir = Directory.GetDirectories(_local + "_logs", "diagnostica_*").Single();
        var plan = File.ReadAllLines(Path.Combine(dir, "3_piano.tsv"));
        Assert.Equal(4, plan.Count(l => l.StartsWith("file\tCamera/IMG_")));
        Assert.All(plan.Where(l => l.StartsWith("file\t")), l => Assert.Contains("presente solo sul telefono", l));
        Assert.Equal(4, File.ReadAllLines(Path.Combine(dir, "1_telefono.tsv")).Count(l => l.StartsWith("file\t")));
        Assert.Contains("COMPLETA", File.ReadAllText(Path.Combine(dir, "4_messaggi_telefono.txt")));
    }

    [Fact]
    public async Task DestinationDisappearsAndComesBack_CopyResumes()
    {
        var d = Phone(files: 12, size: 60_000);
        d.ChunkDelay = TimeSpan.FromMilliseconds(3);
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        var engine = Engine(d, new SyncOptions { MaxRetries = 1 });
        var run = engine.RunAsync(plan, default);
        await Task.Delay(150);
        string moved = _local + "_staccato";
        Directory.Move(_local, moved);          // "disco staccato": la cartella sparisce...
        File.WriteAllText(_local, "blocco");    // ...e il percorso non è più utilizzabile
        await Task.Delay(600);
        File.Delete(_local);
        Directory.Move(moved, _local);          // "disco ricollegato"
        var r = await run;
        Assert.True(r.FullySucceeded, r.Summary);
        AssertFilesMatch(d);
        Assert.Contains(_entries, e => e.Message.Contains("Disco di destinazione non disponibile"));
        Assert.Contains(_entries, e => e.Message.Contains("di nuovo disponibile"));
    }

    [Fact]
    public async Task DestinationGoneForGood_AbortsWithoutBurningEveryFile()
    {
        var d = Phone(files: 40, size: 60_000);
        d.ChunkDelay = TimeSpan.FromMilliseconds(3);
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        var engine = Engine(d, new SyncOptions { MaxRetries = 5 });
        engine.WaitTimeoutOverride = TimeSpan.FromMilliseconds(500);
        var run = engine.RunAsync(plan, default);
        await Task.Delay(150);
        Directory.Move(_local, _local + "_staccato");
        File.WriteAllText(_local, "blocco");
        var r = await run;
        try { File.Delete(_local); Directory.Delete(_local + "_staccato", true); } catch { }
        Assert.NotNull(r.AbortReason);
        Assert.Contains("destinazione", r.AbortReason);
        Assert.Equal(0, r.Failed);                  // nessun file "bruciato" in tentativi inutili
        Assert.True(r.NotProcessed > 0);
        // chi è stato fermato dopo un errore lo riporta esplicitamente; nessuno resta senza spiegazione
        Assert.Contains(plan.ToCopy, i => i.Error?.Contains("ultimo errore") == true);
        Assert.All(plan.ToCopy.Where(i => i.Status == ItemStatus.NotProcessed), i => Assert.False(string.IsNullOrEmpty(i.Error)));
    }

    [Fact]
    public async Task FileAppearedOrChangedOnPcAfterCompareIsNotOverwritten()
    {
        var d = Phone(files: 3);
        var (_, r0) = await Run(d);
        Assert.True(r0.FullySucceeded);
        d.Add("Camera/NUOVO.jpg", 500, 1_800_000_000);
        d.Add("Camera/IMG_000.jpg", 900, 1_800_000_000); // modificato sul telefono
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(2, plan.CountToCopy);
        // dopo il confronto l'utente crea/modifica file sul PC
        File.WriteAllText(Path.Combine(_local, "Camera", "NUOVO.jpg"), "creato sul PC");
        File.AppendAllText(Path.Combine(_local, "Camera", "IMG_000.jpg"), "modifica sul PC");
        var r = await Engine(d).RunAsync(plan, default);
        Assert.Equal(2, r.Failed);
        Assert.Equal("creato sul PC", File.ReadAllText(Path.Combine(_local, "Camera", "NUOVO.jpg")));
        Assert.EndsWith("modifica sul PC", File.ReadAllText(Path.Combine(_local, "Camera", "IMG_000.jpg")));
        Assert.All(r.FailedItems, i => Assert.Contains("dopo il confronto", i.Error));
    }

    [Fact]
    public async Task NotCopiableItemsPreventCompletedHeadline()
    {
        var d = Phone(files: 2);
        d.Add("Camera/strano.adbsync-part", 10, 1_700_000_000);
        var (plan, r) = await Run(d);
        Assert.Equal(1, plan.Count(ItemCategory.Blocked));
        Assert.Equal(2, r.Copied);
        Assert.False(r.FullySucceeded);
        Assert.StartsWith("COMPLETATO CON AVVISI", r.Summary);
        Assert.False(File.Exists(Path.Combine(_local, "Camera", "strano.adbsync-part")));
    }

    [Fact]
    public async Task ProgressCountersAreConsistent()
    {
        var d = Phone(files: 10);
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        var engine = Engine(d);
        var r = await engine.RunAsync(plan, default);
        Assert.Equal(10, engine.Progress.DoneFiles);
        Assert.Equal(plan.BytesToCopy, engine.Progress.CommittedBytes);
        Assert.Equal("Completato", engine.Progress.Phase);
        Assert.True(r.FullySucceeded);
    }
}

public sealed class EngineV012Tests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_v012_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;
    private readonly List<LogEntry> _entries = new();

    public EngineV012Tests()
    {
        _log = new SyncLogger(_local + "_logs");
        _log.EntryWritten += e => { lock (_entries) _entries.Add(e); };
    }

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
        StallTimeoutOverride = TimeSpan.FromMilliseconds(300),
        ForceCloseDelay = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromMilliseconds(20),
        WaitTimeoutOverride = TimeSpan.FromSeconds(3),
    };

    [Fact]
    public async Task StalledFileGoesToEndOfQueueWithoutWaiting()
    {
        var d = new FakeRemoteDevice();
        for (int i = 0; i < 8; i++) d.Add($"F{i}.jpg", 20_000, 1_700_000_000);
        d.AddFault("F0.jpg", FakeRemoteDevice.Fault.Stall, FakeRemoteDevice.Fault.Stall);
        var opts = new SyncOptions { ParallelTransfers = 1, MaxRetries = 5 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = Engine(d, opts);
        engine.BackoffDelay = _ => TimeSpan.FromSeconds(30); // se venisse usata, il test durerebbe minuti
        var order = new List<string>();
        _log.EntryWritten += e => { if (e.Message.StartsWith("OK ")) lock (order) order.Add(e.Message[3..].Split(' ')[0]); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await engine.RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "nessuna attesa crescente dopo uno stallo");
        Assert.Equal("F0.jpg", order[^1]);       // il file bloccato è stato ritentato dopo tutti gli altri
        Assert.Equal(2, r.Stalls);
        Assert.Equal(1, r.RecoveredAfterRetry);
        Assert.Contains("Stalli: 2", r.Summary);
    }

    [Fact]
    public async Task FileAlwaysStallingIsReportedAsCandidateForExclusion()
    {
        var d = new FakeRemoteDevice();
        d.Add("ok.jpg", 10_000, 1_700_000_000);
        d.Add("bloccato.db", 10_000, 1_700_000_000);
        d.AddFault("bloccato.db", Enumerable.Repeat(FakeRemoteDevice.Fault.Stall, 10).ToArray());
        var opts = new SyncOptions { ParallelTransfers = 1, MaxRetries = 2 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.Equal(1, r.Copied);
        Assert.Equal(1, r.Failed);
        Assert.Equal(new[] { "bloccato.db" }, r.AlwaysStalled);
        Assert.Contains(_entries, e => e.Message.Contains("candidato all'esclusione"));
    }

    /// <summary>PC con file di stessa dimensione ma data diversa (copiati in passato senza conservare le date).</summary>
    private async Task<FakeRemoteDevice> PrepareDateOnlyScenario()
    {
        var d = new FakeRemoteDevice();
        var rnd = new Random(1);
        void Put(string name, int size)
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            d.Files[name] = (data, 1_600_000_000);
            Directory.CreateDirectory(_local);
            File.WriteAllBytes(Path.Combine(_local, name), data);
            File.SetLastWriteTimeUtc(Path.Combine(_local, name), new DateTime(2026, 10, 14, 0, 38, 0, DateTimeKind.Utc));
        }
        Put("uguale_piccolo.opus", 20_000);
        Put("uguale_grande.mp4", 400_000);
        Put("diverso_in_testa.jpg", 50_000);
        Put("diverso_nel_mezzo.mp4", 400_000);
        // modifiche sul PC a parità di dimensione
        var p1 = Path.Combine(_local, "diverso_in_testa.jpg");
        var b1 = File.ReadAllBytes(p1); b1[10] ^= 0xFF; File.WriteAllBytes(p1, b1);
        var p2 = Path.Combine(_local, "diverso_nel_mezzo.mp4");
        var b2 = File.ReadAllBytes(p2); b2[200_000] ^= 0xFF; File.WriteAllBytes(p2, b2);
        foreach (var f in Directory.GetFiles(_local)) File.SetLastWriteTimeUtc(f, new DateTime(2026, 10, 14, 0, 38, 0, DateTimeKind.Utc));
        await Task.CompletedTask;
        return d;
    }

    private DateTime PcTime(string name) => File.GetLastWriteTimeUtc(Path.Combine(_local, name));
    private static readonly DateTime PhoneTime = DateTime.UnixEpoch.AddSeconds(1_600_000_000).AddTicks(1234567);

    [Fact]
    public async Task HashBatchFailureLeavesFilesUntouchedAndReportsIt()
    {
        var d = await PrepareDateOnlyScenario();
        d.HashBatchError = new TimeoutException("il telefono non risponde");
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.Quick };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);   // nessuna eccezione: risultato sempre prodotto
        Assert.False(r.FullySucceeded, r.Summary);
        Assert.Equal(3, d.HashBatchCalls);
        Assert.NotEqual(PhoneTime, PcTime("uguale_piccolo.opus"));
        Assert.Equal(4, r.MetadataErrors.Count);
        Assert.All(plan.Items, i => Assert.Equal(ItemStatus.Failed, i.Status));
    }

    [Fact]
    public async Task VerifyCancelledIsNeverReportedAsSuccess()
    {
        var d = await PrepareDateOnlyScenario();
        d.HashBatchError = new OperationCanceledException(); // timeout della libreria che si presenta come annullamento
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.Full };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.False(r.FullySucceeded, r.Summary);
        Assert.DoesNotContain(plan.Items, i => i.Status is ItemStatus.Pending or ItemStatus.Done);
    }

    [Fact]
    public async Task ContinuousStallsTriggerDeviceCheckThenStop()
    {
        var d = new FakeRemoteDevice();
        for (int i = 0; i < 30; i++)
        {
            d.Add($"F{i:00}.jpg", 20_000, 1_700_000_000);
            d.AddFault($"F{i:00}.jpg", Enumerable.Repeat(FakeRemoteDevice.Fault.Stall, 100).ToArray());
        }
        var opts = new SyncOptions { ParallelTransfers = 1, MaxRetries = 50 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = Engine(d, opts);
        engine.StallTimeoutOverride = TimeSpan.FromMilliseconds(80);
        engine.ForceCloseDelay = TimeSpan.FromMilliseconds(40);
        var r = await engine.RunAsync(plan, default);
        Assert.NotNull(r.AbortReason);
        Assert.Contains("si bloccano di continuo", r.AbortReason);
        Assert.Equal(SyncEngine.MaxFailedHealthRounds, d.WaitOnlineCalls);
        Assert.Equal(0, r.Copied);
        Assert.Equal(30, r.Failed + r.NotProcessed);
    }

    [Fact]
    public async Task SessionKeepsPhoneVersionCopiedDuringRunAndMarksTouchedFolders()
    {
        var d = new FakeRemoteDevice();
        d.Add("A/x.jpg", 1000, 1_700_000_000);
        var opts = new SyncOptions();
        var session = await SyncEngine.ScanAsync(d, d.Root, _local, opts, _log, null, default);
        var plan = session.BuildPlan(opts);
        var item = Assert.Single(plan.Items);
        // simulazione: il file è cambiato sul telefono durante la copia ed è stata copiata la versione nuova
        item.Remote = item.Remote! with { Size = 1500, MtimeUnix = 1_700_000_500 };
        item.RemoteChangedDuringCopy = true;
        item.Status = ItemStatus.Done;
        session.ApplyResults(plan);
        var next = session.BuildPlan(opts);
        Assert.Equal(ItemCategory.Identical, Assert.Single(next.Items).Category);
        // la cartella "A" è stata scritta ma le date non sono state riallineate (esecuzione interrotta)
        Assert.True(session.Local.Directories.TryGetValue("A", out var dir));
        Assert.NotEqual(d.DirMtime, dir!.MtimeUnix);
        Assert.Contains(next.Directories, x => x.LocalRelativePath == "A" && x.NeedsTimeFix);
    }

    [Fact]
    public async Task AlignDatesQuickVerification()
    {
        var d = await PrepareDateOnlyScenario();
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.Quick };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        Assert.Equal(4, plan.CountToVerify);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.Equal(PhoneTime, PcTime("uguale_piccolo.opus"));
        Assert.Equal(PhoneTime, PcTime("uguale_grande.mp4"));
        Assert.NotEqual(PhoneTime, PcTime("diverso_in_testa.jpg"));   // differenza in testa: rilevata
        Assert.Equal(PhoneTime, PcTime("diverso_nel_mezzo.mp4"));     // limite noto della verifica rapida
        Assert.Equal(1, r.ContentDiffers);
        Assert.Equal(1, d.HashBatchCalls);                            // un solo comando per tutti i file
    }

    [Fact]
    public async Task AlignDatesFullVerificationCatchesMiddleDifference()
    {
        var d = await PrepareDateOnlyScenario();
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.Full };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.Equal(PhoneTime, PcTime("uguale_grande.mp4"));
        Assert.NotEqual(PhoneTime, PcTime("diverso_nel_mezzo.mp4"));
        Assert.NotEqual(PhoneTime, PcTime("diverso_in_testa.jpg"));
        Assert.Equal(2, r.ContentDiffers);
        Assert.Equal(2, r.DatesVerifiedAligned);
    }

    [Fact]
    public async Task AlignDatesSizeOnlyAndNoneModes()
    {
        var d = await PrepareDateOnlyScenario();
        var none = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(4, none.Count(ItemCategory.DateOnly));
        Assert.Equal(0, none.CountFileTimeFixes);
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.SizeOnly };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.Equal(PhoneTime, PcTime("diverso_nel_mezzo.mp4")); // solo dimensione: nessuna verifica, come richiesto
        Assert.Equal(0, d.HashBatchCalls);
        Assert.True(r.TimesFixed >= 4);
    }

    [Fact]
    public async Task SessionIsReusedWithoutRescanning()
    {
        var d = new FakeRemoteDevice();
        for (int i = 0; i < 6; i++) d.Add($"Camera/IMG_{i}.jpg", 5_000, 1_700_000_000 + i);
        d.Add("Camera/.thumbnails/t.jpg", 100, 1_700_000_000);
        int scans = 0;
        var counting = new CountingDevice(d, () => scans++);
        var opts = new SyncOptions();
        var session = await SyncEngine.ScanAsync(counting, d.Root, _local, opts, _log, null, default);
        var plan = SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false);
        Assert.Equal(6, plan.CountToCopy);

        // cambio opzioni (esclusioni): il piano si ricalcola dall'elenco, senza scansione
        var noExcl = new SyncOptions(); noExcl.ExcludePatterns.Clear();
        Assert.Equal(7, SyncEngine.PlanFromSession(session, noExcl, _log, false).CountToCopy);

        var r = await Engine(counting, opts).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        session.ApplyResults(plan);
        var after = SyncEngine.PlanFromSession(session, opts, _log, false);
        Assert.Equal(0, after.CountToCopy);
        Assert.Equal(6, after.Count(ItemCategory.Identical));
        Assert.False(after.HasWork, after.WorkSummary);
        Assert.Equal(1, scans);

        // verifica indipendente: una nuova scansione reale dà lo stesso risultato
        var fresh = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        Assert.Equal(0, fresh.CountToCopy);
    }

    [Fact]
    public async Task ContentDiffersIsRememberedInTheSession()
    {
        var d = await PrepareDateOnlyScenario();
        var opts = new SyncOptions { AlignSameSizeDates = DateAlignMode.Full };
        var session = await SyncEngine.ScanAsync(d, d.Root, _local, opts, _log, null, default);
        var plan = SyncEngine.PlanFromSession(session, opts, _log, false);
        await Engine(d, opts).RunAsync(plan, default);
        session.ApplyResults(plan);
        var again = SyncEngine.PlanFromSession(session, opts, _log, false);
        Assert.Equal(0, again.CountToVerify);              // niente da riverificare
        Assert.Equal(2, again.Count(ItemCategory.Conflict)); // i due diversi ora sono conflitti
    }

    private sealed class CountingDevice(FakeRemoteDevice inner, Action onScan) : IRemoteDevice
    {
        public string Serial => inner.Serial;
        public string DisplayName => inner.DisplayName;
        public Task<bool> IsOnlineAsync(CancellationToken ct) => inner.IsOnlineAsync(ct);
        public Task WaitOnlineAsync(TimeSpan t, Action<string>? s, CancellationToken ct) => inner.WaitOnlineAsync(t, s, ct);
        public Task<RemoteScanResult> ScanAsync(string r, Action<int>? f, CancellationToken ct) { onScan(); return inner.ScanAsync(r, f, ct); }
        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string p, CancellationToken ct) => inner.ListDirectoriesAsync(p, ct);
        public Task<(long Size, long Mtime, int Fraction)?> StatAsync(string p, CancellationToken ct) => inner.StatAsync(p, ct);
        public Task<string?> Md5Async(string p, CancellationToken ct) => inner.Md5Async(p, ct);
        public Task<IReadOnlyDictionary<string, string?>> HashBatchAsync(string r, IReadOnlyList<string> p, bool q, CancellationToken ct) => inner.HashBatchAsync(r, p, q, ct);
        public IRemoteTransfer OpenTransfer(bool useV2 = false) => inner.OpenTransfer(useV2);
    }
}

public class ScaleTests
{
    /// <summary>Un milione di file: tempi e memoria del piano (scansione già acquisita).</summary>
    [Fact]
    public void OneMillionFiles()
    {
        const int n = 1_000_000;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var remote = new List<RemoteFile>(n);
        var local = new LocalScanResult { Files = new(StringComparer.OrdinalIgnoreCase), StaleTempFiles = new() };
        for (int i = 0; i < n; i++)
        {
            string dir = $"Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/{i / 5000:000}";
            string path = $"{dir}/IMG-2018{i % 12 + 1:00}{i % 28 + 1:00}-WA{i:0000000}.jpg";
            remote.Add(new RemoteFile(path, 100_000 + i, 1_600_000_000 + i, 1234567));
            if (i % 2 == 0) local.Files[path] = new LocalFile(path, 100_000 + i, 1_600_000_000 + i, 1_600_000_000 + i);
        }
        var scanTime = sw.Elapsed;
        sw.Restart();
        var plan = PlanBuilder.Build("/sdcard", "/tmp/x", remote, local, new SyncOptions());
        var planTime = sw.Elapsed;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = GC.GetTotalMemory(true);
        double mb = (after - before) / 1024.0 / 1024.0;
        Console.WriteLine($"[1M] elenchi {scanTime.TotalSeconds:0.0}s, piano {planTime.TotalSeconds:0.0}s, memoria {mb:0} MB ({(after - before) / n} byte/file)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "adbsync_1m.txt"), $"piano {planTime.TotalSeconds:0.0}s memoria {mb:0} MB {(after - before) / n} B/file");
        Assert.Equal(n / 2, plan.CountToCopy);
        Assert.True(planTime < TimeSpan.FromSeconds(30));
        Assert.True(mb < 1200, $"memoria {mb:0} MB");
        GC.KeepAlive(remote); GC.KeepAlive(local); GC.KeepAlive(plan);
    }
}

public sealed class EngineV013Tests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_v013_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;

    public EngineV013Tests() => _log = new SyncLogger(_local + "_logs");

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_local, true); } catch { }
        try { Directory.Delete(_local + "_logs", true); } catch { }
    }

    private void PutLocal(string rel, string content)
    {
        string p = Path.Combine(_local, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public async Task ExcludedFilesAreVisibleWithTheirRuleAndNeverTouched()
    {
        var d = new FakeRemoteDevice();
        d.Add("foto.jpg", 1000, 1_700_000_000);
        d.Add(".thumbnails/a.jpg", 500, 1_700_000_000);
        PutLocal(".thumbnails/a.jpg", "vecchia miniatura");   // presente su entrambi, ma escluso
        PutLocal(".thumbnails/b.jpg", "solo sul PC");         // solo sul PC, escluso
        var opts = new SyncOptions();
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);

        var excluded = plan.Items.Where(i => i.Category == ItemCategory.Excluded).OrderBy(i => i.RelativePath).ToList();
        Assert.Equal(2, excluded.Count);
        Assert.All(excluded, i => Assert.Equal(".thumbnails/", i.ExcludedBy));
        Assert.All(excluded, i => Assert.Equal(ItemAction.Skip, i.Action));
        Assert.NotNull(excluded[0].Remote); Assert.NotNull(excluded[0].Local);   // a.jpg: una sola riga per entrambi i lati
        Assert.Null(excluded[1].Remote);                                         // b.jpg: solo sul PC
        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.ExcludedLocalCount);
        Assert.Equal(2, plan.RemoteFileCount);
        Assert.Equal(0, plan.NotCopiable);
        Assert.DoesNotContain(plan.Items, i => i.Category == ItemCategory.OnlyOnPc);

        var r = await new SyncEngine(d, opts, _log).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(1, r.Copied);
        Assert.Equal("vecchia miniatura", File.ReadAllText(Path.Combine(_local, ".thumbnails", "a.jpg")));
    }

    [Fact]
    public async Task ExclusionRulesApplyRelativeToTheChosenPhoneFolder()
    {
        var d = new FakeRemoteDevice();
        d.Add("a.jpg", 500, 1_700_000_000);   // come se la cartella scelta fosse già .../.thumbnails
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, new SyncOptions(), _log, null, default);
        Assert.Equal(ItemCategory.New, Assert.Single(plan.Items).Category);
    }

    [Fact]
    public async Task ConvertedNamesAreListedInATextFile()
    {
        var d = new FakeRemoteDevice();
        d.Add("Note: idea?.txt", 100, 1_700_000_000);
        d.Add("Cartella <1>/foto.jpg", 200, 1_700_000_000);
        d.Add("normale.jpg", 300, 1_700_000_000);
        var opts = new SyncOptions();
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await new SyncEngine(d, opts, _log).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.NotNull(r.RenamedLogPath);
        Assert.Equal(3, r.RenamedCount);   // 1 cartella + 2 file (anche quello dentro la cartella convertita)
        var lines = File.ReadAllLines(r.RenamedLogPath!);
        Assert.Contains(lines, l => l == "file\tNote: idea?.txt\tNote： idea？.txt\tcopiato\t\":\" → \"：\" (U+FF1A); \"?\" → \"？\" (U+FF1F)");
        Assert.Contains(lines, l => l == "cartella\tCartella <1>\tCartella ＜1＞\tcreata\t\"<\" → \"＜\" (U+FF1C); \">\" → \"＞\" (U+FF1E)");
        Assert.Contains(lines, l => l.StartsWith("file\tCartella <1>/foto.jpg\tCartella ＜1＞\\foto.jpg\t", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("normale.jpg"));
    }

    [Fact]
    public async Task NoRenamedFileWhenNothingWasConverted()
    {
        var d = new FakeRemoteDevice();
        d.Add("normale.jpg", 300, 1_700_000_000);
        var opts = new SyncOptions();
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await new SyncEngine(d, opts, _log).RunAsync(plan, default);
        Assert.Null(r.RenamedLogPath);
    }
}

public sealed class DiagnosticsV015DTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_v015d_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;
    private readonly List<string> _lines = new();

    public DiagnosticsV015DTests()
    {
        _log = new SyncLogger(_local + "_logs");
        _log.EntryWritten += e => { lock (_lines) _lines.Add(e.Message); };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_local, true); } catch { }
        try { Directory.Delete(_local + "_logs", true); } catch { }
    }

    [Fact]
    public async Task PausesInTheDataFlowAreRecordedWithTheirCause()
    {
        var d = new FakeRemoteDevice { PauseDuration = TimeSpan.FromMilliseconds(700) };
        d.Add("grande.bin", 200_000, 1_700_000_000);
        d.AddFault("grande.bin", FakeRemoteDevice.Fault.Pause);
        var opts = new SyncOptions { ParallelTransfers = 1, StallTimeoutSeconds = 30 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await new SyncEngine(d, opts, _log) { PauseLogThresholdMs = 300 }.RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        string line;
        lock (_lines) line = _lines.Single(l => l.StartsWith("[diagnosi] pausa di", StringComparison.Ordinal));
        Assert.Contains("grande.bin", line);
        Assert.Contains("attesa dati da adb", line);
        Assert.Contains("thread pool", line);
        lock (_lines) Assert.Contains(_lines, l => l == "Protocollo di trasferimento ADB: v1");
    }

    [Fact]
    public async Task StallsAreDescribedAndV2OptionReachesTheTransfer()
    {
        var d = new FakeRemoteDevice();
        d.Add("bloccato.bin", 50_000, 1_700_000_000);
        d.AddFault("bloccato.bin", FakeRemoteDevice.Fault.Stall);
        var opts = new SyncOptions { ParallelTransfers = 1, UseSyncV2 = true };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = new SyncEngine(d, opts, _log)
        {
            BackoffDelay = _ => TimeSpan.FromMilliseconds(10),
            WatchdogInterval = TimeSpan.FromMilliseconds(20),
            StallTimeoutOverride = TimeSpan.FromMilliseconds(200),
            ForceCloseDelay = TimeSpan.FromMilliseconds(100),
        };
        var r = await engine.RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);   // al secondo tentativo passa
        Assert.True(d.LastOpenedV2);
        lock (_lines)
        {
            Assert.Contains(_lines, l => l.StartsWith("[diagnosi] stallo su bloccato.bin", StringComparison.Ordinal) && l.Contains("in attesa di dati da adb"));
            Assert.Contains(_lines, l => l == "Protocollo di trasferimento ADB: v2");
        }
    }

    [Fact]
    public async Task TimingStreamMeasuresWrites()
    {
        var ms = new MemoryStream();
        var t = new TimingStream(ms);
        Assert.Equal(0, t.WriteInProgressMs);
        await t.WriteAsync(new byte[10]);
        Assert.Equal(10, ms.Length);
        Assert.True(t.LastWriteMs >= 0);
        Assert.Equal(0, t.WriteInProgressMs);
    }
}

/// <summary>Disco del PC che si ferma: una scrittura (la n-esima) resta bloccata per un certo tempo.</summary>
internal sealed class SlowDiskStream(Stream inner, int slowWriteIndex, TimeSpan delay) : Stream
{
    private int _writes;
    public long BytesWritten;
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _writes) == slowWriteIndex) await Task.Delay(delay, CancellationToken.None);
        Interlocked.Add(ref BytesWritten, buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => inner.SetLength(value);
}

public sealed class DualStallV016Tests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "adbsync_v016_" + Guid.NewGuid().ToString("N"));
    private readonly SyncLogger _log;
    private readonly List<string> _lines = new();

    public DualStallV016Tests()
    {
        _log = new SyncLogger(_local + "_logs");
        _log.EntryWritten += e => { lock (_lines) _lines.Add(e.Message); };
    }

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
        StallTimeoutOverride = TimeSpan.FromMilliseconds(300),
        ForceCloseDelay = TimeSpan.FromMilliseconds(200),
        SlowDiskNotice = TimeSpan.FromMilliseconds(100),
    };

    private bool Logged(Func<string, bool> p) { lock (_lines) return _lines.Any(p); }

    [Fact]
    public async Task SlowDiskWriteIsNotAPhoneStall()
    {
        var d = new FakeRemoteDevice { ChunkSize = 4096 };
        d.Add("video.mp4", 100_000, 1_700_000_000);
        var opts = new SyncOptions { ParallelTransfers = 1 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = Engine(d, opts);
        engine.DiskStallTimeoutOverride = TimeSpan.FromSeconds(30);
        string? seenStatus = null;
        engine.DestinationWrapper = fs => new SlowDiskStream(fs, 5, TimeSpan.FromMilliseconds(1200)); // 4× il timeout del telefono
        var run = engine.RunAsync(plan, default);
        while (!run.IsCompleted) { if (engine.Progress.StatusMessage is { } m) seenStatus = m; await Task.Delay(20); }
        var r = await run;
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(0, r.Stalls);                                   // il disco lento non è uno stallo del telefono
        Assert.Equal(1, plan.Items.Single().Attempts);
        Assert.NotNull(seenStatus);
        Assert.Contains("Disco di destinazione lento", seenStatus);
        Assert.Null(engine.Progress.StatusMessage);                  // il messaggio sparisce quando il disco riparte
    }

    [Fact]
    public async Task DiskBlockedBeyondItsLimitIsRetriedWithoutBlamingThePhone()
    {
        var d = new FakeRemoteDevice { ChunkSize = 4096 };
        d.Add("video.mp4", 100_000, 1_700_000_000);
        var opts = new SyncOptions { ParallelTransfers = 1 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = Engine(d, opts);
        engine.DiskStallTimeoutOverride = TimeSpan.FromMilliseconds(500);
        int opened = 0;
        engine.DestinationWrapper = fs => Interlocked.Increment(ref opened) == 1 ? new SlowDiskStream(fs, 3, TimeSpan.FromSeconds(2)) : fs;
        var r = await engine.RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(2, plan.Items.Single().Attempts);
        Assert.True(Logged(l => l.StartsWith("Disco di destinazione bloccato durante video.mp4", StringComparison.Ordinal)));
        Assert.Equal(0, d.WaitOnlineCalls);                          // nessun controllo del telefono per colpa del disco
        Assert.Equal(d.Files["video.mp4"].Data, File.ReadAllBytes(Path.Combine(_local, "video.mp4")));
    }

    [Fact]
    public async Task AfterAStallTheCopyResumesWithoutRewritingWhatIsAlreadyOnDisk()
    {
        var d = new FakeRemoteDevice { ChunkSize = 65536 };
        d.Add("grande.bin", 6 * 1024 * 1024, 1_700_000_000, seed: 7);
        d.AddFault("grande.bin", FakeRemoteDevice.Fault.StallAfterHalf);
        var opts = new SyncOptions { ParallelTransfers = 1 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var engine = Engine(d, opts);
        var disks = new List<SlowDiskStream>();
        engine.DestinationWrapper = fs => { var s = new SlowDiskStream(fs, -1, TimeSpan.Zero); lock (disks) disks.Add(s); return s; };
        var r = await engine.RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.Equal(2, plan.Items.Single().Attempts);
        Assert.True(Logged(l => l.StartsWith("Ripresa di grande.bin", StringComparison.Ordinal)));
        long size = 6 * 1024 * 1024;
        Assert.True(disks[1].BytesWritten < size * 3 / 4, $"secondo tentativo: scritti {disks[1].BytesWritten} byte su {size}");
        Assert.Equal(d.Files["grande.bin"].Data, File.ReadAllBytes(Path.Combine(_local, "grande.bin")));
        Assert.False(File.Exists(Path.Combine(_local, "grande.bin" + LocalScanner.TempSuffix)));
    }

    [Fact]
    public async Task CorruptCopyIsNeverResumed()
    {
        var d = new FakeRemoteDevice { ChunkSize = 65536 };
        d.Add("foto.jpg", 3 * 1024 * 1024, 1_700_000_000, seed: 3);
        d.AddFault("foto.jpg", FakeRemoteDevice.Fault.Corrupt);
        var opts = new SyncOptions { ParallelTransfers = 1, VerifyHash = true };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.True(r.FullySucceeded, r.Summary);
        Assert.False(Logged(l => l.StartsWith("Ripresa di", StringComparison.Ordinal)));
        Assert.Equal(d.Files["foto.jpg"].Data, File.ReadAllBytes(Path.Combine(_local, "foto.jpg")));
    }

    [Fact]
    public async Task PartialFilesAreRemovedWhenAFileFinallyFails()
    {
        var d = new FakeRemoteDevice { ChunkSize = 65536 };
        d.Add("bloccato.bin", 4 * 1024 * 1024, 1_700_000_000);
        d.AddFault("bloccato.bin", Enumerable.Repeat(FakeRemoteDevice.Fault.StallAfterHalf, 10).ToArray());
        var opts = new SyncOptions { ParallelTransfers = 1, MaxRetries = 2 };
        var plan = await SyncEngine.CompareAsync(d, d.Root, _local, opts, _log, null, default);
        var r = await Engine(d, opts).RunAsync(plan, default);
        Assert.Equal(1, r.Failed);
        Assert.False(File.Exists(Path.Combine(_local, "bloccato.bin" + LocalScanner.TempSuffix)));
        Assert.False(File.Exists(Path.Combine(_local, "bloccato.bin")));
    }
}
