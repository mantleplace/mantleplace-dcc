using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>"Sign in": opens the system browser and waits for the loopback redirect.</summary>
/// <remarks>
/// <para>
/// ⛔<c>HPS-05</c>: the system browser, never an embedded webview and never a password field in
/// Revit. Every decision — PKCE, state validation, which failure a callback is — is made in
/// <c>MantlePlace.Revit.Core</c> and executed by <c>MantlePlace.Revit.Client</c>. This command is
/// the button.
/// </para>
/// <para>
/// The sign-in is awaited on a background thread and only the report comes back here. It touches no
/// <see cref="Document"/>, so it needs no <c>ExternalEvent</c> — that machinery arrives with the
/// vault browser, which does.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SignInCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        AuthSession session = MantlePlaceApplication.Session;

        if (session.State == AuthState.Authenticated)
        {
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Already signed in.",
                MainContent = session.UserEmail.Length > 0
                    ? $"Signed in as {session.UserEmail}."
                    : "This Revit session is signed in.",
            }.Show();
            return Result.Succeeded;
        }

        // Restoring a stored session at startup, or renewing an expiring one, both land here. The
        // state machine would refuse a sign-in raised on top of either and hand back Abandoned,
        // which reads as Result.Cancelled -- a button that does nothing at all. Say what is
        // happening instead: the wait is a network round-trip, not a browser one.
        if (session.State == AuthState.Refreshing)
        {
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Resuming your last session.",
                MainContent = "Mantle Place is restoring the sign-in stored on this machine. "
                    + "Give it a moment, then try again — you may not need to sign in at all.",
            }.Show();
            return Result.Cancelled;
        }

        // Blocking the UI thread on the browser round-trip would freeze Revit for up to the
        // five-minute timeout. GetAwaiter().GetResult() after a Task.Run keeps the wait off the
        // dispatcher; the modeless progress surface arrives with the vault browser.
        AuthOutcome outcome = Task.Run(() => session.SignInAsync()).GetAwaiter().GetResult();

        if (outcome.Cancelled)
        {
            // HPS-09: timing out is a cancellation, not a failure. A curator who wandered off comes
            // back to a signed-out plugin, not an error they have to dismiss.
            return Result.Cancelled;
        }

        if (!outcome.Succeeded)
        {
            message = outcome.Message;
            return Result.Failed;
        }

        new TaskDialog("Mantle Place")
        {
            MainInstruction = "Signed in.",
            MainContent = Describe(session),
        }.Show();

        return Result.Succeeded;
    }

    /// <summary>
    /// Says plainly whether the session survives a restart (<c>HPS-16</c>).
    /// </summary>
    private static string Describe(AuthSession session)
    {
        string who = session.UserEmail.Length > 0
            ? $"Signed in as {session.UserEmail}."
            : "Signed in.";

        return session.IsPersistent
            ? who
            : who + " This machine has no secure credential store, so you will need to sign in again "
                + "next time Revit starts.";
    }
}

/// <summary>"Sign out": drops the session and clears the stored refresh token.</summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SignOutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        MantlePlaceApplication.Session.SignOut();

        new TaskDialog("Mantle Place")
        {
            MainInstruction = "Signed out.",
            MainContent = "The stored session on this machine has been cleared.",
        }.Show();

        return Result.Succeeded;
    }
}
