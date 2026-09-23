using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MantlePlace.Revit.Addin;

/// <summary>"Vault": shows the modeless vault browser.</summary>
/// <remarks>
/// One window per Revit session. A second click focuses the one that is open rather than starting a
/// second browser with its own idea of what is downloading.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class VaultBrowserCommand : IExternalCommand
{
    private static VaultBrowserWindow? _window;

    /// <summary>
    /// Whether the vault browser is open — which silences every notice, and pauses the background
    /// listing while the window owns the listing (<c>HPS-55</c>).
    /// </summary>
    internal static bool IsOpen => _window is not null;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Open(commandData.Application.MainWindowHandle, selectOrderId: null);
        return Result.Succeeded;
    }

    /// <summary>
    /// Opens the vault browser, or brings the open one forward, with <paramref name="selectOrderId"/>
    /// selected when it names a row. Revit's UI thread only.
    /// </summary>
    /// <remarks>
    /// Reachable without a command because a Prepare notice opens the vault too, and a notice is
    /// clicked outside any Revit API context. Nothing here needs one: the window never calls the
    /// Revit API itself, and hands its import to an <c>ExternalEvent</c>.
    /// </remarks>
    internal static void Open(IntPtr revitWindow, string? selectOrderId)
    {
        // Opening the vault is seeing what the notices said, whichever way it was opened.
        PrepareNotifier.Seen();

        if (_window is not null)
        {
            if (selectOrderId is not null)
            {
                _window.Select(selectOrderId);
            }

            _window.Activate();
            return;
        }

        _window = new VaultBrowserWindow(
            MantlePlaceApplication.Session,
            MantlePlaceApplication.Vault,
            MantlePlaceApplication.Cache,
            MantlePlaceApplication.Watcher,
            MantlePlaceApplication.ImportEvent,
            MantlePlaceApplication.ImportHandler,
            revitWindow,
            selectOrderId);

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
