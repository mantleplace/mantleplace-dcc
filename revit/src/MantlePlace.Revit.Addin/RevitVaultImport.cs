using Autodesk.Revit.UI;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

internal sealed class RevitVaultImport(ExternalEvent importEvent, BundleImportEventHandler importHandler) : IVaultImport
{
    public event EventHandler<string>? Completed
    {
        add => importHandler.Completed += value;
        remove => importHandler.Completed -= value;
    }

    public void Open(string bundleZipPath)
    {
        importHandler.QueueImport(bundleZipPath);
        _ = importEvent.Raise();
    }
}
