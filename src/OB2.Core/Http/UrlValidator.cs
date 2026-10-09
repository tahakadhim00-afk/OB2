namespace OB2.Core.Http;

/// <summary>Input validation for download URLs (§8.3 protocol rules, §11.1).</summary>
public static class UrlValidator
{
    public const int MaxLength = 2048;

    public static UrlCheck Check(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return UrlCheck.Invalid;
        }

        text = text.Trim();
        if (text.Length > MaxLength)
        {
            return UrlCheck.TooLong;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return UrlCheck.Invalid;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return UrlCheck.UnsupportedScheme;
        }

        url = parsed;
        return string.IsNullOrEmpty(parsed.UserInfo) ? UrlCheck.Ok : UrlCheck.HasCredentials;
    }

    /// <summary>Throws unless the URL is an absolute http(s) URL within the length limit.</summary>
    public static void EnsureDownloadable(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var check = Check(url.OriginalString, out _);
        if (check is not (UrlCheck.Ok or UrlCheck.HasCredentials))
        {
            throw new ArgumentException($"Not a downloadable URL ({check}).", nameof(url));
        }
    }
}

public enum UrlCheck
{
    Ok,

    /// <summary>Valid, but contains <c>user:password@</c>; the user must confirm (§11.1).</summary>
    HasCredentials,
    Invalid,
    UnsupportedScheme,
    TooLong,
}
