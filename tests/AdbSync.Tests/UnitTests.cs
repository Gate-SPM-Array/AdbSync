using System.Diagnostics;
using AdbSync.Core;

namespace AdbSync.Tests;

public class ScanParserTests
{
    private static RemoteScanResult Parse(params string[] lines)
    {
        var p = new RemoteScanParser();
        foreach (var l in lines) p.Feed(l);
        return p.Complete();
    }

    /// <summary>Output completo e coerente: trovati = dichiarati = ricevuti, blocchi chiusi, esiti 0.</summary>
    private static string[] Ok(params string[] entries) =>
        new[] { "__ADBSYNC_FINDEXIT__:0", $"__ADBSYNC_FOUND__:{entries.Length}", $"__ADBSYNC_BATCH__:{entries.Length}" }
            .Concat(entries)
            .Concat(new[] { "__ADBSYNC_BEND__:0", "__ADBSYNC_XARGSEXIT__:0", "__ADBSYNC_ERREND__", "__ADBSYNC_EXIT__:0" })
            .ToArray();

    [Fact]
    public void ParsesFilesDirsAndFractions()
    {
        var r = Parse(Ok(
            "directory|4096|1700000000|2023-11-14 22:13:20.500000000 +0100|.",
            "directory|4096|1700000001|2023-11-14 22:13:21.000000000 +0100|./dir con spazio",
            "regular file|5|1791988976|2026-10-14 16:42:56.962147815 +0200|./dir con spazio/a|b's.jpg",
            "regular empty file|0|1709283600|2024-03-01 10:00:00.000000000 +0100|./vuoto.txt\r"));
        Assert.Empty(r.Problems);
        Assert.Equal(new RemoteFile("dir con spazio/a|b's.jpg", 5, 1791988976, 9621478), r.Files[0]);
        Assert.Equal(new RemoteFile("vuoto.txt", 0, 1709283600, 0), r.Files[1]);
        Assert.Equal(2, r.Directories.Count);
        Assert.Equal(new RemoteDir("", 1700000000, 5000000), r.Directories[0]);
        Assert.Equal(4, r.DeclaredEntries);
        Assert.Equal(4, r.ReceivedEntries);
    }

    [Fact]
    public void ArgumentListTooLongMakesScanIncomplete()
    {
        // Esattamente ciò che è successo sul telefono reale con la v0.1.0.
        var r = Parse(
            "__ADBSYNC_FINDEXIT__:0", "__ADBSYNC_FOUND__:502",
            "__ADBSYNC_BATCH__:2", "directory|4096|1|x|.", "regular file|5|1|x|./a.jpg", "__ADBSYNC_BEND__:0",
            "__ADBSYNC_BATCH__:500", "__ADBSYNC_BEND__:126",
            "__ADBSYNC_XARGSEXIT__:123",
            "__ADBSYNC_ERR__:find: exec stat: Argument list too long", "__ADBSYNC_ERREND__",
            "__ADBSYNC_EXIT__:0");
        Assert.Contains(r.Problems, p => p.Contains("Argument list too long"));
        Assert.Contains(r.Problems, p => p.Contains("502") && p.Contains("2"));
    }

    [Fact]
    public void CountMismatchWithoutAnyErrorMessageIsStillDetected()
    {
        var lines = Ok("directory|4096|1|x|.", "regular file|5|1|x|./a.jpg").ToList();
        lines[2] = "__ADBSYNC_BATCH__:3";
        var r = Parse(lines.ToArray());
        Assert.Contains(r.Problems, p => p.Contains("conteggio"));
    }

    [Fact]
    public void SilentlyKilledBatchIsDetectedByIndependentCount()
    {
        // Caso trovato in revisione: il processo di un blocco viene ucciso prima di scrivere qualunque cosa.
        // Intestazioni e righe coincidono tra loro, ma il conteggio indipendente di find no.
        var lines = Ok("directory|4096|1|x|.", "regular file|5|1|x|./a.jpg").ToList();
        lines[1] = "__ADBSYNC_FOUND__:502";
        var r = Parse(lines.ToArray());
        Assert.Contains(r.Problems, p => p.Contains("find ha trovato 502"));
    }

