namespace OB2.Core.Engine;

/// <summary>D-90: a running total of bytes received, shared by every download of one engine.</summary>
internal sealed class ByteCounter
{
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    public void Add(long bytes) => Interlocked.Add(ref _total, bytes);
}
