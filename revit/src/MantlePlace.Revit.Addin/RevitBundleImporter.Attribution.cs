using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void WriteAttributionAndProvenance(ImportStep step)
    {
        if (step.Provenance is not { } provenance)
        {
            return;
        }

        Schema schema = AttributionNotes.ProvenanceSchema();
        RecordedAttribution? recorded = ReadRecordedAttribution(schema);

        ImportFailureSwallower swallower = new("Writing the attribution");
        using Transaction transaction = BeginTransaction("Mantle Place: attribution", swallower);
        (string viewLine, string recordedText) = AttributionNotes.WriteAttributionView(_document, provenance, recorded);
        AttributionNotes.WriteProvenance(_document, schema, provenance, recordedText);
        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(viewLine);
        Say($"Recorded on Project Information that this project was imported from order {OrNone(provenance.OrderId)}, "
            + $"build {OrNone(provenance.JobId)} (manifest {OrNone(provenance.ManifestVersion)}).");
    }

    private RecordedAttribution? ReadRecordedAttribution(Schema schema)
    {
        Entity entity = _document.ProjectInformation.GetEntity(schema);
        if (!entity.IsValid())
        {
            return null;
        }

        string orderId = entity.Get<string>(ProvenanceStorage.OrderIdField) ?? string.Empty;
        string jobId = entity.Get<string>(ProvenanceStorage.JobIdField) ?? string.Empty;
        Say($"This project already records an import of order {OrNone(orderId)}, build {OrNone(jobId)}.");

        return new RecordedAttribution(orderId, entity.Get<string>(ProvenanceStorage.NoteTextField) ?? string.Empty)
        {
            JobId = jobId,
        };
    }

    private static string OrNone(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
}
