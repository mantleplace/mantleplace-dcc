using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class VaultBrowserCommand : IExternalCommand
{
    private static VaultBrowserWindow? _window;

    internal static bool IsOpen => _window is not null;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Open(commandData.Application.MainWindowHandle, selectOrderId: null);
        return Result.Succeeded;
    }

    internal static void Open(IntPtr revitWindow, string? selectOrderId)
    {
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
            MantlePlaceApplication.Announced,
            FeedbackLauncher.TryOpen,
            new VaultImporting(
                MantlePlaceApplication.Watcher,
                new RevitVaultImport(MantlePlaceApplication.ImportEvent, MantlePlaceApplication.ImportHandler)),
            revitWindow,
            selectOrderId);

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
