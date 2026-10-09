using System.Text;

namespace OB2.Core.Files;

/// <summary>
/// Turns an untrusted name (from a URL, Content-Disposition, or the browser) into a safe
/// Windows file name (PRD §11.1, AC-11): no path separators or traversal, no reserved device
/// names, no invalid characters, at most 200 characters.
/// </summary>
public static class FileNameSanitizer
{
    public const int MaxLength = 200;
    public const string Fallback = "download";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly HashSet<char> InvalidChars =
    [
        .. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    ];

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        // Keep only the last path component, whichever separator the source used.
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || InvalidChars.Contains(c))
            {
                builder.Append('_');
            }
            else if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                // Drops bidi overrides such as U+202E that disguise "exe.pdf" as "fdp.exe".
                continue;
            }
            else
            {
                builder.Append(c);
            }
        }

        // Windows strips trailing dots and spaces, so "evil.exe." would become "evil.exe".
        var result = builder.ToString().Trim().TrimEnd('.', ' ').TrimStart('.');
        if (result.Length == 0 || result.All(c => c is '.' or '_' or ' '))
        {
            return Fallback;
        }

        var stem = result.Contains('.', StringComparison.Ordinal) ? result[..result.IndexOf('.', StringComparison.Ordinal)] : result;
        if (ReservedNames.Contains(stem.TrimEnd(' ')))
        {
            result = "_" + result;
        }

        return Truncate(result);
    }

    private static string Truncate(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length is 0 or > 20)
        {
            return name[..MaxLength].TrimEnd('.', ' ');
        }

        var stem = name[..^extension.Length];
        return stem[..(MaxLength - extension.Length)].TrimEnd('.', ' ') + extension;
    }
}
