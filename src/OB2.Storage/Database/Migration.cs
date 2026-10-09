using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OB2.Storage.Database;

/// <summary>One numbered schema script, e.g. <c>001_init.sql</c>.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>
    /// SHA-256 of the script with line endings normalized, so a git checkout with different
    /// autocrlf settings does not look like an edited migration.
    /// </summary>
    public string Checksum { get; } = ComputeChecksum(Sql);

    private static string ComputeChecksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

public interface IMigrationSource
{
    IReadOnlyList<Migration> GetMigrations();
}

/// <summary>Reads <c>Migrations/NNN_name.sql</c> files embedded in this assembly.</summary>
public sealed partial class EmbeddedMigrationSource : IMigrationSource
{
    private const string ResourcePrefix = "OB2.Migrations.";

    public static EmbeddedMigrationSource Instance { get; } = new();

    public IReadOnlyList<Migration> GetMigrations()
    {
        var assembly = typeof(EmbeddedMigrationSource).Assembly;
        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[ResourcePrefix.Length..];
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new MigrationException($"Migration resource '{fileName}' does not match NNN_name.sql.");
            }

            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                ReadResource(assembly, resource)));
        }

        return migrations;
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new MigrationException($"Migration resource '{resource}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^(?<version>\d{3,})_(?<name>[A-Za-z0-9_\-]+)\.sql$")]
    private static partial Regex FileNamePattern();
}

public sealed class MigrationException : Exception
{
    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MigrationException()
    {
    }
}
