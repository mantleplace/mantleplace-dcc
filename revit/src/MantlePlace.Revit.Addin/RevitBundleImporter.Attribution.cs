using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The attribution step: the "Mantle Place Attribution" drafting view, and the provenance record on
// Project Information. Both are the manifest's words, copied, and every decision about them is
// AttributionView.Decide's; this file carries it out.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Writes the attribution view and the provenance record, in one transaction.
    /// </summary>
    /// <remarks>
    /// One transaction because each half is how the next import reads the other: the record says
    /// which note in the view is this order's. A view rewritten under a record that failed to store
    /// would leave the next import unable to find the note, and it would add a second one.
    /// </remarks>
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

    /// <summary>
    /// What the project's record says, and the record said aloud — which is the evidence that it
    /// survives a save and a reopen.
    /// </summary>
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
