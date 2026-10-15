using System.Collections.Concurrent;
using System.Security.Cryptography;
using AdbSync.Core;

namespace AdbSync.Tests;

/// <summary>Telefono simulato in memoria, con guasti programmabili per file.</summary>
public sealed class FakeRemoteDevice : IRemoteDevice
{
    public string Root { get; }
    public ConcurrentDictionary<string, (byte[] Data, long Mtime)> Files { get; } = new();

    /// <summary>Comportamenti per percorso relativo: coda di guasti da applicare ai tentativi successivi.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<Fault>> Faults { get; } = new();

    public volatile bool Online = true;
    /// <summary>Se impostato, il dispositivo torna online dopo questo intervallo da quando va offline.</summary>
    public TimeSpan? ComesBackAfter;
    public int PullCalls;
    public int TransfersOpened;
    public int ChunkSize = 4096;
    public TimeSpan ChunkDelay = TimeSpan.Zero;

    public enum Fault { Stall, HangIgnoringCancel, TransientError, NoSuchFile, Truncate, Corrupt, GoOffline }

    public FakeRemoteDevice(string root = "/sdcard/DCIM") => Root = root;

    public string Serial => "FAKE123";
    public string DisplayName => "Telefono finto";

    public void Add(string rel, int size, long mtime, int seed = 0)
    {
        var data = new byte[size];
        new Random(seed == 0 ? rel.GetHashCode() : seed).NextBytes(data);
        Files[rel] = (data, mtime);
    }

    public void AddFault(string rel, params Fault[] faults)
    {
        var q = Faults.GetOrAdd(rel, _ => new ConcurrentQueue<Fault>());
        foreach (var f in faults) q.Enqueue(f);
    }

    private string Rel(string remotePath) => remotePath[(Root.TrimEnd('/').Length + 1)..];

    public Task<bool> IsOnlineAsync(CancellationToken ct) => Task.FromResult(Online);

    public int WaitOnlineCalls;
    public Exception? HashBatchError;

    public async Task WaitOnlineAsync(TimeSpan timeout, Action<string>? status, CancellationToken ct)
    {
        Interlocked.Increment(ref WaitOnlineCalls);
        var deadline = DateTime.UtcNow + timeout;
        status?.Invoke("in attesa");
        while (!Online)
        {
            if (DateTime.UtcNow > deadline) throw new DeviceUnavailableException("telefono sparito");
            await Task.Delay(20, ct);
        }
    }

    private void GoOffline()
    {
        Online = false;
        if (ComesBackAfter is { } after)
            _ = Task.Delay(after).ContinueWith(_ => Online = true);
    }

    /// <summary>Cartelle vuote aggiuntive (oltre a quelle che contengono file).</summary>
    public ConcurrentDictionary<string, long> EmptyDirs { get; } = new();
    public long DirMtime = 1_650_000_000;
    public int FileFraction = 1234567; // frazione di secondo (100 ns) restituita dalla scansione
    /// <summary>Problemi da simulare nella scansione (es. "Argument list too long").</summary>
    public List<string> ScanProblems { get; } = new();

    public Task<RemoteScanResult> ScanAsync(string remoteRoot, Action<int>? filesFound, CancellationToken ct)
    {
        var list = Files.Select(kv => new RemoteFile(kv.Key, kv.Value.Data.Length, kv.Value.Mtime, FileFraction)).ToList();
        var dirs = new Dictionary<string, RemoteDir> { [""] = new RemoteDir("", DirMtime) };
        foreach (var f in list)
        {
            var parts = f.RelativePath.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                string d = string.Join('/', parts.Take(i));
                dirs[d] = new RemoteDir(d, DirMtime + i);
            }
        }
        foreach (var (d, t) in EmptyDirs) dirs[d] = new RemoteDir(d, t);
        filesFound?.Invoke(list.Count);
        return Task.FromResult(new RemoteScanResult
        {
            Files = list,
            Directories = dirs.Values.ToList(),
            Problems = ScanProblems.ToList(),
            DeclaredEntries = list.Count + dirs.Count,
            ReceivedEntries = list.Count + dirs.Count,
        });
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<(long Size, long Mtime, int Fraction)?> StatAsync(string remotePath, CancellationToken ct)
    {
        if (!Files.TryGetValue(Rel(remotePath), out var f)) return Task.FromResult<(long, long, int)?>(null);
        return Task.FromResult<(long, long, int)?>((f.Data.Length, f.Mtime, FileFraction));
    }