    [Fact]
    public void UnclosedBatchKilledProcessAndMissingErrorEndAreDetected()
    {
        var lines = Ok("directory|4096|1|x|.").ToList();
        lines.Remove("__ADBSYNC_BEND__:0");
        lines[lines.IndexOf("__ADBSYNC_XARGSEXIT__:0")] = "__ADBSYNC_XARGSEXIT__:137";
        lines.Remove("__ADBSYNC_ERREND__");
        var r = Parse(lines.ToArray());
        Assert.Contains(r.Problems, p => p.Contains("mai chiusi"));
        Assert.Contains(r.Problems, p => p.Contains("137"));
        Assert.Contains(r.Problems, p => p.Contains("elenco degli errori"));
    }

    [Fact]
    public void UnreadableRootIsAProblemNotAnEmptyFolder()
    {
        var lines = Ok("directory|4096|1|x|.").ToList();
        lines[0] = "__ADBSYNC_FINDEXIT__:1";
        lines.Insert(lines.IndexOf("__ADBSYNC_ERREND__"), "__ADBSYNC_ERR__:find: .: Permission denied");
        Assert.Contains(Parse(lines.ToArray()).Problems, p => p.Contains("permesso negato"));
    }

    [Fact]
    public void PermissionDeniedOnlyToleratedInExactFormat()
    {
        var lines = Ok("directory|4096|1|x|.").ToList();
        lines.Insert(lines.IndexOf("__ADBSYNC_ERREND__"), "__ADBSYNC_ERR__:find: ./Permission denied/x: I/O error");
        Assert.Contains(Parse(lines.ToArray()).Problems, p => p.Contains("I/O error"));
    }

    [Fact]
    public void PermissionDeniedAndVanishedAreTolerated()
    {
        var r = Parse(
            "__ADBSYNC_FINDEXIT__:1", "__ADBSYNC_FOUND__:3",
            "__ADBSYNC_BATCH__:3", "directory|4096|1|x|.", "directory|4096|1|x|./data", "__ADBSYNC_BEND__:1",
            "__ADBSYNC_XARGSEXIT__:123",
            "__ADBSYNC_ERR__:find: ./data/com.lemon.lvoverseas/files/frame_cache: Permission denied",
            "__ADBSYNC_ERR__:stat: './sparito.jpg': No such file or directory",
            "__ADBSYNC_ERREND__",
            "__ADBSYNC_EXIT__:0");
        Assert.Empty(r.Problems);
        Assert.Equal("data/com.lemon.lvoverseas/files/frame_cache", r.PermissionDenied.Single());
        Assert.Single(r.Vanished);
    }

    [Fact]
    public void MissingEndMarkerOrNoDataIsIncomplete()
    {
        Assert.Contains(Parse(Ok("directory|4096|1|x|.").SkipLast(1).ToArray()).Problems, p => p.Contains("interrotta"));
        Assert.Contains(Parse("__ADBSYNC_FOUND__:0", "__ADBSYNC_XARGSEXIT__:0", "__ADBSYNC_ERREND__", "__ADBSYNC_EXIT__:0").Problems, p => p.Contains("alcun blocco"));
        Assert.True(Parse("__ADBSYNC_CDERR__").RootNotFound);
    }

    [Fact]
    public void GarbledLineIsIncomplete()
    {
        var r = Parse(Ok("directory|4096|1|x|.", "regular file|5|1|x|./nome con", "a capo.jpg"));
        Assert.Contains(r.Problems, p => p.Contains("non interpretabili"));
    }

    [Theory]
    [InlineData("2024-03-01 10:00:00.123456789 +0100", 1234567)]
    [InlineData("2024-03-01 10:00:00.5 +0100", 5000000)]
    [InlineData("2024-03-01 10:00:00 +0100", 0)]
    public void ParsesFractions(string human, int expected) => Assert.Equal(expected, RemoteShell.ParseFraction(human));

    [Fact]
    public void QuotesForShell() =>
        Assert.Equal("'/sdcard/it'\\''s here'", RemoteShell.Quote("/sdcard/it's here"));

