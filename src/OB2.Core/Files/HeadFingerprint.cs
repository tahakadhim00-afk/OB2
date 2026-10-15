using System.Security.Cryptography;

namespace OB2.Core.Files;

/// <summary>
/// A hash of the first bytes of a file, used to tell whether the remote entity changed between a
/// pause and a resume when the server offers no ETag and no Last-Modified (§10.3). Without it a
/// file replaced by one of the same length resumes onto different bytes and the result is
/// silently corrupt, which the user may not discover for a long time.
/// </summary>
public static class HeadFingerprint
{
    /// <summary>
    /// How many leading bytes the fingerprint covers. Large enough to be past any header a
    /// container format shares between versions, small enough that re-reading it on resume costs
    /// one request rather than a measurable part of the download.
    /// </summary>
    public const int Length = 64 * 1024;

    /// <summary>Hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Reads up to <see cref="Length"/> bytes from the start of the part file and hashes them.
    /// Returns null when fewer bytes than requested are present, because a short read would hash
    /// a different span than the one the server would be asked for and the comparison would
    /// always disagree.
    /// </summary>
    public static async Task<(string Hash, long Bytes)?> OfFileAsync(string path, int length = Length, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (length <= 0)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[length];
            var read = await stream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return read < length ? null : (Of(buffer.AsSpan(0, read)), read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A fingerprint is an extra safety net; failing to take one must not fail the download.
            return null;
        }
    }

    /// <summary>
    /// Whether a stored fingerprint and a freshly read one disagree. Either being absent means
    /// "cannot tell", which is treated as unchanged so a resume is never refused on no evidence.
    /// </summary>
    public static bool Differs(string? stored, long? storedBytes, string? fresh, long? freshBytes) =>
        stored is not null && fresh is not null && storedBytes == freshBytes
        && !string.Equals(stored, fresh, StringComparison.OrdinalIgnoreCase);
}
