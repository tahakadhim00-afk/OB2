using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Integrity;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Infrastructure;

/// <summary>A migrated database, a save folder, and an engine tuned for fast tests.</summary>
public sealed class EngineFixture : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();

    public EngineFixture(EngineOptions? options = null)
    {
        Options = options ?? FastOptions;
        SaveFolder = Directory.CreateDirectory(_temp.Combine("downloads")).FullName;
        var factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(factory).Run();
        Repository = new SqliteDownloadRepository(factory);
        Engine = CreateEngine();
    }

    public static EngineOptions FastOptions { get; } = new()
    {
        Connections = 1,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadIdleTimeout = TimeSpan.FromSeconds(3),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
        CheckpointBytes = 256 * 1024,
        BufferSize = 64 * 1024,
        RetryBaseDelay = TimeSpan.FromMilliseconds(20),
        RetryMaxDelay = TimeSpan.FromMilliseconds(200),
        MinSegmentBytes = 64 * 1024,
    };

    public EngineOptions Options { get; set; }

    public string SaveFolder { get; }

    public string Root => _temp.Path;

    public SqliteDownloadRepository Repository { get; }

    public DownloadEngine Engine { get; private set; }

    /// <summary>Simulates closing and reopening the app: a new engine over the same database.</summary>
    public DownloadEngine Restart()
    {
        Engine.Dispose();
        Engine = CreateEngine();
        Engine.RecoverInterrupted();
        return Engine;
    }

    public async Task<DownloadItem> DownloadAsync(Uri url, Checksum? checksum = null, string? fileName = null, int? connections = null, string? cookies = null,
        string? referrer = null, string? userAgent = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var item = await AddAsync(url, checksum, fileName, connections, cookies, referrer, userAgent, headers);
        return await RunAsync(item.Id);
    }

    public Task<DownloadItem> AddAsync(Uri url, Checksum? checksum = null, string? fileName = null, int? connections = null, string? cookies = null,
        string? referrer = null, string? userAgent = null, IReadOnlyDictionary<string, string>? headers = null) =>
        Engine.AddAsync(new DownloadRequest
        {
            Url = url,
            SaveFolder = SaveFolder,
            Checksum = checksum,
            FileName = fileName,
            Connections = connections,
            Cookies = cookies,
            Referrer = referrer,
            UserAgent = userAgent,
            Headers = headers ?? new Dictionary<string, string>(),
        });

    public async Task<DownloadItem> RunAsync(long id)
    {
        Engine.Start(id).Should().BeTrue();
        var result = await Engine.WaitAsync(id).WaitAsync(TimeSpan.FromMinutes(5));
        return result!;
    }

    /// <summary>Waits until at least <paramref name="bytes"/> are checkpointed in the database.</summary>
    public async Task WaitForCheckpointAsync(long id, long bytes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Repository.Get(id)?.DownloadedBytes >= bytes)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Download {id} never checkpointed {bytes} bytes.");
    }

    public static string Sha256OfFile(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    public async ValueTask DisposeAsync()
    {
        Engine.Dispose();
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private DownloadEngine CreateEngine() => new(Repository, () => Options);
}