    [Fact]
    public void ParsesStatAndMd5()
    {
        Assert.True(RemoteShell.TryParseStat("1234|1700000000|2023-11-14 22:13:20.250000000 +0100\n", out var s, out var m, out var f));
        Assert.Equal((1234, 1700000000, 2500000), (s, m, f));
        Assert.False(RemoteShell.TryParseStat("stat: '/x': No such file or directory", out _, out _, out _));
        Assert.Equal("14ac2f2fef41919cdcc53c6aefd9aeb2", RemoteShell.ParseMd5("14AC2F2FEF41919CDCC53C6AEFD9AEB2  /sdcard/x.jpg\r\n"));
        Assert.Null(RemoteShell.ParseMd5("md5sum: /x: No such file or directory"));
    }
}

/// <summary>
/// Esegue DAVVERO il comando di scansione con sh + toybox (lo stesso find/stat/xargs di Android),
/// su un albero con decine di migliaia di file dai percorsi lunghi come quelli di WhatsApp.
/// </summary>
public sealed class RealShellScanTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "adbsync_shell_" + Guid.NewGuid().ToString("N"));
    private readonly string _bin;
    private readonly string _root;
    private const string Toybox = "/usr/bin/toybox";

    public RealShellScanTests()
    {
        _bin = Path.Combine(_work, "bin");
        _root = Path.Combine(_work, "sdcard", "Android");
        Directory.CreateDirectory(_bin);
        if (!File.Exists(Toybox)) return;
        foreach (var tool in new[] { "find", "stat", "xargs" })
            File.CreateSymbolicLink(Path.Combine(_bin, tool), Toybox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch { }
    }

    private bool Available => !OperatingSystem.IsWindows() && File.Exists(Toybox);

    private int BuildTree(int files)
    {
        string wa = Path.Combine(_root, "media", "com.whatsapp", "WhatsApp", "Media", "WhatsApp Images");
        Directory.CreateDirectory(wa);
        Directory.CreateDirectory(Path.Combine(_root, "media", "cartella vuota"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "app.con'apice"));
        File.WriteAllText(Path.Combine(_root, "data", "app.con'apice", "x | y $z.txt"), "ciao");
        for (int i = 0; i < files; i++)
            File.WriteAllBytes(Path.Combine(wa, $"IMG-2018{i % 12 + 1:00}{i % 28 + 1:00}-WA{i:00000}.jpg"), Array.Empty<byte>());
        File.SetLastWriteTimeUtc(Path.Combine(_root, "data", "app.con'apice", "x | y $z.txt"), new DateTime(2024, 3, 1, 9, 0, 0, 250, DateTimeKind.Utc));
        return files + 1;
    }

    /// <summary>Rende una cartella illeggibile (come Android/data) se i permessi lo consentono (non come root).</summary>
    private RemoteScanResult RunScanAsUnprivileged(string tmpDir, string lockedDir)
    {
        try { File.SetUnixFileMode(lockedDir, UnixFileMode.None); } catch { }
        try { return RunScan(tmpDir); }
        finally { try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { } }
    }

    private RemoteScanResult RunScan(string tmpDir, string? extraBinFirst = null)
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(RemoteShell.BuildScanCommand(_root, tmpDir));
        psi.Environment["PATH"] = (extraBinFirst is null ? "" : extraBinFirst + ":") + _bin + ":/usr/bin:/bin";
        psi.Environment["TMPDIR"] = "";
        using var p = Process.Start(psi)!;
        var parser = new RemoteScanParser();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null) parser.Feed(line);
        p.WaitForExit();
        return parser.Complete();
    }

    [Fact]
    public void FindsEveryFileAndFolderWithTempDir()
    {
        if (!Available) return;
        int expectedFiles = BuildTree(36_000);
        string tmp = Path.Combine(_work, "tmp");
        Directory.CreateDirectory(tmp);
        var r = RunScan(tmp);
        Assert.Empty(r.Problems);
        Assert.Equal(expectedFiles, r.Files.Count);
        Assert.Contains(r.Directories, d => d.RelativePath == "media/cartella vuota");
        Assert.Contains(r.Directories, d => d.RelativePath == "");
        Assert.Equal(r.DeclaredEntries, r.ReceivedEntries);
        var special = r.Files.Single(f => f.RelativePath == "data/app.con'apice/x | y $z.txt");
        Assert.Equal(2500000, special.MtimeFraction); // 250 ms conservati
        Assert.Empty(Directory.GetFiles(tmp)); // file temporaneo rimosso
    }

    [Fact]
    public void FindsEveryFileWithoutTempDir()
    {
        if (!Available) return;
        int expectedFiles = BuildTree(5_000);
        string locked = Path.Combine(_root, "data", "bloccata");
        Directory.CreateDirectory(Path.Combine(locked, "sub"));
        var r = RunScanAsUnprivileged("/percorso/inesistente", locked);
        Assert.True(r.UsedMergedOutput);
        Assert.Empty(r.Problems);
        Assert.Equal(expectedFiles, r.Files.Count);
        Assert.Contains(r.Directories, d => d.RelativePath == "data/bloccata");
    }

    [Fact]
    public void FailingBatchIsDetected()
    {
        if (!Available) return;
        BuildTree(5_000);
        // stat "difettoso": rifiuta i blocchi che contengono un certo file, come "Argument list too long" sul telefono
        string bad = Path.Combine(_work, "badbin");
        Directory.CreateDirectory(bad);
        string script = Path.Combine(bad, "stat");
        File.WriteAllText(script,
            "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in *WA02345*) echo \"stat: Argument list too long\" >&2; exit 126;; esac; done\nexec " + Toybox + " stat \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        string tmp = Path.Combine(_work, "tmp2");
        Directory.CreateDirectory(tmp);
        var r = RunScan(tmp, bad);
        Assert.NotEmpty(r.Problems);
        Assert.Contains(r.Problems, p => p.Contains("Argument list too long"));
        Assert.Contains(r.Problems, p => p.Contains("conteggio"));
    }

    private string MakeScript(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        return path;
    }

    [Fact]
    public void WorksWithMkshTheAndroidShell()
    {
        if (!Available || !File.Exists("/usr/bin/mksh")) return;
        int expected = BuildTree(8_000);
        string mk = Path.Combine(_work, "mkbin");
        Directory.CreateDirectory(mk);
        File.CreateSymbolicLink(Path.Combine(mk, "sh"), "/usr/bin/mksh"); // su Android "sh" è mksh
        foreach (var tmpDir in new[] { Path.Combine(_work, "tmpmk"), "/percorso/inesistente" })
        {
            Directory.CreateDirectory(Path.Combine(_work, "tmpmk"));
            var psi = new ProcessStartInfo("/usr/bin/mksh") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(RemoteShell.BuildScanCommand(_root, tmpDir));
            psi.Environment["PATH"] = mk + ":" + _bin + ":/usr/bin:/bin";
            psi.Environment["TMPDIR"] = "";
            using var p = Process.Start(psi)!;
            var parser = new RemoteScanParser();
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null) parser.Feed(line);
            var r = parser.Complete();
            Assert.Empty(r.Problems);
            Assert.Equal(expected, r.Files.Count);
        }
    }

    [Fact]
    public void BatchKilledSilentlyIsDetected()
    {
        if (!Available) return;
        BuildTree(5_000);
        // sh "difettoso": il 3° blocco viene ucciso prima di scrivere qualunque cosa (scenario della revisione)
        string bad = Path.Combine(_work, "killbin");
        string counter = Path.Combine(_work, "count");
        MakeScript(bad, "sh", $"c=$(cat '{counter}' 2>/dev/null || echo 0); c=$((c+1)); echo $c > '{counter}'; [ $c -eq 3 ] && kill -9 $$; exec /bin/sh \"$@\"");
        string tmp = Path.Combine(_work, "tmp3");
        Directory.CreateDirectory(tmp);
        var r = RunScan(tmp, bad);
        Assert.NotEmpty(r.Problems);
        Assert.True(r.FoundEntries > r.DeclaredEntries, $"trovati {r.FoundEntries}, dichiarati {r.DeclaredEntries}");
    }

    [Fact]
    public void WorksWhenTrIsMissing()
    {
        if (!Available) return;
        int expected = BuildTree(3_000);
        string bad = Path.Combine(_work, "notr");
        MakeScript(bad, "tr", "exit 127");
        string tmp = Path.Combine(_work, "tmp4");
        Directory.CreateDirectory(tmp);
        var r = RunScan(tmp, bad);
        Assert.Empty(r.Problems);
        Assert.Equal(expected, r.Files.Count);
        Assert.Equal(r.FoundEntries, r.ReceivedEntries);
    }

    [Fact]
    public async Task HashBatchMatchesLocalHashInRealShell()
    {
        if (!Available) return;
        foreach (var tool in new[] { "head", "tail", "md5sum" })
            if (!File.Exists(Path.Combine(_bin, tool))) File.CreateSymbolicLink(Path.Combine(_bin, tool), Toybox);
        Directory.CreateDirectory(_root);
        var rnd = new Random(7);
        var files = new Dictionary<string, int>
        {
            ["piccolo.opus"] = 10, ["medio 100k.jpg"] = 100_000, ["grande.mp4"] = 300_000, ["-inizia col trattino'apice.txt"] = 70_000,
            ["vuoto.bin"] = 0,
        };
        foreach (var (name, size) in files)
        {
            var data = new byte[size];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_root, name), data);
        }
        foreach (bool quick in new[] { true, false })
        {
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(RemoteShell.BuildHashBatchCommand(_root, files.Keys.Append("non-esiste.jpg"), quick));
            psi.Environment["PATH"] = _bin + ":/usr/bin:/bin";
            using var p = Process.Start(psi)!;
            var lines = new List<string>();
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null) lines.Add(line);
            var remote = RemoteShell.ParseHashBatch(lines, out bool complete);
            Assert.True(complete);
            foreach (var name in files.Keys)
                Assert.Equal(await SyncEngine.LocalHash(Path.Combine(_root, name), quick, default), remote[name]);
            Assert.Null(remote["non-esiste.jpg"]);
        }
    }

    [Fact]
    public void MissingRootIsReported()
    {
        if (!Available) return;
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(RemoteShell.BuildScanCommand(_root + "/non-esiste"));
        using var p = Process.Start(psi)!;
        var parser = new RemoteScanParser();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null) parser.Feed(line);
        Assert.True(parser.Complete().RootNotFound);
    }
}

