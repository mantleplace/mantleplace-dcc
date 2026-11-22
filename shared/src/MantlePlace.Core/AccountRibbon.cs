namespace MantlePlace.Core;

public enum AccountFaceAction
{
    SignIn,

    About,
}

public readonly record struct AccountRibbonState(
    string FaceText,
    bool FaceEnabled,
    string FaceToolTip,
    AccountFaceAction FaceAction,
    bool IdentityVisible,
    string IdentityText,
    bool SignOutEnabled,
    string SessionSummary);

public static class AccountRibbon
{
    public const string SignInFace = "Sign In";

    public const string SigningInFace = "Signing In…";

    public const string SignedInFace = "Signed In";

    public const string SendFeedbackFace = "Send Feedback";

    public static AccountRibbonState For(AuthState state, string? email, bool hasStoredSession)
    {
        string address = (email ?? string.Empty).Trim();
        bool authenticated = state == AuthState.Authenticated;
        string summary = DescribeSession(state, address);

        bool signOutEnabled = authenticated || hasStoredSession;

        return state switch
        {
            AuthState.Authenticating or AuthState.Refreshing => new AccountRibbonState(
                FaceText: SigningInFace,
                FaceEnabled: false,

                FaceToolTip: address.Length > 0
                    ? $"Renewing the Mantle Place session for {address}…"
                    : "Signing in to Mantle Place…",
                FaceAction: AccountFaceAction.SignIn,
                IdentityVisible: false,
                IdentityText: string.Empty,
                SignOutEnabled: signOutEnabled,
                SessionSummary: summary),

            AuthState.Authenticated => new AccountRibbonState(
                FaceText: SignedInFace,
                FaceEnabled: true,
                FaceToolTip: $"{summary} Opens About Mantle Place.",
                FaceAction: AccountFaceAction.About,
                IdentityVisible: IsDrawableAsItemText(address),
                IdentityText: address,
                SignOutEnabled: signOutEnabled,
                SessionSummary: summary),

            _ => new AccountRibbonState(
                FaceText: SignInFace,
                FaceEnabled: true,
                FaceToolTip: "Sign in to Mantle Place in your browser.",
                FaceAction: AccountFaceAction.SignIn,
                IdentityVisible: false,
                IdentityText: string.Empty,
                SignOutEnabled: signOutEnabled,
                SessionSummary: summary),
        };
    }

    public static string DescribeSession(AuthState state, string? email)
    {
        string address = (email ?? string.Empty).Trim();

        if (state != AuthState.Authenticated)
        {
            return "Not signed in.";
        }

        return address.Length > 0 ? $"Signed in as {address}." : "Signed in to Mantle Place.";
    }

    private static bool IsDrawableAsItemText(string text)
        => text.Length > 0 && !text.Contains('%', StringComparison.Ordinal);
}
