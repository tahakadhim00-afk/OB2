using System.Globalization;
using System.Windows;
using OB2.Core.Downloads;

namespace OB2.App.Infrastructure;

/// <summary>Looks up strings from Resources/Strings.xaml (NFR Localization).</summary>
internal static class Loc
{
    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Status(DownloadStatus status) => Get("Status_" + status);
}

/// <summary>Human-readable sizes, speeds, and durations.</summary>
internal static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long? bytes)
    {
        if (bytes is not { } b)
        {
            return Loc.Get("Common_Unknown");
        }

        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{b:N0} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {Units[unit]}");
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond < 1 ? string.Empty : Size((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t)
        {
            return string.Empty;
        }

        if (t.TotalDays >= 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)t.TotalDays}d {t.Hours}h");
        }

        return t.TotalHours >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)t.TotalHours}h {t.Minutes:00}m")
            : string.Create(CultureInfo.CurrentCulture, $"{t.Minutes}m {t.Seconds:00}s");
    }
}
