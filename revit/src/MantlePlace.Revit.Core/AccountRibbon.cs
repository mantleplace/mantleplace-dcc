namespace MantlePlace.Revit.Core;

/// <summary>What clicking the Account face does, which depends on the session.</summary>
public enum AccountFaceAction
{
    /// <summary>Start the browser sign-in.</summary>
    SignIn,

    /// <summary>Open "About Mantle Place" — there is nothing left to sign in to.</summary>
    About,
}

/// <summary>
/// Everything the Account split button shows for one auth state.
/// </summary>
/// <param name="FaceText">The face's text. Never empty, never contains <c>%</c>.</param>
/// <param name="FaceEnabled">Whether the face may be clicked.</param>
/// <param name="FaceToolTip">The face's one-line tooltip, carrying the address when known.</param>
/// <param name="FaceAction">What a click on the face does.</param>
/// <param name="IdentityVisible">Whether the dropdown's address row is shown at all.</param>
/// <param name="IdentityText">That row's text. Meaningful only when <paramref name="IdentityVisible"/>.</param>
/// <param name="SignOutEnabled">Whether signing out would drop anything.</param>
/// <param name="SessionSummary">The session in one sentence, for the About dialog.</param>
public readonly record struct AccountRibbonState(
    string FaceText,
    bool FaceEnabled,
    string FaceToolTip,
    AccountFaceAction FaceAction,
    bool IdentityVisible,
    string IdentityText,
    bool SignOutEnabled,
    string SessionSummary);

/// <summary>
/// The Account split button's face and dropdown, derived from the auth session and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>The ribbon may not claim a session the process is not in.</b> Before this, the Account panel
/// held two always-enabled push buttons whose <c>PushButton</c> references were discarded at startup,
/// so nothing could ever change them: state was enforced at click time by a dialog, and the only way
/// to learn whether you were signed in was to click Sign in and read the refusal. The address was
/// visible nowhere but inside that dialog and the sign-in window.
/// </para>
/// <para>
/// The words are the reference host's, deliberately: <c>Sign In</c>, the same one label for both
/// waits, and disabled while either runs (<c>SMantlePlaceVaultPanel::GetAuthButtonText</c>). Only the
/// casing is Revit's — Title Case, because every Autodesk tab beside ours uses it. The face's third
/// state differs because the control does: Unreal's single button toggles to <c>Sign Out</c>, while
/// here signing out moved into the dropdown, so the face reports <c>Signed In</c> instead of offering
/// the opposite action.
/// </para>
/// <para>
/// In the pure core rather than the shim because it is a pure decision and the shim is never built in
/// CI (<c>HPS-02</c>, <c>HPS-42</c>). What is left in the shim is assignment onto a
/// <c>SplitButton</c>, and nothing that has to be reasoned about.
/// </para>
/// </remarks>
public static class AccountRibbon
{
    /// <summary>Signed out, or a renewal the platform refused. The remedy is the same.</summary>
    public const string SignInFace = "Sign In";

    /// <summary>Both waits — the browser round-trip and the token renewal — under one word.</summary>
    public const string SigningInFace = "Signing In…";

    /// <summary>Authenticated. The face reports, it does not offer the opposite action.</summary>
    public const string SignedInFace = "Signed In";

    /// <summary>
    /// The face and dropdown for a session.
    /// </summary>
    /// <param name="state">The session's state (<c>HPS-12</c>).</param>
    /// <param name="email">The signed-in address, or null/empty when the grant named none.</param>
    /// <param name="hasStoredSession">
    /// Whether a refresh token is held — <c>AuthSession.CanRenewSession</c>. Separate from
    /// <paramref name="state"/> because "signed out with a grant stored on this machine" and "signed
    /// out with nothing stored" are the same state and want different dropdowns: only the first has
    /// something for Sign Out to clear.
    /// </param>
    public static AccountRibbonState For(AuthState state, string? email, bool hasStoredSession)
    {
        string address = (email ?? string.Empty).Trim();
        bool authenticated = state == AuthState.Authenticated;
        string summary = DescribeSession(state, address);

        // A signed-out session that still holds a refresh token is the cross-host case: the other
        // host on this machine may have written it, and clearing it is exactly what Sign Out is for.
        bool signOutEnabled = authenticated || hasStoredSession;

        return state switch
        {
            AuthState.Authenticating or AuthState.Refreshing => new AccountRibbonState(
                FaceText: SigningInFace,
                FaceEnabled: false,

                // An address is known here only when a session already exists, which makes this a
                // renewal rather than a first sign-in — so the wait can say whose session it is
                // rather than dropping the one fact the curator would want while they wait.
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

            // Unauthenticated, and Failed — a renewal the platform would not grant. Both are signed
            // out and both are fixed the same way, so neither gets its own word on the face. What
            // went wrong is said where it happened, not left on a ribbon button forever.
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

    /// <summary>
    /// The session in one sentence — the line the About dialog prints and the face's tooltip opens
    /// with.
    /// </summary>
    /// <remarks>
    /// Here rather than at each surface because it had started to spread: the same sentence was being
    /// rebuilt from <c>State</c> and <c>UserEmail</c> wherever one was wanted, each copy free to drift
    /// into claiming a session the other would not have. Deriving it once is the whole point of this
    /// type.
    /// </remarks>
    public static string DescribeSession(AuthState state, string? email)
    {
        string address = (email ?? string.Empty).Trim();

        if (state != AuthState.Authenticated)
        {
            return "Not signed in.";
        }

        return address.Length > 0 ? $"Signed in as {address}." : "Signed in to Mantle Place.";
    }

    /// <summary>
    /// Whether Revit will accept <paramref name="text"/> as a <c>RibbonItem.ItemText</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>ItemText</c> throws <c>Autodesk.Revit.Exceptions.ArgumentException</c> on an empty string
    /// and on one containing <c>%</c> — the ribbon's own format marker. An address may legally hold a
    /// <c>%</c>, and the assignment happens inside a state-change handler on Revit's dispatcher,
    /// where a throw is how an add-in ends the process rather than how it reports a problem. So the
    /// row is hidden rather than risked; the tooltip, which has no such restriction, still carries
    /// the whole address.
    /// </remarks>
    private static bool IsDrawableAsItemText(string text)
        => text.Length > 0 && !text.Contains('%', StringComparison.Ordinal);
}
