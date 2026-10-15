using AdbSync.Core;
using AdbSync.Core.Adb;

namespace AdbSync.Cli;

internal static class Program
{
    private const string Usage = """
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
          --stall <secondi>          timeout di stallo, minimo 1 (predefinito 30): il file bloccato torna in fondo alla coda
          --align-dates <livello>    file con stesso nome e dimensione ma data diversa: allinea la data sul PC
                                     none = no (predefinito) | size = solo dimensione
                                     quick = verifica primi+ultimi 64 KB (MD5) | full = verifica MD5 completo
          --retries <n>              tentativi aggiuntivi per file (predefinito 5)
          --wait-device <secondi>    attesa massima di riconnessione (predefinito 300)
          --verify                   verifica MD5 di ogni file copiato
          --diagnostic               salva gli elenchi completi file per file nella cartella logs
          --list                     (compare) elenca i file da copiare
          --yes                      (sync) non chiedere conferma

        Codici di uscita: 0 = tutto eseguito, 1 = errori su alcuni file, 2 = interrotto o scansione incompleta, 3 = errore di utilizzo/avvio.
        Dati (impostazioni, log, platform-tools): nella cartella del programma (portable).
        """;

    private const string AppVersion = "0.1.2";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine($"AdbSync v{AppVersion}");
            Console.WriteLine(Usage);
            return args.Length == 0 ? 3 : 0;
        }

        Args a;
        try { a = Args.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine("Errore: " + ex.Message); Console.Error.WriteLine("Usa --help per l'elenco delle opzioni."); return 3; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // secondo Ctrl+C: uscita immediata
            e.Cancel = true;
            Console.Error.WriteLine("\nAnnullamento in corso (attendo la chiusura pulita dei trasferimenti)... premi di nuovo Ctrl+C per forzare.");
            cts.Cancel();
        };

        try
        {
            foreach (var m in AppPaths.MigrateLegacy()) Console.WriteLine(m);
            if (a.Command == "setup-adb")
            {
                var path = await AdbEnvironment.DownloadPlatformToolsAsync(new InlineProgress<string>(Console.WriteLine), cts.Token);
                Console.WriteLine("adb pronto: " + path);
                return 0;
            }

            string? adbPath = AdbEnvironment.Locate(a.AdbPath);
            if (adbPath is null)
            {
                Console.Error.WriteLine("adb non trovato. Esegui 'adbsync-cli setup-adb' oppure indica il percorso con --adb.");
                return 3;
            }
            var env = await AdbEnvironment.StartAsync(adbPath, cts.Token);

            var devices = await env.GetDevicesAsync(cts.Token);
            if (a.Command == "devices")
            {
                if (devices.Count == 0) Console.WriteLine("Nessun dispositivo collegato (controlla cavo e 'Debug USB').");
                foreach (var d in devices) Console.WriteLine($"{d.Serial}\t{d.Model}\t{d.StateLabel}");
                return 0;
            }

            if (a.Remote is null || a.Local is null) throw new ArgumentException("servono -r (cartella telefono) e -l (cartella PC)");

            DeviceInfo? dev = a.Serial is not null
                ? devices.FirstOrDefault(d => d.Serial == a.Serial)
                : devices.Count == 1 ? devices[0] : null;
            if (dev is null)
            {
                Console.Error.WriteLine(devices.Count == 0
                    ? "Nessun dispositivo collegato."
                    : a.Serial is null ? "Più dispositivi collegati: specifica --serial." : $"Dispositivo {a.Serial} non trovato.");
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
                        Console.WriteLine($"{"Cartella",-9} {"",10}  {d.RelativePath}/");
                    foreach (var i in plan.Items.Where(i => i.Action != ItemAction.Skip))
                        Console.WriteLine($"{(i.Action == ItemAction.FixTimes ? "Date" : i.Action == ItemAction.VerifyAndFixTimes ? "Verifica" : i.Category.ToString()),-9} {Format.Bytes(i.RemoteSize),10}  {i.RelativePath}");
                }
                Console.WriteLine("Da fare: " + plan.WorkSummary + ".");
                return 0;
            }

            if (!plan.HasWork) { Console.WriteLine("Già allineato: niente da fare."); return 0; }
            if (!a.Yes)
            {
                Console.Write($"Da fare: {plan.WorkSummary}. Procedere? [s/N] ");
                var ans = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (ans is not ("s" or "si" or "sì" or "y" or "yes")) { Console.WriteLine("Nessuna copia eseguita."); return 0; }
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
            if (log.LogFilePath is not null) Console.WriteLine("Log: " + log.LogFilePath);
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
            Console.Error.WriteLine("Annullato.");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Errore: " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            ClearStatusLine();
            Console.Error.WriteLine("Errore: " + ex.Message);
            return 2;
        }
    }

    private static string Describe(SyncProgress p)
    {
        var (bps, eta) = p.Sample();
        long done = p.BytesProcessed;
        double pct = p.TotalBytes > 0 ? done * 100.0 / p.TotalBytes : 100;
        if (p.TotalBytes == 0) return p.Phase + (p.StatusMessage is { } sm ? " | " + sm : "");
        string s = $"{pct,5:0.0}%  {Format.Bytes(done)}/{Format.Bytes(p.TotalBytes)}  {Format.Bytes((long)bps)}/s  " +
                   $"ETA {(eta is null ? "--" : Format.Duration(eta.Value))}  file {p.DoneFiles}/{p.TotalFiles}" +
                   (p.FailedFiles > 0 ? $"  errori {p.FailedFiles}" : "") +
                   (p.RetryingFiles > 0 ? $"  in ritentativo {p.RetryingFiles}" : "") +
                   (p.Stalls > 0 ? $"  stalli {p.Stalls} (in coda {p.RequeuedFiles})" : "");
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
        public bool SizeOnly, NoDst, OverwriteConflicts, NoDefaultExcludes, Verify, Yes, List, Diagnostic;
        public List<string> Excludes = new();
        public int Parallel = 2, Stall = 30, Retries = 5, WaitDevice = 300;

        public static Args Parse(string[] args)
        {
            var a = new Args { Command = args[0].ToLowerInvariant() };
            if (a.Command is not ("devices" or "compare" or "sync" or "setup-adb"))
                throw new ArgumentException($"comando sconosciuto '{args[0]}'");
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"manca il valore per {args[i]}");
                int NextInt() => int.TryParse(Next(), out var v) ? v : throw new ArgumentException($"valore non numerico per {args[i - 1]}");
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
                    case "--stall": a.Stall = NextInt(); if (a.Stall < 1) throw new ArgumentException("--stall deve essere almeno 1"); break;
                    case "--align-dates":
                        a.Align = Next().ToLowerInvariant() switch
                        {
                            "none" or "no" => DateAlignMode.None,
                            "size" => DateAlignMode.SizeOnly,
                            "quick" => DateAlignMode.Quick,
                            "full" or "md5" => DateAlignMode.Full,
                            var v => throw new ArgumentException($"livello '{v}' non valido per --align-dates (none|size|quick|full)"),
                        };
                        break;
                    case "--retries": a.Retries = NextInt(); break;
                    case "--wait-device": a.WaitDevice = NextInt(); break;
                    case "--verify": a.Verify = true; break;
                    case "--diagnostic": a.Diagnostic = true; break;
                    case "--yes": case "-y": a.Yes = true; break;
                    case "--list": a.List = true; break;
                    default: throw new ArgumentException($"opzione sconosciuta '{args[i]}'");
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
            };
            if (NoDefaultExcludes) o.ExcludePatterns.Clear();
            o.ExcludePatterns.Add("*" + LocalScanner.TempSuffix);
            o.ExcludePatterns.AddRange(Excludes);
            return o;
        }
    }
}