public class PathMapperTests
{
    [Theory]
    [InlineData("DCIM/Camera/IMG_001.jpg", "DCIM/Camera/IMG_001.jpg", false)]
    [InlineData("Note/riunione: 10?.txt", "Note/riunione： 10？.txt", true)]
    [InlineData("a/con./CON.txt", "a/con．/_CON.txt", true)]
    [InlineData("x/nome con spazio finale ", "x/nome con spazio finale␣", true)]
    [InlineData("x/aux", "x/_aux", true)]
    public void MapsToWindowsSafeNames(string remote, string expected, bool changed)
    {
        var (local, ch) = PathMapper.ToLocalRelative(remote);
        Assert.Equal(expected, local);
        Assert.Equal(changed, ch);
    }
}

public class PathFilterTests
{
    private static readonly PathFilter Default = new(SyncOptions.DefaultExcludes);

    [Theory]
    [InlineData("Camera/.thumbnails/123.jpg", true)]
    [InlineData(".thumbnails/123.jpg", true)]
    [InlineData("Camera/.trashed-1690000000-IMG_1.jpg", true)]
    [InlineData("Camera/.pending-1-IMG.jpg", true)]
    [InlineData("Camera/IMG_1.jpg.adbsync-part", true)]
    [InlineData("Camera/IMG_1.jpg", false)]
    [InlineData("thumbnails/IMG_1.jpg", false)]
    public void DefaultExcludes(string path, bool excluded) => Assert.Equal(excluded, Default.IsExcluded(path));

