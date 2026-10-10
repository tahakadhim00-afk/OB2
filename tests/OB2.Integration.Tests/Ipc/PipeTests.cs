using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using OB2.Shared;
using OB2.Shared.Ipc;
using OB2.Shared.Protocol;

namespace OB2.Integration.Tests.Ipc;

/// <summary>§14.1 IPC row: host ↔ app pipe messages, malformed frames, oversize messages, app not running.</summary>
public sealed class PipeTests : IAsyncLifetime
{
    private readonly string _pipeName = "OB2-test-" + Guid.NewGuid().ToString("N")[..16];
    private readonly RecordingHandler _handler = new();
    private readonly List<Exception> _errors = [];
    private PipeServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new PipeServer(_pipeName, _handler, _errors.Add);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Request_reaches_the_handler_and_the_reply_comes_back()
    {
        var request = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "https://example.com/a.zip", Cookies = "s=1" });

        var reply = await Send(request);

        reply.Type.Should().Be(MessageTypes.Ack);
        reply.Id.Should().Be(request.Id);
        reply.PayloadAs<AckPayload>()!.DownloadId.Should().Be(42);
        _handler.Received.Should().ContainSingle().Which.PayloadAs<AddDownloadPayload>()!.Cookies.Should().Be("s=1");
    }

    [Fact]
    public async Task Concurrent_clients_are_all_served()
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Send(ProtocolMessage.Simple(MessageTypes.GetStatus))));

        replies.Should().OnlyContain(r => r.Type == MessageTypes.Ack);
        _handler.Received.Should().HaveCount(20);
    }

    [Fact]
    public async Task Invalid_messages_are_answered_by_the_server_without_reaching_the_handler()
    {
        var reply = await Send(ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "file:///C:/secret.txt" }));

        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.UnsupportedScheme);
        _handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_frame_gets_INVALID_MESSAGE()
    {
        var reply = await SendRaw(Frame(Encoding.UTF8.GetBytes("{ this is not json")));

        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
        _handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Oversize_frame_gets_INVALID_MESSAGE()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolInfo.MaxMessageBytes + 1);

        var reply = await SendRaw(header);

        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
    }

    [Fact]
    public async Task A_failing_handler_returns_INTERNAL_ERROR_and_the_server_keeps_running()
    {
        _handler.ThrowNext = true;
        var failed = await Send(ProtocolMessage.Simple(MessageTypes.GetStatus));
        var next = await Send(ProtocolMessage.Simple(MessageTypes.GetStatus));

        failed.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InternalError);
        failed.PayloadAs<ErrorPayload>()!.Message.Should().NotContain("boom", "exception details stay in the app log");
        _errors.Should().ContainSingle();
        next.Type.Should().Be(MessageTypes.Ack);
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_frame_does_not_stop_the_server()
    {
        await using (var pipe = await Connect())
        {
            await pipe.WriteAsync(new byte[] { 50, 0, 0, 0, (byte)'{' });
        }

        (await Send(ProtocolMessage.Simple(MessageTypes.GetStatus))).Type.Should().Be(MessageTypes.Ack);
    }

    [Fact]
    public async Task App_not_running_is_a_timeout_not_a_hang()
    {
        var started = DateTime.UtcNow;
        var act = () => PipeClient.SendAsync("OB2-nobody-" + Guid.NewGuid().ToString("N"), ProtocolMessage.Simple(MessageTypes.Ping), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<TimeoutException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "§8.3: the host must answer within 2 seconds");
    }

    [Fact]
    public async Task IsListening_reports_the_server_state()
    {
        (await PipeClient.IsListeningAsync(_pipeName, TimeSpan.FromSeconds(1))).Should().BeTrue();
        (await PipeClient.IsListeningAsync("OB2-nobody-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200))).Should().BeFalse();
    }

    [Fact]
    public async Task A_slow_handler_times_out_on_the_client()
    {
        _handler.Delay = TimeSpan.FromSeconds(3);
        var act = () => PipeClient.SendAsync(_pipeName, ProtocolMessage.Simple(MessageTypes.GetStatus), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Disposed_server_stops_listening()
    {
        await _server.DisposeAsync();

        (await PipeClient.IsListeningAsync(_pipeName, TimeSpan.FromMilliseconds(300))).Should().BeFalse();
        _server = new PipeServer(_pipeName, _handler); // DisposeAsync runs again at the end of the test
    }

    private Task<ProtocolMessage> Send(ProtocolMessage request) =>
        PipeClient.SendAsync(_pipeName, request, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    private async Task<NamedPipeClientStream> Connect()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000);
        return pipe;
    }

    private async Task<ProtocolMessage> SendRaw(byte[] bytes)
    {
        await using var pipe = await Connect();
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (await MessageFraming.ReadAsync(pipe, cts.Token))!;
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private sealed class RecordingHandler : IIpcHandler
    {
        private readonly List<ProtocolMessage> _received = [];

        public bool ThrowNext { get; set; }

        public TimeSpan Delay { get; set; }

        public IReadOnlyList<ProtocolMessage> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        public async Task<ProtocolMessage> HandleAsync(ProtocolMessage request, CancellationToken cancellationToken)
        {
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new InvalidOperationException("boom");
            }

            await Task.Delay(Delay, cancellationToken);
            lock (_received)
            {
                _received.Add(request);
            }

            return ProtocolMessage.Create(MessageTypes.Ack, new AckPayload(42, "queued"), request.Id);
        }
    }
}
