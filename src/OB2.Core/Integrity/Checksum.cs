using System.Security.Cryptography;

namespace OB2.Core.Integrity;

public enum ChecksumAlgorithm
{
    Md5,
    Sha256,
}

/// <summary>An expected file hash supplied by the user (FR-11).</summary>
public sealed record Checksum(ChecksumAlgorithm Algorithm, string HexValue)
{
    /// <summary>Parses an algorithm name (<c>md5</c>, <c>sha256</c>, <c>sha-256</c>) and a hex digest.</summary>
    public static bool TryCreate(string? algorithm, string? hexValue, out Checksum? checksum)
    {
        checksum = null;
        if (string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(hexValue))
        {
            return false;
        }

        ChecksumAlgorithm algo;
        switch (algorithm.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
        {
            case "MD5":
                algo = ChecksumAlgorithm.Md5;
                break;
            case "SHA256":
                algo = ChecksumAlgorithm.Sha256;
                break;
            default:
                return false;
        }

        var hex = hexValue.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        var expectedLength = algo == ChecksumAlgorithm.Md5 ? 32 : 64;
        if (hex.Length != expectedLength || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        checksum = new Checksum(algo, hex.ToUpperInvariant());
        return true;
    }

    /// <summary>The name stored in <c>downloads.checksum_algo</c>.</summary>
    public string AlgorithmName => Algorithm == ChecksumAlgorithm.Md5 ? "md5" : "sha256";

    public static async Task<string> ComputeAsync(string path, ChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(algorithm == ChecksumAlgorithm.Md5 ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<bool> MatchesAsync(string path, CancellationToken cancellationToken = default) =>
        string.Equals(await ComputeAsync(path, Algorithm, cancellationToken).ConfigureAwait(false), HexValue, StringComparison.OrdinalIgnoreCase);
}
