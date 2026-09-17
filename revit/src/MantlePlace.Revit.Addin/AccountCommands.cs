using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>The Account split button's face: sign in when signed out, About when signed in.</summary>
/// <remarks>
/// <para>
/// One command rather than two because a <c>PushButton</c> binds to one class for the life of the
/// session and the face has to do different things in different states. Which one is not decided
/// here — <see cref="AccountRibbon.For"/> decides it, in the pure core where it is tested, and this
/// command reads the answer.
/// </para>
/// <para>
/// The disabled states need no branch of their own: the face is disabled while a sign-in or a
/// refresh is in flight, so this never runs then. It still asks rather than assuming, because
/// <c>RibbonItem.Enabled</c> is advisory in exactly one case that matters — the ribbon updating a
/// beat behind the session.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AccountCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        return MantlePlaceApplication.Session.AccountFace().FaceAction == AccountFaceAction.About
            ? AboutMantlePlace.Show(commandData.Application)
            : SignInCommand.StartSignIn(commandData);
    }
}

/// <summary>The dropdown's address row. Disabled, so this never runs.</summary>
/// <remarks>
/// A <c>PushButton</c> is the only ribbon item that can hold a line of text inside a split button's
/// dropdown, and every push button needs a command class whether or not it can be clicked. This is
/// that class. It reports success rather than throwing, because a row that somehow became clickable
/// should do nothing rather than raise a fault dialog about the curator's own email address.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AccountIdentityCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => Result.Succeeded;
}

/// <summary>"Open mantle.place": the website, in the curator's default browser.</summary>
/// <remarks>
/// The address is the deployment this plugin already talks to rather than a second key beside it: a
/// dev stack that points <c>apiBaseUrl</c> at itself wants this link to follow, and a config file
/// that can send the two to different places is a config file that will.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenMantlePlaceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string site = MantlePlaceApplication.Endpoints.ApiBaseUrl;

        if (!ShellLauncher.TryOpen(site, out string refused))
        {
            // The same failure the sign-in has, and the same remedy: name the address so it can be
            // typed. A machine with no default browser is rare and entirely the curator's to fix.
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Could not open your browser.",
                MainContent = $"Open this address manually: {refused}",
            }.Show();
        }

        return Result.Succeeded;
    }
}

/// <summary>"About Mantle Place": which build this is, and which Revit it is running in.</summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AboutMantlePlaceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        return AboutMantlePlace.Show(commandData.Application);
    }
}
