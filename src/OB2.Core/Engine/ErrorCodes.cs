using System.Net.Sockets;
using OB2.Core.Files;
using OB2.Core.Http;

namespace OB2.Core.Engine;

/// <summary>Values stored in <c>downloads.error_code</c>.</summary>
public static class ErrorCodes
{
    public const string Network = "NETWORK_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";
    public const string SizeMismatch = "SIZE_MISMATCH";
    public const string DiskFull = "DISK_FULL";
    public const string FileError = "FILE_ERROR";
    public const string UnsafePath = "UNSAFE_PATH";
    public const string RangeNotSupported = "RANGE_NOT_SUPPORTED";
    public const string Internal = "INTERNAL_ERROR";

    /// <summary>yt-dlp, ffmpeg or Deno is missing for a video page (D-71).</summary>
    public const string ToolsMissing = "TOOLS_MISSING";

    /// <summary>yt-dlp reported the site's refusal or a change it can't handle (D-71).</summary>
    public const string SiteError = "SITE_ERROR";

    public static string Http(int status) => $"HTTP_{status}";

    public static string From(Exception ex) => ex switch
    {
        HttpStatusException h => Http((int)h.StatusCode),
        TimeoutException => Timeout,
        UnsafePathException => UnsafePath,
        RangeNotSupportedException => RangeNotSupported,
        DownloadFailedException d => d.Code,
        HttpRequestException or SocketException => Network,
        IOException io when IsDiskFull(io) => DiskFull,
        IOException when ex.InnerException is SocketException or HttpRequestException => Network,
        IOException => FileError,
        UnauthorizedAccessException => FileError,
        _ => Internal,
    };

    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}

/// <summary>A failure with a known error code and a user-readable message.</summary>
public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DownloadFailedException()
    {
        Code = ErrorCodes.Internal;
    }

    public DownloadFailedException(string message)
        : base(message)
    {
        Code = ErrorCodes.Internal;
    }

    public DownloadFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.Internal;
    }

    public string Code { get; }
}

/// <summary>The server ignored or mangled a Range request.</summary>
public sealed class RangeNotSupportedException : Exception
{
    public RangeNotSupportedException(string message)
        : base(message)
    {
    }

    public RangeNotSupportedException()
    {
    }

    public RangeNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
