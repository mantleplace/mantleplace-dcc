using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// AutoCAD's end of the connected host: where a fetched export goes when the web vault sends one
/// (<see cref="IExportImportHost"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Both calls arrive off AutoCAD's thread</b>, from the connected host's lock, and neither waits
/// on AutoCAD: the one slot is a field set under a lock, and the import is posted to the UI thread
/// (<see cref="MantlePlaceApplication.OnUiThread"/>). What happens to it comes back through
/// <see cref="MantlePlaceApplication.Connected"/>.
/// </para>
/// <para>
/// A send opens the shared import window, as it does in Revit (<see cref="WindowedImport"/>, <c>HPS-60</c>).
/// AutoCAD offers no per-layer choice, so there is no checklist to wait on: the import begins as the window
/// opens, the web hears it open and begin, and Cancel stops it between steps, which the web hears as
/// cancelled. While another import runs, from disk or from an earlier send, the export waits its turn.
/// </para>
/// <para>
/// A send names no level, so a 3.0 order arrives at the recommendation. The window gets no level control:
/// the import begins as it opens, so there is no moment before Import to choose another level, and this
/// plugin reads no 3.0 bundle yet (<see cref="ManifestVersions.NewestSupportedMajor"/>), so the window
/// reports a 3.0 send as refused whatever its level.
/// </para>
/// </remarks>
internal sealed class AutoCadExportImportHost : IExportImportHost
{
    private readonly object _gate = new();
    private string? _holding;

    public bool RequestOpen(HostExport export, string zipPath)
    {
        lock (_gate)
        {
            if (_holding is not null || WindowedImport.IsRunning)
            {
                return false;
            }

            _holding = export.Id;
        }

        MantlePlaceApplication.OnUiThread(() => Open(export, zipPath));
        return true;
    }

    /// <summary>
    /// The web asked for this export's window to close. There is no checklist to close it from: the import
    /// began as the window opened, and a running import is left alone, as Revit leaves one.
    /// </summary>
    public void Close(string exportId)
    {
    }

    /// <summary>On AutoCAD's UI thread.</summary>
    private void Open(HostExport export, string zipPath)
    {
        ConnectedHostService? connected = MantlePlaceApplication.Connected;

        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            Release();
            connected?.ImportRefused(export.Id, ExportReasons.WindowFailed, "AutoCAD has no drawing open to import into. Nothing was imported.");
            connected?.TurnFree();
            return;
        }

        string? refusal;
        bool started;
        try
        {
            started = WindowedImport.Start(
                document,
                zipPath,
                end =>
                {
                    Release();
                    connected?.ImportEnded(export.Id, end.Ending, end.Ending == ImportEnding.Imported ? string.Empty : end.Summary);
                },
                out refusal);
        }
#pragma warning disable CA1031 // A window that will not open must be reported to the web, not left to hang the export.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            started = false;
            refusal = $"AutoCAD could not open the window for this export ({ex.Message}).";
        }

        if (!started)
        {
            Release();
            connected?.ImportRefused(export.Id, ExportReasons.WindowFailed, refusal ?? "AutoCAD could not open the window for this export.");
            connected?.TurnFree();
            return;
        }

        MantlePlaceApplication.Say(ExportReceipt.Describe(export, zipPath));
        connected?.ImportOpened(export.Id);
        connected?.ImportBegan(export.Id);
    }

    private void Release()
    {
        lock (_gate)
        {
            _holding = null;
        }
    }
}
