namespace OB2.Core.Files;

/// <summary>Pre-allocation for <c>.pgpart</c> files (§10.3).</summary>
public static class PartFile
{
    /// <summary>
    /// Sets the file to its final length. <c>SetFileValidData</c> needs an admin-only
    /// privilege the portable app never has, so <see cref="FileStream.SetLength"/> is used
    /// (decision D-19).
    /// </summary>
    public static void Preallocate(FileStream file, long length)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length != length)
        {
            file.SetLength(length);
        }
    }
}
