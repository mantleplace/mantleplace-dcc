namespace MantlePlace.Core;

public enum AuthState
{
    Unauthenticated,
    Authenticating,
    Authenticated,
    Refreshing,
    Failed,
}

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

public static class AuthStateMachine
{
    public const AuthState Initial = AuthState.Unauthenticated;

    public static AuthState NextState(AuthState state, AuthEvent signal) => signal switch
    {
        AuthEvent.SignOut => AuthState.Unauthenticated,

        AuthEvent.Cancel => state == AuthState.Authenticating ? AuthState.Unauthenticated : state,

        AuthEvent.BeginSignIn => state is AuthState.Authenticating or AuthState.Refreshing
            ? state
            : AuthState.Authenticating,

        AuthEvent.SignInSucceeded => state == AuthState.Authenticating ? AuthState.Authenticated : state,
        AuthEvent.SignInFailed => state == AuthState.Authenticating ? AuthState.Failed : state,

        AuthEvent.BeginRefresh => state == AuthState.Authenticated ? AuthState.Refreshing : state,

        AuthEvent.BeginRestore => state is AuthState.Unauthenticated or AuthState.Failed
            ? AuthState.Refreshing
            : state,

        AuthEvent.RefreshSucceeded => state == AuthState.Refreshing ? AuthState.Authenticated : state,
        AuthEvent.RefreshFailed => state == AuthState.Refreshing ? AuthState.Failed : state,

        _ => state,
    };
}