    public Task<string?> Md5Async(string remotePath, CancellationToken ct)
    {
        if (!Files.TryGetValue(Rel(remotePath), out var f)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(Convert.ToHexStringLower(MD5.HashData(f.Data)));
    }

    public int HashBatchCalls;

    public Task<IReadOnlyDictionary<string, string?>> HashBatchAsync(string remoteRoot, IReadOnlyList<string> relativePaths, bool quick, CancellationToken ct)
    {
        Interlocked.Increment(ref HashBatchCalls);
        if (HashBatchError is { } err) return Task.FromException<IReadOnlyDictionary<string, string?>>(err);
        var d = new Dictionary<string, string?>();
        foreach (var rel in relativePaths)
        {
            if (!Files.TryGetValue(rel, out var f)) { d[rel] = null; continue; }
            var data = f.Data;
            if (quick)
            {
                int w = Math.Min(SyncEngine.QuickHashWindow, data.Length);
                data = data.AsSpan(0, w).ToArray().Concat(data.AsSpan(data.Length - w, w).ToArray()).ToArray();
            }
            d[rel] = Convert.ToHexStringLower(MD5.HashData(data));
        }
        return Task.FromResult<IReadOnlyDictionary<string, string?>>(d);
    }

    public IRemoteTransfer OpenTransfer()
    {
        Interlocked.Increment(ref TransfersOpened);
        return new Transfer(this);
    }

    private sealed class Transfer(FakeRemoteDevice dev) : IRemoteTransfer
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PullAsync(string remotePath, Stream destination, Action<long> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref dev.PullCalls);
            if (!dev.Online) throw new IOException("device offline");
            string rel = dev.Rel(remotePath);
            Fault? fault = dev.Faults.TryGetValue(rel, out var q) && q.TryDequeue(out var f) ? f : null;

            switch (fault)
            {
                case Fault.NoSuchFile:
                    throw new Exception($"Failed to pull '{remotePath}'. No such file or directory");
                case Fault.TransientError:
                    throw new IOException("connessione reimpostata");
                case Fault.GoOffline:
                    dev.GoOffline();
                    throw new IOException("device offline");
            }

            if (!dev.Files.TryGetValue(rel, out var file)) throw new Exception($"Failed to pull '{remotePath}'. No such file or directory");
            var data = file.Data;
            int limit = fault == Fault.Truncate ? data.Length / 2 : data.Length;
            long sent = 0;
            for (int off = 0; off < limit; off += dev.ChunkSize)
            {
                int n = Math.Min(dev.ChunkSize, limit - off);
                var chunk = data.AsMemory(off, n).ToArray();
                if (fault == Fault.Corrupt && off == 0) chunk[0] ^= 0xFF;
                await destination.WriteAsync(chunk, ct);
                sent += n;
                progress(sent);
                if (dev.ChunkDelay > TimeSpan.Zero) await Task.Delay(dev.ChunkDelay, ct);

                if (fault == Fault.Stall && off == 0)
                    await Task.Delay(Timeout.Infinite, ct); // si blocca finché non viene annullato
                if (fault == Fault.HangIgnoringCancel && off == 0)
                {
                    await _disposed.Task; // ignora l'annullamento: si sblocca solo chiudendo il canale
                    throw new ObjectDisposedException("socket");
                }
            }
        }

        public void Dispose() => _disposed.TrySetResult();
    }
}
