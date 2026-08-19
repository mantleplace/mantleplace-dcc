namespace MantlePlace.Revit.Core;

/// <summary>The five auth states (<c>HPS-12</c>).</summary>
public enum AuthState
{
    Unauthenticated,
    Authenticating,
    Authenticated,
    Refreshing,
    Failed,
}

/// <summary>Everything that can move the session.</summary>
public enum AuthEvent
{
    BeginSignIn,
    SignInSucceeded,
    SignInFailed,
    Cancel,
    BeginRefresh,
    RefreshSucceeded,
    RefreshFailed,
    BeginRestore,
    SignOut,
}

/// <summary>
/// <c>HPS-12</c>: the transition table from corpus <c>auth.stateMachine</c>, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>An event with no matching rule leaves the state unchanged.</b> That default is the rule that
/// matters: an out-of-order callback — a redirect arriving after a cancel, a refresh completing
/// after a sign-out — must not be able to corrupt the session, and a machine that throws or that
/// falls through to <see cref="AuthState.Failed"/> turns a harmless late message into a signed-out
/// curator.
/// </para>
/// <para>
/// The host's suite drives the corpus table across every (state × event) pair rather than
/// re-deriving the guards, so this function and the vectors cannot drift.
/// </para>
/// </remarks>
public static class AuthStateMachine
{
    public const AuthState Initial = AuthState.Unauthenticated;

    /// <summary>Where <paramref name="state"/> goes on <paramref name="signal"/>.</summary>
    public static AuthState NextState(AuthState state, AuthEvent signal) => signal switch
    {
        // From any state. Signing out is always allowed and always lands the same place.
        AuthEvent.SignOut => AuthState.Unauthenticated,

        // Cancelling an in-flight sign-in returns to signed-out; cancelling anything else is a
        // no-op. Cancel never latches Failed — a curator who changed their mind has not hit an
        // error.
        AuthEvent.Cancel => state == AuthState.Authenticating ? AuthState.Unauthenticated : state,

        // A second sign-in while one is in flight is ignored, not queued: two browser tabs and two
        // loopback listeners racing for one callback is worse than one that is already open.
        AuthEvent.BeginSignIn => state is AuthState.Authenticating or AuthState.Refreshing
            ? state
            : AuthState.Authenticating,

        AuthEvent.SignInSucceeded => state == AuthState.Authenticating ? AuthState.Authenticated : state,
        AuthEvent.SignInFailed => state == AuthState.Authenticating ? AuthState.Failed : state,

        AuthEvent.BeginRefresh => state == AuthState.Authenticated ? AuthState.Refreshing : state,

        // Restore runs at startup from a stored refresh token, so it starts from signed-out — or
        // from Failed, which is what a previous startup's network blip left behind.
        AuthEvent.BeginRestore => state is AuthState.Unauthenticated or AuthState.Failed
            ? AuthState.Refreshing
            : state,

        AuthEvent.RefreshSucceeded => state == AuthState.Refreshing ? AuthState.Authenticated : state,
        AuthEvent.RefreshFailed => state == AuthState.Refreshing ? AuthState.Failed : state,

        _ => state,
    };
}
