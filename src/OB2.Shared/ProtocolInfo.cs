namespace OB2.Shared;

/// <summary>Fixed values from the browser-bridge protocol contract (PRD §8.3).</summary>
public static class ProtocolInfo
{
    /// <summary>The <c>v</c> field of every message. Bump when message names or fields change.</summary>
    public const int Version = 1;

    /// <summary>Chrome Native Messaging host name registered under HKCU (§8.2).</summary>
    public const string HostName = "com.ob2.host";

    /// <summary>Messages above 1 MB are rejected.</summary>
    public const int MaxMessageBytes = 1024 * 1024;
}
