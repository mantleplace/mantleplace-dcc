using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

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
        if (!_handler.QueueExport(export.Id, export.OrderId, zipPath))
        {
            return false;
        }

        string exportId = export.Id;
        MantlePlaceApplication.OnUiThread(() =>
        {
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
