using System.Text;
using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Storage.Tests.Repositories;

/// <summary>§11.1 "remember auth" storage (decision D-67).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiCookieStoreTests : IDisposable
{
    private const string Cookies = "session=S3CR3T-value; theme=dark";

    private readonly TempDirectory _temp = new();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteDownloadRepository _repository;
    private readonly DpapiCookieStore _store;

    public DpapiCookieStoreTests()
    {
        _factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(_factory).Run();
        _repository = new SqliteDownloadRepository(_factory);
        _store = new DpapiCookieStore(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Saved_cookies_come_back()
    {
        var id = NewDownload();

        _store.Save(id, Cookies);

        _store.Load(id).Should().Be(Cookies);
    }

    [Fact]
    public void Nothing_saved_means_null()
    {
        _store.Load(NewDownload()).Should().BeNull();
    }

    [Fact]
    public void Saving_again_replaces_the_value()
    {
        var id = NewDownload();
        _store.Save(id, Cookies);

        _store.Save(id, "session=new");

        _store.Load(id).Should().Be("session=new");
        Scalar("SELECT COUNT(*) FROM download_secrets").Should().Be(1);
    }

    [Fact]
    public void The_database_holds_only_ciphertext()
    {
        var id = NewDownload();
        _store.Save(id, Cookies);

        using (var connection = _factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cookies FROM download_secrets";
            var blob = (byte[])command.ExecuteScalar()!;
            Encoding.UTF8.GetString(blob).Should().NotContain("S3CR3T");
            blob.Length.Should().BeGreaterThan(Cookies.Length, "DPAPI adds a header and a MAC");
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_temp.Path, "ob2.db*"))
        {
            Encoding.UTF8.GetString(File.ReadAllBytes(file)).Should().NotContain("S3CR3T", file);
        }
    }

    [Fact]
    public void A_row_that_cannot_be_decrypted_is_dropped()
    {
        // What another Windows user, another PC, or a damaged file looks like.
        var id = NewDownload();
        Execute($"INSERT INTO download_secrets (download_id, cookies, updated_at) VALUES ({id}, x'0102030405', '2026-01-01')");

        _store.Load(id).Should().BeNull();

        Scalar("SELECT COUNT(*) FROM download_secrets").Should().Be(0);
    }

    [Fact]
    public void Delete_and_DeleteAll_forget_cookies()
    {
        var a = NewDownload();
        var b = NewDownload();
        var c = NewDownload();
        _store.Save(a, Cookies);
        _store.Save(b, Cookies);
        _store.Save(c, Cookies);

        _store.Delete(a);
        _store.Load(a).Should().BeNull();
        _store.Load(b).Should().Be(Cookies);

        _store.DeleteAll();
        Scalar("SELECT COUNT(*) FROM download_secrets").Should().Be(0);
    }

    [Fact]
    public void Deleting_the_download_deletes_its_cookies()
    {
        var id = NewDownload();
        _store.Save(id, Cookies);

        _repository.Delete(id);

        Scalar("SELECT COUNT(*) FROM download_secrets").Should().Be(0);
    }

    private long NewDownload() => _repository.Insert(new DownloadItem
    {
        Url = "https://example.com/a.zip",
        FileName = "a.zip",
        DestPath = @"C:\Downloads\a.zip",
        CreatedAt = DateTimeOffset.UtcNow,
    });

    private long Scalar(string sql)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
