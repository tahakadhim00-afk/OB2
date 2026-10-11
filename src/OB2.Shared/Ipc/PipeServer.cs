using System.IO.Pipes;
using OB2.Shared.Protocol;

namespace OB2.Shared.Ipc;

/// <summary>Handles one request from the host (or a second <c>ob2.exe</c>) and returns the reply.</summary>
public interface IIpcHandler
{
    Task<ProtocolMessage> HandleAsync(ProtocolMessage request, CancellationToken cancellationToken);
}

/// <summary>
/// The desktop app's end of the bridge (§7.1): a named pipe restricted to the current user
/// (§11.1), one framed request and one framed reply per connection.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IIpcHandler _handler;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public PipeServer(string pipeName, IIpcHandler handler, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _onError = onError;
    }

    public void Start() => _loop ??= Task.Run(() => AcceptLoopAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Serve each client on its own task so a slow handler never blocks new connections.
            _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ProtocolMessage reply;
            try
            {
                var request = await MessageFraming.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (request.Type == MessageTypes.GetMediaFormats)
                {
                    timeout.CancelAfter(MessageTypes.ReplyTime(request.Type)); // D-72: yt-dlp needs longer
                }

                reply =ProtocolValidator.Validate(request) ?? await _handler.HandleAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                reply = ProtocolMessage.ErrorReply(string.Empty, ProtocolErrors.InvalidMessage, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _onError?.Invoke(ex);
                reply = ProtocolMessage.ErrorReply(string.Empty, ProtocolErrors.InternalError, "The app could not handle the request.");
            }

            try
            {
                await MessageFraming.WriteAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to report to.
            }
        }
    }
}

/// <summary>The host's (or a second <c>ob2.exe</c>'s) end of the pipe.</summary>
public static class PipeClient
{
    /// <exception cref="TimeoutException">No server answered within <paramref name="connectTimeout"/> (app not running).</exception>
    /// <exception cref="ReplyTimeoutException">The app took the request but did not reply within <paramref name="replyTimeout"/>.</exception>
    public static async Task<ProtocolMessage> SendAsync(string pipeName, ProtocolMessage request, TimeSpan connectTimeout, TimeSpan replyTimeout, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(replyTimeout);
        try
        {
            await MessageFraming.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            return await MessageFraming.ReadAsync(pipe, timeout.Token).ConfigureAwait(false)
                ?? throw new ProtocolException("The app closed the connection without replying.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReplyTimeoutException($"The app did not reply within {replyTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>True if something is listening on the pipe right now.</summary>
    public static async Task<bool> IsListeningAsync(string pipeName, TimeSpan timeout)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The app is running and took the request, but its reply did not come in time.</summary>
public sealed class ReplyTimeoutException : TimeoutException
{
    public ReplyTimeoutException()
    {
    }

    public ReplyTimeoutException(string message)
        : base(message)
    {
    }

    public ReplyTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
