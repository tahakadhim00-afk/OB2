using System.Collections.Concurrent;

namespace OB2.Core.Engine;

/// <summary>
/// Caps simultaneous connections per host across all downloads (§16.1), so several
/// downloads from one server do not open 8 connections each.
/// </summary>
public sealed class HostConnectionLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPerHost;

    public HostConnectionLimiter(int maxPerHost)
    {
        _maxPerHost = Math.Max(1, maxPerHost);
    }

    public async Task<IDisposable> AcquireAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var key = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        var gate = _hosts.GetOrAdd(key, _ => new SemaphoreSlim(_maxPerHost, _maxPerHost));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
