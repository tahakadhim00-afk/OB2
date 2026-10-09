using System.Text;
using OB2.Core.IO;
using OB2.Tests.Common;

namespace OB2.Core.Tests.IO;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WriteAllText_creates_file_with_content()
    {
        var path = _temp.Combine("settings.json");

        await AtomicFile.WriteAllTextAsync(path, "{ \"a\": 1 }");

        (await File.ReadAllTextAsync(path)).Should().Be("{ \"a\": 1 }");
    }

    [Fact]
    public async Task WriteAllText_writes_utf8_without_bom()
    {
        var path = _temp.Combine("utf8.txt");

        await AtomicFile.WriteAllTextAsync(path, "تجربة");

        var bytes = await File.ReadAllBytesAsync(path);
        bytes.Should().Equal(new UTF8Encoding(false).GetBytes("تجربة"));
    }

    [Fact]
    public async Task WriteAllText_replaces_existing_file()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, "old content that is longer than the new content");

        await AtomicFile.WriteAllTextAsync(path, "new");

        (await File.ReadAllTextAsync(path)).Should().Be("new");
    }

    [Fact]
    public async Task Write_creates_missing_parent_directory()
    {
        var path = _temp.Combine("a", "b", "file.bin");

        await AtomicFile.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        (await File.ReadAllBytesAsync(path)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Successful_write_leaves_no_temp_files()
    {
        var path = _temp.Combine("settings.json");

        for (var i = 0; i < 5; i++)
        {
            await AtomicFile.WriteAllTextAsync(path, $"v{i}");
        }

        Directory.GetFiles(_temp.Path).Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public async Task Failed_write_keeps_original_content_and_removes_temp_file()
    {
        var path = _temp.Combine("checkpoint.json");
        await File.WriteAllTextAsync(path, "original");

        var act = () => AtomicFile.WriteAsync(path, async (stream, ct) =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"), ct);
            throw new InvalidOperationException("simulated crash mid-write");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllTextAsync(path)).Should().Be("original");
        Directory.GetFiles(_temp.Path).Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public async Task Failed_first_write_does_not_create_target()
    {
        var path = _temp.Combine("new.json");

        var act = () => AtomicFile.WriteAsync(path, (_, _) => throw new IOException("disk full"));

        await act.Should().ThrowAsync<IOException>();
        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(_temp.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_write_keeps_original_content()
    {
        var path = _temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, "original");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => AtomicFile.WriteAllTextAsync(path, "new", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllTextAsync(path)).Should().Be("original");
    }

    [Fact]
    public async Task Concurrent_writers_never_produce_a_mixed_file()
    {
        var path = _temp.Combine("race.txt");
        var payloads = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 10_000)).ToArray();

        await Task.WhenAll(payloads.Select(p => Task.Run(async () =>
        {
            // Losing writers may hit a sharing violation on rename; the file must still be whole.
            try
            {
                await AtomicFile.WriteAllTextAsync(path, p);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        })));

        var content = await File.ReadAllTextAsync(path);
        payloads.Should().Contain(content);
    }
}
