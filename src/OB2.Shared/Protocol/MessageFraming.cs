using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OB2.Shared.Protocol;

/// <summary>
/// Chrome Native Messaging framing: a 32-bit little-endian length, then that many bytes of
/// UTF-8 JSON. The host-to-app pipe uses the same framing. Frames above 1 MB are rejected (§8.3).
/// </summary>
public static class MessageFraming
{
    /// <returns>The message, or null when the stream ended cleanly before a frame started.</returns>
    /// <exception cref="ProtocolException">Oversize frame, truncated frame, or invalid JSON.</exception>
    public static async Task<ProtocolMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken, int maxBytes = ProtocolInfo.MaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < 4)
        {
            throw new ProtocolException("The message length prefix was cut off.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > maxBytes)
        {
            throw new ProtocolException($"The message is {length:N0} bytes; the limit is {maxBytes:N0}.");
        }

        var body = new byte[length];
        if (await ReadFullyAsync(stream, body, cancellationToken).ConfigureAwait(false) < length)
        {
            throw new ProtocolException("The message body was cut off.");
        }

        try
        {
            var message = JsonSerializer.Deserialize(body, ProtocolJsonContext.Default.ProtocolMessage);
            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                throw new ProtocolException("The message has no type.");
            }

            return message;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"The message is not valid JSON: {ex.Message}", ex);
        }
    }

    public static async Task WriteAsync(Stream stream, ProtocolMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ProtocolMessage);
        if (body.Length > ProtocolInfo.MaxMessageBytes)
        {
            throw new ProtocolException("Refusing to send a message above 1 MB.");
        }

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProtocolException()
    {
    }
}

/// <summary>Checks done by both the host and the app before acting on a message (§8.3 protocol rules).</summary>
public static partial class ProtocolValidator
{
    public const int MaxUrlLength = 2048;

    /// <returns>An error reply, or null when the message may be processed.</returns>
    public static ProtocolMessage? Validate(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.V != ProtocolInfo.Version)
        {
            return ProtocolMessage.ErrorReply(message.Id, ProtocolErrors.ProtocolVersionMismatch, $"Protocol version {message.V} is not supported; expected {ProtocolInfo.Version}.");
        }

        if (message.Type == MessageTypes.GetMediaFormats)
        {
            return ValidateUrl(message, message.PayloadAs<MediaFormatsRequest>()?.Url);
        }

        if (message.Type != MessageTypes.AddDownload)
        {
            return null;
        }

        var payload = message.PayloadAs<AddDownloadPayload>();
        if (payload?.MediaFormat is { } format && (payload.MediaPage != true || !IsMediaChoice(format, payload.MediaContainer)))
        {
            return ProtocolMessage.ErrorReply(message.Id, ProtocolErrors.InvalidMessage, "The video format is not one OB2 offers.");
        }

        return ValidateUrl(message, payload?.FinalUrl is { Length: > 0 } ? payload.FinalUrl : payload?.Url);
    }

    private static ProtocolMessage? ValidateUrl(ProtocolMessage message, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return ProtocolMessage.ErrorReply(message.Id, ProtocolErrors.InvalidUrl, "The download address is missing, too long, or not a valid URL.");
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return ProtocolMessage.ErrorReply(message.Id, ProtocolErrors.UnsupportedScheme, "Only http and https downloads can be captured.");
        }

        return null;
    }

    /// <summary>
    /// D-72: a format selector and container a <c>media_formats</c> reply may offer. The selector
    /// becomes yt-dlp's <c>-f</c> value, so it is one or two plain format ids, never an expression.
    /// D-74: or a subtitle track (<c>subs:en</c>, <c>auto-subs:en-orig</c>) saved as srt, ass or vtt;
    /// the language becomes <c>--sub-langs</c>, which reads patterns, so it is letters, digits, <c>_</c> and <c>-</c>.
    /// </summary>
    public static bool IsMediaChoice(string? format, string? container) =>
        format is not null && container is not null
        && (SubtitleSpec().IsMatch(format) ? SubtitleContainer().IsMatch(container) : FormatSpec().IsMatch(format) && Container().IsMatch(container));

    /// <summary>One or two yt-dlp format ids (<c>137</c>, <c>137+140</c>, <c>hls-720</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.=@-]{0,79}(?:\+[A-Za-z0-9_][A-Za-z0-9_.=@-]{0,79})?\z")]
    private static partial Regex FormatSpec();

    [GeneratedRegex(@"^[a-z0-9]{1,5}\z")]
    private static partial Regex Container();

    /// <summary>Mirrors <c>SubtitleTrack.Spec</c> in OB2.Core.</summary>
    [GeneratedRegex(@"^(?:auto-)?subs:[A-Za-z0-9][A-Za-z0-9_-]{0,34}\z")]
    private static partial Regex SubtitleSpec();

    [GeneratedRegex(@"^(?:srt|ass|vtt)\z")]
    private static partial Regex SubtitleContainer();
}
