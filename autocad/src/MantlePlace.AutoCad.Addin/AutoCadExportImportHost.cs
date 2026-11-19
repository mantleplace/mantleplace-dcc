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
/// AutoCAD offers no per-layer choice, so there is no checklist to wait on. A send with nothing to choose
/// (every 2.x order, which has no level control, <see cref="LevelControl.For"/>) begins as the window opens,
/// and the web hears it open and begin. Cancel stops it between steps, which the web hears as cancelled.
/// While another import runs, from disk or from an earlier send, the export waits its turn.
/// </para>
/// <para>
/// A send names no level, so a 3.0 order arrives at the recommendation. When its order offers levels the
/// window opens on a stage before Import (<see cref="WindowedImportStage"/>) with the level control on it,
/// as Revit's window does, and the web hears it open, then begin when Import is pressed; a close before
/// Import is heard as dismissed before it started. Another level is downloaded into the slot and the window
/// reopened on it, or on the level it held when the download failed, before anything is drawn.
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
    /// The web asked for this export's window to close. A running import is left alone, as Revit leaves one;
    /// a window still before Import is left for the curator to close.
    /// </summary>
    public void Close(string exportId)
    {
    }

    /// <summary>On AutoCAD's UI thread.</summary>
    /// <param name="export">The export the web sent.</param>
    /// <param name="zipPath">The zip the slot holds.</param>
    /// <param name="held">The level a reopened window holds after a level choice; <c>null</c> for a first open.</param>
    /// <param name="levelNote">What the level choice did, under the control; <c>null</c> for a first open.</param>
    private void Open(HostExport export, string zipPath, FidelityLevel? held = null, string? levelNote = null)
    {
        ConnectedHostService? connected = MantlePlaceApplication.Connected;

        // A send names no level: a 3.0 order was fetched at the recommendation, and its window opens with
        // the control on it; reopened after a level choice, on the level now in the slot. A 2.x order has none.
        VaultExportSource? source = MantlePlaceApplication.ExportSource;
        VaultBundle? listed = source?.Listed(export.OrderId);
        // The window waits for Import only when it offers that choice: with no level control (every 2.x
        // send) there is no stage, and the import begins as the window opens, as it always has.
        LevelControl? levels = held is { } level ? LevelControl.At(listed, level) : LevelControl.For(listed);
        WindowedImportStage? stage = levels is null
            ? null
            : new(
                levels,
                levelNote,
                source is null
                    ? null
                    : (chosen, token) => source.FetchLevelAsync(export.OrderId, chosen, new Progress<string>(_ => { }), token),
                (zip, now, note) => Open(export, zip, now, note),
                () => connected?.ImportBegan(export.Id));

        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            Refuse(connected, export.Id, held is not null, "AutoCAD has no drawing open to import into. Nothing was imported.");
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
                    connected?.ImportEnded(
                        export.Id,
                        end.Ending,
                        end.Ending is ImportEnding.Imported or ImportEnding.DismissedBeforeStart ? string.Empty : end.Summary);
                },
                out refusal,
                stage);
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
            Refuse(connected, export.Id, held is not null, refusal ?? "AutoCAD could not open the window for this export.");
            return;
        }

        MantlePlaceApplication.Say(held is { } at
            ? $"Reopened for export {export.Id} at the {DownloadLevels.Word(at)} level."
            : ExportReceipt.Describe(export, zipPath));

        // After a reopen this is nothing to the desk: the export is already awaiting options.
        connected?.ImportOpened(export.Id);

        // On the stage before Import, the web hears the import begin when Import is pressed (the stage's
        // Began); with no stage the import has begun already, and a refusal shows its report at once,
        // and is begun and ended as before.
        if (!WindowedImport.AwaitsImport)
        {
            connected?.ImportBegan(export.Id);
        }
    }

    /// <summary>
    /// No window opened on the export. A first open is refused; a reopen after a level choice already had
    /// its window open, so the web hears the import fail instead.
    /// </summary>
    private void Refuse(ConnectedHostService? connected, string exportId, bool reopened, string message)
    {
        Release();
        if (reopened)
        {
            connected?.ImportEnded(exportId, ImportEnding.Failed, message);
        }
        else
        {
            connected?.ImportRefused(exportId, ExportReasons.WindowFailed, message);
        }

        connected?.TurnFree();
    }

    private void Release()
    {
        lock (_gate)
        {
            _holding = null;
        }
    }
}
