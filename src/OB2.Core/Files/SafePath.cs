namespace OB2.Core.Files;

/// <summary>Builds destination paths that are guaranteed to stay inside the chosen save folder (§11.1, AC-11).</summary>
public static class SafePath
{
    /// <summary>
    /// Sanitizes <paramref name="fileName"/>, joins it to <paramref name="saveFolder"/>, and
    /// confirms the resolved path is a direct child of that folder.
    /// </summary>
    /// <exception cref="UnsafePathException">The result would escape the folder.</exception>
    public static string Combine(string saveFolder, string? fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFolder);

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveFolder));
        var safeName = FileNameSanitizer.Sanitize(fileName);
        var full = Path.GetFullPath(Path.Combine(folder, safeName));

        EnsureInside(folder, full);
        return full;
    }

    /// <exception cref="UnsafePathException"><paramref name="path"/> is not directly inside <paramref name="folder"/>.</exception>
    public static void EnsureInside(string folder, string path)
    {
        var normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), normalizedFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException($"'{path}' is outside the save folder '{normalizedFolder}'.");
        }
    }
}

public sealed class UnsafePathException : Exception
{
    public UnsafePathException(string message)
        : base(message)
    {
    }

    public UnsafePathException()
    {
    }

    public UnsafePathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
