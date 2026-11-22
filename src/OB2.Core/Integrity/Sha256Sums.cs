using System.Text.RegularExpressions;

namespace OB2.Core.Integrity;

/// <summary>Reads the checksum files releases publish next to their downloads (D-71, D-84).</summary>
public static partial class Sha256Sums
{
    /// <summary>
    /// The SHA-256 for <paramref name="fileName"/> in a checksum file, lowercase: the hash on the
    /// line naming the file (<c>sha256sum</c> style, with or without <c>*</c>), or the only hash in
    /// the file (one file per checksum). Null when the file is not listed.
    /// </summary>
    public static string? Find(string checksums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (var line in checksums.Split('\n'))
        {
            var match = Sha256().Match(line);
            if (match.Success && Regex.IsMatch(line, @"(^|[\s*/])" + Regex.Escape(fileName) + @"\s*$", RegexOptions.IgnoreCase))
            {
                return match.Value.ToLowerInvariant();
            }
        }

        var all = Sha256().Matches(checksums);
        return all.Count == 1 ? all[0].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();
}
