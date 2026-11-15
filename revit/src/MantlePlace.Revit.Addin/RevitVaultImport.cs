using Autodesk.Revit.UI;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Revit's import from the shared vault window: queue the bundle zip on the import handler and raise
/// the external event that runs it on Revit's thread, inside an API context.
/// </summary>
/// <remarks>
/// The vault window is the shared one (<see cref="VaultBrowserWindow"/>), which knows no host's API;
/// this is the whole of what Revit adds to it. The window stays responsive and hears back through
/// <see cref="Completed"/>.
/// </remarks>
internal sealed class RevitVaultImport(ExternalEvent importEvent, BundleImportEventHandler importHandler) : IVaultImport
{
    /// <inheritdoc/>
    public event EventHandler<string>? Completed
    {
        add => importHandler.Completed += value;
        remove => importHandler.Completed -= value;
    }

    /// <inheritdoc/>
    public void Open(string bundleZipPath)
    {
        importHandler.QueueImport(bundleZipPath);
        _ = importEvent.Raise();
    }
}
