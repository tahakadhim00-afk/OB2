namespace OB2.Core.Organization;

/// <summary>What sort of files a category holds, for its icon (D-86).</summary>
public enum CategoryKind
{
    /// <summary>No extensions, a mix with no clear majority, or types OB2 has no icon for.</summary>
    Other,
    Documents,
    Music,
    Video,
    Programs,
    Compressed,
    Pictures,
}

/// <summary>
/// D-86: a category stores only a name and extensions, and the user may rename, add or delete
/// categories, so its kind is read from its extensions rather than its name: the kind most of
/// them belong to. "Music" renamed "Songs" keeps its icon, and a "Pictures" category the user
/// adds with jpg and png gets a picture.
/// </summary>
public static class CategoryKinds
{
    private static readonly Dictionary<string, CategoryKind> ByExtension = Build(
        (CategoryKind.Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub md mobi azw3 djvu xps"),
        (CategoryKind.Music, "mp3 flac wav m4a aac ogg oga wma opus mid midi aif aiff ape alac"),
        (CategoryKind.Video, "mp4 mkv avi mov wmv webm flv m4v mpg mpeg ts 3gp m2ts vob ogv"),
        (CategoryKind.Programs, "exe msi msix msixbundle appx appxbundle apk xapk bat cmd ps1 dmg deb rpm appimage pkg jar"),
        (CategoryKind.Compressed, "zip rar 7z tar gz tgz bz2 xz zst iso img cab lz lzma tbz2 txz"),
        (CategoryKind.Pictures, "jpg jpeg png gif webp bmp tif tiff svg heic heif avif ico raw psd"));

    public static CategoryKind Of(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var counts = extensions
            .Select(e => ByExtension.GetValueOrDefault(LastPart(e)))
            .Where(kind => kind != CategoryKind.Other)
            .GroupBy(kind => kind)
            .Select(g => (Kind: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();

        // A tie means the category is as much one thing as another: no icon would be right.
        return counts.Count == 0 || (counts.Count > 1 && counts[0].Count == counts[1].Count)
            ? CategoryKind.Other
            : counts[0].Kind;
    }

    /// <summary>"tar.gz" counts as "gz"; a leading dot or upper case changes nothing.</summary>
    private static string LastPart(string extension)
    {
        var trimmed = extension.Trim().TrimStart('.').ToLowerInvariant();
        var dot = trimmed.LastIndexOf('.');
        return dot < 0 ? trimmed : trimmed[(dot + 1)..];
    }

    private static Dictionary<string, CategoryKind> Build(params (CategoryKind Kind, string Extensions)[] lists)
    {
        var map = new Dictionary<string, CategoryKind>(StringComparer.Ordinal);
        foreach (var (kind, extensions) in lists)
        {
            foreach (var extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map.Add(extension, kind); // throws on an extension listed under two kinds
            }
        }

        return map;
    }
}
