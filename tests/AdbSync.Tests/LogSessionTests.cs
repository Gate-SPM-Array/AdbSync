using System.Diagnostics;
using AdbSync.Core;
using Xunit;

namespace AdbSync.Tests;

public sealed class LogSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adbsync_logs_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly DateTime Start = new(2026, 10, 16, 14, 30, 5);

    [Fact]
    public void FolderNameHasDateModelAndPhoneNameButNoSerial()
    {
        var id = new DeviceIdentity("Xiaomi", "2201117TG", "Telefono di Mario", "14");
        Assert.Equal("2026-10-16_14-30-05_Xiaomi-2201117TG_Telefono-di-Mario", LogSessions.FolderName(Start, id));
        Assert.Equal("2026-10-16_14-30-05_Xiaomi-2201117TG_Telefono-di-Mario_cdef",
            LogSessions.FolderName(Start, id, LogSessions.SerialSuffix("ab12-cdef")));
    }

    [Theory]
    [InlineData("samsung", "SM-G991B", null, "2026-10-16_14-30-05_Samsung-SM-G991B")]
    [InlineData("Google", "Google Pixel 7", "Pixel 7", "2026-10-16_14-30-05_Google-Pixel-7_Pixel-7")]
    [InlineData("Google", "Pixel 7", "Pixel 7", "2026-10-16_14-30-05_Google-Pixel-7")]          // nome uguale al modello: non ripetuto
    [InlineData("", "Mi_9", "Ciò: «mio» / <telefono>?", "2026-10-16_14-30-05_Mi-9_Ciò-mio-telefono")] // caratteri non validi su Windows
    [InlineData("", "", null, "2026-10-16_14-30-05_telefono")]
    public void FolderNameIsAlwaysValidOnWindows(string maker, string model, string? name, string expected)
    {
        string folder = LogSessions.FolderName(Start, new DeviceIdentity(maker, model, name, null));
        Assert.Equal(expected, folder);
        Assert.True(LogSessions.IsSessionFolder(folder));
        Assert.DoesNotContain(folder, c => "<>:\"/\\|?* ".Contains(c));
    }

    [Fact]
    public void LongNamesAreShortened()
    {
        string folder = LogSessions.FolderName(Start, new DeviceIdentity("", new string('x', 200), new string('y', 200), null));
        Assert.True(folder.Length <= 20 + 40 + 1 + 40, folder);
    }

    [Fact]
    public void IdentityOutputIsParsedLineByLine()
    {
        var id = DeviceIdentity.Parse("m=Xiaomi\r\np=2201117TG\r\nn=null\r\nb=Il mio Xiaomi\r\na=14\r\n", "fallback");
        Assert.Equal(new DeviceIdentity("Xiaomi", "2201117TG", "Il mio Xiaomi", "14"), id);  // device_name assente: nome Bluetooth
        Assert.Equal("Xiaomi 2201117TG «Il mio Xiaomi», Android 14", id.Describe());
        var empty = DeviceIdentity.Parse("garbage\n", "Pixel 7");
        Assert.Equal("Pixel 7", empty.Model);
        Assert.Null(empty.Name);
    }

    [Fact]
    public void IdentityCommandWorksInMksh()
    {
        if (!File.Exists("/usr/bin/mksh")) return;
        // getprop e settings non esistono fuori da Android: funzioni finte con lo stesso comportamento
        string script = """
            getprop() { case "$1" in ro.product.manufacturer) printf 'Xiaomi\n';; ro.product.model) printf '2201117TG\n';; ro.build.version.release) printf '14\n';; esac; }
            settings() { case "$3" in device_name) printf 'Telefono di Mario\n';; *) printf 'null\n';; esac; }
            """ + "\n" + DeviceIdentity.Command;
        var psi = new ProcessStartInfo("/usr/bin/mksh") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(new DeviceIdentity("Xiaomi", "2201117TG", "Telefono di Mario", "14"), DeviceIdentity.Parse(output, "x"));
    }

    [Fact]
    public void BeforeASessionEverythingGoesToTheSystemLog()
    {
        using var log = SyncLogger.ForApplication(_root, "GUI");
        log.Info("prima della sessione");
        Assert.Null(log.LogDirectory);
        Assert.False(log.HasSession);
        string sys = File.ReadAllText(log.SystemLogPath!);
        Assert.Contains("[GUI]", sys);
        Assert.Contains("prima della sessione", sys);
        Assert.Equal(Path.Combine(_root, SyncLogger.SystemFolderName, $"system_{DateTime.Now:yyyy-MM}.log"), log.SystemLogPath);
    }

    [Fact]
    public void SessionGetsItsOwnFolderAndSystemMessagesGoToBoth()
    {
        using var log = SyncLogger.ForApplication(_root, "GUI");
        string dir = log.StartSession("2026-10-16_14-30-05_Xiaomi-2201117TG")!;
        Assert.Equal(Path.Combine(_root, "2026-10-16_14-30-05_Xiaomi-2201117TG"), dir);
        Assert.Equal(dir, log.LogDirectory);   // report, nomi convertiti e diagnostica vanno qui
        log.Info("copia di foto.jpg");
        log.System(LogLevel.Warning, "telefono scollegato");
        log.Dispose();

        string session = File.ReadAllText(Path.Combine(dir, SyncLogger.SessionLogFileName));
        string sys = File.ReadAllText(Directory.GetFiles(Path.Combine(_root, SyncLogger.SystemFolderName)).Single());
        Assert.Contains("copia di foto.jpg", session);
        Assert.Contains("telefono scollegato", session);
        Assert.DoesNotContain("copia di foto.jpg", sys);      // i dettagli della copia restano nella sessione
        Assert.Contains("telefono scollegato", sys);
        Assert.Contains(dir, sys);                            // il log di sistema dice quale sessione si è aperta
    }

    [Fact]
    public void ANewSessionNeverReusesAnExistingFolder()
    {
        using var log = SyncLogger.ForApplication(_root, "CLI");
        string a = log.StartSession("2026-10-16_14-30-05_X")!;
        log.Info("uno");
        string b = log.StartSession("2026-10-16_14-30-05_X")!;
        log.Info("due");
        log.Dispose();
        Assert.Equal(a + "_2", b);
        Assert.DoesNotContain("due", File.ReadAllText(Path.Combine(a, SyncLogger.SessionLogFileName)));
        Assert.Contains("due", File.ReadAllText(Path.Combine(b, SyncLogger.SessionLogFileName)));
    }

    [Fact]
    public void TwoProgramsCanWriteTheSystemLogTogether()
    {
        using var gui = SyncLogger.ForApplication(_root, "GUI");
        using var cli = SyncLogger.ForApplication(_root, "CLI");
        Parallel.For(0, 200, i => (i % 2 == 0 ? gui : cli).Info("riga " + i));
        var lines = File.ReadAllLines(gui.SystemLogPath!);
        Assert.Equal(200, lines.Length);
        Assert.Equal(100, lines.Count(l => l.Contains("[CLI]")));
    }

    [Fact]
    public void LogFolderStatsCountSessionsAndTotalSize()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(default, LogSessions.Inspect(Path.Combine(_root, "manca")));
        using (var log = SyncLogger.ForApplication(_root, "GUI"))
        {
            log.StartSession("2026-10-15_09-00-00_Xiaomi-2201117TG");
            log.Info(new string('a', 1000));
            log.StartSession("2026-10-16_10-00-00_Xiaomi-2201117TG");
            log.Info(new string('b', 1000));
        }
        File.WriteAllText(Path.Combine(_root, "adbsync_20261014_120000.log"), new string('c', 500));   // versione precedente
        Directory.CreateDirectory(Path.Combine(_root, "diagnostica_20261014_120000"));
        var st = LogSessions.Inspect(_root);
        Assert.Equal(2, st.Sessions);
        Assert.Equal(2, st.OlderFiles);
        long expected = new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        Assert.Equal(expected, st.Bytes);
        Assert.True(st.Bytes > 2500);
    }
}
