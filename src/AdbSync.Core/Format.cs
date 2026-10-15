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
    private const string ItDatePattern = "dd/MM/yyyy HH:mm:ss";

    /// <summary>Formato di numeri e date della lingua attiva (italiano se non impostato).</summary>
    public static CultureInfo Culture { get; private set; } = It;
    private static string _datePattern = ItDatePattern;

    /// <summary>Imposta formato numeri/date per la lingua scelta (null = italiano).</summary>
    public static void SetCulture(string? cultureName, string? datePattern)
    {
        CultureInfo c = It;
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try { c = CultureInfo.GetCultureInfo(cultureName); }
            catch (CultureNotFoundException) { c = CultureInfo.InvariantCulture; }
        }
        Culture = c;
        _datePattern = !string.IsNullOrWhiteSpace(datePattern) ? datePattern!
            : ReferenceEquals(c, It) ? ItDatePattern
            : c.DateTimeFormat.ShortDatePattern + " HH:mm:ss";
    }

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Culture) + " " + units[u];
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }

    public static string Number(long n) => n.ToString("N0", Culture);

    public static string UnixTime(long? unix) =>
        unix is null ? "" : DateTimeOffset.FromUnixTimeSeconds(unix.Value).ToLocalTime().ToString(_datePattern, Culture);
}
