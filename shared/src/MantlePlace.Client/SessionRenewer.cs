using MantlePlace.Core;

namespace MantlePlace.Client;

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
        }
    }
}
