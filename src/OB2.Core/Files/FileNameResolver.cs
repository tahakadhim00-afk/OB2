using System.Net.Http.Headers;

namespace OB2.Core.Files;

/// <summary>
/// Picks the file name for a download (FR-7). Order: Content-Disposition <c>filename*</c>,
/// Content-Disposition <c>filename</c>, a caller hint, the last path segment of the final
/// URL, then of the original URL. The result is always sanitized.
/// </summary>
public static class FileNameResolver
{
    public static string Resolve(ContentDispositionHeaderValue? contentDisposition, string? hint, Uri? finalUrl, Uri? originalUrl)
    {
        var fromHeader = FromContentDisposition(contentDisposition);
        if (fromHeader is not null)
        {
            return FileNameSanitizer.Sanitize(fromHeader);
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            return FileNameSanitizer.Sanitize(hint);
        }

        return FileNameSanitizer.Sanitize(FromUrl(finalUrl) ?? FromUrl(originalUrl));
    }

    public static string? FromContentDisposition(ContentDispositionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }

        // FileNameStar is RFC 5987-decoded by the framework; FileName may still be quoted.
        var name = header.FileNameStar;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = header.FileName?.Trim('"');
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static string? FromUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return null;
        }

        var segment = url.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return segment;
        }
    }
}
