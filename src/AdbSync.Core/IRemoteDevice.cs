namespace AdbSync.Core;

/// <summary>Astrazione del telefono: implementata via ADB e, nei test, da un dispositivo simulato.</summary>
public interface IRemoteDevice
{
    string Serial { get; }
    string DisplayName { get; }

    Task<bool> IsOnlineAsync(CancellationToken ct);

    /// <summary>Attende che il dispositivo torni disponibile; lancia <see cref="DeviceUnavailableException"/> allo scadere.</summary>
    Task WaitOnlineAsync(TimeSpan timeout, Action<string>? status, CancellationToken ct);

    /// <summary>
    /// Elenca file e cartelle. Non lancia eccezioni per scansioni incomplete: le segnala in
    /// <see cref="RemoteScanResult.Problems"/>, così il chiamante può salvare la diagnostica prima di fermarsi.
    /// </summary>
    Task<RemoteScanResult> ScanAsync(string remoteRoot, Action<int>? filesFound, CancellationToken ct);

    Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath, CancellationToken ct);

    Task<(long Size, long Mtime, int Fraction)?> StatAsync(string remotePath, CancellationToken ct);

    Task<string?> Md5Async(string remotePath, CancellationToken ct);

    /// <summary>
    /// Hash di più file con un solo comando (percorsi relativi a <paramref name="remoteRoot"/>).
    /// quick = solo primi e ultimi 64 KB. Valore null per i file non leggibili.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> HashBatchAsync(string remoteRoot, IReadOnlyList<string> relativePaths, bool quick, CancellationToken ct);

    /// <summary>Apre un canale di trasferimento (uno per ciascun trasferimento parallelo); useV2 = protocollo ADB v2.</summary>
    IRemoteTransfer OpenTransfer(bool useV2 = false);
}

public interface IRemoteTransfer : IDisposable
{
    /// <summary>Scarica il file nello stream; <paramref name="progress"/> riceve i byte ricevuti finora.</summary>
    Task PullAsync(string remotePath, Stream destination, Action<long> progress, CancellationToken ct);
}

public class DeviceUnavailableException(string message) : Exception(message);

public class DestinationUnavailableException(string message) : Exception(message);

public class IncompleteScanException(string message) : Exception(message);
