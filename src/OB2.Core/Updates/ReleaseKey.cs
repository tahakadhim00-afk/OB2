namespace OB2.Core.Updates;

/// <summary>
/// D-84: the public keys whose signature OB2 accepts on an update. The private half stays with
/// the developer, outside the repository (password-protected, made by
/// <c>build/OB2.ReleaseSigner keygen</c>). A list, so a new key can be added in one release and
/// the old one removed in a later one, after every copy has updated past the change.
/// </summary>
public static class ReleaseKey
{
    /// <summary>SubjectPublicKeyInfo of each trusted ECDSA P-256 key, Base64.</summary>
    public static IReadOnlyList<string> Trusted { get; } =
    [
        // Taha Kadhim's release key, made 2026-11-24 for 2.0.0, the first release that updates itself.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhYZjKoaYr9qAZLqzaJedGzOfSqo9/Rfgi7TgGNE5V+uG6vtj4vJhLNi14UYkiuonTK7k2bs+fuermW5UV1xYzA==",
    ];
}
