using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// What the Account face says, and when it is allowed to say it.
/// </summary>
/// <remarks>
/// <para>
/// The ribbon used to know nothing about the session: two always-enabled push buttons, with the
/// truth enforced at click time by a dialog. So the answer to "am I signed in?" was "click Sign in
/// and find out", which is the failure this pins shut — the face may not claim a state the session
/// is not in.
/// </para>
/// <para>
/// It lives in the pure core because the decision is a pure one and the shim is never built in CI
/// (<c>HPS-02</c>, <c>HPS-42</c>). The shim's remaining job is to copy these strings onto a
/// <c>SplitButton</c>, which is the part no test can reach.
/// </para>
/// </remarks>
internal static class AccountRibbonTests
{
    /// <summary>Every state, so a new one cannot be added without deciding what the face says.</summary>
    private static readonly AuthState[] EveryState =
    [
        AuthState.Unauthenticated,
        AuthState.Authenticating,
        AuthState.Authenticated,
        AuthState.Refreshing,
        AuthState.Failed,
    ];

    private static readonly string[] EveryAddress =
    [
        "",
        "   ",
        "curator@example.com",
        "od%d@example.com",
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("signed out: the face offers the sign-in and nothing else is on show", () =>
        {
            AccountRibbonState face = AccountRibbon.For(AuthState.Unauthenticated, string.Empty, false);

            run.Equal(face.FaceText, "Sign In", "the face invites a sign-in");
            run.Equal(face.FaceEnabled, true, "it is clickable");
            run.Equal(face.FaceAction == AccountFaceAction.SignIn, true, "clicking it signs in");
            run.Equal(face.IdentityVisible, false, "no address is shown when there is none");
            run.Equal(face.SignOutEnabled, false, "there is nothing to sign out of");
        });

        run.Case("signing in: the face says so and refuses a second click", () =>
        {
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticating, string.Empty, false);

            run.Equal(face.FaceText, "Signing In…", "the face names the wait");
            run.Equal(face.FaceEnabled, false, "a second browser round-trip would race the first");
        });

        run.Case("restoring a stored grant is the same wait, in the same words", () =>
        {
            // The reference host draws Authenticating and Refreshing with one label, and a curator
            // cannot tell a browser round-trip from a token renewal apart anyway. Two labels here
            // would be two vocabularies for one wait.
            AccountRibbonState face = AccountRibbon.For(AuthState.Refreshing, "curator@example.com", true);

            run.Equal(face.FaceText, "Signing In…", "a refresh reads as signing in");
            run.Equal(face.FaceEnabled, false, "the face is disabled while the session moves");
            run.Equal(face.SignOutEnabled, true, "a stored grant can still be dropped");

            // An address is only ever known here because a session already exists, and the wait is
            // the one moment a curator most wants to know whose. Dropping it was the tooltip
            // reporting less than the session knew.
            run.Contains(face.FaceToolTip, "curator@example.com", "the wait names whose session it is");
        });

