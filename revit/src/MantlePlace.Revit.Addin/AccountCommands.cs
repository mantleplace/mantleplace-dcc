using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

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

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AccountIdentityCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => Result.Succeeded;
}

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenMantlePlaceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string site = MantlePlaceApplication.Endpoints.ApiBaseUrl;

        if (!ShellLauncher.TryOpen(site, out string refused))
        {
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Could not open your browser.",
                MainContent = $"Open this address manually: {refused}",
            }.Show();
        }

        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SendFeedbackCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!FeedbackLauncher.TryOpen(null, out string refused))
        {
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Could not open your browser.",
                MainContent = $"Open this address manually: {refused}",
            }.Show();
        }

        return Result.Succeeded;
    }
}

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
