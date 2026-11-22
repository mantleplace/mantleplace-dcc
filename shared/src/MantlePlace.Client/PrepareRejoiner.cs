using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class PrepareRejoiner
{
    private readonly InterruptedPrepareStore _store;
    private readonly PrepareWatcher _watcher;
    private readonly Func<DateTimeOffset> _now;
    private int _armed;
    private volatile bool _stopped;

    public PrepareRejoiner(InterruptedPrepareStore store, PrepareWatcher watcher)
        : this(store, watcher, () => DateTimeOffset.UtcNow)
    {
    }

    public PrepareRejoiner(InterruptedPrepareStore store, PrepareWatcher watcher, Func<DateTimeOffset> now)
    {
        _store = store;
        _watcher = watcher;
        _now = now;
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Stop() => _stopped = true;

    public void OnListed(VaultListing listing)
    {
        if (_stopped || Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return;
        }

        try
        {
            Rejoin(listing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ClientLog.Write($"Re-joining interrupted Prepares failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Rejoin(VaultListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        foreach (InterruptedPrepare entry in _store.Claim(_now()).Where(entry => _watcher.Watching(entry.OrderId) is null))
        {
            if (_stopped)
            {
                return;
            }

            RejoinDecision decision = InterruptedPrepares.Decide(listing, entry.OrderId, out VaultBundle? row);
            if (decision == RejoinDecision.Rejoin)
            {
                _watcher.Rejoin(row!, entry.StartedAt);
            }
            else if (decision == RejoinDecision.Drop)
            {
                _store.Drop(entry.OrderId);
            }
        }
    }
}