        run.Case("a first sign-in has no address to name, and does not pretend otherwise", () =>
        {
            // Authenticating from signed-out, and the startup restore, both arrive with nothing:
            // UserEmail is not set until a grant comes back.
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticating, string.Empty, false);

            run.Equal(face.FaceToolTip, "Signing in to Mantle Place…", "no address, no claim");
        });

        run.Case("the session is described once, and every surface reads that one sentence", () =>
        {
            run.Equal(
                AccountRibbon.DescribeSession(AuthState.Authenticated, "curator@example.com"),
                "Signed in as curator@example.com.",
                "signed in, with an address");
            run.Equal(
                AccountRibbon.DescribeSession(AuthState.Authenticated, "  curator@example.com "),
                "Signed in as curator@example.com.",
                "trimmed, like everywhere else");
            run.Equal(
                AccountRibbon.DescribeSession(AuthState.Authenticated, null),
                "Signed in to Mantle Place.",
                "signed in, no address: the session is still real");
            run.Equal(
                AccountRibbon.DescribeSession(AuthState.Refreshing, "curator@example.com"),
                "Not signed in.",
                "a renewal in flight is not a session yet");
            run.Equal(
                AccountRibbon.DescribeSession(AuthState.Failed, "curator@example.com"),
                "Not signed in.",
                "and a refused renewal is not one either");

            // The face's tooltip opens with the same sentence, so About and the ribbon can never
            // disagree about whether this Revit is signed in.
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticated, "curator@example.com", true);
            run.Equal(
                face.SessionSummary,
                "Signed in as curator@example.com.",
                "the state carries it for the About dialog");
            run.Contains(face.FaceToolTip, face.SessionSummary, "and the tooltip opens with it");
        });

        run.Case("signed in: the face says signed in, the address is on the dropdown", () =>
        {
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticated, "curator@example.com", true);

            run.Equal(face.FaceText, "Signed In", "the face reports the session");
            run.Equal(face.FaceEnabled, true, "it still opens something");
            run.Equal(face.FaceAction == AccountFaceAction.About, true, "signed in, the face opens About");
            run.Equal(face.IdentityVisible, true, "the address has somewhere to be seen");
            run.Equal(face.IdentityText, "curator@example.com", "and it is the address itself");
            run.Contains(face.FaceToolTip, "curator@example.com", "the tooltip carries it too");
            run.Equal(face.SignOutEnabled, true, "there is a session to drop");
        });

        run.Case("signed in with no address: nothing is invented", () =>
        {
            // A grant that omits the email is legal — the address is a convenience, not the session.
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticated, string.Empty, true);

            run.Equal(face.FaceText, "Signed In", "the session is still reported");
            run.Equal(face.IdentityVisible, false, "an empty row would read as an empty account");
            run.Equal(face.SignOutEnabled, true, "and it can still be signed out of");
        });

        run.Case("a failed renewal does not leave the face claiming a session", () =>
        {
            // Failed is where a refresh lands when the platform will not renew. The old ribbon could
            // not move, so it went on offering Sign out and reporting nothing.
            AccountRibbonState face = AccountRibbon.For(AuthState.Failed, "curator@example.com", true);

            run.Equal(face.FaceText, "Sign In", "the face asks for the sign-in that would fix it");
            run.Equal(face.FaceEnabled, true, "and it is clickable, because that is the remedy");
            run.Equal(face.FaceAction == AccountFaceAction.SignIn, true, "clicking it signs in");
            run.Equal(face.IdentityVisible, false, "the address it used to hold is no longer true");
            run.Equal(face.SignOutEnabled, true, "the stored grant is still there to be cleared");
        });

        run.Case("sign out is offered exactly when something would be dropped", () =>
        {
            run.Equal(
                AccountRibbon.For(AuthState.Unauthenticated, string.Empty, false).SignOutEnabled,
                false,
                "signed out with an empty store: the old button reported \"Signed out.\" anyway");
            run.Equal(
                AccountRibbon.For(AuthState.Unauthenticated, string.Empty, true).SignOutEnabled,
                true,
                "signed out with a stored grant: clearing this machine is a real action");
            run.Equal(
                AccountRibbon.For(AuthState.Authenticated, "curator@example.com", true).SignOutEnabled,
                true,
                "signed in: the ordinary case");
        });

        run.Case("the address is trimmed, and one Revit cannot draw hides the row", () =>
        {
            AccountRibbonState padded =
                AccountRibbon.For(AuthState.Authenticated, "  curator@example.com  ", true);
            run.Equal(padded.IdentityText, "curator@example.com", "a padded address is trimmed");

            // ⛔ RibbonItem.ItemText throws Autodesk.Revit.Exceptions.ArgumentException on a string
            // containing "%", and an address may legally contain one. A throw during the state
            // update lands on Revit's dispatcher, which is how an add-in takes the process down.
            AccountRibbonState hostile = AccountRibbon.For(AuthState.Authenticated, "od%d@example.com", true);
            run.Equal(hostile.IdentityVisible, false, "an address Revit cannot draw is not drawn");
            run.Contains(hostile.FaceToolTip, "od%d@example.com", "the tooltip has no such limit and keeps it");
            run.Equal(hostile.FaceText, "Signed In", "and the session is still reported");
        });

        run.Case("every state yields text Revit will accept", () =>
        {
            foreach (AuthState state in EveryState)
            {
                foreach (string email in EveryAddress)
                {
                    AccountRibbonState face = AccountRibbon.For(state, email, true);

                    run.True(face.FaceText.Length > 0, $"{state}: the face text is never empty");
                    run.False(face.FaceText.Contains('%'), $"{state}: the face text never contains %");
                    run.True(face.FaceToolTip.Length > 0, $"{state}: the tooltip is never empty");
                    run.True(face.SessionSummary.Length > 0, $"{state}: the summary is never empty");

                    if (face.IdentityVisible)
                    {
                        run.True(face.IdentityText.Length > 0, $"{state}: a visible row has text");
                        run.False(face.IdentityText.Contains('%'), $"{state}: a visible row never contains %");
                    }
                }
            }
        });

        run.Case("a null address is the same as no address", () =>
        {
            AccountRibbonState face = AccountRibbon.For(AuthState.Authenticated, null, true);

            run.Equal(face.IdentityVisible, false, "nothing to show");
            run.Equal(face.FaceText, "Signed In", "and the session is still reported");
        });

        return run.Report("account ribbon");
    }
}
