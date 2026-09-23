using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Picks back up the Prepares an earlier Revit was watching when it closed or crashed, so the
/// curator is told how they ended (<see cref="InterruptedPrepares"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> At every sign-in — the startup restore of a stored session is one — and never while
/// signed out, and it never asks for a sign-in: an entry waits in the record for the next one, and
/// for at most <see cref="InterruptedPrepares.MaxAge"/>.
/// </para>
/// <para>
/// <b>What it costs.</b> Nothing unless the record holds an entry this process may take. Then one
/// listing, which says which of those orders the platform would still build: one missing from this
/// account's vault, or no longer available in it, is dropped without a notice. Each of the rest is
/// handed to <see cref="PrepareWatcher.Rejoin"/>, one start request per order and never a second
/// build (<c>HPS-24</c>).
/// </para>
/// <para>
/// <b>Silent</b>, like the background listing it shares a seam with: a listing that fails leaves the
/// entries this process just took in the record, to be tried at the next sign-in. Nothing here shows
/// the curator anything; a re-joined Prepare is announced by the watcher when it ends.
/// </para>
/// </remarks>
public sealed class PrepareRejoiner
{
    private readonly IVaultNewsSource _source;
    private readonly InterruptedPrepareStore _store;
    private readonly PrepareWatcher _watcher;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public PrepareRejoiner(IVaultNewsSource source, InterruptedPrepareStore store, PrepareWatcher watcher)
        : this(source, store, watcher, () => DateTimeOffset.UtcNow)
    {
    }

    public PrepareRejoiner(IVaultNewsSource source, InterruptedPrepareStore store, PrepareWatcher watcher, Func<DateTimeOffset> now)
    {
        _source = source;
        _store = store;
        _watcher = watcher;
        _now = now;
    }

    /// <summary>Re-joins in the background — the session just signed in. Never faults.</summary>
    public void Wake() => _ = Task.Run(() => RejoinAsync(_stop.Token), CancellationToken.None);

    /// <summary>Stops re-joining. Called from <c>OnShutdown</c>, before the watcher is interrupted.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>
    /// Takes the interrupted Prepares this process may re-join and re-joins the ones still worth it.
    /// Never faults.
    /// </summary>
    public async Task RejoinAsync(CancellationToken cancellationToken)
    {
        if (!_source.SignedIn)
        {
            return;
        }

        try
        {
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // An order this process is already watching is its own Prepare, not an interrupted one:
            // the record holds it because it is running.
            List<InterruptedPrepare> claimed =
                [.. _store.Claim(_now()).Where(entry => _watcher.Watching(entry.OrderId) is null)];
            if (claimed.Count == 0)
            {
                return;
            }

            (VaultListing? listing, string? error) = await _source.ListAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null || listing is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (InterruptedPrepare entry in claimed)
            {
                if (InterruptedPrepares.RowFor(listing, entry.OrderId) is { } row)
                {
                    _watcher.Rejoin(row, entry.StartedAt);
                }
                else
                {
                    _store.Drop(entry.OrderId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately total: nobody pressed anything, so a failure here is nobody's news.
        }
        catch (OperationCanceledException)
        {
            // Stopped: Revit is shutting down, and the entries stay for the next one.
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
