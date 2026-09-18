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
    /// How wide the note wraps, in paper feet: about 180 mm, the width of an A4 or Letter sheet's
    /// text block, so the view drops onto a sheet without the note running off it.
    /// </summary>
    private const double AttributionNoteWidthFeet = 0.59;

    /// <summary>The paper gap left under the lowest note when another is added beneath it, in feet.</summary>
    private const double AttributionNoteGapFeet = 0.02;

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

        Schema schema = ProvenanceSchema();
        RecordedAttribution? recorded = ReadRecordedAttribution(schema);

        ImportFailureSwallower swallower = new("Writing the attribution");
        using Transaction transaction = BeginTransaction("Mantle Place: attribution", swallower);
        (string viewLine, string recordedText) = WriteAttributionView(provenance, recorded);
        WriteProvenance(schema, provenance, recordedText);
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
        Say($"This project already records an import of order {OrNone(orderId)}, "
            + $"build {OrNone(entity.Get<string>(ProvenanceStorage.JobIdField))}.");

        return new RecordedAttribution(orderId, entity.Get<string>(ProvenanceStorage.NoteTextField) ?? string.Empty);
    }

    /// <summary>Carries out <see cref="AttributionView.Decide"/>.</summary>
    /// <returns>The line to say, and the text the record is to carry.</returns>
    private (string Line, string RecordedText) WriteAttributionView(
        ProjectProvenance provenance,
        RecordedAttribution? recorded)
    {
        const string name = AttributionView.ViewName;
        int count = provenance.Sources.Count;

        // Templates included: Revit refuses a name any view already has, template or not.
        View? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.Ordinal));

        AttributionViewFound found = existing switch
        {
            null => AttributionViewFound.None,
            ViewDrafting { IsTemplate: false } => AttributionViewFound.DraftingView,
            _ => AttributionViewFound.NotADraftingView,
        };

        List<TextNote> notes = found == AttributionViewFound.DraftingView
            ? [.. new FilteredElementCollector(_document, existing!.Id).OfClass(typeof(TextNote)).Cast<TextNote>()]
            : [];

        AttributionNotePlan plan = AttributionView.Decide(
            found,
            [.. notes.Select(note => note.Text)],
            recorded,
            provenance.OrderId,
            AttributionView.NoteText(provenance.Sources));

        switch (plan.Action)
        {
            case AttributionNoteAction.NothingToWrite:
                return (found == AttributionViewFound.None
                    ? "The manifest names no data sources, so no attribution view was made."
                    : $"The manifest names no data sources, so the \"{name}\" view was left as it was.",
                    plan.TextToRecord);

            case AttributionNoteAction.ViewNameTaken:
                return ($"A view named \"{name}\" already exists and is not a drafting view, so the attribution "
                    + "was not written. Rename that view and import again to get one.",
                    plan.TextToRecord);

            case AttributionNoteAction.Keep:
                return ($"The \"{name}\" view already lists this bundle's {count} data source(s).", plan.TextToRecord);

            case AttributionNoteAction.Rewrite:
                notes[plan.NoteIndex].Text = plan.TextToRecord;
                return ($"Rewrote the \"{name}\" view for this build: {count} data source(s).", plan.TextToRecord);

            case AttributionNoteAction.Add:
                ViewDrafting? view = plan.CreateView ? CreateAttributionView() : existing as ViewDrafting;
                if (view is null)
                {
                    // Nothing was written, so the record must not claim a note exists.
                    return ("This project has no drafting view type, so the attribution view was not made.",
                        string.Empty);
                }

                AddAttributionNote(view, notes, plan.TextToRecord);
                return (plan.CreateView
                    ? $"Made the drafting view \"{name}\", listing {count} data source(s). Place it on a sheet "
                        + "to credit the data."
                    : $"Added a note listing {count} data source(s) to the \"{name}\" view. No note already in "
                        + "it was this order's, so any note there — another order's, or one edited by hand — was "
                        + "left as it was.",
                    plan.TextToRecord);

            default:
                // An action added to the core and never carried out here would otherwise write nothing
                // while the record claimed a note; stop the step instead.
                throw new InvalidOperationException(
                    $"This build of the plugin does not know how to carry out the attribution action "
                    + $"'{plan.Action}'. Update the Mantle Place add-in.");
        }
    }

    private ViewDrafting? CreateAttributionView()
    {
        ViewFamilyType? drafting = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.Drafting);
        if (drafting is null)
        {
            return null;
        }

        ViewDrafting view = ViewDrafting.Create(_document, drafting.Id);
        view.Name = AttributionView.ViewName;
        return view;
    }

    /// <summary>
    /// A new note, under whatever notes the view already holds so none is written on top of another.
    /// </summary>
    private void AddAttributionNote(ViewDrafting view, IReadOnlyList<TextNote> existingNotes, string text)
    {
        ElementId typeId = _document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        double width = Math.Clamp(
            AttributionNoteWidthFeet,
            TextElement.GetMinimumAllowedWidth(_document, typeId),
            TextElement.GetMaximumAllowedWidth(_document, typeId));

        double? lowest = existingNotes
            .Select(note => note.get_BoundingBox(view)?.Min.Y)
            .Where(y => y.HasValue)
            .Min();
        XYZ position = lowest is { } y0 ? new XYZ(0, y0 - (AttributionNoteGapFeet * view.Scale), 0) : XYZ.Zero;

        TextNote.Create(_document, view.Id, position, width, text, typeId);
    }

    private void WriteProvenance(Schema schema, ProjectProvenance provenance, string noteText)
    {
        Entity entity = new(schema);
        entity.Set(ProvenanceStorage.OrderIdField, provenance.OrderId);
        entity.Set(ProvenanceStorage.JobIdField, provenance.JobId);
        entity.Set(ProvenanceStorage.ManifestVersionField, provenance.ManifestVersion);
        entity.Set(ProvenanceStorage.SourcesField, ProvenanceStorage.EncodeSources(provenance.Sources));
        entity.Set(ProvenanceStorage.NoteTextField, noteText);
        _document.ProjectInformation.SetEntity(entity);
    }

    /// <summary>
    /// The provenance schema, from this session if a document already brought it in, and built from
    /// the core's constants otherwise.
    /// </summary>
    /// <remarks>
    /// Read access is public, so any tool can ask which bundle a project came from; write access is
    /// this vendor's, so only this add-in can say. Why the GUID is permanent is ADR 0011.
    /// </remarks>
    private static Schema ProvenanceSchema()
    {
        if (Schema.Lookup(ProvenanceStorage.SchemaGuid) is { } existing)
        {
            return existing;
        }

        SchemaBuilder builder = new(ProvenanceStorage.SchemaGuid);
        builder.SetSchemaName(ProvenanceStorage.SchemaName);
        builder.SetVendorId(ProvenanceStorage.VendorId);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Vendor);
        builder.SetDocumentation("Which Mantle Place bundle this project was imported from, and its data sources.");
        builder.AddSimpleField(ProvenanceStorage.OrderIdField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.JobIdField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.ManifestVersionField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.SourcesField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.NoteTextField, typeof(string));
        return builder.Finish();
    }

    private static string OrNone(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
}
