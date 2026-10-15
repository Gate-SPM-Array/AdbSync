using System.Collections.Concurrent;

namespace AdbSync.Core;

public sealed class ActiveTransfer
{
    public required int Slot { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required int Attempt { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    private long _bytes;
    public long Bytes { get => Interlocked.Read(ref _bytes); set => Interlocked.Exchange(ref _bytes, value); }
    public double Percent => Size <= 0 ? 100 : Math.Min(100, Bytes * 100.0 / Size);
    public double BytesPerSecond
    {
        get
        {
            double s = (DateTime.UtcNow - StartedAt).TotalSeconds;
            return s > 0.2 ? Bytes / s : 0;
        }
    }
}

/// <summary>Stato del lavoro in corso, aggiornato dai thread di copia e letto periodicamente dall'interfaccia.</summary>
public sealed class SyncProgress
{
    private long _committedBytes;
    private long _failedBytes;
    private int _done, _failed, _retrying;
    private volatile string _phase = "Pronto";
    private volatile string? _status;

    public ConcurrentDictionary<int, ActiveTransfer> Active { get; } = new();
    public int TotalFiles { get; internal set; }
    public long TotalBytes { get; internal set; }
    public DateTime StartedAt { get; internal set; } = DateTime.UtcNow;

    /// <summary>Impostato a fine lavoro: il tempo trascorso si ferma.</summary>
    public DateTime? FinishedAt { get; internal set; }
    public TimeSpan Elapsed => (FinishedAt ?? DateTime.UtcNow) - StartedAt;

    public string Phase { get => _phase; internal set => _phase = value; }

    /// <summary>Messaggio di stato temporaneo (es. "In attesa del telefono...").</summary>
    public string? StatusMessage { get => _status; internal set => _status = value; }

    public int DoneFiles => Volatile.Read(ref _done);
    public int FailedFiles => Volatile.Read(ref _failed);
    public int RetryingFiles => Volatile.Read(ref _retrying);

    public long CommittedBytes => Interlocked.Read(ref _committedBytes);
    public long InFlightBytes => Active.Values.Sum(a => a.Bytes);
    public long BytesProcessed => CommittedBytes + InFlightBytes;

    internal void FileDone(long bytes) { Interlocked.Add(ref _committedBytes, bytes); Interlocked.Increment(ref _done); }
    internal void FileFailed(long bytes) { Interlocked.Add(ref _failedBytes, bytes); Interlocked.Increment(ref _failed); }
    internal void RetryBegin() => Interlocked.Increment(ref _retrying);
    internal void RetryEnd() => Interlocked.Decrement(ref _retrying);

    private int _requeued, _stalls;
    private long _stallTicks;
    /// <summary>File andati in stallo e in attesa in fondo alla coda.</summary>
    public int RequeuedFiles => Volatile.Read(ref _requeued);
    public int Stalls => Volatile.Read(ref _stalls);
    public TimeSpan StallTimeLost => TimeSpan.FromTicks(Interlocked.Read(ref _stallTicks));
    internal void RequeueBegin() => Interlocked.Increment(ref _requeued);
    internal void RequeueEnd() => Interlocked.Decrement(ref _requeued);
    internal void AddStall(TimeSpan lost) { Interlocked.Increment(ref _stalls); Interlocked.Add(ref _stallTicks, lost.Ticks); }

    // --- velocità media mobile (ultimi ~8 s) ---
    private readonly Queue<(DateTime T, long B)> _samples = new();
    private readonly object _speedLock = new();

    /// <summary>Da chiamare periodicamente (es. ogni 250-1000 ms) per calcolare velocità ed ETA.</summary>
    public (double BytesPerSecond, TimeSpan? Eta) Sample()
    {
        var now = DateTime.UtcNow;
        long b = BytesProcessed;
        lock (_speedLock)
        {
            _samples.Enqueue((now, b));
            while (_samples.Count > 2 && (now - _samples.Peek().T).TotalSeconds > 8) _samples.Dequeue();
            var first = _samples.Peek();
            double secs = (now - first.T).TotalSeconds;
            double bps = secs > 0.5 ? Math.Max(0, (b - first.B) / secs) : 0;
            long remaining = TotalBytes - b - Interlocked.Read(ref _failedBytes);
            TimeSpan? eta = bps > 1024 && remaining > 0 ? TimeSpan.FromSeconds(remaining / bps) : null;
            return (bps, eta);
        }
    }
}
