namespace OB2.Core.Organization;

/// <summary>A download category (FR-18) with its file extensions and optional save folder (FR-29).</summary>
public sealed class Category
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Null means "<c>default save folder</c>\<c>Name</c>" (decision D-33).</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Lower-case extensions without dots.</summary>
    public List<string> Extensions { get; set; } = [];

    public string ResolveSaveFolder(string defaultSaveFolder) =>
        string.IsNullOrWhiteSpace(SaveFolder) ? Path.Combine(defaultSaveFolder, Name) : SaveFolder;

    public static List<string> ParseExtensions(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.TrimStart('.').ToLowerInvariant())
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.Ordinal)];
}

/// <summary>Picks the category for a file name by its extension (FR-18).</summary>
public static class CategoryResolver
{
    /// <returns>The first category listing the extension, or null ("General").</returns>
    public static Category? Match(IEnumerable<Category> categories, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0)
        {
            return null;
        }

        // ".tar.gz" should match "tar.gz" if a category lists it, before falling back to "gz".
        var name = (fileName ?? string.Empty).ToLowerInvariant();
        var list = categories.ToList();
        return list.FirstOrDefault(c => c.Extensions.Any(e => e.Contains('.', StringComparison.Ordinal) && name.EndsWith("." + e, StringComparison.Ordinal)))
            ?? list.FirstOrDefault(c => c.Extensions.Contains(extension));
    }
}

public interface ICategoryRepository
{
    IReadOnlyList<Category> List();

    long Insert(Category category);

    void Update(Category category);

    /// <summary>Downloads in the category keep their files; their category becomes null.</summary>
    void Delete(long id);
}
