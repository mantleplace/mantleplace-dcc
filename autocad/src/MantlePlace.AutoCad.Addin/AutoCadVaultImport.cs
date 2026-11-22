using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

internal sealed class AutoCadVaultImport : IVaultImport
{
    public event EventHandler<string>? Completed;

    public void Open(string bundleZipPath) => MantlePlaceApplication.OnUiThread(() => Import(bundleZipPath));

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
                out refusal);
        }
#pragma warning disable CA1031
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
