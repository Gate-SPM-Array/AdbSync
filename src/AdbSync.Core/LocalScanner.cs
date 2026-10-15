using System.IO.Enumeration;

namespace AdbSync.Core;

public sealed class LocalScanResult
{
    /// <summary>Chiave: percorso relativo con '/', confronto senza distinzione maiuscole/minuscole (come NTFS).</summary>
    public required Dictionary<string, LocalFile> Files { get; init; }
    public Dictionary<string, LocalDir> Directories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cartelle che sono collegamenti (junction/symlink): non vengono attraversate né usate come destinazione.</summary>
    public HashSet<string> ReparseDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public required List<string> StaleTempFiles { get; init; }
}

public static class LocalScanner
{
    public const string TempSuffix = ".adbsync-part";

    private sealed record Entry(bool IsDir, bool IsReparse, string Rel, string Full, long Size, long Mtime, long Ctime);

    public static LocalScanResult Scan(string localRoot, Action<int>? filesFound, CancellationToken ct)
    {
        var files = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, LocalDir>(StringComparer.OrdinalIgnoreCase);
        var reparse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temps = new List<string>();
        if (!Directory.Exists(localRoot))
            return new LocalScanResult { Files = files, Directories = dirs, ReparseDirectories = reparse, StaleTempFiles = temps };

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        try
        {
            var di = new DirectoryInfo(root);
            dirs[""] = new LocalDir("", new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeSeconds(), new DateTimeOffset(di.CreationTimeUtc).ToUnixTimeSeconds());
        }
        catch { /* radice di un'unità o non leggibile: nessuna data */ }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0, // includi file nascosti/di sistema e segnaposto cloud (OneDrive); i link a cartelle non si attraversano (sotto)
            ReturnSpecialDirectories = false,
        };

        var enumerable = new FileSystemEnumerable<Entry>(root, (ref FileSystemEntry e) =>
        {
            string full = e.ToFullPath();
            string rel = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            return new Entry(e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, rel, full, e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.ToUnixTimeSeconds(), e.CreationTimeUtc.ToUnixTimeSeconds());
        }, options)
        {
            // Junction/symlink a cartelle: non si entra (si uscirebbe dalla destinazione scelta).
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        int n = 0;
        foreach (var f in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            if (f.IsDir)
            {
                if (f.IsReparse) reparse.Add(f.Rel);
                else dirs[f.Rel] = new LocalDir(f.Rel, f.Mtime, f.Ctime);
                continue;
            }
            if (f.Rel.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                temps.Add(f.Full);
                continue;
            }
            files[f.Rel] = new LocalFile(f.Rel, f.Size, f.Mtime, f.Ctime);
            if (++n % 500 == 0) filesFound?.Invoke(n);
        }
        filesFound?.Invoke(n);
        return new LocalScanResult { Files = files, Directories = dirs, ReparseDirectories = reparse, StaleTempFiles = temps };
    }
}
