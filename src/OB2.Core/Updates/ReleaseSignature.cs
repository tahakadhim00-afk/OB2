using System.Security.Cryptography;

namespace OB2.Core.Updates;

/// <summary>
/// D-84: a release's <c>SHA256SUMS.txt</c> is signed with the developer's private key, and OB2
/// installs an update only if that signature checks out against a key built into it
/// (<see cref="ReleaseKey"/>). The checksum file then vouches for the zip. ECDSA P-256 over the
/// file's exact bytes with SHA-256; the signature file holds the 64-byte IEEE P1363 form in
/// Base64 on one line. Without it, anyone who could change the GitHub release could ship an
/// update, since a checksum file sitting next to the zip only catches a damaged download.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>The signature file's name: the checksum file's plus <c>.sig</c>.</summary>
    public const string FileSuffix = ".sig";

    /// <summary>Signs <paramref name="data"/>; the result is what goes in the <c>.sig</c> file.</summary>
    public static string Sign(ReadOnlySpan<byte> data, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return Convert.ToBase64String(privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>
    /// True if <paramref name="signature"/> (the <c>.sig</c> file's text) is a signature of
    /// <paramref name="data"/> by the private key of any of <paramref name="trustedKeys"/>
    /// (SubjectPublicKeyInfo, Base64). Anything malformed is simply not valid.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signature, IEnumerable<string> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String((signature ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != 64)
        {
            return false;
        }

        foreach (var key in trustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                continue;
            }

            if (ecdsa.KeySize == 256 && ecdsa.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The public half of <paramref name="key"/>, in the form <see cref="ReleaseKey"/> lists.</summary>
    public static string PublicKeyOf(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
