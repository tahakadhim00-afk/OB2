namespace OB2.Core.Downloads;

/// <summary>
/// "Remember auth" (§11.1): cookies of unfinished downloads kept across app restarts, encrypted.
/// Only used when the user turns the option on; otherwise cookies live in memory only.
/// </summary>
public interface ICookieStore
{
    void Save(long downloadId, string cookies);

    /// <returns>The cookies, or null when none are stored or they can no longer be decrypted.</returns>
    string? Load(long downloadId);

    void Delete(long downloadId);

    /// <summary>Forgets every stored cookie (the user turned the option off).</summary>
    void DeleteAll();
}
