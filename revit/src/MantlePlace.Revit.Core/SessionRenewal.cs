namespace MantlePlace.Revit.Core;

/// <summary>
/// When a session renews on its own, ahead of its access token lapsing.
/// </summary>
/// <remarks>
/// <para>
/// Every route call renews on a <c>401</c>, but the connected host's channel makes no route call
/// while it waits for an export. Without this, an idle Revit holds its channel until the token
/// lapses, the platform closes it, and every rejoin presents the dead token until some unrelated
/// call happens to renew it: the curator's Revit vanishes from the vault once an hour. Renewing a
/// lead ahead of the lapse re-authorises the open channel instead (<see cref="ConnectedHostEdge"/>).
/// </para>
/// <para>
/// The lead is wider than <see cref="TokenGrants.ExpirySkewSeconds"/>, so a token is renewed before
/// any request would have called it expired.
/// </para>
/// </remarks>
public static class SessionRenewal
{
    /// <summary>How long before the lapse a signed-in session renews.</summary>
    public const int LeadSeconds = 2 * TokenGrants.ExpirySkewSeconds;

    /// <summary>How long a renewal the platform could not answer waits before trying again.</summary>
    public const int RetrySeconds = 60;

    /// <summary>
    /// How long to wait before renewing, or <c>null</c> when nothing should be scheduled: signed out,
    /// mid sign-in, already renewing, or failed with no refresh token left to renew from.
    /// </summary>
    public static TimeSpan? Delay(AuthState state, DateTimeOffset now, DateTimeOffset expiresAt, bool canRenew)
    {
        switch (state)
        {
            case AuthState.Authenticated:
                TimeSpan left = expiresAt - now - TimeSpan.FromSeconds(LeadSeconds);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;

            case AuthState.Failed when canRenew:
                return TimeSpan.FromSeconds(RetrySeconds);

            default:
                return null;
        }
    }
}
