using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OB2.Core.Downloads;
using OB2.Core.Time;
using OB2.Storage.Database;

namespace OB2.Storage.Repositories;

/// <summary>
/// §11.1 "remember auth": cookies encrypted with Windows DPAPI for the current user (decision
/// D-67). A portable copy opened by another user or on another PC cannot decrypt them; such
/// rows are dropped, so the download simply resumes without cookies.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCookieStore : ICookieStore
{
    // Ties the ciphertext to this purpose, so another app's DPAPI blob can't be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OB2 download cookies v1");

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public DpapiCookieStore(SqliteConnectionFactory factory, IClock? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? SystemClock.Instance;
    }

    public void Save(long downloadId, string cookies)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookies);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookies), Entropy, DataProtectionScope.CurrentUser);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_secrets (download_id, cookies, updated_at) VALUES ($id, $blob, $at)
            ON CONFLICT(download_id) DO UPDATE SET cookies = excluded.cookies, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", downloadId);
        command.Parameters.AddWithValue("$blob", blob);
        command.Parameters.AddWithValue("$at", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public string? Load(long downloadId)
    {
        byte[]? blob;
        using (var connection = _factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cookies FROM download_secrets WHERE download_id = $id;";
            command.Parameters.AddWithValue("$id", downloadId);
            blob = command.ExecuteScalar() as byte[];
        }

        if (blob is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Another Windows user or PC (portable folder), or a damaged row: it can never be read.
            Delete(downloadId);
            return null;
        }
    }

    public void Delete(long downloadId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_secrets WHERE download_id = $id;";
        command.Parameters.AddWithValue("$id", downloadId);
        command.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_secrets;";
        command.ExecuteNonQuery();
    }
}
