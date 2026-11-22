using OB2.Core.Organization;

namespace OB2.App.ViewModels;

/// <summary>
/// D-86: the icon beside each entry of the left panel, a glyph of the icon font the toolbar uses
/// (Segoe Fluent Icons, or Segoe MDL2 Assets on Windows 10). Drawn in the entry's own text colour,
/// so selection, the dark theme and high contrast apply to it as to the name.
/// </summary>
public static class NavIcons
{
    public const string All = "";          // Download
    public const string Unfinished = "";   // Stopwatch
    public const string Finished = "";     // Completed
    public const string Queues = "";       // BulletedList
    public const string Queue = "";        // MultiSelect (a checklist)
    public const string Categories = "";   // Tag

    public static string ForCategory(CategoryKind kind) => kind switch
    {
        CategoryKind.Documents => "",      // Document
        CategoryKind.Music => "",          // Audio
        CategoryKind.Video => "",          // Video
        CategoryKind.Programs => "",       // OEM (apps)
        CategoryKind.Compressed => "",     // ZipFolder
        CategoryKind.Pictures => "",       // Photo
        _ => "",                           // Folder: each category saves to its own folder
    };
}
