using System.Text;
using AdvancedSharpAdbClient.Exceptions;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace AdbSync.Core.Adb;

/// <summary>Implementazione di <see cref="IRemoteDevice"/> sul protocollo ADB (shell + sync).</summary>
public sealed class AdbRemoteDevice : IRemoteDevice
{
    private readonly AdbEnvironment _env;
    private DeviceData _device;
    private readonly object _lock = new();

    public string Serial { get; }
    public string DisplayName { get; }

    internal AdbRemoteDevice(AdbEnvironment env, string serial, string displayName)
    {
        _env = env;
        Serial = serial;
        DisplayName = displayName;
        _device = new DeviceData { Serial = serial, State = DeviceState.Online };
    }

    private DeviceData Device { get { lock (_lock) return _device; } }

    public async Task<bool> IsOnlineAsync(CancellationToken ct)
    {
        try
        {
            var d = await _env.FindDeviceDataAsync(Serial, ct);
            if (d is { } dd)
            {
                lock (_lock) _device = dd;
                return dd.State == DeviceState.Online;
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Server adb non raggiungibile: proviamo a riavviarlo.
            try { await _env.EnsureServerAsync(ct); } catch (OperationCanceledException) { throw; } catch { }
            return false;
        }
    }

    public async Task WaitOnlineAsync(TimeSpan timeout, Action<string>? status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool first = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsOnlineAsync(ct))
            {
                // Breve verifica che la shell risponda davvero (subito dopo la riconnessione può non essere pronta).
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probe.CancelAfter(TimeSpan.FromSeconds(10));
                    var sb = new StringBuilder();
                    await _env.Client.ExecuteRemoteCommandAsync("printf ok", Device, l => { sb.Append(l); return true; }, Encoding.UTF8, probe.Token);
                    if (sb.ToString().Contains("ok")) return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            if (first)
            {
                status?.Invoke(L.F("In attesa del telefono {0}... (ricollega il cavo / sblocca il telefono se richiesto)", DisplayName));
                first = false;
            }
            if (DateTime.UtcNow > deadline)
                throw new DeviceUnavailableException(L.F("Il telefono non è più raggiungibile da {0}.", Format.Duration(timeout)));
            await Task.Delay(2000, ct);
        }
    }

    private async Task<string> ShellAsync(string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        var sb = new StringBuilder();
        await _env.Client.ExecuteRemoteCommandAsync(command, Device, l => { sb.AppendLine(l); return true; }, Encoding.UTF8, cts.Token);
        if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            throw new TimeoutException(L.F("Il comando sul telefono non ha risposto: {0}", command));
        ct.ThrowIfCancellationRequested();
        return sb.ToString();
    }

