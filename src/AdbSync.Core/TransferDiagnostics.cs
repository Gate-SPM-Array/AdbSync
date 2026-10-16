using System.Diagnostics;

namespace AdbSync.Core;

/// <summary>
/// Stream di destinazione che misura le scritture su disco: quanto è durata l'ultima e se ce n'è una in corso.
/// Serve al diario delle pause per distinguere "il telefono/adb non manda dati" da "il PC sta scrivendo".
/// </summary>
internal sealed class TimingStream(Stream inner) : Stream
{
    private long _writeStartedTicks;   // 0 = nessuna scrittura in corso
    private long _lastWriteTicks;
    private long _lastWriteEnd = Environment.TickCount64;

    /// <summary>Environment.TickCount64 alla fine dell'ultima scrittura (o alla creazione).</summary>
    public long LastWriteEndTick => Interlocked.Read(ref _lastWriteEnd);

    /// <summary>Durata dell'ultima scrittura completata (ms).</summary>
    public double LastWriteMs => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastWriteTicks)).TotalMilliseconds;

    /// <summary>Da quanti ms è in corso una scrittura (0 se nessuna).</summary>
    public double WriteInProgressMs
    {
        get
        {
            long s = Interlocked.Read(ref _writeStartedTicks);
            return s == 0 ? 0 : Stopwatch.GetElapsedTime(s).TotalMilliseconds;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _writeStartedTicks, start);
        try { await inner.WriteAsync(buffer, cancellationToken); }
        finally
        {
            Interlocked.Exchange(ref _lastWriteTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Exchange(ref _lastWriteEnd, Environment.TickCount64);
            Interlocked.Exchange(ref _writeStartedTicks, 0);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        long start = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _writeStartedTicks, start);
        try { inner.Write(buffer, offset, count); }
        finally
        {
            Interlocked.Exchange(ref _lastWriteTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Exchange(ref _lastWriteEnd, Environment.TickCount64);
            Interlocked.Exchange(ref _writeStartedTicks, 0);
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
}

/// <summary>Istantanea dello stato del processo: pause del garbage collector e thread pool (cause "nostre" di una pausa).</summary>
internal readonly record struct ProcessSnapshot(TimeSpan GcPause, int Gen2, int Threads, long Pending)
{
    public static ProcessSnapshot Take() =>
        new(GC.GetTotalPauseDuration(), GC.CollectionCount(2), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);

    public string DescribeSince(ProcessSnapshot before) =>
        L.F("pause GC +{0} ms, GC completi +{1}, thread pool {2} thread / {3} in coda",
            (int)(GcPause - before.GcPause).TotalMilliseconds, Gen2 - before.Gen2, Threads, Pending);
}

/// <summary>
/// Ripresa senza riscrivere: il telefono rimanda il file dall'inizio (il protocollo ADB non permette di partire a metà),
/// ma i primi <c>skip</c> byte sono già sul disco e vengono scartati invece di essere riscritti.
/// </summary>
internal sealed class SkipStream(Stream inner, long skip) : Stream
{
    private long _seen;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_seen + buffer.Length <= skip) { _seen += buffer.Length; return; }
        int offset = _seen >= skip ? 0 : (int)(skip - _seen);
        _seen += buffer.Length;
        await inner.WriteAsync(buffer[offset..], cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _seen; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
