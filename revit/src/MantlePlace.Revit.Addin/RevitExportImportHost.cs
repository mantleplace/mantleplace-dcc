using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The import window, as the connected host asks for it: an export's zip queued to the import
/// handler, opened on Revit's thread by the same <see cref="ExternalEvent"/> the vault window raises.
/// </summary>
/// <remarks>
/// ⛔ <b>Both calls arrive off Revit's thread</b>, from the connected host's lock, and neither waits
/// on Revit: the queue is a field set under the handler's lock, and the raise and the close are
/// posted to Revit's dispatcher (<see cref="MantlePlaceApplication.OnUiThread"/>), the way every
/// other event in this add-in that starts off Revit's thread reaches it. What happens to the window
/// comes back through <see cref="MantlePlaceApplication.Connected"/>, from the handler, on Revit's thread.
/// </remarks>
internal sealed class RevitExportImportHost : IExportImportHost
{
    private readonly BundleImportEventHandler _handler;
    private readonly ExternalEvent _event;

    internal RevitExportImportHost(BundleImportEventHandler handler, ExternalEvent externalEvent)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _event = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
    }

    public bool RequestOpen(HostExport export, string zipPath)
    {
        if (!_handler.QueueExport(export.Id, zipPath))
        {
            return false;
        }

        string exportId = export.Id;
        MantlePlaceApplication.OnUiThread(() =>
        {
            // Pending means a raise is already queued, and that raise opens this export too.
            ExternalEventRequest request = _event.Raise();
            if (request is ExternalEventRequest.Accepted or ExternalEventRequest.Pending)
            {
                return;
            }

            _handler.CloseExport(exportId);
            MantlePlaceApplication.Connected?.ImportRefused(
                exportId,
                ExportReasons.WindowFailed,
                $"Revit did not accept the request to open the import window ({request}).");
        });
        return true;
    }

    public void Close(string exportId) => MantlePlaceApplication.OnUiThread(() => _handler.CloseExport(exportId));
}