    [Fact]
    public void AnchoredAndGlobPatterns()
    {
        var f = new PathFilter(new[] { "WhatsApp/Media/.Statuses/", "*.TMP", "Backups/**/old_*" });
        Assert.True(f.IsExcluded("WhatsApp/Media/.Statuses/a.jpg"));
        Assert.False(f.IsExcluded("Altro/WhatsApp/Media/.Statuses/a.jpg"));
        Assert.True(f.IsExcluded("x/y/file.tmp"));
        Assert.True(f.IsExcluded("Backups/2024/03/old_db.crypt"));
        Assert.False(f.IsExcluded("Backups/2024/new_db.crypt"));
    }
}

public class ClassifyTests
{
    private static readonly SyncOptions O = new();
    private static RemoteFile R(long size, long t) => new("a.jpg", size, t);
    private static LocalFile L(long size, long t) => new("a.jpg", size, t, t);

    [Fact] public void New() => Assert.Equal(ItemCategory.New, PlanBuilder.Classify(R(10, 1000), null, O));
    [Fact] public void IdenticalWithinTolerance() => Assert.Equal(ItemCategory.Identical, PlanBuilder.Classify(R(10, 1000), L(10, 1002), O));
    [Fact] public void IdenticalDstShift() => Assert.Equal(ItemCategory.Identical, PlanBuilder.Classify(R(10, 1000), L(10, 4600), O));
    [Fact] public void DstShiftDisabled() => Assert.Equal(ItemCategory.DateOnly, PlanBuilder.Classify(R(10, 1000), L(10, 4600), new SyncOptions { IgnoreDstShift = false }));
    [Fact] public void PhoneNewer() => Assert.Equal(ItemCategory.Changed, PlanBuilder.Classify(R(12, 5000), L(10, 1000), O));
    [Fact] public void SameSizePhoneNewer() => Assert.Equal(ItemCategory.Changed, PlanBuilder.Classify(R(10, 9000), L(10, 1000), O));
    [Fact] public void SameDateDifferentSize() => Assert.Equal(ItemCategory.Changed, PlanBuilder.Classify(R(12, 1000), L(10, 1000), O));
    [Fact] public void PcNewerDifferentSize() => Assert.Equal(ItemCategory.Conflict, PlanBuilder.Classify(R(12, 1000), L(10, 9000), O));
    [Fact] public void PcNewerSameSize() => Assert.Equal(ItemCategory.DateOnly, PlanBuilder.Classify(R(10, 1000), L(10, 9000), O));
    [Fact] public void SizeOnlyMode() => Assert.Equal(ItemCategory.Identical, PlanBuilder.Classify(R(10, 1000), L(10, 9000), new SyncOptions { CompareMode = CompareMode.SizeOnly }));

