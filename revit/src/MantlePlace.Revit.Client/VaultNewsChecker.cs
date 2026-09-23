using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>What the background listing needs from the session and the vault.</summary>
/// <remarks>
/// The seam exists so that <see cref="VaultNewsChecker"/> — when it asks, and what it does with the
/// answer — is asserted without a network (<c>HPS-42</c>). The real one is
/// <see cref="SessionNewsSource"/>.
/// </remarks>
public interface IVaultNewsSource
{
    /// <summary>Whether the session is signed in right now.</summary>
    bool SignedIn { get; }

    /// <summary>The signed-in address, or empty when the grant named none.</summary>
    string? Email { get; }

    /// <summary>Lists the vault.</summary>
    Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken);
}

/// <summary>The session and vault client this process signed in with.</summary>
public sealed class SessionNewsSource : IVaultNewsSource
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;

    public SessionNewsSource(AuthSession session, VaultClient vault)
    {
        _session = session;
        _vault = vault;
    }

    public bool SignedIn => _session.State == AuthState.Authenticated;

    public string? Email => _session.UserEmail;

    public Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
        => _vault.ListAsync(cancellationToken);
}

/// <summary>
/// Lists the vault in the background and raises the unannounced orders it finds: an order bought on
/// the web that finished while Revit was open, or one built while Revit was closed.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Bounded</b> (<c>HPS-55</c>). It runs at start, at sign-in (<see cref="Wake"/>) and every
/// <see cref="Interval"/> — never below <see cref="VaultNewsCadence.Floor"/> — and asks nothing while
/// signed out or while the vault browser is open, when the window owns the listing and marks what it
/// shows as seen.
/// </para>
/// <para>
/// ⛔ <b>Silent on failure.</b> Nobody asked for this listing, so a failure of it is nobody's news: no
/// notice, no sign-in prompt, and the order is still news at the next listing that works. A token the
/// other host rotated takes the vault client's ordinary retry-once path.
/// </para>
/// <para>
/// It never prepares and never downloads: either would spend build time, bandwidth or disk nobody
/// asked for. <see cref="Arrived"/> is raised on a thread-pool thread; a subscriber that touches a
/// window hops to Revit's UI thread itself.
/// </para>
/// </remarks>
public sealed class VaultNewsChecker
{
    private readonly IVaultNewsSource _source;
    private readonly AnnouncedOrderStore _store;
    private readonly Func<bool> _vaultOpen;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _loopGate = new();
    private CancellationTokenSource? _stop;
    private TaskCompletionSource _wake = NewWake();

    public VaultNewsChecker(IVaultNewsSource source, AnnouncedOrderStore store, Func<bool> vaultOpen)
        : this(source, store, vaultOpen, VaultNewsCadence.Interval)
    {
    }

    public VaultNewsChecker(IVaultNewsSource source, AnnouncedOrderStore store, Func<bool> vaultOpen, TimeSpan interval)
    {
        _source = source;
        _store = store;
        _vaultOpen = vaultOpen;
        Interval = VaultNewsCadence.Bounded(interval);
    }

    /// <summary>Raised with the unannounced orders one listing found, already claimed for this process.</summary>
    public event EventHandler<IReadOnlyList<VaultBundle>>? Arrived;

    /// <summary>
    /// Raised with every listing that succeeded, after <see cref="Arrived"/>, so that other work that
    /// needs the vault's state reads this listing rather than making a request of its own
    /// (<see cref="PrepareRejoiner"/>).
    /// </summary>
    public event EventHandler<VaultListing>? Listed;

    /// <summary>How long between two background listings.</summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Lists once, if the session is signed in and the vault browser is closed, and raises what is
    /// new. Never faults.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!_source.SignedIn || _vaultOpen())
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
            (VaultListing? listing, string? error) = await _source.ListAsync(cancellationToken).ConfigureAwait(false);
            // Stopped while the listing was out: claiming now would record orders as announced with
            // nobody left to announce them, for every Revit on the machine.
            if (error is not null || listing is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            VaultNewsResult news = _store.Claim(listing, _source.Email);
            if (news.Arrivals.Count > 0)
            {
                Arrived?.Invoke(this, news.Arrivals);
            }

            Listed?.Invoke(this, listing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately total: a background listing nobody asked for is never worth a fault.
        }
        catch (OperationCanceledException)
        {
            // Stopped: Revit is shutting down.
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Starts the background loop: one listing now, then one every <see cref="Interval"/>.</summary>
    public void Start()
    {
        lock (_loopGate)
        {
            if (_stop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            CancellationToken token = _stop.Token;
            _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Lists now rather than at the next tick — the session just signed in.</summary>
    public void Wake()
    {
        lock (_loopGate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>Stops the loop. Called from <c>OnShutdown</c>.</summary>
    public void Stop()
    {
        lock (_loopGate)
        {
            // Cancelled, not disposed: the loop may still be registering on its token, and a
            // disposed source throws there. One source per Revit process is nothing to collect.
            _stop?.Cancel();
            _stop = null;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Armed before the listing, so a sign-in that lands while it runs lists again at once
            // rather than waiting out the interval.
            Task woken;
            lock (_loopGate)
            {
                _wake = NewWake();
                woken = _wake.Task;
            }

            await CheckAsync(token).ConfigureAwait(false);

            // The delay is cancelled once the wait ends either way, so a wake leaves no timer behind.
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(Task.Delay(Interval, waiting.Token), woken).ConfigureAwait(false);
            waiting.Cancel();
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
