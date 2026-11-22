namespace MantlePlace.Core;

public static class SessionRenewal
{
    public const int LeadSeconds = 2 * TokenGrants.ExpirySkewSeconds;

    public const int RetrySeconds = 60;

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
