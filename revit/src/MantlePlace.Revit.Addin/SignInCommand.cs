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
/// The command STARTS the sign-in and returns. <see cref="IExternalCommand.Execute"/> is
/// synchronous and the browser round-trip is not, so waiting for it here froze the whole
/// application for up to the five-minute timeout. Progress and cancellation live on
/// <see cref="SignInWindow"/>, which is modeless, so Revit stays usable while the curator is in
/// their browser.
/// </para>
/// <para>
/// It touches no <see cref="Document"/>, so it needs no <c>ExternalEvent</c> — that machinery
/// arrives with the vault browser, which does.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SignInCommand : IExternalCommand
{
    /// <summary>
    /// The one sign-in window, if one is open. A second click focuses it rather than starting a
    /// second browser round-trip that would race the first for the same loopback callback.
    /// </summary>
    private static SignInWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        AuthSession session = MantlePlaceApplication.Session;

        if (_window is not null)
        {
            _window.Activate();
            return Result.Succeeded;
        }

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

        // Start the flow and hand it to a modeless window. What this command must NOT do is wait
        // for it: Execute runs on Revit's UI thread, so blocking here -- which is what
        // Task.Run(...).GetAwaiter().GetResult() did, whatever the thread the work ran on -- froze
        // the application until the browser came back or the five-minute timeout expired.
        _window = new SignInWindow(session, commandData.Application.MainWindowHandle);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        // Discarded deliberately: the outcome is reported on the window, and RunAsync lets no
        // exception escape. Awaiting it here would reintroduce exactly the freeze this removes.
        _ = _window.RunAsync();

        // Succeeded means "the command ran", not "the curator is signed in" -- that answer does not
        // exist yet, and a synchronous command cannot wait for it without freezing Revit.
        return Result.Succeeded;
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
