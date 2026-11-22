using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SignInCommand : IExternalCommand
{
    private static SignInWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => StartSignIn(commandData);

    internal static Result StartSignIn(ExternalCommandData commandData)
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

        _window = new SignInWindow(session, commandData.Application.MainWindowHandle);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        _ = _window.RunAsync();

        return Result.Succeeded;
    }
}

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
