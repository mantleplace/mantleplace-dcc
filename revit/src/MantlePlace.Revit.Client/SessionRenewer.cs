using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Renews the session ahead of its access token lapsing, when <see cref="SessionRenewal"/> says to.
/// </summary>
/// <remarks>
/// Each transition of the session re-plans the one timer, so a renewal some route call made first
/// pushes this one back rather than doubling it. A renewal that lands moves the session through
/// <c>Refreshing</c> to <c>Authenticated</c>, which re-authorises the connected host's channel and
/// plans the next one.
/// </remarks>
public sealed class SessionRenewer : IDisposable
{
    private readonly AuthSession _session;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    public SessionRenewer(AuthSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _timer = new Timer(_ => _ = RenewAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _session.StateChanged += OnStateChanged;
        Plan(_session.State);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.StateChanged -= OnStateChanged;
            _timer.Dispose();
        }
    }

    private void OnStateChanged(object? sender, AuthState state) => Plan(state);

    private void Plan(AuthState state)
    {
        TimeSpan? delay = SessionRenewal.Delay(state, DateTimeOffset.UtcNow, _session.AccessTokenExpiresAt, _session.CanRenewSession);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer.Change(delay ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RenewAsync()
    {
        try
        {
            // The outcome moves the session, and that transition plans what comes next. A failed
            // renewal leaves the session Failed, which only a restore leaves (AuthStateMachine).
            if (_session.State == AuthState.Failed)
            {
                await _session.RestoreAsync().ConfigureAwait(false);
            }
            else
            {
                await _session.RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A timer callback has nobody to throw to; the next route call renews on its 401 instead.
        }
    }
}
