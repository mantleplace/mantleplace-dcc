using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// AutoCAD's end of the connected host: where a fetched export goes when the web vault sends one
/// (<see cref="IExportImportHost"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Both calls arrive off AutoCAD's thread</b>, from the connected host's lock, and neither waits
/// on AutoCAD: the one slot is a field set under a lock, and the window is posted to the UI thread
/// (<see cref="MantlePlaceApplication.OnUiThread"/>). What happens to the window comes back through
/// <see cref="MantlePlaceApplication.Connected"/>.
/// </para>
/// <para>
/// Revit opens an import window here. This opens <see cref="ReceivedExportWindow"/>, which says the
/// bundle arrived and was checked, and that importing is not in the plugin yet. Closing it ends the
/// export as the web's <c>cancelled</c>, the same ending as a Revit user closing the import window
/// before pressing Import.
/// </para>
/// </remarks>
internal sealed class AutoCadExportImportHost : IExportImportHost
{
    private readonly object _gate = new();
    private string? _holding;
    private ReceivedExportWindow? _window;

    public bool RequestOpen(HostExport export, string zipPath)
    {
        lock (_gate)
        {
            if (_holding is not null)
            {
                return false;
            }

            _holding = export.Id;
        }

        MantlePlaceApplication.OnUiThread(() => Open(export, zipPath));
        return true;
    }

    public void Close(string exportId) => MantlePlaceApplication.OnUiThread(() =>
    {
        if (_window is { } window && string.Equals(window.ExportId, exportId, StringComparison.Ordinal))
        {
            // The web cancelled it: the window goes, and what the closing reports is a no-op to the
            // desk, which has already moved the export on.
            window.Close();
        }
    });

    /// <summary>On AutoCAD's UI thread.</summary>
    private void Open(HostExport export, string zipPath)
    {
        ConnectedHostService? connected = MantlePlaceApplication.Connected;
        try
        {
            ReceivedExportWindow window = new(export, zipPath, MantlePlaceApplication.MainWindowHandle());
            window.Closed += (_, _) =>
            {
                _window = null;
                lock (_gate)
                {
                    _holding = null;
                }

                // Nothing was imported, so this is the window going before an import started.
                connected?.ImportEnded(export.Id, ImportEnding.DismissedBeforeStart, string.Empty);
                connected?.TurnFree();
            };

            _window = window;
            window.Show();
            MantlePlaceApplication.Say(ExportReceipt.Describe(export, zipPath));
            connected?.ImportOpened(export.Id);
        }
#pragma warning disable CA1031 // A window that will not open must be reported to the web, not left to hang the export.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _window = null;
            lock (_gate)
            {
                _holding = null;
            }

            connected?.ImportRefused(export.Id, ExportReasons.WindowFailed, $"AutoCAD could not open the window for this export ({ex.Message}).");
            connected?.TurnFree();
        }
    }
}
