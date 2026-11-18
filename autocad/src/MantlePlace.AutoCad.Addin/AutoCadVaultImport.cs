using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// AutoCAD's import from the shared vault window: the verified bundle zip goes to the one windowed
/// import, on AutoCAD's thread, into the active drawing.
/// </summary>
/// <remarks>
/// The vault window is the shared one (<see cref="VaultBrowserWindow"/>), which knows no host's API; this
/// is the whole of what AutoCAD adds to it, as <c>RevitVaultImport</c> is Revit's. The vault window tells
/// the curator to press Import in the import window, so it opens on the stage before Import
/// (<see cref="WindowedImportStage"/>), saying what the import will do. The level was chosen in the vault
/// before the download, so the stage has no level control. The vault window hears the closing report, or
/// why nothing started, through <see cref="Completed"/>.
/// </remarks>
internal sealed class AutoCadVaultImport : IVaultImport
{
    /// <inheritdoc/>
    public event EventHandler<string>? Completed;

    /// <inheritdoc/>
    public void Open(string bundleZipPath) => MantlePlaceApplication.OnUiThread(() => Import(bundleZipPath));

    /// <summary>On AutoCAD's UI thread.</summary>
    private void Import(string zipPath)
    {
        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            Completed?.Invoke(this, "AutoCAD has no drawing open to import into. Open one, then import again. Nothing was imported.");
            return;
        }

        string? refusal;
        bool started;
        try
        {
            started = WindowedImport.Start(
                document,
                zipPath,
                end => Completed?.Invoke(this, end.Summary),
                out refusal,
                new WindowedImportStage(Levels: null, LevelNote: null, Fetch: null, Reopen: null, Began: null));
        }
#pragma warning disable CA1031 // A window that will not open is told to the vault window, never left to take AutoCAD down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            started = false;
            refusal = $"AutoCAD could not open the import window ({ex.Message}). Nothing was imported.";
        }

        if (!started)
        {
            Completed?.Invoke(this, refusal ?? "AutoCAD could not open the import window. Nothing was imported.");
        }
    }
}
