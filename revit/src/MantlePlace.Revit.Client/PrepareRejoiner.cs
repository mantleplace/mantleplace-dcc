using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Picks back up the Prepares an earlier Revit was watching when it closed or crashed, so the
/// curator is told how they ended (<see cref="InterruptedPrepares"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>It makes no request of its own to find out what is still worth re-joining</b>
/// (<c>HPS-55</c>). A listing nobody asked for is bounded to one at start, one at each sign-in and
/// one per interval, never while the vault browser is open — and <see cref="VaultNewsChecker"/>
/// already makes exactly those. So a sign-in <see cref="Arm"/>s this, and the checker's next listing
/// that succeeds is the one it re-joins from (<see cref="OnListed"/>). A sign-in with the vault open,
/// or a listing that fails, leaves it armed for the next listing that happens.
/// </para>
/// <para>
/// <b>Once per sign-in.</b> The startup restore of a stored session is one. An entry whose re-join
/// start fails is kept and tried again at the next sign-in, not at every listing: a start is a
/// request too, and nobody pressed anything. An entry waits in the record for at most
/// <see cref="InterruptedPrepares.MaxAge"/>.
/// </para>
/// <para>
/// <b>Silent.</b> Nothing here shows the curator anything; a re-joined Prepare is announced by the
/// watcher when it ends, one start request per order and never a second build (<c>HPS-24</c>).
/// </para>
/// </remarks>
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

    /// <summary>
    /// The session just signed in: re-join from the next listing. Called before the listing is
    /// woken, so that listing is the one it uses.
    /// </summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Stops re-joining. Called from <c>OnShutdown</c>, before the watcher is interrupted.</summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// A background listing succeeded. Re-joins from it if a sign-in armed this, and never faults:
    /// it runs on the listing's thread-pool thread.
    /// </summary>
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
#pragma warning disable CA1031 // Nobody pressed anything, so a failure here is nobody's news.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Takes the interrupted Prepares this process may re-join and, against <paramref name="listing"/>,
    /// re-joins the ones the platform would still build.
    /// </summary>
    public void Rejoin(VaultListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        // An order this process is already watching is its own Prepare, not an interrupted one: the
        // record holds it because it is running.
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
