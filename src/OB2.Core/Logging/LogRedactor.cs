namespace OB2.Core.Logging;

/// <summary>
/// Strips secrets from values before they are logged (§11.2, AC-13): query strings,
/// fragments, and user info are removed from URLs. Cookie values are never passed to a
/// logger at all; this class exists so URL logging is safe by construction.
/// </summary>
public static class LogRedactor
{
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "[unparsable url]";
        }

        return Url(uri);
    }

    public static string Url(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            return "[relative url]";
        }

        var suffix = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?…";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath}{suffix}";
    }
}
