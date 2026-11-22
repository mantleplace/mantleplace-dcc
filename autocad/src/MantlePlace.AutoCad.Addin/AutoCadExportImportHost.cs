using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

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

    public void Close(string exportId)
    {
    }

    private void Open(HostExport export, string zipPath, FidelityLevel? held = null, string? levelNote = null)
    {
        ConnectedHostService? connected = MantlePlaceApplication.Connected;

        VaultExportSource? source = MantlePlaceApplication.ExportSource;
        VaultBundle? listed = source?.Listed(export.OrderId);
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
#pragma warning disable CA1031
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

        connected?.ImportOpened(export.Id);

        if (!WindowedImport.AwaitsImport)
        {
            connected?.ImportBegan(export.Id);
        }
    }

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