    /// <summary>Se il telefono non invia nulla per questo tempo la scansione è considerata bloccata.</summary>
    public static TimeSpan ScanInactivityTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public async Task<RemoteScanResult> ScanAsync(string remoteRoot, Action<int>? filesFound, CancellationToken ct)
    {
        var parser = new RemoteScanParser();
        long last = 0;
        long lastLineTick = Environment.TickCount64;
        bool stalled = false;
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            while (!scanCts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, scanCts.Token); } catch (OperationCanceledException) { return; }
                if (Environment.TickCount64 - Volatile.Read(ref lastLineTick) > ScanInactivityTimeout.TotalMilliseconds)
                {
                    stalled = true;
                    scanCts.Cancel();
                    return;
                }
            }
        });
        try
        {
            await _env.Client.ExecuteRemoteCommandAsync(RemoteShell.BuildScanCommand(remoteRoot), Device, line =>
            {
                Volatile.Write(ref lastLineTick, Environment.TickCount64);
                parser.Feed(line);
                if (parser.EntriesReceived - last >= 250)
                {
                    last = parser.EntriesReceived;
                    filesFound?.Invoke((int)last);
                }
                return true;
            }, Encoding.UTF8, scanCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Connessione persa a metà: il parser segnalerà la scansione come incompleta (manca il marcatore finale).
            parser.Feed(RemoteShell.ErrMarker + L.F("connessione con il telefono interrotta durante la scansione: {0}", ex.Message));
        }
        finally
        {
            scanCts.Cancel();
            try { await watchdog; } catch { }
        }
        ct.ThrowIfCancellationRequested();
        if (stalled)
            parser.Feed(RemoteShell.ErrMarker + L.F("il telefono non ha inviato dati per {0:0} s: scansione bloccata", ScanInactivityTimeout.TotalSeconds));
        filesFound?.Invoke((int)parser.EntriesReceived);
        return parser.Complete();
    }

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath, CancellationToken ct)
    {
        string output = await ShellAsync(RemoteShell.BuildListDirsCommand(remotePath), ct, TimeSpan.FromSeconds(30));
        return output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("./", StringComparison.Ordinal))
            .Select(l => l[2..])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<(long Size, long Mtime, int Fraction)?> StatAsync(string remotePath, CancellationToken ct)
    {
        string output = await ShellAsync(RemoteShell.BuildStatCommand(remotePath), ct, TimeSpan.FromSeconds(30));
        return RemoteShell.TryParseStat(output, out var s, out var m, out var f) ? (s, m, f) : null;
    }

    public async Task<string?> Md5Async(string remotePath, CancellationToken ct)
    {
        // md5 di file grandi richiede tempo anche sul telefono: timeout generoso.
        string output = await ShellAsync(RemoteShell.BuildMd5Command(remotePath), ct, TimeSpan.FromMinutes(10));
        return RemoteShell.ParseMd5(output);
    }

    public async Task<IReadOnlyDictionary<string, string?>> HashBatchAsync(string remoteRoot, IReadOnlyList<string> relativePaths, bool quick, CancellationToken ct)
    {
        var lines = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(quick ? 5 : 60)); // l'MD5 completo di file grandi richiede tempo anche sul telefono
        try
        {
            await _env.Client.ExecuteRemoteCommandAsync(RemoteShell.BuildHashBatchCommand(remoteRoot, relativePaths, quick), Device,
                l => { lines.Add(l); return true; }, Encoding.UTF8, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("il calcolo degli hash sul telefono non ha risposto in tempo"));
        }
        ct.ThrowIfCancellationRequested();
        if (cts.IsCancellationRequested) throw new TimeoutException(L.T("il calcolo degli hash sul telefono non ha risposto in tempo"));
        var result = RemoteShell.ParseHashBatch(lines, out bool complete);
        if (!complete) throw new IOException(L.T("risposta incompleta dal telefono durante il calcolo degli hash"));
        return result;
    }

    /// <summary>Il telefono ha rifiutato il protocollo v2: da qui in poi si usa la v1.</summary>
    private volatile bool _v2Unsupported;

    public IRemoteTransfer OpenTransfer(bool useV2 = false) => new AdbTransfer(_env.Client, Device, useV2, this);

    private sealed class AdbTransfer(IAdbClient client, DeviceData device, bool useV2, AdbRemoteDevice owner) : IRemoteTransfer
    {
        private SyncService? _service;
        private volatile bool _disposed;

        public async Task PullAsync(string remotePath, Stream destination, Action<long> progress, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _service ??= new SyncService(client, device);
            bool v2 = useV2 && !owner._v2Unsupported;
            long received = 0;
            try
            {
                await _service.PullAsync(remotePath, destination, e => { received = (long)e.ReceivedBytesSize; progress(received); }, useV2: v2, ct);
            }
            catch (AdbException) when (v2 && received == 0 && !ct.IsCancellationRequested)
            {
                // v2 non supportata dal telefono (stat_v2/sendrecv_v2): si riprova subito con la v1, una volta per tutte.
                owner._v2Unsupported = true;
                try { _service.Dispose(); } catch { }
                _service = new SyncService(client, device);
                if (destination.CanSeek) { destination.SetLength(0); destination.Position = 0; }
                await _service.PullAsync(remotePath, destination, e => progress((long)e.ReceivedBytesSize), useV2: false, ct);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _service?.Dispose(); } catch { }
            _service = null;
        }
    }
}
