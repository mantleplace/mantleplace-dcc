using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MantlePlace.Revit.Addin;

/// <summary>"Open vault": shows the modeless bundle browser.</summary>
/// <remarks>
/// One window per Revit session. A second click focuses the one that is open rather than starting a
/// second browser with its own cancellation token and its own idea of what is downloading.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class VaultBrowserCommand : IExternalCommand
{
    private static VaultBrowserWindow? _window;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        if (_window is not null)
        {
            _window.Activate();
            return Result.Succeeded;
        }

        _window = new VaultBrowserWindow(
            MantlePlaceApplication.Session,
            MantlePlaceApplication.Vault,
            MantlePlaceApplication.Cache,
            MantlePlaceApplication.ImportEvent,
            MantlePlaceApplication.ImportHandler,
            commandData.Application.MainWindowHandle);

        _window.Closed += (_, _) => _window = null;
        _window.Show();

        return Result.Succeeded;
    }
}
