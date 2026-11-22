using System.Globalization;

namespace OB2.Core.Updates;

/// <summary>D-85: a release on GitHub that is newer than the running copy.</summary>
/// <param name="Notes">The release description, shown as "What's new".</param>
/// <param name="Page">The release's page on GitHub.</param>
public sealed record UpdateRelease(
    Version Version,
    string Title,
    string Notes,
    Uri Page,
    Uri? Zip,
    Uri? Checksums,
    Uri? Signature,
    long? ZipSize)
{
    /// <summary>
    /// The zip, its checksum file and the signature are all there, so OB2 can install it itself.
    /// Otherwise only its page is offered, to download by hand.
    /// </summary>
    public bool CanInstall => Zip is not null && Checksums is not null && Signature is not null;
}

/// <summary>The <c>major.minor.patch</c> versions OB2's releases use, from a tag (<c>v1.2.0</c>) or the app.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Parses <c>1.2.0</c> or <c>v1.2.0</c> (also <c>1.2</c>, read as <c>1.2.0</c>). Anything else,
    /// including a pre-release suffix such as <c>-beta</c>, is not a release version.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        if (value.Split('.').Length is < 2 or > 3 || !value.All(c => char.IsAsciiDigit(c) || c == '.')
            || !Version.TryParse(value, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>The app's own version from its informational version (<c>1.1.0+5783804…</c>).</summary>
    public static Version OfApp(string informationalVersion) =>
        TryParse(informationalVersion?.Split('+')[0], out var version) ? version : new Version(0, 0, 0);

    public static string Text(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}");
    }
}

/// <summary>A failure the user should read: every message here is shown as it is.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
