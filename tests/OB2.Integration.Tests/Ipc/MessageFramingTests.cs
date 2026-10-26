using System.Buffers.Binary;
using System.Text;
using OB2.Shared;
using OB2.Shared.Protocol;

namespace OB2.Integration.Tests.Ipc;

/// <summary>§8.3 framing and protocol rules (§14.1 IPC row: malformed frames, oversize messages).</summary>
public sealed class MessageFramingTests
{
    [Fact]
    public async Task Round_trip_keeps_envelope_and_payload()
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload
        {
            Url = "https://example.com/a.zip",
            Cookies = "session=abc",
            FileSizeHint = 524288000,
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "en-US" },
            Source = "chrome-downloads",
        });

        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var read = await MessageFraming.ReadAsync(stream, CancellationToken.None);

        read!.V.Should().Be(ProtocolInfo.Version);
        read.Id.Should().Be(message.Id);
        read.Type.Should().Be(MessageTypes.AddDownload);
        var payload = read.PayloadAs<AddDownloadPayload>()!;
        payload.Url.Should().Be("https://example.com/a.zip");
        payload.Cookies.Should().Be("session=abc");
        payload.FileSizeHint.Should().Be(524288000);
        payload.Headers.Should().ContainKey("Accept-Language");
    }

    [Fact]
    public async Task Wire_format_is_little_endian_length_then_camel_case_json()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, ProtocolMessage.Simple(MessageTypes.Ping, "1"), CancellationToken.None);
        var bytes = stream.ToArray();

        BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be((uint)(bytes.Length - 4));
        Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4).Should().Be("""{"v":1,"id":"1","type":"ping"}""");
    }

    [Fact]
    public async Task Parses_the_PRD_example_message()
    {
        var json = """
            { "v": 1, "id": "9f6c1e2a-4b1d-4a8e-9a7b-2f3c4d5e6f70", "type": "add_download",
              "payload": { "url": "https://example.com/files/setup.exe", "finalUrl": "https://cdn.example.com/abc/setup.exe",
                "referrer": "https://example.com/downloads", "filenameHint": "setup.exe", "mimeType": "application/octet-stream",
                "fileSizeHint": 524288000, "userAgent": "Mozilla/5.0 ...", "cookies": "session=abc123; other=xyz",
                "headers": { "Accept-Language": "en-US" }, "source": "chrome-downloads", "autoStart": false } }
            """;
        var message = await ReadFrame(Frame(Encoding.UTF8.GetBytes(json)));

        message!.PayloadAs<AddDownloadPayload>()!.FinalUrl.Should().Be("https://cdn.example.com/abc/setup.exe");
        ProtocolValidator.Validate(message).Should().BeNull();
    }

    [Fact]
    public async Task Empty_stream_means_the_peer_closed()
    {
        (await ReadFrame([])).Should().BeNull();
    }

    [Fact]
    public async Task Truncated_length_prefix_is_rejected()
    {
        var act = () => ReadFrame([5, 0]);
        await act.Should().ThrowAsync<ProtocolException>().WithMessage("*length prefix*");
    }

    [Fact]
    public async Task Truncated_body_is_rejected()
    {
        var frame = Frame(Encoding.UTF8.GetBytes("""{"v":1,"id":"x","type":"ping"}"""));
        var act = () => ReadFrame(frame[..^5]);
        await act.Should().ThrowAsync<ProtocolException>().WithMessage("*cut off*");
    }

    [Fact]
    public async Task Oversize_frame_is_rejected_before_the_body_is_read()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolInfo.MaxMessageBytes + 1);
        var act = () => ReadFrame(header); // no body: the size check must come first
        await act.Should().ThrowAsync<ProtocolException>().WithMessage("*limit*");
    }

    [Fact]
    public async Task A_huge_length_does_not_allocate()
    {
        var act = () => ReadFrame([0xFF, 0xFF, 0xFF, 0xFF]);
        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Writing_above_1_MB_is_refused()
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "https://e.com/" + new string('a', ProtocolInfo.MaxMessageBytes) });
        using var stream = new MemoryStream();
        var act = () => MessageFraming.WriteAsync(stream, message, CancellationToken.None);
        await act.Should().ThrowAsync<ProtocolException>();
        stream.Length.Should().Be(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"v":1,"id":"x"}""")]
    [InlineData("""{"v":1,"id":"x","type":""}""")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"v":"one","type":"ping"}""")]
    public async Task Malformed_json_is_rejected(string json)
    {
        var act = () => ReadFrame(Frame(Encoding.UTF8.GetBytes(json)));
        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public void Other_protocol_versions_are_refused()
    {
        var reply = ProtocolValidator.Validate(new ProtocolMessage { V = 2, Id = "a", Type = MessageTypes.Ping });

        reply!.Type.Should().Be(MessageTypes.Error);
        reply.Id.Should().Be("a");
        reply.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.ProtocolVersionMismatch);
    }

    [Theory]
    [InlineData("ftp://example.com/a.zip", ProtocolErrors.UnsupportedScheme)]
    [InlineData("file:///C:/Windows/win.ini", ProtocolErrors.UnsupportedScheme)]
    [InlineData("javascript:alert(1)", ProtocolErrors.UnsupportedScheme)]
    [InlineData("not a url", ProtocolErrors.InvalidUrl)]
    [InlineData("", ProtocolErrors.InvalidUrl)]
    public void Only_http_and_https_downloads_are_accepted(string url, string code)
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = url });

        ProtocolValidator.Validate(message)!.PayloadAs<ErrorPayload>()!.Code.Should().Be(code);
    }

    [Fact]
    public void The_final_url_is_the_one_checked()
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "https://ok.example/a", FinalUrl = "ftp://evil.example/a" });

        ProtocolValidator.Validate(message)!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.UnsupportedScheme);
    }

    [Fact]
    public void Overlong_urls_are_refused()
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "https://e.com/" + new string('a', ProtocolValidator.MaxUrlLength) });

        ProtocolValidator.Validate(message)!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidUrl);
    }

    [Fact]
    public void Add_download_without_payload_is_refused()
    {
        ProtocolValidator.Validate(ProtocolMessage.Simple(MessageTypes.AddDownload))!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidUrl);
    }

    [Fact]
    public void Asking_for_a_videos_qualities_needs_a_web_address()
    {
        static ProtocolMessage Ask(string? url) => ProtocolMessage.Create(MessageTypes.GetMediaFormats, new MediaFormatsRequest { Url = url });

        ProtocolValidator.Validate(Ask("https://www.youtube.com/watch?v=dQw4w9WgXcQ")).Should().BeNull();
        ProtocolValidator.Validate(Ask("file:///C:/Windows/win.ini"))!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.UnsupportedScheme);
        ProtocolValidator.Validate(Ask(null))!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidUrl);
    }

    [Fact]
    public void The_streams_a_player_loaded_are_a_few_web_addresses()
    {
        // D-79: each one becomes a request and maybe a yt-dlp run, so they are checked like the page.
        static ProtocolMessage Ask(MediaStreamPayload[] streams, string? title = null) => ProtocolMessage.Create(
            MessageTypes.GetMediaFormats,
            new MediaFormatsRequest { Url = "https://movies.example/watch/1", Streams = streams, Title = title });
        static MediaStreamPayload S(string url, Dictionary<string, string>? headers = null, string? referrer = null) => new() { Url = url, Headers = headers, Referrer = referrer };
        static string? Code(ProtocolMessage m) => ProtocolValidator.Validate(m)?.PayloadAs<ErrorPayload>()!.Code;

        Code(Ask([S("https://cdn.example/master.m3u8"), S("https://cdn.example/manifest.mpd")], "Lupin - Movies")).Should().BeNull();
        Code(Ask([.. Enumerable.Range(0, ProtocolValidator.MaxStreams + 1).Select(i => S($"https://cdn.example/{i}.m3u8"))])).Should().Be(ProtocolErrors.InvalidMessage);
        Code(Ask([S("file:///C:/Windows/win.ini")])).Should().Be(ProtocolErrors.InvalidMessage);
        Code(Ask([null!])).Should().Be(ProtocolErrors.InvalidMessage);
        Code(Ask([S("https://cdn.example/master.m3u8")], new string('a', ProtocolValidator.MaxTitleLength + 1))).Should().Be(ProtocolErrors.InvalidMessage);

        // D-80: the browser's headers go into requests and onto yt-dlp's command line.
        var token = new Dictionary<string, string> { ["X-MB-Token"] = "Edge-Cache-Cookie=urlprefix=aHR0cHM6Ly9z:Expires=1793012617", ["Origin"] = "https://movie-box.co" };
        Code(Ask([S("https://cdn.example/index_web.mpd", token, "https://movie-box.co/")])).Should().BeNull();
        Code(Ask([S("https://cdn.example/a.mpd", new() { ["X-Evil"] = "a\r\nHost: elsewhere" })])).Should().Be(ProtocolErrors.InvalidMessage);
        Code(Ask([S("https://cdn.example/a.mpd", new() { ["Bad Name"] = "x" })])).Should().Be(ProtocolErrors.InvalidMessage);
        Code(Ask([S("https://cdn.example/a.mpd", referrer: "not an address")])).Should().Be(ProtocolErrors.InvalidMessage);
    }

    [Fact]
    public void A_download_carries_plain_headers_only()
    {
        static ProtocolMessage Add(Dictionary<string, string> headers, string? cookies = null) =>
            ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload { Url = "https://cdn.example/a.mpd", Headers = headers, Cookies = cookies, Source = "chrome-media" });

        ProtocolValidator.Validate(Add(new() { ["Accept-Language"] = "en-US,en;q=0.9", ["Origin"] = "https://movie-box.co" })).Should().BeNull();
        ProtocolValidator.Validate(Add(new() { ["X-A"] = "1\n2" }))!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
        ProtocolValidator.Validate(Add([], "a=1\r\nX: y"))!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
    }

    [Theory]
    [InlineData("137+140", "mp4", true, true)]
    [InlineData("hls-720", "mp4", true, true)]
    [InlineData("140", "m4a", true, true)]
    [InlineData("137+140", "mp4", false, false)] // a format only goes with a video page
    [InlineData("bestvideo[height<=720]+bestaudio", "mp4", true, false)] // an expression, not ids
    [InlineData("137/18", "mp4", true, false)]
    [InlineData("-137", "mp4", true, false)]
    [InlineData("137\n", "mp4", true, false)]
    [InlineData("137+140", "../x", true, false)]
    [InlineData("137+140", null, true, false)]
    [InlineData("subs:en", "srt", true, true)] // D-74: subtitles
    [InlineData("auto-subs:en-orig", "ass", true, true)]
    [InlineData("subs:pt-BR", "vtt", true, true)]
    [InlineData("subs:en", "srt", false, false)]
    [InlineData("subs:en", "mp4", true, false)]
    [InlineData("subs:en.*", "srt", true, false)] // --sub-langs reads patterns
    [InlineData("subs:-en", "srt", true, false)]
    [InlineData("subs:en,fr", "srt", true, false)]
    [InlineData("subs:", "srt", true, false)]
    public void A_quality_from_the_button_is_plain_format_ids(string format, string? container, bool mediaPage, bool accepted)
    {
        var message = ProtocolMessage.Create(MessageTypes.AddDownload, new AddDownloadPayload
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            MediaPage = mediaPage ? true : null,
            MediaFormat = format,
            MediaContainer = container,
        });

        var reply = ProtocolValidator.Validate(message);

        if (accepted)
        {
            reply.Should().BeNull();
        }
        else
        {
            reply!.PayloadAs<ErrorPayload>()!.Code.Should().Be(ProtocolErrors.InvalidMessage);
        }
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<ProtocolMessage?> ReadFrame(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await MessageFraming.ReadAsync(stream, CancellationToken.None);
    }
}
