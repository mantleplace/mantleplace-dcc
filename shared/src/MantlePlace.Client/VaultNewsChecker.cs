using MantlePlace.Core;

namespace MantlePlace.Client;

public interface IVaultNewsSource
{
    bool SignedIn { get; }

    string? Email { get; }

    Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken);
}

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

    public event EventHandler<IReadOnlyList<VaultBundle>>? Arrived;

    public event EventHandler<VaultListing>? Listed;

    public TimeSpan Interval { get; }

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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            ClientLog.Write($"The background vault listing failed: {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ClientLog.Write($"The background vault listing hit an unexpected fault: {ex}");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

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

    public void Wake()
    {
        lock (_loopGate)
        {
            _wake.TrySetResult();
        }
    }

    public void Stop()
    {
        lock (_loopGate)
        {
            _stop?.Cancel();
            _stop = null;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Task woken;
            lock (_loopGate)
            {
                _wake = NewWake();
                woken = _wake.Task;
            }

            await CheckAsync(token).ConfigureAwait(false);

            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(Task.Delay(Interval, waiting.Token), woken).ConfigureAwait(false);
            waiting.Cancel();
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
