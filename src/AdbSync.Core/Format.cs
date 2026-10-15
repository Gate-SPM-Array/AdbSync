using System.Globalization;

namespace AdbSync.Core;

/// <summary>IProgress che esegue subito il callback (Progress&lt;T&gt; lo posticipa e può alterare l'ordine dei messaggi).</summary>
public sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

public static class Format
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", It) + " " + units[u];
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }

    public static string Number(long n) => n.ToString("N0", It);

    public static string UnixTime(long? unix) =>
        unix is null ? "" : DateTimeOffset.FromUnixTimeSeconds(unix.Value).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", It);
}