    [Fact]
    public void IdenticalWithWrongCreationDateGetsTimeFix()
    {
        var local = new LocalScanResult
        {
            Files = new(StringComparer.OrdinalIgnoreCase) { ["a.jpg"] = new("a.jpg", 10, 1000, 5_000_000) },
            StaleTempFiles = new(),
        };
        var plan = PlanBuilder.Build("/sdcard", "/x", new[] { R(10, 1000) }, local, O);
        Assert.Equal(ItemAction.FixTimes, plan.Items.Single().Action);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void CollisionAndOnlyOnPc()
    {
        var remote = new[] { new RemoteFile("Foto/A.jpg", 1, 1), new RemoteFile("Foto/a.jpg", 2, 1) };
        var local = new LocalScanResult
        {
            Files = new(StringComparer.OrdinalIgnoreCase) { ["Solo/pc.txt"] = new("Solo/pc.txt", 1, 1, 1) },
            StaleTempFiles = new(),
        };
        var plan = PlanBuilder.Build("/sdcard", "/x", remote, local, O);
        Assert.Equal(1, plan.Count(ItemCategory.New));
        Assert.Equal(1, plan.Count(ItemCategory.Collision));
        Assert.Equal(1, plan.Count(ItemCategory.OnlyOnPc));
        Assert.Equal(1, plan.CountToCopy);
    }
}
