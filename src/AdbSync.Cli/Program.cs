using AdbSync.Core;
using AdbSync.Core.Adb;

namespace AdbSync.Cli;

internal static class Program
{
    private const string AppVersion = "0.1.6";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var langMessages = L.Load(LanguageFromArgs(args));
        foreach (var m in langMessages) Console.Error.WriteLine(m);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine($"AdbSync v{AppVersion}");
            Console.WriteLine(L.T("""
            AdbSync (riga di comando) — copia incrementale telefono → PC via ADB

            Uso:
              adbsync-cli devices
              adbsync-cli compare  -r <cartella telefono> -l <cartella PC> [opzioni]
              adbsync-cli sync     -r <cartella telefono> -l <cartella PC> [opzioni] [--yes]
              adbsync-cli setup-adb          (scarica le platform-tools ufficiali Google)

            Opzioni:
              -s, --serial <id>          dispositivo da usare (se ne sono collegati più di uno)
              --adb <percorso>           percorso di adb(.exe)
              --size-only                confronta solo la dimensione (non la data)
              --no-dst                   non ignorare le differenze di esattamente 1 ora
              --overwrite-conflicts      sovrascrivi anche i file più recenti sul PC
              --exclude <modello>        esclusione aggiuntiva (ripetibile), es. --exclude "WhatsApp/Media/.Statuses/"
              --no-default-excludes      non applicare le esclusioni predefinite
              --parallel <1-4>           trasferimenti contemporanei (predefinito 2)
              --stall <secondi>          timeout di stallo del telefono, minimo 1 (predefinito 30): il file bloccato torna in fondo alla coda
              --disk-stall <secondi>     limite di una scrittura sul disco del PC, minimo 30 (predefinito 120)
              --align-dates <livello>    file con stesso nome e dimensione ma data diversa: allinea la data sul PC
                                         none = no (predefinito) | size = solo dimensione
                                         quick = verifica primi+ultimi 64 KB (MD5) | full = verifica MD5 completo
              --retries <n>              tentativi aggiuntivi per file (predefinito 5)
              --wait-device <secondi>    attesa massima di riconnessione (predefinito 300)
              --verify                   verifica MD5 di ogni file copiato
              --diagnostic               salva gli elenchi completi file per file nella cartella logs
              --v2                       usa il protocollo di trasferimento ADB v2 (come adb pull)
              --lang <codice>            lingua dei messaggi (it, en, ... = file lang\<codice>.json); predefinita: quella dell'interfaccia grafica
              --list                     (compare) elenca i file da copiare
              --yes                      (sync) non chiedere conferma

            Codici di uscita: 0 = tutto eseguito, 1 = errori su alcuni file, 2 = interrotto o scansione incompleta, 3 = errore di utilizzo/avvio.
            Dati (impostazioni, log, platform-tools): nella cartella del programma (portable).
            """));
            return args.Length == 0 ? 3 : 0;
        }

        Args a;
        try { a = Args.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(L.F("Errore: {0}", ex.Message)); Console.Error.WriteLine(L.T("Usa --help per l'elenco delle opzioni.")); return 3; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // secondo Ctrl+C: uscita immediata
            e.Cancel = true;
            Console.Error.WriteLine("\n" + L.T("Annullamento in corso (attendo la chiusura pulita dei trasferimenti)... premi di nuovo Ctrl+C per forzare."));
            cts.Cancel();
        };

        try
        {
            foreach (var m in AppPaths.MigrateLegacy()) Console.WriteLine(m);
            if (a.Command == "setup-adb")
            {
                var path = await AdbEnvironment.DownloadPlatformToolsAsync(new InlineProgress<string>(Console.WriteLine), cts.Token);
                Console.WriteLine(L.F("adb pronto: {0}", path));
                return 0;
            }

            string? adbPath = AdbEnvironment.Locate(a.AdbPath);
            if (adbPath is null)
            {
                Console.Error.WriteLine(L.T("adb non trovato. Esegui 'adbsync-cli setup-adb' oppure indica il percorso con --adb."));
                return 3;
            }
            var env = await AdbEnvironment.StartAsync(adbPath, cts.Token);

            var devices = await env.GetDevicesAsync(cts.Token);
            if (a.Command == "devices")
            {
                if (devices.Count == 0) Console.WriteLine(L.T("Nessun dispositivo collegato (controlla cavo e 'Debug USB')."));
                foreach (var d in devices) Console.WriteLine($"{d.Serial}\t{d.Model}\t{d.StateLabel}");
                return 0;
            }

            if (a.Remote is null || a.Local is null) throw new ArgumentException(L.T("servono -r (cartella telefono) e -l (cartella PC)"));

            DeviceInfo? dev = a.Serial is not null
                ? devices.FirstOrDefault(d => d.Serial == a.Serial)
                : devices.Count == 1 ? devices[0] : null;
            if (dev is null)
            {
                Console.Error.WriteLine(devices.Count == 0
                    ? L.T("Nessun dispositivo collegato.")
                    : a.Serial is null ? L.T("Più dispositivi collegati: specifica --serial.") : L.F("Dispositivo {0} non trovato.", a.Serial));
                return 3;
            }
            if (!dev.IsOnline)
            {
                Console.Error.WriteLine($"{dev.Model}: {dev.StateLabel}");
                return 3;
            }

            using var log = new SyncLogger(SyncLogger.DefaultLogDirectory);
            log.EntryWritten += e =>
            {
                if (e.Level == LogLevel.Debug) return;
                ClearStatusLine();
                (e.Level >= LogLevel.Warning ? Console.Error : Console.Out).WriteLine(e.ToString());
            };
            var device = env.OpenDevice(dev.Serial, dev.ToString());
            var options = a.ToOptions();

            var plan = await SyncEngine.CompareAsync(device, a.Remote, a.Local, options, log, StatusLine, cts.Token);
            ClearStatusLine();

            if (a.Command == "compare")
            {
                if (a.List)
                {
                    foreach (var d in plan.Directories.Where(d => d.NeedsCreate))
                        Console.WriteLine($"{L.T("Cartella"),-9} {"",10}  {d.RelativePath}/");
                    foreach (var i in plan.Items.Where(i => i.Action != ItemAction.Skip))
                        Console.WriteLine($"{(i.Action == ItemAction.FixTimes ? L.T("Date") : i.Action == ItemAction.VerifyAndFixTimes ? L.T("Verifica") : i.Category.ToString()),-9} {Format.Bytes(i.RemoteSize),10}  {i.RelativePath}");
                }
                Console.WriteLine(L.F("Da fare: {0}.", plan.WorkSummary));
                if (plan.ExcludedCount + plan.ExcludedLocalCount > 0)
                    Console.WriteLine(L.F("Esclusi dalle regole: {0} file del telefono, {1} solo sul PC (vedi log).", Format.Number(plan.ExcludedCount), Format.Number(plan.ExcludedLocalCount)));
                return 0;
            }

            if (!plan.HasWork) { Console.WriteLine(L.T("Già allineato: niente da fare.")); return 0; }
            if (!a.Yes)
            {
                Console.Write(L.F("Da fare: {0}. Procedere? [s/N] ", plan.WorkSummary));
                var ans = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (ans is not ("s" or "si" or "sì" or "y" or "yes")) { Console.WriteLine(L.T("Nessuna copia eseguita.")); return 0; }
            }

            var engine = new SyncEngine(device, options, log);
            var run = engine.RunAsync(plan, cts.Token);
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(1000));
                if (!run.IsCompleted) StatusLine(Describe(engine.Progress));
            }
            var result = await run;
            ClearStatusLine();
            Console.WriteLine();
            Console.WriteLine(result.Summary);
            if (result.RenamedLogPath is not null) Console.WriteLine(L.F("Nomi convertiti per Windows ({0}): {1}", Format.Number(result.RenamedCount), result.RenamedLogPath));
            if (log.LogFilePath is not null) Console.WriteLine(L.F("Log: {0}", log.LogFilePath));
            return result.FullySucceeded ? 0 : result.AbortReason is not null || result.Cancelled ? 2 : 1;
        }
        catch (IncompleteScanException ex)
        {
            ClearStatusLine();
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            ClearStatusLine();
            Console.Error.WriteLine(L.T("Annullato."));
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(L.F("Errore: {0}", ex.Message));
            return 3;
        }
        catch (Exception ex)
        {
            ClearStatusLine();
            Console.Error.WriteLine(L.F("Errore: {0}", ex.Message));
            return 2;
        }
    }

    /// <summary>Lingua: --lang &lt;codice&gt;, altrimenti variabile ADBSYNC_LANG, altrimenti quella scelta nell'interfaccia grafica.</summary>
    private static string? LanguageFromArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--lang");
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        if (Environment.GetEnvironmentVariable("ADBSYNC_LANG") is { Length: > 0 } env) return env;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                if (doc.RootElement.TryGetProperty("Language", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String) return l.GetString();
            }
        }
        catch { /* impostazioni illeggibili: italiano */ }
        return null;
    }

    private static string Describe(SyncProgress p)
    {
        var (bps, eta) = p.Sample();
        long done = p.BytesProcessed;
        double pct = p.TotalBytes > 0 ? done * 100.0 / p.TotalBytes : 100;
        if (p.TotalBytes == 0) return p.Phase + (p.StatusMessage is { } sm ? " | " + sm : "");
        string s = L.F("{0,5:0.0}%  {1}/{2}  {3}/s  ETA {4}  file {5}/{6}",
                       pct, Format.Bytes(done), Format.Bytes(p.TotalBytes), Format.Bytes((long)bps),
                       eta is null ? "--" : Format.Duration(eta.Value), p.DoneFiles, p.TotalFiles) +
                   (p.FailedFiles > 0 ? "  " + L.F("errori {0}", p.FailedFiles) : "") +
                   (p.RetryingFiles > 0 ? "  " + L.F("in ritentativo {0}", p.RetryingFiles) : "") +
                   (p.Stalls > 0 ? "  " + L.F("stalli {0} (in coda {1})", p.Stalls, p.RequeuedFiles) : "");
        return p.StatusMessage is { } m ? m + " | " + s : s;
    }

    private static int _lastLen;
    private static readonly object ConsoleLock = new();

    private static void StatusLine(string s)
    {
        lock (ConsoleLock)
        {
            if (Console.IsOutputRedirected) { Console.WriteLine(s); return; }
            int w = Math.Max(20, SafeWidth() - 1);
            if (s.Length > w) s = s[..(w - 1)] + "…";
            Console.Write("\r" + s.PadRight(Math.Max(_lastLen, s.Length)));
            _lastLen = s.Length;
        }
    }

    private static void ClearStatusLine()
    {
        lock (ConsoleLock)
        {
            if (_lastLen == 0 || Console.IsOutputRedirected) return;
            Console.Write("\r" + new string(' ', _lastLen) + "\r");
            _lastLen = 0;
        }
    }

    private static int SafeWidth() { try { return Console.WindowWidth; } catch { return 120; } }

    private sealed class Args
    {
        public string Command = "";
        public string? Remote, Local, Serial, AdbPath;
        public DateAlignMode Align = DateAlignMode.None;
        public bool SizeOnly, NoDst, OverwriteConflicts, NoDefaultExcludes, Verify, Yes, List, Diagnostic, SyncV2;
        public List<string> Excludes = new();
        public int Parallel = 2, Stall = 30, Retries = 5, WaitDevice = 300, DiskStall = 120;

        public static Args Parse(string[] args)
        {
            var a = new Args { Command = args[0].ToLowerInvariant() };
            if (a.Command is not ("devices" or "compare" or "sync" or "setup-adb"))
                throw new ArgumentException(L.F("comando sconosciuto '{0}'", args[0]));
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(L.F("manca il valore per {0}", args[i]));
                int NextInt() => int.TryParse(Next(), out var v) ? v : throw new ArgumentException(L.F("valore non numerico per {0}", args[i - 1]));
                switch (args[i])
                {
                    case "-r": case "--remote": a.Remote = Next(); break;
                    case "-l": case "--local": a.Local = Next(); break;
                    case "-s": case "--serial": a.Serial = Next(); break;
                    case "--adb": a.AdbPath = Next(); break;
                    case "--size-only": a.SizeOnly = true; break;
                    case "--no-dst": a.NoDst = true; break;
                    case "--overwrite-conflicts": a.OverwriteConflicts = true; break;
                    case "--exclude": a.Excludes.Add(Next()); break;
                    case "--no-default-excludes": a.NoDefaultExcludes = true; break;
                    case "--parallel": a.Parallel = NextInt(); break;
                    case "--stall": a.Stall = NextInt(); if (a.Stall < 1) throw new ArgumentException(L.T("--stall deve essere almeno 1")); break;
                    case "--align-dates":
                        a.Align = Next().ToLowerInvariant() switch
                        {
                            "none" or "no" => DateAlignMode.None,
                            "size" => DateAlignMode.SizeOnly,
                            "quick" => DateAlignMode.Quick,
                            "full" or "md5" => DateAlignMode.Full,
                            var v => throw new ArgumentException(L.F("livello '{0}' non valido per --align-dates (none|size|quick|full)", v)),
                        };
                        break;
                    case "--retries": a.Retries = NextInt(); break;
                    case "--wait-device": a.WaitDevice = NextInt(); break;
                    case "--verify": a.Verify = true; break;
                    case "--diagnostic": a.Diagnostic = true; break;
                    case "--yes": case "-y": a.Yes = true; break;
                    case "--list": a.List = true; break;
                    case "--v2": a.SyncV2 = true; break;
                    case "--disk-stall":
                        a.DiskStall = NextInt();
                        if (a.DiskStall < SyncOptions.MinDiskStallSeconds) throw new ArgumentException(L.F("--disk-stall deve essere almeno {0}", SyncOptions.MinDiskStallSeconds));
                        break;
                    case "--lang": Next(); break; // già gestita all'avvio (LanguageFromArgs)
                    default: throw new ArgumentException(L.F("opzione sconosciuta '{0}'", args[i]));
                }
            }
            return a;
        }

        public SyncOptions ToOptions()
        {
            var o = new SyncOptions
            {
                CompareMode = SizeOnly ? CompareMode.SizeOnly : CompareMode.SizeAndDate,
                IgnoreDstShift = !NoDst,
                OverwriteConflicts = OverwriteConflicts,
                ParallelTransfers = Parallel,
                StallTimeoutSeconds = Stall,
                MaxRetries = Retries,
                DeviceWaitTimeoutSeconds = WaitDevice,
                VerifyHash = Verify,
                Diagnostic = Diagnostic,
                AlignSameSizeDates = Align,
                UseSyncV2 = SyncV2,
                DiskStallTimeoutSeconds = DiskStall,
            };
            if (NoDefaultExcludes) o.ExcludePatterns.Clear();
            o.ExcludePatterns.Add("*" + LocalScanner.TempSuffix);
            o.ExcludePatterns.AddRange(Excludes);
            return o;
        }
    }
}
